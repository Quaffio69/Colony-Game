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

    /// <summary>Farm plots (grain, herbs, glowcaps).</summary>
    public Farms Farms { get; }

    /// <summary>Water in the world (rivers, lakes, floods): a compressible cellular fluid.</summary>
    public Fluids.Water Water { get; }
    /// <summary>Water wheels, pumps and power.</summary>
    public Fluids.Waterworks Waterworks { get; } = new();
    /// <summary>Walls, wheels and pumps waiting to be built.</summary>
    public Constructions Constructions { get; }

    /// <summary>Water steps every this many ticks.</summary>
    public const int WaterInterval = 4;
    /// <summary>Whole-world water steps run while the world loads, so rivers and lakes start at their flowing rest.</summary>
    public const int WaterSettleSteps = 400;

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
        Farms = new Farms(world);
        Water = new Fluids.Water(world);
        Constructions = new Constructions(world) { IsOccupied = c => Waterworks.Occupied(c) };
        // A floor cell is storage, factory or farm: never two of them.
        Stockpiles.IsBlocked = c => Rooms.RoomAt(c) is not null || Farms.Contains(c);
        Rooms.IsBlocked = c => Stockpiles.Contains(c) || Farms.Contains(c);
        Farms.IsBlocked = c => Stockpiles.Contains(c) || Rooms.RoomAt(c) is not null;
        Designations.CanSow = c => Farms.TryGet(c, out var crop) && !Flora.TryGet(c, out _) && PlantInfo.CanGrowAt(World, c, crop);
        _caveIns = new CaveIns(world);
        DevHooks.SimCreated?.Invoke(this);
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
        if (kind == DesignationKind.Gather)
        {
            Designations.Complete(p);
            if (Flora.TryGet(p, out var plant) && PlantInfo.IsGatherable(plant) && PlantInfo.Harvest(plant.Kind) is { } crop)
            {
                Items.Drop(p, crop, PlantInfo.HarvestYield(plant));
                // Crops are harvested whole (the plot is sown again); wild plants are cut back and regrow.
                if (PlantInfo.IsCrop(plant.Kind)) Flora.Remove(p, out _);
                else Flora.Update(plant with { Growth = PlantInfo.GatheredGrowth });
            }
            return true;
        }
        if (kind == DesignationKind.Sow)
        {
            Designations.Complete(p);
            Farms.Sow(p, Flora);
            return true;
        }

        var before = World.Get(p);
        if (!Designations.Complete(p)) return false;
        if (MiningYield(p, before) is { } y) Items.Drop(p, y, 1);

        // Whatever stood on this tile lost (or changed) its ground.
        var above = p.Offset(0, 0, 1);
        if (Flora.Validate(above)) CancelPlantOrder(above);
        Items.Resettle(above);
        Rooms.ValidateCell(above); // a room floor dug out (or carved into stairs)
        Rooms.ValidateCell(p);

        if (kind == DesignationKind.Dig) CheckSupport(p);
        return true;
    }

    /// <summary>
    /// A builder finished a structure (its materials are already used up): a wall becomes a solid tile (water in
    /// the cell is pushed out by the next water step, a pile in it moves on top); a wheel or pump starts working.
    /// </summary>
    internal void CompleteConstruction(Blueprint bp)
    {
        if (bp.Removed) return;
        Constructions.Remove(bp);
        var cell = bp.Cell;
        switch (bp.Kind)
        {
            case StructureKind.Wall:
                World.Set(cell, TileType.Wall);
                Flora.Remove(cell, out _);
                Items.Resettle(cell);
                Stockpiles.Remove(cell);
                Rooms.ValidateCell(cell);
                break;
            case StructureKind.WaterWheel:
                Waterworks.AddWheel(Water, cell, bp.Facing);
                break;
            default:
                Waterworks.AddPump(cell, bp.Facing);
                break;
        }
    }

    /// <summary>Takes down the wheel or pump in a cell, leaving its materials behind. False if there is none.</summary>
    public bool RemoveStructure(TilePos cell)
    {
        var thing = Waterworks.Remove(cell);
        if (thing is null) return false;
        var cost = Constructions.Cost(thing is Fluids.Pump ? StructureKind.Pump : StructureKind.WaterWheel);
        Items.Drop(cell, cost.Material, cost.Count);
        return true;
    }

    /// <summary>Simulation ticks of work a designation takes.</summary>
    public int WorkTicks(TilePos target, DesignationKind kind)
    {
        if (kind == DesignationKind.Gather) return 30;
        if (kind == DesignationKind.Sow) return 20;
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
        TileType.Stone when Procedural.Noise.Hash(p.X, p.Y, p.Z, 0x5703E) < StoneYield => ItemType.Stone,
        TileType.Clay => ItemType.Clay,
        TileType.Sand => ItemType.Sand,
        TileType.CopperOre => ItemType.CopperOre,
        TileType.TinOre => ItemType.TinOre,
        TileType.IronOre => ItemType.IronOre,
        TileType.SilverOre => ItemType.SilverOre,
        TileType.GoldOre => ItemType.GoldOre,
        TileType.Quartz => ItemType.Quartz,
        TileType.Sulfur => ItemType.Sulfur,
        TileType.Aetherite => ItemType.Aetherite,
        TileType.Wall => ItemType.Stone,
        _ => null,
    };

    private void CancelPlantOrder(TilePos p)
    {
        if (Designations.TryGet(p, out var k) && Designations.IsPlantOrder(k)) Designations.Cancel(p);
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
            CancelPlantOrder(top);
            riders.Add(plant with { Position = plant.Position.Offset(0, 0, -caveIn.Drop) });
        }
        foreach (var q in landed)
            if (Flora.Remove(q, out _)) CancelPlantOrder(q);
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
    public static ColonySim Generate(WorldGenSettings settings, int colonists = 7, bool settleWaterInBackground = false)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var generated = WorldGenerator.GenerateWithTerrain(settings);
        var sim = new ColonySim(generated.World) { Terrain = generated.Terrain };
        long genMs = clock.ElapsedMilliseconds;

        clock.Restart();
        sim.Water.FillFromTerrain(generated.Terrain);
        var stepping = DevHooks.Stepping; // generation's own settling isn't the caller's step (Wait may inline it)
        DevHooks.Stepping = null;
        // Settling the water only reads the terrain, as do flora placement and connectivity: run it alongside them.
        long waterMs = 0;
        var settle = Task.Run(() =>
        {
            var waterClock = System.Diagnostics.Stopwatch.StartNew();
            sim.Water.Settle(WaterSettleSteps);
            waterMs = waterClock.ElapsedMilliseconds;
        });

        clock.Restart();
        sim.Flora = FloraGenerator.Generate(settings, generated.Terrain, generated.World);
        long floraMs = clock.ElapsedMilliseconds;
        int trees = sim.Flora.All.Count(p => PlantInfo.IsTree(p.Kind));

        // Founders: one Scholar and a couple of Literates lead the party (schools need teachers; see Graduate).
        foreach (var p in sim.FindSpawnPoints(generated.Terrain.SpawnX, generated.Terrain.SpawnY, colonists))
        {
            int n = sim._colonists.Count;
            sim.AddColonist(NameGenerator.Generate(settings.Seed, n), p, education: n == 0 ? 2 : n <= 2 ? 1 : 0);
        }

        clock.Restart();
        sim.Connectivity.Update();
        long connMs = clock.ElapsedMilliseconds;
        if (settleWaterInBackground) sim._waterSettling = settle; // the game draws its first frames meanwhile
        else settle.Wait();
        DevHooks.Stepping = stepping;
        var t = generated.Terrain;
        sim.StartupReport = $"terrain {genMs} ms ({t.RiverCells} river cells, {t.LakeCells} lake cells), " +
                            $"water settling {(settleWaterInBackground ? "in the background" : $"{waterMs} ms in parallel")} ({sim.Water.WetChunks} wet chunks, {sim.Water.Springs.Count} springs), " +
                            $"flora {floraMs} ms ({trees} trees, {sim.Flora.Count - trees} plants), " +
                            $"connectivity {connMs} ms " +
                            $"({sim.Connectivity.RegionCount} regions, {sim.Connectivity.ComponentCount} components)";
        return sim;
    }

    private Task? _waterSettling;

    /// <summary>False while generated water is still settling in the background (see <see cref="Generate"/>).</summary>
    public bool WaterReady => _waterSettling is null || _waterSettling.IsCompleted;

    /// <summary>Blocks until the water has settled. Call before changing the world after a background settle.</summary>
    public void WaitForWater()
    {
        _waterSettling?.Wait();
        _waterSettling = null;
    }

    /// <summary>Human-readable timings from <see cref="Generate"/>.</summary>
    public string StartupReport { get; private set; } = "";

    public Colonist AddColonist(string name, TilePos position, int education = 0, bool automaton = false)
    {
        var c = new Colonist(_colonists.Count + _arriving.Count, name, position, education) { IsAutomaton = automaton };
        if (_stepping) _arriving.Add(c); // joining mid-tick (e.g. an automaton finished): starts next tick
        else _colonists.Add(c);
        return c;
    }

    private readonly List<Colonist> _arriving = new();
    private bool _stepping;

    // ---- Events -----------------------------------------------------------------------------------------

    private readonly List<(long Tick, string Text)> _events = new();
    private const int EventsKept = 12;

    /// <summary>Recent colony events (graduations, arrivals), oldest first.</summary>
    public IReadOnlyList<(long Tick, string Text)> Events => _events;

    internal void Announce(string text)
    {
        _events.Add((Tick, text));
        if (_events.Count > EventsKept) _events.RemoveAt(0);
    }

    // ---- Education --------------------------------------------------------------------------------------

    private readonly int[] _reservedStudents = new int[8];

    /// <summary>The education level an Academy recipe teaches (its output item names a level).</summary>
    public static int Teaches(Recipe recipe) => Catalog.Education(recipe.Output.Name);

    /// <summary>Colonists one level below what <paramref name="recipe"/> teaches, not already booked for a class.</summary>
    public int StudentsAvailable(Recipe recipe)
    {
        int level = Teaches(recipe);
        int n = 0;
        foreach (var c in _colonists)
            if (!c.IsAutomaton && c.Education == level - 1) n++;
        return Math.Max(0, n - _reservedStudents[level]);
    }

    internal void ReserveStudents(Recipe recipe, int units) => _reservedStudents[Teaches(recipe)] += units;

    internal void ReleaseStudents(Recipe recipe, int units)
    {
        int level = Teaches(recipe);
        _reservedStudents[level] = Math.Max(0, _reservedStudents[level] - units);
    }

    /// <summary>A class finished: up to <paramref name="units"/> students (lowest id first) rise to the level taught.</summary>
    internal void Graduate(Recipe recipe, int units)
    {
        ReleaseStudents(recipe, units);
        int level = Teaches(recipe);
        foreach (var c in _colonists)
        {
            if (units == 0) break;
            if (c.IsAutomaton || c.Education != level - 1) continue;
            c.Education = level;
            units--;
            Announce($"{c.Name} graduated from the Academy: now a {Catalog.EducationName(level)}");
        }
    }

    /// <summary>Worker items (automatons) step off the assembly frame and join the colony as unskilled workers.</summary>
    internal void AddWorkers(Recipe recipe, int units, TilePos at)
    {
        for (int i = 0; i < units; i++)
        {
            int n = _colonists.Count(c => c.IsAutomaton) + _arriving.Count(c => c.IsAutomaton) + 1;
            AddColonist($"{recipe.Output.Name} {n}", at, automaton: true);
            Announce($"{recipe.Output.Name} {n} awakens and joins the colony");
        }
    }

    private int _eventRoomsVersion = -1;
    private bool _magisterArrived;

    /// <summary>
    /// Story events driven by what the colony has built. The first Magister can't be taught (only Magisters
    /// teach Magisters), so one arrives once the Academy has an attunement circle to work at.
    /// </summary>
    private void CheckEvents()
    {
        if (_magisterArrived || Rooms.Version == _eventRoomsVersion) return;
        _eventRoomsVersion = Rooms.Version;
        int magister = Catalog.EducationLevels.Count - 1;
        if (_colonists.Any(c => c.Education >= magister))
        {
            _magisterArrived = true;
            return;
        }
        foreach (var room in Rooms.All)
            foreach (var part in room.Parts)
            {
                if (!part.Built || part.Recipe is null || part.Type.Recipes.All(r => r.Kind != ItemKind.Education || Teaches(r) < magister)) continue;
                var at = Movement.IsStandable(World, part.Origin) ? part.Origin : room.Cells.First();
                var name = NameGenerator.Generate(unchecked((int)Tick * 31 + 7), _colonists.Count);
                AddColonist(name, at, education: magister);
                Announce($"A wandering Magister, {name}, is drawn to the {part.Name.ToLowerInvariant()} and joins the colony");
                _magisterArrived = true;
                return;
            }
    }

    public void Step()
    {
        DevHooks.Stepping?.Invoke();
        WaitForWater();
        Tick++;
        _tickNodeStart = Pathfinder.NodesExpanded;
        Farms.Tick(Tick, Flora, Designations, Water);
        if (Tick % WaterInterval == 0)
        {
            Water.Step();
            Waterworks.Tick(Water, World);
        }
        CheckEvents();
        if (_arriving.Count > 0)
        {
            _colonists.AddRange(_arriving);
            _arriving.Clear();
        }

        // Reachability data is only needed by colonists looking for work, so refresh it lazily.
        bool anyIdle = false;
        foreach (var c in _colonists) anyIdle |= c.State == ColonistState.Idle;
        if (anyIdle)
        {
            Connectivity.Update(); // O(1) unless the world changed
            JobBoard.Update(World, Designations, Connectivity); // no-op unless something changed
        }

        _stepping = true;
        try
        {
            foreach (var c in _colonists) c.Tick(this);
        }
        finally
        {
            _stepping = false;
        }
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
