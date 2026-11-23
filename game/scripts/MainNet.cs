using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core;
using Colony.Core.Commands;
using Colony.Core.Net;
using Godot;

namespace ColonyGame;

/// <summary>
/// Online play (lockstep, <see cref="LockstepHost"/>/<see cref="LockstepClient"/>): hosting the current world or a new
/// one, joining a host, and the per-frame work. The host's sim runs as usual and streams turns; a client's sim only runs
/// the ticks the host has confirmed, and its commands go to the host and come back in a turn. Sessions live in statics
/// so they survive the scene reload that opens a joined world.
/// </summary>
public partial class Main
{
    public const int DefaultPort = 7777;
    private static LockstepHost? s_host;
    private static LockstepClient? s_client;
    private static int? s_hostPort;
    private static GameSettings? s_settings;
    /// <summary>The sim made from <see cref="s_settings"/> (generated, loaded or joined; not a test's world): only it can be saved or shared.</summary>
    private static ColonySim? s_replayable;
    private static long s_lastNetCheck;

    public LockstepHost? Host => s_host;
    public LockstepClient? Client => s_client;
    public bool Online => s_host != null || s_client != null;
    /// <summary>This world can be saved and shared (snapshots hold everything; only test worlds lack the settings).</summary>
    public bool Shareable => ReferenceEquals(s_replayable, _sim) && s_settings != null;
    public string PlayerName { get; set; } = System.Environment.UserName is { Length: > 0 } n ? n : "Player";

    /// <summary>Remembers how a freshly generated world was made (called once it exists).</summary>
    private void RememberSettings()
    {
        s_settings = new GameSettings(Seed, WorldWidth, WorldDepth, WorldHeight, StartingColonists, Colonies);
        s_replayable = _sim;
    }


    /// <summary>Hosts this world (it needs a free colony for each guest). Null on success, else why not.</summary>
    public string? HostGame(int port)
    {
        if (Online) return "Already online";
        if (!Shareable) return "A test's world can't be shared: start a new one";
        if (_sim.Factions.Count < 2) return "This world has one colony: start a new one with room for guests";
        try
        {
            s_host = new LockstepHost(_sim, s_settings!, port, PlayerName);
        }
        catch (Exception e)
        {
            return $"Can't host on port {port}: {e.Message}";
        }
        HookSession();
        UI.GlyphStatus.Say($"Hosting on port {s_host.Port}: {_sim.Factions.Count - 1} colonies free for guests");
        return null;
    }

    /// <summary>Starts a new world for <paramref name="colonies"/> players and hosts it once it is made.</summary>
    public void HostNewGame(int seed, int size, int colonies, int port)
    {
        s_hostPort = port;
        NewColony(seed, size, Math.Max(2, colonies));
    }

    /// <summary>Joins a host: receives the game so far, builds the same world and opens it. Null on success, else why not.</summary>
    public string? JoinGame(string address, int port)
    {
        if (Online) return "Already online";
        try
        {
            var client = LockstepClient.Connect(address, port, PlayerName);
            var sim = client.BuildWorld(); // the same world, replayed to where the host was when we joined
            s_client = client;
            s_settings = client.Settings;
            s_replayable = sim;
            s_player = client.Faction;
            s_launch = sim;
            s_home = null;
            s_launchResult = null;
            GetTree().ReloadCurrentScene();
            return null;
        }
        catch (Exception e)
        {
            return $"Couldn't join {address}:{port}: {e.InnerException?.Message ?? e.Message}";
        }
    }

    public void LeaveOnline()
    {
        s_host?.Dispose();
        s_client?.Dispose();
        s_host = null;
        s_client = null;
        _tool.Remote = null;
        UI.GlyphStatus.Say("Offline");
    }

    /// <summary>After the scene (re)loads: picks up a pending host request and wires the session to this scene.</summary>
    private void ReadyNet()
    {
        if (s_hostPort is { } port)
        {
            s_hostPort = null;
            if (HostGame(port) is { } problem) UI.GlyphStatus.Say(problem, 8);
        }
        else HookSession();
    }

    private void HookSession()
    {
        if (s_client is { } client)
        {
            Player = _tool.Player = client.Faction;
            _tool.Remote = c => client.Send(c);
            _sim.Commands.Applied += OnRemoteApplied;
            client.Message -= OnNetMessage;
            client.Message += OnNetMessage;
        }
        if (s_host is { } host)
        {
            host.Message -= OnNetMessage;
            host.Message += OnNetMessage;
        }
    }

    private void OnNetMessage(string text) => UI.GlyphStatus.Say(text, 6);

    /// <summary>A client sees what its own commands did once they come back from the host.</summary>
    private void OnRemoteApplied(Command command, string message)
    {
        if (command.Player == Player) _tool.ShowResult(message);
    }

    private void UnhookNet()
    {
        _sim.Commands.Applied -= OnRemoteApplied;
        if (s_client != null) s_client.Message -= OnNetMessage;
        if (s_host != null) s_host.Message -= OnNetMessage;
    }

    /// <summary>A client's frame: take in turns and run up to the host's confirmed tick. True if it handled the sim this frame.</summary>
    private bool ClientFrame()
    {
        if (s_client is not { } client) return false;
        client.Poll();
        if (client.ResyncReady)
        {
            // Out of step with the host: open its fresh snapshot (a scene reload, like joining) instead of playing on.
            var sim = client.TakeResync();
            s_replayable = sim;
            s_launch = sim;
            s_launchResult = null;
            GetTree().ReloadCurrentScene();
            return true;
        }
        if (!_sim.WaterReady) return true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long behind = client.Behind(_sim);
        // Run what the host has confirmed; when far behind (a slow frame, a long join), catch up over a few frames.
        client.Advance(_sim, (int)Math.Clamp(behind <= 60 ? behind : behind / 3, 0, 400));
        _simMsThisFrame = clock.Elapsed.TotalMilliseconds;
        return true;
    }

