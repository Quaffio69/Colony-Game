using Colony.Core.Agents;
using Colony.Core.Items;

namespace Colony.Core.Trade;

/// <summary>Where a deal stands.</summary>
public enum TradeState : byte
{
    /// <summary>On the table: the colony whose <see cref="TradeDeal.Turn"/> it is may accept, counter or decline.</summary>
    Offered,
    /// <summary>Agreed: each side's traders carry their goods to the other's trade depot.</summary>
    Accepted,
    /// <summary>Everything delivered.</summary>
    Done,
    /// <summary>Turned down by the colony it was offered to.</summary>
    Declined,
    /// <summary>Taken back by the colony that made the last offer.</summary>
    Withdrawn,
}

/// <summary>
/// One line of a deal: <see cref="Count"/> of an item that colony <see cref="From"/> gives the other. Once the deal is
/// accepted, <see cref="Delivered"/> counts what has reached the other colony's depot and <see cref="InTransit"/> what
/// its traders are carrying there now.
/// </summary>
public sealed class TradeLine
{
    public int From { get; init; }
    public ItemType Type { get; init; }
    public int Count { get; init; }
    public int Delivered { get; internal set; }
    public int InTransit { get; internal set; }
    /// <summary>Still to be picked up by a trader.</summary>
    public int Unclaimed => Count - Delivered - InTransit;
    public bool Complete => Delivered >= Count;
}

/// <summary>
/// A deal between two colonies, brokered in turns: one colony offers terms (what each side gives), the other accepts,
/// declines or counters with new terms (then it is the first's turn again). Accepted, it is binding: each side's
/// traders (<see cref="Work.Trading"/>) carry their goods from their own stores to the other side's trade depot, and
/// the deal is done when every line is delivered. Goods are not set aside on acceptance: traders send what the stores
/// hold, and wait for the rest.
/// </summary>
public sealed class TradeDeal
{
    public int Id { get; init; }
    /// <summary>The colony that made the first offer, and its partner.</summary>
    public int A { get; init; }
    public int B { get; init; }
    public List<TradeLine> Lines { get; internal set; } = new();
    public TradeState State { get; internal set; }
    /// <summary>The colony that must answer the current offer.</summary>
    public int Turn { get; internal set; }
    /// <summary>Counter-offers made so far.</summary>
    public int Round { get; internal set; }
    public long Opened { get; init; }
    public long Updated { get; internal set; }

    public bool Involves(int faction) => A == faction || B == faction;
    public int Other(int faction) => faction == A ? B : A;
    public bool Open => State is TradeState.Offered or TradeState.Accepted;
    public IEnumerable<TradeLine> From(int faction) => Lines.Where(l => l.From == faction);
}

/// <summary>A term as a command carries it: who gives how many of which item (by catalog id).</summary>
public readonly record struct TradeTerm(int From, string Item, int Count);

/// <summary>A deal's terms in a command: compared by value (records compare arrays by reference).</summary>
public sealed record TradeTerms(IReadOnlyList<TradeTerm> Lines)
{
    public bool Equals(TradeTerms? other) => other is not null && Lines.SequenceEqual(other.Lines);
    public override int GetHashCode()
    {
        int h = 17;
        foreach (var t in Lines) h = h * 31 + t.GetHashCode();
        return h;
    }
}

/// <summary>Every deal between colonies, by id. Commands drive it (<see cref="Commands.ProposeTrade"/> ...); traders
/// deliver through <see cref="Claim"/>, <see cref="Unclaim"/> and <see cref="Delivered"/>.</summary>
public sealed class Trades
{
    private readonly List<TradeDeal> _deals = new();
    private int _nextId = 1;
    /// <summary>Settled deals kept for the record (oldest dropped first).</summary>
    public const int HistoryKept = 40;
    /// <summary>Most lines a deal may have, and most of one item on a line.</summary>
    public const int MaxLines = 16, MaxCount = 100_000;

    public IReadOnlyList<TradeDeal> All => _deals;
    /// <summary>Bumps on every change (offers, answers, deliveries): views and idle traders watch it.</summary>
    public int Version { get; private set; }

    public TradeDeal? Get(int id) => _deals.FirstOrDefault(d => d.Id == id);

    /// <summary>Deals whose goods this colony owes and its traders could still pick up.</summary>
    public IEnumerable<(TradeDeal Deal, TradeLine Line)> Owed(int faction)
    {
        foreach (var d in _deals)
        {
            if (d.State != TradeState.Accepted) continue;
            foreach (var l in d.Lines)
                if (l.From == faction && l.Unclaimed > 0) yield return (d, l);
        }
    }

    // ---- Brokering (called by commands; each returns the message for the issuing player) -----------------

