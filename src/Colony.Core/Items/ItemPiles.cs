using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Items;

/// <summary>
/// A stack of one item type lying in a cell. <see cref="Reserved"/> units are promised to a hauler coming to
/// pick them up; <see cref="Incoming"/> units are promised space for a hauler delivering here (a pile can
/// exist with Count 0 while a delivery is on its way, which also fixes the cell's type).
/// </summary>
public record struct ItemPile(ItemType Type, int Count, int Reserved, int Incoming)
{
    public readonly int Available => Count - Reserved;
    public readonly int Room => ItemInfo.StackLimit(Type) - Count - Incoming;
}

/// <summary>
/// All item piles in the world, at most one per cell, each resting on a floor (solid ground or a stair).
/// Counts, not objects: memory grows with piles, not with items, so thousands of workers moving goods stays
/// cheap. Deterministic: iteration order depends only on the order of operations.
/// </summary>
public sealed class ItemPiles
{
    private const int SpillRadius = 4;

    private readonly VoxelWorld _world;
    private readonly Dictionary<TilePos, ItemPile> _piles = new();

    public ItemPiles(VoxelWorld world) => _world = world;

    /// <summary>Increments on every change, including reservations.</summary>
    public int Version { get; private set; }
    /// <summary>Increments only when piles appear, move, vanish or change count (for views).</summary>
    public int LayoutVersion { get; private set; }

    public IReadOnlyDictionary<TilePos, ItemPile> All => _piles;
    public int Count => _piles.Count;

    public bool TryGet(TilePos p, out ItemPile pile) => _piles.TryGetValue(p, out pile);

    /// <summary>Total units of each type lying in cells matching <paramref name="where"/> (all cells if null).</summary>
    public int[] Totals(Func<TilePos, bool>? where = null)
    {
        var totals = new int[ItemInfo.TypeCount];
        foreach (var (p, pile) in _piles)
            if (where is null || where(p)) totals[(int)pile.Type] += pile.Count;
        return totals;
    }

    /// <summary>
    /// Drops items at <paramref name="at"/>: they fall to the floor below, join a matching pile with room, and
    /// spill onto nearby floor when it is full. Anything that finds no room within a few tiles is piled onto
    /// the landing cell regardless (never lost).
    /// </summary>
    public void Drop(TilePos at, ItemType type, int count)
    {
        if (count <= 0) return;
        var landing = Settle(at);
        foreach (var cell in SpillOrder(landing))
        {
            int room = RoomAt(cell, type);
            if (room <= 0) continue;
            int n = Math.Min(room, count);
            AddCount(cell, type, n);
            count -= n;
            if (count == 0) return;
        }
        // Everything nearby is full: overfill the nearest cell that is empty or holds the same type.
        foreach (var cell in SpillOrder(landing, SpillRadius * 4))
        {
            if (_piles.TryGetValue(cell, out var pile) && pile.Type != type) continue;
            if (!Movement.IsStandable(_world, cell)) continue;
            AddCount(cell, type, count);
            return;
        }
    }

    /// <summary>Units of <paramref name="type"/> that could still be added to the cell (counting promised deliveries).</summary>
    public int RoomAt(TilePos p, ItemType type)
    {
        if (!Movement.IsStandable(_world, p)) return 0;
        if (!_piles.TryGetValue(p, out var pile)) return ItemInfo.StackLimit(type);
        return pile.Type == type ? Math.Max(0, pile.Room) : 0;
    }

    /// <summary>Promises <paramref name="amount"/> units of the pile to a hauler. False if not available.</summary>
    public bool Reserve(TilePos p, int amount)
    {
        if (!_piles.TryGetValue(p, out var pile) || pile.Available < amount) return false;
        _piles[p] = pile with { Reserved = pile.Reserved + amount };
        Version++;
        return true;
    }

