using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Combat;

/// <summary>
/// A Dijkstra map ("flow field"): the walking cost from every cell in a box to the nearest of some sources (a nest's
/// entrance; the colonists a swarm is after). One search serves a whole swarm: each creature just steps to the
/// neighbour with the lowest value, so hundreds of ants path for the price of one flood. Costs are tenths of a flat step.
/// </summary>
internal sealed class FlowField
{
    private readonly Dictionary<TilePos, int> _dist = new();
    public int Count => _dist.Count;
    public bool TryGet(TilePos p, out int d) => _dist.TryGetValue(p, out d);

    public static FlowField Build(VoxelWorld w, IEnumerable<TilePos> sources, int minX, int minY, int maxX, int maxY, int maxNodes,
        List<(TilePos To, float Cost)> moves)
    {
        var f = new FlowField();
        var open = new PriorityQueue<TilePos, int>();
        foreach (var s in sources)
        {
            if (s.X < minX || s.X > maxX || s.Y < minY || s.Y > maxY || !Movement.IsStandable(w, s) || f._dist.ContainsKey(s)) continue;
            f._dist[s] = 0;
            open.Enqueue(s, 0);
        }
        int settled = 0;
        while (open.TryDequeue(out var p, out int d))
        {
            if (d > f._dist[p]) continue;
            if (++settled >= maxNodes) break;
            Movement.GetMoves(w, p, moves);
            foreach (var (q, cost) in moves)
            {
                if (q.X < minX || q.X > maxX || q.Y < minY || q.Y > maxY) continue;
                int nd = d + (int)(cost * 10 + 0.5f);
                if (f._dist.TryGetValue(q, out int old) && old <= nd) continue;
                f._dist[q] = nd;
                open.Enqueue(q, nd);
            }
        }
        return f;
    }

    /// <summary>The neighbour (by a legal move) with a lower value than here, lowest first; null if none.</summary>
    public TilePos? Downhill(VoxelWorld w, TilePos at, List<(TilePos To, float Cost)> moves)
    {
        int here = _dist.TryGetValue(at, out int h) ? h : int.MaxValue;
        Movement.GetMoves(w, at, moves);
        TilePos? best = null;
        int bestD = here;
        foreach (var (q, _) in moves)
            if (_dist.TryGetValue(q, out int d) && d < bestD)
            {
                bestD = d;
                best = q;
            }
        return best;
    }
}
