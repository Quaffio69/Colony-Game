using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Agents;

/// <summary>
/// Scaffolds: a tool item (made at a carpentry's joinery bench) for work out of reach, such as a ceiling two levels
/// over the floor. When a designation can only be worked from a scaffold (<see cref="Job.HighSpots"/>), a colonist
/// fetches the nearest free scaffold, carries it to a spot two levels below the work, sets it up, stands on it (it
/// reaches from the cell above where it stands), does the work, takes the scaffold down and leaves it there as an
/// ordinary pile (haulers bring it back to storage). Dropping the work at any point leaves the scaffold where it is.
/// </summary>
public sealed partial class Colonist
{
    public const int ErectTicks = 60, StrikeTicks = 40;

    private static ItemType? _scaffoldItem;
    /// <summary>The scaffold item (catalog id "scaffold").</summary>
    public static ItemType ScaffoldItem => _scaffoldItem ??= Catalog.Item("scaffold");

    private enum ScaffoldStep : byte { None, Fetch, Carry, Erect, Up, Strike }
    private ScaffoldStep _scaffold;
    private TilePos _scaffoldSource, _scaffoldJob;

    /// <summary>How far the scaffold it is working from stands up (0 = none, 1 = fully up), for views.</summary>
    public float ScaffoldHeight => _scaffold switch
    {
        ScaffoldStep.Erect => WorkDone / (float)Math.Max(1, WorkRequired),
        ScaffoldStep.Up => 1f,
        ScaffoldStep.Strike => 1f - WorkDone / (float)Math.Max(1, WorkRequired),
        _ => 0f,
    };

    /// <summary>Standing on a scaffold (drawn a level up, working from the cell above <see cref="Position"/>).</summary>
    public bool OnScaffold => _scaffold == ScaffoldStep.Up;

    /// <summary>Where it reaches from: the cell above where it stands while on a scaffold.</summary>
    private TilePos ReachFrom => _scaffold is ScaffoldStep.Carry or ScaffoldStep.Erect or ScaffoldStep.Up or ScaffoldStep.Strike
        ? Position.Offset(0, 0, 1) : Position;

    /// <summary>The nearest free scaffold it may take in its component, or null.</summary>
    private TilePos? NearestScaffold(ColonySim sim, int component)
    {
        var type = ScaffoldItem;
        TilePos? best = null;
        int bestDist = int.MaxValue;
        foreach (var (p, pile) in sim.Items.All)
        {
            if (pile.Type != type || pile.Available <= 0 || !Needs.Usable(sim, p, Faction)) continue;
            if (sim.Connectivity.ComponentOf(p) != component) continue;
            if (Hauling.RoleOf(p, type, sim.Stockpiles, sim.Rooms, sim.Constructions) is StorageRole.Construction) continue;
            int d = Math.Abs(p.X - Position.X) + Math.Abs(p.Y - Position.Y) + 2 * Math.Abs(p.Z - Position.Z);
            if (d < bestDist || d == bestDist && CompareCells(p, best!.Value) < 0) { best = p; bestDist = d; }
        }
        return best;
    }

    /// <summary>Claims a job only a scaffold reaches and sets off to fetch the scaffold at <paramref name="at"/>.</summary>
    private bool TryFetchScaffold(ColonySim sim, Job job, TilePos at, int component)
    {
        if (ScaffoldSpots(sim, job.Target, component).Count == 0) return false;
        var path = Route(sim, ReachableAround(sim, at, component), at);
        if (path is null)
        {
            _pathFailed.Add(job.Target);
            return false;
        }
        if (!sim.Designations.Claim(job.Target, Id)) return false;
        if (!sim.Items.Reserve(at, 1))
        {
            sim.Designations.Release(job.Target, Id);
            return false;
        }
        _jobKind = job.Kind;
        _scaffold = ScaffoldStep.Fetch;
        _scaffoldSource = at;
        _scaffoldJob = job.Target;
        Begin(ColonistTask.ScaffoldFetch, at, path);
        return true;
    }

