using Colony.Core.Agents;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Nature;
using Colony.Core.Navigation;
using Colony.Core.Orders;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core;

/// <summary>
/// Root of the simulation state. The presentation layer holds one of these, sends it commands, and calls
/// <see cref="Step"/> at a fixed rate of <see cref="TicksPerSecond"/>. Same seed + same commands at the
/// same ticks gives the same outcome.
/// </summary>
public sealed class ColonySim
{
    public const int TicksPerSecond = 20;

    private readonly List<Colonist> _colonists = new();

    public VoxelWorld World { get; }
    public Designations Designations { get; }
    public Connectivity Connectivity { get; }
    public JobBoard JobBoard { get; } = new();
    public IReadOnlyList<Colonist> Colonists => _colonists;
    public long Tick { get; private set; }

    /// <summary>
    /// Pathfinding nodes colonists may expand per tick before further searches wait for the next tick.
    /// Deterministic (counts nodes, not time), so it never changes outcomes between runs.
    /// </summary>
    public const int PathNodeBudgetPerTick = 6_000;

    private long _tickNodeStart;

    internal bool PathBudgetExhausted => Pathfinder.NodesExpanded - _tickNodeStart >= PathNodeBudgetPerTick;

    /// <summary>Path searches run by colonists so far (diagnostics and tests).</summary>
    public long PathSearches { get; internal set; }

    private readonly CaveIns _caveIns;
    private readonly List<CaveIn> _recentCaveIns = new();
    private const int RecentCaveInsKept = 16;

    /// <summary>The most recent cave-ins, oldest first (for notifications).</summary>
    public IReadOnlyList<CaveIn> RecentCaveIns => _recentCaveIns;
    public int CaveInCount { get; private set; }

    /// <summary>Trees and surface plants.</summary>
    public Flora Flora
    {
        get => _flora;
        private set
        {
            _flora = value;
            Designations.Flora = value;
        }
    }
    private Flora _flora = null!;

    /// <summary>Item piles lying in the world (stored or loose).</summary>
    public ItemPiles Items { get; }
    public Stockpiles Stockpiles { get; }
    /// <summary>Products made by factories so far (stats).</summary>
    public long ItemsProduced { get; internal set; }

    /// <summary>Factory rooms and their parts.</summary>
    public Rooms Rooms { get; }

    /// <summary>Share of dug stone tiles that leave a usable stone (the rest is rubble).</summary>
    public const float StoneYield = 0.5f;

    public ColonySim(VoxelWorld world)
    {
        World = world;
        Designations = new Designations(world);
        Connectivity = new Connectivity(world);
        Flora = new Flora(world);
        Items = new ItemPiles(world);
        Stockpiles = new Stockpiles(world);
        Rooms = new Rooms(world);
        // A floor cell is either storage or factory, never both.
        Stockpiles.IsBlocked = c => Rooms.RoomAt(c) is not null;
        Rooms.IsBlocked = Stockpiles.Contains;
        _caveIns = new CaveIns(world);
    }

    /// <summary>
    /// Carries out a designation (dig, carve, or chop), leaves its yield, keeps plants and item piles on solid
    /// ground, then lets any mass it left unsupported cave in. Everything that changes the world on a
    /// colonist's (or the debug tool's) behalf should come through here.
    /// </summary>
    public bool CompleteDesignation(TilePos p)
    {
        if (!Designations.TryGet(p, out var kind)) return false;
        if (kind == DesignationKind.Chop)
        {
            Designations.Complete(p);
            if (Flora.Remove(p, out var tree)) Items.Drop(p, ItemType.Log, LogYield(tree));
            return true;
        }

        var before = World.Get(p);
        if (!Designations.Complete(p)) return false;
        if (MiningYield(p, before) is { } y) Items.Drop(p, y, 1);

        // Whatever stood on this tile lost (or changed) its ground.
        var above = p.Offset(0, 0, 1);
        if (Flora.Validate(above)) CancelChop(above);
        Items.Resettle(above);
        Rooms.ValidateCell(above); // a room floor dug out (or carved into stairs)
        Rooms.ValidateCell(p);

        if (kind == DesignationKind.Dig) CheckSupport(p);
        return true;
    }

    /// <summary>Simulation ticks of work a designation takes.</summary>
    public int WorkTicks(TilePos target, DesignationKind kind)
    {
        if (kind != DesignationKind.Chop) return Designations.WorkTicks(kind, World.Get(target));
        if (!Flora.TryGet(target, out var tree)) return 1;
        return 40 + (int)(PlantInfo.MatureHeight(tree.Kind) * tree.Growth / 255f * 14f);
    }

    /// <summary>Logs a felled tree leaves: bigger and older trees give more.</summary>
    public static int LogYield(Plant tree) =>
        1 + (int)MathF.Round(PlantInfo.MatureHeight(tree.Kind) * tree.Growth / 255f * 0.6f);

    /// <summary>Item left by removing the rock at <paramref name="p"/> (digging it out or carving it into stairs).</summary>
    public static ItemType? MiningYield(TilePos p, TileType removed) => removed switch
    {
        TileType.Ore => ItemType.Ore,
        TileType.Stone when Procedural.Noise.Hash(p.X, p.Y, p.Z, 0x5703E) < StoneYield => ItemType.Stone,
        _ => null,
    };

