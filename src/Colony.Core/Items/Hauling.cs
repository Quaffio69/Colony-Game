using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Items;

/// <summary>One trip: carry <see cref="Amount"/> units of <see cref="Type"/> from a loose pile to a stockpile cell.</summary>
public readonly record struct HaulPlan(TilePos Source, TilePos Destination, ItemType Type, int Amount);

/// <summary>
/// Chooses haul trips. Linear scans over loose piles and stockpile cells: fine for thousands of each; a
/// spatial index is the upgrade path once colonies get industrial-scale stockpiles.
/// </summary>
public static class Hauling
{
    private const int SourcesConsidered = 3;

    /// <summary>
    /// Nearest loose piles reachable from <paramref name="component"/>, each paired with the nearest reachable
    /// stockpile cell that has room for it. Candidates in <paramref name="exclude"/> are skipped.
    /// </summary>
    public static List<HaulPlan> Plans(ItemPiles items, Stockpiles stockpiles, VoxelWorld world,
        Connectivity connectivity, TilePos from, int component, IReadOnlySet<TilePos>? exclude = null)
    {
        var plans = new List<HaulPlan>();
        if (stockpiles.Count == 0) return plans;

        // Nearest loose, unreserved piles in the colonist's component.
        var sources = new List<(long Dist, TilePos Pos, ItemPile Pile)>(SourcesConsidered + 1);
        foreach (var (p, pile) in items.All)
        {
            if (pile.Available <= 0 || stockpiles.Contains(p)) continue;
            if (exclude != null && exclude.Contains(p)) continue;
            if (connectivity.ComponentOf(p) != component) continue;
            long d = Dist(p, from);
            if (sources.Count == SourcesConsidered && d >= sources[^1].Dist) continue;
            int at = sources.FindIndex(s => s.Dist > d);
            sources.Insert(at < 0 ? sources.Count : at, (d, p, pile));
            if (sources.Count > SourcesConsidered) sources.RemoveAt(sources.Count - 1);
        }

        foreach (var (_, src, pile) in sources)
        {
            TilePos? best = null;
            long bestDist = long.MaxValue;
            int bestRoom = 0;
            foreach (var cell in stockpiles.Cells)
            {
                long d = Dist(cell, src);
                if (d > bestDist || (d == bestDist && best is { } b && Tie(cell, b))) continue;
                int room = items.RoomAt(cell, pile.Type);
                if (room <= 0 || connectivity.ComponentOf(cell) != component) continue;
                best = cell;
                bestDist = d;
                bestRoom = room;
            }
            if (best is { } dest)
            {
                int amount = Math.Min(Math.Min(pile.Available, bestRoom), ItemInfo.CarryLimit(pile.Type));
                plans.Add(new HaulPlan(src, dest, pile.Type, amount));
            }
        }
        return plans;
    }

    private static long Dist(TilePos a, TilePos b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + 4 * dz * dz;
    }

    /// <summary>Deterministic tie-break between equally distant cells (HashSet order must not matter).</summary>
    private static bool Tie(TilePos candidate, TilePos current) =>
        (candidate.Z, candidate.Y, candidate.X).CompareTo((current.Z, current.Y, current.X)) >= 0;
}
