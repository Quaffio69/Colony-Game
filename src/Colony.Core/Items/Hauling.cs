using Colony.Core.Industry;
using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Items;

/// <summary>One trip: carry <see cref="Amount"/> units of <see cref="Type"/> from one cell to another.</summary>
public readonly record struct HaulPlan(TilePos Source, TilePos Destination, ItemType Type, int Amount);

/// <summary>What a cell holding items is for, which decides where haulers take things from and to.</summary>
public enum StorageRole : byte
{
    /// <summary>Lying around: haul to storage (or straight to a factory that wants it).</summary>
    Loose,
    /// <summary>Stockpile zone: stays unless a factory wants it.</summary>
    Stockpile,
    /// <summary>Factory input crate: filled with the room's input, never emptied by haulers.</summary>
    Input,
    /// <summary>Factory output crate: products to be taken away (to stockpiles or other factories).</summary>
    Output,
    /// <summary>Building materials on a blueprint's footprint: stays until a builder uses it.</summary>
    Construction,
}

/// <summary>
/// Chooses haul trips, in priority order:
/// <list type="number">
/// <item>Demand: factory input crates with room pull their room's input from loose piles, stockpiles or other
/// factories' output crates (this is what links factories together; conveyors will plug in here).</item>
/// <item>Supply: loose piles and output crates go to the nearest stockpile cell with room.</item>
/// </list>
/// Linear scans over piles, crates and stockpile cells: fine for thousands of each; a spatial index is the
/// upgrade path once colonies get industrial-scale stockpiles.
/// </summary>
public static class Hauling
{
    private const int Considered = 3;

    /// <summary>Role of the pile at <paramref name="p"/>. Wrong goods in an input crate count as loose (cleared out).</summary>
    public static StorageRole RoleOf(TilePos p, ItemType type, Stockpiles stockpiles, Rooms rooms, Orders.Constructions? sites = null)
    {
        if (sites?.At(p) is { } site) return site.Cost.Material == type ? StorageRole.Construction : StorageRole.Loose;
        var part = rooms.PartAt(p);
        if (part is { Built: false }) return part.Cost.Material == type ? StorageRole.Construction : StorageRole.Loose;
        if (part?.Role == PartRole.Input) return part.Room.Needs(type) ? StorageRole.Input : StorageRole.Loose;
        if (part?.Role == PartRole.Output) return StorageRole.Output;
        return stockpiles.Contains(p) ? StorageRole.Stockpile : StorageRole.Loose;
    }

    public static List<HaulPlan> Plans(ItemPiles items, Stockpiles stockpiles, Rooms rooms, Connectivity connectivity,
        TilePos from, int component, IReadOnlySet<TilePos>? exclude = null, Orders.Constructions? sites = null)
    {
        var plans = new List<HaulPlan>();
        bool Usable(TilePos p) => (exclude is null || !exclude.Contains(p)) && connectivity.ComponentOf(p) == component;

        // 1. Demand: input crates that want their machines' inputs.
        var demands = new Nearest<(TilePos Cell, ItemType Type, int Room)>(Considered);
        foreach (var room in rooms.All)
        {
            foreach (var (cell, type, space) in InputDemands(room, items))
                if (Usable(cell)) demands.Offer(Dist(cell, from), (cell, type, space));
            // Blueprints want their materials, spread over their footprint (one pile per cell).
            foreach (var bp in room.Blueprints)
            {
                var (material, cost) = bp.Cost;
                int need = cost;
                foreach (var c in bp.Cells)
                    if (items.TryGet(c, out var pile) && pile.Type == material) need -= pile.Count + pile.Incoming;
                if (need <= 0) continue;
                foreach (var c in bp.Cells)
                {
                    int space = Math.Min(items.RoomAt(c, material), need);
                    if (space <= 0 || !Usable(c)) continue;
                    demands.Offer(Dist(c, from), (c, material, space));
                    break;
                }
            }
        }
        // Structure blueprints (walls, wheels, pumps) want their material in their own cell.
        if (sites is not null)
            foreach (var bp in sites.All)
            {
                var (material, cost) = bp.Cost;
                int need = cost;
                if (items.TryGet(bp.Cell, out var pile)) need = pile.Type == material ? cost - pile.Count - pile.Incoming : 0;
                int space = Math.Min(items.RoomAt(bp.Cell, material), need);
                if (space > 0 && Usable(bp.Cell)) demands.Offer(Dist(bp.Cell, from), (bp.Cell, material, space));
            }
        foreach (var (cell, type, space) in demands.Items)
        {
            var source = new Nearest<(TilePos Pos, int Available)>(1);
            foreach (var (p, pile) in items.All)
            {
                if (pile.Type != type || pile.Available <= 0 || !Usable(p)) continue;
                if (RoleOf(p, pile.Type, stockpiles, rooms, sites) is StorageRole.Input or StorageRole.Construction) continue;
                source.Offer(Dist(p, cell), (p, pile.Available));
            }
            foreach (var (src, available) in source.Items)
                plans.Add(new HaulPlan(src, cell, type, Math.Min(Math.Min(available, space), ItemInfo.CarryLimit(type))));
        }

        // 2. Supply: loose piles and output crates to the nearest stockpile cell with room.
        if (stockpiles.Count == 0) return plans;
        var sources = new Nearest<(TilePos Pos, ItemPile Pile)>(Considered);
        foreach (var (p, pile) in items.All)
        {
            if (pile.Available <= 0 || !Usable(p)) continue;
            var role = RoleOf(p, pile.Type, stockpiles, rooms, sites);
            if (role is StorageRole.Loose or StorageRole.Output) sources.Offer(Dist(p, from), (p, pile));
        }
        foreach (var (src, pile) in sources.Items)
        {
            var dest = new Nearest<(TilePos Cell, int Room)>(1);
            foreach (var cell in stockpiles.Cells)
            {
                int space = items.RoomAt(cell, pile.Type);
                if (space > 0 && connectivity.ComponentOf(cell) == component) dest.Offer(Dist(cell, src), (cell, space), cell);
            }
            foreach (var (cell, space) in dest.Items)
                plans.Add(new HaulPlan(src, cell, pile.Type, Math.Min(Math.Min(pile.Available, space), ItemInfo.CarryLimit(pile.Type))));
        }
        return plans;
    }

