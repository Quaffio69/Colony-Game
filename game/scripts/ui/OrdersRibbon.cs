using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ToolMode = ColonyGame.DesignationTool.ToolMode;

namespace ColonyGame.UI;

/// <summary>
/// The orders ribbon: a small pill above the dock listing every order with its icon and hotkey. It shows while an order
/// tool is in hand (pressing any order's hotkey brings it up) or while pinned open by its own "Orders" pill; clicking
/// an entry picks it. Hotkeys follow Dwarf Fortress's designation letters where the camera (WASD, Q/E, Z/X) and the
/// window keys leave them free, shown DF-style: lowercase is the key alone, uppercase is Shift+key.
/// </summary>
public partial class OrdersRibbon : Control
{
    public sealed record Entry(ToolMode Mode, string Glyph, string Tone, string Name, Key Key, bool Shift, string Tip)
    {
        /// <summary>DF-style: "n" is N, "P" is Shift+P.</summary>
        public string KeyName => Key == Key.Delete ? "Del" : Shift ? Key.ToString().ToUpperInvariant() : Key.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// The orders and their keys. DF: d mine, u/j/i stairs, t chop, p gather, x remove; d, j and i are taken here (pan
    /// right, Work, Stocks), so mine is n, down stair k, up/down stair o, and the removal box is X (Shift+X, or Del).
    /// Stockpile (DF's p menu) is P, farm plot F (again: the next crop), dam wall B.
    /// </summary>
    public static readonly Entry[] Entries =
    [
        new(ToolMode.Dig, "▼", "ore", "Dig", Key.N, false, "Dig out a box of rock and soil (DF: mine)"),
        new(ToolMode.Stairs, "≡", "stair", "Stairs", Key.Y, false, "Stairs through a box of levels: down on top, up/down between, up at the bottom"),
        new(ToolMode.UpStair, "▲", "stair", "Up", Key.U, false, "Carve up stairs"),
        new(ToolMode.DownStair, "▼", "stair", "Down", Key.K, false, "Carve down stairs"),
        new(ToolMode.UpDownStair, "÷", "stair", "Up/down", Key.O, false, "Carve up/down stairs"),
        new(ToolMode.Chop, "♣", "pine", "Chop", Key.T, false, "Fell trees in an area (logs by tree size)"),
        new(ToolMode.Gather, "♦", "good", "Gather", Key.P, false, "Gather ripe plants in an area (berries, fiber, herbs)"),
        new(ToolMode.Stockpile, "≡", "item", "Stockpile", Key.P, true, "Zone floor where haulers store goods (racks: Build, Zones)"),
        new(ToolMode.FarmGrain, "τ", "farm", "Farm", Key.F, true, "Farm plot: press again for the next crop (grain, herbs, glowcaps)"),
        new(ToolMode.Wall, "#", "wall", "Dam wall", Key.B, true, "Full stone wall cells for dams and canals (stack to raise)"),
        new(ToolMode.Cancel, "×", "bad", "Cancel", Key.X, true, "Cancel designations and remove zone cells in a box (also Del)"),
    ];

    /// <summary>The order a key picks (null: not an order key).</summary>
    public static ToolMode? OrderFor(Key key, bool shift)
    {
        if (key == Key.Delete) return ToolMode.Cancel;
        foreach (var e in Entries)
            if (e.Key == key && e.Shift == shift) return e.Mode;
        return null;
    }

    /// <summary>Order tools (dig .. cancel): the ribbon shows while one is in hand.</summary>
    public static bool IsOrder(ToolMode m) => m is not ToolMode.None && !DesignationTool.IsBuild(m);

    private static bool Covers(Entry e, ToolMode m) =>
        e.Mode == m || e.Mode == ToolMode.FarmGrain && m is ToolMode.FarmHerbs or ToolMode.FarmGlowcaps;

    private readonly GameUi _ui;
    private readonly GlyphPanel _pillPanel = new() { Pill = true }, _ribbonPanel = new() { Pill = true };
    private readonly PillButton _pill;
    private readonly Row _row;
    /// <summary>Kept open by its pill even with no order in hand.</summary>
    public bool Pinned { get; set; }

