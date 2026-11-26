using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Colony.Core.Commands;
using Colony.Core.Procedural;

namespace Colony.Core.Net;

/// <summary>How a networked world is made: every peer generates it from these, so they start identical.</summary>
public sealed record GameSettings(int Seed, int Width, int Depth, int Height, int Colonists, int Colonies)
{
    public ColonySim Create(bool settleWaterInBackground = false) =>
        ColonySim.Generate(new WorldGenSettings(Seed, Width, Depth, Height), Colonists, settleWaterInBackground, Colonies);

    public void Write(BinaryWriter w)
    {
        w.Write(Seed); w.Write(Width); w.Write(Depth); w.Write(Height); w.Write(Colonists); w.Write(Colonies);
    }

    public static GameSettings Read(BinaryReader r) =>
        new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
}

internal enum Msg : byte
{
    Hello = 1,   // client -> host: protocol version, player name
    Welcome,     // host -> client: your colony, the settings, the tick, a snapshot of the game
    Command,     // client -> host: a command to apply (the host stamps the client's colony on it)
    Turn,        // host -> client: commands applied (with their ticks) and the tick to run up to
    Hash,        // host -> client: the state hash at a tick (desync check)
    Bye,         // either way: a reason, then the connection closes
    Players,     // host -> client: who is in the game
    Resync,      // client -> host: my world no longer matches yours; send it again
    State,       // host -> client: the tick and a snapshot of the game (a resync), commands after it follow in turns
}

/// <summary>
/// One TCP connection carrying length-prefixed messages (type byte + payload). A background thread reads into
/// <see cref="Inbox"/>; the owner drains it on its own (main) thread, so the sim is only ever touched there.
/// </summary>
internal sealed class Peer : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly object _writeLock = new();
    public readonly ConcurrentQueue<(Msg Type, byte[] Data)> Inbox = new();
    public volatile bool Closed;
    public string? CloseReason;

    public Peer(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "Colony net reader" };
        reader.Start();
    }

    private void ReadLoop()
    {
        try
        {
            var head = new byte[5];
            while (!Closed)
            {
                _stream.ReadExactly(head);
                int length = BitConverter.ToInt32(head, 1);
                if (length < 0 || length > 256 << 20) throw new InvalidDataException("bad frame length");
                var data = new byte[length];
                _stream.ReadExactly(data);
                Inbox.Enqueue(((Msg)head[0], data));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException or EndOfStreamException or SocketException)
        {
            CloseReason ??= "connection lost";
            Closed = true;
        }
    }

    public void Send(Msg type, byte[] data)
    {
        if (Closed) return;
        try
        {
            lock (_writeLock)
            {
                Span<byte> head = stackalloc byte[5];
                head[0] = (byte)type;
                BitConverter.TryWriteBytes(head[1..], data.Length);
                _stream.Write(head);
                _stream.Write(data);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            CloseReason ??= "connection lost";
            Closed = true;
        }
    }

    public static byte[] Pack(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        write(w);
        w.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        Closed = true;
        _tcp.Dispose();
    }
}

/// <summary>A player in a networked game.</summary>
public sealed record NetPlayer(int Faction, string Name)
{
    /// <summary>
    /// What the host keeps of a player's key (a random secret each install makes once): a short SHA-256 digest, so the
    /// key itself never travels on to other players or into saves. Empty for no key.
    /// </summary>
    public static string KeyHash(string key) => key.Length == 0 ? ""
        : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..24];
}

/// <summary>
/// The host of a lockstep game: its sim is the reference. Clients connect, each gets its colony (its own again when it
/// comes back, by its key: see <see cref="Seat"/>; else a free one) and a copy of the
/// game as it stands (a snapshot); their commands are applied on the host before its next tick, stamped with
/// their colony. After each frame <see cref="Broadcast"/> streams the newly logged commands and the host's tick, so
/// every client applies the same commands before the same ticks, and a state hash now and then catches desyncs.
/// The host never waits for clients: they trail it by their latency and catch up.
/// </summary>
public sealed class LockstepHost : IDisposable
{
    public const int Protocol = 18;
    /// <summary>Ticks between desync checks (5 s at 1x).</summary>
    public const int HashInterval = 100;

