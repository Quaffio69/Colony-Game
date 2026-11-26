using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Navigation;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Agents;

public enum ColonistState { Idle, Moving, Working }

/// <summary>What a colonist is doing beyond walking: a designation, one leg of a haul trip, factory work, or a meal.</summary>
public enum ColonistTask { None, Designation, HaulPickup, HaulDeliver, CraftFetch, CraftWork, CraftStore, Build, Eat, Sleep, FurnishPickup, FurnishDeliver, Equip, Fight, Flee, Rally, ScaffoldFetch, TradePickup, TradeDeliver, Board, Stoke }

/// <summary>Broad kinds of work a colonist may prefer (see <see cref="Colonist.Labors"/>); each covers some <see cref="Work"/> types.</summary>
public enum Labor { Designations, Hauling, Industry }

/// <summary>
/// Work types a colonist can be set to (see <see cref="Colonist.Priority"/>), in the column order of the work grid.
/// Mining = dig and stairs; Woodcutting = felling; Farming = sowing and harvesting plots and gathering wild plants;
/// Construction = building blueprints (parts, walls, floors, roofs, wheels); Hauling = stockpiles, factory crates,
/// building materials and home fixtures; Industry = running factory machines (and the Academy); Trading = carrying
/// what the colony sold to the buyer's trade depot.
/// </summary>
public enum Work : byte { Mining, Woodcutting, Farming, Construction, Hauling, Industry, Trading }

/// <summary>
/// A colony member. When idle it looks for work in its own connectivity component (so unreachable work never
/// costs a search): designations from the <see cref="JobBoard"/> (dig, carve, chop), or haul trips that carry
/// loose item piles to stockpiles (<see cref="Hauling"/>). Some colonists prefer hauling, the rest prefer
/// designations, until proper job priorities exist. Every reservation it takes is released on completion or
/// <see cref="Abandon"/>, and anything it carries is dropped rather than lost.
/// </summary>
public sealed partial class Colonist
{
    /// <summary>Ticks to walk one flat tile (scaled by each move's cost).</summary>
    public const int TicksPerFlatStep = 7;
    /// <summary>Jobs tried (path searches) per idle tick before giving up until something changes.</summary>
    public const int CandidatesPerSearch = 3;
    /// <summary>Ticks to pick up or put down a load.</summary>
    public const int HandleTicks = 6;

    private readonly Queue<TilePos> _path = new();
    // A long trip is walked in legs (see PlanPath): where the whole trip ends, set while the path is only a leg.
    private (HashSet<TilePos> Goals, TilePos Near)? _trip, _plannedTrip;
    private float _stepTicksTotal, _stepTicksDone;
    // Versions seen by the last fruitless search; while none change, an idle colonist doesn't search again.
    private int _searchedDesignations = -1, _searchedConnectivity = -1, _searchedItems = -1, _searchedStockpiles = -1;
    private int _retryDelay;
    private bool _deferred; // the shared path budget ran out mid-search: resume next tick
    // Targets whose path search failed despite sharing a component (node budget hit); cleared on world change.
    private readonly HashSet<TilePos> _pathFailed = new();
    private int _pathFailedConnectivity = -1;
    private HaulPlan _haul;
    /// <summary>A trader's trip: a deal line's goods from our stores to the buyer's depot.</summary>
    private (Trade.TradeDeal Deal, Trade.TradeLine Line, TilePos Source, TilePos Destination, int Amount) _trade;
    private CraftPlan _craft;
    private BuildPlan _build;

    /// <summary>Putting up a blueprint from the materials reserved on its footprint.</summary>
    private readonly record struct BuildPlan(Part? Part, Blueprint? Site, List<(TilePos Cell, int Amount)> Materials)
    {
        public string Name => Part?.Name ?? Constructions.Name(Site!.Kind);
        public string Where => Part is { } p ? $" in {p.Room.Name}" : "";
        public bool Removed => Part?.Removed ?? Site!.Removed;
        public BuildCost Cost => Part?.Cost ?? Site!.Cost;
        public int Ticks => Part is { } p ? PartInfo.BuildTicks(p.Type, p.Size) : Constructions.BuildTicks(Site!);
        public bool InReach(TilePos at) => Part is { } p ? p.Cells.Any(c => Colonist.InReach(at, c)) : Colonist.InReach(at, Site!.Kind == StructureKind.Conveyor ? Site.Cell : Site!.Target);
        public void ReleaseBuilder(int id)
        {
            if (Part is { } p && p.BuilderId == id) p.BuilderId = -1;
            if (Site is { } s && s.BuilderId == id) s.BuilderId = -1;
        }
    }

    /// <summary>One production cycle: input crates (one pickup per crate) -> machine -> output crate (none for education or workers).</summary>
    private readonly record struct CraftPlan(Part Machine, Batch Batch, List<(TilePos Cell, ItemType Item, int Count)> Pickups, TilePos? OutputCell);
    private int _fetchIndex; // next pickup of the craft plan
    private readonly List<(ItemType Item, int Count)> _carried = new(); // inputs in hand for the machine

    public int Id { get; }
    public string Name { get; }
    public TilePos Position { get; private set; }
    public ColonistState State { get; private set; }
    public ColonistTask Task { get; private set; }

    /// <summary>Tile currently being stepped towards, for smooth rendering; null when not mid-step.</summary>
    public TilePos? NextStep { get; private set; }
    /// <summary>0..1 progress of the current step.</summary>
    public float StepProgress => NextStep is null || _stepTicksTotal <= 0 ? 0f : _stepTicksDone / _stepTicksTotal;

    /// <summary>The tile being worked or walked to (designation target, or haul source/destination).</summary>
    public TilePos? JobTarget { get; private set; }
    public int WorkDone { get; private set; }
    public int WorkRequired { get; private set; }

    /// <summary>Load in hand (count 0 = nothing).</summary>
    public ItemType CarryingType { get; private set; }
    public int CarryingCount { get; private set; }

    /// <summary>
    /// Broad work kinds in preference order: its <see cref="PreferredWork"/> first, then the others. Breaks ties between
    /// work types set to the same <see cref="Priority"/>.
    /// </summary>
    public Labor[] Labors => PreferredWork switch
    {
        Labor.Designations => [Labor.Designations, Labor.Hauling, Labor.Industry],
        Labor.Industry => [Labor.Industry, Labor.Designations, Labor.Hauling],
        _ => [Labor.Hauling, Labor.Industry, Labor.Designations],
    };

    /// <summary>
    /// Index into <see cref="Catalog.EducationLevels"/> (0 = Unskilled). A colonist can run recipes at or below
    /// it; the Academy raises it one level at a time.
    /// </summary>
    public int Education { get; internal set; }
    /// <summary>Built, not born: a tireless unskilled worker that can't be taught.</summary>
    public bool IsAutomaton { get; init; }
    public string EducationName => IsMachine ? MachineInfo.Name(Machine) : IsAutomaton ? "Automaton" : Catalog.EducationName(Education);

    public Colonist(int id, string name, TilePos position, int education = 0)
    {
        Id = id;
        Name = name;
        Position = position;
        Education = education;
        Satiety = Needs.FullSatiety - id * 977 % (Needs.Day / 2); // staggered, so the colony doesn't all eat at once
        Rest = Needs.FullRest - id * 1531 % (Needs.Day / 2); // ...or sleep at once
        Household = id;
        FavoriteFood = Pick(Catalog.Items.Where(i => i.Nutrition > 0).Select(i => i.Type).ToList(), id, 1);
        LikedMaterial = Pick(Materials, id, 2);
        LikedArt = Pick(Catalog.Fixtures["decor"].Where(i => i.Def.FixtureTier >= 2).ToList(), id, 3);
        PreferredWork = (id % 3) switch { 0 => Labor.Designations, 1 => Labor.Industry, _ => Labor.Hauling };
        for (int w = 0; w < WorkTypes; w++) _priority[w] = LaborOf((Work)w) == PreferredWork ? (byte)2 : (byte)3;
    }

    // ---- Work priorities ----------------------------------------------------------------------------------
    // RimWorld/DF-style: each work type is off (0) or 1 (first) to 4 (last). An idle colonist looks for work of
    // priority 1, then 2...; work types of the same priority are tried in its Labors order, and the designation
    // kinds among them (dig, chop, farm) are searched together, nearest first. New colonists get 2 for the kind
    // of work they prefer and 3 for the rest. Children don't work whatever is set.

    public const int WorkTypes = 7;
    public const int LowestPriority = 4;
    public static readonly string[] WorkNames = ["Mining", "Woodcutting", "Farming", "Construction", "Hauling", "Industry", "Trading"];
    private readonly byte[] _priority = new byte[WorkTypes];

    /// <summary>0 = never does it; 1 = does it first ... 4 = last.</summary>
    public int Priority(Work work) => _priority[(int)work];

    /// <summary>Sets a work type's priority (clamped to 0..4). Takes effect at its next search for work; the job in hand is finished.</summary>
    public void SetPriority(Work work, int priority)
    {
        _priority[(int)work] = (byte)Math.Clamp(priority, 0, LowestPriority);
        if (IsMachine) _priority[(int)work] = 0; // a machine is driven: no work of its own
        _searchedDesignations = -1; // look again with the new priorities
        _retryDelay = 0;
    }

    /// <summary>Whether it will do this work at all.</summary>
    public bool Does(Work work) => _priority[(int)work] > 0 && !IsChild;

    public static Labor LaborOf(Work work) => work switch
    {
        Work.Hauling or Work.Trading => Labor.Hauling,
        Work.Industry => Labor.Industry,
        _ => Labor.Designations,
    };

    /// <summary>The work type a designation kind belongs to.</summary>
    public static Work WorkOf(DesignationKind kind) => kind switch
    {
        DesignationKind.Chop => Work.Woodcutting,
        DesignationKind.Gather or DesignationKind.Sow => Work.Farming,
        _ => Work.Mining,
    };

    /// <summary>The work type of what it is doing now (null when idle, eating or sleeping).</summary>
    public Work? CurrentWork => Task switch
    {
        ColonistTask.Designation or ColonistTask.ScaffoldFetch => WorkOf(_jobKind),
        ColonistTask.Build => Work.Construction,
        ColonistTask.CraftFetch or ColonistTask.CraftWork or ColonistTask.CraftStore => Work.Industry,
        ColonistTask.HaulPickup or ColonistTask.HaulDeliver or ColonistTask.FurnishPickup or ColonistTask.FurnishDeliver => Work.Hauling,
        ColonistTask.TradePickup or ColonistTask.TradeDeliver => Work.Trading,
        ColonistTask.Board => _boardThen switch { ColonistTask.Designation => WorkOf(_jobKind), ColonistTask.HaulPickup => Work.Hauling, _ => Work.Trading },
        ColonistTask.Stoke when _vehicle is { } v => MachineInfo.WorkOf(v.Machine),
        _ => null,
    };
    private DesignationKind _jobKind;