    public OrdersRibbon(GameUi ui)
    {
        _ui = ui;
        MouseFilter = MouseFilterEnum.Ignore;
        _pill = new PillButton(this);
        _pillPanel.AddChild(_pill);
        _row = new Row(this);
        _ribbonPanel.AddChild(_row);
        AddChild(_pillPanel);
        AddChild(_ribbonPanel);
    }
    public OrdersRibbon() : this(null!) { }

    public override void _Ready()
    {
        if (GlyphManager.Instance is { } m)
        {
            m.Blockers.Add(_pillPanel);
            m.Blockers.Add(_ribbonPanel);
        }
    }

    public override void _ExitTree()
    {
        if (GlyphManager.Instance is { } m)
        {
            m.Blockers.Remove(_pillPanel);
            m.Blockers.Remove(_ribbonPanel);
        }
    }

    public bool Open => _ui != null && (Pinned || IsOrder(_ui.Tool.Mode));
    private ToolMode Current => _ui.Tool.Mode;

    /// <summary>The pill: open it pinned; open, it closes (and puts any order tool down).</summary>
    private void TogglePinned()
    {
        if (Open)
        {
            Pinned = false;
            if (IsOrder(Current)) _ui.Tool.CancelPlacement();
        }
        else Pinned = true;
    }

    private void Pick(Entry e) => _ui.Tool.PickOrder(e.Mode);

    public override void _Process(double delta)
    {
        if (_ui == null || GlyphManager.Instance is not { } m) return;
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        bool menu = m.MenuOpen;
        _pillPanel.Visible = !menu;
        var at = m.Origin + new Vector2(cw, (m.Rows - 2) * ch);
        _pill.Refresh();
        _pillPanel.Position = at;
        _pillPanel.Size = new Vector2(_pill.Cells * cw, ch);
        _pill.Size = _pillPanel.Size;

        _ribbonPanel.Visible = Open && !menu;
        if (!_ribbonPanel.Visible) return;
        int room = m.Cols - 3 - _pill.Cells;
        _row.Compact = _row.CellsFor(false) > room;
        _row.Refresh();
        _ribbonPanel.Position = at + new Vector2((_pill.Cells + 1) * cw, 0);
        _ribbonPanel.Size = new Vector2(_row.Cells * cw, ch);
        _row.Size = _ribbonPanel.Size;
    }

    /// <summary>"⚒ Orders": toggles the ribbon pinned open.</summary>
    private sealed partial class PillButton : GlyphControl
    {
        private readonly OrdersRibbon _r;
        private bool _hover, _open;
        public int Cells => 10;

        public PillButton(OrdersRibbon r)
        {
            _r = r;
            MouseDefaultCursorShape = CursorShape.PointingHand;
            MouseEntered += () => { _hover = true; GlyphStatus.Tip = GlyphStatus.Banner = "Orders: dig, stairs, chop, gather, zones, cancel · each has a hotkey · click to keep this open"; QueueRedraw(); };
            MouseExited += () => { _hover = false; GlyphStatus.Tip = GlyphStatus.Banner = null; QueueRedraw(); };
        }
        public PillButton() : this(null!) { }

        public void Refresh()
        {
            if (_r.Open == _open) return;
            _open = _r.Open;
            QueueRedraw();
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                AcceptEvent();
                _r.TogglePinned();
                QueueRedraw();
            }
        }

        public override void _Draw()
        {
            var r = new Rect2(0, Ch * 0.14f, Size.X, Ch * 0.72f);
            if (_r.Open) GlyphDraw.Round(this, r, GlyphTheme.Tok("accent", 0.2f), Ch * 0.36f);
            else if (_hover) GlyphDraw.Round(this, r, GlyphTheme.Tok("text", 0.08f), Ch * 0.36f);
            GlyphDraw.Glyph(this, "▼", new Rect2(Cw, 0, Cw, Ch), Tok(_r.Open ? "accent" : "ore"));
            GlyphDraw.Text(this, "Orders", new Vector2(3 * Cw, 0), 6 * Cw, _r.Open ? Tok("hi") : Tok("text"), _r.Open);
        }
    }

