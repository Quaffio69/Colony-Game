using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core;
using Colony.Core.Agents;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.World;
using Godot;

namespace ColonyGame.UI;

/// <summary>Colour of an item kind (its catalog colour), and a glyph for its category.</summary>
public static class ItemLook
{
    public static Color Color(ItemType t)
    {
        uint c = t.Def.Color;
        return new Color(((c >> 16) & 0xff) / 255f, ((c >> 8) & 0xff) / 255f, (c & 0xff) / 255f);
    }

    public static string Glyph(ItemType t)
    {
        if (t == ItemType.Log) return "♣";
        if (t == ItemType.Plank) return "≡";
        if (t == ItemType.Stone) return "•";
        if (t.Def.Nutrition > 0) return "%";
        return t.Def.Category switch
        {
            "raw" => "•",
            "material" => "≡",
            "metal" => "£",
            "component" => "÷",
            "textile" => "≈",
            "provision" => "¡",
            "culture" => "♦",
            "home" => "⌂",
            "knowledge" => "Θ",
            "arcane" => "☼",
            "mechanism" => "◘",
            "military" => "▲",
            "wonder" => "‼",
            _ => "•",
        };
    }
}

// ---- Colonists (C) ------------------------------------------------------------------------------------------

/// <summary>Everyone: what they do and their mood (Overview), food and rest (Needs), schooling and family (Family). Click to find.</summary>
public partial class ColonistsWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private int _tab, _selected = -1;
    private string _roster = "";
    private readonly List<(Colonist C, GlyphRow Row, Label A, Label B, GlyphBar Bar1, GlyphBar Bar2)> _rows = new();
    public static readonly string[] Tabs = ["Overview", "Needs", "Family"];

    public ColonistsWindow(GameUi ui) : base("colonists", "Colonists", "☺", "accent", "C")
    {
        _ui = ui;
        MinCols = 44;
        Default = new WindowState { X = 2, Y = 2, W = 66, H = 17 };
    }
    public ColonistsWindow() : this(null!) { }

    protected override void Build()
    {
        var bar = new HBoxContainer();
        bar.AddChild(new GlyphSegmented(Tabs, _tab, i => { _tab = i; _roster = ""; Tick(); }));
        bar.AddChild(G.Spacer());
        bar.AddChild(G.Button(" Find all ", _ui.Main.FocusHome, "Look at the colony (Home key)"));
        Header.AddChild(bar);
        Header.AddChild(G.Gap(0.25f));
        _roster = "";
    }

    public override void Tick()
    {
        var sim = _ui.Sim;
        string roster = _tab + ":" + string.Join(",", _ui.Mine.Select(c => c.Id) + "/" + _ui.Me);
        if (roster != _roster)
        {
            _roster = roster;
            BuildRows();
        }
        foreach (var r in _rows) Update(r);
    }

    private void BuildRows()
    {
        G.Clear(Content);
        _rows.Clear();
        var heads = new HBoxContainer();
        heads.AddChild(G.Text("", cols: 1));
        heads.AddChild(G.Text("Name", "Faint", cols: 14));
        heads.AddChild(G.Text(_tab switch { 0 => "Doing", 1 => "Food", _ => "Schooling" }, "Faint", cols: _tab == 1 ? 10 : 14));
        heads.AddChild(G.Text(_tab switch { 0 => "Mood", 1 => "Rest", _ => "Family" }, "Faint", expand: true));
        Content.AddChild(heads);
        foreach (var c in _ui.Mine)
        {
            var colonist = c;
            var row = new GlyphRow { Clickable = true };
            row.AddChild(new GlyphChip(c.IsAutomaton ? "◘" : "☺", GlyphTheme.Person(c.Id)));
            row.AddChild(G.Text(c.Name, "Text", cols: 14));
            var a = G.Text("", "Dim", cols: _tab == 1 ? 0 : 14);
            var bar1 = new GlyphBar(0, 8, null, "100%");
            var bar2 = new GlyphBar(0, 8, null, "100%");
            var b = G.Text("", "Dim", expand: true);
            switch (_tab)
            {
                case 0: row.AddChild(a); row.AddChild(bar1); bar1.SizeFlagsHorizontal = SizeFlags.ExpandFill; break;
                case 1: row.AddChild(bar1); row.AddChild(bar2); row.AddChild(b); break;
                default: row.AddChild(a); row.AddChild(b); break;
            }
            row.Pressed += () =>
            {
                _selected = colonist.Id;
                _ui.Main.FocusOn(colonist.Position);
                _ui.Main.Selection.Select(new PickColonist(colonist)); // and show it in Inspect
                foreach (var r in _rows) r.Row.Selected = r.C.Id == _selected;
            };
            row.MouseEntered += () => GlyphStatus.Tip = $"{colonist.Name}: {colonist.Describe()} · likes {colonist.DescribeLikes(_ui.Sim.Tick)} · click to find";
            row.MouseExited += () => GlyphStatus.Tip = null;
            row.Selected = c.Id == _selected;
            Content.AddChild(row);
            _rows.Add((c, row, a, b, bar1, bar2));
        }
    }

    public static string Doing(Colonist c) =>
        c.IsChild && c.Task == ColonistTask.None ? "Playing"
        : c.CurrentWork is { } w ? Colonist.WorkNames[(int)w]
        : c.Task == ColonistTask.Sleep ? "Sleeping"
        : c.Task == ColonistTask.Eat ? "Eating"
        : c.State == ColonistState.Moving ? "Walking"
        : "Idle";

    private void Update((Colonist C, GlyphRow Row, Label A, Label B, GlyphBar Bar1, GlyphBar Bar2) r)
    {
        var c = r.C;
        long tick = _ui.Sim.Tick;
        switch (_tab)
        {
            case 0:
                r.A.Text = Doing(c);
                G.Retone(r.A, c.Starving ? "Bad" : "Dim");
                if (c.IsAutomaton) r.Bar1.Set(1, "  —", "faint");
                else r.Bar1.Set(c.Happiness, $"{c.Happiness * 100,3:0}%");
                break;
            case 1:
                if (c.IsAutomaton)
                {
                    r.Bar1.Set(1, "  —", "faint");
                    r.Bar2.Set(1, "  —", "faint");
                }
                else
                {
                    float food = Math.Min(1, c.Satiety / (float)Needs.FullSatiety), rest = Math.Min(1, c.Rest / Needs.FullRest);
                    r.Bar1.Set(food, $"{food * 100,3:0}%");
                    r.Bar2.Set(rest, $"{rest * 100,3:0}%");
                }
                r.B.Text = c.IsAutomaton ? "" : c.Home?.Name ?? "homeless";
                G.Retone(r.B, c.Home == null && !c.IsAutomaton ? "Warn" : "Dim");
                break;
            default:
                r.A.Text = c.IsChild ? $"Child, {(tick - c.BornTick) / Needs.Day}d" : c.EducationName;
                r.B.Text = c.Spouse is { } s ? $"married to {s.Name}"
                    : c.IsChild ? "at home"
                    : c.InFamily ? "grown, at home"
                    : c.IsAutomaton ? "" : "single";
                break;
        }
    }
}