    // ---- Hunger ------------------------------------------------------------------------------------------

    /// <summary>Ticks of fullness left (about <see cref="Needs.FullSatiety"/> after a good meal, more after a big one); 0 = starving.</summary>
    public int Satiety { get; internal set; }
    /// <summary>How long it has gone with nothing in its belly (it leaves at <see cref="Needs.StarvingLimit"/>).</summary>
    public int StarvingTicks { get; private set; }
    public bool Hungry => !IsAutomaton && Satiety < Needs.Hungry;
    public bool Starving => !IsAutomaton && Satiety == 0;
    private (TilePos Cell, ItemType Type, int Units) _meal;
    private int _foodRetry;

    /// <summary>Gets hungry; drops its work to eat when famished. False if this tick went to that.</summary>
    private bool TickHunger(ColonySim sim)
    {
        if (IsAutomaton || !sim.NeedsEnabled) return true;
        if (Satiety > 0) Satiety--;
        else StarvingTicks++;
        if (_foodRetry > 0) _foodRetry--;
        if (Downed || Task is ColonistTask.Fight or ColonistTask.Flee) return true; // no leaving a fight for a meal (drafted, it eats between fights)
        if (Satiety >= Needs.Famished || Task == ColonistTask.Eat || _foodRetry > 0) return true;
        return !TryEat(sim); // drops its work (and any load) only if there is food to go to
    }

    /// <summary>
    /// Heads for the nearest food it may eat (not a factory's input crate), reserving enough to fill up; gives up
    /// its current task first if it has food to go to. False if there is none in reach (it tries again a little later).
    /// </summary>
    private bool TryEat(ColonySim sim)
    {
        if (sim.PathBudgetExhausted) return false;
        int component = sim.Connectivity.ComponentOf(Position);
        _foodRetry = 100;
        if (component < 0) return false;
        var options = new List<(TilePos Cell, ItemPile Pile, int Dist)>();
        foreach (var (p, pile) in sim.Items.All)
        {
            if (!Needs.IsFood(pile.Type) || pile.Available <= 0 || !Needs.Usable(sim, p, Faction)) continue;
            if (sim.Connectivity.ComponentOf(p) != component) continue;
            options.Add((p, pile, Math.Abs(p.X - Position.X) + Math.Abs(p.Y - Position.Y) + 2 * Math.Abs(p.Z - Position.Z)));
        }
        if (options.Count == 0) return false;
        if (Task != ColonistTask.None) Abandon(sim); // the work waits (its claims released, any load dropped)
        // Its favourite is worth a few extra steps.
        int Cost((TilePos Cell, ItemPile Pile, int Dist) o) => o.Dist - (o.Pile.Type == FavoriteFood ? 8 : 0);
        options.Sort((a, b) => Cost(a) != Cost(b) ? Cost(a).CompareTo(Cost(b)) : CompareCells(a.Cell, b.Cell));
        foreach (var (cell, pile, _) in options.Take(CandidatesPerSearch))
        {
            var path = Route(sim, ReachableAround(sim, cell, component), cell);
            if (path is null) continue;
            int perUnit = Needs.MealTicks * Needs.Nutrition(pile.Type);
            int units = Math.Clamp((Needs.FullSatiety - Satiety) / perUnit, 1, pile.Available); // never more than fits
            if (!sim.Items.Reserve(cell, units)) continue;
            _meal = (cell, pile.Type, units);
            _foodRetry = 0;
            Begin(ColonistTask.Eat, cell, path);
            return true;
        }
        return false;
    }

