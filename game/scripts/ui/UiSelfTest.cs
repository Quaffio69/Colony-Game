using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace ColonyGame.UI;

/// <summary>
/// --ui-selftest: drives the Glyph UI with real input events (hotkeys, Esc, dragging a title bar, resizing, the dock,
/// tabs, the main menu) and prints PASS/FAIL per check, then quits. A smoke test for the window manager's behaviour.
/// </summary>
public partial class UiSelfTest : Node
{
    private readonly GameUi _ui;
    private int _failed;

    public UiSelfTest(GameUi ui) => _ui = ui;
    public UiSelfTest() : this(null!) { }

    public override async void _Ready()
    {
        try
        {
            await Run();
        }
        catch (Exception e)
        {
            GD.PrintErr($"[ui-selftest] crashed: {e}");
            _failed++;
        }
        GD.Print(_failed == 0 ? "[ui-selftest] ALL PASSED" : $"[ui-selftest] {_failed} FAILED");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private async Task Frames(int n = 2)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"[ui-selftest] {(ok ? "PASS" : "FAIL")} {what}");
        if (!ok) _failed++;
    }

    private static void Key(Key k, bool ctrl = false)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = true, CtrlPressed = ctrl });
        Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = false, CtrlPressed = ctrl });
    }

    private static void Mouse(Vector2 at, bool pressed, MouseButton button = MouseButton.Left, bool doubleClick = false)
    {
        Input.ParseInputEvent(new InputEventMouseButton
        {
            Position = at, GlobalPosition = at, ButtonIndex = button, Pressed = pressed, DoubleClick = doubleClick,
            ButtonMask = pressed ? (MouseButtonMask)(1 << ((int)button - 1)) : 0,
        });
    }

    private static void Move(Vector2 from, Vector2 to, bool held)
    {
        Input.ParseInputEvent(new InputEventMouseMotion
        {
            Position = to, GlobalPosition = to, Relative = to - from,
            ButtonMask = held ? MouseButtonMask.Left : 0,
        });
    }

    private async Task Click(Vector2 at)
    {
        Move(at - Vector2.One, at, false);
        await Frames(1);
        Mouse(at, true);
        await Frames(1);
        Mouse(at, false);
        await Frames(2);
    }

    private async Task Run()
    {
        await Frames(10);
        var m = GlyphManager.Instance!;
        var colonists = _ui.Window("colonists")!;
        var build = _ui.Window("build")!;
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        // Start from no windows, whatever the saved layout restored (the Esc-order checks count the open ones).
        foreach (var w in m.Windows) if (w.IsShown) w.Close();
        await Frames();

        // Hotkeys open and toggle; the newest is active.
        Key(Godot.Key.C);
        await Frames();
        Check("C opens Colonists", colonists.IsShown && colonists.Visible);
        Key(Godot.Key.B);
        await Frames();
        Check("B opens Build, which becomes active", build.IsShown && m.Active == build);
        Key(Godot.Key.C);
        await Frames();
        Check("C on an open, inactive window focuses it", colonists.IsShown && m.Active == colonists);
        Key(Godot.Key.C);
        await Frames();
        Check("C on the active window minimises it", colonists.State.Min && !colonists.Visible && m.Active == build);
        Key(Godot.Key.C);
        await Frames();
        Check("C again restores it", colonists.IsShown && m.Active == colonists);

        // Drag by the title row: whole cells.
        int x0 = colonists.State.X, y0 = colonists.State.Y;
        var grab = colonists.GlobalPosition + new Vector2(8 * cw, ch / 2);
        Move(grab - Vector2.One, grab, false);
        await Frames(1);
        Mouse(grab, true);
        await Frames(1);
        var to = grab + new Vector2(5 * cw + 1, 2 * ch + 1);
        Move(grab, to, true);
        await Frames(1);
        Mouse(to, false);
        await Frames(2);
        Check($"dragging the title moves by whole cells ({x0},{y0} -> {colonists.State.X},{colonists.State.Y})", colonists.State.X == x0 + 5 && colonists.State.Y == y0 + 2);

        // Resize from the corner grip.
        int w0 = colonists.State.W, h0 = colonists.State.H;
        var corner = colonists.GlobalPosition + colonists.Size - new Vector2(cw / 2, ch / 2);
        Move(corner - Vector2.One, corner, false);
        await Frames(1);
        Mouse(corner, true);
        await Frames(1);
        var to2 = corner + new Vector2(4 * cw, 3 * ch);
        Move(corner, to2, true);
        await Frames(1);
        Mouse(to2, false);
        await Frames(2);
        Check($"the corner grip resizes ({w0}x{h0} -> {colonists.State.W}x{colonists.State.H})", colonists.State.W == w0 + 4 && colonists.State.H == h0 + 3);

        // Double-click the title maximises and restores.
        var title = colonists.GlobalPosition + new Vector2(10 * cw, ch / 2);
        Mouse(title, true, doubleClick: true);
        Mouse(title, false);
        await Frames(2);
        Check("double-clicking the title maximises", colonists.Maximized && colonists.Size.X > (m.Cols - 4) * cw);
        Mouse(title = colonists.GlobalPosition + new Vector2(10 * cw, ch / 2), true, doubleClick: true);
        Mouse(title, false);
        await Frames(2);
        Check("and again restores", !colonists.Maximized);

        // Put Colonists back where it was (the drag and resize above moved it over Build).
        colonists.State.X = x0; colonists.State.Y = y0; colonists.State.W = w0; colonists.State.H = h0;
        m.Layout(colonists);
        await Frames();

        // Clicking a window brings it to the front.
        Key(Godot.Key.B);
        await Frames();
        Check("B focuses Build", m.Active == build);
        await Click(colonists.GlobalPosition + new Vector2(colonists.Size.X - 2 * cw, colonists.Size.Y - 2 * ch));
        Check("clicking inside Colonists brings it to the front", m.Active == colonists);

        // Tabs (segmented) in Build.
        Key(Godot.Key.B);
        await Frames();
        var tabs = FindFirst<GlyphSegmented>(build.Header);
        Check("Build has its tab row", tabs != null);
        if (tabs != null)
        {
            // "Orders" (6+2) + "Zones" (5+2) cells: the third segment starts at 15 cells.
            await Click(tabs.GlobalPosition + new Vector2(16 * cw, ch / 2));
            await Frames(3);
            Check($"clicking the third tab selects Rooms (index {tabs.Index})", tabs.Index == 2);
        }

        // A build tool, then Esc order: cancel placement, then close the active window, then the menu.
        _ui.Tool.SelectWorkshop(Colony.Core.Industry.Catalog.Home);
        await Frames();
        Key(Godot.Key.Escape);
        await Frames();
        Check("Esc first drops the build tool", _ui.Tool.Mode == DesignationTool.ToolMode.None && build.IsShown);
        var active = m.Active;
        Key(Godot.Key.Escape);
        await Frames();
        Check($"Esc then closes the active window ({active?.Id})", active != null && !active.IsShown);
        Key(Godot.Key.Escape);
        await Frames();
        Key(Godot.Key.Escape);
        await Frames();
        Check("with no windows left, Esc opens the main menu", m.Windows.TrueForAll(w => !w.IsShown) && _ui.MenuOpen);
        long tick = _ui.Sim.Tick;
        await Frames(10);
        Check("the sim waits while the menu is up", _ui.Sim.Tick == tick);
        Key(Godot.Key.Escape);
        await Frames();
        Check("Esc closes the menu", !_ui.MenuOpen);

        // The dock: click an item to open, again to minimise.
        var dockItem = FindDock(m, "Stocks");
        Check("the dock lists Stocks", dockItem != null);
        if (dockItem != null)
        {
            var stocks = _ui.Window("stocks")!;
            await Click(dockItem.GlobalPosition + dockItem.Size / 2);
            Check("clicking it in the dock opens it", stocks.IsShown && m.Active == stocks);
            await Click(dockItem.GlobalPosition + dockItem.Size / 2);
            Check("clicking it again minimises it", stocks.State.Min);
        }

        // Ctrl+Tab cycles focus.
        _ui.OpenWindow("colonists");
        _ui.OpenWindow("work");
        await Frames();
        var before = m.Active;
        Key(Godot.Key.Tab, ctrl: true);
        await Frames();
        Check($"Ctrl+Tab brings another window forward ({before?.Id} -> {m.Active?.Id})", m.Active != before && m.Active != null);

        // Appearance: switching theme rebuilds without errors.
        string theme = GlyphTheme.Options.Theme;
        GlyphTheme.Set(o => o.Theme = "glacier");
        await Frames(3);
        Check("theme switch applies", GlyphTheme.Current.Id == "glacier");
        GlyphTheme.Set(o => o.Theme = theme);
        await Frames(3);
        Check("windows survive the rebuild", _ui.Window("work")!.Content.GetChildCount() > 0);

        // Orders: no tool to begin with; an order's hotkey picks it and brings up the ribbon; a right click puts it down.
        foreach (var w in m.Windows.Where(w => w.IsShown).ToList()) w.Close();
        while (_ui.Tool.CancelPlacement()) { }
        await Frames();
        var tool = _ui.Tool;
        Check("no tool once everything is put down, and no ribbon", tool.Mode == DesignationTool.ToolMode.None && !_ui.Ribbon.Open);
        Key(Godot.Key.N);
        await Frames();
        Check("n picks Dig and brings up the orders ribbon", tool.Mode == DesignationTool.ToolMode.Dig && _ui.Ribbon.Open);
        var mid = GetViewport().GetVisibleRect().Size / 2;
        Move(mid - Vector2.One, mid, false);
        await Frames(1);
        Mouse(mid, true, MouseButton.Right);
        Mouse(mid, false, MouseButton.Right);
        await Frames();
        Check("a right click puts the order down (the ribbon goes)", tool.Mode == DesignationTool.ToolMode.None && !_ui.Ribbon.Open);
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.Shift, PhysicalKeycode = Godot.Key.Shift, Pressed = true, ShiftPressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.P, PhysicalKeycode = Godot.Key.P, Pressed = true, ShiftPressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.P, PhysicalKeycode = Godot.Key.P, Pressed = false, ShiftPressed = true });
        int level = _ui.Main.View.ViewLevel;
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.Shift, PhysicalKeycode = Godot.Key.Shift, Pressed = false });
        await Frames();
        Check("Shift+P picks Stockpile (and isn't a level tap)", tool.Mode == DesignationTool.ToolMode.Stockpile && _ui.Main.View.ViewLevel == level);
        Key(Godot.Key.Escape);
        await Frames();
        Key(Godot.Key.Ctrl);
        await Frames();
        Check($"tapping Ctrl goes a level down ({level} -> {_ui.Main.View.ViewLevel})", _ui.Main.View.ViewLevel == level - 1);
        Key(Godot.Key.Shift);
        await Frames();
        Check("tapping Shift goes a level up", _ui.Main.View.ViewLevel == level);
        _ui.Ribbon.Pinned = true;
        await Frames();
        Check("the Orders pill pins the ribbon open with no tool", _ui.Ribbon.Open && tool.Mode == DesignationTool.ToolMode.None);
        _ui.Ribbon.Pinned = false;
    }

    private static T? FindFirst<T>(Node root) where T : class
    {
        foreach (var c in root.GetChildren())
        {
            if (c is T t) return t;
            if (FindFirst<T>(c) is { } d) return d;
        }
        return null;
    }

    private static Control? FindDock(GlyphManager m, string title)
    {
        Control? Find(Node n)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is Control ctl && ctl.HasMeta("dock") && DockTitle(ctl) == title) return ctl;
                if (Find(c) is { } d) return d;
            }
            return null;
        }
        return Find(m);
    }

    private static string DockTitle(Control dockItem) => dockItem.GetMeta("title", "").AsString();
}
