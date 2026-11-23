using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core;
using Colony.Core.Agents;
using Colony.Core.Items;
using Colony.Core.World;
using Godot;

namespace ColonyGame.UI;

/// <summary>
/// The game's Glyph UI: the HUD pill (colony, date, key stocks with daily trends, alerts, level, speed), the windows
/// (each with its hotkey: C colonists, I stocks, B build, L chronicle, H housing, J work, V inspect, M military, F6 trade, F1 tests,
/// F2 appearance, F3 help), the status pill (hover hints, the tool and what it is doing) and the main menu (Esc
/// when nothing is left to close). Reads the sim; player actions go through the same calls the old menus made.
/// </summary>
public partial class GameUi : CanvasLayer
{
    public readonly Main Main;
    public ColonySim Sim => Main.Sim;
    /// <summary>The local player's colony: what the HUD and windows show.</summary>
    public int Me => Main.Player;
    public Colony.Core.Agents.Faction MyFaction => Sim.Factions[Me];
    /// <summary>This colony's colonists.</summary>
    public List<Colony.Core.Agents.Colonist> Mine => Sim.Colonists.Where(c => c.Faction == Me).ToList();

    /// <summary>Goods this colony can use: loose, or in its own stockpiles, crates and sites.</summary>
    public bool Ours(TilePos p) =>
        (!Sim.Stockpiles.Contains(p) || Sim.Stockpiles.OwnerOf(p) == Me) && (Sim.Rooms.PartAt(p) is not { } part || part.Room.Faction == Me);
    public DesignationTool Tool => Main.Tool;
    private readonly GlyphManager _m;
    private readonly Dictionary<string, GlyphWindow> _windows = new();
    private readonly MainMenu _menu;
    private BuildWindow _build = null!;
    /// <summary>The orders ribbon above the dock (and its pill).</summary>
    public OrdersRibbon Ribbon { get; private set; } = null!;

    public GameUi(Main main)
    {
        Main = main;
        Layer = 5;
        _m = new GlyphManager();
        AddChild(_m);
        _m.CancelPlacement = () => Tool.CancelPlacement();
        _m.EscapeFallback = () => ShowMenu(!_menu!.Visible);
        _m.WorldStatus = WorldStatus;

        Add(new ColonistsWindow(this), Key.C);
        Add(new StocksWindow(this), Key.I);
        _build = new BuildWindow(this);
        Add(_build, Key.B);
        Add(new ChronicleWindow(this), Key.L);
        Add(new HousingWindow(this), Key.H);
        Add(new WorkWindow(this), Key.J);
        Add(new InspectorWindow(this), Key.V);
        Add(new MilitaryWindow(this), Key.M);
        Add(new TradeWindow(this), Key.F6);
        if (GameInfo.DevBuild) Add(new TestsWindow(this), Key.F1); // developer builds only: it loads the test DLL from the source tree
        Add(new AppearanceWindow(this), Key.F2);
        Add(new HelpWindow(this), Key.F3);
        Add(new GraphicsWindow(this), Key.F10);

        BuildHud();
        Ribbon = new OrdersRibbon(this) { Name = "OrdersRibbon" };
        _m.AddChild(Ribbon);
        _m.AddChild(new Notices(this) { Name = "Notices" });
        if (main != null)
        {
            // A click with no tool selects what is under the cursor and shows it in Inspect; Esc / a right click clears it.
            main.Tool.Clicked += tile =>
            {
                main.Selection.PickAt(tile, Me);
                if (main.Selection.Current != null && Window("inspect") is { IsShown: false } inspect) _m.Open(inspect);
            };
            main.Tool.NothingToCancel = () => main.Selection.Clear();
        }
        _m.AddChild(new ProfilerPanel(this) { Name = "Profiler" });
        _menu = new MainMenu(this);
        _m.Overlay.AddChild(_menu);
        _m.Changed();
    }
    public GameUi() : this(null!) { }

    public override void _Ready()
    {
        GetTree().Root.ContentScaleFactor = GlyphTheme.Options.Scale;
    }

    private void Add(GlyphWindow w, Key key)
    {
        _windows[w.Id] = w;
        _m.Register(w, key);
    }

    public bool MenuOpen => _m.MenuOpen;
    public GlyphWindow? Window(string id) => _windows.GetValueOrDefault(id);
    public void OpenWindow(string id) { if (Window(id) is { } w) _m.Open(w); }
    public void Toggle(string id) { if (Window(id) is { } w) _m.Toggle(w); }
    public void ShowMenu(bool show, string page = "") => _menu.Show(show, page);
    public void ResetLayout() => _m.ResetLayout();

