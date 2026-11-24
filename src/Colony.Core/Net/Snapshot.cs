using System.IO.Compression;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Net;

/// <summary>Binary helpers for snapshots.</summary>
internal static class SnapIO
{
    /// <summary>Version of the snapshot being read (on this thread): sections added later are skipped in older ones.</summary>
    [ThreadStatic] public static int ReadVersion;
    /// <summary>Reading a snapshot older than <paramref name="version"/> (0, not set: the current one).</summary>
    public static bool Before(int version) => ReadVersion != 0 && ReadVersion < version;

    public static void Write(this BinaryWriter w, TilePos p) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
    public static TilePos ReadPos(this BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

    public static void WriteInts(this BinaryWriter w, int[] a)
    {
        w.Write(a.Length);
        w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
    }

    public static int[] ReadInts(this BinaryReader r)
    {
        var a = new int[r.ReadInt32()];
        r.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
        return a;
    }

    public static void WriteShorts(this BinaryWriter w, short[] a)
    {
        w.Write(a.Length);
        w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
    }

    public static short[] ReadShorts(this BinaryReader r)
    {
        var a = new short[r.ReadInt32()];
        r.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
        return a;
    }

    public static void WriteBools(this BinaryWriter w, bool[] a)
    {
        w.Write(a.Length);
        w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
    }

    public static bool[] ReadBools(this BinaryReader r)
    {
        var a = new bool[r.ReadInt32()];
        r.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
        return a;
    }

    public static void WriteFloats(this BinaryWriter w, float[] a)
    {
        w.Write(a.Length);
        foreach (float v in a) w.Write(v);
    }

    public static void ReadFloatsInto(this BinaryReader r, float[] into)
    {
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            float v = r.ReadSingle();
            if (i < into.Length) into[i] = v;
        }
    }

    public static void Section(this BinaryWriter w, string name) => w.Write(name);

    public static void Section(this BinaryReader r, string name)
    {
        string got = r.ReadString();
        if (got != name) throw new InvalidDataException($"snapshot out of step: expected section '{name}', found '{got}'");
    }

    /// <summary>Rebuilds a dictionary in its current enumeration order, so its order no longer depends on past removals.</summary>
    public static void Compact<TKey, TValue>(this Dictionary<TKey, TValue> d) where TKey : notnull
    {
        var items = d.ToList();
        d.Clear();
        foreach (var (k, v) in items) d.Add(k, v);
    }

    public static void Compact<T>(this HashSet<T> s)
    {
        var items = s.ToList();
        s.Clear();
        foreach (var v in items) s.Add(v);
    }
}

/// <summary>
/// The whole game state in one blob: regenerate the terrain from <see cref="GameSettings"/>, then lay back the chunks
/// changed since, the water, plants, items, orders, zones, rooms, blueprints, waterworks, colonies (societies, homes)
/// and colonists. Taking one first <see cref="ColonySim.Settle"/>s the live game (colonists put down what they were
/// doing, rebuildable caches are cleared, containers are compacted), and the live game goes on from exactly the
/// state a loaded copy starts from, so a snapshot loads bit-identical (tested by playing both on). Far faster than
/// replaying a long game, and what a host sends a player who joins.
/// </summary>
public static class Snapshot
{
    private const string Magic = "COLONYSNAP";
    /// <summary>9: machines (kind, fuel). 8: unseen cavern cells per chunk. 7: order priorities. 6: blueprints built from another cell (decks, stairs). 5: machine stock limits. 4: trade depots and deals (a seventh work type). 3: storage racks and grass regrowth. Versions 2-4 still
    /// load (without what came later).</summary>
    private const int Version = 9;

    /// <summary>Where the last load or take spent its time (ms), for the bench.</summary>
    public static string LastTimings { get; internal set; } = "";

    /// <summary>
    /// Settles the game (as a command, logged and, online, applied by every peer) and writes its state (gzip). The live
    /// game continues from the settled state.
    /// </summary>
    public static byte[] Take(ColonySim sim, GameSettings settings)
    {
        sim.Commands.Execute(new Commands.SettleGame()); // a command: online, every peer settles before this tick too
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(Version);
            settings.Write(w);
        }
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new BinaryWriter(gz))
            sim.SnapSave(w);
        return ms.ToArray();
    }

    public static GameSettings ReadSettings(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        return Header(r);
    }

    private static GameSettings Header(BinaryReader r)
    {
        if (r.ReadString() != Magic) throw new InvalidDataException("not a Colony snapshot");
        int version = r.ReadInt32();
        if (version is < 2 or > Version) throw new InvalidDataException($"snapshot version {version} (this game reads 2..{Version})");
        SnapIO.ReadVersion = version;
        return GameSettings.Read(r);
    }

    /// <summary>Rebuilds the game a snapshot holds. Off the main thread is fine (nothing else touches the new sim).</summary>
    public static ColonySim Load(byte[] data, Action<float>? progress = null)
    {
        var ms = new MemoryStream(data);
        GameSettings settings;
        using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true)) settings = Header(r);
        progress?.Invoke(0.05f);
        var genClock = System.Diagnostics.Stopwatch.StartNew();
        var generated = WorldGenerator.GenerateWithTerrain(new WorldGenSettings(settings.Seed, settings.Width, settings.Depth, settings.Height));
        long genMs = genClock.ElapsedMilliseconds;
        progress?.Invoke(0.6f);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var body = new BinaryReader(gz);
        var sim = ColonySim.SnapLoad(body, generated.World, generated.Terrain);
        LastTimings = $"terrain {genMs}, " + LastTimings;
        progress?.Invoke(1f);
        return sim;
    }
}
