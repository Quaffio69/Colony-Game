using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Navigation;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Agents;

public enum ColonistState { Idle, Moving, Working }

/// <summary>What a colonist is doing beyond walking: a designation, one leg of a haul trip, or factory work.</summary>
public enum ColonistTask { None, Designation, HaulPickup, HaulDeliver, CraftFetch, CraftWork, CraftStore, Build }

/// <summary>Kinds of work, in the order a colonist prefers them (see <see cref="Colonist.Labors"/>).</summary>
public enum Labor { Designations, Hauling, Industry }

/// <summary>
/// A colony member. When idle it looks for work in its own connectivity component (so unreachable work never
/// costs a search): designations from the <see cref="JobBoard"/> (dig, carve, chop), or haul trips that carry
/// loose item piles to stockpiles (<see cref="Hauling"/>). Some colonists prefer hauling, the rest prefer
/// designations, until proper job priorities exist. Every reservation it takes is released on completion or
/// <see cref="Abandon"/>, and anything it carries is dropped rather than lost.
/// </summary>
public sealed class Colonist
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
    private CraftPlan _craft;
    private BuildPlan _build;

    /// <summary>Putting up a blueprint from the materials reserved on its footprint.</summary>
    private readonly record struct BuildPlan(Part? Part, Blueprint? Site, List<(TilePos Cell, int Amount)> Materials)
    {
        public string Name => Part?.Name ?? Constructions.Name(Site!.Kind);
        public string Where => Part is { } p ? $" in {p.Room.Name}" : "";
        public bool Removed => Part?.Removed ?? Site!.Removed;
        public BuildCost Cost => Part?.Cost ?? Site!.Cost;
        public int Ticks => Part is { } p ? PartInfo.BuildTicks(p.Type, p.Size) : Constructions.BuildTicks(Site!.Kind);
        public bool InReach(TilePos at) => Part is { } p ? p.Cells.Any(c => Colonist.InReach(at, c)) : Colonist.InReach(at, Site!.Cell);
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
    /// Work kinds in preference order. Rotates by id so a third of the colony leans to each kind, and everyone
    /// falls back on the others; real job priorities will replace this.
    /// </summary>
    public Labor[] Labors => (Id % 3) switch
    {
        0 => [Labor.Designations, Labor.Hauling, Labor.Industry],
        1 => [Labor.Industry, Labor.Designations, Labor.Hauling],
        _ => [Labor.Hauling, Labor.Industry, Labor.Designations],
    };

    /// <summary>
    /// Index into <see cref="Catalog.EducationLevels"/> (0 = Unskilled). A colonist can run recipes at or below
    /// it; the Academy raises it one level at a time.
    /// </summary>
    public int Education { get; internal set; }
    /// <summary>Built, not born: a tireless unskilled worker that can't be taught.</summary>
    public bool IsAutomaton { get; init; }
    public string EducationName => IsAutomaton ? "Automaton" : Catalog.EducationName(Education);

    public Colonist(int id, string name, TilePos position, int education = 0)
    {
        Id = id;
        Name = name;
        Position = position;
        Education = education;
    }

    public string Describe() => (Task, State) switch
    {
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
        _ => "idle",
    };

    private string _haulDestination = "stockpile";

    /// <summary>Tiles a colonist standing at <paramref name="at"/> can work on: all 26 neighbours.</summary>
    public static bool InReach(TilePos at, TilePos target) =>
        Math.Abs(at.X - target.X) <= 1 && Math.Abs(at.Y - target.Y) <= 1 && Math.Abs(at.Z - target.Z) <= 1;

    internal void Tick(ColonySim sim)
    {
        ApplyGravity(sim);

        if (Task == ColonistTask.Designation && JobTarget is { } job && !sim.Designations.IsClaimedBy(job, Id))
            Abandon(sim); // cancelled, re-designated, or completed by someone else
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
                if (NothingNewSinceLastSearch(sim)) return;
                FindWork(sim);
                break;

            case ColonistState.Moving:
                if (NextStep is null)
                {
                    if (_path.Count == 0 && _trip is { } trip)
                    {
                        // End of a leg: plan the next one (when the shared path budget allows).
                        if (sim.PathBudgetExhausted) return;
                        sim.PathSearches++;
                        var leg = PlanPath(sim, trip.Goals, trip.Near);
                        _trip = _plannedTrip;
                        if (leg is null)
                        {
                            Abandon(sim);
                            return;
                        }
                        foreach (var p in leg) _path.Enqueue(p);
                    }
                    if (_path.Count == 0)
                    {
                        Arrive(sim);
                        return;
                    }
                    var next = _path.Dequeue();
                    if (Movement.MoveCost(sim.World, Position, next) is not { } cost)
                    {
                        Abandon(sim); // terrain changed under the path; replan
                        return;
                    }
                    NextStep = next;
                    _stepTicksTotal = cost * TicksPerFlatStep;
                    _stepTicksDone = 0;
                }

                _stepTicksDone++;
                if (_stepTicksDone >= _stepTicksTotal)
                {
                    Position = NextStep!.Value;
                    NextStep = null;
                    if (_path.Count == 0 && _trip is null) Arrive(sim);
                }
                break;

            case ColonistState.Working:
                if (++WorkDone < WorkRequired) return;
                switch (Task)
                {
                    case ColonistTask.Designation:
                        if (!InReach(Position, JobTarget!.Value))
                        {
                            Abandon(sim);
                            return;
                        }
                        sim.CompleteDesignation(JobTarget!.Value);
                        Finish();
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
                }
                break;
        }
    }

    // ---- Finding work ------------------------------------------------------------------------------------

    private bool NothingNewSinceLastSearch(ColonySim sim) =>
        _searchedDesignations == sim.Designations.Version && _searchedConnectivity == sim.Connectivity.Version &&
        _searchedItems == sim.Items.Version && _searchedStockpiles == sim.Stockpiles.Version &&
        _searchedRooms == sim.Rooms.Version;

    private int _searchedRooms = -1;

    private void FindWork(ColonySim sim)
    {
        // Path searches are the expensive part of the sim; spread them over ticks rather than stacking them.
        if (sim.PathBudgetExhausted) return;
        _searchedDesignations = sim.Designations.Version;
        _searchedConnectivity = sim.Connectivity.Version;
        _searchedItems = sim.Items.Version;
        _searchedStockpiles = sim.Stockpiles.Version;
        _searchedRooms = sim.Rooms.Version;
        _deferred = false;

        if (_pathFailedConnectivity != sim.Connectivity.Version)
        {
            _pathFailed.Clear();
            _pathFailedConnectivity = sim.Connectivity.Version;
        }

        int component = sim.Connectivity.ComponentOf(Position);
        if (component < 0) return;

        foreach (var labor in Labors)
        {
            bool found = labor switch
            {
                Labor.Designations => TryTakeJob(sim, component) || (!_deferred && TryBuild(sim, component)),
                Labor.Hauling => TryHaul(sim, component),
                _ => TryCraft(sim, component),
            };
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

    private bool TryTakeJob(ColonySim sim, int component)
    {
        var orders = sim.Designations;
        foreach (var job in sim.JobBoard.Candidates(component, Position, orders, CandidatesPerSearch, _pathFailed))
        {
            if (sim.PathBudgetExhausted)
            {
                _deferred = true;
                return false;
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
            if (!orders.Claim(job.Target, Id)) continue;

            Begin(ColonistTask.Designation, job.Target, path);
            return true;
        }
        return false;
    }

    private bool TryHaul(ColonySim sim, int component)
    {
        foreach (var plan in Hauling.Plans(sim.Items, sim.Stockpiles, sim.Rooms, sim.Connectivity, Position, component, _pathFailed, sim.Constructions))
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
            if (!sim.Items.Reserve(plan.Source, plan.Amount)) continue;
            if (!sim.Items.ReserveIncoming(plan.Destination, plan.Type, plan.Amount))
            {
                sim.Items.Unreserve(plan.Source, plan.Amount);
                continue;
            }
            _haul = plan;
            _haulDestination = sim.Rooms.PartAt(plan.Destination) switch
            {
                { Built: false } => "a construction site",
                { Role: PartRole.Input } => "a factory",
                _ => "stockpile",
            };
            Begin(ColonistTask.HaulPickup, plan.Source, path);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Factory work: a free machine (biggest first) whose recipe this colonist is educated enough for, in a room
    /// whose input crates hold a batch of every input and whose output crate has room for the products
    /// (education and worker recipes have no products to store). Reserves everything, then walks to the first
    /// input crate.
    /// </summary>
    private bool TryCraft(ColonySim sim, int component)
    {
        foreach (var room in sim.Rooms.All)
        foreach (var machine in FreeMachinesBiggestFirst(room))
        {
            var recipe = machine.Recipe!;
            if (recipe.Education > Education || IsAutomaton && recipe.Kind == ItemKind.Education) continue;
            if (MachineSpots(sim, machine, component).Count == 0) continue;
            var full = machine.Batch;
            int maxUnits = full.Units;
            if (recipe.Kind == ItemKind.Education) maxUnits = Math.Min(maxUnits, sim.StudentsAvailable(recipe));
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
                foreach (var crate in room.PartsWithRole(PartRole.Output))
                {
                    var c = crate.Cells[0];
                    if (sim.Items.RoomAt(c, recipe.Output) >= batch.OutCount && sim.Connectivity.ComponentOf(c) == component)
                    { output = c; break; }
                }
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
            if (recipe.Kind == ItemKind.Education) sim.ReserveStudents(recipe, batch.Units);
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
        foreach (var room in sim.Rooms.All)
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
        foreach (var site in sim.Constructions.All)
        {
            if (site.BuilderId >= 0) continue;
            var (_, cost) = site.Cost;
            if (Constructions.MaterialsOnSite(site, sim.Items, available: true) < cost) continue;
            // Never stand in the cell being built (a wall would entomb the builder).
            var spots = ReachableAround(sim, site.Cell, component);
            spots.Remove(site.Cell);
            if (spots.Count == 0) continue;
            if (sim.PathBudgetExhausted)
            {
                _deferred = true;
                return false;
            }
            var path = Route(sim, spots, site.Cell);
            if (path is null)
            {
                _pathFailed.Add(site.Cell);
                continue;
            }
            if (!sim.Items.Reserve(site.Cell, cost)) continue;
            site.BuilderId = Id;
            _build = new BuildPlan(null, site, [(site.Cell, cost)]);
            Begin(ColonistTask.Build, site.Cell, path);
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
                sim.Graduate(batch.Recipe, batch.Units);
                Finish();
                return;
            case ItemKind.Worker:
                sim.AddWorkers(batch.Recipe, batch.Units, Position);
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
            _ => InReach(Position, target),
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
            case ColonistTask.Designation:
                if (!sim.Designations.TryGet(target, out var kind))
                {
                    Abandon(sim);
                    return;
                }
                StartWorking(sim.WorkTicks(target, kind));
                break;
            case ColonistTask.HaulPickup:
            case ColonistTask.HaulDeliver:
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
        WorkRequired = Math.Max(1, ticks);
    }

    private void PickUp(ColonySim sim)
    {
        int taken = sim.Items.TakeReserved(_haul.Source, _haul.Amount);
        if (taken < _haul.Amount) sim.Items.ReleaseIncoming(_haul.Destination, _haul.Amount - taken);
        if (taken == 0)
        {
            Finish(); // someone or something moved the pile; nothing to carry
            return;
        }
        CarryingType = _haul.Type;
        CarryingCount = taken;
        _haul = _haul with { Amount = taken };

        int component = sim.Connectivity.ComponentOf(Position);
        var path = Route(sim, ReachableAround(sim, _haul.Destination, component), _haul.Destination);
        if (path is null)
        {
            Abandon(sim); // drops the load here; it'll be hauled again from wherever it lands
            return;
        }
        Begin(ColonistTask.HaulDeliver, _haul.Destination, path);
    }

    private void Deliver(ColonySim sim) => Deliver(sim, _haul.Destination);

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
        _trip = null;
        Task = ColonistTask.None;
        JobTarget = null;
        State = ColonistState.Idle;
        _searchedDesignations = -1;
    }

    /// <summary>Gives up the current task, releasing every claim and reservation and dropping any load.</summary>
    private void Abandon(ColonySim sim)
    {
        switch (Task)
        {
            case ColonistTask.Designation when JobTarget is { } t:
                sim.Designations.Release(t, Id);
                break;
            case ColonistTask.HaulPickup:
                sim.Items.Unreserve(_haul.Source, _haul.Amount);
                sim.Items.ReleaseIncoming(_haul.Destination, _haul.Amount);
                break;
            case ColonistTask.HaulDeliver:
                sim.Items.ReleaseIncoming(_haul.Destination, _haul.Amount);
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
        }
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
        if (_craft.Batch.Recipe.Kind == ItemKind.Education) sim.ReleaseStudents(_craft.Batch.Recipe, _craft.Batch.Units);
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
    private List<TilePos>? PlanPath(ColonySim sim, HashSet<TilePos> goals, TilePos near)
    {
        _plannedTrip = null;
        var connectivity = sim.Connectivity;
        var chain = connectivity.RegionChain(Position, goals);
        if (chain is null) return null;
        if (chain.Count > LegRegions + LegRegions / 2)
        {
            int stop = chain[LegRegions];
            var leg = connectivity.Widen(chain, 0, LegRegions + 1);
            var (lmin, lmax) = connectivity.RegionBounds(leg);
            const int lpad = 2;
            var lbox = TileBox.Capture(sim.World, lmin.X - lpad, lmin.Y - lpad, lmin.Z - lpad,
                lmax.X - lmin.X + 2 * lpad, lmax.Y - lmin.Y + 2 * lpad, lmax.Z - lmin.Z + 2 * lpad);
            var path = Pathfinder.FindPathToward(lbox, sim.World.Width, sim.World.Depth, Position,
                p => connectivity.RegionOf(p) == stop, near, LegMaxNodes, p => leg.Contains(connectivity.RegionOf(p)));
            if (path is not null) _plannedTrip = (goals, near);
            return path;
        }
        var corridor = connectivity.Widen(chain, 0, chain.Count);
        bool InCorridor(TilePos p) => corridor.Contains(connectivity.RegionOf(p));

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
            return Pathfinder.FindPathToAny(box, sim.World.Width, sim.World.Depth, Position, goals, near, allowed: InCorridor);
        }
        return Pathfinder.FindPathToAny(sim.World, Position, goals, near, allowed: InCorridor);
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
}
