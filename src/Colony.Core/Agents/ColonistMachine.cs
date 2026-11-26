using Colony.Core.Items;
using Colony.Core.Navigation;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Agents;

/// <summary>Heavy machinery: steam engines built at the artificer's assembly frame and driven by colonists.</summary>
public enum MachineKind : byte
{
    None,
    /// <summary>A tracked steam excavator with a boom, stick and bucket: its driver digs and carves stairs several times faster.</summary>
    Excavator,
    /// <summary>A steam power wagon: its driver hauls many loads a trip, unloading into the stockpile cells round the stop.</summary>
    PowerWagon,
}

public static class MachineInfo
{
    /// <summary>The machine a catalog item becomes (by id), or None.</summary>
    public static MachineKind Of(string itemId) => itemId switch
    {
        "excavator" => MachineKind.Excavator,
        "power_wagon" => MachineKind.PowerWagon,
        _ => MachineKind.None,
    };

    public static string Name(MachineKind k) => k switch
    {
        MachineKind.Excavator => "Excavator",
        MachineKind.PowerWagon => "Power wagon",
        _ => "",
    };

    /// <summary>The work a machine is driven for.</summary>
    public static Work WorkOf(MachineKind k) => k == MachineKind.Excavator ? Work.Mining : Work.Hauling;

    /// <summary>Ticks to move a step, as a multiple of walking: the excavator crawls on its tracks, the wagon rolls.</summary>
    public static float StepScale(MachineKind k) => k switch
    {
        MachineKind.Excavator => Balance.ExcavatorStepScale,
        MachineKind.PowerWagon => Balance.WagonStepScale,
        _ => 1f,
    };
}

/// <summary>
/// Machines are driven. A machine is an entity of its own (fuel, a place it is parked) that never looks for work; a
/// colonist taking a job may first walk to a free machine, climb in and drive it to the work, like a scaffold fetched
/// for a high job. While aboard, the driver moves at the machine's pace and works with it (the excavator digs faster,
/// the wagon carries more), keeps driving from job to job while that still pays, stokes it with charcoal when it runs
/// low, and parks it where it stands when the work runs out or it has to eat, sleep, fight or answer a draft.
/// <para>
/// Whether a machine is worth fetching is judged on the work it would do before its driver gets out, not on one job:
/// fetching an excavator across the map for a single tile never pays, while a big excavation pays for a long walk.
/// The same test (without the climb in) decides whether a driver drives on to the next job or gets out, so the two
/// never disagree and a colonist doesn't hop in and out.
/// </para>
/// </summary>
public sealed partial class Colonist
{
    /// <summary>What machine this is (None for people and automatons). Machines are automatons: no needs, homes or families.</summary>
    public MachineKind Machine { get; internal set; }

    public bool IsMachine => Machine != MachineKind.None;

    /// <summary>Fuel in the firebox, in units of work (see <see cref="Balance.FuelPerCharcoal"/>); at 0 its driver parks it until charcoal is found.</summary>
    public int Fuel { get; internal set; }

    /// <summary>Fuel when full.</summary>
    public static int FuelCapacity => Balance.FuelCharcoalLoad * Balance.FuelPerCharcoal;

    public bool LowOnFuel => IsMachine && Fuel < FuelCapacity / 4;
    public bool OutOfFuel => IsMachine && Fuel <= 0;

    /// <summary>Sets a new machine up: full of fuel, and no work of its own (it is driven).</summary>
    internal void SetUpMachine()
    {
        Fuel = FuelCapacity;
        for (int w = 0; w < WorkTypes; w++) _priority[w] = 0;
    }

    // ---- Driving ------------------------------------------------------------------------------------------------

    /// <summary>The machine it is driving (or, before it climbs in, walking to); its claim on that machine.</summary>
    private Colonist? _vehicle;
    private bool _aboard;
    /// <summary>On a machine: the colonist coming to drive it or driving it.</summary>
    private Colonist? _claim;
    /// <summary>What it boards a machine for: a designation (claimed) or a haul (reserved in <see cref="_haul"/>).</summary>
    private ColonistTask _boardThen;
    private TilePos _boardJob;
    /// <summary>Ticks it leaves machines be after getting out of one.</summary>
    private int _machineRest;
    private int _fuelRetry;
    private (TilePos Cell, int Units) _stoke;
    private int _loadedVehicle = -1;

    /// <summary>The machine it is driving (null on foot, and while still walking to one).</summary>
    public Colonist? Vehicle => _aboard ? _vehicle : null;

    /// <summary>On a machine: who is driving it (null while parked).</summary>
    public Colonist? Driver => _claim is { _aboard: true } d ? d : null;

