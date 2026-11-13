using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Orders;

/// <summary>A designation plus the standable tiles a colonist could work it from.</summary>
public sealed class Job
{
    public required TilePos Target { get; init; }
    public required DesignationKind Kind { get; init; }
    /// <summary>Standable tiles within reach of <see cref="Target"/>.</summary>
    public required HashSet<TilePos> WorkSpots { get; init; }
    /// <summary>Connectivity components of the work spots; a colonist in one of these can reach the job.</summary>
    public required int[] Components { get; init; }
}

/// <summary>
/// Cached view of all designations with their reachability, rebuilt only when designations or connectivity
/// change. Jobs nobody can reach (sealed in rock, on an unreachable ledge, ...) are filtered out with a
/// component check instead of a failed path search.
/// </summary>
public sealed class JobBoard
{
    private readonly List<Job> _jobs = new();
    private int _designationVersion = -1, _connectivityVersion = -1;

    public IReadOnlyList<Job> Jobs => _jobs;

    /// <summary>Jobs no standable tile can work at all (e.g. buried in solid rock).</summary>
    public int InaccessibleCount { get; private set; }

    public void Update(VoxelWorld world, Designations designations, Connectivity connectivity)
    {
        if (designations.Version == _designationVersion && connectivity.Version == _connectivityVersion) return;
        _designationVersion = designations.Version;
        _connectivityVersion = connectivity.Version;

        _jobs.Clear();
        InaccessibleCount = 0;
        var comps = new List<int>(4);
        foreach (var (target, kind) in designations.All)
        {
            var spots = new HashSet<TilePos>();
            comps.Clear();
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                // Standing directly on a tile you're digging out would drop you into the hole.
                if (dx == 0 && dy == 0 && dz == 1 && kind == DesignationKind.Dig) continue;
                var s = target.Offset(dx, dy, dz);
                int c = connectivity.ComponentOf(s);
                if (c < 0) continue;
                spots.Add(s);
                if (!comps.Contains(c)) comps.Add(c);
            }

            if (spots.Count == 0)
            {
                InaccessibleCount++;
                continue;
            }
            _jobs.Add(new Job { Target = target, Kind = kind, WorkSpots = spots, Components = comps.ToArray() });
        }
    }

    /// <summary>
    /// Up to <paramref name="max"/> unclaimed jobs reachable from <paramref name="component"/>, nearest first
    /// (straight-line), skipping any in <paramref name="exclude"/>. Ties prefer higher levels, so digging
    /// proceeds top-down.
    /// </summary>
    public List<Job> Candidates(int component, TilePos from, Designations designations, int max,
        IReadOnlySet<TilePos>? exclude = null)
    {
        var best = new List<(long Key, Job Job)>(max + 1);
        foreach (var job in _jobs)
        {
            if (Array.IndexOf(job.Components, component) < 0) continue;
            if (!designations.Contains(job.Target) || designations.IsClaimed(job.Target)) continue; // board may lag a tick
            if (exclude != null && exclude.Contains(job.Target)) continue;

            long dx = job.Target.X - from.X, dy = job.Target.Y - from.Y, dz = job.Target.Z - from.Z;
            long dist = dx * dx + dy * dy + 4 * dz * dz;
            // Pack (distance, -z, y, x) into one sortable key for a deterministic order.
            long key = (dist << 24) | ((long)(255 - Math.Clamp(job.Target.Z, 0, 255)) << 16)
                       | ((long)(job.Target.Y & 0xFF) << 8) | (long)(job.Target.X & 0xFF);

            if (best.Count == max && key >= best[^1].Key) continue;
            int at = best.FindIndex(b => b.Key > key);
            best.Insert(at < 0 ? best.Count : at, (key, job));
            if (best.Count > max) best.RemoveAt(best.Count - 1);
        }
        return best.ConvertAll(b => b.Job);
    }
}
