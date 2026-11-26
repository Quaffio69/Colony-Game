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
public sealed partial class ColonySim
{
    public const int TicksPerSecond = 20;

    private readonly List<Colonist> _colonists = new();

    public VoxelWorld World { get; }
    public Designations Designations { get; }
    public Connectivity Connectivity { get; }
    public JobBoard JobBoard { get; } = new();
    public IReadOnlyList<Colonist> Colonists => _colonists;
    public long Tick { get; private set; }

    /// <summary>The machines among the colonists (excavators, power wagons), in colonist order; rebuilt when the roster changes.</summary>
    internal IReadOnlyList<Colonist> Machines
    {
        get
        {
            if (_machinesRoster == _roster) return _machines;
            _machines.Clear();
            foreach (var c in _colonists)
                if (c.IsMachine) _machines.Add(c);
            _machinesRoster = _roster;
            return _machines;
        }
    }
    private readonly List<Colonist> _machines = new();
    private int _roster, _machinesRoster = -1;

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
    /// <summary>Mist off the waterfalls (drawn by the game; lifts the mood of colonists in it).</summary>
    public Fluids.Mist Mist { get; }
    /// <summary>Water wheels, pumps and power.</summary>
    public Fluids.Waterworks Waterworks { get; } = new();
    /// <summary>Conveyor belts and the goods riding them.</summary>
    public Transport.Conveyors Conveyors { get; }
    /// <summary>Walls, wheels and pumps waiting to be built.</summary>
    public Constructions Constructions { get; }
    /// <summary>Grass growing back over dug-out dirt.</summary>
    public Regrowth Regrowth { get; } = new();

    /// <summary>Whether a scaffold lies anywhere (a running count, so colonists don't scan the piles for one).</summary>
    internal bool AnyScaffold => Items.UnitsOf(Colonist.ScaffoldItem) > 0;
    /// <summary>Wild creatures and their nests (giant ants).</summary>
    public Combat.Wildlife Wildlife { get; } = new();
    /// <summary>Deals between colonies (offers, counters, deliveries to trade depots).</summary>
    public Trade.Trades Trades { get; } = new();
    /// <summary>Colonists who died (stats).</summary>
    public int Deaths { get; private set; }

    /// <summary>Water steps every this many ticks.</summary>
    public const int WaterInterval = 4;
    /// <summary>Whole-world water steps run while the world loads, so rivers and lakes start at their flowing rest.</summary>
    public const int WaterSettleSteps = 400;


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
        Mist = new Fluids.Mist(world);
        Conveyors = new Transport.Conveyors(world);
        Constructions = new Constructions(world)
        {
            IsOccupied = c => Waterworks.Occupied(c) || Stockpiles.Racks.ContainsKey(c) || Conveyors.Contains(c), IsStorage = c => Stockpiles.Contains(c),
            Keeps = (c, t) => Conveyors.IsHopper(c) && Conveyors.At(c) is { } at && at.Belt.CarriesItem(t),
        };
        Waterworks.OtherDemand = f => Conveyors.PowerDemand(f);
        Rooms.IsPartBlocked = c => Conveyors.Contains(c);
        Items.CapacityAt = Stockpiles.CapacityAt;
        Stockpiles.RackLost = (c, rack) => Items.Drop(c, ItemType.Plank, Constructions.Cost(StructureKind.Rack, rack.Levels).Count);
        // A floor cell is storage, factory or farm: never two of them.
        Stockpiles.IsBlocked = c => Rooms.RoomAt(c) is not null || Farms.Contains(c) || Conveyors.Contains(c);
        Rooms.IsBlocked = c => Stockpiles.Contains(c) || Farms.Contains(c);
        Farms.IsBlocked = c => Stockpiles.Contains(c) || Rooms.RoomAt(c) is not null || Conveyors.Contains(c);
        Designations.CanSow = c => Farms.TryGet(c, out var crop) && !Flora.TryGet(c, out _) && PlantInfo.CanGrowAt(World, c, crop);
        _caveIns = new CaveIns(world);
        World.ChunkChanged += i => Wildlife.OnChunkChanged(World, i);
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