    private readonly ColonySim _sim;
    private readonly GameSettings _settings;
    private readonly TcpListener _listener;
    private readonly List<Client> _clients = new();
    private long _lastHashTick = -1;

    private sealed class Client(Peer peer)
    {
        public readonly Peer Peer = peer;
        public int Faction = -1;
        public string Name = "";
        public int SentLog;      // how much of the command log it has been sent
        public long SentTick = -1;
    }

    public string HostName { get; }
    public int Port { get; }
    public event Action<string>? Message;

    public LockstepHost(ColonySim sim, GameSettings settings, int port, string hostName = "Host", string hostKey = "")
    {
        _sim = sim;
        _settings = settings;
        HostName = hostName;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        // The host plays the first colony; recorded like anyone's, so its saves know whose it is.
        string hash = NetPlayer.KeyHash(hostKey);
        if (sim.Factions[0].Player != hostName || sim.Factions[0].PlayerKey != hash) sim.Commands.Execute(new ClaimColony(0, hostName, hash));
    }

    public IReadOnlyList<NetPlayer> Players =>
        new[] { new NetPlayer(0, HostName) }.Concat(_clients.Where(c => c.Faction > 0).Select(c => new NetPlayer(c.Faction, c.Name))).ToList();

    /// <summary>Accepts newcomers and applies what clients sent (queued for the host's next tick). Main thread, every frame.</summary>
    public void Poll()
    {
        while (_listener.Pending()) _clients.Add(new Client(new Peer(_listener.AcceptTcpClient())));
        foreach (var c in _clients.ToList())
        {
            while (c.Peer.Inbox.TryDequeue(out var m)) Handle(c, m.Type, m.Data);
            if (c.Peer.Closed)
            {
                _clients.Remove(c);
                c.Peer.Dispose();
                if (c.Faction > 0)
                {
                    Message?.Invoke($"{c.Name} left ({c.Peer.CloseReason})");
                    SendPlayers();
                }
            }
        }
    }

    private void Handle(Client c, Msg type, byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        switch (type)
        {
            case Msg.Hello:
                int version = r.ReadInt32();
                c.Name = r.ReadString();
                if (version != Protocol)
                {
                    Refuse(c, $"version mismatch (host {Protocol}, you {version})");
                    return;
                }
                string key = r.BaseStream.Position < r.BaseStream.Length ? r.ReadString() : "";
                string hash = NetPlayer.KeyHash(key);
                if (_sim.Factions.Where((_, i) => IsOnline(i)).Any(o => hash.Length > 0 && o.PlayerKey == hash)) hash = ""; // a second copy on one computer: a player of its own
                var (free, back, why) = Seat(c.Name, hash);
                if (free <= 0)
                {
                    Refuse(c, why);
                    return;
                }
                // Recorded in the game (and its saves) through a command, so every peer agrees on whose colony it is.
                var colony = _sim.Factions[free];
                if (colony.Player != c.Name || colony.PlayerKey != hash) _sim.Commands.Execute(new ClaimColony(free, c.Name, hash));
                // A snapshot of the game as it stands (settling it is a command: guests already here settle before this tick too).
                var state = Snapshot.Take(_sim, _settings);
                c.Faction = free;
                c.SentLog = _sim.Commands.Log.Count;
                c.SentTick = _sim.Tick;
                c.Peer.Send(Msg.Welcome, Peer.Pack(w =>
                {
                    w.Write(free);
                    _settings.Write(w);
                    w.Write(_sim.Tick);
                    var log = state;
                    w.Write(log.Length);
                    w.Write(log);
                }));
                Message?.Invoke(back ? $"{c.Name} is back, playing {colony.Name}" : $"{c.Name} joined as {colony.Name}");
                SendPlayers();
                break;
            case Msg.Resync when c.Faction > 0:
                // A desynced guest gets the game again as it stands, like a joiner; turns go on from here.
                var fresh = Snapshot.Take(_sim, _settings);
                c.SentLog = _sim.Commands.Log.Count;
                c.SentTick = _sim.Tick;
                c.Peer.Send(Msg.State, Peer.Pack(w => { w.Write(_sim.Tick); w.Write(fresh.Length); w.Write(fresh); }));
                Message?.Invoke($"{c.Name}'s world fell out of step: sent them the game again");
                break;
            case Msg.Command when c.Faction > 0:
                var command = CommandCodec.Read(r) with { Player = c.Faction }; // a client acts for its own colony only
                _sim.Commands.Submit(command);
                break;
            case Msg.Bye:
                c.Peer.CloseReason = r.ReadString();
                c.Peer.Closed = true;
                break;
        }
    }