// ---- Stocks (I) -------------------------------------------------------------------------------------------

/// <summary>What the colony has: food, materials and goods (stockpiled or not), and trends over the last days.</summary>
public partial class StocksWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private string _shown = "";

    public StocksWindow(GameUi ui) : base("stocks", "Stocks", "≡", "info", "I")
    {
        _ui = ui;
        MinCols = 30;
        Default = new WindowState { X = -2, Y = 2, W = 40, H = 22 };
    }
    public StocksWindow() : this(null!) { }

    protected override void Build() => _shown = "";

    public override void Tick()
    {
        var totals = _ui.Totals;
        float days = Society.FoodDays(_ui.Sim, _ui.Me);
        string key = string.Join(",", totals) + $"|{days:0.0}|{_ui.History.GetValueOrDefault("meals")?.Count}";
        if (key == _shown) return;
        _shown = key;
        G.Clear(Content);

        Content.AddChild(new GlyphSection("Food"));
        Content.AddChild(new GlyphKV("%", GlyphTheme.Tok("good"), "Days of food", float.IsInfinity(days) ? "-" : $"{days:0.0}", days < 2 ? "bad" : "hi"));
        Content.AddChild(new GlyphKV("%", GlyphTheme.Tok("good"), "Meals in store", Society.FoodInStore(_ui.Sim, _ui.Me).ToString()));
        var items = Enumerable.Range(0, totals.Length).Where(i => totals[i] > 0).Select(i => new ItemType((ushort)i)).ToList();
        foreach (var t in items.Where(t => t.Def.Nutrition > 0)) Add(t, totals);

        Group("Materials", items.Where(t => t.Def.Nutrition <= 0 && t.Def.Category is "raw" or "material" or "metal" or "component"), totals);
        Group("Goods", items.Where(t => t.Def.Nutrition <= 0 && t.Def.Category is not ("raw" or "material" or "metal" or "component")), totals);

        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("Trends (3 days)"));
        foreach (var (k, name, glyph, tone) in new[] { ("meals", "Meals", "%", "good"), ("log", "Logs", "♣", "pine"), ("plank", "Planks", "≡", "wood"), ("stone", "Stone", "•", "rock"), ("people", "Colonists", "☺", "accent") })
        {
            var list = _ui.History.GetValueOrDefault(k);
            if (list == null || list.Count < 2) continue;
            float d = _ui.DailyChange(k);
            var row = new HBoxContainer();
            row.AddChild(new GlyphChip(glyph, tone));
            row.AddChild(G.Text(name, expand: true));
            row.AddChild(new GlyphSparkline(list.ToArray(), 16));
            row.AddChild(G.Text(d == 0 ? "0" : $"{d:+0;-0}", d >= 0 ? "Good" : "Bad", cols: 6, align: HorizontalAlignment.Right));
            Content.AddChild(row);
        }
        int loose = 0;
        foreach (var (p, pile) in _ui.Sim.Items.All) if (!_ui.Sim.Stockpiles.Contains(p)) loose += pile.Count;
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphKV(null, default, "Lying about (not stockpiled)", loose.ToString(), "dim"));
    }

    private void Group(string title, IEnumerable<ItemType> types, int[] totals)
    {
        var list = types.ToList();
        if (list.Count == 0) return;
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection(title));
        foreach (var t in list) Add(t, totals);
    }

    private void Add(ItemType t, int[] totals) =>
        Content.AddChild(new GlyphKV(ItemLook.Glyph(t), ItemLook.Color(t), t.Name, totals[t.Index].ToString()) { MouseFilter = MouseFilterEnum.Pass }
            .WithBanner(() => $"{t.Name}: {t.Def.Weight:0.#} kg each, {t.Def.CarryLimit} a load{(t.Def.Source is { } s ? $" · from {s}" : "")}"));
}

// ---- Chronicle (L) ----------------------------------------------------------------------------------------