    /// <summary>One line for the HUD: who you are online and how it's going.</summary>
    public string NetStatus
    {
        get
        {
            if (s_host is { } host) return $"Hosting :{host.Port} · {host.Players.Count}/{_sim.Factions.Count} players";
            if (s_client is { } client)
            {
                if (!client.Connected) return $"Disconnected ({client.CloseReason})";
                if (client.DesyncTick is { } t) return $"Out of step at tick {t}: reloading from the host...";
                long behind = client.Behind(_sim);
                return $"Online · {client.Players.Count} players" + (behind > 20 ? $" · {behind} behind" : "");
            }
            return "";
        }
    }

    /// <summary>
    /// --net-check=TICKS (with --host or --join): prints the state hash when the sim reaches TICKS and quits (a host
    /// waits a little longer for its guests): run a host and a client as two processes and compare the lines.
    /// </summary>
    private void NetCheck()
    {
        if (!long.TryParse(Arg("--net-check"), out long at) || _sim.Tick < at || s_lastNetCheck == at) return;
        if (_sim.Tick >= at && s_lastNetCheck < at)
        {
            s_lastNetCheck = at;
            GD.Print($"[net] {(s_host != null ? "host" : s_client != null ? "client" : "solo")} tick {_sim.Tick} hash {StateHash.Compute(_sim):x16}" +
                     $" (ants slain {_sim.Wildlife.Slain}, out {_sim.Wildlife.Creatures.Count}, colonists hurt {_sim.Colonists.Count(c => c.Health < Colony.Core.Agents.Colonist.MaxHealth)}, dead {_sim.Deaths})" +
                     (s_client?.DesyncTick is { } d ? $" DESYNC at {d}" : "") + (s_client is { Resyncs: > 0 } rc ? $" (resynced {rc.Resyncs}x)" : ""));
            if (s_client != null) GetTree().Quit();
        }
    }

    private static bool s_desynced;

    /// <summary>--net-desync=TICK (a client): puts a stone into this world alone at that tick, so the next hash check
    /// finds it out of step and it reloads from the host (the NetCheck line then says how many resyncs).</summary>
    private void NetDesyncTest()
    {
        if (s_desynced || s_client == null || !long.TryParse(Arg("--net-desync"), out long at) || _sim.Tick < at) return;
        s_desynced = true;
        _sim.Items.Drop(_sim.Colonists[0].Position, Colony.Core.Items.ItemType.Stone, 7);
        GD.Print($"[net] client dropped a stone at tick {_sim.Tick} (a deliberate desync)");
    }

    private long _netQuitAt = -1;

    /// <summary>Dev flags: --host[=PORT], --join=ADDRESS[:PORT], --name=NAME, --speed=0..2.</summary>
    private void NetDevArgs()
    {
        if (Arg("--name") is { Length: > 0 } name) PlayerName = name;
        if (int.TryParse(Arg("--speed"), out int speed)) SpeedIndex = speed;
        if (Arg("--host") is { } host && !Online)
        {
            int port = int.TryParse(host, out int p) ? p : DefaultPort;
            if (HostGame(port) is { } problem) GD.PrintErr($"[net] {problem}");
            else GD.Print($"[net] hosting on {s_host!.Port}");
        }
        if (Arg("--join") is { Length: > 0 } join && !Online)
        {
            var parts = join.Split(':');
            int port = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : DefaultPort;
            if (JoinGame(parts[0], port) is { } problem) GD.PrintErr($"[net] {problem}");
        }
    }

    private bool _netOrdersGiven;

    /// <summary>
    /// --net-orders: a few frames in, this player orders its colony about through the normal command path (fell the
    /// woods round it, dig a room, zone a stockpile; with --net-ants also an angry ant nest beside it), so a --net-check
    /// run exercises commands from both sides.
    /// </summary>
    private void NetOrders()
    {
        if (_netOrdersGiven || Arg("--net-orders") == null || _frames < 30 || !_sim.WaterReady) return;
        if (_sim.Colonists.FirstOrDefault(c => c.Faction == Player) is not { } c) return;
        _netOrdersGiven = true;
        var p = c.Position;
        _tool.Issue(new DesignatePlants(Colony.Core.Orders.DesignationKind.Chop, p.X - 25, p.Y - 25, p.X + 25, p.Y + 25));
        _tool.Issue(new DesignateBox(p.Offset(3, -2, -1), p.Offset(6, 2, -1), Colony.Core.Orders.DesignationKind.Dig));
        _tool.Issue(new AddStockpile(p.Offset(-5, -5, -1), p.Offset(-2, -2, -1)));
        if (Arg("--net-ants") != null) // --net-ants: an angry nest next door, so the check covers a fight
            _tool.Issue(new DebugSpawnNest(new Colony.Core.World.TilePos(p.X + 10, p.Y, _sim.World.SurfaceLevel(p.X + 10, p.Y) + 1), 25, true));
        GD.Print($"[net] {(s_client != null ? "client" : "host")} gave orders as colony {Player}");
    }

    /// <summary>The host side of --net-check: quits a while after the check tick so guests can get there.</summary>
    private void HostNetCheckQuit()
    {
        if (s_host == null || !long.TryParse(Arg("--net-check"), out long at)) return;
        if (_sim.Tick >= at && _netQuitAt < 0) _netQuitAt = (long)Time.GetTicksMsec() + 15000;
        if (_netQuitAt > 0 && Time.GetTicksMsec() > (ulong)_netQuitAt) GetTree().Quit();
    }
}