        if (kind == DesignationKind.Dig)
        {
            Regrowth.Watch(World, p.Offset(0, 0, -1), Tick); // fresh dirt laid bare: grass creeps back over it
            CheckSupport(p);
        }
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
        if (bp.IsCover)
        {
            if (bp.Kind == StructureKind.Roof) World.SetRoof(bp.Cell, bp.Material);
            else World.SetFloor(bp.Cell, bp.Material);
            return;
        }
        if (bp.IsEdge)
        {
            // A door or window cut into a built wall gives back the wall's material.
            byte old = World.Edge(bp.EdgeCell, bp.Side);
            if (EdgeInfo.Kind(old) == EdgeKind.Wall && bp.Kind != StructureKind.EdgeWall)
            {
                var (mat, n) = Constructions.Cost(StructureKind.EdgeWall, EdgeInfo.Material(old));
                Items.Drop(bp.Cell, mat, n);
            }
            World.SetEdge(bp.EdgeCell, bp.Side, EdgeInfo.Make(Constructions.EdgeKindOf(bp.Kind), bp.Material));
            return;
        }
        var cell = bp.Target;
        switch (bp.Kind)
        {
            case StructureKind.Deck:
                World.Set(cell, TileType.Deck);
                World.SetFloor(cell, bp.Material);
                break;
            case StructureKind.Stair:
                World.Set(cell, (TileType)bp.Material);
                Flora.Remove(cell, out _);
                break;
            case StructureKind.Wall:
                World.Set(cell, TileType.Wall);
                Flora.Remove(cell, out _);
                Items.Resettle(cell);
                Stockpiles.Remove(cell);
                Rooms.ValidateCell(cell);
                break;
            case StructureKind.Conveyor:
                Conveyors.TileBuilt(cell);
                break;
            case StructureKind.WaterWheel:
                Waterworks.AddWheel(Water, cell, bp.Facing, bp.Faction);
                break;
            case StructureKind.Rack:
                if (!Stockpiles.AddRack(cell, bp.Material, bp.Facing)) Items.Drop(cell, bp.Cost.Material, bp.Cost.Count); // its zone went meanwhile
                break;
            default:
                Waterworks.AddPump(cell, bp.Facing, bp.Faction);
                break;
        }
    }

    /// <summary>Takes down the wheel or pump in a cell, leaving its materials behind. False if there is none.</summary>
    public bool RemoveStructure(TilePos cell)
    {
        if (World.Get(cell) == TileType.Deck)
        {
            // A deck comes down with its floor; its boards are left on whatever is below it.
            var (mat, n) = Constructions.Cost(StructureKind.Deck, World.FloorAt(cell));
            World.Set(cell, TileType.Air);
            Items.Drop(cell, mat, n);
            return true;
        }
        if (Stockpiles.RemoveRack(cell)) return true; // its planks are dropped (RackLost)
        var thing = Waterworks.Remove(cell);
        if (thing is null) return false;
        var cost = Constructions.Cost(thing is Fluids.Pump ? StructureKind.Pump : StructureKind.WaterWheel);
        Items.Drop(cell, cost.Material, cost.Count);
        return true;
    }

    /// <summary>Simulation ticks of work a designation takes.</summary>
    public int WorkTicks(TilePos target, DesignationKind kind)
    {
        if (kind == DesignationKind.Gather) return Balance.GatherTicks;
        if (kind == DesignationKind.Sow) return Balance.SowTicks;
        if (kind != DesignationKind.Chop)
            return Designations.WorkTicks(kind, World.Get(target), World.RockAt(target), World.ZeroLevel - target.Z);
        if (!Flora.TryGet(target, out var tree)) return 1;
        return Balance.ChopTicks + (int)(PlantInfo.MatureHeight(tree.Kind) * tree.Growth / 255f * Balance.ChopTicksPerLevel);
    }

    /// <summary>Logs a felled tree leaves: bigger and older trees give more.</summary>
    public static int LogYield(Plant tree) =>
        1 + (int)MathF.Round(PlantInfo.MatureHeight(tree.Kind) * tree.Growth / 255f * Balance.LogsPerLevel);

    /// <summary>Item left by removing the rock at <paramref name="p"/> (digging it out or carving it into stairs).</summary>
    public static ItemType? MiningYield(TilePos p, TileType removed) => removed switch
    {
        TileType.Stone when Procedural.Noise.Hash(p.X, p.Y, p.Z, 0x5703E) < Balance.StoneYield => ItemType.Stone,
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
        _ when TileInfo.IsGem(removed) => ItemType.RoughGem(removed),
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
    public static ColonySim Generate(WorldGenSettings settings, int colonists = 7, bool settleWaterInBackground = false, int factions = 1,
        bool wildlife = true)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var generated = WorldGenerator.GenerateWithTerrain(settings);
        generated.World.Seal(); // from here on, changed chunks are what a snapshot stores
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

        // Founders: one Scholar and a couple of Literates lead each party (schools need teachers; see Graduate).
        // The first colony lands at the terrain's spawn; others land on dry ground spaced round it (multiplayer).
        for (int f = 0; f < Math.Max(1, factions); f++)
        {
            var faction = f == 0 ? sim._factions[0] : sim.AddFaction(FactionNames[f % FactionNames.Length]);
            var (cx, cy) = f == 0 ? (generated.Terrain.SpawnX, generated.Terrain.SpawnY) : sim.OtherLanding(generated.Terrain, f, factions);
            faction.Landing = new TilePos(cx, cy, 0);
            int first = sim._colonists.Count;
            foreach (var p in sim.FindSpawnPoints(cx, cy, colonists, dry: f > 0))
            {
                int n = sim._colonists.Count - first;
                sim.AddColonist(NameGenerator.Generate(settings.Seed + f * 7919, n), p, education: n == 0 ? 2 : n <= 2 ? 1 : 0, faction: f);
            }
            if (sim._colonists.Count > first)
            {
                var landing = sim._colonists[^1].Position;
                // With several colonies, each one's provisions start in a little stockpile of its own (loose goods are
                // anyone's, and the neighbours would count them as theirs too).
                if (factions > 1)
                    sim.Stockpiles.AddFloorBox(landing.Offset(-1, -1, -1), landing.Offset(1, 1, -1), f);
                sim.Items.Drop(landing, ItemType.Berries, StartingBerries);
                sim.Items.Drop(landing, Catalog.Item("bread"), StartingBread);
                sim.Items.Drop(landing, ItemType.Log, StartingLogs);
            }
        }

        if (wildlife) sim.Wildlife.Populate(sim, settings.Seed);

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

    public Colonist AddColonist(string name, TilePos position, int education = 0, bool automaton = false, int faction = 0)
    {
        var c = new Colonist(ColonistsEver++, name, position, education) { IsAutomaton = automaton, Faction = faction };
        if (_stepping) _arriving.Add(c); // joining mid-tick (e.g. an automaton finished): starts next tick
        else { _colonists.Add(c); _roster++; }
        return c;
    }

    /// <summary>Colonists who ever joined (ids are never reused, even after someone leaves).</summary>
    public int ColonistsEver { get; private set; }

    /// <summary>Hunger, the living standard and settlers (<see cref="Society"/>). Off for sandbox tests that don't feed anyone.</summary>
    public bool NeedsEnabled { get; set; } = true;

    /// <summary>Every colony in the world (index = faction id); the first is the local player's in single player.</summary>
    public IReadOnlyList<Faction> Factions => _factions;
    private readonly List<Faction> _factions = [new Faction(0, "The Colony")];

    /// <summary>A new colony (a player or an NPC) landing at a spot.</summary>
    public Faction AddFaction(string name, TilePos? landing = null)
    {
        var f = new Faction(_factions.Count, name) { Landing = landing };
        _factions.Add(f);
        return f;
    }

    /// <summary>The first colony's society (shorthand for <c>Factions[0].Society</c>).</summary>
    public Society Society => _factions[0].Society;

    /// <summary>Homes, their furnishing policies, fixtures and residents.</summary>
    public Housing Housing => _factions[0].Housing;
    /// <summary>Player commands: every change a player makes goes through these (see <see cref="Commands.Command"/>).</summary>
    public Commands.CommandQueue Commands => _commands ??= new Commands.CommandQueue(this);
    private Commands.CommandQueue? _commands;

    private int _families;

    /// <summary>A new family id (households of the married; never a colonist id).</summary>
    internal int NewFamily() => 1_000_000 + ++_families;

    /// <summary>A child born to a couple: in their household, at home with them, unschooled, too young to work.</summary>
    internal Colonist AddChild(Colonist mother, Colonist father)
    {
        var at = mother.Home is { } home ? Housing.SleepCell(home, mother) : mother.Position;
        var child = AddColonist(NameGenerator.Generate(unchecked((int)Tick * 37 + 5), ColonistsEver), at, faction: mother.Faction);
        child.IsChild = true;
        child.BornTick = Tick;
        child.Household = mother.Household;
        return child;
    }

    /// <summary>A colonist leaves for good: drops what it carries and lets go of every claim.</summary>
    internal void Depart(Colonist c)
    {
        c.Leave(this);
        c.DropGear(this); // the colony's arms stay with the colony
        _colonists.Remove(c);
        _roster++;
    }

    private readonly List<(Colonist Colonist, string Message)> _dying = new();

    /// <summary>A colonist will die at the end of this tick (never mid-iteration).</summary>
    internal void QueueDeath(Colonist c, string message)
    {
        if (c.Dead || _dying.Any(d => d.Colonist == c)) return;
        _dying.Add((c, message));
    }

    /// <summary>Dies: drops its load and gear where it lies, is mourned, and is gone (its family stays a family).</summary>
    private void Bury()
    {
        foreach (var (c, message) in _dying)
        {
            if (!_colonists.Contains(c)) continue;
            c.Leave(this);
            c.DropGear(this);
            c.Dead = true;
            if (c.Spouse is { } s && s.Spouse == c) s.Spouse = null;
            c.Spouse = null;
            _colonists.Remove(c);
            _roster++;
            Deaths++;
            Announce(message, c.Faction);
        }
        _dying.Clear();
    }

    /// <summary>Colony-wide orders for a colonist (drafting, squad moves): null if fine, else why not.</summary>
    internal string? CanCommand(Colonist c, int player) =>
        c.Faction != player ? $"{c.Name} belongs to another colony" : null;

    /// <summary>Where newcomers arrive: standable ground around the landing place (or the first colonist).</summary>
    internal List<TilePos> ArrivalSpots(int count, int faction = 0)
    {
        if (_factions[faction].Landing is { } land) return FindSpawnPoints(land.X, land.Y, count);
        if (faction == 0 && Terrain is { } t) return FindSpawnPoints(t.SpawnX, t.SpawnY, count);
        if (_colonists.FirstOrDefault(c => c.Faction == faction) is { } member) return FindSpawnPoints(member.Position.X, member.Position.Y, count);
        return [];
    }

    /// <summary>Food the founders bring along, about two weeks for seven (dropped beside the landing place).</summary>
    public const int StartingBerries = 90, StartingBread = 48;
    /// <summary>Timber the founders bring: enough for the first workshop stations (a saw horse and a joinery bench) and a scaffold.</summary>
    public const int StartingLogs = 20;

    private readonly List<Colonist> _arriving = new();
    private bool _stepping;

    // ---- Events -----------------------------------------------------------------------------------------

    private readonly List<SimEvent> _events = new();
    private const int EventsKept = 400;

    /// <summary>Recent colony events (graduations, arrivals), oldest first.</summary>
    public IReadOnlyList<SimEvent> Events => _events;

    internal void Announce(string text, int faction = -1)
    {
        _events.Add(new SimEvent(Tick, text, faction));
        if (_events.Count > EventsKept) _events.RemoveAt(0);
    }

    // ---- Education --------------------------------------------------------------------------------------

    private readonly Dictionary<(int Faction, int Level), int> _reservedStudents = new();

    /// <summary>The education level an Academy recipe teaches (its output item names a level).</summary>
    public static int Teaches(Recipe recipe) => Catalog.Education(recipe.Output.Name);

    /// <summary>Colonists one level below what <paramref name="recipe"/> teaches, not already booked for a class.</summary>
    public int StudentsAvailable(Recipe recipe, int faction = 0)
    {
        int level = Teaches(recipe);
        int n = 0;
        foreach (var c in _colonists)
            if (!c.IsAutomaton && c.Faction == faction && c.Education == level - 1) n++;
        return Math.Max(0, n - _reservedStudents.GetValueOrDefault((faction, level)));
    }

    internal void ReserveStudents(Recipe recipe, int units, int faction = 0) =>
        _reservedStudents[(faction, Teaches(recipe))] = _reservedStudents.GetValueOrDefault((faction, Teaches(recipe))) + units;

    internal void ReleaseStudents(Recipe recipe, int units, int faction = 0)
    {
        int level = Teaches(recipe);
        _reservedStudents[(faction, level)] = Math.Max(0, _reservedStudents.GetValueOrDefault((faction, level)) - units);
    }

    /// <summary>A class finished: up to <paramref name="units"/> students (lowest id first) rise to the level taught.</summary>
    internal void Graduate(Recipe recipe, int units, int faction = 0)
    {
        ReleaseStudents(recipe, units, faction);
        int level = Teaches(recipe);
        foreach (var c in _colonists)
        {
            if (units == 0) break;
            if (c.IsAutomaton || c.Faction != faction || c.Education != level - 1) continue;
            c.Education = level;
            units--;
            Announce($"{c.Name} graduated from the Academy: now a {Catalog.EducationName(level)}", faction);
        }
    }

    /// <summary>Worker items (automatons) step off the assembly frame and join the colony as unskilled workers.</summary>
    internal void AddWorkers(Recipe recipe, int units, TilePos at, int faction = 0)
    {
        for (int i = 0; i < units; i++)
        {
            int n = _colonists.Count(c => c.IsAutomaton) + _arriving.Count(c => c.IsAutomaton) + 1;
            AddColonist($"{recipe.Output.Name} {n}", at, automaton: true, faction: faction);
            Announce($"{recipe.Output.Name} {n} awakens and joins the colony", faction);
        }
    }

    /// <summary>Machines (excavator, power wagon) roll off the assembly frame fuelled, parked for a colonist to drive.</summary>
    internal void AddMachines(Recipe recipe, int units, TilePos at, int faction = 0)
    {
        var kind = Agents.MachineInfo.Of(recipe.Output.Id);
        if (kind == Agents.MachineKind.None) throw new InvalidOperationException($"'{recipe.Output.Id}' is no machine");
        for (int i = 0; i < units; i++) AddMachine(kind, at, faction);
    }

    /// <summary>Adds a machine of a kind to a colony (named "Excavator 2" ...), full of fuel.</summary>
    /// <summary>Whether a machine other than <paramref name="except"/> stands on <paramref name="cell"/> (arrivals this tick included).</summary>
    internal bool MachineStandsOn(TilePos cell, Colonist? except)
    {
        foreach (var m in _colonists) if (m.IsMachine && m != except && m.Position == cell) return true;
        foreach (var m in _arriving) if (m.IsMachine && m != except && m.Position == cell) return true;
        return false;
    }

    /// <summary>
    /// The nearest standable tile to <paramref name="at"/> no other machine stands on (heavy machinery never shares a
    /// tile): ring by ring on its level, then a level up and down, in the same component; <paramref name="at"/> itself
    /// when there is none within 8 tiles.
    /// </summary>
    internal TilePos FreeMachineCell(TilePos at, Colonist? except)
    {
        int component = Connectivity.ComponentOf(at);
        for (int r = 0; r <= 8; r++)
        foreach (int dz in new[] { 0, 1, -1 })
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            var c = at.Offset(dx, dy, dz);
            if (!World.InBounds(c) || !Navigation.Movement.IsStandable(World, c) || MachineStandsOn(c, except)) continue;
            if (component >= 0 && c != at && Connectivity.ComponentOf(c) != component) continue;
            return c;
        }
        return at;
    }

    public Colonist AddMachine(Agents.MachineKind kind, TilePos at, int faction = 0)
    {
        int n = _colonists.Count(c => c.Machine == kind) + _arriving.Count(c => c.Machine == kind) + 1;
        string name = $"{Agents.MachineInfo.Name(kind)} {n}";
        var c = AddColonist(name, FreeMachineCell(at, null), automaton: true, faction: faction); // one machine to a tile
        c.Machine = kind;
        c.SetUpMachine();
        Announce($"{name} rolls out of the workshop, fired up and ready", faction);
        return c;
    }

    private int _eventRoomsVersion = -1;

    /// <summary>
    /// Story events driven by what a colony has built. The first Magister can't be taught (only Magisters
    /// teach Magisters), so one arrives once a colony's Academy has an attunement circle to work at.
    /// </summary>
    private void CheckEvents()
    {
        if (Rooms.Version == _eventRoomsVersion) return;
        _eventRoomsVersion = Rooms.Version;
        int magister = Catalog.EducationLevels.Count - 1;
        foreach (var faction in _factions)
        {
            if (faction.MagisterArrived) continue;
            if (_colonists.Any(c => c.Faction == faction.Id && c.Education >= magister))
            {
                faction.MagisterArrived = true;
                continue;
            }
            foreach (var room in Rooms.All)
            {
                if (room.Faction != faction.Id || faction.MagisterArrived) continue;
                foreach (var part in room.Parts)
                {
                    if (!part.Built || part.Recipe is null || part.Type.Recipes.All(r => r.Kind != ItemKind.Education || Teaches(r) < magister)) continue;
                    var at = Movement.IsStandable(World, part.Origin) ? part.Origin : room.Cells.First();
                    var name = NameGenerator.Generate(unchecked((int)Tick * 31 + 7), _colonists.Count);
                    AddColonist(name, at, education: magister, faction: faction.Id);
                    Announce($"A wandering Magister, {name}, is drawn to the {part.Name.ToLowerInvariant()} and joins the colony", faction.Id);
                    faction.MagisterArrived = true;
                    break;
                }
            }
        }
    }

    public void Step()
    {
        DevHooks.Stepping?.Invoke();
        WaitForWater();
        _commands?.Flush(); // anything players queued applies before this tick
        Tick++;
        _tickNodeStart = Pathfinder.NodesExpanded;
        if (_waterworksDue) RunWaterworks(); // the step begun at the end of the last tick lands first
        DevStep(); // dev mode: what was just planned stands at once
        Farms.Tick(Tick, Flora, Designations, Water);
        Conveyors.Tick(this); // before colonists act: belts deliver, and haulers see what is on its way
        Regrowth.Tick(this);
        CheckEvents();
        if (_arriving.Count > 0)
        {
            _colonists.AddRange(_arriving);
            _roster++;
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
        Wildlife.Tick(this); // creatures act after colonists (blows struck this tick land before they move)
        if (_dying.Count > 0) Bury();
        foreach (var f in _factions)
        {
            f.Housing.Tick(this); // follows home rooms, wears fixtures, houses people
            if (NeedsEnabled) f.Society.Tick(this);
        }

        // Water steps last in the tick, after everything that changes the world. With AsyncWater it computes on
        // worker threads until the next tick begins (the game draws a frame meanwhile); the outcome is the same.
        if (Tick % WaterInterval == 0)
        {
            Water.BeginStep(AsyncWater);
            _waterworksDue = true;
            if (!AsyncWater) RunWaterworks();
        }
    }

    /// <summary>
    /// Let the water step run in the background between ticks (the game sets this; tests and tools step
    /// synchronously so they can read the water right after <see cref="Step"/>). Either way the results are identical.
    /// </summary>
    public bool AsyncWater { get; set; }

    private bool _waterworksDue;

    private void RunWaterworks()
    {
        Water.Join();
        Waterworks.Tick(Water, World, _factions.Count);
        Mist.Sample(this); // reads the falls of the step just landed
        _waterworksDue = false;
    }

    /// <summary>Standable surface tiles nearest to (x, y), spiralling outwards, one per column.</summary>
    /// <summary>Names for the colonies after the first.</summary>
    public static readonly string[] FactionNames = ["The Colony", "Ironvale", "Stonebrook", "Ashford", "Highmere", "Duskhollow", "Fernwatch", "Coldharbor"];

    /// <summary>
    /// Where colony <paramref name="f"/> of <paramref name="count"/> lands: evenly round the first colony's spawn, a quarter of
    /// the map away (at least 24 tiles), nudged to the nearest dry land.
    /// </summary>
    private (int X, int Y) OtherLanding(Procedural.Terrain terrain, int f, int count)
    {
        float r = Math.Max(24, Math.Min(World.Width, World.Depth) / 4f);
        double angle = 2 * Math.PI * (f - 1) / Math.Max(1, count - 1);
        int x = Math.Clamp((int)(terrain.SpawnX + r * Math.Cos(angle)), 4, World.Width - 5);
        int y = Math.Clamp((int)(terrain.SpawnY + r * Math.Sin(angle)), 4, World.Depth - 5);
        return FindSpawnPoints(x, y, 1, dry: true) is [var p] ? (p.X, p.Y) : (x, y);
    }

    private List<TilePos> FindSpawnPoints(int cx, int cy, int count, bool dry = false)
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
            if (Movement.IsStandable(World, p) && (!dry || Water.MassAt(p) < Fluids.Water.MinMass)) result.Add(p);
        }
        return result;
    }
}