    /// <summary>On a machine: who is walking over to drive it (null when nobody is).</summary>
    public Colonist? ComingToDrive => _claim is { _aboard: false } d ? d : null;

    /// <summary>The kind of machine it is driving, or None.</summary>
    private MachineKind Drives => _aboard ? _vehicle!.Machine : MachineKind.None;

    /// <summary>A machine's tick: it only falls if dug out from under, and gets back up when struck down. Its driver moves it.</summary>
    private void MachineTick(ColonySim sim)
    {
        if (_fuelRetry > 0) _fuelRetry--;
        if (Downed && sim.Tick % 30 == Id % 30 && ++Health >= StandUpHealth) Downed = false;
        if (Driver != null) return;
        if (Movement.IsStandable(sim.World, Position)) return;
        ApplyGravity(sim);
        if (sim.MachineStandsOn(Position, this)) Position = sim.FreeMachineCell(Position, this); // never lands on another
    }

    // ---- One machine to a tile ----------------------------------------------------------------------------------
    // Colonists may share a tile (and spread out when fighting); heavy machinery never does. A machine holds the tile
    // it stands on and, mid-step, the one it is stepping into. Drivers plan round other machines (PlanPath), and before
    // each step check the tile is still free, waiting a little for a machine in the way to move on before giving the
    // job up and planning again.

    /// <summary>Ticks a driver waits for a machine in its way before giving up the trip.</summary>
    public const int MachineWaitTicks = 20;
    private int _blockedFor;

    /// <summary>Whether a machine other than <paramref name="except"/> stands on <paramref name="cell"/> or is stepping into it.</summary>
    internal static bool MachineAt(ColonySim sim, TilePos cell, Colonist? except)
    {
        foreach (var m in sim.Machines)
            if (m != except && (m.Position == cell || m.NextStep == cell)) return true;
        return false;
    }

    /// <summary>The tiles every machine but <paramref name="me"/> holds.</summary>
    private static HashSet<TilePos>? OtherMachineCells(ColonySim sim, Colonist me)
    {
        HashSet<TilePos>? cells = null;
        foreach (var m in sim.Machines)
        {
            if (m == me) continue;
            (cells ??= new()).Add(m.Position);
            if (m.NextStep is { } n) cells.Add(n);
        }
        return cells;
    }

    /// <summary>Driving: whether the next step is held by another machine (it waits; false once it should give up).</summary>
    private bool WaitForMachineAt(ColonySim sim, TilePos next, out bool giveUp)
    {
        giveUp = false;
        if (!_aboard || !MachineAt(sim, next, _vehicle))
        {
            _blockedFor = 0;
            return false;
        }
        if (++_blockedFor > MachineWaitTicks)
        {
            _blockedFor = 0;
            giveUp = true;
        }
        return true;
    }

    /// <summary>After its own tick: a driver whose task has nothing to do with its machine gets out; otherwise the machine follows.</summary>
    private void CheckVehicle(ColonySim sim)
    {
        if (_machineRest > 0) _machineRest--;
        if (!_aboard) return;
        var v = _vehicle!;
        bool fits = Task switch
        {
            ColonistTask.None or ColonistTask.Stoke => true,
            ColonistTask.Designation => v.Machine == MachineKind.Excavator,
            ColonistTask.HaulPickup or ColonistTask.HaulDeliver or ColonistTask.TradePickup or ColonistTask.TradeDeliver => v.Machine == MachineKind.PowerWagon,
            _ => false, // a meal, bed, a fight, a draft...
        };
        if (!fits || Downed || Drafted || v.Downed) Park(sim);
        else
        {
            // Fell (or was dropped) onto another machine's tile: off to the nearest free one.
            if (NextStep == null && sim.MachineStandsOn(Position, v)) Position = sim.FreeMachineCell(Position, v);
            SyncVehicle();
        }
    }

    /// <summary>The machine stands (and is drawn) where its driver is, mid-step included.</summary>
    private void SyncVehicle()
    {
        var v = _vehicle!;
        v.Position = Position;
        v.NextStep = NextStep;
        v._stepTicksTotal = _stepTicksTotal;
        v._stepTicksDone = _stepTicksDone;
    }

    /// <summary>Gets out (the machine stays where it stands) or gives up its claim on one it was walking to.</summary>
    internal void Park(ColonySim sim)
    {
        if (_vehicle is not { } v) return;
        if (_aboard)
        {
            v.Position = Position;
            v.NextStep = null;
            v._stepTicksTotal = v._stepTicksDone = 0;
            _machineRest = Balance.MachineRestTicks;
        }
        if (v._claim == this) v._claim = null;
        _vehicle = null;
        _aboard = false;
    }

