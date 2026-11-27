using Colony.Core.Commands;

namespace Colony.Core.Net;

/// <summary>
/// A saved game. Version 3 adds the world-generation choices (<see cref="WorldShape"/>; older saves rebuild their
/// world with the first generator). Version 2 holds a <see cref="Snapshot"/> of the whole state (loads in about the time it takes to
/// make the terrain); version 1 held the command log instead (loads by regenerating and replaying the game at sim
/// speed) and still loads. Both carry the settings, the tick, a name, the local player and when it was saved.
/// </summary>
public sealed record SaveGame(GameSettings Settings, long Tick, IReadOnlyList<LoggedCommand> Log, string Name, int Player, long SavedAt,
    byte[]? State = null)
{
    private const string Magic = "COLONYSAVE";
    private const int Version = 3;

    /// <summary>Saves a game as a snapshot (this settles it: see <see cref="Snapshot.Take"/>).</summary>
    public static SaveGame Of(ColonySim sim, GameSettings settings, string name, int player)
    {
        var state = Snapshot.Take(sim, settings);
        return new(settings, sim.Tick, [], name, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), state);
    }

    public void Write(Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(Name);
        w.Write(Player);
        w.Write(SavedAt);
        Settings.Write(w);
        w.Write(Tick);
        w.Write(State != null);
        var body = State ?? CommandCodec.EncodeLog(Log);
        w.Write(body.Length);
        w.Write(body);
    }

    /// <summary>Reads a save (its body too unless <paramref name="headerOnly"/>, for listing saves quickly).</summary>
    public static SaveGame Read(Stream stream, bool headerOnly = false)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadString() != Magic) throw new InvalidDataException("not a Colony save");
        int version = r.ReadInt32();
        if (version is < 1 or > Version) throw new InvalidDataException($"save version {version} (this game reads up to {Version})");
        string name = r.ReadString();
        int player = r.ReadInt32();
        long savedAt = r.ReadInt64();
        var settings = GameSettings.Read(r, withShape: version >= 3);
        long tick = r.ReadInt64();
        bool snapshot = version == 2 || version >= 3 && r.ReadBoolean();
        if (headerOnly) return new SaveGame(settings, tick, [], name, player, savedAt);
        var body = r.ReadBytes(r.ReadInt32());
        return !snapshot
            ? new SaveGame(settings, tick, CommandCodec.DecodeLog(body), name, player, savedAt)
            : new SaveGame(settings, tick, [], name, player, savedAt, body);
    }

    /// <summary>
    /// Rebuilds the game: from the snapshot, or (old saves) by generating the world and replaying to <see cref="Tick"/>.
    /// <paramref name="progress"/> gets 0..1 now and then; safe off the main thread.
    /// </summary>
    public ColonySim Load(Action<float>? progress = null, CancellationToken cancel = default)
    {
        if (State != null) return Snapshot.Load(State, progress);
        progress?.Invoke(0);
        var sim = Settings.Create();
        progress?.Invoke(0.05f);
        int next = 0;
        while (sim.Tick < Tick || next < Log.Count)
        {
            cancel.ThrowIfCancellationRequested();
            while (next < Log.Count && Log[next].Tick <= sim.Tick) sim.Commands.Execute(Log[next++].Command);
            if (sim.Tick >= Tick) break;
            sim.Step();
            if (sim.Tick % 200 == 0) progress?.Invoke(0.05f + 0.95f * sim.Tick / Math.Max(1, Tick));
        }
        progress?.Invoke(1);
        return sim;
    }
}
