using Colony.Core.World;

namespace Colony.Core.Navigation;

/// <summary>
/// Movement rules for walking agents. An agent is one tile tall and occupies an air tile whose floor
/// (the tile below) is solid. Each move goes to one of the 8 horizontal neighbours on the same level,
/// or climbs or drops one level onto an orthogonal neighbour.
/// <list type="bullet">
/// <item>Diagonal moves may not cut corners: both orthogonal cells they pass between must be open.</item>
/// <item>Climbing up needs headroom (the tile above the agent must be open).</item>
/// <item>Dropping down needs the tile in front, at the current level, to be open.</item>
/// <item>Climbs onto or off a <see cref="TileType.Stairs"/> floor cost <see cref="StairCost"/>.</item>
/// </list>
/// The move graph is undirected: every legal move is also legal in reverse (connectivity relies on this).
/// Costs are in "flat tile" units. Generic over <see cref="ITileReader"/> so it runs on the live world or
/// on a <see cref="TileBox"/> snapshot.
/// </summary>
public static class Movement
{
    public const float FlatCost = 1f;
    public const float DiagonalCost = 1.41421356f;
    public const float ClimbUpCost = 2.5f;
    public const float ClimbDownCost = 1.8f;
    public const float StairCost = 1.2f;

    private static readonly (int dx, int dy)[] Orthogonal = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private static readonly (int dx, int dy)[] Diagonal = [(1, 1), (1, -1), (-1, 1), (-1, -1)];

    /// <summary>In bounds and not solid.</summary>
    public static bool IsOpen<T>(in T w, int x, int y, int z) where T : ITileReader =>
        w.InBounds(x, y, z) && w.Get(x, y, z) == TileType.Air;

    /// <summary>An agent can stand here: open tile with a solid floor.</summary>
    public static bool IsStandable<T>(in T w, TilePos p) where T : ITileReader =>
        p.Z > 0 && IsOpen(w, p.X, p.Y, p.Z) && w.Get(p.X, p.Y, p.Z - 1) != TileType.Air;

    /// <summary>Fills <paramref name="output"/> with every legal move from <paramref name="p"/> and its cost.</summary>
    public static void GetMoves<T>(in T w, TilePos p, List<(TilePos To, float Cost)> output) where T : ITileReader
    {
        output.Clear();

        foreach (var (dx, dy) in Orthogonal)
        {
            int x = p.X + dx, y = p.Y + dy;
            if (!w.InBounds(x, y, p.Z)) continue;

            if (w.Get(x, y, p.Z) != TileType.Air)
            {
                // Climb up onto the block in front.
                var up = new TilePos(x, y, p.Z + 1);
                if (IsStandable(w, up) && IsOpen(w, p.X, p.Y, p.Z + 1))
                    output.Add((up, ClimbCost(w, p, up, ClimbUpCost)));
            }
            else if (p.Z > 0 && w.Get(x, y, p.Z - 1) != TileType.Air)
            {
                output.Add((new TilePos(x, y, p.Z), FlatCost));
            }
            else
            {
                // No floor in front: step down one level if there's footing there.
                var down = new TilePos(x, y, p.Z - 1);
                if (IsStandable(w, down))
                    output.Add((down, ClimbCost(w, p, down, ClimbDownCost)));
            }
        }

        foreach (var (dx, dy) in Diagonal)
        {
            var q = new TilePos(p.X + dx, p.Y + dy, p.Z);
            if (IsStandable(w, q) && IsOpen(w, p.X + dx, p.Y, p.Z) && IsOpen(w, p.X, p.Y + dy, p.Z))
                output.Add((q, DiagonalCost));
        }
    }

    /// <summary>Cost of a single move, or null if it is not currently legal.</summary>
    public static float? MoveCost<T>(in T w, TilePos from, TilePos to) where T : ITileReader
    {
        var moves = new List<(TilePos, float)>(12);
        GetMoves(w, from, moves);
        foreach (var (q, cost) in moves)
            if (q == to) return cost;
        return null;
    }

    private static float ClimbCost<T>(in T w, TilePos a, TilePos b, float raw) where T : ITileReader =>
        w.Get(a.X, a.Y, a.Z - 1) == TileType.Stairs || w.Get(b.X, b.Y, b.Z - 1) == TileType.Stairs
            ? StairCost
            : raw;
}