    /// <summary>Its machine is gone (broken up or left the colony): on foot from here.</summary>
    private void LoseVehicle(ColonySim sim)
    {
        if (Task == ColonistTask.Board) Abandon(sim); // releases the claim with the job
        _vehicle = null;
        _aboard = false;
    }

    /// <summary>A machine it may drive: its colony's, in its component, nobody else's, standing, and fuelled or fuel to be had.</summary>
    private bool MayDrive(ColonySim sim, Colonist m, int component) =>
        m._claim == null && !m.Downed && m.Faction == Faction && m._fuelRetry == 0
        && (m.Fuel > 0 || !sim.NeedsEnabled || sim.Items.UnitsOf(ItemType.Charcoal) > 0)
        && sim.Connectivity.ComponentOf(m.Position) == component;

    /// <summary>Whether it will fetch a machine at all now (people only, and not straight after getting out of one).</summary>
    private bool MayFetchMachine(ColonySim sim) => !IsMachine && !IsChild && !_aboard && _machineRest == 0 && sim.Machines.Count > 0;

    /// <summary>Claims a machine and walks to it, to drive it on to <paramref name="job"/> (a claimed designation or the reserved haul).</summary>
    private void BeginBoarding(Colonist m, ColonistTask then, TilePos job, List<TilePos> path)
    {
        m._claim = this;
        _vehicle = m;
        _aboard = false;
        _boardThen = then;
        _boardJob = job;
        Begin(ColonistTask.Board, m.Position, path);
    }

    /// <summary>Climbed in: drives to the job it fetched the machine for.</summary>
    private void FinishBoarding(ColonySim sim)
    {
        var v = _vehicle!;
        Position = v.Position;
        _aboard = true;
        SyncVehicle();
        int component = sim.Connectivity.ComponentOf(Position);
        if (_boardThen == ColonistTask.None)
        {
            Finish(); // fetched for a deal: aboard, it looks for its work again (DriveOn)
            return;
        }
        if (_boardThen == ColonistTask.HaulPickup) ExtendLoad(sim, component); // a wagon load: more of the pile, room round the stop
        var target = _boardThen == ColonistTask.HaulPickup ? _haul.Source : _boardJob;
        Task = _boardThen; // the job's own claims from here on (Abandon releases them as that task)
        JobTarget = target;
        var path = Route(sim, ReachableAround(sim, target, component), target);
        if (path is null)
        {
            Abandon(sim); // stays aboard; looks for other work
            return;
        }
        Begin(_boardThen, target, path);
    }

    /// <summary>Abandoning the walk to a machine: lets go of the job it was for.</summary>
    private void AbandonBoarding(ColonySim sim)
    {
        if (_boardThen == ColonistTask.Designation) sim.Designations.Release(_boardJob, Id);
        else if (_boardThen == ColonistTask.HaulPickup)
        {
            sim.Items.Unreserve(_haul.Source, _haul.Amount);
            sim.Items.ReleaseIncoming(_haul.Destination, _haul.Amount);
        }
    }

    /// <summary>
    /// A driver with work to do (the machine's own trade only: digging, or for the wagon hauling and shipping deals, in
    /// its priority order): false when there is none worth driving to.
    /// </summary>
    private bool DriveOn(ColonySim sim, int component)
    {
        if (_vehicle!.Machine == MachineKind.Excavator) return Does(Work.Mining) && TryTakeJob(sim, component, MiningKinds);
        bool trade = Does(Work.Trading), haul = Does(Work.Hauling);
        if (trade && (!haul || Priority(Work.Trading) <= Priority(Work.Hauling)))
            return TryTrade(sim, component) || haul && !_deferred && TryHaul(sim, component);
        return haul && TryHaul(sim, component) || trade && !_deferred && TryTrade(sim, component);
    }

    /// <summary>The designation kinds the excavator works (dig and stairs).</summary>
    private static readonly int MiningKinds = Enumerable.Range(0, (int)DesignationKind.Sow + 1)
        .Where(k => WorkOf((DesignationKind)k) == Work.Mining).Aggregate(0, (m, k) => m | 1 << k);

    /// <summary>Burns its machine's fuel for work done (only while needs run: sandboxes and tests are free).</summary>
    private void BurnFuel(ColonySim sim, int amount)
    {
        if (_aboard && sim.NeedsEnabled) _vehicle!.Fuel = Math.Max(0, _vehicle.Fuel - amount);
    }

    // ---- Is it worth it? ----------------------------------------------------------------------------------------
    // Costs in ticks, from straight-line steps (|dx| + |dy| + 2|dz|, as elsewhere): rough, but the same on both sides.

