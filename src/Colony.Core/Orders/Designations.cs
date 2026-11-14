using Colony.Core.World;

namespace Colony.Core.Orders;

public enum DesignationKind : byte
{
    /// <summary>Remove the tile, leaving air.</summary>
    Dig,
    /// <summary>Carve the tile into an <see cref="TileType.UpStair"/>.</summary>
    UpStair,
    /// <summary>Carve the tile into a <see cref="TileType.DownStair"/>.</summary>
    DownStair,
    /// <summary>Carve the tile into an <see cref="TileType.UpDownStair"/>.</summary>
    UpDownStair,
    /// <summary>Fell the tree standing in this (open) cell; leaves logs.</summary>
    Chop,
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

    /// <summary>Trees, for validating <see cref="DesignationKind.Chop"/> orders (set by <see cref="ColonySim"/>).</summary>
    public Nature.Flora? Flora { get; set; }

    public bool Designate(TilePos p, DesignationKind kind = DesignationKind.Dig)
    {
        if (!_world.InBounds(p)) return false;
        if (kind == DesignationKind.Chop)
        {
            if (Flora is null || !Flora.TryGet(p, out var plant) || !Nature.PlantInfo.IsTree(plant.Kind)) return false;
        }
        else
        {
            var tile = _world.Get(p);
            if (tile == TileType.Air) return false;
            if (Result(kind, tile) == tile) return false; // nothing would change
        }
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
    /// Chop orders on every tree whose column lies in the rectangle between two corners, at any level:
    /// felling always happens on the surface, so the area is picked in 2D. Returns how many trees were marked.
    /// </summary>
    public int DesignateChopArea(int x0, int y0, int x1, int y1) =>
        ForTreesInArea(x0, y0, x1, y1, p => Designate(p, DesignationKind.Chop));

    /// <summary>Cancels chop orders in the columns of a rectangle (any level). Returns how many were removed.</summary>
    public int CancelChopArea(int x0, int y0, int x1, int y1) =>
        ForTreesInArea(x0, y0, x1, y1, p => _orders.TryGetValue(p, out var k) && k == DesignationKind.Chop && Cancel(p));

    private int ForTreesInArea(int x0, int y0, int x1, int y1, Func<TilePos, bool> action)
    {
        if (Flora is null) return 0;
        (x0, x1) = (Math.Max(0, Math.Min(x0, x1)), Math.Min(_world.Width - 1, Math.Max(x0, x1)));
        (y0, y1) = (Math.Max(0, Math.Min(y0, y1)), Math.Min(_world.Depth - 1, Math.Max(y0, y1)));
        var hits = new List<TilePos>();
        for (int ry = y0 >> Nature.Flora.RegionShift; ry <= y1 >> Nature.Flora.RegionShift; ry++)
        for (int rx = x0 >> Nature.Flora.RegionShift; rx <= x1 >> Nature.Flora.RegionShift; rx++)
            foreach (var plant in Flora.InRegion(rx, ry))
            {
                var p = plant.Position;
                if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1 && Nature.PlantInfo.IsTree(plant.Kind)) hits.Add(p);
            }
        // Region lists reorder on removal: sort so the result never depends on that history.
        hits.Sort((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        int n = 0;
        foreach (var p in hits)
            if (action(p)) n++;
        return n;
    }

    /// <summary>
    /// Designates a connected staircase in the inclusive box between two corners (any size, any number of
    /// levels): down stairs on the top level, up/down stairs in between, up stairs on the bottom level.
    /// A single-level box gets up/down stairs. The world's bottom layer is never touched.
    /// </summary>
    /// <returns>How many tiles were designated.</returns>
    public int DesignateStairs(TilePos a, TilePos b)
    {
        int top = Math.Max(a.Z, b.Z), bottom = Math.Max(1, Math.Min(a.Z, b.Z));
        int n = 0;
        for (int z = top; z >= bottom; z--)
        {
            var kind = top == bottom ? DesignationKind.UpDownStair
                : z == top ? DesignationKind.DownStair
                : z == bottom ? DesignationKind.UpStair
                : DesignationKind.UpDownStair;
            n += DesignateBox(a with { Z = z }, b with { Z = z }, kind);
        }
        return n;
    }

    /// <summary>
    /// The tile a designation leaves behind. Carving a stair into an existing stair adds directions, DF-style:
    /// an up stair designated onto a down stair makes an up/down stair.
    /// </summary>
    public static TileType Result(DesignationKind kind, TileType current)
    {
        if (kind == DesignationKind.Chop) return current;
        if (kind == DesignationKind.Dig) return TileType.Air;
        bool up = kind is DesignationKind.UpStair or DesignationKind.UpDownStair || TileInfo.GoesUp(current);
        bool down = kind is DesignationKind.DownStair or DesignationKind.UpDownStair || TileInfo.GoesDown(current);
        return up && down ? TileType.UpDownStair : up ? TileType.UpStair : TileType.DownStair;
    }

    public static bool IsStair(DesignationKind kind) =>
        kind is DesignationKind.UpStair or DesignationKind.DownStair or DesignationKind.UpDownStair;

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

    /// <summary>
    /// Carries out a terrain designation (dig or carve) and removes it; a <see cref="DesignationKind.Chop"/> is
    /// only removed (felling the tree is <see cref="ColonySim.CompleteDesignation"/>'s job). False if none.
    /// </summary>
    public bool Complete(TilePos p)
    {
        if (!_orders.Remove(p, out var kind)) return false;
        _claims.Remove(p);
        if (kind != DesignationKind.Chop) _world.Set(p, Result(kind, _world.Get(p)));
        Version++;
        return true;
    }

    /// <summary>How many simulation ticks of work a designation takes.</summary>
    public static int WorkTicks(DesignationKind kind, TileType tile)
    {
        int dig = tile switch
        {
            TileType.Grass or TileType.Soil => 25,
            TileType.Stone => 60,
            TileType.Ore => 80,
            _ when TileInfo.IsStair(tile) => 20, // already carved: only reshaping or clearing it
            _ => 40,
        };
        return IsStair(kind) ? dig + 30 : dig;
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