/// <summary>The colony's events, newest at the bottom (stays there unless scrolled up).</summary>
public partial class ChronicleWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private long _lastTick = -1;
    private int _lastCount = -1;

    public ChronicleWindow(GameUi ui) : base("chronicle", "Chronicle", "‼", "bad", "L")
    {
        _ui = ui;
        MinCols = 30;
        StickToBottom = true;
        Default = new WindowState { X = -2, Y = 25, W = 56, H = 12 };
    }
    public ChronicleWindow() : this(null!) { }

    public static bool IsAlert(string text) => text.Contains("left the colony") || text.Contains("starv", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("struck down") || text.Contains("died") || text.Contains("raid") || text.Contains("swarm out") || text.Contains("restless");

    private static (string Glyph, string Tone) Kind(string text) =>
        text.Contains("destroyed a nest") ? ("♠", "good")
        : text.Contains("struck down") || text.Contains("died") || text.Contains("raid") || text.Contains("swarm") || text.Contains("restless") ? ("♠", "bad")
        : text.Contains("back on their feet") ? ("♥", "good")
        : IsAlert(text) ? ("↓", "bad")
        : text.Contains("marry") ? ("♥", "bad")
        : text.Contains("born") ? ("☺", "good")
        : text.Contains("grown up") ? ("▲", "good")
        : text.Contains("settler") ? ("↑", "info")
        : text.Contains("graduated") || text.Contains("Magister") ? ("Θ", "accent")
        : text.Contains("awakens") ? ("◘", "info")
        : text.Contains("food", StringComparison.OrdinalIgnoreCase) ? ("%", "warn")
        : ("•", "dim");

    protected override void Build() => _lastCount = -1;

    public override void Tick()
    {
        var sim = _ui.Sim;
        var events = sim.Events.Where(e => e.Faction == _ui.Me || e.Faction < 0).Select(e => (e.Tick, e.Text)).Concat(sim.RecentCaveIns.Select(c =>
            (c.Tick, $"Cave-in: {c.Tiles} tiles fell {c.Drop} levels near {c.Origin.X},{c.Origin.Y}"))).OrderBy(e => e.Item1).ToList();
        long last = events.Count > 0 ? events[^1].Item1 : -1;
        if (events.Count == _lastCount && last == _lastTick) return;
        _lastCount = events.Count;
        _lastTick = last;
        G.Clear(Content);
        if (events.Count == 0) Content.AddChild(G.Para("Nothing has happened yet. Weddings, births, arrivals, departures and cave-ins are written here.", "Faint"));
        foreach (var (tick, text) in events)
        {
            var (glyph, tone) = text.StartsWith("Cave-in") ? ("▼", "warn") : Kind(text);
            var row = new HBoxContainer();
            row.AddChild(new GlyphChip(glyph, tone));
            row.AddChild(G.Text($"D{tick / Needs.Day + 1} {GameUi.TimeOfDay(tick)}", "Faint", cols: 9));
            var para = G.Para(text, "Text");
            row.AddChild(para);
            Content.AddChild(row);
        }
    }
}

// ---- Inspector (V) ----------------------------------------------------------------------------------------

