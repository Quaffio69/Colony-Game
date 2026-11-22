using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Items;
using Colony.Core.Trade;
using Godot;

namespace ColonyGame.UI;

/// <summary>
/// F6: trade with the other colonies. Offers and deals (whose move, what each side gives, delivery progress) with
/// Accept / Counter / Decline / Withdraw; and the offer being drawn up: what you give from your stores and what you ask
/// for from theirs (click an item to add it, − + to change the amount, Shift for tens), sent as a new offer or as the
/// counter to one. Agreed deals are carried by each side's traders (work: Trading) to the other's trade depot.
/// </summary>
public partial class TradeWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private int _partner = -1;
    /// <summary>The deal the offer being drawn up answers (0: a new offer).</summary>
    private int _counter;
    private readonly SortedDictionary<int, int> _give = new(), _ask = new();
    private string _shown = "";
    private int _edits;
    private const int ShownStock = 14;

    public TradeWindow(GameUi ui) : base("trade", "Trade", "$", "accent", "F6")
    {
        _ui = ui;
        MinCols = 76;
        Default = new WindowState { X = 4, Y = 3, W = 86, H = 32 };
    }
    public TradeWindow() : this(null!) { }

    protected override void Build() => _shown = "";

    private int Me => _ui.Me;

    public override void Tick()
    {
        var sim = _ui.Sim;
        if (_partner == Me || _partner >= sim.Factions.Count) _partner = -1;
        if (_partner < 0 && sim.Factions.Count > 1) _partner = Me == 0 ? 1 : 0;
        string key = $"{sim.Trades.Version}|{Me}|{_partner}|{_counter}|{_edits}|{sim.Factions.Count}|{sim.Stockpiles.Version}|{sim.Tick / 60}"; // stock counts every 3 s; deals at once
        if (key == _shown) return;
        _shown = key;
        Rebuild();
    }

    /// <summary>After a click: rebuilt next frame (the pressed button may not be freed inside its own signal).</summary>
    private void Changed()
    {
        _edits++;
        Callable.From(() => { _shown = ""; Tick(); }).CallDeferred();
    }

    private void Rebuild()
    {
        var sim = _ui.Sim;
        G.Clear(Header);
        G.Clear(Content);
        G.Clear(Footer);
        if (sim.Factions.Count < 2)
        {
            Content.AddChild(G.Para("There is no other colony to trade with. Start a game with two or more colonies (New colony) " +
                                    "or play online (Multiplayer); F4 plays the next colony in a hot-seat game.", "Dim"));
            return;
        }

        // Header: who to trade with.
        var others = Enumerable.Range(0, sim.Factions.Count).Where(f => f != Me).ToList();
        var bar = new HBoxContainer();
        bar.AddChild(G.Text("Trade with ", "Dim"));
        bar.AddChild(new GlyphSegmented(others.Select(f => sim.Factions[f].Name).ToArray(), Math.Max(0, others.IndexOf(_partner)), i =>
        {
            if (others[i] == _partner) return;
            _partner = others[i];
            ClearOffer();
            Changed();
        }));
        bar.AddChild(G.Spacer());
        int waiting = sim.Trades.All.Count(d => d.State == TradeState.Offered && d.Turn == Me);
        bar.AddChild(G.Text(waiting > 0 ? $"{G.Plural(waiting, "offer")} waiting on you" : "", "Warn"));
        Header.AddChild(bar);

        bool myDepot = sim.Stockpiles.HasDepot(Me);
        if (!myDepot)
            Header.AddChild(G.Para("You have no trade depot: zone one (Build, Zones, Trade depot) so other colonies can deliver what you buy.", "Warn"));

        // Deals: open ones first (newest first), then a few settled ones.
        var mine = sim.Trades.All.Where(d => d.Involves(Me)).ToList();
        var open = mine.Where(d => d.Open).OrderByDescending(d => d.Updated).ThenByDescending(d => d.Id).ToList();
        var closed = mine.Where(d => !d.Open).OrderByDescending(d => d.Updated).ThenByDescending(d => d.Id).Take(4).ToList();
        Content.AddChild(new GlyphSection("Offers and deals"));
        if (open.Count == 0 && closed.Count == 0) Content.AddChild(G.Para("None yet: draw up an offer below.", "Faint"));
        foreach (var d in open.Concat(closed)) DealRow(d);

        Content.AddChild(G.Gap());
        Composer();

        // Footer: send or clear the offer.
        var foot = new HBoxContainer();
        bool any = _give.Count + _ask.Count > 0;
        string partnerName = sim.Factions[_partner].Name;
        var send = G.Button(_counter != 0 ? " Send counter-offer " : " Propose ", Send,
            _counter != 0 ? $"Answer {partnerName}'s offer with these terms: their turn next" : $"Offer these terms to {partnerName}", "Primary");
        send.Disabled = !any;
        foot.AddChild(send);
        foot.AddChild(G.Button(" Clear ", () => { ClearOffer(); Changed(); }, "Start the offer afresh"));
        foot.AddChild(G.Spacer());
        foot.AddChild(G.Text("Agreed deals: traders (work: Trading) carry the goods to the buyer's depot", "Faint"));
        Footer.AddChild(foot);
    }

    // ---- Deals ---------------------------------------------------------------------------------------------

    private void DealRow(TradeDeal d)
    {
        var sim = _ui.Sim;
        int other = d.Other(Me);
        string name = sim.Factions[other].Name;
        var (status, tone) = d.State switch
        {
            TradeState.Offered when d.Turn == Me => (d.Round > 0 ? "they counter: your move" : "your move", "Warn"),
            TradeState.Offered => ("waiting for their answer", "Dim"),
            TradeState.Accepted => ("agreed: delivering", "Info"),
            TradeState.Done => ("done", "Good"),
            TradeState.Declined => (d.Turn == Me ? "you declined" : "they declined", "Faint"),
            _ => (d.Turn == Me ? "they withdrew" : "you withdrew", "Faint"),
        };
        var row = new HBoxContainer();
        row.AddChild(new GlyphChip("$", d.Open ? "accent" : "dim"));
        row.AddChild(G.Text($" #{d.Id} with {name}", d.Open ? "Hi" : "Dim", bold: d.Open, cols: 26));
        row.AddChild(G.Text(status, tone, expand: true));
        if (d.State == TradeState.Offered && d.Turn == Me)
        {
            var deal = d;
            row.AddChild(G.Button(" Accept ", () => { _ui.Tool.Issue(new AcceptTrade(deal.Id)); Changed(); },
                "Agree to these terms: binding, both sides' traders set out", "Primary"));
            row.AddChild(G.Button(" Counter ", () => { LoadOffer(deal); Changed(); }, "Change the terms below and send them back"));
            row.AddChild(G.Button(" Decline ", () => { _ui.Tool.Issue(new RejectTrade(deal.Id)); Changed(); }, "Turn the offer down"));
        }
        else if (d.State == TradeState.Offered)
        {
            var deal = d;
            row.AddChild(G.Button(" Withdraw ", () => { _ui.Tool.Issue(new RejectTrade(deal.Id)); Changed(); }, "Take your offer back"));
        }
        Content.AddChild(row);
        Content.AddChild(Terms("   You give", d, d.From(Me), Me));
        Content.AddChild(Terms("   You get", d, d.From(other), other));
    }

    private Control Terms(string label, TradeDeal d, IEnumerable<TradeLine> lines, int from)
    {
        var sim = _ui.Sim;
        var row = new HBoxContainer();
        row.AddChild(G.Text(label, "Faint", cols: 13));
        var list = lines.ToList();
        if (list.Count == 0)
        {
            row.AddChild(G.Text("nothing", "Faint"));
            return row;
        }
        int[]? stores = d.Open ? Trades.Stores(sim, from) : null;
        var parts = new List<string>();
        foreach (var l in list)
        {
            string s = $"{l.Count} {l.Type.Name.ToLowerInvariant()}";
            if (d.State == TradeState.Offered && stores![l.Type.Index] < l.Count)
                s += from == Me ? $" (you have {stores[l.Type.Index]})" : $" (they have {stores[l.Type.Index]})";
            if (d.State == TradeState.Accepted)
            {
                s += $" ({l.Delivered} delivered{(l.InTransit > 0 ? $", {l.InTransit} on the way" : "")}";
                if (l.Unclaimed > 0 && stores![l.Type.Index] == 0) s += from == Me ? ", none in your stores" : ", they have none yet";
                s += ")";
            }
            parts.Add(s);
        }
        var text = G.Text(string.Join(" · ", parts), d.Open ? "Text" : "Dim", expand: true);
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(text);
        return row;
    }

    // ---- The offer being drawn up ---------------------------------------------------------------------------

    private void Composer()
    {
        var sim = _ui.Sim;
        string partnerName = sim.Factions[_partner].Name;
        Content.AddChild(new GlyphSection(_counter != 0 ? $"Counter-offer to {partnerName} (deal #{_counter})" : $"New offer to {partnerName}"));
        var mineStock = Trades.Stores(sim, Me);
        var theirStock = Trades.Stores(sim, _partner);
        var cols = new HBoxContainer();
        cols.AddThemeConstantOverride("separation", (int)(GlyphTheme.Cell.X * 3));
        cols.AddChild(Side("You give", _give, mineStock, "your stores"));
        cols.AddChild(Side("You ask for", _ask, theirStock, $"{partnerName}'s stores"));
        Content.AddChild(cols);
    }

    private Control Side(string title, SortedDictionary<int, int> terms, int[] stock, string storeName)
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddChild(G.Text(title, "Hi", bold: true));
        if (terms.Count == 0) col.AddChild(G.Text("nothing yet", "Faint"));
        foreach (var (index, count) in terms.ToList())
        {
            var type = new ItemType((ushort)index);
            int i = index;
            var row = new HBoxContainer();
            row.AddChild(new GlyphChip(ItemLook.Glyph(type), ItemLook.Color(type)));
            row.AddChild(G.Text($" {type.Name}", "Text", cols: 18));
            row.AddChild(G.Button(" − ", () => { Step(terms, i, -1); Changed(); }, "One fewer (Shift: ten)", "Ghost"));
            row.AddChild(G.Text(count.ToString(), "Hi", cols: 6, align: HorizontalAlignment.Center));
            row.AddChild(G.Button(" + ", () => { Step(terms, i, 1); Changed(); }, "One more (Shift: ten)", "Ghost"));
            if (count > stock[i]) row.AddChild(G.Text($" {stock[i]} in store", "Warn"));
            row.AddChild(G.Spacer());
            row.AddChild(G.Button(" × ", () => { terms.Remove(i); Changed(); }, "Take it off the offer", "Ghost"));
            col.AddChild(row);
        }
        col.AddChild(G.Gap(0.3f));
        col.AddChild(G.Text($"Add from {storeName}:", "Faint"));
        var top = Enumerable.Range(0, stock.Length).Where(i => stock[i] > 0 && !terms.ContainsKey(i))
            .OrderByDescending(i => stock[i]).ThenBy(i => i).Take(ShownStock).ToList();
        if (top.Count == 0) col.AddChild(G.Text("  nothing in store", "Faint"));
        foreach (int i in top)
        {
            var type = new ItemType((ushort)i);
            int have = stock[i], index = i;
            var row = new GlyphRow { Clickable = true };
            row.AddChild(new GlyphChip(ItemLook.Glyph(type), ItemLook.Color(type)));
            row.AddChild(G.Text($" {type.Name}", "Text", expand: true));
            row.AddChild(G.Text(have.ToString(), "Dim", cols: 7, align: HorizontalAlignment.Right));
            row.Pressed += () => { terms[index] = Math.Min(have, 10); Changed(); };
            row.MouseEntered += () => GlyphStatus.Tip = $"Add {type.Name.ToLowerInvariant()} to the offer ({have} in {storeName})";
            row.MouseExited += () => GlyphStatus.Tip = null;
            col.AddChild(row);
        }
        return col;
    }

    private static void Step(SortedDictionary<int, int> terms, int index, int sign)
    {
        int by = Input.IsKeyPressed(Key.Shift) ? 10 : 1;
        int n = Math.Clamp(terms.GetValueOrDefault(index) + sign * by, 0, Trades.MaxCount);
        if (n == 0) terms.Remove(index);
        else terms[index] = n;
    }

    private void ClearOffer()
    {
        _give.Clear();
        _ask.Clear();
        _counter = 0;
    }

    /// <summary>Loads a deal's terms to counter it.</summary>
    private void LoadOffer(TradeDeal d)
    {
        ClearOffer();
        _partner = d.Other(Me);
        _counter = d.Id;
        foreach (var l in d.Lines) (l.From == Me ? _give : _ask)[l.Type.Index] = l.Count;
    }

    private void Send()
    {
        var lines = _give.Select(e => new TradeTerm(Me, new ItemType((ushort)e.Key).Id, e.Value))
            .Concat(_ask.Select(e => new TradeTerm(_partner, new ItemType((ushort)e.Key).Id, e.Value))).ToArray();
        var terms = new TradeTerms(lines);
        _ui.Tool.Issue(_counter != 0 ? new CounterTrade(_counter, terms) : new ProposeTrade(_partner, terms));
        ClearOffer();
        Changed();
    }
}
