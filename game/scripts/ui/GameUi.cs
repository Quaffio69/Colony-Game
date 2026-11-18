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
/// (each with its hotkey: C colonists, I stocks, B build, L chronicle, H housing, J work, V inspect, F1 tests,
/// F2 appearance, F3 help), the status pill (hover hints, the tool and what it is doing) and the main menu (Esc
/// when nothing is left to close). Reads the sim; player actions go through the same calls the old menus made.
/// </summary>
public partial class GameUi : CanvasLayer
{
    public readonly Main Main;
    public ColonySim Sim => Main.Sim;
    public DesignationTool Tool => Main.Tool;
    private readonly GlyphManager _m;
    private readonly Dictionary<string, GlyphWindow> _windows = new();
    private readonly MainMenu _menu;
    private BuildWindow _build = null!;

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
        Add(new TestsWindow(this), Key.F1);
        Add(new AppearanceWindow(this), Key.F2);
        Add(new HelpWindow(this), Key.F3);

        BuildHud();
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
    public void ShowMenu(bool show) => _menu.Show(show);
    public void ResetLayout() => _m.ResetLayout();

    /// <summary>--build-menu=WORKSHOP: opens Build on that workshop's room tab.</summary>
    public void OpenBuild(string workshopId)
    {
        _m.Open(_build);
        _build.ShowWorkshop(workshopId);
    }

    // ---- HUD --------------------------------------------------------------------------------------------

    private Label _colony = null!, _date = null!, _level = null!, _terrain = null!;
    private readonly List<HudStat> _stats = new();
    private HudStat _alerts = null!;
    private GlyphSegmented _speed = null!;

    private void BuildHud()
    {
        var row = _m.HudRow;
        row.AddChild(new GlyphChip("☼", "accent"));
        row.AddChild(Space(1));
        _colony = G.Text("", "Hi", bold: true);
        row.AddChild(_colony);
        row.AddChild(Space(2));
        _date = G.Text("", "Dim");
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
        _terrain = G.Text("", "Faint");
        row.AddChild(_terrain);
        row.AddChild(Space(2));
        _level = G.Text("", "Dim");
        _level.MouseFilter = Control.MouseFilterEnum.Pass;
        _level.WithTip("View level · Z/X or PgDn/PgUp to go down/up");
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
        _totals = Sim.Items.Totals();
        long step = Needs.Day / SamplesPerDay;
        if (_lastSample >= 0 && Sim.Tick - _lastSample < step) return;
        _lastSample = Sim.Tick;
        void Add(string key, float v)
        {
            if (!History.TryGetValue(key, out var list)) History[key] = list = new List<float>();
            list.Add(v);
            if (list.Count > SamplesPerDay * 4) list.RemoveAt(0);
        }
        Add("meals", Society.FoodInStore(Sim));
        Add("log", Count(ItemType.Log));
        Add("plank", Count(ItemType.Plank));
        Add("stone", Count(ItemType.Stone));
        Add("people", Sim.Colonists.Count);
        Add("mood", Sim.Society.Happiness * 100);
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
        int n = Sim.Events.Count(e => e.Tick >= since && ChronicleWindow.IsAlert(e.Text));
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
        if (Tool.LastAction is { } act && act != _lastAction)
        {
            _lastAction = act;
            GlyphStatus.Say(act);
        }
    }

    private string? _lastAction;