/// <summary>
/// Details of what is selected (click it in the world with no tool: a colonist, workshop, home, stockpile or farm
/// field, blueprint, pile, nest or tile; the window opens by itself), else of whatever is under the cursor (it keeps
/// the last thing while the mouse is on the UI). Go to, Follow (colonists) and Clear sit in the header.
/// </summary>
public partial class InspectorWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private Pick? _hover;
    private Label _mode = null!;
    private Button _goto = null!, _follow = null!, _clear = null!;

    public InspectorWindow(GameUi ui) : base("inspect", "Inspect", "Θ", "info", "V")
    {
        _ui = ui;
        MinCols = 40;
        Default = new WindowState { X = -2, Y = 2, W = 52, H = 26 };
    }
    public InspectorWindow() : this(null!) { }

    private SelectionView Sel => _ui.Main.Selection;

    protected override void Build()
    {
        var bar = new HBoxContainer();
        _mode = G.Text("", "Faint", expand: true);
        _mode.ClipText = true;
        bar.AddChild(_mode);
        _goto = G.Button(" Go to ", () => { if (Sel.Where is { } w) _ui.Main.FocusOn(w); }, "Centre the camera on it");
        _follow = G.Button(" Follow ", () => Sel.Follow = !Sel.Follow, "Keep the camera on this colonist (WASD or a right-drag stops)");
        _clear = G.Button(" × ", () => Sel.Clear(), "Clear the selection (Esc, or a right click)");
        bar.AddChild(_goto);
        bar.AddChild(_follow);
        bar.AddChild(_clear);
        Header.AddChild(bar);
        Header.AddChild(G.Gap(0.25f));
    }

    public override void Tick()
    {
        var sim = _ui.Sim;
        var pick = Sel.Current;
        if (pick == null && _ui.Tool.Hovered is { } h) _hover = Sel.PickFor(h, _ui.Me);
        pick ??= _hover;
        _mode.Text = Sel.Current != null ? "Selected · Esc clears" : "Under the cursor · click to keep";
        G.Retone(_mode, Sel.Current != null ? "Accent" : "Faint");
        _goto.Visible = Sel.Current != null;
        _clear.Visible = Sel.Current != null;
        _follow.Visible = Sel.Current is PickColonist;
        _follow.Text = Sel.Follow ? " Following " : " Follow ";
        G.Clear(Content);
        switch (pick)
        {
            case null:
                Content.AddChild(G.Para("Point at anything in the world, or click it (no tool in hand) to keep it here: colonists, workshops, homes, stockpiles, farms, blueprints, piles, nests.", "Faint"));
                break;
            case PickColonist c: ColonistPanel(c.Colonist); break;
            case PickRoom r when sim.Rooms.All.Contains(r.Room):
                var room = r.Room;
                if (room.Faction != _ui.Me) Content.AddChild(G.Para($"Belongs to {sim.Factions[room.Faction].Name}.", "Warn"));
                if (room.Type.IsHome && sim.Factions[room.Faction].Housing.HomeOf(room) is { } home) HomePanel(home);
                else if (!room.Type.IsHome) RoomPanel(room);
                break;
            case PickZone z: ZonePanel(z); break;
            case PickPile p: PilePanel(p.Cell); break;
            case PickSite s: SitePanel(s.Blueprint); break;
            case PickNest n: NestPanel(n.Nest); break;
            case PickTile t: TilePanel(t.Tile); break;
        }
    }

    private void ColonistPanel(Colonist c)
    {
        var sim = _ui.Sim;
        Content.AddChild(new GlyphSection(c.Name));
        if (sim.Factions.Count > 1) KV("Colony", sim.Factions[c.Faction].Name, c.Faction == _ui.Me ? "hi" : "warn");
        KV("Schooling", c.IsChild ? "child" : c.EducationName);
        KV("Doing", c.Describe(), "dim");
        if (c.CarryingCount > 0) KV("Carrying", $"{c.CarryingCount} {c.CarryingType.Name.ToLowerInvariant()}");
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("Condition"));
        BarRow("Health", c.Health / (float)Colonist.MaxHealth, c.Downed ? "struck down" : $"{c.Health}/{Colonist.MaxHealth}", c.Health < 35 ? "bad" : null);
        if (!c.IsAutomaton)
        {
            BarRow("Food", c.Satiety / (float)Needs.FullSatiety, c.Starving ? "starving" : c.Hungry ? "hungry" : "fed", c.Hungry ? "warn" : null);
            BarRow("Rest", c.Rest / Needs.FullRest, c.Rest < Needs.FullRest * 0.25f ? "tired" : "rested");
            BarRow("Mood", c.Happiness, $"{c.Happiness:P0}", c.Happiness < Needs.Unhappy ? "bad" : null);
        }
        KV("Home", c.Home?.Name ?? (c.IsAutomaton ? "-" : "homeless"), c.Home == null && !c.IsAutomaton ? "bad" : "hi");
        if (c.Spouse != null) KV("Married to", c.Spouse.Name, "dim");
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("Gear"));
        KV("Weapon", c.Weapon?.Name ?? "bare hands", c.Weapon != null ? "hi" : "faint");
        KV("Armour", c.Armour?.Name ?? "none", c.Armour != null ? "hi" : "faint");
        if (!c.TwoHanded) KV("Shield", c.Shield?.Name ?? "none", c.Shield != null ? "hi" : "faint");
        if (c.Drafted) KV("Drafted", "yes (Military, M)", "bad");
        if (!c.IsChild)
        {
            Content.AddChild(G.Gap());
            Content.AddChild(new GlyphSection("Work (J to change)"));
            Content.AddChild(G.Para(string.Join(" · ", Enumerable.Range(0, Colonist.WorkTypes).Select(w =>
                $"{(w < ShortWork.Length ? ShortWork[w] : Colonist.WorkNames[w])} {(c.Priority((Work)w) is var p and > 0 ? p.ToString() : "-")}")), "Dim"));
            Content.AddChild(G.Para($"Likes {c.FavoriteFood.Name.ToLowerInvariant()}, things of {c.LikedMaterial}, and {c.PreferredWork.ToString().ToLowerInvariant()} work", "Faint"));
        }
    }

    private static readonly string[] ShortWork = ["Mine", "Wood", "Farm", "Build", "Haul", "Craft", "Trade"];

    private void ZonePanel(PickZone z)
    {
        var sim = _ui.Sim;
        var cells = Sel.Current == z ? Sel.Zone : Sel.ZoneCells(z.Cell, z.Farm);
        if (cells.Count == 0) cells = Sel.ZoneCells(z.Cell, z.Farm);
        int levels = cells.Select(c => c.Z).Distinct().Count();
        if (z.Farm)
        {
            Content.AddChild(new GlyphSection("Farm field"));
            KV("Size", $"{cells.Count} plots on {G.Plural(levels, "level")}");
            var crops = new Dictionary<string, (int Plots, int Growing, int Ripe)>();
            foreach (var c in cells)
            {
                if (!sim.Farms.TryGet(c, out var crop)) continue;
                string name = Colony.Core.Nature.PlantInfo.Name(crop);
                var (n, g, r) = crops.GetValueOrDefault(name);
                bool has = sim.Flora.TryGet(c, out var plant);
                crops[name] = (n + 1, g + (has ? 1 : 0), r + (has && Colony.Core.Nature.PlantInfo.IsGatherable(plant) ? 1 : 0));
            }
            foreach (var (name, (n, g, r)) in crops) KV(name, $"{n} plots, {g} sown, {r} ripe");
            Content.AddChild(G.Para("Half again as fast within 2 tiles of water. Shift+X over plots removes them.", "Faint"));
            return;
        }
        bool depot = sim.Stockpiles.IsDepot(z.Cell);
        Content.AddChild(new GlyphSection(depot ? "Trade depot" : "Stockpile"));
        if (sim.Factions.Count > 1) KV("Colony", sim.Factions[sim.Stockpiles.OwnerOf(z.Cell)].Name);
        if (depot) Content.AddChild(G.Para("Other colonies' traders deliver here what you buy from them (Trade, F6); your haulers carry it on to your stockpiles.", "Faint"));
        int racks = 0, capacity = 0, used = 0;
        var goods = new Dictionary<Colony.Core.Items.ItemType, int>();
        foreach (var c in cells)
        {
            int cap = sim.Stockpiles.CapacityAt(c);
            capacity += cap;
            if (cap > 1) racks++;
            if (sim.Items.TryGet(c, out var pile) && pile.Count > 0)
            {
                goods[pile.Type] = goods.GetValueOrDefault(pile.Type) + pile.Count;
                used += (pile.Count + Colony.Core.Items.ItemInfo.StackLimit(pile.Type) - 1) / Colony.Core.Items.ItemInfo.StackLimit(pile.Type);
            }
        }
        KV("Size", $"{cells.Count} cells on {G.Plural(levels, "level")}");
        KV("Racks", racks > 0 ? racks.ToString() : "none (Build, Zones: storage rack)", racks > 0 ? "hi" : "faint");
        BarRow("Space", capacity == 0 ? 0 : used / (float)capacity, $"{used}/{capacity} stacks", used >= capacity ? "bad" : null);
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("Holding"));
        if (goods.Count == 0) Content.AddChild(G.Para(depot ? "Nothing waiting: deliveries are carried on to the stockpiles." : "Nothing yet: haulers bring loose goods here.", "Faint"));
        foreach (var (type, n) in goods.OrderByDescending(g => g.Value).Take(24))
            Content.AddChild(new GlyphKV(ItemLook.Glyph(type), ItemLook.Color(type), type.Name, n.ToString()));
    }

    private void PilePanel(TilePos cell)
    {
        var sim = _ui.Sim;
        if (!sim.Items.TryGet(cell, out var pile)) return;
        Content.AddChild(new GlyphSection(pile.Type.Name));
        KV("Here", $"{pile.Count} of {sim.Items.LimitAt(cell, pile.Type)}");
        if (pile.Reserved > 0) KV("Promised to a hauler", pile.Reserved.ToString(), "dim");
        if (pile.Incoming > 0) KV("On its way", pile.Incoming.ToString(), "dim");
        var role = Colony.Core.Items.Hauling.RoleOf(cell, pile.Type, sim.Stockpiles, sim.Rooms, sim.Constructions);
        KV("Lying", role switch
        {
            _ when sim.Stockpiles.IsDepot(cell) => "in a trade depot: delivered, haulers take it on to the stockpiles",
            Colony.Core.Items.StorageRole.Loose =>sim.Stockpiles.Count == 0 ? "loose (zone a stockpile, Shift+P, to store it)" : "loose: haulers will store it",
            Colony.Core.Items.StorageRole.Stockpile => "in a stockpile",
            Colony.Core.Items.StorageRole.Input => "in a factory's input crate",
            Colony.Core.Items.StorageRole.Output => "in a factory's output crate",
            _ => "on a building site (its materials)",
        }, "dim");
        if (pile.Type.Def.Uses.Count > 0) Content.AddChild(G.Para("Used for: " + string.Join(", ", pile.Type.Def.Uses), "Faint"));
    }

    private void SitePanel(Blueprint bp)
    {
        Content.AddChild(new GlyphSection(Tools.BuiltName(bp) + " (planned)"));
        KV("Made of", bp.Cost.Material.Name.ToLowerInvariant());
        Content.AddChild(G.Para(WorldInfo.Waiting(_ui, bp.Cost.Material, bp.Cost.Count,
            Colony.Core.Orders.Constructions.MaterialsOnSite(bp, _ui.Sim.Items)), "Dim"));
        KV("Builder", bp.BuilderId >= 0 ? _ui.Sim.Colonists.FirstOrDefault(c => c.Id == bp.BuilderId)?.Name ?? "someone" : "nobody yet", "dim");
        Content.AddChild(G.Para("Shift+X over it cancels the plan (what was delivered stays).", "Faint"));
    }

    private void NestPanel(Colony.Core.Combat.Nest n)
    {
        var sim = _ui.Sim;
        Content.AddChild(new GlyphSection($"{n.Kind.Name} nest"));
        KV("Ants", $"{n.Total} ({n.Out} out)");
        BarRow("Mound", n.Health / (float)Math.Max(1, n.Kind.NestHealth), $"{n.Health}/{n.Kind.NestHealth}", "bad");
        bool hostile = n.HostileTo(_ui.Me, sim.Tick);
        KV("Mood", hostile ? "hostile to you" : n.Crowded > 0 ? "crowded by your land" : "calm: leave them be", hostile ? "bad" : "good");
        Content.AddChild(G.Para("G then click the nest sends the drafted against it (Military, M).", "Faint"));
    }

    /// <summary>A tile: its name and level, then what is built and lying there (the hover card's facts, with materials).</summary>
    private void TilePanel(TilePos tile)
    {
        var card = WorldInfo.For(_ui, tile);
        Content.AddChild(new GlyphSection(WorldInfo.GroundName(_ui.Sim, tile)));
        KV("Where", $"{tile.X}, {tile.Y}, level {WorldInfo.Level(_ui.Sim, tile.Z)}", "dim");
        if (card.Lines.Count == 0) return;
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("Here"));
        foreach (var (glyph, color, text, tone) in card.Lines)
        {
            var row = new HBoxContainer();
            row.AddChild(new GlyphChip(glyph, color));
            row.AddChild(G.Para(text, tone));
            Content.AddChild(row);
        }
    }

    private void KV(string name, string value, string tone = "hi") => Content.AddChild(new GlyphKV(null, default, name, value, tone));

    private void BarRow(string name, float value, string label, string? tone = null)
    {
        var row = new HBoxContainer();
        row.AddChild(G.Text(name, cols: 14));
        var bar = new GlyphBar(value, 0, tone, label);
        row.AddChild(bar);
        Content.AddChild(row);
    }

    private void HomePanel(Home home)
    {
        var sim = _ui.Sim;
        var plan = Housing.Plan(home);
        Content.AddChild(new GlyphSection(home.Name));
        KV("Policy (F to switch, H to edit)", home.Policy.Name);
        KV("Size", G.Plural(home.Room.Cells.Count, "cell"), home.Room.Cells.Count < Housing.MinCells ? "bad" : "hi");
        KV("Room for", G.Plural(plan.Residents, "resident"));
        KV("Beds", home.Beds.ToString());
        string who = home.Family != 0 && home.Residents.FirstOrDefault(r => r.Spouse != null) is { } head ? $"the family of {head.Name} & {head.Spouse!.Name}"
            : home.Policy.Shared ? "shared lodging" : home.Residents.Count == 0 ? "empty" : "lodgers";
        KV("Household", who, "dim");
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("Fixtures"));
        for (int s = 0; s < Housing.SlotCount; s++)
        {
            if (plan.Count[s] == 0 && home.Count(s) == 0) continue;
            int have = home.Count(s);
            float wear = home.Fixtures.Where(f => f.Slot == s).Select(f => f.Condition).DefaultIfEmpty(1).Min();
            BarRow(Housing.Slots[s], plan.Count[s] == 0 ? 1 : have / (float)plan.Count[s],
                $"{have}/{plan.Count[s]} {Catalog.FixtureTiers[plan.Tier[s]]}{(wear < 0.25f ? " worn" : "")}");
        }
        var shell = home.Shell;
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("Building"));
        BarRow("Walls", shell.Enclosure, shell.Enclosure <= 0 ? "none: Build (B), Structures" : $"{shell.Enclosure:P0} {string.Join(", ", shell.Materials)}");
        KV("Windows", shell.Windows.ToString());
        KV("Floor", shell.Floored > 0 ? $"{shell.Floored:P0} {Catalog.FixtureTiers[Math.Clamp((int)MathF.Round(shell.FloorTier), 0, 3)]}" : "bare", shell.Floored > 0 ? "hi" : "dim");
        KV("Roof", shell.Covered > 0 ? $"{shell.Covered:P0} {Catalog.FixtureTiers[Math.Clamp((int)MathF.Round(shell.RoofTier), 0, 3)]}" : "open sky", shell.Covered > 0 ? "hi" : "dim");
        if (home.Residents.Count > 0)
        {
            Content.AddChild(G.Gap());
            Content.AddChild(new GlyphSection("Living here"));
            foreach (var c in home.Residents)
            {
                var row = new HBoxContainer();
                row.AddChild(new GlyphChip("☺", GlyphTheme.Person(c.Id)));
                row.AddChild(G.Text(c.Name, cols: 12));
                row.AddChild(G.Text(c.IsChild ? "child" : c.EducationName, "Dim", cols: 10));
                float score = Housing.Score(home, c);
                row.AddChild(new GlyphBar(score, 0, null, $"{score * 100,3:0}%"));
                Content.AddChild(row);
            }
        }
    }

    /// <summary>Stock limits a machine can be set to (0: none).</summary>
    private static readonly int[] LimitSteps = [0, 10, 20, 50, 100, 200, 500];

    private void RoomPanel(Room room)
    {
        var sim = _ui.Sim;
        int levels = room.Cells.Select(c => c.Z).Distinct().Count();
        Content.AddChild(new GlyphSection(room.Name));
        Content.AddChild(G.Para($"{room.Type.Name} workshop: {room.Type.About}", "Faint"));
        KV("Status", Rooms.Status(room, sim), "dim");
        KV("Size", $"{room.Cells.Count} cells on {G.Plural(levels, "level")}");
        BarRow("Efficiency", Math.Min(1, room.Efficiency / 2f), $"{room.Efficiency:P0}", "accent");
        var machines = room.PartsWithRole(PartRole.Machine).GroupBy(m => (m.Name, m.Recipe)).ToList();
        if (machines.Count > 0)
        {
            Content.AddChild(G.Gap());
            Content.AddChild(new GlyphSection("Machines"));
            foreach (var g in machines)
            {
                var (name, recipe) = g.Key;
                var b = g.First().Batch;
                double perMinute = g.Count() * b.OutCount * 60.0 * ColonySim.TicksPerSecond * room.Efficiency / b.WorkTicks;
                Content.AddChild(new GlyphKV("÷", GlyphTheme.Tok("warn"), $"{g.Count()} {name.ToLowerInvariant()}: {recipe!.Name.ToLowerInvariant()}", $"{perMinute:0.#}/min"));
                Content.AddChild(G.Para($"   {recipe.Describe()} ({Catalog.EducationName(recipe.Education)}) · F over it to change", "Faint"));
                if (recipe.Kind == Colony.Core.Items.ItemKind.Good)
                {
                    // Stock limit: the machines stop starting batches once the colony holds this many.
                    var group = g.ToList();
                    int limit = group[0].Limit, stock = Rooms.StockOf(sim, recipe.Output, room.Faction);
                    var row = new HBoxContainer();
                    row.AddChild(G.Text("   Keep in store", "Dim", expand: true));
                    void Set(int value)
                    {
                        foreach (var m in group) _ui.Tool.Issue(new Colony.Core.Commands.SetMachineLimit(m.Cells[0], value));
                    }
                    int Step(int dir)
                    {
                        int at = Array.FindIndex(LimitSteps, v => v == limit);
                        if (at < 0) at = Array.FindIndex(LimitSteps, v => v > limit) - (dir > 0 ? 1 : 0);
                        return LimitSteps[Math.Clamp(at + dir, 0, LimitSteps.Length - 1)];
                    }
                    row.AddChild(G.Button(" ‹ ", () => Set(Step(-1)), "Lower the stock limit", "Ghost"));
                    row.AddChild(G.Text(limit == 0 ? "no limit" : $"{stock}/{limit}", limit > 0 && stock >= limit ? "Good" : "Hi", cols: 10, align: HorizontalAlignment.Center));
                    row.AddChild(G.Button(" › ", () => Set(Step(1)), "Raise the stock limit (the machines stop once the colony holds this many)", "Ghost"));
                    Content.AddChild(row);
                }
            }
        }
        var aux = room.Parts.Where(p => p.Built && p.Role == PartRole.Auxiliary).GroupBy(p => p.Name).ToList();
        if (aux.Count > 0)
        {
            Content.AddChild(G.Gap());
            Content.AddChild(new GlyphSection("Tools and fittings"));
            foreach (var g in aux) KV(g.Key, g.Count().ToString());
        }
        if (room.Blueprints.Any())
        {
            Content.AddChild(G.Gap());
            Content.AddChild(new GlyphSection("Under construction"));
            foreach (var p in room.Blueprints)
            {
                int have = Rooms.MaterialsOnSite(p, sim.Items);
                string state = p.BuilderId >= 0 ? "building" : have >= p.Cost.Count ? "needs a builder" : $"{have}/{p.Cost.Count} {p.Cost.Material.Name.ToLowerInvariant()}";
                KV(p.Name, state, "dim");
            }
        }
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("Crates"));
        foreach (var (role, label) in new[] { (PartRole.Input, "In"), (PartRole.Output, "Out") })
            foreach (var crate in room.PartsWithRole(role))
            {
                if (sim.Items.TryGet(crate.Cells[0], out var pile) && pile.Count > 0)
                    Content.AddChild(new GlyphKV(ItemLook.Glyph(pile.Type), ItemLook.Color(pile.Type), $"{label}: {pile.Type.Name}", pile.Count.ToString()));
                else KV($"{label}: empty crate", "-", "faint");
            }
    }
}