public sealed partial class ColonySim
{
    // ---- Snapshot (see Net.Snapshot) ----------------------------------------------------------------------

    /// <summary>
    /// Brings the game to a state a snapshot holds completely: water finished, queued commands applied, every
    /// colonist puts down what it was doing (claims released, loads dropped), containers compacted (their order no
    /// longer depends on history) and the derived maps (connectivity, job board) rebuilt from scratch. A loaded
    /// snapshot ends up in exactly this state, so both go on alike.
    /// </summary>
    public void Settle()
    {
        WaitForWater();
        Water.Join();
        Commands.Flush();
        foreach (var c in _colonists) c.Settle(this);
        Water.Join();
        Designations.Settle();
        Stockpiles.Settle();
        Farms.Settle();
        Items.Settle();
        Rooms.Settle();
        Constructions.Settle();
        Flora.Settle();
        Water.Settle();
        foreach (var f in _factions) f.Housing.Settle();
        Wildlife.Settle(this);
        Trades.Settle();
        Conveyors.Settle();
        _reservedStudents.Clear();
        RebuildDerived();
    }

    private void RebuildDerived()
    {
        Connectivity.Reset();
        JobBoard.Reset();
        Connectivity.Update();
        JobBoard.Update(World, Designations, Connectivity);
    }