    /// <summary>Turns command terms into deal lines, merging repeats; null and a reason when they don't make a deal.</summary>
    private static List<TradeLine>? Lines(ColonySim sim, int a, int b, TradeTerms terms, out string why)
    {
        var merged = new List<TradeLine>();
        foreach (var t in terms.Lines)
        {
            if (t.From != a && t.From != b) { why = "Every line must be given by one of the two colonies"; return null; }
            if (!Industry.Catalog.TryItem(t.Item, out var type)) { why = $"No such item: {t.Item}"; return null; }
            if (t.Count <= 0) continue;
            int at = merged.FindIndex(l => l.From == t.From && l.Type == type);
            int count = Math.Min(MaxCount, t.Count + (at >= 0 ? merged[at].Count : 0));
            var line = new TradeLine { From = t.From, Type = type, Count = count };
            if (at >= 0) merged[at] = line;
            else merged.Add(line);
        }
        if (merged.Count == 0) { why = "An offer needs something in it"; return null; }
        if (merged.Count > MaxLines) { why = $"At most {MaxLines} lines in one deal"; return null; }
        why = "";
        return merged;
    }

    public string Propose(ColonySim sim, int from, int to, TradeTerms terms)
    {
        if (to == from || to < 0 || to >= sim.Factions.Count) return "Choose another colony to trade with";
        if (Lines(sim, from, to, terms, out var why) is not { } lines) return why;
        var deal = new TradeDeal { Id = _nextId++, A = from, B = to, Lines = lines, Turn = to, Opened = sim.Tick, Updated = sim.Tick };
        _deals.Add(deal);
        Version++;
        sim.Announce($"{sim.Factions[from].Name} offers a trade: {Summary(deal, to)} (Trade, F6)", to);
        return $"Offer sent to {sim.Factions[to].Name}";
    }

    public string Counter(ColonySim sim, int id, int by, TradeTerms terms)
    {
        if (Answerable(sim, id, by, out var deal) is { } no) return no;
        if (Lines(sim, deal!.A, deal.B, terms, out var why) is not { } lines) return why;
        deal.Lines = lines;
        deal.Turn = deal.Other(by);
        deal.Round++;
        deal.Updated = sim.Tick;
        Version++;
        sim.Announce($"{sim.Factions[by].Name} counters your offer: {Summary(deal, deal.Turn)} (Trade, F6)", deal.Turn);
        return $"Counter-offer sent to {sim.Factions[deal.Turn].Name}";
    }

    public string Accept(ColonySim sim, int id, int by)
    {
        if (Answerable(sim, id, by, out var deal) is { } no) return no;
        int other = deal!.Other(by);
        // Each side that receives goods needs somewhere to receive them.
        foreach (int side in new[] { by, other })
            if (deal.Lines.Any(l => l.From != side) && !sim.Stockpiles.HasDepot(side))
                return side == by ? "Zone a trade depot first (Build, Zones): their traders deliver there"
                    : $"{sim.Factions[side].Name} has no trade depot to deliver to yet";
        deal.State = TradeState.Accepted;
        deal.Updated = sim.Tick;
        Version++;
        sim.Announce($"{sim.Factions[by].Name} accepts the trade: traders set out", other);
        sim.Announce($"Trade agreed with {sim.Factions[other].Name}: traders set out", by);
        return "Trade agreed: traders set out";
    }

    /// <summary>Turns down an offer (whose turn it is) or takes one back (the colony that made it).</summary>
    public string Reject(ColonySim sim, int id, int by)
    {
        var deal = Get(id);
        if (deal is null || !deal.Involves(by)) return "No such offer";
        if (deal.State != TradeState.Offered) return deal.State == TradeState.Accepted ? "An agreed trade is binding" : "That offer is closed";
        bool declined = deal.Turn == by;
        deal.State = declined ? TradeState.Declined : TradeState.Withdrawn;
        deal.Updated = sim.Tick;
        Version++;
        Prune();
        int other = deal.Other(by);
        sim.Announce(declined ? $"{sim.Factions[by].Name} declines your offer" : $"{sim.Factions[by].Name} withdraws its offer", other);
        return declined ? "Offer declined" : "Offer withdrawn";
    }

    private string? Answerable(ColonySim sim, int id, int by, out TradeDeal? deal)
    {
        deal = Get(id);
        if (deal is null || !deal.Involves(by)) return "No such offer";
        if (deal.State != TradeState.Offered) return "That offer is closed";
        if (deal.Turn != by) return $"Waiting for {sim.Factions[deal.Turn].Name} to answer";
        return null;
    }

    // ---- Deliveries (traders) -------------------------------------------------------------------------------

