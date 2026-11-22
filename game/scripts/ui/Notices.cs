using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core;
using Godot;
using ToolMode = ColonyGame.DesignationTool.ToolMode;

namespace ColonyGame.UI;

/// <summary>
/// Overlays that tell the player what is going on without opening anything:
/// <list type="bullet">
/// <item>the tool card: the tool in hand in an accent-framed pill at the bottom centre (its icon, name, what to do,
/// the box's size while dragging), so it is always obvious what a click will do;</item>
/// <item>the box size at the cursor while dragging a box;</item>
/// <item>a "Paused" pill under the HUD while the game is paused;</item>
/// <item>toasts: the colony's news (arrivals, births, departures, cave-ins, raids...) for a few seconds under the HUD,
/// alerts in red; click one to open the Chronicle.</item>
/// </list>
/// Read-only: none of them take clicks from the world except the toasts.
/// </summary>
public partial class Notices : Control
{
    private readonly GameUi _ui;
    private readonly GlyphPanel _card = new() { Pill = true, MouseFilter = MouseFilterEnum.Ignore };
    private readonly CardLine _cardLine;
    private readonly GlyphPanel _paused = new() { Pill = true, MouseFilter = MouseFilterEnum.Ignore };
    private readonly Label _pausedText = new() { MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(GlyphPanel Panel, Label Text, double Until)> _toasts = new();
    private SimEvent? _lastEvent;
    private bool _bannerOwned;

    public Notices(GameUi ui)
    {
        _ui = ui;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        _cardLine = new CardLine(this);
        _card.AddChild(_cardLine);
        _card.Active = true; // the accent frame
        AddChild(_card);
        _paused.AddChild(_pausedText);
        _paused.Active = true;
        AddChild(_paused);
    }
    public Notices() : this(null!) { }

    public override void _Ready()
    {
        // News from before this view opened is old news.
        if (_ui != null && _ui.Sim.Events.Count > 0) _lastEvent = _ui.Sim.Events[^1];
    }

    public override void _Process(double delta)
    {
        if (_ui == null || GlyphManager.Instance is not { } m || m.Cols == 0) return;
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        bool menu = m.MenuOpen;

        // The tool card.
        var tool = _ui.Tool;
        bool show = !menu && (tool.Mode != ToolMode.None || tool.SquadOrders);
        _card.Visible = show;
        if (show)
        {
            _cardLine.Refresh(m.Cols - 8);
            float w = _cardLine.Cells * cw;
            _card.Size = new Vector2(w, ch);
            _card.Position = m.Origin + new Vector2(MathF.Floor((m.Cols * cw - w) / 2 / cw) * cw, (m.Rows - 3) * ch);
            _cardLine.Size = _card.Size;
        }

        // The box's size at the cursor while dragging one over the world.
        if (!menu && tool.BoxSize is { } box && !m.Blocks(GetViewport().GetMousePosition()))
        {
            GlyphStatus.Banner = box.H > 1 ? $"{box.W}×{box.D}×{box.H} · {box.W * box.D * box.H} tiles" : $"{box.W}×{box.D} · {box.W * box.D} tiles";
            _bannerOwned = true;
        }
        else if (_bannerOwned)
        {
            GlyphStatus.Banner = null;
            _bannerOwned = false;
        }

        // Paused.
        bool paused = _ui.Main.Paused && _ui.Main.Client == null && !menu;
        _paused.Visible = paused;
        if (paused)
        {
            string text = "‖ Paused · Space to resume";
            _pausedText.Text = text;
            G.Retone(_pausedText, "Accent", bold: true);
            float w = (text.Length + 2) * cw;
            _paused.Size = new Vector2(w, ch);
            _paused.Position = m.Origin + new Vector2(MathF.Floor((m.Cols * cw - w) / 2 / cw) * cw, ch * 1.2f);
            _pausedText.Position = new Vector2(cw, 0);
            _pausedText.Size = new Vector2(w - 2 * cw, ch);
        }

        TickToasts(m, cw, ch, menu, paused);
    }

    // ---- Toasts -------------------------------------------------------------------------------------------

    private const double ToastSeconds = 6;
    private const int MaxToasts = 3;

    private void TickToasts(GlyphManager m, float cw, float ch, bool menu, bool paused)
    {
        var events = _ui.Sim.Events;
        if (events.Count > 0 && !events[^1].Equals(_lastEvent))
        {
            // New since the last look: walk back to the last one seen (the list drops its oldest past 400).
            var fresh = new List<SimEvent>();
            for (int i = events.Count - 1; i >= 0 && !events[i].Equals(_lastEvent) && fresh.Count < 8; i--) fresh.Add(events[i]);
            _lastEvent = events[^1];
            fresh.Reverse();
            foreach (var e in fresh)
                if (e.Faction < 0 || e.Faction == _ui.Me) AddToast(e.Text, ChronicleWindow.IsAlert(e.Text));
        }
        double now = Time.GetTicksMsec() / 1000.0;
        for (int i = _toasts.Count - 1; i >= 0; i--)
            if (_toasts[i].Until < now || _toasts.Count - i > MaxToasts)
            {
                _toasts[i].Panel.QueueFree();
                _toasts.RemoveAt(i);
            }
        float y = ch * (paused ? 2.4f : 1.2f);
        foreach (var (panel, label, _) in _toasts)
        {
            panel.Visible = !menu;
            string text = label.Text;
            int maxCols = Math.Max(20, m.Cols / 2);
            float w = (Math.Min(text.Length, maxCols) + 2) * cw;
            panel.Size = new Vector2(w, ch);
            panel.Position = m.Origin + new Vector2(MathF.Floor((m.Cols * cw - w) / 2 / cw) * cw, y);
            label.Position = new Vector2(cw, 0);
            label.Size = new Vector2(w - 2 * cw, ch);
            y += ch * 1.15f;
        }
    }

    private void AddToast(string text, bool alert)
    {
        var panel = new GlyphPanel { Pill = true };
        var label = new Label { Text = (alert ? "‼ " : "• ") + text, MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center, ClipText = true };
        G.Retone(label, alert ? "Bad" : "Hi", bold: alert);
        panel.AddChild(label);
        panel.MouseDefaultCursorShape = CursorShape.PointingHand;
        panel.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                _ui.OpenWindow("chronicle");
                panel.AcceptEvent();
            }
        };
        panel.MouseEntered += () => GlyphStatus.Tip = "Click: the Chronicle (L)";
        panel.MouseExited += () => GlyphStatus.Tip = null;
        AddChild(panel);
        _toasts.Add((panel, label, Time.GetTicksMsec() / 1000.0 + (alert ? ToastSeconds * 1.5 : ToastSeconds)));
    }

    // ---- The tool card ------------------------------------------------------------------------------------

    /// <summary>"▼ Dig   drag a box: click a corner, then the other · Shift: remove · RMB: put down".</summary>
    private sealed partial class CardLine : GlyphControl
    {
        private readonly Notices _n;
        private string _glyph = "", _tone = "accent", _name = "", _hint = "";
        public int Cells { get; private set; } = 10;

        public CardLine(Notices n) { _n = n; MouseFilter = MouseFilterEnum.Ignore; }
        public CardLine() : this(null!) { }

        public void Refresh(int maxCols)
        {
            var tool = _n._ui.Tool;
            string glyph, tone, name, hint;
            if (tool.SquadOrders)
            {
                (glyph, tone, name) = ("‼", "bad", "Squad orders");
                hint = "click the ground to march there, a nest to attack it · RMB or G: done";
            }
            else
            {
                var entry = OrdersRibbon.Entries.FirstOrDefault(e => e.Mode == tool.Mode || e.Mode == ToolMode.FarmGrain && tool.Mode is ToolMode.FarmHerbs or ToolMode.FarmGlowcaps or ToolMode.FarmFlax);
                glyph = entry?.Glyph ?? "#";
                tone = entry?.Tone ?? "warn";
                string full = tool.ModeName(tool.Mode);
                name = (full.StartsWith("Build: ") ? full[7..] : full).Split(" (")[0];
                bool click = tool.PartFor(tool.Mode) is not null || tool.Mode is ToolMode.Structure or ToolMode.Edge;
                hint = tool.BoxSize is { } b ? $"{b.W}×{b.D}×{b.H} · click to confirm · Shift/Ctrl: levels · RMB: abort"
                    : tool.Mode == ToolMode.WallLine ? "click an edge, drag along it, click · Shift: remove · RMB: put down"
                    : click ? "click to place · Shift+click: remove · R: turn · RMB: put down"
                    : tool.Mode is ToolMode.Cancel or ToolMode.Demolish ? "drag a box: click a corner, then the other · RMB: put down"
                    : "drag a box: click a corner, then the other · Shift: remove · RMB: put down";
                if (tool.PlacementProblem is { } why) hint = $"can't place: {why} · RMB: put down";
            }
            int cells = 2 + 2 + name.Length + 3 + hint.Length + 1;
            if (cells > maxCols)
            {
                hint = hint[..Math.Max(0, hint.Length - (cells - maxCols) - 1)] + "…";
                cells = maxCols;
            }
            if (glyph == _glyph && name == _name && hint == _hint) return;
            (_glyph, _tone, _name, _hint, Cells) = (glyph, tone, name, hint, cells);
            QueueRedraw();
        }

        public override void _Draw()
        {
            GlyphDraw.Chip(this, _glyph, new Vector2(1.5f * Cw, 0), Tok(_tone));
            GlyphDraw.Text(this, _name, new Vector2(3.8f * Cw, 0), _name.Length * Cw + 2, Tok("accent"), true);
            float x = (4 + _name.Length + 2) * Cw;
            GlyphDraw.Text(this, _hint, new Vector2(x, 0), Size.X - x, Tok("text"));
        }
    }
}