    private void CancelChop(TilePos p)
    {
        if (Designations.TryGet(p, out var k) && k == DesignationKind.Chop) Designations.Cancel(p);
    }

    /// <summary>Call after removing a solid tile by any other means (tests, future tools).</summary>
    public void CheckSupport(TilePos removed) => _caveIns.AfterRemoval(removed, Tick, OnCaveIn);

    private void OnCaveIn(CaveIn caveIn, IReadOnlyList<TilePos> landed)
    {
        // Orders on tiles that fell no longer point at the rock they were placed on.
        foreach (var q in landed) Designations.Cancel(q.Offset(0, 0, caveIn.Drop));

        // Plants growing on top of the mass ride it down; plants where it lands are crushed.
        var riders = new List<Plant>();
        foreach (var q in landed)
        {
            var top = q.Offset(0, 0, caveIn.Drop + 1);
            if (!Flora.Remove(top, out var plant)) continue;
            CancelChop(top);
            riders.Add(plant with { Position = plant.Position.Offset(0, 0, -caveIn.Drop) });
        }
        foreach (var q in landed)
            if (Flora.Remove(q, out _)) CancelChop(q);
        foreach (var plant in riders) Flora.Add(plant);

        // Item piles: buried ones are pushed up out of the rock, ones left floating fall to the new floor.
        foreach (var q in landed)
        {
            Items.Resettle(q);
            Items.Resettle(q.Offset(0, 0, caveIn.Drop + 1));
            Rooms.ValidateCell(q); // buried under the fallen rock
            Rooms.ValidateCell(q.Offset(0, 0, caveIn.Drop + 1)); // its floor fell away
        }

        CaveInCount++;
        _recentCaveIns.Add(caveIn);
        if (_recentCaveIns.Count > RecentCaveInsKept) _recentCaveIns.RemoveAt(0);
        // Colonists caught underneath or riding the mass are handled by their own gravity next tick.
    }

    /// <summary>
    /// Surface and hydrology data the world was generated from (rivers, lakes, intended water depths);
    /// null for hand-built worlds.
    /// </summary>
    public Terrain? Terrain { get; private set; }

    /// <summary>Generates a world and drops a starting party at the terrain's suggested spawn.</summary>
    public static ColonySim Generate(WorldGenSettings settings, int colonists = 7)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var generated = WorldGenerator.GenerateWithTerrain(settings);
        var sim = new ColonySim(generated.World) { Terrain = generated.Terrain };
        long genMs = clock.ElapsedMilliseconds;

        clock.Restart();
        sim.Flora = FloraGenerator.Generate(settings, generated.Terrain, generated.World);
        long floraMs = clock.ElapsedMilliseconds;
        int trees = sim.Flora.All.Count(p => PlantInfo.IsTree(p.Kind));

        foreach (var p in sim.FindSpawnPoints(generated.Terrain.SpawnX, generated.Terrain.SpawnY, colonists))
            sim.AddColonist(NameGenerator.Generate(settings.Seed, sim._colonists.Count), p);

        clock.Restart();
        sim.Connectivity.Update();
        var t = generated.Terrain;
        sim.StartupReport = $"terrain {genMs} ms ({t.RiverCells} river cells, {t.LakeCells} lake cells), " +
                            $"flora {floraMs} ms ({trees} trees, {sim.Flora.Count - trees} plants), " +
                            $"connectivity {clock.ElapsedMilliseconds} ms " +
                            $"({sim.Connectivity.RegionCount} regions, {sim.Connectivity.ComponentCount} components)";
        return sim;
    }

    /// <summary>Human-readable timings from <see cref="Generate"/>.</summary>
    public string StartupReport { get; private set; } = "";

    public Colonist AddColonist(string name, TilePos position)
    {
        var c = new Colonist(_colonists.Count, name, position);
        _colonists.Add(c);
        return c;
    }

    public void Step()
    {
        Tick++;
        _tickNodeStart = Pathfinder.NodesExpanded;

        // Reachability data is only needed by colonists looking for work, so refresh it lazily.
        bool anyIdle = false;
        foreach (var c in _colonists) anyIdle |= c.State == ColonistState.Idle;
        if (anyIdle)
        {
            Connectivity.Update(); // O(1) unless the world changed
            JobBoard.Update(World, Designations, Connectivity); // no-op unless something changed
        }

        foreach (var c in _colonists) c.Tick(this);
    }

    /// <summary>Standable surface tiles nearest to (x, y), spiralling outwards, one per column.</summary>
    private List<TilePos> FindSpawnPoints(int cx, int cy, int count)
    {
        var result = new List<TilePos>();
        for (int r = 0; result.Count < count && r < Math.Max(World.Width, World.Depth); r++)
        for (int dy = -r; dy <= r && result.Count < count; dy++)
        for (int dx = -r; dx <= r && result.Count < count; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // ring only
            int x = cx + dx, y = cy + dy;
            if (!World.InBounds(x, y, 0)) continue;
            var p = new TilePos(x, y, World.SurfaceLevel(x, y) + 1);
            if (Movement.IsStandable(World, p)) result.Add(p);
        }
        return result;
    }
}