    private static int Steps(TilePos a, TilePos b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + 2 * Math.Abs(a.Z - b.Z);

    /// <summary>Walking <paramref name="walk"/> steps to a dig and digging <paramref name="jobs"/> like it by hand.</summary>
    internal static float HandDigCost(int walk, int jobs, int digTicks) => walk * TicksPerFlatStep + jobs * (float)digTicks;

    /// <summary>
    /// The same with the excavator: walking to it, climbing in, crawling it to the dig, then each job dug fast with a
    /// crawl to the next (a step at the excavator's pace instead of a walk).
    /// </summary>
    internal static float ExcavatorDigCost(int toMachine, int drive, int jobs, int digTicks, bool board) =>
        toMachine * TicksPerFlatStep + (board ? Balance.MachineBoardTicks : 0) + drive * TicksPerFlatStep * Balance.ExcavatorStepScale
        + jobs * (digTicks / Balance.ExcavatorDigSpeed + (Balance.ExcavatorStepScale - 1) * TicksPerFlatStep);

    /// <summary>Hauling <paramref name="units"/> over <paramref name="haul"/> steps in loads of <paramref name="load"/>, there and back between trips.</summary>
    private static float Trips(int units, int load, int haul, out int trips)
    {
        trips = Math.Max(1, (units + load - 1) / Math.Max(1, load));
        return (2 * trips - 1) * haul * TicksPerFlatStep;
    }

    internal static float HandHaulCost(int walk, int haul, int units, int load) =>
        walk * TicksPerFlatStep + Trips(units, load, haul, out int trips) + trips * 2 * HandleTicks;

    internal static float WagonHaulCost(int toWagon, int drive, int haul, int units, int load, bool board) =>
        toWagon * TicksPerFlatStep + (board ? Balance.MachineBoardTicks : 0)
        + Balance.WagonStepScale * (drive * TicksPerFlatStep + Trips(units, load, haul, out int trips)) + trips * 2 * HandleTicks;

    /// <summary>Open, unclaimed dig and stair orders round a cell (the job itself included), counted up to <paramref name="cap"/>.</summary>
    private static int MiningOrdersNear(ColonySim sim, TilePos at, int faction, int cap)
    {
        var d = sim.Designations;
        int n = 0, r = Balance.MachineClusterReach, l = Balance.ExcavatorClusterLevels;
        for (int dz = -l; dz <= l; dz++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            var p = at.Offset(dx, dy, dz);
            if (!d.TryGet(p, out var kind) || WorkOf(kind) != Work.Mining || d.IsClaimed(p)) continue;
            if (faction != Owners.Any && d.OwnerOf(p) != faction) continue;
            if (++n >= cap) return n;
        }
        return n;
    }

    /// <summary>The pile a haul starts from plus loose goods of its kind round it (same level), counted up to <paramref name="cap"/>.</summary>
    private static int GoodsNear(ColonySim sim, HaulPlan plan, int cap)
    {
        int n = sim.Items.TryGet(plan.Source, out var own) ? own.Available + plan.Amount : plan.Amount; // its own units are reserved already
        int r = Balance.MachineClusterReach;
        for (int dy = -r; dy <= r && n < cap; dy++)
        for (int dx = -r; dx <= r && n < cap; dx++)
        {
            var c = plan.Source.Offset(dx, dy, 0);
            if (c == plan.Source || !sim.Items.TryGet(c, out var pile) || pile.Type != plan.Type || pile.Available <= 0) continue;
            if (sim.Stockpiles.Contains(c) || sim.Rooms.PartAt(c) != null || sim.Constructions.At(c) != null) continue;
            n += pile.Available;
        }
        return Math.Min(n, cap);
    }

    // Two passes. Straight-line steps shortlist the cheapest machine that could pay at all (a full session of work
    // checked before the orders or goods are counted); only that one is then measured by pathfinding (its trip to the
    // work, and the walk to it; the walk to the work by hand is the path the colonist planned anyway), at most two or
    // three searches, counted against the shared per-tick path budget. A machine the measurement turns down is not
    // measured again for the same work for a while (_machineNo), so idle colonists don't search for it over and over.

    /// <summary>A machine measured and turned down for some work, until a tick.</summary>
    private (int Machine, TilePos Work, long Until) _machineNo = (-1, default, 0);

    private bool TurnedDown(ColonySim sim, Colonist m, TilePos work) =>
        _machineNo.Machine == m.Id && _machineNo.Work == work && sim.Tick < _machineNo.Until;

    private void TurnDown(ColonySim sim, Colonist m, TilePos work) => _machineNo = (m.Id, work, sim.Tick + Balance.MachineRestTicks);

    /// <summary>Steps of a path just planned by <see cref="Route"/> (a long trip's first leg, plus a straight line from its end).</summary>
    private int RouteSteps(List<TilePos> path, TilePos near) =>
        path.Count + (_plannedTrip != null && path.Count > 0 ? Steps(path[^1], near) : 0);

    /// <summary>Steps from <paramref name="from"/> to any of <paramref name="goals"/> by pathfinding (null: no way); plans nothing.</summary>
    private int? MeasureSteps(ColonySim sim, TilePos from, HashSet<TilePos> goals, TilePos near, Colonist machine)
    {
        if (goals.Count == 0) return null;
        if (goals.Contains(from)) return 0;
        var saved = _plannedTrip;
        sim.PathSearches++;
        var path = PlanPath(sim, goals, near, from, machine);
        int? steps = path is null ? null : RouteSteps(path, near);
        _plannedTrip = saved;
        return steps;
    }

    /// <summary>
    /// Before walking to a dig (<paramref name="walk"/>: the steps of the path it planned to it): an excavator worth
    /// fetching for it, and if so it claims the job and sets off to the machine. False with <see cref="_deferred"/> set
    /// when the path budget ran out before it could measure.
    /// </summary>
    private bool TryFetchExcavator(ColonySim sim, Job job, int component, int walk)
    {
        if (!MayFetchMachine(sim) || !Does(Work.Mining) || WorkOf(job.Kind) != Work.Mining) return false;
        int dig = sim.WorkTicks(job.Target, job.Kind);
        int jobs = -1;
        Colonist? best = null;
        float bestCost = float.MaxValue;
        foreach (var m in sim.Machines)
        {
            if (m.Machine != MachineKind.Excavator || !MayDrive(sim, m, component) || TurnedDown(sim, m, job.Target)) continue;
            int toMachine = Steps(Position, m.Position), drive = Steps(m.Position, job.Target);
            if (ExcavatorDigCost(toMachine, drive, Balance.ExcavatorSessionJobs, dig, true) >= HandDigCost(walk, Balance.ExcavatorSessionJobs, dig)) continue;
            if (jobs < 0) jobs = MiningOrdersNear(sim, job.Target, Filter(sim), Balance.ExcavatorSessionJobs);
            float cost = ExcavatorDigCost(toMachine, drive, jobs, dig, true);
            if (cost < HandDigCost(walk, jobs, dig) && cost < bestCost) (best, bestCost) = (m, cost);
        }
        if (best is null) return false;
        if (sim.PathBudgetExhausted)
        {
            _deferred = true;
            return false;
        }
        // Measured: its crawl to the dig, then the walk to it (planned last: that is the path it sets off on).
        var path = MeasureSteps(sim, best.Position, ReachableAround(sim, job.Target, component), job.Target, best) is { } crawl
            ? Route(sim, ReachableAround(sim, best.Position, component), best.Position) is { } p
              && ExcavatorDigCost(RouteSteps(p, best.Position), crawl, jobs, dig, true) < HandDigCost(walk, jobs, dig) ? p : null
            : null;
        if (path is null)
        {
            TurnDown(sim, best, job.Target);
            return false;
        }
        if (!sim.Designations.Claim(job.Target, Id)) return false;
        _jobKind = job.Kind;
        BeginBoarding(best, ColonistTask.Designation, job.Target, path);
        return true;
    }

    /// <summary>Driving the excavator: whether the next dig (<paramref name="steps"/> along the path planned to it) is worth crawling to.</summary>
    private bool ExcavatorStays(ColonySim sim, Job job, int steps)
    {
        int dig = sim.WorkTicks(job.Target, job.Kind);
        int jobs = MiningOrdersNear(sim, job.Target, Filter(sim), Balance.ExcavatorSessionJobs);
        return ExcavatorDigCost(0, steps, jobs, dig, board: false) < HandDigCost(steps, jobs, dig);
    }

    /// <summary>
    /// A power wagon worth fetching to carry goods from <paramref name="source"/> to <paramref name="dest"/> (it walks
    /// <paramref name="walk"/> steps to the source by hand), and the path to it; null when walking does better.
    /// <paramref name="unitsFor"/> counts the goods there are to carry, up to a cap (asked only once a wagon could pay).
    /// </summary>
    private List<TilePos>? WagonWorthFetching(ColonySim sim, int component, TilePos source, TilePos dest, int walk,
        int hand, int wagon, Func<int, int> unitsFor, out Colonist? chosen)
    {
        chosen = null;
        int most = Balance.WagonSessionLoads * wagon, units = -1, haul = Steps(source, dest);
        float bestCost = float.MaxValue;
        foreach (var m in sim.Machines)
        {
            if (m.Machine != MachineKind.PowerWagon || !MayDrive(sim, m, component) || TurnedDown(sim, m, source)) continue;
            int toWagon = Steps(Position, m.Position), drive = Steps(m.Position, source);
            if (WagonHaulCost(toWagon, drive, haul, most, wagon, true) >= HandHaulCost(walk, haul, most, hand)) continue;
            if (units < 0) units = unitsFor(most);
            float cost = WagonHaulCost(toWagon, drive, haul, units, wagon, true);
            if (cost < HandHaulCost(walk, haul, units, hand) && cost < bestCost) (chosen, bestCost) = (m, cost);
        }
        if (chosen is null) return null;
        if (sim.PathBudgetExhausted)
        {
            _deferred = true;
            chosen = null;
            return null;
        }
        // Measured: the haul itself, the wagon's drive to the pickup, then the walk to the wagon (the path it sets off on).
        var c = chosen;
        List<TilePos>? path = null;
        if (MeasureSteps(sim, source, ReachableAround(sim, dest, component), dest, c) is { } h
            && MeasureSteps(sim, c.Position, ReachableAround(sim, source, component), source, c) is { } drive2
            && Route(sim, ReachableAround(sim, c.Position, component), c.Position) is { } p
            && WagonHaulCost(RouteSteps(p, c.Position), drive2, h, units, wagon, true) < HandHaulCost(walk, h, units, hand))
            path = p;
        if (path is null)
        {
            TurnDown(sim, c, source);
            chosen = null;
        }
        return path;
    }

    /// <summary>Before walking to a haul's pickup: a power wagon worth fetching for it and what lies round it; if so it reserves the haul and sets off to the wagon.</summary>
    private bool TryFetchWagon(ColonySim sim, HaulPlan plan, int component, int walk)
    {
        if (!MayFetchMachine(sim) || !Does(Work.Hauling)) return false;
        int hand = ItemInfo.CarryLimit(plan.Type);
        int wagon = sim.Stockpiles.Contains(plan.Destination) ? hand * Balance.WagonLoads : hand; // only stockpiles take a whole wagon load
        var path = WagonWorthFetching(sim, component, plan.Source, plan.Destination, walk, hand, wagon, cap => GoodsNear(sim, plan, cap), out var m);
        if (path is null || !ReserveHaul(sim, plan)) return false;
        BeginBoarding(m!, ColonistTask.HaulPickup, plan.Source, path);
        return true;
    }

    /// <summary>
    /// Before walking to goods owed on a deal: a power wagon worth fetching to deliver what is still owed of that line
    /// (as much as the colony holds). If so it sets off to the wagon and, aboard, looks for its work again (nothing is
    /// reserved meanwhile: the deal may be shipped by someone else first).
    /// </summary>
    private bool TryFetchWagonForTrade(ColonySim sim, Trade.TradeLine line, TilePos source, TilePos dest, int component, int walk)
    {
        if (!MayFetchMachine(sim) || !Does(Work.Trading)) return false;
        int hand = ItemInfo.CarryLimit(line.Type);
        var path = WagonWorthFetching(sim, component, source, dest, walk, hand, hand * Balance.WagonLoads,
            cap => Math.Min(cap, Math.Min(line.Unclaimed, sim.Items.UnitsOf(line.Type))), out var m);
        if (path is null) return false;
        BeginBoarding(m!, ColonistTask.None, source, path);
        return true;
    }

    // ---- Fuel ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A driver whose machine runs low drives it to the nearest charcoal it may use (not in a factory's input crate) and
    /// stokes the firebox full there.
    /// </summary>
    private bool TryStoke(ColonySim sim)
    {
        var v = _vehicle!;
        if (!sim.NeedsEnabled || v._fuelRetry > 0 || sim.PathBudgetExhausted) return false;
        int component = sim.Connectivity.ComponentOf(Position);
        v._fuelRetry = 100;
        if (component < 0) return false;
        var fuel = ItemType.Charcoal;
        var options = new List<(TilePos Cell, ItemPile Pile, int Dist)>();
        foreach (var (p, pile) in sim.Items.All)
        {
            if (pile.Type != fuel || pile.Available <= 0 || !Needs.Usable(sim, p, Faction)) continue;
            if (sim.Connectivity.ComponentOf(p) != component) continue;
            options.Add((p, pile, Steps(p, Position)));
        }
        if (options.Count == 0) return false;
        options.Sort((a, b) => a.Dist != b.Dist ? a.Dist.CompareTo(b.Dist) : CompareCells(a.Cell, b.Cell));
        foreach (var (cell, pile, _) in options.Take(CandidatesPerSearch))
        {
            var path = Route(sim, ReachableAround(sim, cell, component), cell);
            if (path is null) continue;
            int units = Math.Clamp((FuelCapacity - v.Fuel) / Balance.FuelPerCharcoal, 1, pile.Available);
            if (!sim.Items.Reserve(cell, units)) continue;
            _stoke = (cell, units);
            v._fuelRetry = 0;
            Begin(ColonistTask.Stoke, cell, path);
            return true;
        }
        return false;
    }

    private void FinishStoke(ColonySim sim)
    {
        int taken = sim.Items.TakeReserved(_stoke.Cell, _stoke.Units);
        if (_vehicle is { } v) v.Fuel = Math.Min(FuelCapacity, v.Fuel + taken * Balance.FuelPerCharcoal);
        Finish();
    }

    /// <summary>
    /// A driver between jobs: charcoal first when the machine runs low; an empty one with none to be had is parked (and
    /// left alone a while). False when this tick went to that.
    /// </summary>
    private bool TendVehicle(ColonySim sim)
    {
        if (!_aboard || !sim.NeedsEnabled) return true;
        var v = _vehicle!;
        if (v.LowOnFuel && TryStoke(sim)) return false;
        if (v.OutOfFuel)
        {
            v._fuelRetry = Math.Max(v._fuelRetry, 600);
            Park(sim);
        }
        return true;
    }

    // ---- Describing, saving -------------------------------------------------------------------------------------

    private string DescribeMachine() =>
        Driver is { } d ? $"driven by {d.Name}"
        : ComingToDrive is { } c ? $"waiting for {c.Name} to drive it"
        : OutOfFuel ? "parked, out of fuel (a driver needs charcoal in reach)"
        : "parked";

    private string DescribeBoarding() => State == ColonistState.Working
        ? $"climbing into {_vehicle!.Name}"
        : $"going to drive {_vehicle!.Name}";

    private string DescribeStoke() => State == ColonistState.Working
        ? $"stoking {_vehicle!.Name} with charcoal"
        : $"driving {_vehicle!.Name} to charcoal";

    /// <summary>After a snapshot is read: back in the machine it was driving (by id).</summary>
    internal void LinkVehicle(IReadOnlyDictionary<int, Colonist> byId)
    {
        if (_loadedVehicle < 0) return;
        var v = byId[_loadedVehicle];
        _loadedVehicle = -1;
        _vehicle = v;
        _aboard = true;
        v._claim = this;
    }

    private void SettleMachine()
    {
        _boardThen = default;
        _boardJob = default;
        _machineRest = 0;
        _fuelRetry = 0;
        _stoke = default;
        _machineNo = (-1, default, 0);
        _blockedFor = 0;
        _tradeGoods.Clear();
        _tradeRoom.Clear();
    }

    // ---- The power wagon's big loads ---------------------------------------------------------------------------

    /// <summary>Room the wagon reserved in stockpile cells round its stop (beyond the plan's destination).</summary>
    private readonly List<(TilePos Cell, int Amount)> _spill = new();
    /// <summary>Units of the spill still reserved at the haul's source pile (until pickup).</summary>
    private int _spillFromSource;

    /// <summary>Units a load can be: a colonist's carry, times the wagon's when driving one.</summary>
    private int CarryCapacity(ItemType type) => ItemInfo.CarryLimit(type) * (Drives == MachineKind.PowerWagon ? Balance.WagonLoads : 1);

    /// <summary>How far round its stop a wagon unloads, and round its pickup it gathers more (Chebyshev tiles).</summary>
    public const int WagonReach = 3;

    /// <summary>
    /// Reserves room for up to <paramref name="want"/> more units in the stockpile cells round the haul's destination (same
    /// level, same stockpile, within its filter and cap; nearest first), adding to the spill. Returns how many it found room for.
    /// </summary>
    private int ReserveSpillRoom(ColonySim sim, int want, int component)
    {
        // Only into the same stockpile, and as far as its filter and cap allow.
        if (sim.Stockpiles.ZoneAt(_haul.Destination) is not { } home) return 0;
        want = Math.Min(want, home.Takes(_haul.Type, home.Held(_haul.Type, sim.Items)));
        int got = 0;
        for (int r = 1; r <= WagonReach && got < want; r++)
        for (int dy = -r; dy <= r && got < want; dy++)
        for (int dx = -r; dx <= r && got < want; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            var c = _haul.Destination.Offset(dx, dy, 0);
            if (sim.Stockpiles.ZoneAt(c) != home) continue;
            if (sim.Rooms.PartAt(c) != null || sim.Constructions.At(c) != null || sim.Connectivity.ComponentOf(c) != component) continue;
            int n = Math.Min(want - got, sim.Items.RoomAt(c, _haul.Type));
            if (n <= 0 || !sim.Items.ReserveIncoming(c, _haul.Type, n)) continue;
            _spill.Add((c, n));
            got += n;
        }
        return got;
    }

    /// <summary>
    /// A wagon's haul to a stockpile grows past one cell's room: more of the source pile, with room reserved in the cells
    /// round the destination, up to its capacity. (More is gathered from piles round the source at pickup.)
    /// </summary>
    private void ExtendLoad(ColonySim sim, int component)
    {
        _spill.Clear();
        _spillFromSource = 0;
        if (Drives != MachineKind.PowerWagon || !sim.Stockpiles.Contains(_haul.Destination)) return;
        if (!sim.Items.TryGet(_haul.Source, out var pile)) return;
        int want = Math.Min(CarryCapacity(_haul.Type) - _haul.Amount, pile.Available);
        if (want <= 0) return;
        int room = ReserveSpillRoom(sim, want, component);
        if (room > 0 && sim.Items.Reserve(_haul.Source, room)) _spillFromSource = room;
        else ReleaseSpill(sim, source: false);
    }

    /// <summary>Gives back the room a wagon reserved round its stop (and its source units, before pickup).</summary>
    private void ReleaseSpill(ColonySim sim, bool source)
    {
        foreach (var (c, n) in _spill) sim.Items.ReleaseIncoming(c, n);
        if (source && _spillFromSource > 0) sim.Items.Unreserve(_haul.Source, _spillFromSource);
        _spill.Clear();
        _spillFromSource = 0;
    }

    /// <summary>
    /// At pickup: takes the spill's units from the source (a shortfall gives back room, last cells first), then gathers
    /// more of the same from loose piles round it into new room, up to the wagon's capacity. Returns the units added.
    /// </summary>
    private int PickUpSpill(ColonySim sim, int carrying, int component)
    {
        if (Drives != MachineKind.PowerWagon) return 0;
        int taken = _spillFromSource > 0 ? sim.Items.TakeReserved(_haul.Source, _spillFromSource) : 0;
        for (int shortBy = _spillFromSource - taken, i = _spill.Count - 1; shortBy > 0 && i >= 0; i--)
        {
            var (c, n) = _spill[i];
            int back = Math.Min(n, shortBy);
            sim.Items.ReleaseIncoming(c, back);
            _spill[i] = (c, n - back);
            shortBy -= back;
        }
        _spill.RemoveAll(s => s.Amount == 0);
        _spillFromSource = 0;
        if (!sim.Stockpiles.Contains(_haul.Destination)) return taken;

        int capacity = CarryCapacity(_haul.Type);
        for (int dy = -WagonReach; dy <= WagonReach; dy++)
        for (int dx = -WagonReach; dx <= WagonReach; dx++)
        {
            int load = carrying + taken;
            if (load >= capacity) return taken;
            var c = _haul.Source.Offset(dx, dy, 0);
            if (c == _haul.Source || !sim.Items.TryGet(c, out var pile) || pile.Type != _haul.Type || pile.Available <= 0) continue;
            if (sim.Stockpiles.Contains(c) || sim.Rooms.PartAt(c) != null || sim.Constructions.At(c) != null || sim.Connectivity.ComponentOf(c) != component) continue;
            int room = ReserveSpillRoom(sim, Math.Min(pile.Available, capacity - load), component);
            if (room == 0) return taken; // nowhere left to put more
            if (!sim.Items.Reserve(c, room))
            {
                for (int i = _spill.Count - 1, give = room; give > 0 && i >= 0; i--)
                {
                    var (sc, n) = _spill[i];
                    int back = Math.Min(n, give);
                    sim.Items.ReleaseIncoming(sc, back);
                    _spill[i] = (sc, n - back);
                    give -= back;
                }
                _spill.RemoveAll(s => s.Amount == 0);
                continue;
            }
            taken += sim.Items.TakeReserved(c, room);
        }
        return taken;
    }

    /// <summary>At the stop: fills the spill cells from the load (what doesn't fit stays carried and goes to the destination).</summary>
    private void DeliverSpill(ColonySim sim)
    {
        foreach (var (c, n) in _spill)
        {
            int put = Math.Min(n, CarryingCount);
            if (put < n) sim.Items.ReleaseIncoming(c, n - put); // Deliver releases what it is handed; the rest of the room goes back
            CarryingCount -= sim.Items.Deliver(c, CarryingType, put);
        }
        _spill.Clear();
    }
}
