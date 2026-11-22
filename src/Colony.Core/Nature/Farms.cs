using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Nature;

/// <summary>
/// Farm plots: zoned floor cells that grow one crop each (wheat and herbs on grass or soil, glowcaps on any
/// floor under a roof). Farms run themselves: every <see cref="GrowInterval"/> ticks all plots grow at once
/// (one flora rebuild per region, not one per plant), empty plots get a Sow order and fully grown crops a
/// Gather order, which colonists take like any designation. Harvesting removes the crop and leaves its yield;
/// the plot is then sown again. Deterministic: plots are processed in the order they were zoned.
/// </summary>
public sealed class Farms
{
    public const int GrowInterval = 100;

    private readonly VoxelWorld _world;
    private readonly Dictionary<TilePos, PlantKind> _plots = new();
    private readonly List<TilePos> _order = new();
    private readonly Owners _owners = new();

    /// <summary>The faction whose plot this is (its sow and harvest orders are theirs).</summary>
    public int OwnerOf(TilePos p) => _owners.Of(p);

    public Farms(VoxelWorld world) => _world = world;

    public int Version { get; private set; }
    public int Count => _plots.Count;
    public IReadOnlyList<TilePos> Cells => _order;
    public bool Contains(TilePos p) => _plots.ContainsKey(p);
    public bool TryGet(TilePos p, out PlantKind crop) => _plots.TryGetValue(p, out crop);

    /// <summary>Cells farms may not claim (rooms, stockpiles). Set by <see cref="ColonySim"/>.</summary>
    public Func<TilePos, bool>? IsBlocked { get; set; }

    /// <summary>Crops a plot can grow.</summary>
    public static readonly PlantKind[] Crops = { PlantKind.Wheat, PlantKind.Herbs, PlantKind.Glowcap };

    /// <summary>Zones the floor in the box between two ground tiles for <paramref name="crop"/>. Returns how many cells.</summary>
    public int AddFloorBox(PlantKind crop, TilePos a, TilePos b, int faction = 0)
    {
        if (!PlantInfo.IsCrop(crop)) throw new ArgumentException($"{crop} is not a crop", nameof(crop));
        int n = 0;
        ForBox(a, b, p =>
        {
            var cell = p.Offset(0, 0, 1);
            if (!TileInfo.IsSolid(_world.Get(p)) || _plots.ContainsKey(cell) || IsBlocked?.Invoke(cell) == true) return;
            if (!PlantInfo.CanGrowAt(_world, cell, crop)) return;
            _plots[cell] = crop;
            _owners.Set(cell, faction);
            _order.Add(cell);
            n++;
        });
        if (n > 0) Version++;
        return n;
    }

    /// <summary>Removes plots standing on (or in) the box. Returns how many. Crops already growing stay until harvested or cleared.</summary>
    public int RemoveBox(TilePos a, TilePos b, int faction = Owners.Any)
    {
        int n = 0;
        ForBox(a, b, p =>
        {
            foreach (var c in new[] { p.Offset(0, 0, 1), p })
                if (_plots.ContainsKey(c) && _owners.Matches(c, faction) && _plots.Remove(c))
                {
                    _order.Remove(c);
                    _owners.Clear(c);
                    n++;
                }
        });
        if (n > 0) Version++;
        return n;
    }

    /// <summary>Plants the plot's crop at <paramref name="cell"/> (a Sow order was completed).</summary>
    internal bool Sow(TilePos cell, Flora flora)
    {
        if (!_plots.TryGetValue(cell, out var crop) || flora.TryGet(cell, out _)) return false;
        byte variant = (byte)(Procedural.Noise.Hash(cell.X, cell.Y, cell.Z, 0xFA2) * 255);
        return flora.Add(new Plant(cell, crop, 0, variant));
    }

    /// <summary>Grows every plot's crop and places Sow and Gather orders. Call once per tick.</summary>
    internal void Tick(long tick, Flora flora, Designations orders, Fluids.Water? water = null)
    {
        if (tick % GrowInterval != 0 || _order.Count == 0) return;
        foreach (var cell in _order)
        {
            var crop = _plots[cell];
            if (!flora.TryGet(cell, out var plant))
            {
                if (!orders.Contains(cell) && PlantInfo.CanGrowAt(_world, cell, crop)) orders.Designate(cell, DesignationKind.Sow, _owners.Of(cell));
                continue;
            }
            if (!PlantInfo.IsCrop(plant.Kind)) continue; // a wild plant got there first; leave it
            if (plant.Growth < 255)
            {
                int ticks = PlantInfo.GrowTicks(plant.Kind);
                if (water is not null && Irrigated(water, cell)) ticks = (int)(ticks / Balance.IrrigationSpeed); // irrigated plots grow faster
                int step = GrowthStep(tick / GrowInterval, ticks);
                flora.Update(plant with { Growth = (byte)Math.Min(255, plant.Growth + step) });
            }
            else if (!orders.Contains(cell)) orders.Designate(cell, DesignationKind.Gather, _owners.Of(cell));
        }
    }

    /// <summary>
    /// Growth gained in growth round <paramref name="round"/> by a crop taking <paramref name="growTicks"/>: the exact
    /// share, its fractions spread over rounds (a byte of growth can't hold them), so slow crops keep their true pace.
    /// </summary>
    public static int GrowthStep(long round, int growTicks)
    {
        long per = 255L * GrowInterval;
        return (int)((round + 1) * per / growTicks - round * per / growTicks);
    }

    /// <summary>Water within <see cref="IrrigationRadius"/> tiles, on the plot's level or the one below (a ditch beside it).</summary>
    public static bool Irrigated(Fluids.Water water, TilePos cell)
    {
        for (int dz = -1; dz <= 0; dz++)
        for (int dy = -IrrigationRadius; dy <= IrrigationRadius; dy++)
        for (int dx = -IrrigationRadius; dx <= IrrigationRadius; dx++)
            if (water.MassAt(cell.X + dx, cell.Y + dy, cell.Z + dz) >= Fluids.Water.Full / 4) return true;
        return false;
    }

    public const int IrrigationRadius = 2;

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
        Net.SnapIO.Compact(_plots);
        _owners.Compact();
    }

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(_plots.Count);
        foreach (var (p, crop) in _plots) { Net.SnapIO.Write(w, p); w.Write((int)crop); }
        w.Write(_order.Count);
        foreach (var p in _order) Net.SnapIO.Write(w, p);
        _owners.SnapSave(w);
        w.Write(Version);
    }

    internal void SnapLoad(BinaryReader r)
    {
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) _plots[Net.SnapIO.ReadPos(r)] = (PlantKind)r.ReadInt32();
        int no = r.ReadInt32();
        for (int i = 0; i < no; i++) _order.Add(Net.SnapIO.ReadPos(r));
        _owners.SnapLoad(r);
        Version = r.ReadInt32();
    }
}
