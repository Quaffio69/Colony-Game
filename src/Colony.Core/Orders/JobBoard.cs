using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Orders;

/// <summary>A designation plus the standable tiles a colonist could work it from.</summary>
public sealed class Job
{
    public required TilePos Target { get; init; }
    public DesignationKind Kind { get; internal set; }
    /// <summary>Standable tiles within reach of <see cref="Target"/>.</summary>
    public IReadOnlyList<TilePos> WorkSpots => _spots;
    /// <summary>Connectivity components of the work spots; a colonist in one of these can reach the job.</summary>
    public int[] Components { get; internal set; } = [];

    internal readonly List<TilePos> _spots = new(8);
    internal int Stamp;
}

/// <summary>
/// Cached view of all designations with their reachability. Unreachable jobs (sealed in rock, on an
/// unreachable ledge, ...) are filtered out with a component check instead of a failed path search.
/// Incremental: jobs persist across updates; when designations change only added/removed ones are touched,
/// and when connectivity changes only jobs next to changed chunks re-check their work spots (the rest just
/// refresh their component ids, which costs nothing for buried jobs with no spots).
/// </summary>
public sealed class JobBoard
{
    private readonly Dictionary<TilePos, Job> _byTarget = new();
    private readonly List<Job> _jobs = new(); // accessible jobs, in a deterministic order
    private int _designationVersion = -1, _connectivityVersion = -1;
    private int _stamp;
    private VoxelWorld? _world;
    private readonly HashSet<int> _changedChunks = new();
    private bool _allChanged = true;

    public IReadOnlyList<Job> Jobs => _jobs;

    /// <summary>Jobs no standable tile can work at all (e.g. buried in solid rock).</summary>
    public int InaccessibleCount { get; private set; }

    public void Update(VoxelWorld world, Designations designations, Connectivity connectivity)
    {
        if (_world != world)
        {
            _world = world;
            world.ChunkChanged += i => _changedChunks.Add(i);
            _allChanged = true;
        }
        bool ordersChanged = designations.Version != _designationVersion;
        bool reachChanged = connectivity.Version != _connectivityVersion;
        if (!ordersChanged && !reachChanged) return;
        _designationVersion = designations.Version;
        _connectivityVersion = connectivity.Version;

        // 1. Sync jobs with designations (new, changed kind, removed).
        _stamp++;
        var fresh = new List<Job>();
        foreach (var (target, kind) in designations.All)
        {
            if (!_byTarget.TryGetValue(target, out var job))
            {
                job = new Job { Target = target, Kind = kind };
                _byTarget[target] = job;
                fresh.Add(job);
            }
            else if (job.Kind != kind)
            {
                job.Kind = kind;
                fresh.Add(job);
            }
            job.Stamp = _stamp;
        }
        if (_byTarget.Count != designations.Count)
        {
            var gone = new List<TilePos>();
            foreach (var (t, job) in _byTarget)
                if (job.Stamp != _stamp) gone.Add(t);
            foreach (var t in gone) _byTarget.Remove(t);
        }

        // 2. Work spots: recompute where the ground nearby changed (or the job is new); otherwise just
        // refresh component ids, which are renumbered on every connectivity rebuild.
        var comps = new List<int>(4);
        foreach (var job in fresh) Recompute(job, connectivity, comps);
        if (reachChanged)
        {
            foreach (var job in _byTarget.Values)
            {
                if (job.Stamp == -_stamp) continue; // already recomputed as fresh
                if (_allChanged || NearChange(job.Target, world)) Recompute(job, connectivity, comps);
                else RefreshComponents(job, connectivity, comps);
            }
            _changedChunks.Clear();
            _allChanged = false;
        }

        // 3. The accessible list, in designation order (deterministic).
        _jobs.Clear();
        InaccessibleCount = 0;
        foreach (var target in designations.All.Keys)
        {
            var job = _byTarget[target];
            if (job._spots.Count > 0) _jobs.Add(job);
            else InaccessibleCount++;
        }
    }

    private void Recompute(Job job, Connectivity connectivity, List<int> comps)
    {
        job._spots.Clear();
        comps.Clear();
        var target = job.Target;
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            // Standing directly on a tile you're digging out would drop you into the hole.
            if (dx == 0 && dy == 0 && dz == 1 && job.Kind == DesignationKind.Dig) continue;
            var s = target.Offset(dx, dy, dz);
            int c = connectivity.ComponentOf(s);
            if (c < 0) continue;
            job._spots.Add(s);
            if (!comps.Contains(c)) comps.Add(c);
        }
        job.Components = comps.ToArray();
        job.Stamp = -_stamp;
    }

    private static void RefreshComponents(Job job, Connectivity connectivity, List<int> comps)
    {
        if (job._spots.Count == 0) return;
        comps.Clear();
        foreach (var s in job._spots)
        {
            int c = connectivity.ComponentOf(s);
            if (c >= 0 && !comps.Contains(c)) comps.Add(c);
        }
        if (comps.Count != job.Components.Length || !comps.TrueForAll(c => Array.IndexOf(job.Components, c) >= 0))
            job.Components = comps.ToArray();
    }

    /// <summary>Whether any chunk overlapping the job's reach (target +-1, plus 2 below for floors) changed.</summary>
    private bool NearChange(TilePos t, VoxelWorld world)
    {
        if (_changedChunks.Count == 0) return false;
        int cx0 = (t.X - 1) >> Chunk.Shift, cx1 = (t.X + 1) >> Chunk.Shift;
        int cy0 = (t.Y - 1) >> Chunk.Shift, cy1 = (t.Y + 1) >> Chunk.Shift;
        int cz0 = (t.Z - 3) >> Chunk.Shift, cz1 = (t.Z + 1) >> Chunk.Shift;
        for (int cz = cz0; cz <= cz1; cz++)
        for (int cy = cy0; cy <= cy1; cy++)
        for (int cx = cx0; cx <= cx1; cx++)
            if (world.GetChunk(cx, cy, cz) is not null && _changedChunks.Contains(world.ChunkIndex(cx, cy, cz)))
                return true;
        return false;
    }

    /// <summary>
    /// Up to <paramref name="max"/> unclaimed jobs reachable from <paramref name="component"/>, nearest first
    /// (straight-line), skipping any in <paramref name="exclude"/>. Ties prefer higher levels, so digging
    /// proceeds top-down.
    /// </summary>
    public List<Job> Candidates(int component, TilePos from, Designations designations, int max,
        IReadOnlySet<TilePos>? exclude = null, int kinds = -1, int faction = Owners.Any)
    {
        var best = new List<(long Key, Job Job)>(max + 1);
        foreach (var job in _jobs)
        {
            if ((kinds & (1 << (int)job.Kind)) == 0) continue; // a kind of work this colonist isn't looking for now
            if (Array.IndexOf(job.Components, component) < 0) continue;
            if (!designations.Contains(job.Target) || designations.IsClaimed(job.Target)) continue; // board may lag a tick
            if (exclude != null && exclude.Contains(job.Target)) continue;
            if (faction != Owners.Any && designations.OwnerOf(job.Target) != faction) continue; // another colony's order

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
