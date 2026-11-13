using Colony.Core.World;

namespace Colony.Core.Navigation;

/// <summary>
/// Movement rules for walking agents. An agent is one tile tall and occupies an open (non-solid) cell. It can
/// stand there if the tile below is solid, or if the cell is itself a stair (DF-style: the stair is its footing).
/// <list type="bullet">
/// <item>Walk to any of the 8 horizontal neighbours that is standable. Diagonals may not cut corners: both
/// orthogonal cells passed between must be open.</item>
/// <item>Climb up or drop down one level onto an orthogonal neighbour, through the open air cell above the lower
/// end (headroom). Slow, unless either end is a stair cell.</item>
/// <item>Stairs (DF-style): from an up stair (<see cref="TileType.UpStair"/>/<see cref="TileType.UpDownStair"/>)
/// straight up into a down stair (<see cref="TileType.DownStair"/>/<see cref="TileType.UpDownStair"/>) and back.</item>
/// </list>
/// The move graph is undirected: every legal move is also legal in reverse (connectivity relies on this).
/// Costs are in "flat tile" units. Generic over <see cref="ITileReader"/> so it runs on the live world or
/// on a <see cref="TileBox"/> snapshot. Reads at most 1 tile around and 2 below a cell.
/// </summary>
public static class Movement
{
    public const float FlatCost = 1f;
    public const float DiagonalCost = 1.41421356f;
    public const float ClimbUpCost = 2.5f;
    public const float ClimbDownCost = 1.8f;
    /// <summary>One level by stairs. Also the cheapest way to change level (the heuristic relies on this).</summary>
    public const float StairCost = 1.2f;

    private static readonly (int dx, int dy)[] Orthogonal = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private static readonly (int dx, int dy)[] Diagonal = [(1, 1), (1, -1), (-1, 1), (-1, -1)];

    /// <summary>In bounds and not solid (air or a stair cell).</summary>
    public static bool IsOpen<T>(in T w, int x, int y, int z) where T : ITileReader =>
        w.InBounds(x, y, z) && !TileInfo.IsSolid(w.Get(x, y, z));

    /// <summary>An agent can stand here: an open cell that is a stair or has a solid floor.</summary>
    public static bool IsStandable<T>(in T w, TilePos p) where T : ITileReader
    {
        if (p.Z <= 0 || !w.InBounds(p.X, p.Y, p.Z)) return false;
        var t = w.Get(p.X, p.Y, p.Z);
        if (TileInfo.IsSolid(t)) return false;
        return TileInfo.IsStair(t) || TileInfo.IsSolid(w.Get(p.X, p.Y, p.Z - 1));
    }

    /// <summary>Fills <paramref name="output"/> with every legal move from <paramref name="p"/> and its cost.</summary>
    public static void GetMoves<T>(in T w, TilePos p, List<(TilePos To, float Cost)> output) where T : ITileReader
    {
        output.Clear();
        var here = w.Get(p.X, p.Y, p.Z);

        foreach (var (dx, dy) in Orthogonal)
        {
            int x = p.X + dx, y = p.Y + dy;
            var flat = new TilePos(x, y, p.Z);
            if (IsStandable(w, flat)) output.Add((flat, FlatCost));
            // No early exit: a stair cell can sit directly above a walkable neighbour, and the reverse drop out
            // of it must stay legal (undirected graph). The conditions below exclude every other overlap.
            // Climb: up through the air above us onto the neighbour one level up.
            var up = new TilePos(x, y, p.Z + 1);
            if (IsClimb(w, p, up)) output.Add((up, ClimbCost(w, p, up, ClimbUpCost)));
            // Drop: through the air above the neighbour down into it.
            var down = new TilePos(x, y, p.Z - 1);
            if (IsClimb(w, down, p)) output.Add((down, ClimbCost(w, p, down, ClimbDownCost)));
        }

        foreach (var (dx, dy) in Diagonal)
        {
            var q = new TilePos(p.X + dx, p.Y + dy, p.Z);
            if (IsStandable(w, q) && IsOpen(w, p.X + dx, p.Y, p.Z) && IsOpen(w, p.X, p.Y + dy, p.Z))
                output.Add((q, DiagonalCost));
        }

        // Stairs: straight up or down the shaft.
        if (TileInfo.GoesUp(here) && w.InBounds(p.X, p.Y, p.Z + 1) && TileInfo.GoesDown(w.Get(p.X, p.Y, p.Z + 1)))
            output.Add((p.Offset(0, 0, 1), StairCost));
        if (TileInfo.GoesDown(here) && p.Z > 1 && TileInfo.GoesUp(w.Get(p.X, p.Y, p.Z - 1)))
            output.Add((p.Offset(0, 0, -1), StairCost));
    }

    /// <summary>
    /// A one-level climb between <paramref name="lower"/> and the orthogonally adjacent, one level higher
    /// <paramref name="upper"/> (either direction). The agent passes through the cell above the lower end,
    /// which must be plain air: neither solid, nor a stair cell it would be skipping.
    /// </summary>
    private static bool IsClimb<T>(in T w, TilePos lower, TilePos upper) where T : ITileReader =>
        w.InBounds(lower.X, lower.Y, lower.Z + 1) && w.Get(lower.X, lower.Y, lower.Z + 1) == TileType.Air &&
        IsStandable(w, lower) && IsStandable(w, upper);

    /// <summary>Getting into or out of a stair cell sideways is as easy as a stair step.</summary>
    private static float ClimbCost<T>(in T w, TilePos a, TilePos b, float raw) where T : ITileReader =>
        TileInfo.IsStair(w.Get(a.X, a.Y, a.Z)) || TileInfo.IsStair(w.Get(b.X, b.Y, b.Z)) ? StairCost : raw;

    /// <summary>Cost of a single move, or null if it is not currently legal.</summary>
    public static float? MoveCost<T>(in T w, TilePos from, TilePos to) where T : ITileReader
    {
        var moves = new List<(TilePos, float)>(12);
        GetMoves(w, from, moves);
        foreach (var (q, cost) in moves)
            if (q == to) return cost;
        return null;
    }
}