// ---- Help (F3) ----------------------------------------------------------------------------------------------

/// <summary>Keys, and a debug readout (frame rate, sim cost, water, power).</summary>
public partial class HelpWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private readonly List<(GlyphKV Row, string Name, Func<string> Value)> _live = new();

    public HelpWindow(GameUi ui) : base("help", "Help", "?", "dim", "F3")
    {
        _ui = ui;
        MinCols = 36;
        Default = new WindowState { X = 40, Y = 3, W = 60, H = 30 };
    }
    public HelpWindow() : this(null!) { }

    /// <summary>Every key, grouped the way the game is played: looking around, giving orders, building, the windows.</summary>
    private static readonly (string Section, (string Key, string What)[] Keys)[] Sections =
    [
        ("Camera", [
            ("WASD / Q E", "pan / turn the camera"), ("RMB drag", "pan"), ("Wheel, middle drag", "zoom, orbit"),
            ("Shift / Ctrl (tap)", "view a level up / down"), ("Z X, PgDn PgUp", "view a level down / up"), ("LMB drag, no tool", "move through the levels"),
            ("Home", "look at the colony")]),
        ("Looking", [
            ("Hover", "the card at the bottom right says what is there"), ("Click, no tool", "keep it in Inspect (V)"), ("Esc / RMB click", "clear the selection")]),
        ("Orders", [
            ("N  Y", "dig, stairway"), ("U  K  O", "up, down, up/down stair"), ("T  P", "chop trees, gather plants"),
            ("Shift+P", "stockpile"), ("Shift+F", "farm (again: the next crop)"), ("Shift+B", "stone block (dams)"),
            ("Shift+O", "demolish"), ("Shift+X, Del", "cancel orders"), ("Tab / Shift+Tab", "cycle the orders")]),
        ("Using a tool", [
            ("Drag", "a box (or click one corner, then the other)"), ("Shift + drag", "remove instead"),
            ("Shift / Ctrl, Z X", "stretch the box over levels"), ("RMB click, Esc", "abort the box, then put the tool down"),
            ("Walls: click / drag", "one piece / a line along an edge, or a box round"), ("R, [ ]", "turn, size of the part being placed"),
            ("F / Shift+F", "next recipe (machine) or policy (home) under the cursor")]),
        ("People", [
            ("G", "draft everyone, then click ground to march or a nest to attack (G again: done)"), ("Shift+G", "stand everyone down")]),
        ("Windows", [
            ("C  I  B  L", "colonists, stocks, build, chronicle"), ("H  J  V  M", "housing, work, inspect, military"), ("F6", "trade"),
            ("F2  F3  F10", "appearance, this help, pixel look"), ("Esc", "close the window, then the menu"), ("Ctrl+Tab", "next window")]),
        ("Game", [
            ("Space, 1 2 3", "pause, speed"), ("F5 / F9", "quicksave / load the newest save"), ("F4", "play the next colony (hot-seat)")]),
    ];

    protected override void Build()
    {
        _live.Clear();
        foreach (var (section, keys) in Sections)
        {
            Content.AddChild(new GlyphSection(section));
            foreach (var (k, what) in keys) Content.AddChild(new GlyphKV(null, default, what, k, "accent"));
            Content.AddChild(G.Gap(0.4f));
        }
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("Debug"));
        var sim = _ui.Sim;
        Live("Frame rate", () => $"{Engine.GetFramesPerSecond()} fps");
        Live("Sim per frame", () => $"{_ui.Main.SimMs:0.0} ms");
        Live("Terrain sections to build", () => _ui.Main.PendingSections.ToString());
        Live("Water spans moving", () => $"{sim.Water.LastStepCells} in {sim.Water.AwakeChunks} blocks ({sim.Water.WetChunks} wet)");
        Live("Power", () => $"{sim.Waterworks.Generated:0.#} made, {sim.Waterworks.Demand:0.#} wanted ({sim.Waterworks.Supply:P0})");
        Live("Designations, blueprints", () => $"{sim.Designations.Count}, {sim.Constructions.Count}");
        Live("Stockpile cells, farm plots", () => $"{sim.Stockpiles.Count}, {sim.Farms.Count}");
        Live("Settlers, left, weddings, births", () => $"{_ui.MyFaction.Society.SettlersArrived}, {_ui.MyFaction.Society.Departed}, {_ui.MyFaction.Society.Marriages}, {_ui.MyFaction.Society.Births}");
        Live("Goods met", () => string.Join(", ", Needs.Goods.Select((g, k) => $"{g.Need} {_ui.MyFaction.Society.Met[k]:P0}")));
        Live("Wildlife", () => $"{sim.Wildlife.Nests.Count} nests, {sim.Wildlife.Creatures.Count} out, {sim.Wildlife.Slain} slain, {sim.Wildlife.LastTickMs:0.00} ms a tick");
    }

    private void Live(string name, Func<string> value)
    {
        var kv = new GlyphKV(null, default, name, value());
        Content.AddChild(kv);
        _live.Add((kv, name, value));
    }

    public override void Tick()
    {
        foreach (var (row, name, value) in _live) row.Set(name, value());
    }
}

