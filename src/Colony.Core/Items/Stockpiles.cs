using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Items;

/// <summary>
/// Cells the player has zoned for storage. Haulers bring loose piles here; a pile in a stockpile cell counts
/// as stored. Cells are walkable floor cells (the open cell above the ground the player marked), grouped into
/// <see cref="Stockpile"/>s with their own settings (what they take, priority, reach, which workshops draw from them).
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

    public bool Add(TilePos cell, int faction = 0, bool depot = false)
    {
        if (_cells.Contains(cell))
        {
            // Re-zoning its own cell: a stockpile becomes a trade depot or back.
            if (_owners.Of(cell) != faction || _depots.Contains(cell) == depot) return false;
            if (depot)
            {
                _depots.Add(cell);
                Unzone(cell);
            }
            else
            {
                _depots.Remove(cell);
                Zone(cell, faction);
            }
            Version++;
            return true;
        }
        if (!Movement.IsStandable(_world, cell) || IsBlocked?.Invoke(cell) == true || !_cells.Add(cell)) return false;
        _owners.Set(cell, faction);
        if (depot) _depots.Add(cell);
        else Zone(cell, faction);
        Version++;
        return true;
    }

    // ---- Stockpiles and their settings ---------------------------------------------------------------------
    // Every zoned cell but a trade depot's belongs to one Stockpile. A box zoned at once is one stockpile (the first cell
    // decides: it joins a stockpile of its colony it touches, else starts a new one); a lone cell joins one it touches.

    private readonly List<Stockpile> _zones = new(); // by id
    private readonly Dictionary<TilePos, Stockpile> _zoneOf = new();
    private int _nextZone = 1;
    private Stockpile? _boxZone; // the stockpile a box being zoned goes into

    /// <summary>The colony's stockpiles, in id order (all colonies' when <paramref name="faction"/> is Any).</summary>
    public IEnumerable<Stockpile> Zones(int faction = Owners.Any) => _zones.Where(z => faction == Owners.Any || z.Faction == faction);

    /// <summary>The stockpile a cell belongs to (null for a trade depot or an unzoned cell).</summary>
    public Stockpile? ZoneAt(TilePos p) => _zoneOf.GetValueOrDefault(p);

    public Stockpile? ZoneById(int id)
    {
        int i = _zones.BinarySearch(new Stockpile(id, 0), ById);
        return i >= 0 ? _zones[i] : null;
    }

    private static readonly Comparer<Stockpile> ById = Comparer<Stockpile>.Create((a, b) => a.Id.CompareTo(b.Id));

    /// <summary>The cells of a stockpile.</summary>
    public IReadOnlyCollection<TilePos> CellsOf(Stockpile zone) => zone.Cells;

    /// <summary>Changes a stockpile's setting (bumps <see cref="Version"/> so haulers look again). False if nothing changed.</summary>
    public bool Configure(Stockpile zone, StockpileOption option, int value, int arg = 0, string text = "")
    {
        if (!zone.Configure(option, value, arg, text)) return false;
        Version++;
        return true;
    }

    private void Zone(TilePos cell, int faction)
    {
        var zone = _boxZone is { } b && b.Faction == faction ? b : Touching(cell, faction) ?? NewZone(faction);
        if (_boxing) _boxZone = zone;
        zone.Cells.Add(cell);
        _zoneOf[cell] = zone;
    }

    private Stockpile NewZone(int faction)
    {
        var zone = new Stockpile(_nextZone++, faction);
        _zones.Add(zone);
        return zone;
    }

    /// <summary>The lowest-id stockpile of the colony with a cell touching this one (8-way, or a level up or down).</summary>
    private Stockpile? Touching(TilePos cell, int faction)
    {
        Stockpile? best = null;
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
            if (_zoneOf.TryGetValue(cell.Offset(dx, dy, dz), out var z) && z.Faction == faction && (best is null || z.Id < best.Id)) best = z;
        return best;
    }

    private void Unzone(TilePos cell)
    {
        if (!_zoneOf.Remove(cell, out var zone)) return;
        zone.Cells.Remove(cell);
        if (zone.Cells.Count == 0) _zones.Remove(zone);
    }

    private bool _boxing;

    // ---- Trade depots ------------------------------------------------------------------------------------
    // A trade depot is a stockpile zone where other colonies' traders deliver what they sold us. Goods there are the
    // owner's (only its colonists use them, only its haulers move them); haulers never store goods there (the room is
    // kept for deliveries) and carry deliveries on to the ordinary stockpiles.
    private readonly HashSet<TilePos> _depots = new();

    public bool IsDepot(TilePos p) => _depots.Contains(p);
    public IReadOnlyCollection<TilePos> DepotCells => _depots;
    /// <summary>Whether a colony has any trade depot.</summary>
    public bool HasDepot(int faction)
    {
        foreach (var p in _depots)
            if (_owners.Of(p) == faction) return true;
        return false;
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
        _depots.Remove(cell);
        Unzone(cell);
        _owners.Clear(cell);
        Version++;
        return true;
    }

    /// <summary>
    /// Zones the floor in the inclusive box between two ground tiles: every solid tile in the box whose cell
    /// above is walkable. Returns how many cells were added.
    /// </summary>
    public int AddFloorBox(TilePos a, TilePos b, int faction = 0, bool depot = false)
    {
        int n = 0;
        _boxing = true; // one box, one stockpile
        _boxZone = null;
        ForBox(a, b, p =>
        {
            if (TileInfo.IsSolid(_world.Get(p)) && Add(p.Offset(0, 0, 1), faction, depot)) n++;
        });
        _boxing = false;
        _boxZone = null;
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
        Net.SnapIO.Compact(_depots);
        foreach (var z in _zones) Net.SnapIO.Compact(z.Cells);
        Net.SnapIO.Compact(_zoneOf);
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
        w.Write(_depots.Count);
        foreach (var p in _depots) Net.SnapIO.Write(w, p);
        w.Write(_nextZone);
        w.Write(_zones.Count);
        foreach (var z in _zones)
        {
            z.SnapSave(w);
            w.Write(z.Cells.Count);
            foreach (var p in z.Cells) Net.SnapIO.Write(w, p);
        }
    }

    internal void SnapLoad(BinaryReader r)
    {
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) _cells.Add(Net.SnapIO.ReadPos(r));
        _owners.SnapLoad(r);
        Version = r.ReadInt32();
        if (Net.SnapIO.Before(3)) { GroupOldCells(); return; }
        int racks = r.ReadInt32();
        for (int i = 0; i < racks; i++) _racks[Net.SnapIO.ReadPos(r)] = new Rack(r.ReadByte(), r.ReadByte());
        RackVersion = r.ReadInt32();
        if (Net.SnapIO.Before(4)) { GroupOldCells(); return; }
        int depots = r.ReadInt32();
        for (int i = 0; i < depots; i++) _depots.Add(Net.SnapIO.ReadPos(r));
        if (Net.SnapIO.Before(13))
        {
            GroupOldCells();
            return;
        }
        _nextZone = r.ReadInt32();
        int zones = r.ReadInt32();
        for (int i = 0; i < zones; i++)
        {
            var z = Stockpile.SnapLoad(r);
            _zones.Add(z);
            int cells = r.ReadInt32();
            for (int k = 0; k < cells; k++)
            {
                var p = Net.SnapIO.ReadPos(r);
                z.Cells.Add(p);
                _zoneOf[p] = z;
            }
        }
    }

    /// <summary>A save from before stockpiles had settings: each connected group of a colony's cells becomes one, with defaults.</summary>
    private void GroupOldCells()
    {
        var order = _cells.Where(c => !_depots.Contains(c)).OrderBy(c => c.Z).ThenBy(c => c.Y).ThenBy(c => c.X).ToList();
        foreach (var start in order)
        {
            if (_zoneOf.ContainsKey(start)) continue;
            int faction = _owners.Of(start);
            var zone = NewZone(faction);
            var queue = new Queue<TilePos>();
            queue.Enqueue(start);
            zone.Cells.Add(start);
            _zoneOf[start] = zone;
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var n = c.Offset(dx, dy, dz);
                    if (!_cells.Contains(n) || _depots.Contains(n) || _zoneOf.ContainsKey(n) || _owners.Of(n) != faction) continue;
                    zone.Cells.Add(n);
                    _zoneOf[n] = zone;
                    queue.Enqueue(n);
                }
            }
        }
    }
}

/// <summary>A built storage rack: how many levels it rises and which way its open front faces (0..3: +x, +y, -x, -y).</summary>
public readonly record struct Rack(byte Levels, byte Facing);