    /// <summary>--build-menu=WORKSHOP: opens Build on that workshop's room tab.</summary>
    public void OpenBuild(string workshopId)
    {
        _m.Open(_build);
        _build.ShowWorkshop(workshopId);
    }

    // ---- HUD --------------------------------------------------------------------------------------------

    private Label _colony = null!, _date = null!, _level = null!, _terrain = null!, _net = null!;
    private readonly List<HudStat> _stats = new();
    private HudStat _alerts = null!;
    private GlyphSegmented _speed = null!;

    private void BuildHud()
    {
        var row = _m.HudRow;
        row.AddChild(new GlyphChip("☼", "accent"));
        row.AddChild(Space(1));
        _colony = G.Text("", "Hi", bold: true);
        _colony.MouseFilter = Control.MouseFilterEnum.Pass;
        _colony.WithBanner(() => Sim.Factions.Count > 1 ? $"Your colony, {MyFaction.Name} · F4: play the next" : $"Your colony, {MyFaction.Name}");
        row.AddChild(_colony);
        row.AddChild(Space(2));
        _date = G.Text("", "Dim");
        _date.MouseFilter = Control.MouseFilterEnum.Pass;
        _date.WithBanner(() => $"Day {Sim.Tick / Needs.Day + 1} · a day is {Needs.Day / ColonySim.TicksPerSecond / 60} minutes at normal speed");
        row.AddChild(_date);
        row.AddChild(Space(2));
        _stats.Add(new HudStat("☺", "accent", () => Toggle("colonists")));
        _stats.Add(new HudStat("%", "good", () => Toggle("stocks")));
        _stats.Add(new HudStat("♣", "pine", () => Toggle("stocks")));
        _stats.Add(new HudStat("≡", "wood", () => Toggle("stocks")));
        _stats.Add(new HudStat("•", "rock", () => Toggle("stocks")));
        _stats.Add(new HudStat("♥", "bad", () => Toggle("colonists")));
        _stats.Add(new HudStat("⌂", "furn", () => Toggle("housing")));
        foreach (var s in _stats)
        {
            row.AddChild(s);
            row.AddChild(Space(1));
        }
        _alerts = new HudStat("‼", "bad", () => OpenWindow("chronicle")) { Pill = true };
        row.AddChild(_alerts);
        row.AddChild(G.Spacer());
        _net = G.Text("", "Info");
        _net.MouseFilter = Control.MouseFilterEnum.Pass;
        _net.WithBanner(() => "Online play · Esc, Multiplayer: host, join or leave");
        row.AddChild(_net);
        row.AddChild(Space(2));
        _terrain = G.Text("", "Faint");
        row.AddChild(_terrain);
        row.AddChild(Space(2));
        _level = G.Text("", "Dim");
        _level.MouseFilter = Control.MouseFilterEnum.Pass;
        _level.WithBanner(() => "View level · tap Shift / Ctrl: up / down · drag with no tool · Z/X, PgDn/PgUp");
        row.AddChild(_level);
        row.AddChild(Space(2));
        _speed = new GlyphSegmented(["‖", "►", "►►", "►►►"], 1, i =>
        {
            if (i == 0) Main.Paused = true;
            else
            {
                Main.Paused = false;
                Main.SpeedIndex = i - 1;
            }
        })
        {
            Solid = true,
            BannerTips = true,
            Tips = ["Pause (Space)", $"Normal speed (1)", $"Fast: {Main.SpeedSteps[1]}x (2)", $"Fastest: {Main.SpeedSteps[2]}x (3)"],
        };
        row.AddChild(_speed);
    }

    private static Control Space(int cells) => new() { CustomMinimumSize = new Vector2(cells * GlyphTheme.Cw, 0), MouseFilter = Control.MouseFilterEnum.Ignore };

    // ---- History (daily trends) ------------------------------------------------------------------------

    /// <summary>Samples taken every eighth of a day: meals in store and totals of a few goods.</summary>
    public const int SamplesPerDay = 8;
    public readonly Dictionary<string, List<float>> History = new();
    private long _lastSample = -1;
    private int[] _totals = [];
    private double _hudIn;

    public int[] Totals => _totals;