    /// <summary>
    /// Deliveries a room's input crates want: for each input its machines need (see <see cref="Room.InputNeeds"/>)
    /// still short of its target, a crate already holding it with room, or else an empty crate. Each needed input
    /// that has no crate yet keeps one empty crate set aside, so one input can't fill every crate and starve the
    /// others.
    /// </summary>
    public static IEnumerable<(TilePos Cell, ItemType Type, int Space)> InputDemands(Room room, ItemPiles items)
    {
        var needs = room.InputNeeds();
        if (needs.Count == 0) yield break;
        var crates = room.PartsWithRole(PartRole.Input).Select(c => c.Cells[0]).ToList();
        var empty = crates.Where(c => !items.TryGet(c, out _)).ToList();
        bool HasCrate(ItemType t) => crates.Any(c => items.TryGet(c, out var p) && p.Type == t);
        int crateless = needs.Count(n => !HasCrate(n.Item));
        foreach (var (type, wanted) in needs)
        {
            int have = 0;
            TilePos? withRoom = null;
            foreach (var c in crates)
            {
                if (!items.TryGet(c, out var p) || p.Type != type) continue;
                have += p.Count + p.Incoming;
                if (withRoom is null && items.RoomAt(c, type) > 0) withRoom = c;
            }
            if (have >= wanted) continue;
            bool has = HasCrate(type);
            if (withRoom is null && empty.Count > 0 && (!has || empty.Count > crateless))
            {
                withRoom = empty[0];
                empty.RemoveAt(0);
                if (!has) crateless--;
            }
            if (withRoom is { } cell) yield return (cell, type, Math.Min(items.RoomAt(cell, type), wanted - have));
        }
    }

    private static long Dist(TilePos a, TilePos b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + 4 * dz * dz;
    }

    /// <summary>
    /// Keeps the <c>k</c> nearest offers. Ties are broken by a cell key when one is given (for sets whose
    /// iteration order must not matter), otherwise by arrival order (for deterministically ordered sources).
    /// </summary>
    private sealed class Nearest<T>(int k)
    {
        private readonly List<(long Dist, long Tie, T Item)> _best = new(k + 1);

        public void Offer(long dist, T item, TilePos? tieCell = null)
        {
            long tie = tieCell is { } c ? ((long)c.Z << 40) | ((long)c.Y << 20) | (uint)c.X : 0;
            if (_best.Count == k && (dist, tie).CompareTo((_best[^1].Dist, _best[^1].Tie)) >= 0) return;
            int at = _best.FindIndex(b => (b.Dist, b.Tie).CompareTo((dist, tie)) > 0);
            _best.Insert(at < 0 ? _best.Count : at, (dist, tie, item));
            if (_best.Count > k) _best.RemoveAt(_best.Count - 1);
        }

        public IEnumerable<T> Items => _best.Select(b => b.Item);
    }
}
