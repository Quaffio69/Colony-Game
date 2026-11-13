using Colony.Core.Agents;
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

    public ColonySim(VoxelWorld world)
    {
        World = world;
        Designations = new Designations(world);
        Connectivity = new Connectivity(world);
        _caveIns = new CaveIns(world);
    }

    /// <summary>
    /// Carries out a designation, then lets any mass it left unsupported cave in. Everything that changes the
    /// world on a colonist's (or the debug tool's) behalf should come through here.
    /// </summary>
    public bool CompleteDesignation(TilePos p)
    {
        if (!Designations.TryGet(p, out var kind) || !Designations.Complete(p)) return false;
        if (kind == DesignationKind.Dig) CheckSupport(p);
        return true;
    }

    /// <summary>Call after removing a solid tile by any other means (tests, future tools).</summary>
    public void CheckSupport(TilePos removed) => _caveIns.AfterRemoval(removed, Tick, OnCaveIn);

    private void OnCaveIn(CaveIn caveIn, IReadOnlyList<TilePos> landed)
    {
        // Orders on tiles that fell no longer point at the rock they were placed on.
        foreach (var q in landed) Designations.Cancel(q.Offset(0, 0, caveIn.Drop));
        CaveInCount++;
        _recentCaveIns.Add(caveIn);
        if (_recentCaveIns.Count > RecentCaveInsKept) _recentCaveIns.RemoveAt(0);
        // Colonists caught underneath or riding the mass are handled by their own gravity next tick.
    }

    /// <summary>Generates a world and drops a starting party in the valley south of the central mountain.</summary>
    public static ColonySim Generate(WorldGenSettings settings, int colonists = 7)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sim = new ColonySim(WorldGenerator.Generate(settings));
        long genMs = clock.ElapsedMilliseconds;

        var (sx, sy) = WorldGenerator.SuggestedSpawn(settings);
        foreach (var p in sim.FindSpawnPoints(sx, sy, colonists))
            sim.AddColonist(NameGenerator.Generate(settings.Seed, sim._colonists.Count), p);

        clock.Restart();
        sim.Connectivity.Update();
        sim.StartupReport = $"terrain {genMs} ms, connectivity {clock.ElapsedMilliseconds} ms " +
                            $"({sim.Connectivity.RegionCount} regions, {sim.Connectivity.ComponentCount} components)";
        return sim;
    }

    /// <summary>Human-readable timings from <see cref="Generate"/>.</summary>
    public string StartupReport { get; private set; } = "";

    public Colonist AddColonist(string name, TilePos position)
    {
        var c = new Colonist(_colonists.Count, name, position);
        _colonists.Add(c);
        return c;
    }

    public void Step()
    {
        Tick++;
        _tickNodeStart = Pathfinder.NodesExpanded;

        // Reachability data is only needed by colonists looking for work, so refresh it lazily.
        bool anyIdle = false;
        foreach (var c in _colonists) anyIdle |= c.State == ColonistState.Idle;
        if (anyIdle)
        {
            if (Designations.Count > 0) Connectivity.Update();
            JobBoard.Update(World, Designations, Connectivity); // no-op unless something changed
        }

        foreach (var c in _colonists) c.Tick(this);
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