    /// <summary>
    /// The colony a player joining with this name and key hash takes: their own if they played one before (the key
    /// decides; a colony recorded with no key, as in a save from before keys, goes by name), else a free one nobody has
    /// played; colonies held for absent players wait for them (the host can free one: <see cref="Free"/>). 0 with the
    /// reason when there is none.
    /// </summary>
    private (int Colony, bool Back, string Why) Seat(string name, string hash)
    {
        var online = _clients.Where(o => o.Faction > 0).Select(o => o.Faction).Append(0).ToHashSet();
        var colonies = _sim.Factions;
        for (int f = 0; f < colonies.Count; f++)
            if (hash.Length > 0 && colonies[f].PlayerKey == hash && !online.Contains(f)) return (f, true, "");
        // (The same key as a connected player: a second copy of the game on one computer. It joins as a player of its own.)
        for (int f = 1; f < colonies.Count; f++)
            if (!online.Contains(f) && colonies[f].PlayerKey.Length == 0 && string.Equals(colonies[f].Player, name, StringComparison.OrdinalIgnoreCase))
                return (f, true, "");
        for (int f = 1; f < colonies.Count; f++)
            if (!online.Contains(f) && !colonies[f].Claimed) return (f, false, "");
        var away = Enumerable.Range(1, colonies.Count - 1).Where(f => !online.Contains(f)).Select(f => colonies[f].Player).ToList();
        return (0, false, away.Count > 0
            ? $"every colony is taken: the others are held for {string.Join(", ", away)} until they come back (or the host frees one)"
            : $"the game is full ({colonies.Count} colonies)");
    }

    /// <summary>Whether a colony's player is connected (the host always is).</summary>
    public bool IsOnline(int colony) => colony == 0 || _clients.Any(c => c.Faction == colony);

    /// <summary>
    /// The host frees a colony held for an absent player: the next newcomer may take it (false while its player is
    /// connected, or for the host's own).
    /// </summary>
    public bool Free(int colony)
    {
        if (colony <= 0 || colony >= _sim.Factions.Count || IsOnline(colony)) return false;
        _sim.Commands.Execute(new ClaimColony(colony, "", ""));
        SendPlayers();
        return true;
    }

    private void Refuse(Client c, string reason)
    {
        c.Peer.Send(Msg.Bye, Peer.Pack(w => w.Write(reason)));
        c.Peer.Closed = true;
    }

    private void SendPlayers()
    {
        var players = Players;
        var data = Peer.Pack(w =>
        {
            w.Write(players.Count);
            foreach (var p in players) { w.Write(p.Faction); w.Write(p.Name); }
        });
        foreach (var c in _clients.Where(c => c.Faction > 0)) c.Peer.Send(Msg.Players, data);
    }

    /// <summary>
    /// Sends each client the commands logged since its last turn and the host's tick (call after the frame's ticks;
    /// the host also flushes queued commands here so they apply even while paused). Every <see cref="HashInterval"/>
    /// ticks, a state hash.
    /// </summary>
    public void Broadcast()
    {
        _sim.Commands.Flush();
        var log = _sim.Commands.Log;
        // Hashes go before the turn that reaches their tick: a client must hold a tick's hash before it can step past it,
        // or the check is skipped (a race a fast loopback client could lose).
        foreach (var (tick, hash) in _hashQueue)
        {
            var data = Peer.Pack(w => { w.Write(tick); w.Write(hash); });
            foreach (var c in _clients.Where(c => c.Faction > 0)) c.Peer.Send(Msg.Hash, data);
        }
        _hashQueue.Clear();
        foreach (var c in _clients)
        {
            if (c.Faction <= 0 || (c.SentLog == log.Count && c.SentTick == _sim.Tick)) continue;
            var entries = log.Skip(c.SentLog).ToList();
            c.Peer.Send(Msg.Turn, Peer.Pack(w =>
            {
                w.Write(_sim.Tick);
                var bytes = CommandCodec.EncodeLog(entries);
                w.Write(bytes.Length);
                w.Write(bytes);
            }));
            c.SentLog = log.Count;
            c.SentTick = _sim.Tick;
        }
    }