    private void Sample()
    {
        _totals = Sim.Items.Totals(Ours);
        long step = Needs.Day / SamplesPerDay;
        if (_lastSample >= 0 && Sim.Tick - _lastSample < step) return;
        _lastSample = Sim.Tick;
        void Add(string key, float v)
        {
            if (!History.TryGetValue(key, out var list)) History[key] = list = new List<float>();
            list.Add(v);
            if (list.Count > SamplesPerDay * 4) list.RemoveAt(0);
        }
        Add("meals", Society.FoodInStore(Sim, Me));
        Add("log", Count(ItemType.Log));
        Add("plank", Count(ItemType.Plank));
        Add("stone", Count(ItemType.Stone));
        Add("people", Mine.Count);
        Add("mood", MyFaction.Society.Happiness * 100);
    }

    public int Count(ItemType t) => t.Index < _totals.Length ? _totals[t.Index] : 0;

    /// <summary>Change over the last day (or since the first sample).</summary>
    public float DailyChange(string key)
    {
        if (!History.TryGetValue(key, out var list) || list.Count < 2) return 0;
        int back = Math.Min(SamplesPerDay, list.Count - 1);
        return list[^1] - list[^(back + 1)];
    }

    public static string TimeOfDay(long tick)
    {
        long inDay = tick % Needs.Day;
        int minutes = (int)(inDay * 24 * 60 / Needs.Day);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    public static string Date(long tick) => $"Day {tick / Needs.Day + 1} · {TimeOfDay(tick)}";

    /// <summary>Recent alerts: departures, starvation, cave-ins in the last day.</summary>
    public int AlertCount()
    {
        long since = Sim.Tick - Needs.Day;
        int n = Sim.Events.Count(e => e.Tick >= since && (e.Faction == Me || e.Faction < 0) && ChronicleWindow.IsAlert(e.Text));
        n += Sim.RecentCaveIns.Count(c => c.Tick >= since);
        return n;
    }

    public override void _Process(double delta)
    {
        if (Main == null) return;
        if ((_hudIn -= delta) > 0) return;
        _hudIn = 0.2;
        Sample();
        UpdateHud();
        if (Tool.LastAction is { } act && Tool.ActionSerial != _lastAction)
        {
            _lastAction = Tool.ActionSerial;
            GlyphStatus.Say(act);
        }
    }

    private int _lastAction;

    private void UpdateHud()
    {
        if (Main.Test is { } test)
        {
            _colony.Text = "Test world";
            G.Retone(_colony, "Warn", bold: true);
        }
        else _colony.Text = Sim.Factions.Count > 1 ? $"{MyFaction.Name} ({Me + 1}/{Sim.Factions.Count})" : MyFaction.Name;
        _date.Text = Date(Sim.Tick);
        _date.Visible = _m.Cols > 110;

        int people = Mine.Count, children = Mine.Count(c => c.IsChild);
        int starving = Mine.Count(c => c.Starving), hungry = Mine.Count(c => c.Hungry);
        _stats[0].Set(people.ToString(), Math.Sign(DailyChange("people")), starving > 0,
            $"Colonists: {people}" + (children > 0 ? $" ({children} children)" : "") + (starving > 0 ? $", {starving} starving" : hungry > 0 ? $", {hungry} hungry" : "") + " · click: colonists");
        float days = Society.FoodDays(Sim, Me);
        _stats[1].Set(float.IsInfinity(days) ? "-" : $"{days:0.0}d", Math.Sign(DailyChange("meals")), days < 2,
            $"Food: {(float.IsInfinity(days) ? "nobody eats" : $"{days:0.0} days")} ({Society.FoodInStore(Sim, Me)} meals){(days < Needs.SettlerFoodDays ? $" · settlers come at {Needs.SettlerFoodDays:0} days" : "")}");
        _stats[2].Set(Count(ItemType.Log).ToString(), Math.Sign(DailyChange("log")), false, $"Logs: {Count(ItemType.Log)} ({DailyChange("log"):+0;-0;0}/day)");
        _stats[3].Set(Count(ItemType.Plank).ToString(), Math.Sign(DailyChange("plank")), false, $"Planks: {Count(ItemType.Plank)} ({DailyChange("plank"):+0;-0;0}/day)");
        _stats[4].Set(Count(ItemType.Stone).ToString(), Math.Sign(DailyChange("stone")), false, $"Stone: {Count(ItemType.Stone)} ({DailyChange("stone"):+0;-0;0}/day)");
        float mood = MyFaction.Society.Happiness;
        _stats[5].Set($"{mood * 100:0}%", Math.Sign(DailyChange("mood")), mood < Needs.Unhappy + 0.1f, $"Mood: {mood:P0} (goods standard {MyFaction.Society.Standard:P0}) · below {Needs.Unhappy:P0} for {Needs.UnhappyDaysToLeave} days, singles leave");
        int homeless = Mine.Count(c => !c.IsAutomaton && c.Home == null);
        _stats[6].Set(homeless > 0 ? $"{homeless}!" : MyFaction.Housing.FreeBeds.ToString(), 0, homeless > 0,
            homeless > 0 ? $"{homeless} homeless · {MyFaction.Housing.FreeBeds} free beds in {MyFaction.Housing.All.Count} homes" : $"{MyFaction.Housing.FreeBeds} free beds in {MyFaction.Housing.All.Count} homes");
        int alerts = AlertCount();
        _alerts.Visible = alerts > 0;
        _alerts.Set(alerts.ToString(), 0, false, "Alerts today · click: chronicle");
        _level.Text = $"Level {Main.View.ViewLevel}";
        _terrain.Text = Main.PendingSections > 0 ? $"building terrain {Main.PendingSections}" : !Sim.WaterReady ? "settling water…" : "";
        _speed.Index = Main.Paused ? 0 : Main.SpeedIndex + 1;
        _speed.Visible = Main.Client == null; // a guest runs at the host's pace
        _net.Text = Main.NetStatus;
        G.Retone(_net, _net.Text.StartsWith("Out of step") || _net.Text.StartsWith("Disconnected") ? "Bad" : "Info");
    }

    // ---- Status pill (when the mouse is over the world) -----------------------------------------------

    private (string, bool) WorldStatus()
    {
        if (Tool.SelectionInfo is { } sel) return (sel, true);
        string tool = Tool.SquadOrders ? "Squad orders: click ground to march, a nest to attack it · RMB: done" : Tool.ModeName(Tool.Mode);
        string hover = HoverText();
        return (hover.Length > 0 ? $"{tool} · {hover}" : $"{tool} · n/u/k/o/t/p…: orders · B: build · RMB drag: pan · Esc: menu", false);
    }

    /// <summary>"Saw horse planned: 0/2 logs here · no logs to bring: chop trees (t)" or "... waiting for a builder".</summary>
    public string Waiting(string name, ItemType material, int needed, int delivered)
    {
        string mat = material.Name.ToLowerInvariant();
        string s = $"{name} planned: {Math.Min(delivered, needed)}/{needed} {mat} here";
        if (delivered >= needed) return s + " · waiting for a builder (Construction work)";
        if (Count(material) - delivered > 0) return s + " · haulers will bring the rest";
        string from = material == ItemType.Log ? "chop trees (t)" : material == ItemType.Plank ? "saw logs at a saw bench (Carpentry)"
            : material == ItemType.Stone ? "dig rock (n)" : "make some";
        return s + $" · no {mat} to bring: {from}";
    }

    /// <summary>What is under the cursor: tile, plant, water, waterworks, farm plot, machine.</summary>
    public string HoverText() => Tool.Hovered is { } hovered ? Describe(hovered) : "";

    /// <summary>A tile and what is on it, in one line.</summary>
    public string Describe(TilePos h)
    {
        var above = h.Offset(0, 0, 1);
        string s = $"{TileInfo.Name(Sim.World.Get(h))} at {h.X},{h.Y},{h.Z}";
        if (Sim.Flora.TryGet(above, out var plant))
            s += $", {Colony.Core.Nature.PlantInfo.Name(plant.Kind)}" + (Colony.Core.Nature.PlantInfo.IsGatherable(plant) ? " (ripe)" : "");
        if (Sim.Water.MassAt(above) >= Colony.Core.Fluids.Water.MinMass)
            s += $", water {Sim.Water.Level(above)}/7" + (Sim.Water.Pressure(above) is var pr and > 0.05f ? $" (pressed by {pr:0.0})" : "");
        if (Sim.Waterworks.At(above) is { } thing)
            s += thing switch
            {
                Colony.Core.Fluids.WaterWheel ww => $", water wheel making {ww.Power:0.#} power",
                Colony.Core.Fluids.Pump pp => $", pump moving {pp.Pumped} per step",
                _ => "",
            };
        if (Sim.Farms.TryGet(above, out var crop))
            s += $", farm plot ({Colony.Core.Nature.PlantInfo.Name(crop).ToLowerInvariant()})";
        if (Tool.HoveredMachine() is { Recipe: { } making } hm)
            s += $", {hm.Name.ToLowerInvariant()} making {making.Name.ToLowerInvariant()} (F to change)";
        if (Sim.Rooms.RoomAt(above) is { } room) s += room.Faction == Me ? $" · {room.Name} (V: details)" : $" · {room.Name} of {Sim.Factions[room.Faction].Name}";
        // Blueprints say what they wait for: their materials (and where those come from), or a builder.
        if (Sim.Rooms.PartAt(above) is { Built: false } planned)
            s += " · " + Waiting(planned.Name, planned.Cost.Material, planned.Cost.Count, planned.Cells.Sum(c => Sim.Items.TryGet(c, out var p) && p.Type == planned.Cost.Material ? p.Count : 0));
        else if ((Sim.Constructions.At(above) ?? Sim.Constructions.CoverAt(above, false) ?? Sim.Constructions.CoverAt(above, true)) is { } site)
            s += " · " + Waiting(Colony.Core.Orders.Constructions.Name(site.Kind), site.Cost.Material, site.Cost.Count, Colony.Core.Orders.Constructions.MaterialsOnSite(site, Sim.Items));
        if (Sim.Items.TryGet(above, out var pile) && pile.Count > 0) s += $" · {pile.Count} {pile.Type.Name.ToLowerInvariant()}";
        if (Sim.Wildlife.NestNear(above, 2) is { } nest)
            s += $" · {nest.Kind.Name.ToLowerInvariant()} nest of {nest.Total} ({(nest.HostileTo(Me, Sim.Tick) ? "hostile" : "calm, leave them be")}; G then click: attack)";
        int ants = Sim.Wildlife.Near(above, 1).Count();
        if (ants > 0) s += $" · {G.Plural(ants, "giant ant")}";
        return s;
    }
}

/// <summary>A HUD resource: chip, bold value, ↑/↓ for the daily change; blinks bad when low; hover hint, click opens.</summary>
public partial class HudStat : GlyphControl
{
    private readonly string _glyph, _tone;
    private readonly Action _click;
    private string _value = "";
    private int _trend;
    private bool _warn, _hover;
    private string _tip = "";
    public bool Pill { get; init; }