    /// <summary>Standable cells in the component two levels below the target's reach, with open air above them.</summary>
    private static HashSet<TilePos> ScaffoldSpots(ColonySim sim, TilePos target, int component)
    {
        var spots = new HashSet<TilePos>();
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            var s = target.Offset(dx, dy, -2);
            var up = s.Offset(0, 0, 1);
            if (sim.World.InBounds(up) && sim.World.Get(up) == TileType.Air && sim.Connectivity.ComponentOf(s) == component) spots.Add(s);
        }
        return spots;
    }

    /// <summary>Picked the scaffold up: carries it to a spot under the work.</summary>
    private void PickUpScaffold(ColonySim sim)
    {
        if (sim.Items.TakeReserved(_scaffoldSource, 1) == 0)
        {
            Abandon(sim); // someone moved it
            return;
        }
        CarryingType = ScaffoldItem;
        CarryingCount = 1;
        _scaffold = ScaffoldStep.Carry;
        int component = sim.Connectivity.ComponentOf(Position);
        var path = Route(sim, ScaffoldSpots(sim, _scaffoldJob, component), _scaffoldJob);
        if (path is null)
        {
            Abandon(sim); // drops the scaffold here
            return;
        }
        Begin(ColonistTask.Designation, _scaffoldJob, path);
    }

    /// <summary>Arrived under the work with the scaffold: sets it up (false if it can't stand here).</summary>
    private bool BeginErect(ColonySim sim)
    {
        if (sim.World.Get(Position.Offset(0, 0, 1)) != TileType.Air) return false;
        CarryingCount = 0; // it stands now (dropped again if the work is abandoned)
        _scaffold = ScaffoldStep.Erect;
        StartWorking(ErectTicks);
        return true;
    }

    /// <summary>
    /// A designation's work finished with a scaffold involved: up -> work, worked -> take it down, down -> done.
    /// False when the scaffold isn't involved (plain work).
    /// </summary>
    private bool ScaffoldWorkStep(ColonySim sim)
    {
        switch (_scaffold)
        {
            case ScaffoldStep.Erect:
                _scaffold = ScaffoldStep.Up;
                if (!sim.Designations.TryGet(JobTarget!.Value, out var kind)) Strike();
                else StartWorking(sim.WorkTicks(JobTarget.Value, kind));
                return true;
            case ScaffoldStep.Up:
                if (InReach(ReachFrom, JobTarget!.Value)) sim.CompleteDesignation(JobTarget.Value);
                Strike();
                return true;
            case ScaffoldStep.Strike:
                sim.Items.Drop(Position, ScaffoldItem, 1);
                _scaffold = ScaffoldStep.None;
                Finish();
                return true;
            default:
                return false;
        }
    }

    private void Strike()
    {
        _scaffold = ScaffoldStep.Strike;
        StartWorking(StrikeTicks);
    }

    /// <summary>Abandoning: an erected scaffold stays where it stood, as a pile; one being fetched is unreserved.</summary>
    private void AbandonScaffold(ColonySim sim)
    {
        switch (_scaffold)
        {
            case ScaffoldStep.Fetch:
                sim.Items.Unreserve(_scaffoldSource, 1);
                sim.Designations.Release(_scaffoldJob, Id);
                break;
            case ScaffoldStep.Erect or ScaffoldStep.Up or ScaffoldStep.Strike:
                sim.Items.Drop(Position, ScaffoldItem, 1);
                break;
        }
        _scaffold = ScaffoldStep.None;
    }

    private string DescribeScaffold() => _scaffold switch
    {
        ScaffoldStep.Fetch => "fetching a scaffold",
        ScaffoldStep.Carry => $"carrying a scaffold to {JobTarget}",
        ScaffoldStep.Erect => "putting up a scaffold",
        ScaffoldStep.Strike => "taking down a scaffold",
        _ => $"working at {JobTarget} from a scaffold ({WorkDone * 100 / Math.Max(1, WorkRequired)}%)",
    };
}