    private void UpdateHud()
    {
        if (Main.Test is { } test)
        {
            _colony.Text = "Test world";
            G.Retone(_colony, "Warn", bold: true);
        }
        else _colony.Text = "The Colony";
        _date.Text = Date(Sim.Tick);
        _date.Visible = _m.Cols > 110;

        int people = Sim.Colonists.Count, children = Sim.Colonists.Count(c => c.IsChild);
        int starving = Sim.Colonists.Count(c => c.Starving), hungry = Sim.Colonists.Count(c => c.Hungry);
        _stats[0].Set(people.ToString(), Math.Sign(DailyChange("people")), starving > 0,
            $"Colonists: {people}" + (children > 0 ? $" ({children} children)" : "") + (starving > 0 ? $", {starving} starving" : hungry > 0 ? $", {hungry} hungry" : "") + " · click: colonists");
        float days = Society.FoodDays(Sim);
        _stats[1].Set(float.IsInfinity(days) ? "-" : $"{days:0.0}d", Math.Sign(DailyChange("meals")), days < 2,
            $"Food: {(float.IsInfinity(days) ? "nobody eats" : $"{days:0.0} days")} ({Society.FoodInStore(Sim)} meals){(days < Needs.SettlerFoodDays ? $" · settlers come at {Needs.SettlerFoodDays:0} days" : "")}");
        _stats[2].Set(Count(ItemType.Log).ToString(), Math.Sign(DailyChange("log")), false, $"Logs: {Count(ItemType.Log)} ({DailyChange("log"):+0;-0;0}/day)");
        _stats[3].Set(Count(ItemType.Plank).ToString(), Math.Sign(DailyChange("plank")), false, $"Planks: {Count(ItemType.Plank)} ({DailyChange("plank"):+0;-0;0}/day)");
        _stats[4].Set(Count(ItemType.Stone).ToString(), Math.Sign(DailyChange("stone")), false, $"Stone: {Count(ItemType.Stone)} ({DailyChange("stone"):+0;-0;0}/day)");
        float mood = Sim.Society.Happiness;
        _stats[5].Set($"{mood * 100:0}%", Math.Sign(DailyChange("mood")), mood < Needs.Unhappy + 0.1f, $"Mood: {mood:P0} (goods standard {Sim.Society.Standard:P0}) · below {Needs.Unhappy:P0} for {Needs.UnhappyDaysToLeave} days, singles leave");
        int homeless = Sim.Colonists.Count(c => !c.IsAutomaton && c.Home == null);
        _stats[6].Set(homeless > 0 ? $"{homeless}!" : Sim.Housing.FreeBeds.ToString(), 0, homeless > 0,
            homeless > 0 ? $"{homeless} homeless · {Sim.Housing.FreeBeds} free beds in {Sim.Housing.All.Count} homes" : $"{Sim.Housing.FreeBeds} free beds in {Sim.Housing.All.Count} homes");
        int alerts = AlertCount();
        _alerts.Visible = alerts > 0;
        _alerts.Set(alerts.ToString(), 0, false, "Alerts today · click: chronicle");
        _level.Text = $"Level {Main.View.ViewLevel}";
        _terrain.Text = Main.PendingSections > 0 ? $"building terrain {Main.PendingSections}" : !Sim.WaterReady ? "settling water…" : "";
        _speed.Index = Main.Paused ? 0 : Main.SpeedIndex + 1;
    }

    // ---- Status pill (when the mouse is over the world) -----------------------------------------------

    private (string, bool) WorldStatus()
    {
        if (Tool.SelectionInfo is { } sel) return (sel, true);
        string tool = Tool.ModeName(Tool.Mode);
        string hover = HoverText();
        return (hover.Length > 0 ? $"{tool} · {hover}" : $"{tool} · Tab: orders · B: build · Esc: menu", false);
    }

    /// <summary>What is under the cursor: tile, plant, water, waterworks, farm plot, machine.</summary>
    public string HoverText()
    {
        if (Tool.Hovered is not { } h) return "";
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
        if (Sim.Rooms.RoomAt(above) is { } room) s += $" · {room.Name} (V: details)";
        if (Sim.Items.TryGet(above, out var pile) && pile.Count > 0) s += $" · {pile.Count} {pile.Type.Name.ToLowerInvariant()}";
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
        MouseEntered += () => { _hover = true; GlyphStatus.Tip = _tip; QueueRedraw(); };
        MouseExited += () => { _hover = false; GlyphStatus.Tip = null; QueueRedraw(); };
    }
    public HudStat() : this("•", "dim", () => { }) { }

    public void Set(string value, int trend, bool warn, string tip)
    {
        _tip = tip;
        if (_hover) GlyphStatus.Tip = tip;
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