    public HudStat(string glyph, string tone, Action click)
    {
        _glyph = glyph; _tone = tone; _click = click;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        MouseEntered += () => { _hover = true; GlyphStatus.Tip = GlyphStatus.Banner = _tip; QueueRedraw(); };
        MouseExited += () => { _hover = false; GlyphStatus.Tip = GlyphStatus.Banner = null; QueueRedraw(); };
    }
    public HudStat() : this("•", "dim", () => { }) { }

    public void Set(string value, int trend, bool warn, string tip)
    {
        _tip = tip;
        if (_hover) GlyphStatus.Tip = GlyphStatus.Banner = tip;
        if (value == _value && trend == _trend && warn == _warn && !warn) return;
        bool resize = value.Length != _value.Length || (trend != 0) != (_trend != 0);
        _value = value; _trend = trend; _warn = warn;
        if (resize) UpdateMinimumSize();
        QueueRedraw();
    }

    private int Cells => 2 + _value.Length + (_trend != 0 ? 1 : 0) + (Pill ? 1 : 0);
    public override Vector2 _GetMinimumSize() => new(Cells * Cw, Ch);

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            AcceptEvent();
            _click();
        }
    }

    public override void _Draw()
    {
        if (Pill) GlyphDraw.Round(this, new Rect2(0, Ch * 0.1f, Size.X, Ch * 0.8f), GlyphTheme.Tok("bad", 0.2f), Ch * 0.4f);
        else if (_hover) GlyphDraw.Round(this, new Rect2(-Cw * 0.3f, Ch * 0.1f, Size.X + Cw * 0.3f, Ch * 0.8f), GlyphTheme.Tok("text", 0.07f), Ch * 0.4f);
        float x = Pill ? Cw * 0.5f : 0;
        if (Pill) GlyphDraw.Glyph(this, _glyph, new Rect2(x, 0, Cw, Ch), Tok(_tone));
        else GlyphDraw.Chip(this, _glyph, new Vector2(x, 0), Tok(_tone));
        x += 2 * Cw;
        bool blink = _warn && (Time.GetTicksMsec() / 500) % 2 == 0;
        GlyphDraw.Text(this, _value, new Vector2(x, 0), _value.Length * Cw + 2, Pill ? Tok("bad") : blink ? Tok("bad") : Tok("text"), bold: true);
        x += _value.Length * Cw;
        if (_trend != 0) GlyphDraw.Glyph(this, _trend > 0 ? "↑" : "↓", new Rect2(x, 0, Cw, Ch), _trend > 0 ? Tok("good") : Tok("bad"));
    }

    public override void _Process(double delta)
    {
        if (_warn) QueueRedraw(); // blink
    }
}
