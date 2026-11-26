using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Items;
using Godot;

namespace ColonyGame.UI;

/// <summary>
/// Stockpile settings (F8, or Settings in Inspect on a stockpile): one stockpile at a time, stepped through with ‹ ›. Its
/// name; priority (1 filled first); how far it draws goods from (a slider, 500 tiles by default, or any distance); a cap
/// on each item; reserve (kept for workshops and trade); which workshops may draw from it (all, only these, all but
/// these); and the items it takes, by category (folding open to single items). Every change is a
/// <see cref="ConfigureStockpile"/> command.
/// </summary>
public partial class StockpileWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private int _zoneId = -1, _named = -1;
    private string? _shown;
    private readonly HashSet<string> _open = new(); // categories folded open
    private LineEdit _name = null!;
    private Label _which = null!;

    /// <summary>Haul-from notches (tiles); the last is any distance (0).</summary>
    private static readonly int[] Reaches = [10, 20, 30, 50, 75, 100, 150, 200, 300, 400, 500, 750, 1000, 1500, 2000, 0];
    /// <summary>Cap steps (0: any amount).</summary>
    private static readonly int[] Caps = [0, 10, 25, 50, 100, 200, 500, 1000, 2000, 5000];

    public StockpileWindow(GameUi ui) : base("stockpile", "Stockpiles", "≡", "warn", "F8")
    {
        _ui = ui;
        MinCols = 60;
        Default = new WindowState { X = -2, Y = 3, W = 66, H = 40 };
    }
    public StockpileWindow() : this(null!) { }

    /// <summary>Opens on one stockpile (from Inspect).</summary>
    public void ShowZone(int id)
    {
        _zoneId = id;
        _shown = null;
        _ui.OpenWindow(Id);
    }

    private List<Stockpile> Mine => _ui.Sim.Stockpiles.Zones(_ui.Me).ToList();

    private void Issue(StockpileOption option, int value = 0, int arg = 0, string text = "") =>
        _ui.Tool.Issue(new ConfigureStockpile(_zoneId, option, value, arg, text));

    protected override void Build()
    {
        var bar = new HBoxContainer();
        bar.AddChild(G.Button("‹", () => Step(-1), "The previous stockpile"));
        _name = new LineEdit { MaxLength = Stockpile.MaxNameLength, SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "Name", CaretBlink = true };
        _name.TextSubmitted += _ => Rename();
        _name.FocusExited += Rename;
        _name.WithTip("Its name: type and press Enter");
        bar.AddChild(_name);
        bar.AddChild(G.Button("›", () => Step(+1), "The next stockpile"));
        _which = G.Text("", "Faint", cols: 8, align: HorizontalAlignment.Right);
        bar.AddChild(_which);
        Header.AddChild(bar);
        Header.AddChild(G.Gap(0.25f));
        Footer.AddChild(G.Gap(0.25f));
        Footer.AddChild(G.Para("Zone stockpiles with Build ▸ Zones; a box zoned against one extends it. Click one with no tool in hand, then Settings in Inspect, to open it here.", "Faint"));
        _named = -1;
        _shown = null;
    }

    private void Rename()
    {
        if (_ui.Sim.Stockpiles.ZoneById(_zoneId) is { } z && _name.Text.Trim().Length > 0 && _name.Text.Trim() != z.Name)
            Issue(StockpileOption.Name, text: _name.Text);
    }

    private void Step(int by)
    {
        var all = Mine;
        if (all.Count == 0) return;
        int i = all.FindIndex(z => z.Id == _zoneId);
        _zoneId = all[((i < 0 ? 0 : i + by) % all.Count + all.Count) % all.Count].Id;
        _shown = null;
    }

    public override void Tick()
    {
        var sim = _ui.Sim;
        var all = Mine;
        if (sim.Stockpiles.ZoneById(_zoneId) is not { } zone || zone.Faction != _ui.Me)
        {
            zone = all.FirstOrDefault();
            _zoneId = zone?.Id ?? -1;
        }
        if (zone != null && _named != zone.Id && !_name.HasFocus() || zone != null && !_name.HasFocus() && _name.Text != zone.Name)
        {
            _name.Text = zone.Name;
            _named = zone.Id;
        }
        _name.Editable = zone != null;
        _which.Text = zone == null ? "" : $"{all.IndexOf(zone) + 1} of {all.Count}";
        string key = $"{_zoneId}|{sim.Stockpiles.Version}|{sim.Rooms.Version}|{string.Join(",", _open.Order())}";
        if (key == _shown) return;
        _shown = key;
        G.Clear(Content);
        if (zone == null)
        {
            Content.AddChild(G.Para("No stockpiles yet. Zone one with Build ▸ Zones ▸ Stockpile: haulers bring loose goods there.", "Faint"));
            return;
        }
        General(zone);
        Content.AddChild(G.Gap());
        Workshops(zone);
        Content.AddChild(G.Gap());
        Items(zone);
    }

    private void General(Stockpile zone)
    {
        Content.AddChild(new GlyphSection("Stockpile"));
        Content.AddChild(new GlyphKV("≡", GlyphTheme.Tok("item"), "Size", G.Plural(zone.CellCount, "cell")));

        var priority = new HBoxContainer();
        priority.AddChild(G.Text("Priority", cols: 12));
        priority.AddChild(new GlyphSegmented(["1", "2", "3", "4", "5"], zone.Priority - 1, i => Issue(StockpileOption.Priority, i + 1))
        {
            Tips = ["1: filled first, and takes goods from stockpiles of lower priority", "2", "3: the usual", "4", "5: filled last"],
        });
        priority.AddChild(G.Spacer());
        priority.AddChild(G.Text("1 filled first", "Faint"));
        Content.AddChild(priority);

        int reach = Array.IndexOf(Reaches, zone.Reach);
        if (reach < 0) reach = Array.FindIndex(Reaches, r => r == 0 || r >= zone.Reach);
        Content.AddChild(new GlyphNotchSlider("Haul from within", Reaches.Select(r => r == 0 ? "any distance" : $"{r} tiles").ToArray(), reach,
            i => Issue(StockpileOption.Reach, Reaches[i])).WithTip("Haulers bring it only goods lying this close (straight line); the far end: any distance"));

        var cap = new HBoxContainer();
        cap.AddChild(G.Text("Keep at most", cols: 12));
        int ci = Array.FindIndex(Caps, c => c >= zone.Cap);
        cap.AddChild(G.Button("−", () => Issue(StockpileOption.Cap, Caps[Math.Max(0, (ci < 0 ? Caps.Length : ci) - 1)]), "Lower the cap"));
        cap.AddChild(G.Text(zone.Cap == 0 ? "any amount" : zone.Cap.ToString(), "Hi", bold: true, cols: 11, align: HorizontalAlignment.Center));
        cap.AddChild(G.Button("+", () => Issue(StockpileOption.Cap, Caps[Math.Min(Caps.Length - 1, ci < 0 ? Caps.Length - 1 : zone.Cap == Caps[ci] ? ci + 1 : ci)]), "Raise the cap"));
        cap.AddChild(G.Spacer());
        cap.AddChild(G.Text("of each item", "Faint"));
        Content.AddChild(cap.WithTip("Haulers stop bringing an item once it holds this many (a pantry needn't hoard stone)"));

        Content.AddChild(new GlyphSwitch("Reserve: only workshops and trade take from it", zone.Reserve, on => Issue(StockpileOption.Reserve, on ? 1 : 0))
            .WithTip("Colonists don't eat, burn, wear or furnish from a reserve: seed stock, trade goods, a factory's own store"));
    }

    private void Workshops(Stockpile zone)
    {
        Content.AddChild(new GlyphSection("Workshops that may draw from it"));
        Content.AddChild(new GlyphSegmented([" All ", " Only these ", " All but these "], (int)zone.Access, i => Issue(StockpileOption.Access, i))
        {
            Tips = ["Every workshop's input crates may take from it", "A whitelist: only the workshops switched on", "A blacklist: every workshop but those switched on"],
        });
        if (zone.Access == WorkshopAccess.All)
        {
            Content.AddChild(G.Para("Every workshop of the colony may fill its input crates from it.", "Faint"));
            return;
        }
        var shops = _ui.Sim.Rooms.All.Where(r => r.Faction == _ui.Me && !r.Type.IsHome).OrderBy(r => r.Id).ToList();
        if (shops.Count == 0) Content.AddChild(G.Para("No workshops yet.", "Faint"));
        foreach (var room in shops)
        {
            var r = room;
            Content.AddChild(new GlyphSwitch(r.Name, zone.ListsWorkshop(r.Id), on => Issue(StockpileOption.Workshop, r.Id, on ? 1 : 0))
                .WithTip(zone.Access == WorkshopAccess.Only ? $"On: {r.Name} may draw from it" : $"On: {r.Name} may not draw from it"));
        }
    }

    private void Items(Stockpile zone)
    {
        var defs = Catalog.Items.Where(d => d.Kind != ItemKind.Machine && d.Kind != ItemKind.Worker && d.Kind != ItemKind.Education).ToList();
        int taken = defs.Count(d => zone.Accepts(d.Type));
        Content.AddChild(new GlyphSection("Items it takes"));
        var bulk = new HBoxContainer();
        bulk.AddChild(G.Button(" Everything ", () => Issue(StockpileOption.Everything, 1), "Take every item"));
        bulk.AddChild(G.Button(" Nothing ", () => Issue(StockpileOption.Everything, 0), "Take nothing (then switch on what it should)"));
        bulk.AddChild(G.Spacer());
        bulk.AddChild(G.Text($"{taken} of {defs.Count} items", "Faint"));
        Content.AddChild(bulk);
        foreach (var group in defs.GroupBy(d => d.Category).OrderBy(g => g.Key))
        {
            string cat = group.Key;
            int on = group.Count(d => zone.Accepts(d.Type)), all = group.Count();
            bool open = _open.Contains(cat);
            var row = new HBoxContainer();
            row.AddChild(G.Button(open ? "▾" : "▸", () => { if (!_open.Remove(cat)) _open.Add(cat); }, open ? "Fold up" : "Show each item", "Ghost"));
            string label = $"{char.ToUpperInvariant(cat[0])}{cat[1..]}  {(on == all ? "all" : on == 0 ? "none" : $"{on} of {all}")}";
            row.AddChild(new GlyphSwitch(label, on == all, v => Issue(StockpileOption.Category, v ? 1 : 0, text: cat))
                .WithTip(on > 0 && on < all ? $"Some {cat} items: switch on to take them all" : $"Every {cat} item"));
            Content.AddChild(row);
            if (!open) continue;
            foreach (var def in group.OrderBy(d => d.Name))
            {
                var t = def.Type;
                var item = new HBoxContainer();
                item.AddChild(new Control { CustomMinimumSize = new Vector2(3 * GlyphTheme.Cw, 0), MouseFilter = MouseFilterEnum.Ignore });
                item.AddChild(new GlyphChip(ItemLook.Glyph(t), ItemLook.Color(t)));
                item.AddChild(G.Text(" ", cols: 1));
                item.AddChild(new GlyphSwitch(def.Name, zone.Accepts(t), v => Issue(StockpileOption.Item, v ? 1 : 0, text: def.Id)));
                Content.AddChild(item);
            }
        }
    }
}
