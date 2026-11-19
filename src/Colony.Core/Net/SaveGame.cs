using Colony.Core.Commands;

namespace Colony.Core.Net;

/// <summary>
/// A saved game: how the world was made (<see cref="GameSettings"/>), every command applied (with its tick) and the
/// tick reached. Loading regenerates the world and replays the log, so a save is small and exact (and an online host's
/// save holds every player's commands). Snapshots of the full state would load long games faster; until then a game
/// replays at simulation speed (about 2000 ticks a second on a big map).
/// </summary>
public sealed record SaveGame(GameSettings Settings, long Tick, IReadOnlyList<LoggedCommand> Log, string Name, int Player, long SavedAt)
{
    private const string Magic = "COLONYSAVE";
    private const int Version = 1;

    public static SaveGame Of(ColonySim sim, GameSettings settings, string name, int player) =>
        new(settings, sim.Tick, sim.Commands.Log.ToList(), name, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

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
        var log = CommandCodec.EncodeLog(Log);
        w.Write(log.Length);
        w.Write(log);
    }

    /// <summary>Reads a save (the log too unless <paramref name="headerOnly"/>, for listing saves quickly).</summary>
    public static SaveGame Read(Stream stream, bool headerOnly = false)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadString() != Magic) throw new InvalidDataException("not a Colony save");
        int version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"save version {version} (this game reads {Version})");
        string name = r.ReadString();
        int player = r.ReadInt32();
        long savedAt = r.ReadInt64();
        var settings = GameSettings.Read(r);
        long tick = r.ReadInt64();
        var log = headerOnly ? new List<LoggedCommand>() : CommandCodec.DecodeLog(r.ReadBytes(r.ReadInt32()));
        return new SaveGame(settings, tick, log, name, player, savedAt);
    }

    /// <summary>
    /// Rebuilds the game: generates the world and replays to <see cref="Tick"/>. <paramref name="progress"/> gets 0..1
    /// now and then; safe to run off the main thread (the sim is touched by nobody else until it returns).
    /// </summary>
    public ColonySim Load(Action<float>? progress = null, CancellationToken cancel = default)
    {
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