    internal void SnapSave(BinaryWriter w)
    {
        Net.SnapIO.Section(w, "world"); World.SnapSave(w);
        Net.SnapIO.Section(w, "flora"); Flora.SnapSave(w);
        Net.SnapIO.Section(w, "items"); Items.SnapSave(w);
        Net.SnapIO.Section(w, "orders"); Designations.SnapSave(w);
        Net.SnapIO.Section(w, "stockpiles"); Stockpiles.SnapSave(w);
        Net.SnapIO.Section(w, "farms"); Farms.SnapSave(w);
        Net.SnapIO.Section(w, "rooms"); Rooms.SnapSave(w);
        Net.SnapIO.Section(w, "blueprints"); Constructions.SnapSave(w);
        Net.SnapIO.Section(w, "waterworks"); Waterworks.SnapSave(w);
        Net.SnapIO.Section(w, "water"); Water.SnapSave(w);
        Net.SnapIO.Section(w, "colonists");
        w.Write(_colonists.Count);
        foreach (var c in _colonists) c.SnapSave(w);
        Net.SnapIO.Section(w, "factions");
        w.Write(_factions.Count);
        foreach (var f in _factions)
        {
            w.Write(f.Name);
            w.Write(f.Landing.HasValue);
            if (f.Landing is { } l) Net.SnapIO.Write(w, l);
            w.Write(f.MagisterArrived);
            w.Write(f.Player);
            w.Write(f.PlayerKey);
            f.Society.SnapSave(w);
            f.Housing.SnapSave(w);
        }
        Net.SnapIO.Section(w, "wildlife"); Wildlife.SnapSave(w);
        Net.SnapIO.Section(w, "regrowth"); Regrowth.SnapSave(w);
        Net.SnapIO.Section(w, "trades"); Trades.SnapSave(w);
        Net.SnapIO.Section(w, "mist"); Mist.SnapSave(w);
        Net.SnapIO.Section(w, "conveyors"); Conveyors.SnapSave(w);
        Net.SnapIO.Section(w, "sim");
        w.Write(Tick); w.Write(PathSearches); w.Write(CaveInCount); w.Write(ItemsProduced); w.Write(ColonistsEver);
        w.Write(_families); w.Write(_eventRoomsVersion); w.Write(_waterworksDue); w.Write(NeedsEnabled); w.Write(Deaths);
        w.Write(_recentCaveIns.Count);
        foreach (var c in _recentCaveIns) { Net.SnapIO.Write(w, c.Origin); w.Write(c.Tiles); w.Write(c.Drop); w.Write(c.Tick); }
        w.Write(_events.Count);
        foreach (var e in _events) { w.Write(e.Tick); w.Write(e.Text); w.Write(e.Faction); }
        w.Write(DevMode); w.Write(DevInstantOrders);
        Net.SnapIO.Section(w, "end");
    }