    private readonly List<(long Tick, ulong Hash)> _hashQueue = new();

    /// <summary>Call after every host tick: takes the state hash on the check ticks (before that tick's commands apply).</summary>
    public void AfterTick()
    {
        if (_sim.Tick % HashInterval != 0 || _sim.Tick == _lastHashTick || _clients.Count == 0) return;
        _lastHashTick = _sim.Tick;
        _hashQueue.Add((_sim.Tick, StateHash.Compute(_sim)));
    }

    public void Dispose()
    {
        foreach (var c in _clients)
        {
            c.Peer.Send(Msg.Bye, Peer.Pack(w => w.Write("the host closed the game")));
            c.Peer.Dispose();
        }
        _listener.Stop();
    }
}

/// <summary>
/// A client of a lockstep game. <see cref="Connect"/> joins and receives the game so far; <see cref="BuildWorld"/>
/// loads it. Then each frame: <see cref="Poll"/>, and
/// <see cref="Advance"/> runs ticks no further than the host has confirmed, applying each command before the same tick
/// the host did. Its own commands go to the host (<see cref="Send"/>) and come back in a turn like everyone else's.
/// </summary>
public sealed class LockstepClient : IDisposable
{
    private readonly Peer _peer;
    private readonly Queue<LoggedCommand> _pending = new();
    private readonly Dictionary<long, ulong> _hashes = new();

    public int Faction { get; }
    public GameSettings Settings { get; }
    /// <summary>The host's tick when this client joined (the replay runs to it).</summary>
    public long JoinTick { get; }
    private readonly byte[] _joinState;
    /// <summary>How far the host has confirmed: the sim may run up to here.</summary>
    public long ConfirmedTick { get; private set; }
    public IReadOnlyList<NetPlayer> Players { get; private set; } = [];
    public bool Connected => !_peer.Closed;
    public string? CloseReason => _peer.CloseReason;
    /// <summary>Set when the sim's state hash disagreed with the host's: the tick it happened. The sim stops there and
    /// the host is asked for the game again (<see cref="ResyncReady"/>, then <see cref="TakeResync"/>).</summary>
    public long? DesyncTick { get; private set; }
    public event Action<string>? Message;
    /// <summary>The host's fresh snapshot after a desync has arrived: load it with <see cref="TakeResync"/>.</summary>
    public bool ResyncReady => _resync != null;
    /// <summary>How many times this client has been sent the game again.</summary>
    public int Resyncs { get; private set; }
    private byte[]? _resync;

    private LockstepClient(Peer peer, int faction, GameSettings settings, long tick, byte[] state)
    {
        _peer = peer;
        Faction = faction;
        Settings = settings;
        JoinTick = ConfirmedTick = tick;
        _joinState = state;
    }

