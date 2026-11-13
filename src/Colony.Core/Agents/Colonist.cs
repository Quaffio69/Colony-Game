using Colony.Core.Navigation;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Agents;

public enum ColonistState { Idle, Moving, Working }

/// <summary>
/// A colony member. An idle colonist asks the <see cref="JobBoard"/> for the nearest unclaimed job in its
/// own connectivity component, paths to a work spot with A*, and works the tile until done.
/// Unreachable jobs never trigger a search.
/// </summary>
public sealed class Colonist
{
    /// <summary>Ticks to walk one flat tile (scaled by each move's cost).</summary>
    public const int TicksPerFlatStep = 7;
    /// <summary>Jobs tried (path searches) per idle tick before giving up until something changes.</summary>
    public const int CandidatesPerSearch = 3;

    private readonly Queue<TilePos> _path = new();
    private float _stepTicksTotal, _stepTicksDone;
    private int _searchedDesignations = -1, _searchedConnectivity = -1;
    private int _retryDelay;
    // Jobs whose path search failed despite sharing a component (node budget hit); cleared on world change.
    private readonly HashSet<TilePos> _pathFailed = new();
    private int _pathFailedConnectivity = -1;

    public int Id { get; }
    public string Name { get; }
    public TilePos Position { get; private set; }
    public ColonistState State { get; private set; }

    /// <summary>Tile currently being stepped towards, for smooth rendering; null when not mid-step.</summary>
    public TilePos? NextStep { get; private set; }
    /// <summary>0..1 progress of the current step.</summary>
    public float StepProgress => NextStep is null || _stepTicksTotal <= 0 ? 0f : _stepTicksDone / _stepTicksTotal;

    public TilePos? JobTarget { get; private set; }
    public int WorkDone { get; private set; }
    public int WorkRequired { get; private set; }

    public Colonist(int id, string name, TilePos position)
    {
        Id = id;
        Name = name;
        Position = position;
    }

    public string Describe() => State switch
    {
        ColonistState.Moving when JobTarget is { } t => $"walking to job at {t}",
        ColonistState.Working when JobTarget is { } t => $"working at {t} ({WorkDone * 100 / Math.Max(1, WorkRequired)}%)",
        _ => "idle",
    };

    /// <summary>Tiles a colonist standing at <paramref name="at"/> can work on: all 26 neighbours.</summary>
    public static bool InReach(TilePos at, TilePos target) =>
        Math.Abs(at.X - target.X) <= 1 && Math.Abs(at.Y - target.Y) <= 1 && Math.Abs(at.Z - target.Z) <= 1;

    internal void Tick(ColonySim sim)
    {
        var world = sim.World;
        var orders = sim.Designations;

        ApplyGravity(sim);

        if (JobTarget is { } job && !orders.IsClaimedBy(job, Id))
            Abandon(sim); // cancelled, re-designated, or completed by someone else

        switch (State)
        {
            case ColonistState.Idle:
                if (_retryDelay > 0)
                {
                    _retryDelay--;
                    return;
                }
                // Nothing new since the last fruitless search: stay idle without searching again.
                if (_searchedDesignations == orders.Version && _searchedConnectivity == sim.Connectivity.Version) return;
                TryTakeJob(sim);
                break;

            case ColonistState.Moving:
                if (NextStep is null)
                {
                    if (_path.Count == 0)
                    {
                        BeginWork(sim);
                        return;
                    }
                    var next = _path.Dequeue();
                    if (Movement.MoveCost(world, Position, next) is not { } cost)
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
                    if (_path.Count == 0) BeginWork(sim);
                }
                break;

            case ColonistState.Working:
                var target = JobTarget!.Value;
                if (!InReach(Position, target))
                {
                    Abandon(sim);
                    return;
                }
                if (++WorkDone >= WorkRequired)
                {
                    orders.Complete(target);
                    JobTarget = null;
                    State = ColonistState.Idle;
                }
                break;
        }
    }

    private void TryTakeJob(ColonySim sim)
    {
        // Path searches are the expensive part of the sim; spread them over ticks rather than stacking them.
        if (sim.PathBudgetExhausted) return;
        var orders = sim.Designations;
        var connectivity = sim.Connectivity;
        _searchedDesignations = orders.Version;
        _searchedConnectivity = connectivity.Version;

        if (_pathFailedConnectivity != connectivity.Version)
        {
            _pathFailed.Clear();
            _pathFailedConnectivity = connectivity.Version;
        }

        int component = connectivity.ComponentOf(Position);
        if (component < 0) return;

        foreach (var job in sim.JobBoard.Candidates(component, Position, orders, CandidatesPerSearch, _pathFailed))
        {
            if (sim.PathBudgetExhausted)
            {
                _searchedDesignations = -1; // resume next tick
                return;
            }
            var goals = new HashSet<TilePos>();
            foreach (var s in job.WorkSpots)
                if (connectivity.ComponentOf(s) == component) goals.Add(s);

            sim.PathSearches++;
            var path = goals.Contains(Position) ? new List<TilePos>() : PlanPath(sim, goals, job.Target);
            if (path is null)
            {
                _pathFailed.Add(job.Target);
                continue;
            }
            if (!orders.Claim(job.Target, Id)) continue;

            JobTarget = job.Target;
            _path.Clear();
            foreach (var p in path) _path.Enqueue(p);
            State = ColonistState.Moving;
            return;
        }

        // Every candidate failed; if others remain, try the next batch shortly.
        if (_pathFailed.Count > 0) _searchedDesignations = -1;
        _retryDelay = 5;
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
        if (volume <= MaxSnapshotTiles)
        {
            var box = TileBox.Capture(sim.World, min.X - pad, min.Y - pad, min.Z - pad,
                max.X - min.X + 2 * pad, max.Y - min.Y + 2 * pad, max.Z - min.Z + 2 * pad);
            return Pathfinder.FindPathToAny(box, sim.World.Width, sim.World.Depth, Position, goals, near, allowed: InCorridor);
        }
        return Pathfinder.FindPathToAny(sim.World, Position, goals, near, allowed: InCorridor);
    }

    /// <summary>Largest corridor bounding box (in tiles) worth copying into a snapshot for pathfinding.</summary>
    private const long MaxSnapshotTiles = 4_000_000;

    private void BeginWork(ColonySim sim)
    {
        var target = JobTarget!.Value;
        if (!InReach(Position, target) || !sim.Designations.TryGet(target, out var kind))
        {
            Abandon(sim);
            return;
        }
        State = ColonistState.Working;
        WorkDone = 0;
        WorkRequired = Designations.WorkTicks(kind, sim.World.Get(target));
    }

    private void Abandon(ColonySim sim)
    {
        if (JobTarget is { } t) sim.Designations.Release(t, Id);
        JobTarget = null;
        _path.Clear();
        NextStep = null;
        State = ColonistState.Idle;
        _searchedDesignations = -1; // look again straight away
    }

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
            while (p.Z > 1 && !w.IsSolid(p.X, p.Y, p.Z - 1)) p = p.Offset(0, 0, -1);
        }

        if (p == Position) return;
        Position = p;
        Abandon(sim);
    }
}
