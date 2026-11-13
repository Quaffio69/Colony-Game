using Colony.Core.World;

namespace Colony.Core.Nature;

/// <summary>
/// Every tree and surface plant in the world, at most one per cell, bucketed into square regions of
/// <see cref="RegionSize"/> columns. Each region has a version counter so views (and later, systems such as
/// forestry or gathering) can process only what changed. Deterministic: contents depend only on the order
/// of operations, never on hashing or timing.
/// </summary>
public sealed class Flora
{
    public const int RegionShift = 7;
    public const int RegionSize = 1 << RegionShift; // 128 columns

    private readonly VoxelWorld _world;
    private readonly List<Plant>[] _regions;
    private readonly int[] _regionVersion;
    private readonly Dictionary<long, int> _index = new(); // cell key -> index within its region's list

    public int RegionsX { get; }
    public int RegionsY { get; }
    public int Count => _index.Count;
    /// <summary>Increments on every change anywhere.</summary>
    public int Version { get; private set; }

    public Flora(VoxelWorld world)
    {
        _world = world;
        RegionsX = (world.Width + RegionSize - 1) >> RegionShift;
        RegionsY = (world.Depth + RegionSize - 1) >> RegionShift;
        _regions = new List<Plant>[RegionsX * RegionsY];
        for (int i = 0; i < _regions.Length; i++) _regions[i] = new List<Plant>();
        _regionVersion = new int[_regions.Length];
    }

    public IReadOnlyList<Plant> InRegion(int rx, int ry) => _regions[ry * RegionsX + rx];
    public int RegionVersion(int rx, int ry) => _regionVersion[ry * RegionsX + rx];

    public IEnumerable<Plant> All
    {
        get
        {
            foreach (var region in _regions)
                foreach (var p in region) yield return p;
        }
    }

    public bool TryGet(TilePos p, out Plant plant)
    {
        if (_world.InBounds(p) && _index.TryGetValue(Key(p), out int i))
        {
            plant = _regions[RegionOf(p)][i];
            return true;
        }
        plant = default;
        return false;
    }

    /// <summary>Adds a plant if its cell is free and it can grow there.</summary>
    public bool Add(Plant plant)
    {
        var p = plant.Position;
        if (!PlantInfo.CanGrowAt(_world, p)) return false;
        long key = Key(p);
        if (_index.ContainsKey(key)) return false;
        int r = RegionOf(p);
        _index[key] = _regions[r].Count;
        _regions[r].Add(plant);
        Changed(r);
        return true;
    }

    public bool Remove(TilePos p, out Plant removed)
    {
        removed = default;
        if (!_world.InBounds(p) || !_index.Remove(Key(p), out int i)) return false;
        int r = RegionOf(p);
        var list = _regions[r];
        removed = list[i];
        int last = list.Count - 1;
        if (i != last)
        {
            list[i] = list[last];
            _index[Key(list[i].Position)] = i;
        }
        list.RemoveAt(last);
        Changed(r);
        return true;
    }

    /// <summary>Removes the plant at <paramref name="p"/> if the ground changed so it can no longer grow there.</summary>
    public bool Validate(TilePos p) => _index.ContainsKey(Key(p)) && !PlantInfo.CanGrowAt(_world, p) && Remove(p, out _);

    /// <summary>Bulk load for world generation: skips per-plant checks (the generator guarantees them).</summary>
    internal void AddGenerated(List<Plant> plants)
    {
        foreach (var plant in plants)
        {
            long key = Key(plant.Position);
            if (_index.ContainsKey(key)) continue;
            int r = RegionOf(plant.Position);
            _index[key] = _regions[r].Count;
            _regions[r].Add(plant);
            _regionVersion[r]++;
        }
        Version++;
    }

    private void Changed(int region)
    {
        _regionVersion[region]++;
        Version++;
    }

    private int RegionOf(TilePos p) => (p.Y >> RegionShift) * RegionsX + (p.X >> RegionShift);
    private long Key(TilePos p) => ((long)p.Z * _world.Depth + p.Y) * _world.Width + p.X;
}
