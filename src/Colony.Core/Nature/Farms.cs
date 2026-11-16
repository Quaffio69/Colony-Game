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
    public int AddFloorBox(PlantKind crop, TilePos a, TilePos b)
    {
        if (!PlantInfo.IsCrop(crop)) throw new ArgumentException($"{crop} is not a crop", nameof(crop));
        int n = 0;
        ForBox(a, b, p =>
        {
            var cell = p.Offset(0, 0, 1);
            if (!TileInfo.IsSolid(_world.Get(p)) || _plots.ContainsKey(cell) || IsBlocked?.Invoke(cell) == true) return;
            if (!PlantInfo.CanGrowAt(_world, cell, crop)) return;
            _plots[cell] = crop;
            _order.Add(cell);
            n++;
        });
        if (n > 0) Version++;
        return n;
    }

    /// <summary>Removes plots standing on (or in) the box. Returns how many. Crops already growing stay until harvested or cleared.</summary>
    public int RemoveBox(TilePos a, TilePos b)
    {
        int n = 0;
        ForBox(a, b, p =>
        {
            foreach (var c in new[] { p.Offset(0, 0, 1), p })
                if (_plots.Remove(c))
                {
                    _order.Remove(c);
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
    internal void Tick(long tick, Flora flora, Designations orders)
    {
        if (tick % GrowInterval != 0 || _order.Count == 0) return;
        foreach (var cell in _order)
        {
            var crop = _plots[cell];
            if (!flora.TryGet(cell, out var plant))
            {
                if (!orders.Contains(cell) && PlantInfo.CanGrowAt(_world, cell, crop)) orders.Designate(cell, DesignationKind.Sow);
                continue;
            }
            if (!PlantInfo.IsCrop(plant.Kind)) continue; // a wild plant got there first; leave it
            if (plant.Growth < 255)
            {
                int step = (int)MathF.Ceiling(255f * GrowInterval / PlantInfo.GrowTicks(plant.Kind));
                flora.Update(plant with { Growth = (byte)Math.Min(255, plant.Growth + step) });
            }
            else if (!orders.Contains(cell)) orders.Designate(cell, DesignationKind.Gather);
        }
    }

    private static void ForBox(TilePos a, TilePos b, Action<TilePos> action)
    {
        for (int z = Math.Min(a.Z, b.Z); z <= Math.Max(a.Z, b.Z); z++)
        for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
        for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
            action(new TilePos(x, y, z));
    }
}
