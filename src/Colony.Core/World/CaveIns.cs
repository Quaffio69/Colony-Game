namespace Colony.Core.World;

/// <summary>A mass of tiles that lost its support and fell.</summary>
public readonly record struct CaveIn(TilePos Origin, int Tiles, int Drop, long Tick);

/// <summary>
/// Dwarf Fortress-style structural support: a solid tile is supported if a chain of face-adjacent (6-way)
/// solid tiles connects it to the world's bottom layer. After a tile is removed, each neighbouring solid mass
/// that is no longer supported falls straight down as one rigid piece until any part of it lands.
/// <para>
/// The support search is a bounded flood fill that prefers going down, so in ordinary rock it reaches the
/// bottom in roughly <c>z</c> steps. A mass larger than <see cref="MaxMassTiles"/> is treated as
/// self-supporting: huge spans hold, and the per-dig cost stays bounded. Deterministic.
/// </para>
/// </summary>
public sealed class CaveIns
{
    /// <summary>Floods larger than this count as supported.</summary>
    public const int MaxMassTiles = 100_000;

    private readonly VoxelWorld _world;
    // Tile key -> flood index that visited it, reset per check. Reused to avoid per-dig allocations.
    private readonly Dictionary<long, int> _seen = new();
    private readonly Stack<TilePos> _stack = new();
    private readonly List<TilePos> _mass = new();
    private readonly List<(TilePos Pos, TileType Tile)> _moving = new();

    public CaveIns(VoxelWorld world) => _world = world;

    /// <summary>Tiles visited by the last <see cref="AfterRemoval"/> (diagnostics, benchmarks).</summary>
    public int LastVisited { get; private set; }

    /// <summary>
    /// Call after the tile at <paramref name="removed"/> became non-solid. Drops every mass that lost its
    /// support and reports each one through <paramref name="onFall"/>, with the fallen tiles' new positions.
    /// </summary>
    public void AfterRemoval(TilePos removed, long tick, Action<CaveIn, IReadOnlyList<TilePos>>? onFall = null)
    {
        _seen.Clear();
        LastVisited = 0;
        int flood = 0;
        Span<TilePos> neighbours =
        [
            removed.Offset(0, 0, 1), removed.Offset(1, 0, 0), removed.Offset(-1, 0, 0),
            removed.Offset(0, 1, 0), removed.Offset(0, -1, 0), removed.Offset(0, 0, -1),
        ];
        foreach (var start in neighbours)
        {
            if (!IsSolid(start) || _seen.ContainsKey(Key(start))) continue;
            flood++;
            if (IsSupported(start, flood)) continue;

            int drop = DropDistance(flood);
            if (drop <= 0) continue; // can't happen for a genuinely unsupported mass; be safe anyway
            var landed = Move(drop);
            onFall?.Invoke(new CaveIn(start, landed.Count, drop, tick), landed);
        }
    }

    private bool IsSolid(TilePos p) => _world.InBounds(p) && TileInfo.IsStructural(_world.Get(p));

    private long Key(TilePos p) => ((long)p.Z * _world.Depth + p.Y) * _world.Width + p.X;

    /// <summary>Flood fill from <paramref name="start"/>; fills <see cref="_mass"/> when the result is false.</summary>
    private bool IsSupported(TilePos start, int flood)
    {
        _mass.Clear();
        _stack.Clear();
        _stack.Push(start);
        _seen[Key(start)] = flood;
        while (_stack.TryPop(out var p))
        {
            _mass.Add(p);
            LastVisited++;
            if (p.Z == 0 || _mass.Count >= MaxMassTiles) return true;

            // Pushed last = popped first: try down, then sideways, then up.
            if (Visit(p.Offset(0, 0, 1), flood)) return true;
            if (Visit(p.Offset(0, -1, 0), flood)) return true;
            if (Visit(p.Offset(0, 1, 0), flood)) return true;
            if (Visit(p.Offset(-1, 0, 0), flood)) return true;
            if (Visit(p.Offset(1, 0, 0), flood)) return true;
            if (Visit(p.Offset(0, 0, -1), flood)) return true;
        }
        return false;
    }

    /// <summary>Queues a solid tile; true if it belongs to an earlier flood, which was therefore supported.</summary>
    private bool Visit(TilePos q, int flood)
    {
        if (!IsSolid(q)) return false;
        if (_seen.TryGetValue(Key(q), out int other)) return other != flood;
        _seen[Key(q)] = flood;
        _stack.Push(q);
        return false;
    }

    /// <summary>How far the mass in <see cref="_mass"/> can fall before some tile of it rests on solid ground.</summary>
    private int DropDistance(int flood)
    {
        int drop = int.MaxValue;
        foreach (var p in _mass)
        {
            var below = p.Offset(0, 0, -1);
            if (_seen.TryGetValue(Key(below), out int f) && f == flood) continue; // part of the mass itself
            int free = 0;
            for (int z = p.Z - 1; z >= 0 && !TileInfo.IsStructural(_world.Get(p.X, p.Y, z)); z--)
                if (++free >= drop) break;
            drop = Math.Min(drop, free);
            if (drop == 0) break;
        }
        return drop == int.MaxValue ? 0 : drop;
    }

    private List<TilePos> Move(int drop)
    {
        _moving.Clear();
        foreach (var p in _mass) _moving.Add((p, _world.Get(p)));
        foreach (var (p, _) in _moving) _world.Set(p, TileType.Air);
        var landed = new List<TilePos>(_moving.Count);
        foreach (var (p, tile) in _moving)
        {
            var q = p.Offset(0, 0, -drop);
            _world.Set(q, tile);
            landed.Add(q);
        }
        return landed;
    }
}