    public void Unreserve(TilePos p, int amount)
    {
        if (!_piles.TryGetValue(p, out var pile)) return;
        Store(p, pile with { Reserved = Math.Max(0, pile.Reserved - amount) });
    }

    /// <summary>Promises space for an incoming delivery. False if there's no longer room.</summary>
    public bool ReserveIncoming(TilePos p, ItemType type, int amount)
    {
        if (RoomAt(p, type) < amount) return false;
        var pile = _piles.TryGetValue(p, out var existing) ? existing : new ItemPile(type, 0, 0, 0);
        _piles[p] = pile with { Incoming = pile.Incoming + amount };
        Version++;
        return true;
    }

    public void ReleaseIncoming(TilePos p, int amount)
    {
        if (!_piles.TryGetValue(p, out var pile)) return;
        Store(p, pile with { Incoming = Math.Max(0, pile.Incoming - amount) });
    }

    /// <summary>Takes up to <paramref name="amount"/> reserved units for a hauler; releases that reservation.</summary>
    public int TakeReserved(TilePos p, int amount)
    {
        if (!_piles.TryGetValue(p, out var pile)) return 0;
        int taken = Math.Min(amount, pile.Count);
        Store(p, pile with { Count = pile.Count - taken, Reserved = Math.Max(0, pile.Reserved - amount) });
        if (taken > 0) LayoutVersion++;
        return taken;
    }

    /// <summary>
    /// Completes a delivery: adds up to <paramref name="amount"/> units (as room allows, counting the delivery's
    /// own reservation) and releases that reservation. Returns how many were added.
    /// </summary>
    public int Deliver(TilePos p, ItemType type, int amount)
    {
        ReleaseIncoming(p, amount);
        int n = Math.Min(amount, RoomAt(p, type));
        if (n > 0) AddCount(p, type, n);
        return n;
    }

    /// <summary>
    /// Re-homes the pile at <paramref name="p"/> after the ground changed: pushed up out of rock that filled
    /// its cell, or dropped to the next floor if its own was removed. Its reservations are cancelled (haulers
    /// notice when they arrive).
    /// </summary>
    public void Resettle(TilePos p)
    {
        if (!_piles.TryGetValue(p, out var pile)) return;
        var to = Settle(p);
        if (to == p) return;
        _piles.Remove(p);
        Version++;
        LayoutVersion++;
        if (pile.Count > 0) Drop(to, pile.Type, pile.Count);
    }

    /// <summary>Where something released at <paramref name="p"/> comes to rest.</summary>
    public TilePos Settle(TilePos p)
    {
        while (p.Z < _world.Height - 1 && TileInfo.IsSolid(_world.Get(p))) p = p.Offset(0, 0, 1);
        while (p.Z > 1 && !Movement.IsStandable(_world, p)) p = p.Offset(0, 0, -1);
        return p;
    }

    /// <summary>The landing cell, then rings of nearby cells (each settled onto its floor), in a fixed order.</summary>
    private IEnumerable<TilePos> SpillOrder(TilePos landing, int radius = SpillRadius)
    {
        yield return landing;
        for (int r = 1; r <= radius; r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            var c = landing.Offset(dx, dy, 0);
            if (!_world.InBounds(c)) continue;
            var s = Settle(c);
            if (Math.Abs(s.Z - landing.Z) <= 1) yield return s; // don't tumble off cliffs or climb walls
        }
    }

    private void AddCount(TilePos p, ItemType type, int n)
    {
        var pile = _piles.TryGetValue(p, out var existing) ? existing : new ItemPile(type, 0, 0, 0);
        if (pile.Type != type) throw new InvalidOperationException($"{p} holds {pile.Type}, not {type}");
        _piles[p] = pile with { Count = pile.Count + n };
        Version++;
        LayoutVersion++;
    }

    private void Store(TilePos p, ItemPile pile)
    {
        if (pile is { Count: 0, Reserved: 0, Incoming: 0 }) _piles.Remove(p);
        else _piles[p] = pile;
        Version++;
    }
}
