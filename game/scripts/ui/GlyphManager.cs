using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ColonyGame.UI;

/// <summary>
/// The Glyph UI root (GLYPH_UI.md section 5): owns the generated theme, the window stack (z-order = child order; the
/// front window is the active one), the HUD row at the top and the dock and status pill on the bottom row, hotkeys,
/// Esc and Ctrl+Tab, and saving the layout. Everything sits on a character grid centred in the viewport.
/// </summary>
public partial class GlyphManager : Control
{
    public static GlyphManager? Instance { get; private set; }

    public readonly List<GlyphWindow> Windows = new();
    /// <summary>Worst update time per window (ms), for the bench report.</summary>
    public readonly Dictionary<string, double> TickMs = new();
    private readonly Control _desk = new() { MouseFilter = MouseFilterEnum.Ignore };
    public GlyphPanel Hud { get; } = new() { Pill = true };
    public HBoxContainer HudRow { get; } = new();
    private readonly GlyphPanel _dock = new() { Pill = true };
    private readonly HBoxContainer _dockRow = new();
    private readonly GlyphPanel _status = new() { Pill = true };
    private readonly Label _statusText = new();
    private readonly GlyphPanel _banner = new() { Pill = true };
    private readonly TipText _bannerText = new();
    private string? _tipSeen, _bannerShown;
    private double _tipSince;
    private readonly Control _overlay = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly Dictionary<Key, GlyphWindow> _hotkeys = new();

    /// <summary>Cancels a pending placement or selection in the world (Esc does that before closing windows).</summary>
    public Func<bool>? CancelPlacement;
    /// <summary>Called when Esc has nothing left to close (opens the main menu).</summary>
    public Action? EscapeFallback;
    /// <summary>Status text when the mouse is over nothing in the UI: (text, is it a prompt?).</summary>
    public Func<(string Text, bool Prompt)>? WorldStatus;

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public Vector2 Origin { get; private set; }
    private const int DeskTop = 2;
    private int DeskBottom => Rows - 2;

    private double _saveIn = -1, _tickIn;
    private Vector2 _lastSize;