    /// <summary>Connects, says hello and waits for the welcome (or a refusal, thrown as an exception with the host's reason).</summary>
    public static LockstepClient Connect(string host, int port, string name, int timeoutMs = 10000, string key = "")
    {
        var tcp = new TcpClient();
        if (!tcp.ConnectAsync(host, port).Wait(timeoutMs)) throw new TimeoutException($"no answer from {host}:{port}");
        var peer = new Peer(tcp);
        peer.Send(Msg.Hello, Peer.Pack(w => { w.Write(LockstepHost.Protocol); w.Write(name); w.Write(key); }));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var early = new List<(Msg, byte[])>();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (peer.Inbox.TryDequeue(out var m))
            {
                using var r = new BinaryReader(new MemoryStream(m.Data));
                if (m.Type == Msg.Bye) { peer.Dispose(); throw new InvalidOperationException(r.ReadString()); }
                if (m.Type != Msg.Welcome) { early.Add(m); continue; }
                int faction = r.ReadInt32();
                var settings = GameSettings.Read(r);
                long tick = r.ReadInt64();
                var state = r.ReadBytes(r.ReadInt32());
                var client = new LockstepClient(peer, faction, settings, tick, state);
                foreach (var e in early) peer.Inbox.Enqueue(e); // anything that raced ahead of the welcome
                return client;
            }
            if (peer.Closed) throw new IOException(peer.CloseReason ?? "connection lost");
            Thread.Sleep(5);
        }
        peer.Dispose();
        throw new TimeoutException("the host didn't welcome us in time");
    }

    /// <summary>The host's game as it stood when we joined, from its snapshot.</summary>
    public ColonySim BuildWorld()
    {
        var sim = Snapshot.Load(_joinState);
        return sim;
    }

    public void Send(Command command) => _peer.Send(Msg.Command, Peer.Pack(w => CommandCodec.Write(w, command with { Player = Faction })));

    /// <summary>Takes in what the host sent. Main thread, every frame.</summary>
    public void Poll()
    {
        while (_peer.Inbox.TryDequeue(out var m))
        {
            using var r = new BinaryReader(new MemoryStream(m.Data));
            switch (m.Type)
            {
                case Msg.Turn:
                    long to = r.ReadInt64();
                    foreach (var e in CommandCodec.DecodeLog(r.ReadBytes(r.ReadInt32()))) _pending.Enqueue(e);
                    ConfirmedTick = Math.Max(ConfirmedTick, to);
                    break;
                case Msg.Hash:
                    _hashes[r.ReadInt64()] = r.ReadUInt64();
                    break;
                case Msg.State:
                    // Everything sent before this is in the snapshot: drop it and go on from the host's tick.
                    long at = r.ReadInt64();
                    _resync = r.ReadBytes(r.ReadInt32());
                    _pending.Clear();
                    _hashes.Clear();
                    ConfirmedTick = at;
                    break;
                case Msg.Players:
                    int n = r.ReadInt32();
                    var list = new List<NetPlayer>();
                    for (int i = 0; i < n; i++) list.Add(new NetPlayer(r.ReadInt32(), r.ReadString()));
                    Players = list;
                    break;
                case Msg.Bye:
                    _peer.CloseReason = r.ReadString();
                    _peer.Closed = true;
                    Message?.Invoke($"Disconnected: {_peer.CloseReason}");
                    break;
            }
        }
    }

    /// <summary>
    /// Runs up to <paramref name="maxTicks"/> ticks, never past <see cref="ConfirmedTick"/>, applying each pending
    /// command before the tick it was applied before on the host. Returns how many ticks ran.
    /// </summary>
    public int Advance(ColonySim sim, int maxTicks)
    {
        int ran = 0;
        if (DesyncTick != null) return 0; // out of step: wait for the host's snapshot rather than play on a wrong world
        while (true)
        {
            while (_pending.TryPeek(out var e) && e.Tick <= sim.Tick)
            {
                _pending.Dequeue();
                sim.Commands.Execute(e.Command);
            }
            if (ran >= maxTicks || sim.Tick >= ConfirmedTick) break;
            sim.Step();
            ran++;
            if (_hashes.Remove(sim.Tick, out ulong expected) && DesyncTick is null && StateHash.Compute(sim) != expected)
            {
                DesyncTick = sim.Tick;
                Message?.Invoke($"Desync at tick {sim.Tick}: this world no longer matches the host's; reloading it from the host");
                _peer.Send(Msg.Resync, []);
                break;
            }
        }
        return ran;
    }

    /// <summary>Builds the world from the host's snapshot sent after a desync (the game then swaps to it) and resumes.</summary>
    public ColonySim TakeResync()
    {
        var data = _resync ?? throw new InvalidOperationException("no snapshot from the host yet");
        var sim = Snapshot.Load(data);
        _resync = null;
        DesyncTick = null;
        Resyncs++;
        return sim;
    }

    /// <summary>Ticks the host has confirmed that this sim hasn't run yet.</summary>
    public long Behind(ColonySim sim) => ConfirmedTick - sim.Tick;

    public void Dispose()
    {
        _peer.Send(Msg.Bye, Peer.Pack(w => w.Write("left the game")));
        _peer.Dispose();
    }
}
