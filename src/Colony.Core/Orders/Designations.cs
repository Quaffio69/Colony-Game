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
    /// <summary>Harvest the ripe plant in this (open) cell (berries, fiber); the plant is cut back, not removed.</summary>
    Gather,
    /// <summary>Plant a farm plot's crop in this (open) cell. Placed by <see cref="Nature.Farms"/>, not the player.</summary>
    Sow,
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
    private readonly Owners _owners = new();
    private readonly Dictionary<int, int> _counts = new(); // orders per colony (never enumerated)
    private readonly Dictionary<TilePos, byte> _priority = new(); // orders not at DefaultPriority

    /// <summary>How many orders a colony has (capped at <see cref="Balance.MaxOrders"/>; farm sowing excepted).</summary>
    public int OrdersOf(int faction) => _counts.GetValueOrDefault(faction);

    /// <summary>Set when an order was refused for the cap; box commands read and reset it to say so.</summary>
    public bool LimitReached { get; set; }

    private void Uncount(TilePos p)
    {
        int f = _owners.Of(p);
        _counts[f] = Math.Max(0, OrdersOf(f) - 1);
    }

    /// <summary>The faction whose order this is (0 when unowned or the first colony).</summary>
    public int OwnerOf(TilePos p) => _owners.Of(p);

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

    public bool Designate(TilePos p, DesignationKind kind = DesignationKind.Dig, int faction = 0)
    {
        if (!_world.InBounds(p)) return false;
        if (kind == DesignationKind.Sow)
        {
            if (CanSow?.Invoke(p) != true) return false;
        }
        else if (IsPlantOrder(kind))
        {
            if (Flora is null || !Flora.TryGet(p, out var plant) || !Accepts(kind, plant)) return false;
        }
        else
        {
            var tile = _world.Get(p);
            if (tile is TileType.Air or TileType.Deck) return false; // a built floor is taken down (Demolish), not dug
            if (_world.IsBedrock(p)) return false; // the floor of the world
            if (Result(kind, tile) == tile) return false; // nothing would change
        }
        bool exists = _orders.TryGetValue(p, out var existing);
        if (exists && (existing == kind || _owners.Of(p) != faction)) return false; // already so, or another colony's order
        if (!exists && kind != DesignationKind.Sow && OrdersOf(faction) >= Balance.MaxOrders)
        {
            LimitReached = true; // a colony's orders are capped (a box over the whole map hung the game)
            return false;
        }
        if (!exists) _counts[faction] = OrdersOf(faction) + 1;
        _owners.Set(p, faction);

        _orders[p] = kind;
        _claims.Remove(p); // the job changed; whoever held it will notice and drop it
        Version++;
        return true;
    }

    // ---- Priority (DF-style: 1 first ... 5 last) ---------------------------------------------------------------

    public const int DefaultPriority = 3, MaxPriority = 5;

    /// <summary>An order's priority: 1 is done first, 5 last (3 unless set). Colonists take the most urgent order they can
    /// reach, then the highest, then the nearest.</summary>
    public int PriorityOf(TilePos p) => _priority.TryGetValue(p, out byte v) ? v : DefaultPriority;

    /// <summary>Sets the priority of every order (of this colony) in the inclusive box; returns how many it covered.</summary>
    public int SetPriorityBox(TilePos a, TilePos b, int priority, int faction = Owners.Any)
    {
        priority = Math.Clamp(priority, 1, MaxPriority);
        int n = ForBox(a, b, p =>
        {
            if (!_orders.ContainsKey(p) || !_owners.Matches(p, faction)) return false;
            if (priority == DefaultPriority) _priority.Remove(p);
            else _priority[p] = (byte)priority;
            return true;
        });
        if (n > 0) Version++;
        return n;
    }

    public bool Cancel(TilePos p, int faction = Owners.Any)
    {
        if (!_orders.ContainsKey(p) || !_owners.Matches(p, faction)) return false;
        _orders.Remove(p);
        _priority.Remove(p);
        Uncount(p);
        _owners.Clear(p);
        _claims.Remove(p);
        Version++;
        return true;
    }

    /// <summary>Designates every solid tile in the inclusive box between two corners, top level first, until the
    /// colony reaches <see cref="Balance.MaxOrders"/>; returns how many were added.</summary>
    public int DesignateBox(TilePos a, TilePos b, DesignationKind kind = DesignationKind.Dig, int faction = 0)
    {
        LimitReached = false;
        return ForBox(a, b, p => Designate(p, kind, faction), stopAtLimit: true);
    }

    /// <summary>What a box command adds to its message when the cap cut it short.</summary>
    public string LimitNote => LimitReached ? $" (order limit reached: a colony has at most {Balance.MaxOrders} orders)" : "";

    /// <summary>Cancels every designation in the inclusive box between two corners; returns how many were removed.</summary>
    public int CancelBox(TilePos a, TilePos b, int faction = Owners.Any) => ForBox(a, b, p => Cancel(p, faction));

    /// <summary>Orders that target a plant (standing in an open cell) rather than a tile.</summary>
    public static bool IsPlantOrder(DesignationKind kind) => kind is DesignationKind.Chop or DesignationKind.Gather or DesignationKind.Sow;

    /// <summary>Says whether a cell is an empty farm plot (validates <see cref="DesignationKind.Sow"/>; set by <see cref="ColonySim"/>).</summary>
    public Func<TilePos, bool>? CanSow { get; set; }

    private static bool Accepts(DesignationKind kind, Nature.Plant plant) => kind == DesignationKind.Chop
        ? Nature.PlantInfo.IsTree(plant.Kind)
        : Nature.PlantInfo.IsGatherable(plant);

    /// <summary>
    /// Plant orders (<see cref="DesignationKind.Chop"/>, <see cref="DesignationKind.Gather"/>) on every plant they
    /// apply to whose column lies in the rectangle between two corners, at any level: this work always happens
    /// on the surface, so the area is picked in 2D. Returns how many plants were marked.
    /// </summary>
    public int DesignatePlantArea(DesignationKind kind, int x0, int y0, int x1, int y1, int faction = 0) =>
        ForPlantsInArea(x0, y0, x1, y1, plant => Accepts(kind, plant) && !_world.IsUnseen(plant.Position), p => Designate(p, kind, faction)); // not in caverns no one has found

    /// <summary>Cancels orders of one plant kind in the columns of a rectangle (any level). Returns how many.</summary>
    public int CancelPlantArea(DesignationKind kind, int x0, int y0, int x1, int y1, int faction = Owners.Any) =>
        ForPlantsInArea(x0, y0, x1, y1, _ => true, p => _orders.TryGetValue(p, out var k) && k == kind && Cancel(p, faction));

    private int ForPlantsInArea(int x0, int y0, int x1, int y1, Func<Nature.Plant, bool> filter, Func<TilePos, bool> action)
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
                if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1 && filter(plant)) hits.Add(p);
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
    public int DesignateStairs(TilePos a, TilePos b, int faction = 0)
    {
        int top = Math.Max(a.Z, b.Z), bottom = Math.Max(1, Math.Min(a.Z, b.Z));
        int n = 0;
        for (int z = top; z >= bottom; z--)
        {
            var kind = top == bottom ? DesignationKind.UpDownStair
                : z == top ? DesignationKind.DownStair
                : z == bottom ? DesignationKind.UpStair
                : DesignationKind.UpDownStair;
            n += DesignateBox(a with { Z = z }, b with { Z = z }, kind, faction);
        }
        return n;
    }

    /// <summary>
    /// The tile a designation leaves behind. Carving a stair into an existing stair adds directions, DF-style:
    /// an up stair designated onto a down stair makes an up/down stair.
    /// </summary>
    public static TileType Result(DesignationKind kind, TileType current)
    {
        if (IsPlantOrder(kind)) return current;
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
        _priority.Remove(p);
        Uncount(p);
        _claims.Remove(p);
        _owners.Clear(p);
        if (!IsPlantOrder(kind)) _world.Set(p, Result(kind, _world.Get(p)));
        Version++;
        return true;
    }

    /// <summary>How many simulation ticks of work a designation takes (natural rock as slate, at level zero).</summary>
    public static int WorkTicks(DesignationKind kind, TileType tile) => WorkTicks(kind, tile, Procedural.Rock.Slate, 0);

    /// <summary>
    /// Ticks of work for a designation on a tile: by material (natural rock by its kind), and below level zero longer the
    /// deeper it is (<see cref="Balance.DigDepthHardening"/> per level).
    /// </summary>
    public static int WorkTicks(DesignationKind kind, TileType tile, Procedural.Rock rock, int belowZero)
    {
        int dig = tile switch
        {
            TileType.Grass or TileType.Soil or TileType.Sand => Balance.DigLoose,
            TileType.Clay => Balance.DigClay,
            TileType.Stone => Procedural.Geology.DigTicks(rock),
            TileType.Aetherite => Balance.DigAetherite,
            _ when TileInfo.IsGem(tile) => Balance.DigGem,
            _ when TileInfo.IsMineral(tile) => Balance.DigOre,
            _ when TileInfo.IsStair(tile) => Balance.DigStair, // already carved: only reshaping or clearing it
            _ => Balance.DigOther,
        };
        if (belowZero > 0) dig = (int)(dig * (1f + Balance.DigDepthHardening * belowZero));
        return IsStair(kind) ? dig + Balance.StairCarveTicks : dig;
    }

    /// <summary>Runs over the box clipped to the world, top level first (so a capped box takes the top levels).</summary>
    private int ForBox(TilePos a, TilePos b, Func<TilePos, bool> action, bool stopAtLimit = false)
    {
        int n = 0;
        int x0 = Math.Max(0, Math.Min(a.X, b.X)), x1 = Math.Min(_world.Width - 1, Math.Max(a.X, b.X));
        int y0 = Math.Max(0, Math.Min(a.Y, b.Y)), y1 = Math.Min(_world.Depth - 1, Math.Max(a.Y, b.Y));
        for (int z = Math.Min(_world.Height - 1, Math.Max(a.Z, b.Z)); z >= Math.Max(0, Math.Min(a.Z, b.Z)); z--)
        for (int y = y0; y <= y1 && !(stopAtLimit && LimitReached); y++)
        for (int x = x0; x <= x1; x++)
            if (action(new TilePos(x, y, z))) n++;
        return n;
    }

    // ---- Snapshot -----------------------------------------------------------------------------------------

    internal void Settle()
    {
        Net.SnapIO.Compact(_orders);
        Net.SnapIO.Compact(_claims);
        Net.SnapIO.Compact(_priority);
        _owners.Compact();
    }

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(_orders.Count);
        foreach (var (p, k) in _orders) { Net.SnapIO.Write(w, p); w.Write((byte)k); }
        w.Write(_claims.Count);
        foreach (var (p, id) in _claims) { Net.SnapIO.Write(w, p); w.Write(id); }
        _owners.SnapSave(w);
        w.Write(Version);
        w.Write(_priority.Count);
        foreach (var (p, v) in _priority) { Net.SnapIO.Write(w, p); w.Write(v); }
    }

    internal void SnapLoad(BinaryReader r)
    {
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) _orders[Net.SnapIO.ReadPos(r)] = (DesignationKind)r.ReadByte();
        int nc = r.ReadInt32();
        for (int i = 0; i < nc; i++) _claims[Net.SnapIO.ReadPos(r)] = r.ReadInt32();
        _owners.SnapLoad(r);
        _counts.Clear();
        foreach (var p in _orders.Keys) _counts[_owners.Of(p)] = OrdersOf(_owners.Of(p)) + 1;
        Version = r.ReadInt32();
        _priority.Clear();
        if (Net.SnapIO.Before(7)) return; // order priorities came with version 7
        int np = r.ReadInt32();
        for (int i = 0; i < np; i++) _priority[Net.SnapIO.ReadPos(r)] = r.ReadByte();
    }
}