    private static int CompareCells(TilePos a, TilePos b) =>
        a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);

    private void FinishMeal(ColonySim sim)
    {
        int eaten = sim.Items.TakeReserved(_meal.Cell, _meal.Units);
        Satiety += eaten * Needs.MealTicks * Needs.Nutrition(_meal.Type); // a loaf more than fills it: it lasts longer, nothing wasted
        if (eaten > 0 && _meal.Type == FavoriteFood) LastTreat = sim.Tick;
        if (Satiety > 0) StarvingTicks = 0;
        Finish();
    }

    /// <summary>Leaves the colony: lets go of every claim and drops what it carries.</summary>
    internal void Leave(ColonySim sim)
    {
        Abandon(sim);
        Park(sim);
        if (_claim is { } driver) driver.LoseVehicle(sim); // a machine broken up under its driver
    }

    // ---- Likes ----------------------------------------------------------------------------------------------
    // Fixed for each colonist (from its id): small pleasures on top of its needs. Meeting one lifts its mood a little;
    // missing one costs nothing.

    public static readonly string[] Materials = ["wood", "stone", "metal", "glass", "cloth", "clay"];

    /// <summary>The food it walks a little further for; a treat lifts its mood for a day.</summary>
    public ItemType FavoriteFood { get; }
    /// <summary>The material it likes to see in its home (a fixture made of it).</summary>
    public string LikedMaterial { get; }
    /// <summary>The art it likes in its home: tapestries, paintings or sculptures.</summary>
    public ItemType LikedArt { get; }
    /// <summary>The work it would rather do: tried first, and doing it most of the day pleases it.</summary>
    public Labor PreferredWork { get; }
    internal long LastTreat = long.MinValue / 2;
    internal int WorkTicks, PreferredTicks; // today's work, and how much of it was the preferred kind
    /// <summary>Mist sweeps (seconds) today spent standing in waterfall mist (<see cref="Fluids.Mist"/>).</summary>
    internal int MistSweeps;

    /// <summary>Mood from waterfall mist today: up to <see cref="Balance.MistMood"/> after <see cref="Balance.MistMoodSeconds"/> in it.</summary>
    public float MistBonus => Balance.MistMood * Math.Min(1f, MistSweeps / (float)Balance.MistMoodSeconds);

    private static T Pick<T>(IReadOnlyList<T> options, int id, int salt) =>
        options[(int)(Procedural.Noise.Hash(id, salt, 91, 4243) * (options.Count - 1e-3f))];

    private static Labor LaborOf(ColonistTask task) => task switch
    {
        ColonistTask.Designation or ColonistTask.Build or ColonistTask.ScaffoldFetch => Labor.Designations,
        ColonistTask.CraftFetch or ColonistTask.CraftWork or ColonistTask.CraftStore => Labor.Industry,
        _ => Labor.Hauling,
    };

    /// <summary>Which of its likes were met lately: a recent treat, its material and art at home, its kind of work.</summary>
    public (bool Treat, bool Material, bool Art, bool Work) LikesMet(long tick) => (
        tick - LastTreat < Needs.Day,
        Home is { } h && (h.Fixtures.Any(f => f.Item.Def.Material == LikedMaterial) || h.Shell.Materials.Contains(LikedMaterial)),
        Home?.Fixtures.Any(f => f.Item == LikedArt) ?? false,
        WorkTicks >= 200 && PreferredTicks * 2 >= WorkTicks);

    /// <summary>Mood from likes met: up to +0.15 (each of four counts a quarter).</summary>
    public float LikesBonus(long tick)
    {
        var (a, b, c, d) = LikesMet(tick);
        return 0.0375f * ((a ? 1 : 0) + (b ? 1 : 0) + (c ? 1 : 0) + (d ? 1 : 0));
    }

    /// <summary>"bread, wood, paintings, hauling", the met ones marked with +.</summary>
    public string DescribeLikes(long tick)
    {
        var (a, b, c, d) = LikesMet(tick);
        static string M(string s, bool met) => met ? s + "+" : s;
        return $"{M(FavoriteFood.Name.ToLowerInvariant(), a)}, {M(LikedMaterial, b)}, {M(LikedArt.Name.ToLowerInvariant(), c)}, {M(PreferredWork.ToString().ToLowerInvariant(), d)}";
    }

    // ---- Family -------------------------------------------------------------------------------------------

    /// <summary>
    /// Its household: its own id while single; a married couple and their children share a family id (see
    /// <see cref="Society"/>), and a family lives together in one home (<see cref="Housing"/>).
    /// </summary>
    public int Household { get; internal set; }
    /// <summary>The colony it belongs to (<see cref="ColonySim.Factions"/>): it only works, eats, sleeps and stores within it.</summary>
    public int Faction { get; init; }
    private Housing MyHousing(ColonySim sim) => sim.Factions[Faction].Housing;
    /// <summary>Its faction as a filter, or <see cref="Owners.Any"/> when the world has one colony (no checks to pay for).</summary>
    private int Filter(ColonySim sim) => sim.Factions.Count > 1 ? Faction : Owners.Any;
    public Colonist? Spouse { get; internal set; }
    /// <summary>Born in the colony and not grown yet: eats and sleeps, doesn't work.</summary>
    public bool IsChild { get; internal set; }
    /// <summary>When it was born (-1: arrived grown).</summary>
    public long BornTick { get; internal set; } = -1;
    /// <summary>Part of a family (married, or a child of one, grown or not) rather than a single.</summary>
    public bool InFamily => Household != Id;

    // ---- Home, rest and mood -----------------------------------------------------------------------------

    /// <summary>Where it lives (a bed of its own), or null: homeless colonists sleep on the floor.</summary>
    public Home? Home { get; internal set; }
    /// <summary>0..1, set each day by <see cref="Society"/> from its home, belly and the living standard.</summary>
    public float Happiness { get; internal set; } = 0.5f;
    /// <summary>Days running it has been unhappy (see <see cref="Needs.Unhappy"/>).</summary>
    public int UnhappyDays { get; internal set; }
    /// <summary>Work goes 15% slower for the miserable and 15% faster for the content (automatons: steady).</summary>
    public float WorkSpeed => IsAutomaton ? 1f : 0.85f + 0.3f * Happiness;
    /// <summary>Ticks awake it can still manage (<see cref="Needs.FullRest"/> after a full sleep).</summary>
    public float Rest { get; internal set; }
    public bool Tired => !IsAutomaton && Rest < Needs.Tired;
    private float _sleepRate;

    /// <summary>Tires while awake; dropping from exhaustion (wherever it is) when rest runs out.</summary>
    private void TickRest(ColonySim sim)
    {
        if (IsAutomaton || !sim.NeedsEnabled || Task == ColonistTask.Sleep || Downed) return;
        Rest = Math.Max(0, Rest - 1);
        if (Rest > 0 || Task is ColonistTask.Eat or ColonistTask.Fight or ColonistTask.Flee || Drafted) return;
        if (Task != ColonistTask.None) Abandon(sim);
        StartSleeping(onTheFloor: true);
    }

    /// <summary>Goes home to bed, or (homeless, or home out of reach) lies down where it is.</summary>
    private bool TrySleep(ColonySim sim)
    {
        if (Home is { } home && !sim.PathBudgetExhausted)
        {
            var bed = Housing.SleepCell(home, this);
            int component = sim.Connectivity.ComponentOf(Position);
            if (sim.Connectivity.ComponentOf(bed) == component && Route(sim, new HashSet<TilePos> { bed }, bed) is { } path)
            {
                _sleepRate = Housing.RestRate(home, this);
                Begin(ColonistTask.Sleep, bed, path);
                return true;
            }
        }
        StartSleeping(onTheFloor: true);
        return true;
    }

    private void StartSleeping(bool onTheFloor)
    {
        if (onTheFloor) _sleepRate = Housing.RestRate(null, this);
        Task = ColonistTask.Sleep;
        JobTarget = Position;
        _path.Clear();
        NextStep = null;
        StartWorking(int.MaxValue);
    }

    // ---- Furnishing homes ------------------------------------------------------------------------------

    private (Home Home, int Slot, TilePos Source, ItemType Item, int Count) _furnish;

    /// <summary>
    /// Brings fixtures to a home that its policy wants (nearest homes first, from the nearest pile of exactly the
    /// wanted tier) and installs them. Pieces are promised to the home so nobody else brings the same ones.
    /// </summary>
    private bool TryFurnish(ColonySim sim, int component)
    {
        if (MyHousing(sim).All.Count == 0) return false;
        var demands = MyHousing(sim).Demands().ToList();
        if (demands.Count == 0) return false; // every home has what its policy wants (checked before scanning the piles)
        var sources = new List<(TilePos Cell, ItemPile Pile)>();
        foreach (var (p, pile) in sim.Items.All)
            if (pile.Type.Def.Fixture != null && pile.Available > 0 && Needs.Usable(sim, p, Faction) && sim.Rooms.RoomAt(p) is not { Type.IsHome: true }
                && sim.Connectivity.ComponentOf(p) == component)
                sources.Add((p, pile));
        if (sources.Count == 0) return false;
        int Dist(TilePos a, TilePos b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + 2 * Math.Abs(a.Z - b.Z);
        foreach (var (home, slot, tier, missing) in demands.OrderBy(d => Dist(d.Home.Room.Cells.First(), Position)).ThenBy(d => d.Home.Room.Id).ToList())
        {
            if (sim.PathBudgetExhausted)
            {
                _deferred = true;
                return false;
            }
            var match = sources.Where(s => Housing.SlotOf(s.Pile.Type) == slot && s.Pile.Type.Def.FixtureTier == tier && !_pathFailed.Contains(s.Cell))
                .OrderBy(s => Dist(s.Cell, Position)).ThenBy(s => CompareKey(s.Cell)).FirstOrDefault();
            if (match.Pile.Count == 0) continue;
            if (!home.Room.Cells.Any(c => sim.Connectivity.ComponentOf(c) == component)) continue;
            var path = Route(sim, ReachableAround(sim, match.Cell, component), match.Cell);
            if (path is null)
            {
                _pathFailed.Add(match.Cell);
                continue;
            }
            int amount = Math.Min(Math.Min(missing, match.Pile.Available), ItemInfo.CarryLimit(match.Pile.Type));
            if (!sim.Items.Reserve(match.Cell, amount)) continue;
            MyHousing(sim).Promise(home, slot, amount);
            _furnish = (home, slot, match.Cell, match.Pile.Type, amount);
            Begin(ColonistTask.FurnishPickup, match.Cell, path);
            return true;
        }
        return false;
    }

    private static (int, int, int) CompareKey(TilePos p) => (p.Z, p.Y, p.X);

    private void FurnishPickUp(ColonySim sim)
    {
        int taken = sim.Items.TakeReserved(_furnish.Source, _furnish.Count);
        if (taken < _furnish.Count) MyHousing(sim).Unpromise(_furnish.Home, _furnish.Slot, _furnish.Count - taken);
        _furnish = _furnish with { Count = taken };
        if (taken == 0)
        {
            Finish();
            return;
        }
        CarryingType = _furnish.Item;
        CarryingCount = taken;
        int component = sim.Connectivity.ComponentOf(Position);
        var goals = _furnish.Home.Room.Cells.Where(c => sim.Connectivity.ComponentOf(c) == component).ToHashSet();
        var path = Route(sim, goals, _furnish.Home.Room.Cells.First());
        if (path is null)
        {
            Abandon(sim);
            return;
        }
        Begin(ColonistTask.FurnishDeliver, path.Count > 0 ? path[^1] : Position, path);
    }

    private void FurnishInstall(ColonySim sim)
    {
        int installed = MyHousing(sim).Install(sim, _furnish.Home, CarryingType, CarryingCount);
        MyHousing(sim).Unpromise(_furnish.Home, _furnish.Slot, _furnish.Count);
        if (installed < CarryingCount) sim.Items.Drop(Position, CarryingType, CarryingCount - installed); // surplus: hauled back to store
        CarryingCount = 0;
        Finish();
    }

    public string Describe() =>
        Dead ? "dead" : Downed ? "struck down, recovering" : Task is ColonistTask.Equip or ColonistTask.Fight or ColonistTask.Flee or ColonistTask.Rally ? DescribeCombat()
        : Drafted ? "drafted" : IsMachine ? DescribeMachine()
        : _aboard && Task == ColonistTask.None ? $"in {_vehicle!.Name}"
        : _aboard && Task != ColonistTask.Stoke ? $"{DescribeWork()}, driving {_vehicle!.Name}"
        : DescribeWork();

    private string DescribeWork() => (Task, State) switch
    {
        (ColonistTask.ScaffoldFetch, _) => DescribeScaffold(),
        (ColonistTask.Designation, _) when _scaffold != ScaffoldStep.None => DescribeScaffold(),
        (ColonistTask.Designation, ColonistState.Moving) => $"walking to job at {JobTarget}",
        (ColonistTask.Designation, ColonistState.Working) =>
            $"working at {JobTarget} ({WorkDone * 100 / Math.Max(1, WorkRequired)}%)",
        (ColonistTask.HaulPickup, _) => $"fetching {ItemInfo.Name(_haul.Type).ToLowerInvariant()} at {_haul.Source}",
        (ColonistTask.HaulDeliver, _) => $"hauling {CarryingCount} {ItemInfo.Name(CarryingType).ToLowerInvariant()} to {_haulDestination}",
        (ColonistTask.CraftFetch, _) => $"fetching {ItemInfo.Name(_craft.Pickups[Math.Min(_fetchIndex, _craft.Pickups.Count - 1)].Item).ToLowerInvariant()} for the {_craft.Machine.Name.ToLowerInvariant()}",
        (ColonistTask.CraftWork, ColonistState.Working) =>
            $"making {_craft.Batch.Recipe.Name.ToLowerInvariant()} at the {_craft.Machine.Name.ToLowerInvariant()} in {_craft.Machine.Room.Name} ({WorkDone * 100 / Math.Max(1, WorkRequired)}%)",
        (ColonistTask.CraftWork, _) => $"carrying {string.Join(", ", _carried.Select(c => ItemInfo.Name(c.Item).ToLowerInvariant()))} to the {_craft.Machine.Name.ToLowerInvariant()}",
        (ColonistTask.CraftStore, _) => $"storing {CarryingCount} {ItemInfo.Name(CarryingType).ToLowerInvariant()}",
        (ColonistTask.Build, ColonistState.Working) =>
            $"building a {_build.Name.ToLowerInvariant()}{_build.Where} ({WorkDone * 100 / Math.Max(1, WorkRequired)}%)",
        (ColonistTask.Build, _) => $"walking to build a {_build.Name.ToLowerInvariant()}",
        (ColonistTask.Board, _) => DescribeBoarding(),
        (ColonistTask.Stoke, _) => DescribeStoke(),
        (ColonistTask.Eat, ColonistState.Working) => $"eating {ItemInfo.Name(_meal.Type).ToLowerInvariant()}",
        (ColonistTask.Eat, _) => $"going to eat {ItemInfo.Name(_meal.Type).ToLowerInvariant()}",
        (ColonistTask.Sleep, ColonistState.Working) => Home != null && _sleepRate > Housing.RestRate(null, this) ? "asleep in bed" : "asleep on the floor",
        (ColonistTask.Sleep, _) => "going to bed",
        (ColonistTask.FurnishPickup, _) => $"fetching {ItemInfo.Name(_furnish.Item).ToLowerInvariant()} for {_furnish.Home.Name}",
        (ColonistTask.FurnishDeliver, ColonistState.Working) => $"installing {ItemInfo.Name(CarryingType).ToLowerInvariant()} in {_furnish.Home.Name}",
        (ColonistTask.FurnishDeliver, _) => $"carrying {ItemInfo.Name(CarryingType).ToLowerInvariant()} to {_furnish.Home.Name}",
        (ColonistTask.TradePickup, _) => $"fetching {ItemInfo.Name(_trade.Line.Type).ToLowerInvariant()} sold to {_tradePartner}",
        (ColonistTask.TradeDeliver, _) => $"carrying {CarryingCount} {ItemInfo.Name(CarryingType).ToLowerInvariant()} to {_tradePartner}'s trade depot",
        _ => "idle",
    };

    private string _tradePartner = "";

    private string _haulDestination = "stockpile";

    /// <summary>Tiles a colonist standing at <paramref name="at"/> can work on: all 26 neighbours.</summary>
    public static bool InReach(TilePos at, TilePos target) =>
        Math.Abs(at.X - target.X) <= 1 && Math.Abs(at.Y - target.Y) <= 1 && Math.Abs(at.Z - target.Z) <= 1;

    internal void Tick(ColonySim sim)
    {
        if (IsMachine)
        {
            MachineTick(sim); // driven, not working
            return;
        }
        TickPerson(sim);
        CheckVehicle(sim);
    }

    private void TickPerson(ColonySim sim)
    {
        ApplyGravity(sim);
        TickHunger(sim);
        TickRest(sim);
        if (TickCombat(sim)) return; // down, drafted, fighting back or fleeing: that is the whole tick

        if (Task == ColonistTask.Designation && _scaffold != ScaffoldStep.Strike && JobTarget is { } job && !sim.Designations.IsClaimedBy(job, Id))
            Abandon(sim); // cancelled, re-designated, or completed by someone else
        else if (Task == ColonistTask.ScaffoldFetch && !sim.Designations.IsClaimedBy(_scaffoldJob, Id))
            Abandon(sim); // the work it wanted the scaffold for went away
        else if (Task == ColonistTask.Board && (_vehicle?._claim != this || _boardThen == ColonistTask.Designation && !sim.Designations.IsClaimedBy(_boardJob, Id)))
            Abandon(sim); // the machine is gone, or the work it wanted the machine for
        else if (Task == ColonistTask.Build && _build.Removed)
            Abandon(sim); // the blueprint was cancelled (or its floor lost)
        else if (Task is ColonistTask.CraftFetch or ColonistTask.CraftWork && _craft.Machine.Removed)
            Abandon(sim); // the machine was removed (or its floor lost)

        switch (State)
        {
            case ColonistState.Idle:
                if (_retryDelay > 0)
                {
                    _retryDelay--;
                    return;
                }
                if (Hungry && sim.NeedsEnabled && _foodRetry == 0 && TryEat(sim)) return; // a meal before the next job
                if (!TendVehicle(sim)) return; // drivers: charcoal for the machine before the next job
                if (Tired && sim.NeedsEnabled && TrySleep(sim)) return; // and bed before work
                if (IsChild) return; // children don't work (they eat and sleep above)
                if (NothingNewSinceLastSearch(sim)) return;
                FindWork(sim);
                break;

            case ColonistState.Moving:
                switch (AdvanceMove(sim))
                {
                    case MoveResult.Arrived: Arrive(sim); break;
                    case MoveResult.Failed: Abandon(sim); break;
                }
                break;

            case ColonistState.Working:
                if (Task is not (ColonistTask.Sleep or ColonistTask.Eat))
                {
                    WorkTicks++;
                    if (LaborOf(Task) == PreferredWork) PreferredTicks++;
                }
                if (Task == ColonistTask.Sleep)
                {
                    Rest = Math.Min(Needs.FullRest, Rest + _sleepRate);
                    if (Rest >= Needs.FullRest) Finish(); // rested: up and about
                    return;
                }
                if (++WorkDone < WorkRequired) return;
                switch (Task)
                {
                    case ColonistTask.ScaffoldFetch:
                        PickUpScaffold(sim);
                        break;
                    case ColonistTask.Designation:
                        if (ScaffoldWorkStep(sim)) break;
                        if (!InReach(Position, JobTarget!.Value))
                        {
                            Abandon(sim);
                            return;
                        }
                        sim.CompleteDesignation(JobTarget!.Value);
                        if (Drives == MachineKind.Excavator) BurnFuel(sim, Balance.ExcavatorFuelPerDig);
                        Finish();
                        break;
                    case ColonistTask.Board:
                        FinishBoarding(sim);
                        break;
                    case ColonistTask.Stoke:
                        FinishStoke(sim);
                        break;
                    case ColonistTask.HaulPickup:
                        PickUp(sim);
                        break;
                    case ColonistTask.HaulDeliver:
                        Deliver(sim);
                        break;
                    case ColonistTask.CraftFetch:
                        CraftPickUp(sim);
                        break;
                    case ColonistTask.CraftWork:
                        CraftFinish(sim);
                        break;
                    case ColonistTask.CraftStore:
                        Deliver(sim, _craft.OutputCell!.Value);
                        break;
                    case ColonistTask.Build:
                        FinishBuild(sim);
                        break;
                    case ColonistTask.Eat:
                        FinishMeal(sim);
                        break;
                    case ColonistTask.FurnishPickup:
                        FurnishPickUp(sim);
                        break;
                    case ColonistTask.FurnishDeliver:
                        FurnishInstall(sim);
                        break;
                    case ColonistTask.TradePickup:
                        TradePickUp(sim);
                        break;
                    case ColonistTask.TradeDeliver:
                        TradeDeliver(sim);
                        break;
                    case ColonistTask.Equip:
                        FinishEquip(sim);
                        break;
                }
                break;
        }
    }

    private enum MoveResult { Moving, Arrived, Failed }

    /// <summary>
    /// One tick along the path (planning the next leg of a long trip at the end of one): arrived when the path is done,
    /// failed when a leg can't be planned or the ground changed under the next step.
    /// </summary>
    private MoveResult AdvanceMove(ColonySim sim)
    {
        if (NextStep is null)
        {
            if (_path.Count == 0 && _trip is { } trip)
            {
                // End of a leg: plan the next one (when the shared path budget allows).
                if (sim.PathBudgetExhausted) return MoveResult.Moving;
                sim.PathSearches++;
                var leg = PlanPath(sim, trip.Goals, trip.Near);
                _trip = _plannedTrip;
                if (leg is null) return MoveResult.Failed;
                foreach (var p in leg) _path.Enqueue(p);
            }
            if (_path.Count == 0) return MoveResult.Arrived;
            if (WaitForMachineAt(sim, _path.Peek(), out bool giveUp)) return giveUp ? MoveResult.Failed : MoveResult.Moving; // one machine to a tile
            var next = _path.Dequeue();
            if (Movement.MoveCost(sim.World, Position, next) is not { } cost) return MoveResult.Failed; // terrain changed under the path; replan
            NextStep = next;
            _stepTicksTotal = cost * TicksPerFlatStep * MachineInfo.StepScale(Drives);
            _stepTicksDone = 0;
        }

        _stepTicksDone++;
        if (_stepTicksDone >= _stepTicksTotal)
        {
            Position = NextStep!.Value;
            NextStep = null;
            if (_path.Count == 0 && _trip is null) return MoveResult.Arrived;
        }
        return MoveResult.Moving;
    }

    // ---- Finding work ------------------------------------------------------------------------------------

    private bool NothingNewSinceLastSearch(ColonySim sim) =>
        _searchedDesignations == sim.Designations.Version && _searchedConnectivity == sim.Connectivity.Version &&
        _searchedItems == sim.Items.Version && _searchedStockpiles == sim.Stockpiles.Version &&
        _searchedRooms == sim.Rooms.Version && _searchedHousing == MyHousing(sim).Version && _searchedTrades == sim.Trades.Version &&
        _searchedBelts == sim.Conveyors.WorkVersion;

    private int _searchedRooms = -1, _searchedHousing = -1, _searchedTrades = -1, _searchedBelts = -1;

    private void FindWork(ColonySim sim)
    {
        // Path searches are the expensive part of the sim; spread them over ticks rather than stacking them.
        if (sim.PathBudgetExhausted) return;
        _searchedDesignations = sim.Designations.Version;
        _searchedConnectivity = sim.Connectivity.Version;
        _searchedItems = sim.Items.Version;
        _searchedStockpiles = sim.Stockpiles.Version;
        _searchedRooms = sim.Rooms.Version;
        _searchedHousing = MyHousing(sim).Version;
        _searchedTrades = sim.Trades.Version;
        _searchedBelts = sim.Conveyors.WorkVersion;
        _deferred = false;

        if (_pathFailedConnectivity != sim.Connectivity.Version)
        {
            _pathFailed.Clear();
            _pathFailedConnectivity = sim.Connectivity.Version;
        }

        int component = sim.Connectivity.ComponentOf(Position);
        if (component < 0) return;

        if (_aboard)
        {
            // Driving: the machine's own trade while that pays (whatever its other priorities: it got in for this work).
            if (DriveOn(sim, component)) return;
            if (_deferred)
            {
                _searchedDesignations = -1;
                return;
            }
            Park(sim); // nothing worth driving to: out, and on foot from here
        }

        for (int priority = 1; priority <= LowestPriority && !_deferred; priority++)
        foreach (var labor in Labors)
        {
            bool found = false;
            switch (labor)
            {
                case Labor.Designations:
                    int kinds = 0; // designation kinds at this priority, searched together nearest first
                    for (int k = 0; k <= (int)DesignationKind.Sow; k++)
                        if (_priority[(int)WorkOf((DesignationKind)k)] == priority) kinds |= 1 << k;
                    found = kinds != 0 && TryTakeJob(sim, component, kinds);
                    if (!found && !_deferred && _priority[(int)Work.Construction] == priority) found = TryBuild(sim, component);
                    break;
                case Labor.Hauling:
                    if (_priority[(int)Work.Trading] == priority) found = TryTrade(sim, component);
                    // Homes first: a few trips that house people, which general hauling (never short of work in a
                    // busy colony) would otherwise starve forever.
                    if (!found && !_deferred && _priority[(int)Work.Hauling] == priority)
                        found = TryFurnish(sim, component) || (!_deferred && TryHaul(sim, component));
                    break;
                default:
                    if (_priority[(int)Work.Industry] == priority) found = TryCraft(sim, component);
                    break;
            }
            if (found) return;
            if (_deferred) break;
        }

        if (_deferred)
        {
            _searchedDesignations = -1; // budget ran out: resume next tick
            return;
        }
        // Every candidate failed; if others remain, try the next batch shortly.
        if (_pathFailed.Count > 0) _searchedDesignations = -1;
        _retryDelay = 5;
    }

    private bool TryTakeJob(ColonySim sim, int component, int kinds)
    {
        var orders = sim.Designations;
        bool driving = _aboard; // an excavator doesn't climb a scaffold
        var candidates = sim.JobBoard.Candidates(component, Position, orders, CandidatesPerSearch, _pathFailed, kinds, Filter(sim), scaffolds: !driving && sim.AnyScaffold);
        // Jobs only a scaffold reaches crowd out the rest when there is no scaffold to fetch: look again without them.
        TilePos? scaffold = null;
        if (candidates.Exists(j => Array.IndexOf(j.Components, component) < 0) && (scaffold = NearestScaffold(sim, component)) is null)
            candidates = sim.JobBoard.Candidates(component, Position, orders, CandidatesPerSearch, _pathFailed, kinds, Filter(sim));
        bool first = true;
        foreach (var job in candidates)
        {
            if (sim.PathBudgetExhausted)
            {
                _deferred = true;
                return false;
            }
            if (Array.IndexOf(job.Components, component) < 0)
            {
                if (!driving && scaffold is { } at && TryFetchScaffold(sim, job, at, component)) return true;
                continue;
            }
            var goals = new HashSet<TilePos>();
            foreach (var s in job.WorkSpots)
                if (sim.Connectivity.ComponentOf(s) == component) goals.Add(s);

            var path = Route(sim, goals, job.Target);
            if (path is null)
            {
                _pathFailed.Add(job.Target);
                continue;
            }
            if (first)
            {
                // The nearest job decides the machine (by the path just planned to it): drive on to it, or fetch one for it.
                first = false;
                var trip = _plannedTrip;
                int walk = RouteSteps(path, job.Target);
                if (driving && !ExcavatorStays(sim, job, walk)) return false;
                if (!driving && TryFetchExcavator(sim, job, component, walk)) return true;
                if (_deferred) return false;
                _plannedTrip = trip; // measuring planned other paths
            }
            if (!orders.Claim(job.Target, Id)) continue;

            _jobKind = job.Kind;
            Begin(ColonistTask.Designation, job.Target, path);
            return true;
        }
        return false;
    }

    private bool TryHaul(ColonySim sim, int component)
    {
        bool first = true;
        foreach (var plan in Hauling.Plans(sim.Items, sim.Stockpiles, sim.Rooms, sim.Connectivity, Position, component, _pathFailed, sim.Constructions, Filter(sim), sim.Conveyors, sim))
        {
            if (sim.PathBudgetExhausted)
            {
                _deferred = true;
                return false;
            }
            var path = Route(sim, ReachableAround(sim, plan.Source, component), plan.Source);
            if (path is null)
            {
                _pathFailed.Add(plan.Source);
                continue;
            }
            if (first)
            {
                first = false; // the nearest haul decides whether a wagon is worth fetching
                var trip = _plannedTrip;
                if (TryFetchWagon(sim, plan, component, RouteSteps(path, plan.Source))) return true;
                if (_deferred) return false;
                _plannedTrip = trip;
            }
            if (!ReserveHaul(sim, plan)) continue;
            ExtendLoad(sim, component);
            Begin(ColonistTask.HaulPickup, plan.Source, path);
            return true;
        }
        return false;
    }

    /// <summary>Takes on a haul plan: its units at the source, room for them at the destination.</summary>
    private bool ReserveHaul(ColonySim sim, HaulPlan plan)
    {
        if (!sim.Items.Reserve(plan.Source, plan.Amount)) return false;
        if (!sim.Items.ReserveIncoming(plan.Destination, plan.Type, plan.Amount))
        {
            sim.Items.Unreserve(plan.Source, plan.Amount);
            return false;
        }
        _haul = plan;
        _haulDestination = sim.Rooms.PartAt(plan.Destination) switch
        {
            { Built: false } => "a construction site",
            { Role: PartRole.Input } => "a factory",
            _ => "stockpile",
        };
        return true;
    }

    /// <summary>
    /// Trading: goods this colony owes on an agreed deal, from the nearest pile in its own stores to a cell of the
    /// buyer's trade depot with room (nearest first), as much as one load carries. The line is claimed as it goes
    /// (<see cref="Trade.Trades.Claim"/>) so traders share the work; giving up hands it back. Driving a power wagon, a load
    /// is many: more from the store piles of it round the pickup, into the buyer's depot cells round the drop
    /// (<see cref="WagonReach"/>). On foot, it may first fetch a wagon if the rest of the line makes that pay.
    /// </summary>
    private bool TryTrade(ColonySim sim, int component)
    {
        static long Dist(TilePos a, TilePos b)
        {
            long dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + 4 * dz * dz;
        }
        bool first = true;
        foreach (var (deal, line) in sim.Trades.Owed(Faction).ToList())
        {
            int buyer = deal.Other(Faction);
            TilePos? dest = null;
            long best = long.MaxValue;
            foreach (var c in sim.Stockpiles.DepotCells)
            {
                if (sim.Stockpiles.OwnerOf(c) != buyer || sim.Connectivity.ComponentOf(c) != component || sim.Items.RoomAt(c, line.Type) <= 0) continue;
                long d = Dist(c, Position);
                if (d < best || d == best && CompareCells(c, dest!.Value) < 0) (best, dest) = (d, c);
            }
            if (dest is not { } to) continue; // their depot is full, missing or out of reach: try later
            TilePos? source = null;
            best = long.MaxValue;
            foreach (var (p, pile) in sim.Items.All)
            {
                if (pile.Type != line.Type || pile.Available <= 0 || _pathFailed.Contains(p) || sim.Connectivity.ComponentOf(p) != component) continue;
                if (!Trade.Trades.Sendable(sim, p, pile.Type, Faction)) continue;
                long d = Dist(p, Position);
                if (d < best || d == best && CompareCells(p, source!.Value) < 0) (best, source) = (d, p);
            }
            if (source is not { } from) continue; // nothing of it in store yet: the deal waits
            if (sim.PathBudgetExhausted)
            {
                _deferred = true;
                return false;
            }
            var path = Route(sim, ReachableAround(sim, from, component), from);
            if (path is null)
            {
                _pathFailed.Add(from);
                continue;
            }
            if (first)
            {
                first = false; // the first deal it can ship decides whether a wagon is worth fetching
                var trip = _plannedTrip;
                if (TryFetchWagonForTrade(sim, line, from, to, component, RouteSteps(path, from))) return true;
                if (_deferred) return false;
                _plannedTrip = trip;
            }
            int amount = ReserveTrade(sim, line, buyer, from, to, component);
            if (amount == 0) continue;
            sim.Trades.Claim(line, amount);
            _trade = (deal, line, from, to, amount);
            _tradePartner = sim.Factions[buyer].Name;
            Begin(ColonistTask.TradePickup, from, path);
            return true;
        }
        return false;
    }

    /// <summary>Goods reserved for a trade trip (the pickup pile first; a wagon gathers from piles round it).</summary>
    private readonly List<(TilePos Cell, int Amount)> _tradeGoods = new();
    /// <summary>Room reserved for them in the buyer's depot (the drop cell first; a wagon fills cells round it).</summary>
    private readonly List<(TilePos Cell, int Amount)> _tradeRoom = new();

    /// <summary>Cells round <paramref name="c"/> on its level, ring by ring out to <paramref name="reach"/> (the cell itself first).</summary>
    private static IEnumerable<TilePos> Rings(TilePos c, int reach)
    {
        yield return c;
        for (int r = 1; r <= reach; r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r) yield return c.Offset(dx, dy, 0);
    }

    /// <summary>
    /// Reserves one trip of a deal line: room in the buyer's depot, then goods from our stores to fill it (as much as is
    /// still owed and it can carry). Returns the units, 0 if none (nothing is left reserved then).
    /// </summary>
    private int ReserveTrade(ColonySim sim, Trade.TradeLine line, int buyer, TilePos from, TilePos to, int component)
    {
        _tradeGoods.Clear();
        _tradeRoom.Clear();
        int reach = Drives == MachineKind.PowerWagon ? WagonReach : 0;
        int want = Math.Min(line.Unclaimed, CarryCapacity(line.Type)), room = 0, goods = 0;
        foreach (var c in Rings(to, reach))
        {
            if (room >= want) break;
            if (c != to && (!sim.Stockpiles.IsDepot(c) || sim.Stockpiles.OwnerOf(c) != buyer || sim.Connectivity.ComponentOf(c) != component)) continue;
            int n = Math.Min(want - room, sim.Items.RoomAt(c, line.Type));
            if (n <= 0 || !sim.Items.ReserveIncoming(c, line.Type, n)) continue;
            _tradeRoom.Add((c, n));
            room += n;
        }
        foreach (var c in Rings(from, reach))
        {
            if (goods >= room) break;
            if (!sim.Items.TryGet(c, out var pile) || pile.Type != line.Type || pile.Available <= 0) continue;
            if (c != from && (sim.Connectivity.ComponentOf(c) != component || !Trade.Trades.Sendable(sim, c, pile.Type, Faction))) continue;
            int n = Math.Min(room - goods, pile.Available);
            if (!sim.Items.Reserve(c, n)) continue;
            _tradeGoods.Add((c, n));
            goods += n;
        }
        TrimTradeRoom(sim, room - goods);
        return goods;
    }

    /// <summary>Gives back <paramref name="units"/> of the depot room reserved for a trade (last cells first).</summary>
    private void TrimTradeRoom(ColonySim sim, int units)
    {
        for (int i = _tradeRoom.Count - 1; units > 0 && i >= 0; i--)
        {
            var (c, n) = _tradeRoom[i];
            int back = Math.Min(n, units);
            sim.Items.ReleaseIncoming(c, back);
            _tradeRoom[i] = (c, n - back);
            units -= back;
        }
        _tradeRoom.RemoveAll(r => r.Amount == 0);
    }

    /// <summary>Lets go of a trade trip's reservations: goods not yet picked up and depot room not yet filled.</summary>
    private void ReleaseTrade(ColonySim sim)
    {
        foreach (var (c, n) in _tradeGoods) sim.Items.Unreserve(c, n);
        foreach (var (c, n) in _tradeRoom) sim.Items.ReleaseIncoming(c, n);
        _tradeGoods.Clear();
        _tradeRoom.Clear();
    }

    private void TradePickUp(ColonySim sim)
    {
        int taken = 0;
        foreach (var (c, n) in _tradeGoods) taken += sim.Items.TakeReserved(c, n);
        _tradeGoods.Clear();
        if (taken < _trade.Amount)
        {
            TrimTradeRoom(sim, _trade.Amount - taken);
            sim.Trades.Unclaim(_trade.Line, _trade.Amount - taken);
        }
        if (taken == 0)
        {
            Finish();
            return;
        }
        CarryingType = _trade.Line.Type;
        CarryingCount = taken;
        _trade = _trade with { Amount = taken };
        int component = sim.Connectivity.ComponentOf(Position);
        var path = Route(sim, ReachableAround(sim, _trade.Destination, component), _trade.Destination);
        if (path is null)
        {
            Abandon(sim); // the load is dropped here, and owed again
            return;
        }
        Begin(ColonistTask.TradeDeliver, _trade.Destination, path);
    }

    private void TradeDeliver(ColonySim sim)
    {
        int count = CarryingCount;
        foreach (var (c, n) in _tradeRoom)
        {
            int put = Math.Min(n, CarryingCount);
            if (put < n) sim.Items.ReleaseIncoming(c, n - put); // Deliver releases what it is handed; the rest of the room goes back
            CarryingCount -= sim.Items.Deliver(c, CarryingType, put);
        }
        _tradeRoom.Clear();
        if (CarryingCount > 0) sim.Items.Drop(Position, CarryingType, CarryingCount); // spills beside the depot: still theirs to collect
        CarryingCount = 0;
        if (Drives == MachineKind.PowerWagon) BurnFuel(sim, Balance.WagonFuelPerTrip);
        sim.Trades.Delivered(sim, _trade.Deal, _trade.Line, count);
        Finish();
    }

    /// <summary>
    /// Factory work: a free machine (biggest first) whose recipe this colonist is educated enough for, in a room
    /// whose input crates hold a batch of every input and whose output crate has room for the products
    /// (education and worker recipes have no products to store). Reserves everything, then walks to the first
    /// input crate.
    /// </summary>
    private bool TryCraft(ColonySim sim, int component)
    {
        foreach (var room in sim.Rooms.All.Where(r => r.Faction == Faction))
        foreach (var machine in FreeMachinesBiggestFirst(room))
        {
            var recipe = machine.Recipe!;
            if (recipe.Education > Education && !sim.DevMode || IsAutomaton && recipe.Kind == ItemKind.Education) continue; // dev mode: no schooling needed
            if (Rooms.AtLimit(sim, machine)) continue; // stocked up to its limit
            if (MachineSpots(sim, machine, component).Count == 0) continue;
            var full = machine.Batch;
            int maxUnits = full.Units;
            if (recipe.Kind == ItemKind.Education) maxUnits = Math.Min(maxUnits, sim.StudentsAvailable(recipe, Faction));
            if (maxUnits == 0) continue;

            // A full batch if the crates hold one; otherwise whatever they hold, as long as nothing more is on
            // its way (so the last logs still get sawn), in proportionally less time.
            var crates = room.PartsWithRole(PartRole.Input).Select(c => c.Cells[0])
                .Where(c => sim.Connectivity.ComponentOf(c) == component).ToList();
            int units = maxUnits;
            bool refilling = false;
            foreach (var ing in recipe.Inputs)
            {
                int available = 0;
                foreach (var c in crates)
                    if (sim.Items.TryGet(c, out var pile) && pile.Type == ing.Item)
                    {
                        available += pile.Available;
                        refilling |= pile.Incoming > 0;
                    }
                units = Math.Min(units, available / ing.Count);
            }
            if (units == 0 || units < maxUnits && refilling) continue;
            var batch = units == full.Units ? full : PartInfo.PartialBatch(full, units);

            TilePos? output = null;
            if (recipe.Kind == ItemKind.Good)
            {
                output = Rooms.OutputCrateFor(room, recipe.Output, batch.OutCount, sim.Items, c => sim.Connectivity.ComponentOf(c) == component);
                if (output is null) continue;
            }

            // Pickups: each input from as few crates as possible, in crate order.
            var pickups = new List<(TilePos Cell, ItemType Item, int Count)>();
            for (int i = 0; i < recipe.Inputs.Count; i++)
            {
                var item = recipe.Inputs[i].Item;
                int need = batch.InCount(i);
                foreach (var c in crates)
                {
                    if (need == 0) break;
                    if (!sim.Items.TryGet(c, out var pile) || pile.Type != item || pile.Available <= 0) continue;
                    int take = Math.Min(need, pile.Available);
                    pickups.Add((c, item, take));
                    need -= take;
                }
            }
            if (sim.PathBudgetExhausted)
            {
                _deferred = true;
                return false;
            }

            var first = pickups[0].Cell;
            var path = Route(sim, ReachableAround(sim, first, component), first);
            if (path is null)
            {
                _pathFailed.Add(first);
                continue;
            }
            int reserved = 0;
            while (reserved < pickups.Count && sim.Items.Reserve(pickups[reserved].Cell, pickups[reserved].Count)) reserved++;
            if (reserved < pickups.Count || output is { } o && !sim.Items.ReserveIncoming(o, recipe.Output, batch.OutCount))
            {
                for (int i = 0; i < reserved; i++) sim.Items.Unreserve(pickups[i].Cell, pickups[i].Count);
                continue;
            }
            if (recipe.Kind == ItemKind.Education) sim.ReserveStudents(recipe, batch.Units, Faction);
            machine.ClaimedBy = Id;
            _craft = new CraftPlan(machine, batch, pickups, output);
            _fetchIndex = 0;
            Begin(ColonistTask.CraftFetch, first, path);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Construction: a blueprint nobody is building whose materials are all on its footprint. Reserves them,
    /// then walks next to the part.
    /// </summary>
    private bool TryBuild(ColonySim sim, int component)
    {
        foreach (var room in sim.Rooms.All.Where(r => r.Faction == Faction))
        foreach (var part in room.Blueprints)
        {
            if (part.BuilderId >= 0) continue;
            var (material, cost) = part.Cost;
            if (Rooms.MaterialsOnSite(part, sim.Items, available: true) < cost) continue;
            var spots = MachineSpots(sim, part, component);
            if (spots.Count == 0) continue;
            if (sim.PathBudgetExhausted)
            {
                _deferred = true;
                return false;
            }
            var path = Route(sim, spots, part.Origin);
            if (path is null)
            {
                _pathFailed.Add(part.Origin);
                continue;
            }
            var reserved = new List<(TilePos, int)>();
            int left = cost;
            foreach (var c in part.Cells)
            {
                if (left == 0) break;
                if (!sim.Items.TryGet(c, out var pile) || pile.Type != material || pile.Available <= 0) continue;
                int take = Math.Min(left, pile.Available);
                if (!sim.Items.Reserve(c, take)) continue;
                reserved.Add((c, take));
                left -= take;
            }
            if (left > 0)
            {
                foreach (var (c, n) in reserved) sim.Items.Unreserve(c, n);
                continue;
            }
            part.BuilderId = Id;
            _build = new BuildPlan(part, null, reserved);
            Begin(ColonistTask.Build, part.Origin, path);
            return true;
        }
        return TryBuildStructure(sim, component);
    }

    /// <summary>A structure blueprint (wall, wheel, pump) nobody is building whose material is in its cell.</summary>
    private bool TryBuildStructure(ColonySim sim, int component)
    {
        foreach (var site in sim.Constructions.All.Where(s => s.Faction == Faction))
        {
            if (site.BuilderId >= 0) continue;
            var (_, cost) = site.Cost;
            if (Constructions.MaterialsOnSite(site, sim.Items, available: true) < cost) continue;
            // Never stand in the cell being built (a wall would entomb the builder). A deck or upper stair is built from
            // below (its materials are there): around the cell it goes in, never in it.
            // A conveyor tile is built from the ground under it (a raised one goes up on trestles from there).
            var spots = ReachableAround(sim, site.Kind == StructureKind.Conveyor ? site.Cell : site.Target, component);
            spots.Remove(site.Target);
            if (spots.Count == 0) continue;
            if (sim.PathBudgetExhausted)
            {
                _deferred = true;
                return false;
            }
            var path = Route(sim, spots, site.Target);
            if (path is null)
            {
                _pathFailed.Add(site.Cell);
                continue;
            }
            if (!sim.Items.Reserve(site.Cell, cost)) continue;
            site.BuilderId = Id;
            _build = new BuildPlan(null, site, [(site.Cell, cost)]);
            Begin(ColonistTask.Build, site.Target, path);
            return true;
        }
        return false;
    }

    /// <summary>Construction done: the materials are used up and the part or structure starts working.</summary>
    private void FinishBuild(ColonySim sim)
    {
        int needed = 0, taken = 0;
        foreach (var (c, n) in _build.Materials)
        {
            needed += n;
            taken += sim.Items.TakeReserved(c, n);
        }
        if (taken < needed)
        {
            // Something took materials from under us (shouldn't happen with reservations): give them back.
            if (taken > 0) sim.Items.Drop(_build.Materials[0].Cell, _build.Cost.Material, taken);
            _build.ReleaseBuilder(Id);
            Finish();
            return;
        }
        if (_build.Part is { } part) sim.Rooms.CompleteConstruction(part);
        else sim.CompleteConstruction(_build.Site!);
        Finish();
    }

    /// <summary>Unclaimed machines, biggest (least labour per unit) first, then in placement order.</summary>
    private static IEnumerable<Part> FreeMachinesBiggestFirst(Room room) =>
        room.PartsWithRole(PartRole.Machine).Where(m => m.ClaimedBy < 0).OrderByDescending(m => m.Size).ThenBy(m => m.Id);

    /// <summary>Standable tiles in reach of any of the machine's cells, within the component.</summary>
    private static HashSet<TilePos> MachineSpots(ColonySim sim, Part machine, int component)
    {
        var spots = new HashSet<TilePos>();
        foreach (var c in machine.Cells) spots.UnionWith(ReachableAround(sim, c, component));
        return spots;
    }

    /// <summary>Takes the next input from its crate, then walks to the next crate or, with everything in hand, to the machine.</summary>
    private void CraftPickUp(ColonySim sim)
    {
        var (cell, item, count) = _craft.Pickups[_fetchIndex++];
        int taken = sim.Items.TakeReserved(cell, count);
        if (taken > 0) _carried.Add((item, taken));
        CarryingType = item;
        CarryingCount = _carried.Sum(c => c.Count);
        if (taken < count)
        {
            Abandon(sim); // someone or something took the batch; drops what we got
            return;
        }
        int component = sim.Connectivity.ComponentOf(Position);
        if (_fetchIndex < _craft.Pickups.Count)
        {
            var next = _craft.Pickups[_fetchIndex].Cell;
            var toCrate = Route(sim, ReachableAround(sim, next, component), next);
            if (toCrate is null)
            {
                Abandon(sim);
                return;
            }
            Begin(ColonistTask.CraftFetch, next, toCrate);
            return;
        }
        // Every input reservation is used up now: from here on, cleanup is the work phase's (see Abandon).
        var path = Route(sim, MachineSpots(sim, _craft.Machine, component), _craft.Machine.Origin);
        if (path is null)
        {
            Task = ColonistTask.CraftWork;
            Abandon(sim);
            return;
        }
        Begin(ColonistTask.CraftWork, _craft.Machine.Origin, path);
    }

    /// <summary>
    /// Batch processed: the inputs are used up. Goods are carried to the output crate; an education batch
    /// promotes its students and a worker batch (automatons) adds colonists, right here.
    /// </summary>
    private void CraftFinish(ColonySim sim)
    {
        var batch = _craft.Batch;
        _craft.Machine.Working = false;
        _craft.Machine.ClaimedBy = -1; // free for the next worker while we carry the products away
        _carried.Clear();
        CarryingCount = 0;
        switch (batch.Recipe.Kind)
        {
            case ItemKind.Education:
                sim.Graduate(batch.Recipe, batch.Units, Faction);
                Finish();
                return;
            case ItemKind.Worker:
                sim.AddWorkers(batch.Recipe, batch.Units, Position, Faction);
                Finish();
                return;
            case ItemKind.Machine:
                sim.AddMachines(batch.Recipe, batch.Units, Position, Faction);
                Finish();
                return;
        }
        CarryingType = batch.Recipe.Output;
        CarryingCount = batch.OutCount;
        sim.ItemsProduced += batch.OutCount;

        var outCell = _craft.OutputCell!.Value;
        int component = sim.Connectivity.ComponentOf(Position);
        var path = Route(sim, ReachableAround(sim, outCell, component), outCell);
        if (path is null)
        {
            Task = ColonistTask.CraftStore;
            Abandon(sim);
            return;
        }
        Begin(ColonistTask.CraftStore, outCell, path);
    }

    private void Begin(ColonistTask task, TilePos target, List<TilePos> path)
    {
        Task = task;
        JobTarget = target;
        _blockedFor = 0;
        _path.Clear();
        foreach (var p in path) _path.Enqueue(p);
        _trip = _plannedTrip; // the path is one leg of a longer trip if the plan said so
        State = ColonistState.Moving;
    }

    /// <summary>A path to any of <paramref name="goals"/> (empty if already at one), or null.</summary>
    private List<TilePos>? Route(ColonySim sim, HashSet<TilePos> goals, TilePos near)
    {
        if (goals.Count == 0) return null;
        _plannedTrip = null;
        if (goals.Contains(Position)) return new List<TilePos>();
        sim.PathSearches++;
        return PlanPath(sim, goals, near);
    }

    /// <summary>Standable tiles in reach of <paramref name="target"/> (itself included) within the component.</summary>
    private static HashSet<TilePos> ReachableAround(ColonySim sim, TilePos target, int component)
    {
        var goals = new HashSet<TilePos>();
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            var s = target.Offset(dx, dy, dz);
            if (sim.Connectivity.ComponentOf(s) == component) goals.Add(s);
        }
        return goals;
    }

    // ---- Doing work -------------------------------------------------------------------------------------

    private void Arrive(ColonySim sim)
    {
        var target = JobTarget!.Value;
        bool inReach = Task switch
        {
            ColonistTask.CraftWork => _craft.Machine.Cells.Any(c => InReach(Position, c)), // machines can be several tiles long
            ColonistTask.Build => _build.InReach(Position),
            ColonistTask.Board => _vehicle is { } v && InReach(Position, v.Position), // it may have fallen since
            _ => InReach(ReachFrom, target), // from the cell above when bringing a scaffold
        };
        if (!inReach)
        {
            Abandon(sim);
            return;
        }
        switch (Task)
        {
            case ColonistTask.CraftFetch:
            case ColonistTask.CraftStore:
                StartWorking(HandleTicks);
                break;
            case ColonistTask.CraftWork:
                _craft.Machine.Working = true;
                StartWorking((int)MathF.Round(_craft.Batch.WorkTicks / _craft.Machine.Room.Efficiency));
                break;
            case ColonistTask.Build:
                StartWorking(_build.Ticks);
                break;
            case ColonistTask.Eat:
                StartWorking(Needs.EatTicks);
                break;
            case ColonistTask.Sleep:
                StartWorking(int.MaxValue); // until rested (see Tick)
                break;
            case ColonistTask.FurnishPickup:
                StartWorking(HandleTicks);
                break;
            case ColonistTask.FurnishDeliver:
                StartWorking(Housing.InstallTicks);
                break;
            case ColonistTask.Equip:
                StartWorking(HandleTicks);
                break;
            case ColonistTask.Designation:
                if (!sim.Designations.TryGet(target, out var kind))
                {
                    Abandon(sim);
                    return;
                }
                if (_scaffold == ScaffoldStep.Carry)
                {
                    if (!BeginErect(sim)) Abandon(sim);
                    return;
                }
                StartWorking(sim.WorkTicks(target, kind));
                break;
            case ColonistTask.ScaffoldFetch:
                StartWorking(HandleTicks);
                break;
            case ColonistTask.Board:
                StartWorking(Balance.MachineBoardTicks);
                break;
            case ColonistTask.Stoke:
                StartWorking(Needs.EatTicks);
                break;
            case ColonistTask.HaulPickup:
            case ColonistTask.HaulDeliver:
            case ColonistTask.TradePickup:
            case ColonistTask.TradeDeliver:
                StartWorking(HandleTicks);
                break;
            default:
                Abandon(sim);
                break;
        }
    }

    private void StartWorking(int ticks)
    {
        State = ColonistState.Working;
        WorkDone = 0;
        // Mood sets the pace of real work (not of sleeping, eating or the long sleep sentinel).
        if (ticks != int.MaxValue && Task is not (ColonistTask.Eat or ColonistTask.Sleep)) ticks = (int)MathF.Round(ticks / WorkSpeed);
        if (Drives == MachineKind.Excavator && Task == ColonistTask.Designation) ticks = Math.Max(1, (int)MathF.Round(ticks / Balance.ExcavatorDigSpeed));
        WorkRequired = Math.Max(1, ticks);
    }

    private void PickUp(ColonySim sim)
    {
        int taken = sim.Items.TakeReserved(_haul.Source, _haul.Amount);
        if (taken < _haul.Amount) sim.Items.ReleaseIncoming(_haul.Destination, _haul.Amount - taken);
        int spill = PickUpSpill(sim, taken, sim.Connectivity.ComponentOf(Position)); // a wagon's extra units, for the cells round its stop
        if (taken + spill == 0)
        {
            Finish(); // someone or something moved the pile; nothing to carry
            return;
        }
        CarryingType = _haul.Type;
        int component = sim.Connectivity.ComponentOf(Position);
        if (Drives != MachineKind.PowerWagon) taken += TopUp(sim, _haul.Source, _haul.Destination, _haul.Type, taken, component);
        CarryingCount = taken + spill;
        _haul = _haul with { Amount = taken };

        var path = Route(sim, ReachableAround(sim, _haul.Destination, component), _haul.Destination);
        if (path is null)
        {
            Abandon(sim); // drops the load here; it'll be hauled again from wherever it lands
            return;
        }
        Begin(ColonistTask.HaulDeliver, _haul.Destination, path);
    }

    /// <summary>
    /// Fills the rest of a load from loose piles of the same item right beside the one just picked up (gathering and
    /// felling leave many small piles, and a playtest's haulers walked a whole trip for 2-5 herbs at a time). Only goods
    /// lying loose (not in a stockpile, crate, depot or building site), as far as the destination has room. Returns
    /// the units added.
    /// </summary>
    private static int TopUp(ColonySim sim, TilePos source, TilePos destination, ItemType type, int carrying, int component)
    {
        int added = 0, limit = ItemInfo.CarryLimit(type);
        // A stockpile with a cap takes no more than it has left (what is on its way counts already).
        if (sim.Stockpiles.ZoneAt(destination) is { Cap: > 0 } zone)
            limit = Math.Min(limit, carrying + zone.Takes(type, zone.Held(type, sim.Items)));
        for (int dy = -TopUpReach; dy <= TopUpReach && carrying + added < limit; dy++)
        for (int dx = -TopUpReach; dx <= TopUpReach && carrying + added < limit; dx++)
        {
            var c = source.Offset(dx, dy, 0);
            if (c == source || !sim.Items.TryGet(c, out var pile) || pile.Type != type || pile.Available <= 0) continue;
            if (sim.Stockpiles.Contains(c) || sim.Rooms.PartAt(c) != null || sim.Constructions.At(c) != null || sim.Connectivity.ComponentOf(c) != component) continue;
            int n = Math.Min(Math.Min(pile.Available, limit - carrying - added), sim.Items.RoomAt(destination, type));
            if (n <= 0) break; // destination full
            if (!sim.Items.ReserveIncoming(destination, type, n)) break;
            if (!sim.Items.Reserve(c, n))
            {
                sim.Items.ReleaseIncoming(destination, n);
                continue;
            }
            added += sim.Items.TakeReserved(c, n);
        }
        return added;
    }

    /// <summary>How far round a picked-up pile a hauler gathers more of the same (Chebyshev tiles, same level).</summary>
    private const int TopUpReach = 2;

    private void Deliver(ColonySim sim)
    {
        if (_spill.Count > 0) DeliverSpill(sim);
        if (Drives == MachineKind.PowerWagon) BurnFuel(sim, Balance.WagonFuelPerTrip);
        Deliver(sim, _haul.Destination);
    }

    /// <summary>Puts the load into <paramref name="destination"/> (whose incoming reservation it fills); spills the rest.</summary>
    private void Deliver(ColonySim sim, TilePos destination)
    {
        int added = sim.Items.Deliver(destination, CarryingType, CarryingCount);
        if (added < CarryingCount) sim.Items.Drop(Position, CarryingType, CarryingCount - added);
        CarryingCount = 0;
        Finish();
    }

    /// <summary>Task done: back to idle, looking for work straight away.</summary>
    private void Finish()
    {
        _scaffold = ScaffoldStep.None;
        _trip = null;
        Task = ColonistTask.None;
        JobTarget = null;
        State = ColonistState.Idle;
        _searchedDesignations = -1;
    }

    /// <summary>Gives up the current task, releasing every claim and reservation and dropping any load.</summary>
    private void Abandon(ColonySim sim)
    {
        AbandonScaffold(sim);
        switch (Task)
        {
            case ColonistTask.Designation when JobTarget is { } t:
                sim.Designations.Release(t, Id);
                break;
            case ColonistTask.HaulPickup:
                sim.Items.Unreserve(_haul.Source, _haul.Amount);
                sim.Items.ReleaseIncoming(_haul.Destination, _haul.Amount);
                ReleaseSpill(sim, source: true);
                break;
            case ColonistTask.HaulDeliver:
                sim.Items.ReleaseIncoming(_haul.Destination, _haul.Amount);
                ReleaseSpill(sim, source: false);
                break;
            case ColonistTask.TradePickup:
                ReleaseTrade(sim);
                sim.Trades.Unclaim(_trade.Line, _trade.Amount);
                break;
            case ColonistTask.TradeDeliver:
                ReleaseTrade(sim);
                sim.Trades.Unclaim(_trade.Line, _trade.Amount); // the load is dropped below; still owed
                break;
            case ColonistTask.CraftFetch:
                for (int i = _fetchIndex; i < _craft.Pickups.Count; i++) sim.Items.Unreserve(_craft.Pickups[i].Cell, _craft.Pickups[i].Count);
                ReleaseCraft(sim);
                break;
            case ColonistTask.CraftWork:
                ReleaseCraft(sim);
                break;
            case ColonistTask.CraftStore:
                if (_craft.OutputCell is { } o) sim.Items.ReleaseIncoming(o, _craft.Batch.OutCount);
                break;
            case ColonistTask.Build:
                foreach (var (c, n) in _build.Materials) sim.Items.Unreserve(c, n);
                _build.ReleaseBuilder(Id);
                break;
            case ColonistTask.Eat:
                sim.Items.Unreserve(_meal.Cell, _meal.Units);
                break;
            case ColonistTask.FurnishPickup:
                sim.Items.Unreserve(_furnish.Source, _furnish.Count);
                MyHousing(sim).Unpromise(_furnish.Home, _furnish.Slot, _furnish.Count);
                break;
            case ColonistTask.FurnishDeliver:
                MyHousing(sim).Unpromise(_furnish.Home, _furnish.Slot, _furnish.Count);
                break;
            case ColonistTask.Equip:
                sim.Items.Unreserve(_equip.Cell, 1);
                break;
            case ColonistTask.Board:
                AbandonBoarding(sim);
                break;
            case ColonistTask.Stoke:
                sim.Items.Unreserve(_stoke.Cell, _stoke.Units);
                break;
        }
        if (!_aboard) Park(sim); // a machine it was walking to is free again (aboard, it drives on)
        if (_carried.Count > 0)
        {
            foreach (var (item, n) in _carried) sim.Items.Drop(Position, item, n);
            _carried.Clear();
            CarryingCount = 0;
        }
        else if (CarryingCount > 0)
        {
            sim.Items.Drop(Position, CarryingType, CarryingCount);
            CarryingCount = 0;
        }
        _path.Clear();
        NextStep = null;
        Finish();
    }

    /// <summary>Undoes a craft plan's output space, student places and machine claim.</summary>
    private void ReleaseCraft(ColonySim sim)
    {
        if (_craft.OutputCell is { } o) sim.Items.ReleaseIncoming(o, _craft.Batch.OutCount);
        if (_craft.Batch.Recipe.Kind == ItemKind.Education) sim.ReleaseStudents(_craft.Batch.Recipe, _craft.Batch.Units, Faction);
        ReleaseMachine();
    }

    private void ReleaseMachine()
    {
        var m = _craft.Machine;
        if (m.ClaimedBy != Id) return;
        m.ClaimedBy = -1;
        m.Working = false;
    }

    /// <summary>
    /// Hierarchical pathfinding: pick a corridor of connectivity regions first, then run tile A* only inside
    /// it. Keeps long trips across hilly terrain from exploring the whole map.
    /// </summary>
    /// <para>
    /// Long trips go in legs of <see cref="LegRegions"/> regions: the path only reaches the region that many
    /// steps along the chain, and the next leg is planned on arrival (<see cref="_plannedTrip"/> says so). A trip
    /// across the map would otherwise be one search of tens of thousands of nodes (a frame-long stall), and a
    /// failed one, retried whenever the world changed, a stall that repeats while the colonist never sets off.
    /// </para>
    private List<TilePos>? PlanPath(ColonySim sim, HashSet<TilePos> goals, TilePos near) => PlanPath(sim, goals, near, Position, _aboard ? _vehicle : null);

    /// <summary>
    /// The same from any cell, for a colonist or (<paramref name="machine"/>) a machine: tiles other machines stand on or
    /// are stepping into are walls to a machine (heavy machinery never shares a tile), and never goals.
    /// </summary>
    private List<TilePos>? PlanPath(ColonySim sim, HashSet<TilePos> goals, TilePos near, TilePos from, Colonist? machine)
    {
        _plannedTrip = null;
        var blocked = machine != null ? OtherMachineCells(sim, machine) : null;
        if (blocked is { Count: > 0 })
        {
            goals = goals.Where(g => !blocked.Contains(g)).ToHashSet();
            if (goals.Count == 0) return null;
        }
        bool Free(TilePos p) => blocked is null || !blocked.Contains(p);
        var connectivity = sim.Connectivity;
        var chain = connectivity.RegionChain(from, goals);
        if (chain is null) return null;
        if (chain.Count > LegRegions + LegRegions / 2)
        {
            int stop = chain[LegRegions];
            var leg = connectivity.Widen(chain, 0, LegRegions + 1);
            var (lmin, lmax) = connectivity.RegionBounds(leg);
            const int lpad = 2;
            var lbox = TileBox.Capture(sim.World, lmin.X - lpad, lmin.Y - lpad, lmin.Z - lpad,
                lmax.X - lmin.X + 2 * lpad, lmax.Y - lmin.Y + 2 * lpad, lmax.Z - lmin.Z + 2 * lpad);
            var path = Pathfinder.FindPathToward(lbox, sim.World.Width, sim.World.Depth, from,
                p => connectivity.RegionOf(p) == stop && Free(p), near, LegMaxNodes, p => leg.Contains(connectivity.RegionOf(p)) && Free(p));
            if (path is not null) _plannedTrip = (goals, near);
            return path;
        }
        var corridor = connectivity.Widen(chain, 0, chain.Count);
        bool InCorridor(TilePos p) => corridor.Contains(connectivity.RegionOf(p)) && Free(p);

        // Search a snapshot of the corridor's bounding box when it is small enough: reads become plain array
        // lookups instead of chunk lookups. Moves read 1 tile around and 2 below a cell, hence the padding.
        var (min, max) = connectivity.RegionBounds(corridor);
        const int pad = 2;
        long volume = (long)(max.X - min.X + 2 * pad) * (max.Y - min.Y + 2 * pad) * (max.Z - min.Z + 2 * pad);
        // Copying costs about as much as a few thousand node expansions, so only long trips (many regions)
        // are worth it; short hops search the live world directly.
        if (corridor.Count >= MinSnapshotRegions && volume <= MaxSnapshotTiles)
        {
            var box = TileBox.Capture(sim.World, min.X - pad, min.Y - pad, min.Z - pad,
                max.X - min.X + 2 * pad, max.Y - min.Y + 2 * pad, max.Z - min.Z + 2 * pad);
            return Pathfinder.FindPathToAny(box, sim.World.Width, sim.World.Depth, from, goals, near, allowed: InCorridor);
        }
        return Pathfinder.FindPathToAny(sim.World, from, goals, near, allowed: InCorridor);
    }

    /// <summary>Regions per leg of a long trip (trips up to 1.5x this go in one search).</summary>
    private const int LegRegions = 10;
    private const int LegMaxNodes = 12_000;

    /// <summary>Largest corridor bounding box (in tiles) worth copying into a snapshot for pathfinding.</summary>
    private const long MaxSnapshotTiles = 2_000_000;
    /// <summary>Corridors with fewer regions than this are searched on the live world.</summary>
    private const int MinSnapshotRegions = 24;

    /// <summary>If the floor was dug away, drop to the next solid ground.</summary>
    private void ApplyGravity(ColonySim sim)
    {
        var w = sim.World;
        if (Movement.IsStandable(w, Position)) return;

        var p = Position;
        if (w.IsSolid(p.X, p.Y, p.Z))
        {
            // Entombed (shouldn't happen through normal play): pop up to the first open space.
            while (p.Z < w.Height - 1 && !Movement.IsStandable(w, p)) p = p.Offset(0, 0, 1);
        }
        else
        {
            while (p.Z > 1 && !Movement.IsStandable(w, p)) p = p.Offset(0, 0, -1); // stairs catch a fall
        }

        if (p == Position) return;
        Position = p;
        Abandon(sim);
    }

    // ---- Snapshot -----------------------------------------------------------------------------------------

    /// <summary>
    /// Puts down whatever it was doing (claims released, any load dropped where it stands) and forgets its search
    /// caches: what is left is the durable state a snapshot holds, so the live game goes on exactly as a loaded one.
    /// </summary>
    internal void Settle(ColonySim sim)
    {
        if (Task != ColonistTask.None || State != ColonistState.Idle || CarryingCount > 0 || _carried.Count > 0) Abandon(sim);
        _path.Clear();
        _trip = _plannedTrip = null;
        _stepTicksTotal = _stepTicksDone = 0;
        _searchedDesignations = _searchedConnectivity = _searchedItems = _searchedStockpiles = -1;
        _searchedRooms = _searchedHousing = _searchedTrades = _searchedBelts = -1;
        _trade = default;
        _tradePartner = "";
        _retryDelay = 0;
        _deferred = false;
        _pathFailed.Clear();
        _pathFailedConnectivity = -1;
        _haul = default;
        _craft = default;
        _build = default;
        _fetchIndex = 0;
        _jobKind = default;
        _foodRetry = 0;
        _sleepRate = 0;
        _meal = default;
        _furnish = default;
        _haulDestination = "stockpile";
        _scaffold = ScaffoldStep.None;
        _scaffoldSource = _scaffoldJob = default;
        SettleMachine(); // still in a machine it was driving (that is durable); a machine it was walking to was let go above
        if (_aboard) SyncVehicle();
        SettleCombat();
        JobTarget = null;
        NextStep = null;
        WorkDone = WorkRequired = 0;
        State = ColonistState.Idle;
        Task = ColonistTask.None;
    }

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(Id); w.Write(Name); Net.SnapIO.Write(w, Position); w.Write(Education); w.Write(IsAutomaton); w.Write(Faction);
        w.Write(_priority);
        w.Write(Satiety); w.Write(StarvingTicks); w.Write(Household); w.Write(Spouse?.Id ?? -1); w.Write(IsChild); w.Write(BornTick);
        w.Write(Happiness); w.Write(UnhappyDays); w.Write(Rest); w.Write(LastTreat); w.Write(WorkTicks); w.Write(PreferredTicks);
        w.Write(CarryingType.Index);
        SnapSaveCombat(w);
        w.Write((byte)Machine);
        w.Write(Fuel);
        w.Write(MistSweeps);
        w.Write(_aboard ? _vehicle!.Id : -1);
    }

    /// <summary>Reads a colonist; its spouse is linked afterwards (by id) and its home by the housing.</summary>
    internal static (Colonist Colonist, int SpouseId) SnapLoad(BinaryReader r)
    {
        int id = r.ReadInt32();
        string name = r.ReadString();
        var pos = Net.SnapIO.ReadPos(r);
        int education = r.ReadInt32();
        bool automaton = r.ReadBoolean();
        int faction = r.ReadInt32();
        var c = new Colonist(id, name, pos, education) { IsAutomaton = automaton, Faction = faction };
        if (Net.SnapIO.Before(4)) r.BaseStream.ReadExactly(c._priority.AsSpan(0, (int)Work.Trading)); // before trading; it keeps its default
        else r.BaseStream.ReadExactly(c._priority);
        c.Satiety = r.ReadInt32(); c.StarvingTicks = r.ReadInt32(); c.Household = r.ReadInt32();
        int spouse = r.ReadInt32();
        c.IsChild = r.ReadBoolean(); c.BornTick = r.ReadInt64();
        c.Happiness = r.ReadSingle(); c.UnhappyDays = r.ReadInt32(); c.Rest = r.ReadSingle(); c.LastTreat = r.ReadInt64();
        c.WorkTicks = r.ReadInt32(); c.PreferredTicks = r.ReadInt32();
        c.CarryingType = new ItemType(r.ReadUInt16());
        c.SnapLoadCombat(r);
        if (!Net.SnapIO.Before(9))
        {
            c.Machine = (MachineKind)r.ReadByte();
            c.Fuel = r.ReadInt32();
        }
        if (!Net.SnapIO.Before(11)) c.MistSweeps = r.ReadInt32();
        if (!Net.SnapIO.Before(12)) c._loadedVehicle = r.ReadInt32(); // linked by id once all are read (LinkVehicle)
        return (c, spouse);
    }
}