    /// <summary>The entries: icon chip, name, hotkey; the one in hand highlighted.</summary>
    private sealed partial class Row : GlyphControl
    {
        private readonly OrdersRibbon _r;
        private int _hover = -1;
        private string _drawn = "";
        public bool Compact;
        private readonly List<(int X, int Cells)> _spans = new();
        public int Cells { get; private set; }

        public Row(OrdersRibbon r)
        {
            _r = r;
            MouseDefaultCursorShape = CursorShape.PointingHand;
            MouseExited += () => { _hover = -1; GlyphStatus.Tip = GlyphStatus.Banner = null; QueueRedraw(); };
        }
        public Row() : this(null!) { }

        private static string NameOf(Entry e, ToolMode current) =>
            e.Mode == ToolMode.FarmGrain ? current switch { ToolMode.FarmHerbs => "Herbs", ToolMode.FarmGlowcaps => "Glowcaps", ToolMode.FarmGrain => "Grain", _ => e.Name } : e.Name;

        private static int EntryCells(Entry e, ToolMode current, bool compact) => compact ? 2 + e.KeyName.Length + 1 : 2 + NameOf(e, current).Length + 1 + e.KeyName.Length + 2;

        public int CellsFor(bool compact) => 1 + Entries.Sum(e => EntryCells(e, _r.Current, compact));

        public void Refresh()
        {
            var current = _r.Current;
            string key = $"{current}/{Compact}/{_hover}";
            if (key == _drawn) return;
            _drawn = key;
            _spans.Clear();
            int x = 1;
            foreach (var e in Entries)
            {
                int n = EntryCells(e, current, Compact);
                _spans.Add((x, n));
                x += n;
            }
            Cells = x;
            QueueRedraw();
        }

        private int At(float px)
        {
            int col = (int)(px / Cw);
            for (int i = 0; i < _spans.Count; i++)
                if (col >= _spans[i].X && col < _spans[i].X + _spans[i].Cells) return i;
            return -1;
        }

        public override void _GuiInput(InputEvent e)
        {
            switch (e)
            {
                case InputEventMouseMotion m:
                    int i = At(m.Position.X);
                    if (i != _hover)
                    {
                        _hover = i;
                        var en = i >= 0 ? Entries[i] : null;
                        GlyphStatus.Tip = GlyphStatus.Banner = en is null ? null : $"{NameOf(en, _r.Current)} ({(en.Shift ? "Shift+" : "")}{en.Key}): {en.Tip}";
                        QueueRedraw();
                    }
                    break;
                case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } b:
                    AcceptEvent();
                    if (At(b.Position.X) is >= 0 and var hit) _r.Pick(Entries[hit]);
                    break;
            }
        }

        public override void _Draw()
        {
            var current = _r.Current;
            for (int i = 0; i < Entries.Length && i < _spans.Count; i++)
            {
                var e = Entries[i];
                var (x0, n) = _spans[i];
                bool on = Covers(e, current);
                float x = x0 * Cw;
                var r = new Rect2(x - 0.4f * Cw, Ch * 0.14f, (n - (Compact ? 0.2f : 1.2f)) * Cw, Ch * 0.72f);
                if (on) GlyphDraw.Round(this, r, GlyphTheme.Tok("accent", 0.2f), Ch * 0.36f);
                else if (i == _hover) GlyphDraw.Round(this, r, GlyphTheme.Tok("text", 0.08f), Ch * 0.36f);
                GlyphDraw.Chip(this, e.Glyph, new Vector2(x, 0), Tok(e.Tone));
                x += 2 * Cw;
                if (!Compact)
                {
                    string name = NameOf(e, current);
                    GlyphDraw.Text(this, name, new Vector2(x, 0), name.Length * Cw + 2, on ? Tok("hi") : Tok("text"), on);
                    x += (name.Length + 1) * Cw;
                }
                GlyphDraw.Text(this, e.KeyName, new Vector2(x, 0), e.KeyName.Length * Cw + 2, on ? Tok("accent") : Tok("faint"), true);
            }
        }
    }
}