    /// <summary>A trader takes <paramref name="count"/> of a line to carry.</summary>
    internal void Claim(TradeLine line, int count)
    {
        line.InTransit += count;
        Version++;
    }

    /// <summary>A trader gives up (or picked up less): the goods are owed again.</summary>
    internal void Unclaim(TradeLine line, int count)
    {
        line.InTransit = Math.Max(0, line.InTransit - count);
        Version++;
    }

    /// <summary>A trader set its load down at the other colony's depot.</summary>
    internal void Delivered(ColonySim sim, TradeDeal deal, TradeLine line, int count)
    {
        line.InTransit = Math.Max(0, line.InTransit - count);
        line.Delivered += count;
        Version++;
        if (deal.State != TradeState.Accepted || !deal.Lines.All(l => l.Complete)) return;
        deal.State = TradeState.Done;
        deal.Updated = sim.Tick;
        Prune();
        sim.Announce($"Trade with {sim.Factions[deal.B].Name} complete: everything delivered", deal.A);
        sim.Announce($"Trade with {sim.Factions[deal.A].Name} complete: everything delivered", deal.B);
    }

    private void Prune()
    {
        int closed = _deals.Count(d => !d.Open);
        for (int i = 0; i < _deals.Count && closed > HistoryKept; i++)
            if (!_deals[i].Open) { _deals.RemoveAt(i--); closed--; }
    }

    // ---- Reading ------------------------------------------------------------------------------------------

    /// <summary>"you give 20 planks; you get 5 iron ingots", as seen by <paramref name="viewer"/>.</summary>
    public static string Summary(TradeDeal deal, int viewer)
    {
        string List(IEnumerable<TradeLine> lines)
        {
            var parts = lines.Select(l => $"{l.Count} {l.Type.Name.ToLowerInvariant()}").ToList();
            return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
        }
        return $"you give {List(deal.From(viewer))}; you get {List(deal.From(deal.Other(viewer)))}";
    }

    /// <summary>What a colony could send: units of each item in its own stores (not factory inputs or building materials).</summary>
    public static int[] Stores(ColonySim sim, int faction)
    {
        var totals = new int[ItemInfo.TypeCount];
        foreach (var (p, pile) in sim.Items.All)
            if (pile.Available > 0 && Sendable(sim, p, pile.Type, faction)) totals[pile.Type.Index] += pile.Available;
        return totals;
    }

    /// <summary>Goods at <paramref name="p"/> this colony may send: its own stores or loose goods (not factory inputs or
    /// building materials), never another colony's.</summary>
    public static bool Sendable(ColonySim sim, World.TilePos p, ItemType type, int faction) =>
        (!sim.Stockpiles.Contains(p) || sim.Stockpiles.OwnerOf(p) == faction) &&
        (sim.Rooms.PartAt(p) is not { } part || part.Room.Faction == faction) &&
        Hauling.RoleOf(p, type, sim.Stockpiles, sim.Rooms, sim.Constructions) is not (StorageRole.Construction or StorageRole.Input);

    // ---- Snapshot -------------------------------------------------------------------------------------------

    /// <summary>Nothing is in transit once every colonist has put its load down.</summary>
    internal void Settle()
    {
        foreach (var d in _deals)
        foreach (var l in d.Lines) l.InTransit = 0;
    }

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(_nextId); w.Write(Version);
        w.Write(_deals.Count);
        foreach (var d in _deals)
        {
            w.Write(d.Id); w.Write(d.A); w.Write(d.B); w.Write((byte)d.State); w.Write(d.Turn); w.Write(d.Round);
            w.Write(d.Opened); w.Write(d.Updated);
            w.Write(d.Lines.Count);
            foreach (var l in d.Lines) { w.Write(l.From); w.Write(l.Type.Index); w.Write(l.Count); w.Write(l.Delivered); w.Write(l.InTransit); }
        }
    }

    internal void SnapLoad(BinaryReader r)
    {
        _nextId = r.ReadInt32(); Version = r.ReadInt32();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var d = new TradeDeal
            {
                Id = r.ReadInt32(), A = r.ReadInt32(), B = r.ReadInt32(), State = (TradeState)r.ReadByte(), Turn = r.ReadInt32(),
                Round = r.ReadInt32(), Opened = r.ReadInt64(), Updated = r.ReadInt64(),
            };
            int lines = r.ReadInt32();
            for (int k = 0; k < lines; k++)
                d.Lines.Add(new TradeLine { From = r.ReadInt32(), Type = new ItemType(r.ReadUInt16()), Count = r.ReadInt32(), Delivered = r.ReadInt32(), InTransit = r.ReadInt32() });
            _deals.Add(d);
        }
    }
}
