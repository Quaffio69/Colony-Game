using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Items;

/// <summary>
/// Cells the player has zoned for storage. Haulers bring loose piles here; a pile in a stockpile cell counts
/// as stored. Cells are walkable floor cells (the open cell above the ground the player marked). Accepts every
/// item type for now; filters and room-like grouping come with the factory builder.
/// </summary>
public sealed class Stockpiles
{
    private readonly VoxelWorld _world;
    private readonly HashSet<TilePos> _cells = new();
    private readonly Owners _owners = new();

    /// <summary>The faction whose stockpile this cell is.</summary>
    public int OwnerOf(TilePos p) => _owners.Of(p);

    public Stockpiles(VoxelWorld world) => _world = world;

    public int Version { get; private set; }
    public int Count => _cells.Count;
    public IReadOnlyCollection<TilePos> Cells => _cells;
    public bool Contains(TilePos p) => _cells.Contains(p);

    /// <summary>Cells stockpiles may not claim (e.g. factory rooms). Set by <see cref="ColonySim"/>.</summary>
    public Func<TilePos, bool>? IsBlocked { get; set; }

    public bool Add(TilePos cell, int faction = 0)
    {
        if (!Movement.IsStandable(_world, cell) || IsBlocked?.Invoke(cell) == true || !_cells.Add(cell)) return false;
        _owners.Set(cell, faction);
        Version++;
        return true;
    }

    // ---- Storage racks --------------------------------------------------------------------------------
    // Warehouse shelving built on a stockpile cell (a construction: see Orders.Constructions): it rises up to
    // MaxRackLevels levels into the open space above its floor and multiplies what the cell holds. Racks don't block
    // movement (agents squeeze past, as with room parts); a sliding ladder is drawn on tall ones.

    /// <summary>Levels a rack may stand (each holds <see cref="ShelvesPerLevel"/> stacks).</summary>
    public const int MaxRackLevels = 3, ShelvesPerLevel = 2;
    private readonly Dictionary<TilePos, Rack> _racks = new();
    public IReadOnlyDictionary<TilePos, Rack> Racks => _racks;
    /// <summary>Bumps when racks are built or taken down (for views).</summary>
    public int RackVersion { get; private set; }
    /// <summary>A rack was taken down (its zone removed, or walled over): the sim refunds its materials here.</summary>
    public Action<TilePos, Rack>? RackLost { get; set; }

    /// <summary>Stacks a rack of this many levels holds (the floor stack plus its shelves).</summary>
    public static int Capacity(int levels) => 1 + ShelvesPerLevel * levels;
    /// <summary>How many stacks the cell holds (1 without a rack).</summary>
    public int CapacityAt(TilePos p) => _racks.TryGetValue(p, out var r) ? Capacity(r.Levels) : 1;

    /// <summary>Levels a rack built here would rise: open cells from this one up, at most <see cref="MaxRackLevels"/>.</summary>
    public static int RackLevelsAt(VoxelWorld world, TilePos cell)
    {
        int n = 0;
        while (n < MaxRackLevels && world.InBounds(cell.Offset(0, 0, n)) && !TileInfo.IsSolid(world.Get(cell.Offset(0, 0, n)))) n++;
        return n;
    }

    internal bool AddRack(TilePos cell, int levels, int facing)
    {
        if (!_cells.Contains(cell) || _racks.ContainsKey(cell)) return false;
        _racks[cell] = new Rack((byte)Math.Clamp(levels, 1, MaxRackLevels), (byte)(facing & 3));
        RackVersion++;
        Version++;
        return true;
    }

    /// <summary>Takes the rack in a cell down (its materials are refunded through <see cref="RackLost"/>). False if there is none.</summary>
    public bool RemoveRack(TilePos cell)
    {
        if (!_racks.Remove(cell, out var rack)) return false;
        RackVersion++;
        Version++;
        RackLost?.Invoke(cell, rack);
        return true;
    }

    public bool Remove(TilePos cell, int faction = Owners.Any)
    {
        if (!_cells.Contains(cell) || !_owners.Matches(cell, faction)) return false;
        RemoveRack(cell);
        _cells.Remove(cell);
        _owners.Clear(cell);
        Version++;
        return true;
    }

    /// <summary>
    /// Zones the floor in the inclusive box between two ground tiles: every solid tile in the box whose cell
    /// above is walkable. Returns how many cells were added.
    /// </summary>
    public int AddFloorBox(TilePos a, TilePos b, int faction = 0)
    {
        int n = 0;
        ForBox(a, b, p =>
        {
            if (TileInfo.IsSolid(_world.Get(p)) && Add(p.Offset(0, 0, 1), faction)) n++;
        });
        return n;
    }

    /// <summary>Removes zoned cells standing on (or in) the box. Returns how many were removed.</summary>
    public int RemoveBox(TilePos a, TilePos b, int faction = Owners.Any)
    {
        int n = 0;
        ForBox(a, b, p =>
        {
            if (Remove(p.Offset(0, 0, 1), faction)) n++;
            if (Remove(p, faction)) n++;
        });
        return n;
    }

    private static void ForBox(TilePos a, TilePos b, Action<TilePos> action)
    {
        for (int z = Math.Min(a.Z, b.Z); z <= Math.Max(a.Z, b.Z); z++)
        for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
        for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
            action(new TilePos(x, y, z));
    }

    // ---- Snapshot -----------------------------------------------------------------------------------------

    internal void Settle()
    {
        Net.SnapIO.Compact(_cells);
        _owners.Compact();
        Net.SnapIO.Compact(_racks);
    }

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(_cells.Count);
        foreach (var p in _cells) Net.SnapIO.Write(w, p);
        _owners.SnapSave(w);
        w.Write(Version);
        w.Write(_racks.Count);
        foreach (var (p, rack) in _racks) { Net.SnapIO.Write(w, p); w.Write(rack.Levels); w.Write(rack.Facing); }
        w.Write(RackVersion);
    }

    internal void SnapLoad(BinaryReader r)
    {
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) _cells.Add(Net.SnapIO.ReadPos(r));
        _owners.SnapLoad(r);
        Version = r.ReadInt32();
        if (Net.SnapIO.Before(3)) return;
        int racks = r.ReadInt32();
        for (int i = 0; i < racks; i++) _racks[Net.SnapIO.ReadPos(r)] = new Rack(r.ReadByte(), r.ReadByte());
        RackVersion = r.ReadInt32();
    }
}

/// <summary>A built storage rack: how many levels it rises and which way its open front faces (0..3: +x, +y, -x, -y).</summary>
public readonly record struct Rack(byte Levels, byte Facing);
