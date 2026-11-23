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

    /// <summary>
    /// "▼ Dig  [Drag] a box  [Shift] remove  [RMB] done": the tool in hand (from <see cref="Tools"/>), then its controls as
    /// keycaps; while boxing, the box size; when the spot is no good, why not.
    /// </summary>
    private sealed partial class CardLine : GlyphControl
    {
        private readonly Notices _n;
        private string _glyph = "", _name = "", _extra = "", _drawn = "", _extraTone = "hi";
        private Color _color;
        private (string Key, string What)[] _keys = [];
        public int Cells { get; private set; } = 10;

        public CardLine(Notices n) { _n = n; MouseFilter = MouseFilterEnum.Ignore; }
        public CardLine() : this(null!) { }

        public void Refresh(int maxCols)
        {
            var tool = _n._ui.Tool;
            string glyph, name, extra = "", extraTone = "hi";
            Color color;
            (string Key, string What)[] keys;
            if (tool.SquadOrders)
            {
                (glyph, color, name) = ("‼", Tok("bad"), "Squad orders");
                keys = [("Click", "march there"), ("Click a nest", "attack it"), ("G", "done")];
            }
            else
            {
                var info = Tools.Current(tool);
                (glyph, color, name, keys) = (info.Glyph, info.Color, info.Name, info.How);
                if (tool.Removing) { name = "Remove: " + name; color = Tok("bad"); }
                if (tool.EdgePieces is { } pieces) extra = G.Plural(pieces, "piece");
                else if (tool.BoxSize is { } b) extra = b.H > 1 ? $"{b.W}×{b.D}×{b.H}" : $"{b.W}×{b.D}";
                if (tool.IsArea && tool.Selecting) keys = [("Click", "confirm"), ("RMB", "abort")];
                if (tool.PlacementProblem is { } why && !tool.Selecting)
                {
                    extra = "can't place: " + why;
                    extraTone = "bad";
                }
            }
            string key = $"{glyph}|{name}|{extra}|{extraTone}|{string.Join(",", keys)}|{maxCols}|{color}";
            if (key == _drawn) return;
            _drawn = key;
            // Leave hints off the end (whole ones) until it fits.
            int Width((string, string)[] k) => 1 + 2 + name.Length + 2 + (extra.Length > 0 ? extra.Length + 2 : 0) + KeyHints.CellsOf(k) + 2;
            while (keys.Length > 1 && Width(keys) > maxCols) keys = keys[..^1];
            (_glyph, _color, _name, _extra, _extraTone, _keys, Cells) = (glyph, color, name, extra, extraTone, keys, Math.Min(maxCols, Width(keys)));
            QueueRedraw();
        }

        public override void _Draw()
        {
            float x = 1.2f * Cw;
            GlyphDraw.Chip(this, _glyph, new Vector2(x, 0), _color);
            x += 2.4f * Cw;
            GlyphDraw.Text(this, _name, new Vector2(x, 0), _name.Length * Cw + 2, Tok("accent"), true);
            x += (_name.Length + 2) * Cw;
            if (_extra.Length > 0)
            {
                GlyphDraw.Text(this, _extra, new Vector2(x, 0), _extra.Length * Cw + 2, Tok(_extraTone), true);
                x += (_extra.Length + 2) * Cw;
            }
            foreach (var (k, w) in _keys)
            {
                if (x + (GlyphDraw.KeycapCells(k) + 1 + w.Length) * Cw > Size.X) break;
                x += GlyphDraw.Keycap(this, k, x) * Cw + Cw;
                GlyphDraw.Text(this, w, new Vector2(x, 0), w.Length * Cw + 2, Tok("dim"));
                x += (w.Length + 2) * Cw;
            }
        }
    }
}
