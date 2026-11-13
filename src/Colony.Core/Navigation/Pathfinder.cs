using Colony.Core.World;

namespace Colony.Core.Navigation;

/// <summary>Grid searches over <see cref="Movement"/> rules. Paths exclude the start and end at the goal.</summary>
public static class Pathfinder
{
    public const int DefaultMaxNodes = 60_000;

    /// <summary>
    /// Weighted A* factor for colonist routing. Above 1 trades a little path optimality (at most this factor)
    /// for far fewer expanded nodes over hilly terrain, where climbs cost much more than the heuristic assumes.
    /// </summary>
    public static float HeuristicWeight { get; set; } = 1.2f;

    /// <summary>Nodes expanded by searches on the current thread (budgeting and diagnostics).</summary>
    public static long NodesExpanded { get => _nodesExpanded; set => _nodesExpanded = value; }
    [ThreadStatic] private static long _nodesExpanded;

    /// <summary>A* from <paramref name="start"/> to <paramref name="goal"/>. Returns null if unreachable within the node budget.</summary>
    public static List<TilePos>? FindPath(VoxelWorld w, TilePos start, TilePos goal, int maxNodes = DefaultMaxNodes)
    {
        if (!Movement.IsStandable(w, start) || !Movement.IsStandable(w, goal)) return null;
        return Search(w, w.Width, w.Depth, start, p => p == goal, p => Heuristic(p, goal), maxNodes, out _);
    }

    /// <summary>
    /// A* to whichever of <paramref name="goals"/> is cheapest to reach. All goals must lie within one tile
    /// (Chebyshev) of <paramref name="near"/>, which keeps the heuristic admissible.
    /// </summary>
    /// <param name="allowed">Optional filter: tiles the path may pass through (e.g. a region corridor).</param>
    public static List<TilePos>? FindPathToAny(VoxelWorld w, TilePos start, IReadOnlySet<TilePos> goals, TilePos near,
        int maxNodes = DefaultMaxNodes, Func<TilePos, bool>? allowed = null) =>
        FindPathToAny(w, w.Width, w.Depth, start, goals, near, maxNodes, allowed);

    /// <summary>
    /// As above, over any tile source, typically a <see cref="TileBox"/> snapshot covering the search area
    /// (with 2 tiles of padding), which avoids per-read chunk lookups. Width/depth are the world's.
    /// </summary>
    public static List<TilePos>? FindPathToAny<T>(T tiles, int worldWidth, int worldDepth, TilePos start,
        IReadOnlySet<TilePos> goals, TilePos near, int maxNodes = DefaultMaxNodes, Func<TilePos, bool>? allowed = null)
        where T : ITileReader
    {
        if (goals.Count == 0 || !Movement.IsStandable(tiles, start)) return null;
        // Any goal is at most one diagonal-and-vertical step from `near`, which costs at most this much.
        const float slack = Movement.DiagonalCost + 0.01f;
        return Search(tiles, worldWidth, worldDepth, start, goals.Contains,
            p => HeuristicWeight * MathF.Max(0f, Heuristic(p, near) - slack), maxNodes, out _, allowed);
    }

    /// <summary>
    /// Dijkstra flood from <paramref name="start"/>; returns the path to the cheapest node satisfying
    /// <paramref name="isGoal"/>, or null if none is found within the node budget.
    /// </summary>
    public static List<TilePos>? FindNearest(VoxelWorld w, TilePos start, Func<TilePos, bool> isGoal,
        out TilePos found, int maxNodes = DefaultMaxNodes)
    {
        found = default;
        if (!Movement.IsStandable(w, start)) return null;
        return Search(w, w.Width, w.Depth, start, isGoal, _ => 0f, maxNodes, out found);
    }

    /// <summary>Total cost of walking a path, or null if any step is not legal.</summary>
    public static float? PathCost(VoxelWorld w, TilePos start, IReadOnlyList<TilePos> path)
    {
        float total = 0;
        var at = start;
        foreach (var next in path)
        {
            if (Movement.MoveCost(w, at, next) is not { } c) return null;
            total += c;
            at = next;
        }
        return total;
    }

    private sealed class Scratch
    {
        public readonly List<Node> Nodes = new(1024);
        public readonly Dictionary<long, int> Index = new(1024);
        public readonly PriorityQueue<int, float> Open = new();
        public readonly List<(TilePos To, float Cost)> Moves = new(12);
    }

    [ThreadStatic] private static Scratch? _scratch;

    private struct Node
    {
        public TilePos Pos;
        public float G;
        public int Parent;
        public bool Closed;
    }

    private static List<TilePos>? Search<T>(T w, int keyWidth, int keyDepth, TilePos start, Func<TilePos, bool> isGoal,
        Func<TilePos, float> h, int maxNodes, out TilePos found, Func<TilePos, bool>? allowed = null) where T : ITileReader
    {
        found = default;
        long Key(TilePos p) => ((long)p.Z * keyDepth + p.Y) * keyWidth + p.X;

        // Reused per thread: searches run back to back, and reallocating these (especially a large index
        // dictionary) each time showed up as GC churn on long trips.
        var scratch = _scratch ??= new Scratch();
        var nodes = scratch.Nodes;
        var index = scratch.Index;
        var open = scratch.Open;
        var moves = scratch.Moves;
        nodes.Clear();
        open.Clear();
        if (index.Count > 50_000) index.TrimExcess(1024); // don't keep clearing a huge table after one long search
        index.Clear();

        nodes.Add(new Node { Pos = start, G = 0, Parent = -1 });
        index[Key(start)] = 0;
        open.Enqueue(0, h(start));
        int closedCount = 0;

        while (open.TryDequeue(out int i, out _))
        {
            var node = nodes[i];
            if (node.Closed) continue; // stale queue entry
            node.Closed = true;
            nodes[i] = node;

            if (isGoal(node.Pos))
            {
                found = node.Pos;
                return Reconstruct(nodes, i);
            }
            _nodesExpanded++;
            if (++closedCount >= maxNodes) break;

            Movement.GetMoves(w, node.Pos, moves);
            foreach (var (q, cost) in moves)
            {
                if (allowed != null && !allowed(q)) continue;
                float g = node.G + cost;
                long k = Key(q);
                if (index.TryGetValue(k, out int j))
                {
                    var other = nodes[j];
                    if (other.Closed || other.G <= g) continue;
                    other.G = g;
                    other.Parent = i;
                    nodes[j] = other;
                    open.Enqueue(j, g + h(q));
                }
                else
                {
                    j = nodes.Count;
                    nodes.Add(new Node { Pos = q, G = g, Parent = i });
                    index[k] = j;
                    open.Enqueue(j, g + h(q));
                }
            }
        }

        return null;
    }

    /// <summary>Admissible: every move changes z by at most 1 and costs at least <see cref="Movement.StairCost"/> when it does.</summary>
    internal static float Heuristic(TilePos a, TilePos b)
    {
        int dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y), dz = Math.Abs(a.Z - b.Z);
        float octile = Math.Max(dx, dy) + (Movement.DiagonalCost - 1f) * Math.Min(dx, dy);
        return Math.Max(octile, dz * Movement.StairCost);
    }

    private static List<TilePos> Reconstruct(List<Node> nodes, int end)
    {
        var path = new List<TilePos>();
        for (int i = end; nodes[i].Parent >= 0; i = nodes[i].Parent)
            path.Add(nodes[i].Pos);
        path.Reverse();
        return path;
    }
}
