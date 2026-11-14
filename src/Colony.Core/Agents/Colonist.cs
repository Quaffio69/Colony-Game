using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Navigation;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Agents;

public enum ColonistState { Idle, Moving, Working }

/// <summary>What a colonist is doing beyond walking: a designation, one leg of a haul trip, or factory work.</summary>
public enum ColonistTask { None, Designation, HaulPickup, HaulDeliver, CraftFetch, CraftWork, CraftStore }

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

    /// <summary>One production cycle: input crate -> machine -> output crate.</summary>
    private readonly record struct CraftPlan(Part Machine, TilePos InputCell, TilePos OutputCell, Recipe Recipe);

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

    public Colonist(int id, string name, TilePos position)
    {
        Id = id;
        Name = name;
        Position = position;
    }

    public string Describe() => (Task, State) switch
    {
        (ColonistTask.Designation, ColonistState.Moving) => $"walking to job at {JobTarget}",
        (ColonistTask.Designation, ColonistState.Working) =>
            $"working at {JobTarget} ({WorkDone * 100 / Math.Max(1, WorkRequired)}%)",
        (ColonistTask.HaulPickup, _) => $"fetching {ItemInfo.Name(_haul.Type).ToLowerInvariant()} at {_haul.Source}",
        (ColonistTask.HaulDeliver, _) => $"hauling {CarryingCount} {ItemInfo.Name(CarryingType).ToLowerInvariant()} to {(_haulToFactory ? "a factory" : "stockpile")}",
        (ColonistTask.CraftFetch, _) => $"fetching {ItemInfo.Name(_craft.Recipe.Input).ToLowerInvariant()} for the {_craft.Machine.Name.ToLowerInvariant()}",
        (ColonistTask.CraftWork, ColonistState.Working) =>
            $"working the {_craft.Machine.Name.ToLowerInvariant()} in {_craft.Machine.Room.Name} ({WorkDone * 100 / Math.Max(1, WorkRequired)}%)",
        (ColonistTask.CraftWork, _) => $"carrying {ItemInfo.Name(CarryingType).ToLowerInvariant()} to the {_craft.Machine.Name.ToLowerInvariant()}",
        (ColonistTask.CraftStore, _) => $"storing {CarryingCount} {ItemInfo.Name(CarryingType).ToLowerInvariant()}",
        _ => "idle",
    };

    private string Destination(TilePos p) => _haulToFactory ? "a factory" : "stockpile";
    private bool _haulToFactory;

    /// <summary>Tiles a colonist standing at <paramref name="at"/> can work on: all 26 neighbours.</summary>
    public static bool InReach(TilePos at, TilePos target) =>
        Math.Abs(at.X - target.X) <= 1 && Math.Abs(at.Y - target.Y) <= 1 && Math.Abs(at.Z - target.Z) <= 1;

    internal void Tick(ColonySim sim)
    {
        ApplyGravity(sim);

        if (Task == ColonistTask.Designation && JobTarget is { } job && !sim.Designations.IsClaimedBy(job, Id))
            Abandon(sim); // cancelled, re-designated, or completed by someone else
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
                    if (_path.Count == 0) Arrive(sim);
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
                        Deliver(sim, _craft.OutputCell);
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
                Labor.Designations => TryTakeJob(sim, component),
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
        foreach (var plan in Hauling.Plans(sim.Items, sim.Stockpiles, sim.Rooms, sim.Connectivity, Position, component, _pathFailed))
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
            _haulToFactory = sim.Rooms.PartAt(plan.Destination)?.Role == PartRole.Input;
            Begin(ColonistTask.HaulPickup, plan.Source, path);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Factory work: a free machine (biggest first) in a room whose input crate holds a batch for it and whose
    /// output crate has room for the products. Reserves all three, then walks to the input crate.
    /// </summary>
    private bool TryCraft(ColonySim sim, int component)
    {
        foreach (var room in sim.Rooms.All)
        foreach (var machine in FreeMachinesBiggestFirst(room))
        {
            if (MachineSpots(sim, machine, component).Count == 0) continue;
            // A full batch if any crate holds one; otherwise whatever is left in a crate nobody is refilling
            // (so the last logs still get sawn), in proportionally less time.
            var full = machine.Batch;
            int unitIn = room.Recipe.InCount, batchUnits = full.InCount / unitIn;
            TilePos? input = null, output = null, partialCell = null;
            int partialUnits = 0;
            foreach (var crate in room.PartsWithRole(PartRole.Input))
            {
                var c = crate.Cells[0];
                if (!sim.Items.TryGet(c, out var pile) || pile.Type != full.Input || sim.Connectivity.ComponentOf(c) != component)
                    continue;
                int units = Math.Min(batchUnits, pile.Available / unitIn);
                if (units == batchUnits) { input = c; break; }
                if (units > partialUnits && pile.Incoming == 0) (partialCell, partialUnits) = (c, units);
            }
            var recipe = full;
            if (input is null && partialCell is not null)
            {
                input = partialCell;
                recipe = PartInfo.PartialBatch(full, partialUnits, batchUnits);
            }
            foreach (var crate in room.PartsWithRole(PartRole.Output))
            {
                var c = crate.Cells[0];
                if (sim.Items.RoomAt(c, recipe.Output) >= recipe.OutCount && sim.Connectivity.ComponentOf(c) == component)
                { output = c; break; }
            }
            if (input is not { } inCell || output is not { } outCell) continue;
            if (sim.PathBudgetExhausted)
            {
                _deferred = true;
                return false;
            }

            var path = Route(sim, ReachableAround(sim, inCell, component), inCell);
            if (path is null)
            {
                _pathFailed.Add(inCell);
                continue;
            }
            if (!sim.Items.Reserve(inCell, recipe.InCount)) continue;
            if (!sim.Items.ReserveIncoming(outCell, recipe.Output, recipe.OutCount))
            {
                sim.Items.Unreserve(inCell, recipe.InCount);
                continue;
            }
            machine.ClaimedBy = Id;
            _craft = new CraftPlan(machine, inCell, outCell, recipe);
            Begin(ColonistTask.CraftFetch, inCell, path);
            return true;
        }
        return false;
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

    private void CraftPickUp(ColonySim sim)
    {
        int taken = sim.Items.TakeReserved(_craft.InputCell, _craft.Recipe.InCount);
        // The input reservation is used up now: from here on, cleanup is the work phase's (see Abandon).
        Task = ColonistTask.CraftWork;
        CarryingType = _craft.Recipe.Input;
        CarryingCount = taken;
        if (taken < _craft.Recipe.InCount)
        {
            Abandon(sim); // someone or something took the batch; drops what we got
            return;
        }
        int component = sim.Connectivity.ComponentOf(Position);
        var path = Route(sim, MachineSpots(sim, _craft.Machine, component), _craft.Machine.Origin);
        if (path is null)
        {
            Abandon(sim);
            return;
        }
        Begin(ColonistTask.CraftWork, _craft.Machine.Origin, path);
    }

    /// <summary>Batch processed: the input is used up and the products are in hand.</summary>
    private void CraftFinish(ColonySim sim)
    {
        _craft.Machine.Working = false;
        _craft.Machine.ClaimedBy = -1; // free for the next worker while we carry the products away
        CarryingType = _craft.Recipe.Output;
        CarryingCount = _craft.Recipe.OutCount;
        sim.ItemsProduced += _craft.Recipe.OutCount;

        int component = sim.Connectivity.ComponentOf(Position);
        var path = Route(sim, ReachableAround(sim, _craft.OutputCell, component), _craft.OutputCell);
        if (path is null)
        {
            Abandon(sim);
            return;
        }
        Begin(ColonistTask.CraftStore, _craft.OutputCell, path);
    }

    private void Begin(ColonistTask task, TilePos target, List<TilePos> path)
    {
        Task = task;
        JobTarget = target;
        _path.Clear();
        foreach (var p in path) _path.Enqueue(p);
        State = ColonistState.Moving;
    }

    /// <summary>A path to any of <paramref name="goals"/> (empty if already at one), or null.</summary>
    private List<TilePos>? Route(ColonySim sim, HashSet<TilePos> goals, TilePos near)
    {
        if (goals.Count == 0) return null;
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
        bool inReach = Task == ColonistTask.CraftWork
            ? _craft.Machine.Cells.Any(c => InReach(Position, c)) // machines can be several tiles long
            : InReach(Position, target);
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
                StartWorking((int)MathF.Round(_craft.Recipe.WorkTicks / _craft.Machine.Room.Efficiency));
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
                sim.Items.Unreserve(_craft.InputCell, _craft.Recipe.InCount);
                sim.Items.ReleaseIncoming(_craft.OutputCell, _craft.Recipe.OutCount);
                ReleaseMachine();
                break;
            case ColonistTask.CraftWork:
                sim.Items.ReleaseIncoming(_craft.OutputCell, _craft.Recipe.OutCount);
                ReleaseMachine();
                break;
            case ColonistTask.CraftStore:
                sim.Items.ReleaseIncoming(_craft.OutputCell, _craft.Recipe.OutCount);
                break;
        }
        if (CarryingCount > 0)
        {
            sim.Items.Drop(Position, CarryingType, CarryingCount);
            CarryingCount = 0;
        }
        _path.Clear();
        NextStep = null;
        Finish();
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
    private List<TilePos>? PlanPath(ColonySim sim, HashSet<TilePos> goals, TilePos near)
    {
        var connectivity = sim.Connectivity;
        var corridor = connectivity.Corridor(Position, goals);
        if (corridor is null) return null;
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