    public GlyphManager()
    {
        GlyphTheme.EnsureLoaded();
        Instance = this;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        // One copy of the frame before any panel draws, so every glass panel blurs the game itself.
        AddChild(new BackBufferCopy { CopyMode = BackBufferCopy.CopyModeEnum.Viewport });
        _desk.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_desk);
        HudRow.MouseFilter = MouseFilterEnum.Ignore;
        Hud.AddChild(HudRow);
        AddChild(Hud);
        _dockRow.MouseFilter = MouseFilterEnum.Ignore;
        _dockRow.AddThemeConstantOverride("separation", 0);
        _dock.AddChild(_dockRow);
        AddChild(_dock);
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusText.MouseFilter = MouseFilterEnum.Ignore;
        _status.AddChild(_statusText);
        _status.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_status);
        _banner.AddChild(_bannerText);
        _banner.MouseFilter = MouseFilterEnum.Ignore;
        _banner.Visible = false;
        _banner.ZIndex = 50;
        _overlay.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_overlay);
        AddChild(_banner); // above everything, menu included
        GlyphTheme.Changed += OnThemeChanged;
        Theme = GlyphTheme.Theme;
    }
    public override void _ExitTree()
    {
        GlyphTheme.Changed -= OnThemeChanged;
        if (Instance == this) Instance = null;
    }

    /// <summary>Full-screen layers above everything (the main menu).</summary>
    public Control Overlay => _overlay;

    private void OnThemeChanged()
    {
        Theme = GlyphTheme.Theme;
        GetTree().Root.ContentScaleFactor = GlyphTheme.Options.Scale;
        _lastSize = Vector2.Zero; // re-layout on the new cell size
        foreach (var w in Windows) if (w.IsShown) w.Rebuild();
        RebuildDock();
    }

    public void Register(GlyphWindow w, Key hotkey = Key.None)
    {
        w.Manager = this;
        if (GlyphTheme.Options.Windows.TryGetValue(w.Id, out var saved)) w.State = saved;
        else
        {
            w.State = new WindowState { X = w.Default.X, Y = w.Default.Y, W = w.Default.W, H = w.Default.H, Open = w.Default.Open };
            GlyphTheme.Options.Windows[w.Id] = w.State;
        }
        Windows.Add(w);
        _desk.AddChild(w);
        if (hotkey != Key.None) _hotkeys[hotkey] = w;
        RebuildDock();
    }

    public GlyphWindow? Active
    {
        get
        {
            for (int i = _desk.GetChildCount() - 1; i >= 0; i--)
                if (_desk.GetChild(i) is GlyphWindow { IsShown: true } w) return w;
            return null;
        }
    }

    public void Focus(GlyphWindow w)
    {
        _desk.MoveChild(w, -1);
        UpdateActive();
    }

    public void Open(GlyphWindow w)
    {
        w.State.Open = true;
        w.State.Min = false;
        w.EnsureBuilt();
        Layout(w);
        Focus(w);
        Changed();
    }

    public void Minimize(GlyphWindow w)
    {
        w.State.Min = true;
        Changed();
    }

    /// <summary>Dock / hotkey: open a closed window, focus an unfocused one, minimise the focused one.</summary>
    public void Toggle(GlyphWindow w)
    {
        if (w.IsShown)
        {
            if (Active == w) Minimize(w);
            else Focus(w);
        }
        else Open(w);
    }

    /// <summary>Something about the windows changed: update visibility and active styling, save soon.</summary>
    public void Changed()
    {
        foreach (var w in Windows)
        {
            bool show = w.IsShown;
            if (show && !w.Visible) { w.EnsureBuilt(); Layout(w); }
            w.Visible = show;
        }
        UpdateActive();
        _saveIn = 0.5;
    }

    private void UpdateActive()
    {
        var active = Active;
        foreach (var w in Windows) w.Active = w == active;
        foreach (var item in _dockRow.GetChildren().OfType<DockItem>()) item.QueueRedraw();
    }

    /// <summary>Clamps a window to the desk (min size, at least 4 cells on screen) and places it.</summary>
    public void Layout(GlyphWindow w)
    {
        if (Cols == 0) return; // before the first frame: placed once the grid is known
        var s = w.State;
        s.W = Math.Clamp(Math.Max(s.W, w.NeededCols()), w.MinCols, Math.Max(w.MinCols, Cols - 2)); // never narrower than its content
        s.H = Math.Clamp(s.H, w.MinRows, Math.Max(w.MinRows, DeskBottom - DeskTop));
        if (!s.Placed)
        {
            // Defaults: a negative X counts from the right edge; keep the whole window on screen if it fits.
            if (s.X < 0) s.X = Cols + s.X - s.W;
            s.X = Math.Clamp(s.X, 1, Math.Max(1, Cols - 1 - s.W));
            s.Y = Math.Clamp(s.Y, DeskTop, Math.Max(DeskTop, DeskBottom - s.H));
            s.Placed = true;
        }
        s.X = Math.Clamp(s.X, 4 - s.W, Math.Max(4 - s.W, Cols - 4));
        s.Y = Math.Clamp(s.Y, DeskTop, Math.Max(DeskTop, DeskBottom - 2));
        var rect = w.Maximized ? new Rect2I(1, DeskTop, Cols - 2, DeskBottom - DeskTop) : new Rect2I(s.X, s.Y, s.W, s.H);
        w.Place(rect, Origin);
    }

    public void ResetLayout()
    {
        foreach (var w in Windows)
        {
            w.State.X = w.Default.X; w.State.Y = w.Default.Y; w.State.W = w.Default.W; w.State.H = w.Default.H;
            w.State.Placed = false;
            w.Maximized = false;
            Layout(w);
        }
        Changed();
    }

    /// <summary>True if the point (viewport coordinates) is over any shown panel: the world should ignore the mouse.</summary>
    public bool Blocks(Vector2 point)
    {
        if (_overlay.GetChildren().OfType<Control>().Any(c => c.Visible)) return true;
        if (Hud.Visible && Hud.GetGlobalRect().HasPoint(point)) return true;
        if (_dock.GetGlobalRect().HasPoint(point)) return true;
        if (Blockers.Any(c => c.IsVisibleInTree() && c.GetGlobalRect().HasPoint(point))) return true;
        return Windows.Any(w => w.Visible && w.GetGlobalRect().HasPoint(point));
    }

    /// <summary>Other panels the world shouldn't see clicks through (the orders ribbon and its pill).</summary>
    public readonly List<Control> Blockers = new();

    /// <summary>A tooltip or banner is up at the cursor.</summary>
    public bool TipShowing => _banner.Visible;

    /// <summary>The bottom row's dock pill, for panels laid out beside or above it.</summary>
    public Rect2 DockRect => new(_dock.Position, _dock.Size);

    public bool MenuOpen => _overlay.GetChildren().OfType<Control>().Any(c => c.Visible);

    // ---- Input --------------------------------------------------------------------------------------------

    public override void _Input(InputEvent e)
    {
        // Clicking anywhere in a window brings it to the front (the click itself still goes to its controls).
        if (e is InputEventMouseButton { Pressed: true } b && !MenuOpen)
        {
            var p = b.Position;
            for (int i = _desk.GetChildCount() - 1; i >= 0; i--)
                if (_desk.GetChild(i) is GlyphWindow { Visible: true } w && w.GetGlobalRect().HasPoint(p))
                {
                    if (Active != w) Focus(w);
                    break;
                }
        }
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            if (GetViewport().GuiGetFocusOwner() is LineEdit le) { le.ReleaseFocus(); GetViewport().SetInputAsHandled(); return; }
            if (MenuOpen) { EscapeFallback?.Invoke(); }
            else if (CancelPlacement?.Invoke() == true) { }
            else if (Active is { } a) a.Close();
            else EscapeFallback?.Invoke();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || MenuOpen) return;
        if (k.Keycode == Key.Tab && k.CtrlPressed)
        {
            var shown = Windows.Where(w => w.IsShown).ToList();
            if (shown.Count > 0)
            {
                // Cycle: the backmost shown window comes to the front.
                for (int i = 0; i < _desk.GetChildCount(); i++)
                    if (_desk.GetChild(i) is GlyphWindow { IsShown: true } back) { Focus(back); break; }
            }
            GetViewport().SetInputAsHandled();
            return;
        }
        if (k.CtrlPressed || k.AltPressed || k.ShiftPressed) return; // shifted letters are order hotkeys (OrdersRibbon)
        if (_hotkeys.TryGetValue(k.Keycode, out var w))
        {
            Toggle(w);
            GetViewport().SetInputAsHandled();
        }
    }

    // ---- Per frame ----------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        var size = GetViewportRect().Size;
        if (size != _lastSize)
        {
            _lastSize = size;
            Cols = Math.Max(20, (int)(size.X / GlyphTheme.Cw));
            Rows = Math.Max(10, (int)(size.Y / GlyphTheme.Ch));
            Origin = new Vector2(MathF.Floor((size.X - Cols * GlyphTheme.Cw) / 2), MathF.Floor((size.Y - Rows * GlyphTheme.Ch) / 2));
            foreach (var w in Windows) if (w.Visible) Layout(w);
            Hud.Position = Origin + new Vector2(GlyphTheme.Cw, 0);
            Hud.Size = new Vector2((Cols - 2) * GlyphTheme.Cw, GlyphTheme.Ch);
            HudRow.Position = new Vector2(GlyphTheme.Cw, 0);
            HudRow.Size = new Vector2((Cols - 4) * GlyphTheme.Cw, GlyphTheme.Ch);
            HudRow.AddThemeConstantOverride("separation", 0);
            RebuildDock();
        }

        if ((_tickIn -= delta) <= 0)
        {
            _tickIn = 0.25;
            foreach (var w in Windows)
                if (w.Visible)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    w.Tick();
                    if (!w.Maximized && w.NeededCols() > w.State.W && w.State.W < Cols - 2)
                    {
                        // Grown wider than the window (new rows, longer text): widen it, keeping it on screen.
                        int need = Math.Min(w.NeededCols(), Cols - 2);
                        w.State.X = Math.Max(1, Math.Min(w.State.X, Cols - 1 - need));
                        Layout(w);
                    }
                    double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    TickMs[w.Id] = Math.Max(TickMs.GetValueOrDefault(w.Id), ms);
                }
        }
        UpdateStatus();
        UpdateBanner();

        if (_saveIn > 0 && (_saveIn -= delta) <= 0) GlyphTheme.Save();
    }

    // ---- Dock and status ----------------------------------------------------------------------------------

    private void RebuildDock()
    {
        G.Clear(_dockRow);
        foreach (var w in Windows) _dockRow.AddChild(new DockItem(this, w, Cols < 96));
        float width = GlyphTheme.Cw;
        foreach (var item in _dockRow.GetChildren().OfType<DockItem>()) width += item.Cells * GlyphTheme.Cw + GlyphTheme.Cw;
        _dockRow.AddThemeConstantOverride("separation", (int)GlyphTheme.Cw);
        _dockRow.Position = new Vector2(GlyphTheme.Cw, 0);
        _dock.Position = Origin + new Vector2(GlyphTheme.Cw, (Rows - 1) * GlyphTheme.Ch);
        _dock.Size = new Vector2(width, GlyphTheme.Ch);
        _dockRow.Size = new Vector2(width - 2 * GlyphTheme.Cw, GlyphTheme.Ch);
    }


    /// <summary>Seconds the mouse rests on a control before its tip shows at the cursor.</summary>
    private const double TipDelay = 0.35;
    /// <summary>Widest a tooltip grows before it wraps (columns).</summary>
    private const int TipCols = 46;

    /// <summary>
    /// The cursor banner and tooltips, in one glass box just below and right of the mouse: <see cref="GlyphStatus.Banner"/>
    /// at once (HUD pills, the box size while dragging), else the hovered control's <see cref="GlyphStatus.Tip"/> once the
    /// mouse has rested on it. Wrapped to <see cref="TipCols"/>; a first line followed by '\n' is a bold title.
    /// </summary>
    private void UpdateBanner()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        string? text = GlyphStatus.Banner;
        if (string.IsNullOrEmpty(text) && GlyphStatus.Tip is { Length: > 0 } tip)
        {
            if (tip != _tipSeen) { _tipSeen = tip; _tipSince = now; }
            if (now - _tipSince >= TipDelay) text = tip;
        }
        else if (GlyphStatus.Tip == null) _tipSeen = null;
        _banner.Visible = !string.IsNullOrEmpty(text) && Cols > 0;
        if (!_banner.Visible) { _bannerShown = null; return; }
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        if (text != _bannerShown)
        {
            _bannerShown = text;
            _bannerText.Set(text!, Math.Clamp(Cols / 2, 12, TipCols));
        }
        int rows = Math.Max(1, _bannerText.Rows);
        float w = (_bannerText.Cols + 2) * cw, h = rows == 1 ? ch : (rows + 0.6f) * ch;
        var mouse = GetViewport().GetMousePosition();
        var size = GetViewportRect().Size;
        var at = mouse + new Vector2(cw, ch * 1.1f);
        at.X = Math.Clamp(at.X, 0, Math.Max(0, size.X - w));
        if (at.Y + h > size.Y) at.Y = Math.Max(0, mouse.Y - h - ch * 0.6f);
        _banner.Pill = rows == 1;
        _banner.Size = new Vector2(w, h);
        _banner.Position = at;
        _bannerText.Position = new Vector2(cw, rows == 1 ? 0 : ch * 0.3f);
        _bannerText.Size = new Vector2(w - 2 * cw, rows * ch);
    }

    /// <summary>The status pill (bottom right): what the last order did and other messages, for a few seconds.</summary>
    private void UpdateStatus()
    {
        string text;
        bool prompt;
        double now = Time.GetTicksMsec() / 1000.0;
        if (GlyphStatus.Message != null && now < GlyphStatus.MessageUntil) (text, prompt) = (GlyphStatus.Message, true);
        else if (WorldStatus?.Invoke() is { } ws) (text, prompt) = ws;
        else (text, prompt) = ("", false);

        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        float dockEnd = _dock.Position.X + _dock.Size.X - Origin.X;
        int room = (int)((Cols * cw - dockEnd) / cw) - 4;
        _status.Visible = room > 6 && text.Length > 0 && !MenuOpen;
        if (!_status.Visible) return;
        if (text.Length > room) text = text[..(room - 1)] + "…";
        _statusText.Text = text;
        G.Retone(_statusText, prompt ? "Warn" : "Dim");
        float w = (text.Length + 2) * cw;
        _status.Size = new Vector2(w, ch);
        _status.Position = Origin + new Vector2(Cols * cw - cw - w, (Rows - 1) * ch);
        _statusText.Position = new Vector2(cw, 0);
        _statusText.Size = new Vector2(w - 2 * cw, ch);
    }

    /// <summary>A dock entry: glyph and title; focused = accent @20% with an accent underline, open = faint underline.</summary>
    private sealed partial class DockItem : GlyphControl
    {
        private readonly GlyphManager _m;
        private readonly GlyphWindow _w;
        private readonly bool _compact;
        private bool _hover;
        public int Cells => _compact ? 3 : _w.Title.Length + 4;

        public DockItem(GlyphManager m, GlyphWindow w, bool compact)
        {
            _m = m; _w = w; _compact = compact;
            SetMeta("dock", true);
            SetMeta("title", w?.Title ?? "");
            MouseDefaultCursorShape = CursorShape.PointingHand;
            MouseEntered += () =>
            {
                _hover = true;
                QueueRedraw();
                GlyphStatus.Tip = $"{_w.Title} ({_w.Hotkey}) · " + (_w.IsShown ? _m.Active == _w ? "click to minimise" : "click to focus" : _w.State.Min ? "minimised · click to restore" : "closed · click to open");
            };
            MouseExited += () => { _hover = false; GlyphStatus.Tip = null; QueueRedraw(); };
        }
        public DockItem() : this(null!, null!, false) { }

        public override Vector2 _GetMinimumSize() => new(Cells * Cw, Ch);

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                AcceptEvent();
                _m.Toggle(_w);
            }
        }

        public override void _Draw()
        {
            bool vis = _w.IsShown, on = vis && _m.Active == _w;
            var r = new Rect2(0, Ch * 0.14f, Size.X, Ch * 0.72f);
            if (on) GlyphDraw.Round(this, r, GlyphTheme.Tok("accent", 0.2f), Ch * 0.36f);
            else if (_hover) GlyphDraw.Round(this, r, GlyphTheme.Tok("text", 0.08f), Ch * 0.36f);
            if (vis) DrawRect(new Rect2(Size.X / 2 - Cw / 2, Ch * 0.86f - 1, Cw, 2), on ? Tok("accent") : GlyphTheme.Tok("text", 0.4f));
            GlyphDraw.Glyph(this, _w.Glyph, new Rect2(Cw, 0, Cw, Ch), vis ? Tok(_w.Tone) : Tok("faint"));
            if (!_compact)
                GlyphDraw.Text(this, _w.Title, new Vector2(3 * Cw, 0), Size.X, on ? Tok("hi") : vis ? Tok("text") : Tok("faint"), on);
        }
    }
}