// ---- Appearance (F2) --------------------------------------------------------------------------------------

/// <summary>Theme, frame, corners, font, size, spacing, UI scale and effects; saved per user.</summary>
public partial class AppearanceWindow : GlyphWindow
{
    private readonly GameUi _ui;

    public AppearanceWindow(GameUi ui) : base("appearance", "Appearance", "☼", "accent", "F2")
    {
        _ui = ui;
        MinCols = 36;
        Default = new WindowState { X = -2, Y = 2, W = 46, H = 32 };
    }
    public AppearanceWindow() : this(null!) { }

    protected override void Build()
    {
        var o = GlyphTheme.Options;
        Content.AddChild(new GlyphSection("Theme"));
        foreach (var t in GlyphTheme.Themes)
        {
            var theme = t;
            var row = new GlyphRow { Clickable = true, Selected = t.Id == o.Theme };
            row.AddChild(new Swatches(t));
            row.AddChild(G.Text(t.Label, t.Id == o.Theme ? "Hi" : "Text", bold: t.Id == o.Theme, cols: 9));
            row.AddChild(G.Text(t.Blurb, "Faint", expand: true));
            row.Pressed += () => GlyphTheme.Set(x => x.Theme = theme.Id);
            Content.AddChild(row);
        }
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("Frame"));
        Content.AddChild(Line(null, new GlyphSegmented(["Hairline", "Double", "Glow", "None"], Array.IndexOf(GlyphTheme.Frames, o.Frame),
            i => GlyphTheme.Set(x => x.Frame = GlyphTheme.Frames[i]))));
        Content.AddChild(Line("Corners", new GlyphSegmented(GlyphTheme.Radii.Select(r => r.ToString()).ToArray(), Array.IndexOf(GlyphTheme.Radii, o.Radius),
            i => GlyphTheme.Set(x => x.Radius = GlyphTheme.Radii[i]))));
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("Type"));
        var fonts = GlyphTheme.Fonts.Keys.ToArray();
        Content.AddChild(Line(null, new GlyphSegmented(fonts.Select(f => GlyphTheme.Fonts[f].Label).ToArray(), Array.IndexOf(fonts, o.Font),
            i => GlyphTheme.Set(x => x.Font = fonts[i]))));
        var size = new HBoxContainer();
        size.AddChild(G.Text("Size", "Dim", cols: 8));
        size.AddChild(G.Button("−", () => GlyphTheme.Set(x => x.Size = Math.Max(10, x.Size - 1))));
        size.AddChild(G.Text($"{o.Size}px", "Hi", bold: true, cols: 5, align: HorizontalAlignment.Center));
        size.AddChild(G.Button("+", () => GlyphTheme.Set(x => x.Size = Math.Min(28, x.Size + 1))));
        Content.AddChild(size);
        var densities = GlyphTheme.Densities.Keys.ToArray();
        Content.AddChild(Line("Spacing", new GlyphSegmented(densities.Select(d => char.ToUpper(d[0]) + d[1..]).ToArray(), Array.IndexOf(densities, o.Density),
            i => GlyphTheme.Set(x => x.Density = densities[i]))));
        float[] scales = [1f, 1.25f, 1.5f, 2f];
        Content.AddChild(Line("UI scale", new GlyphSegmented(["1×", "1.25×", "1.5×", "2×"], Math.Max(0, Array.IndexOf(scales, o.Scale)),
            i => GlyphTheme.Set(x => x.Scale = scales[i]))));
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("Effects"));
        Content.AddChild(new GlyphSwitch("Frosted glass", o.Glass, on => GlyphTheme.Set(x => x.Glass = on)));
        Content.AddChild(new GlyphSwitch("Soft shadows", o.Shadow, on => GlyphTheme.Set(x => x.Shadow = on)));
        Content.AddChild(new GlyphSwitch("Icon chips", o.Chips, on => GlyphTheme.Set(x => x.Chips = on)));
        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("World"));
        Content.AddChild(new GlyphSwitch("Pixel-art renderer (F10 tunes it)", o.PixelArt, on =>
        {
            GlyphTheme.Set(x => x.PixelArt = on);
            GlyphTheme.Save();
            Callable.From(_ui.Main.ReloadView).CallDeferred(); // views are built for one renderer: rebuild them on this world
        }).WithTip("Pixel art, outlines, palettes, dithered trees and ASCII zoom (off: the classic smooth look)"));
        Content.AddChild(G.Gap());
        var buttons = new HBoxContainer();
        buttons.AddChild(G.Button(" Reset ", GlyphTheme.ResetAppearance, "Default font, size, frame and effects (keeps the theme)"));
        buttons.AddChild(G.Button(" Reset layout ", _ui.ResetLayout, "Every window back to its default place and size"));
        Content.AddChild(buttons);
    }

    private static HBoxContainer Line(string? label, Control control)
    {
        var row = new HBoxContainer();
        if (label != null) row.AddChild(G.Text(label, "Dim", cols: 8));
        row.AddChild(control);
        return row;
    }

    /// <summary>Six dots: bg, panel, text, accent, good, bad.</summary>
    private sealed partial class Swatches : GlyphControl
    {
        private readonly GlyphTheme.ThemeData _t;
        public Swatches(GlyphTheme.ThemeData t) { _t = t; MouseFilter = MouseFilterEnum.Ignore; }
        public Swatches() : this(null!) { }
        public override Vector2 _GetMinimumSize() => new(7 * Cw, Ch);

        public override void _Draw()
        {
            string[] keys = ["bg", "panel", "text", "accent", "good", "bad"];
            for (int i = 0; i < keys.Length; i++)
            {
                var c = _t.Tokens[keys[i]];
                DrawCircle(new Vector2((i + 0.5f) * Cw, Ch / 2), Math.Min(Cw, Ch) * 0.36f, c, true, -1, true);
                DrawCircle(new Vector2((i + 0.5f) * Cw, Ch / 2), Math.Min(Cw, Ch) * 0.36f, GlyphTheme.Tok("text", 0.25f), false, 1, true);
            }
        }
    }
}