    /// <summary>A sim from a snapshot body, over the regenerated <paramref name="world"/> (see <see cref="Net.Snapshot.Load"/>).</summary>
    internal static ColonySim SnapLoad(BinaryReader r, VoxelWorld world, Terrain? terrain)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var times = new List<string>();
        void Lap(string what) { times.Add($"{what} {clock.ElapsedMilliseconds}"); clock.Restart(); }
        Net.SnapIO.Section(r, "world"); world.SnapLoad(r);
        Lap("world");
        var sim = new ColonySim(world) { Terrain = terrain };
        Lap("sim");
        Net.SnapIO.Section(r, "flora"); sim.Flora.SnapLoad(r);
        Lap("flora");
        Net.SnapIO.Section(r, "items"); sim.Items.SnapLoad(r);
        Net.SnapIO.Section(r, "orders"); sim.Designations.SnapLoad(r);
        Net.SnapIO.Section(r, "stockpiles"); sim.Stockpiles.SnapLoad(r);
        Net.SnapIO.Section(r, "farms"); sim.Farms.SnapLoad(r);
        Net.SnapIO.Section(r, "rooms"); sim.Rooms.SnapLoad(r);
        Net.SnapIO.Section(r, "blueprints"); sim.Constructions.SnapLoad(r);
        Net.SnapIO.Section(r, "waterworks"); sim.Waterworks.SnapLoad(r);
        Lap("items to waterworks");
        Net.SnapIO.Section(r, "water"); sim.Water.SnapLoad(r, terrain);
        Lap("water");
        Net.SnapIO.Section(r, "colonists");
        int nc = r.ReadInt32();
        var spouses = new List<(Colonist, int)>();
        var byId = new Dictionary<int, Colonist>();
        for (int i = 0; i < nc; i++)
        {
            var (c, spouse) = Colonist.SnapLoad(r);
            sim._colonists.Add(c);
            sim._roster++;
            byId[c.Id] = c;
            spouses.Add((c, spouse));
        }
        foreach (var (c, spouse) in spouses) c.Spouse = spouse >= 0 ? byId[spouse] : null;
        foreach (var c in sim._colonists) c.LinkVehicle(byId); // drivers back in their machines
        Net.SnapIO.Section(r, "factions");
        int nf = r.ReadInt32();
        for (int i = 0; i < nf; i++)
        {
            string name = r.ReadString();
            TilePos? landing = r.ReadBoolean() ? Net.SnapIO.ReadPos(r) : null;
            var f = i == 0 ? sim._factions[0] : sim.AddFaction(name);
            f.Name = name;
            f.Landing = landing;
            f.MagisterArrived = r.ReadBoolean();
            if (!Net.SnapIO.Before(14)) { f.Player = r.ReadString(); f.PlayerKey = r.ReadString(); }
            f.Society.SnapLoad(r);
            f.Housing.SnapLoad(r, sim.Rooms, byId);
        }
        Net.SnapIO.Section(r, "wildlife"); sim.Wildlife.SnapLoad(r);
        if (!Net.SnapIO.Before(3)) { Net.SnapIO.Section(r, "regrowth"); sim.Regrowth.SnapLoad(r); }
        if (!Net.SnapIO.Before(4)) { Net.SnapIO.Section(r, "trades"); sim.Trades.SnapLoad(r); }
        if (!Net.SnapIO.Before(11)) { Net.SnapIO.Section(r, "mist"); sim.Mist.SnapLoad(r); }
        if (!Net.SnapIO.Before(15)) { Net.SnapIO.Section(r, "conveyors"); sim.Conveyors.SnapLoad(r); }
        Net.SnapIO.Section(r, "sim");
        sim.Tick = r.ReadInt64(); sim.PathSearches = r.ReadInt64(); sim.CaveInCount = r.ReadInt32(); sim.ItemsProduced = r.ReadInt64();
        sim.ColonistsEver = r.ReadInt32(); sim._families = r.ReadInt32(); sim._eventRoomsVersion = r.ReadInt32();
        sim._waterworksDue = r.ReadBoolean(); sim.NeedsEnabled = r.ReadBoolean(); sim.Deaths = r.ReadInt32();
        int ncv = r.ReadInt32();
        for (int i = 0; i < ncv; i++) sim._recentCaveIns.Add(new CaveIn(Net.SnapIO.ReadPos(r), r.ReadInt32(), r.ReadInt32(), r.ReadInt64()));
        int ne = r.ReadInt32();
        for (int i = 0; i < ne; i++) sim._events.Add(new SimEvent(r.ReadInt64(), r.ReadString(), r.ReadInt32()));
        if (!Net.SnapIO.Before(10)) { sim.DevMode = r.ReadBoolean(); sim.DevInstantOrders = r.ReadBoolean(); }
        Net.SnapIO.Section(r, "end");
        Lap("colonists to end");
        sim.RebuildDerived();
        Lap("connectivity + jobs");
        Net.Snapshot.LastTimings = string.Join(", ", times) + " ms";
        return sim;
    }
}

/// <summary>Something worth telling the player: when, what, and which colony it concerns (-1: everyone).</summary>
public readonly record struct SimEvent(long Tick, string Text, int Faction = -1);
