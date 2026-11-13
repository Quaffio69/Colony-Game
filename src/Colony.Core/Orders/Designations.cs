using Colony.Core.World;

namespace Colony.Core.Orders;

public enum DesignationKind : byte
{
    /// <summary>Remove the tile, leaving air.</summary>
    Dig,
    /// <summary>Carve the solid tile into a <see cref="TileType.Stairs"/> step.</summary>
    CarveStairs,
}

/// <summary>
/// Player-placed work orders on tiles. Only solid tiles can be designated. Colonists claim a designation
/// while working on it, so two never take the same job.
/// </summary>
public sealed class Designations
{
    private readonly VoxelWorld _world;
    private readonly Dictionary<TilePos, DesignationKind> _orders = new();
    private readonly Dictionary<TilePos, int> _claims = new();

    public Designations(VoxelWorld world) => _world = world;

    /// <summary>Increments on every change, so views and idle colonists can cheaply notice updates.</summary>
    public int Version { get; private set; }
    public int Count => _orders.Count;
    public int UnclaimedCount => _orders.Count - _claims.Count;
    public IReadOnlyDictionary<TilePos, DesignationKind> All => _orders;

    public bool Contains(TilePos p) => _orders.ContainsKey(p);
    public bool TryGet(TilePos p, out DesignationKind kind) => _orders.TryGetValue(p, out kind);

    public bool Designate(TilePos p, DesignationKind kind = DesignationKind.Dig)
    {
        if (!_world.InBounds(p)) return false;
        var tile = _world.Get(p);
        if (tile == TileType.Air) return false;
        if (kind == DesignationKind.CarveStairs && tile == TileType.Stairs) return false;
        if (_orders.TryGetValue(p, out var existing) && existing == kind) return false;

        _orders[p] = kind;
        _claims.Remove(p); // the job changed; whoever held it will notice and drop it
        Version++;
        return true;
    }

    public bool Cancel(TilePos p)
    {
        if (!_orders.Remove(p)) return false;
        _claims.Remove(p);
        Version++;
        return true;
    }

    /// <summary>Designates every solid tile in the inclusive box between two corners; returns how many were added.</summary>
    public int DesignateBox(TilePos a, TilePos b, DesignationKind kind = DesignationKind.Dig) =>
        ForBox(a, b, p => Designate(p, kind));

    /// <summary>Cancels every designation in the inclusive box between two corners; returns how many were removed.</summary>
    public int CancelBox(TilePos a, TilePos b) => ForBox(a, b, Cancel);

    /// <summary>
    /// Designates a spiral staircase in the rectangle spanned by <paramref name="a"/> and <paramref name="b"/>
    /// (x/y only; at least 2x2), occupying levels <paramref name="topZ"/> down to <paramref name="topZ"/> - depth + 1.
    /// On each level one perimeter tile becomes a stair step and the rest is dug out. The step advances one
    /// tile around the perimeter per level, so every step is a one-level climb from the previous one.
    /// Arriving at the bottom, an agent stands on the (undug) level below the stairs.
    /// </summary>
    /// <returns>How many tiles were designated.</returns>
    public int DesignateStairs(TilePos a, TilePos b, int topZ, int depth)
    {
        int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
        int y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
        if (x1 - x0 < 1 || y1 - y0 < 1) throw new ArgumentException("Stairs need at least a 2x2 footprint.");
        if (depth < 1) throw new ArgumentOutOfRangeException(nameof(depth));

        var ring = PerimeterRing(x0, y0, x1, y1);
        int n = 0;
        int bottom = Math.Max(1, topZ - depth + 1); // never dig out the world's floor
        for (int z = topZ; z >= bottom; z--)
        {
            var (sx, sy) = ring[(topZ - z) % ring.Count];
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var kind = x == sx && y == sy ? DesignationKind.CarveStairs : DesignationKind.Dig;
                if (Designate(new TilePos(x, y, z), kind)) n++;
            }
        }
        return n;
    }

    /// <summary>Perimeter cells of a rectangle in walking order (each orthogonally adjacent to the next, wrapping).</summary>
    public static List<(int X, int Y)> PerimeterRing(int x0, int y0, int x1, int y1)
    {
        var ring = new List<(int, int)>();
        for (int x = x0; x <= x1; x++) ring.Add((x, y0));
        for (int y = y0 + 1; y <= y1; y++) ring.Add((x1, y));
        for (int x = x1 - 1; x >= x0; x--) ring.Add((x, y1));
        for (int y = y1 - 1; y > y0; y--) ring.Add((x0, y));
        return ring;
    }

    public bool IsClaimed(TilePos p) => _claims.ContainsKey(p);

    public bool IsClaimedBy(TilePos p, int colonistId) => _claims.TryGetValue(p, out int id) && id == colonistId;

    public bool Claim(TilePos p, int colonistId)
    {
        if (!_orders.ContainsKey(p) || _claims.ContainsKey(p)) return false;
        _claims[p] = colonistId;
        return true;
    }

    public void Release(TilePos p, int colonistId)
    {
        if (IsClaimedBy(p, colonistId)) _claims.Remove(p);
    }

    /// <summary>Carries out a designation (dig or carve) and removes it. Returns false if there was none.</summary>
    public bool Complete(TilePos p)
    {
        if (!_orders.Remove(p, out var kind)) return false;
        _claims.Remove(p);
        _world.Set(p, kind == DesignationKind.Dig ? TileType.Air : TileType.Stairs);
        Version++;
        return true;
    }

    /// <summary>How many simulation ticks of work a designation takes.</summary>
    public static int WorkTicks(DesignationKind kind, TileType tile)
    {
        int dig = tile switch
        {
            TileType.Grass or TileType.Soil => 25,
            TileType.Stone or TileType.Stairs => 60,
            TileType.Ore => 80,
            _ => 40,
        };
        return kind == DesignationKind.CarveStairs ? dig + 30 : dig;
    }

    private static int ForBox(TilePos a, TilePos b, Func<TilePos, bool> action)
    {
        int n = 0;
        for (int z = Math.Min(a.Z, b.Z); z <= Math.Max(a.Z, b.Z); z++)
        for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
        for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
            if (action(new TilePos(x, y, z))) n++;
        return n;
    }
}
