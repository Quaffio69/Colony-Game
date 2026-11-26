using System;
using System.Collections.Generic;
using Godot;

namespace ColonyGame.UI;

/// <summary>
/// A Glyph surface: a soft shadow (or the glow frame) drawn here, and a glass rect (glass.gdshader) that blurs what is
/// behind, lays the panel colour over it, masks it to a rounded rect and draws the frame, top highlight and title band.
/// Children draw on top. Used by windows, the HUD, the dock and the status pill.
/// </summary>
public partial class GlyphPanel : Control
{
    private static Shader? _shader;
    protected readonly ColorRect Glass;
    private readonly ShaderMaterial _material;
    private bool _active;

    /// <summary>A pill (fully rounded ends) rather than the user's corner radius.</summary>
    public bool Pill { get; set; }
    /// <summary>Always opaque, never frosted (over a solid backdrop there is nothing to see through).</summary>
    public bool Opaque { get; set; }
    /// <summary>Height of the title band in pixels (0: none).</summary>
    public float TitleHeight { get; set; }

    public bool Active
    {
        get => _active;
        set { if (_active != value) { _active = value; Refresh(); } }
    }

    public GlyphPanel()
    {
        MouseFilter = MouseFilterEnum.Stop;
        _shader ??= GD.Load<Shader>("res://shaders/glass.gdshader");
        _material = new ShaderMaterial { Shader = _shader };
        Glass = new ColorRect { MouseFilter = MouseFilterEnum.Ignore, Material = _material, Color = Colors.White };
        Glass.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(Glass, false, InternalMode.Front);
    }

    public float Radius => Pill ? Size.Y / 2 : GlyphTheme.Options.Radius;

    public override void _Notification(int what)
    {
        if (what is (int)NotificationResized or (int)NotificationThemeChanged) Refresh();
    }

    public override void _GuiInput(InputEvent e)
    {
        // Panels swallow the mouse so nothing beneath (the world) reacts; wheel included.
        if (e is InputEventMouseButton) AcceptEvent();
    }

    protected void Refresh()
    {
        var o = GlyphTheme.Options;
        bool light = GlyphTheme.Light;
        float title = TitleHeight;
        _material.SetShaderParameter("size", Size);
        _material.SetShaderParameter("radius", Radius);
        _material.SetShaderParameter("panel", GlyphTheme.Tok("panel") with { A = light ? 0.82f : 0.78f });
        _material.SetShaderParameter("glass", o.Glass && !Opaque ? 1f : 0f);
        Color border = o.Frame switch
        {
            "none" => Colors.Transparent,
            "glow" when _active => GlyphTheme.Tok("accent", 0.8f),
            _ when _active => GlyphTheme.Tok("accent", 0.55f),
            _ => GlyphTheme.Tok("text", light ? 0.16f : 0.11f),
        };
        _material.SetShaderParameter("border", border);
        _material.SetShaderParameter("border2", o.Frame == "double" ? (_active ? GlyphTheme.Tok("accent", 0.3f) : GlyphTheme.Tok("text", 0.07f)) : Colors.Transparent);
        _material.SetShaderParameter("highlight", light || Pill ? Colors.Transparent : new Color(1, 1, 1, 0.06f));
        _material.SetShaderParameter("title_h", title);
        _material.SetShaderParameter("band", GlyphTheme.Tok("text", 0.055f));
        _material.SetShaderParameter("divider", GlyphTheme.Tok("text", light ? 0.10f : 0.07f));
        QueueRedraw();
    }

    public override void _Draw()
    {
        var o = GlyphTheme.Options;
        bool glow = o.Frame == "glow" && _active;
        if (!o.Shadow && !glow) return;
        bool light = GlyphTheme.Light;
        int r = (int)Radius;
        var sb = new StyleBoxFlat
        {
            BgColor = Colors.Transparent, DrawCenter = false, AntiAliasing = true,
            CornerRadiusTopLeft = r, CornerRadiusTopRight = r, CornerRadiusBottomLeft = r, CornerRadiusBottomRight = r,
        };
        if (glow)
        {
            sb.ShadowColor = GlyphTheme.Tok("accent", 0.45f);
            sb.ShadowSize = 16;
        }
        else
        {
            // A tight contact shadow: the old one (up to 34 px, offset 12 down, 55% black) smeared a dark band under every
            // window over the bright pixel-art world.
            sb.ShadowColor = light ? new Color(60 / 255f, 40 / 255f, 20 / 255f, 0.16f) : new Color(0, 0, 0, _active ? 0.32f : 0.24f);
            sb.ShadowSize = Pill ? 4 : _active ? 10 : 6;
            sb.ShadowOffset = new Vector2(0, Pill ? 1 : _active ? 3 : 2);
        }
        DrawStyleBox(sb, new Rect2(Vector2.Zero, Size));
    }
}

/// <summary>
/// A Glyph window (GLYPH_UI.md section 2): title row (chip, bold title, hotkey, − □ ×), content inset 2 columns and
/// 1 row, optional fixed header and footer around a scrolling body with the scrollbar in the last column, a resize grip.
/// Moves and resizes in whole cells; double-click the title to maximise. Subclasses fill <see cref="Header"/>,
/// <see cref="Content"/> and <see cref="Footer"/> in <see cref="Build"/> and update them in <see cref="Tick"/>.
/// </summary>
public partial class GlyphWindow : GlyphPanel
{
    public string Id { get; }
    public string Title { get; protected set; }
    public string Glyph { get; }
    public string Tone { get; }
    public string Hotkey { get; }
    public int MinCols { get; init; } = 30;
    public int MinRows { get; init; } = 7;
    /// <summary>Keeps the view on the newest (last) content unless the user scrolls up (logs).</summary>
    public bool StickToBottom { get; init; }
    public WindowState State { get; set; } = new();
    public bool Maximized { get; set; }
    public WindowState Default { get; init; } = new() { X = 4, Y = 3, W = 50, H = 16 };

    public VBoxContainer Header { get; } = new();
    public VBoxContainer Content { get; } = new();
    public VBoxContainer Footer { get; } = new();
    public ScrollContainer Scroll { get; } = new();

    public event Action<GlyphWindow>? Closed;
    internal GlyphManager Manager = null!;

    private readonly MarginContainer _body = new();
    private readonly MarginContainer _scrollPad = new();
    private readonly MarginContainer _headerPad = new(), _footerPad = new();
    private readonly TitleBar _titleBar;
    private readonly ResizeHandle _right, _bottom, _corner;
    private bool _built;
    private bool _atEnd = true;

    public GlyphWindow(string id, string title, string glyph, string tone, string hotkey)
    {
        Id = id;
        Title = title;
        Glyph = glyph;
        Tone = tone;
        Hotkey = hotkey;
        Visible = false;

        _body.MouseFilter = MouseFilterEnum.Ignore;
        _body.ClipContents = true; // nothing draws outside the panel, whatever its content asks for
        AddChild(_body);
        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 0);
        _body.AddChild(column);
        _headerPad.AddChild(Header);
        column.AddChild(_headerPad);
        Scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        Scroll.ClipContents = true;
        _headerPad.ClipContents = _footerPad.ClipContents = true;
        Scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        Scroll.MouseFilter = MouseFilterEnum.Pass;
        column.AddChild(Scroll);
        _scrollPad.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _scrollPad.MouseFilter = MouseFilterEnum.Ignore;
        Scroll.AddChild(_scrollPad);
        Content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Content.MouseFilter = MouseFilterEnum.Ignore;
        _scrollPad.AddChild(Content);
        _footerPad.AddChild(Footer);
        column.AddChild(_footerPad);
        foreach (var box in new[] { Header, Content, Footer }) box.AddThemeConstantOverride("separation", 0);
        Scroll.GetVScrollBar().Changed += KeepAtEnd;
        Scroll.GetVScrollBar().ValueChanged += _ => _atEnd = Scroll.ScrollVertical >= Scroll.GetVScrollBar().MaxValue - Scroll.GetVScrollBar().Page - 2;

        _titleBar = new TitleBar(this);
        AddChild(_titleBar);
        _right = new ResizeHandle(this, true, false);
        _bottom = new ResizeHandle(this, false, true);
        _corner = new ResizeHandle(this, true, true);
        AddChild(_right);
        AddChild(_bottom);
        AddChild(_corner);
    }

    private void KeepAtEnd()
    {
        if (StickToBottom && _atEnd) Scroll.ScrollVertical = (int)Scroll.GetVScrollBar().MaxValue;
    }

    /// <summary>Fills the header, content and footer (on first open and when the theme changes).</summary>
    protected virtual void Build() { }
    /// <summary>Updates live values a few times a second while the window is shown.</summary>
    public virtual void Tick() { }

    /// <summary>
    /// True while the mouse is on a button inside <paramref name="area"/> (or holds one down there): a window that rebuilds
    /// those rows every tick skips the rebuild then, or the button under the pointer is freed and remade (its hover
    /// flickers, and a click whose press and release straddle a rebuild is lost).
    /// </summary>
    protected bool PointerOnButtonIn(Node area)
    {
        if (!IsInsideTree()) return false;
        bool button = false;
        for (Node? n = GetViewport().GuiGetHoveredControl(); n != null && n != this; n = n.GetParent())
        {
            button |= n is BaseButton;
            if (n == area) return button;
        }
        return false;
    }

    public void Rebuild()
    {
        G.Clear(Header);
        G.Clear(Content);
        G.Clear(Footer);
        Build();
        _built = true;
        Tick();
    }

    public bool IsShown => State.Open && !State.Min;

    /// <summary>Cells wide the window must be for its header, content and footer to fit (chrome included).</summary>
    internal int NeededCols()
    {
        if (!_built) return 0;
        float inner = Math.Max(Header.GetCombinedMinimumSize().X, Math.Max(Content.GetCombinedMinimumSize().X, Footer.GetCombinedMinimumSize().X));
        return (int)MathF.Ceiling(inner / GlyphTheme.Cw) + 4; // 2 cells of margin left, scrollbar and pad right
    }

    internal void EnsureBuilt()
    {
        if (!_built) Rebuild();
    }

    public void Close()
    {
        State.Open = false;
        Maximized = false;
        Manager.Changed();
        Closed?.Invoke(this);
    }

    /// <summary>Places the window from its cell rect (or the maximise area) and lays out the chrome.</summary>
    internal void Place(Rect2I cells, Vector2 origin)
    {
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        Position = origin + new Vector2(cells.Position.X * cw, cells.Position.Y * ch);
        Size = new Vector2(cells.Size.X * cw, cells.Size.Y * ch);
        TitleHeight = ch;
        float bar = Math.Max(3, cw * 0.28f);
        _body.AddThemeConstantOverride("margin_left", (int)(2 * cw));
        _body.AddThemeConstantOverride("margin_right", (int)(cw * 0.5f - bar / 2));
        _body.AddThemeConstantOverride("margin_top", (int)ch);
        _body.AddThemeConstantOverride("margin_bottom", (int)ch);
        _body.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        int pad = (int)(2 * cw - (cw * 0.5f - bar / 2));
        _scrollPad.AddThemeConstantOverride("margin_right", (int)(pad - bar));
        _headerPad.AddThemeConstantOverride("margin_right", pad);
        _footerPad.AddThemeConstantOverride("margin_right", pad);
        _titleBar.Position = Vector2.Zero;
        _titleBar.Size = new Vector2(Size.X, ch);
        _right.Visible = _bottom.Visible = _corner.Visible = !Maximized;
        _right.Position = new Vector2(Size.X - cw * 0.5f, ch);
        _right.Size = new Vector2(cw * 0.5f, Size.Y - 2 * ch);
        _bottom.Position = new Vector2(cw, Size.Y - ch * 0.5f);
        _bottom.Size = new Vector2(Size.X - 2 * cw, ch * 0.5f);
        _corner.Position = new Vector2(Size.X - cw, Size.Y - ch);
        _corner.Size = new Vector2(cw, ch);
        Refresh();
        _titleBar.QueueRedraw();
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton) AcceptEvent();
    }

    /// <summary>Title row: chip, title (hi when active), hotkey (faint), − □ × with hover circles; drag to move.</summary>
    private sealed partial class TitleBar : Control
    {
        private readonly GlyphWindow _w;
        private int _hover = -1, _pressed = -1;
        private Vector2? _dragFrom;
        private Vector2I _dragStart;

        public TitleBar(GlyphWindow w)
        {
            _w = w;
            MouseFilter = MouseFilterEnum.Stop;
            MouseExited += () => { _hover = -1; QueueRedraw(); GlyphStatus.Tip = null; };
        }
        public TitleBar() : this(null!) { }

        private float Cw => GlyphTheme.Cw;
        private float ControlX(int i) => Size.X - Cw - (3 - i) * 2 * Cw;
        private int ControlAt(Vector2 p)
        {
            for (int i = 0; i < 3; i++)
                if (p.X >= ControlX(i) && p.X < ControlX(i) + 2 * Cw) return i;
            return -1;
        }

        public override void _GuiInput(InputEvent e)
        {
            switch (e)
            {
                case InputEventMouseMotion m:
                    int h = ControlAt(m.Position);
                    if (h != _hover) { _hover = h; QueueRedraw(); }
                    GlyphStatus.Tip = h switch
                    {
                        0 => "Minimise to the dock",
                        1 => _w.Maximized ? "Restore" : "Maximise",
                        2 => "Close (Esc)",
                        _ => $"{_w.Title} · drag to move · double-click to maximise",
                    };
                    if (_dragFrom is { } from && !_w.Maximized)
                    {
                        var d = (GlobalPosition + m.Position) - from;
                        _w.State.X = _dragStart.X + (int)MathF.Round(d.X / GlyphTheme.Cw);
                        _w.State.Y = _dragStart.Y + (int)MathF.Round(d.Y / GlyphTheme.Ch);
                        _w.Manager.Layout(_w);
                    }
                    AcceptEvent();
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                    if (b.Pressed)
                    {
                        _pressed = ControlAt(b.Position);
                        if (_pressed < 0 && b.DoubleClick)
                        {
                            _w.Maximized = !_w.Maximized;
                            _w.Manager.Layout(_w);
                        }
                        else if (_pressed < 0)
                        {
                            _dragFrom = GlobalPosition + b.Position;
                            _dragStart = new Vector2I(_w.State.X, _w.State.Y);
                        }
                    }
                    else
                    {
                        if (_pressed >= 0 && ControlAt(b.Position) == _pressed)
                        {
                            switch (_pressed)
                            {
                                case 0: _w.Manager.Minimize(_w); break;
                                case 1: _w.Maximized = !_w.Maximized; _w.Manager.Layout(_w); break;
                                case 2: _w.Close(); break;
                            }
                        }
                        if (_dragFrom != null) _w.Manager.Changed();
                        _pressed = -1;
                        _dragFrom = null;
                    }
                    AcceptEvent();
                    break;
                case InputEventMouseButton:
                    AcceptEvent();
                    break;
            }
        }

        public override void _Draw()
        {
            float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
            bool active = _w.Active;
            GlyphDraw.Chip(this, _w.Glyph, new Vector2(cw, 0), GlyphTheme.Tok(_w.Tone));
            float max = ControlX(0) - 4 * cw;
            GlyphDraw.Text(this, _w.Title, new Vector2(3 * cw, 0), max, GlyphTheme.Tok(active ? "hi" : "dim"), bold: true);
            float tw = GlyphDraw.TextWidth(_w.Title, true);
            if (tw + 3 * cw < max && _w.Hotkey.Length > 0)
                GlyphDraw.Text(this, _w.Hotkey, new Vector2(4 * cw + tw, 0), max - tw, GlyphTheme.Tok("faint"));
            for (int i = 0; i < 3; i++)
            {
                var center = new Vector2(ControlX(i) + cw, ch / 2);
                bool hot = i == _hover;
                if (hot) DrawCircle(center, ch * 0.36f, i == 2 ? GlyphTheme.Tok("bad") : GlyphTheme.Tok("text", 0.14f), true, -1, true);
                var c = hot && i == 2 ? Colors.White : GlyphTheme.Tok(active ? "dim" : "faint");
                float s = MathF.Round(ch * 0.16f);
                switch (i)
                {
                    case 0: DrawLine(center + new Vector2(-s, 0.5f), center + new Vector2(s, 0.5f), c, 1.5f); break;
                    case 1:
                        if (_w.Maximized) GlyphDraw.Glyph(this, "◄", new Rect2(center - new Vector2(cw / 2, ch / 2), new Vector2(cw, ch)), c);
                        else DrawRect(new Rect2(center - new Vector2(s, s), new Vector2(2 * s, 2 * s)), c, false, 1.5f);
                        break;
                    default:
                        DrawLine(center + new Vector2(-s, -s), center + new Vector2(s, s), c, 1.5f, true);
                        DrawLine(center + new Vector2(-s, s), center + new Vector2(s, -s), c, 1.5f, true);
                        break;
                }
            }
        }
    }

    /// <summary>The right edge, bottom edge and corner grip: drag to resize (whole cells).</summary>
    private sealed partial class ResizeHandle : Control
    {
        private readonly GlyphWindow _w;
        private readonly bool _x, _y;
        private Vector2? _from;
        private Vector2I _start;
        private bool _hover;

        public ResizeHandle(GlyphWindow w, bool x, bool y)
        {
            _w = w; _x = x; _y = y;
            MouseFilter = MouseFilterEnum.Stop;
            MouseDefaultCursorShape = x && y ? CursorShape.Fdiagsize : x ? CursorShape.Hsize : CursorShape.Vsize;
            MouseEntered += () => { _hover = true; QueueRedraw(); GlyphStatus.Tip = "Drag to resize"; };
            MouseExited += () => { _hover = false; QueueRedraw(); if (GlyphStatus.Tip == "Drag to resize") GlyphStatus.Tip = null; };
        }
        public ResizeHandle() : this(null!, false, false) { }

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
            {
                if (b.Pressed)
                {
                    _from = GlobalPosition + b.Position;
                    _start = new Vector2I(_w.State.W, _w.State.H);
                }
                else
                {
                    _from = null;
                    _w.Manager.Changed();
                }
                AcceptEvent();
            }
            else if (e is InputEventMouseMotion mm && _from is { } from)
            {
                var d = (GlobalPosition + mm.Position) - from;
                if (_x) _w.State.W = _start.X + (int)MathF.Round(d.X / GlyphTheme.Cw);
                if (_y) _w.State.H = _start.Y + (int)MathF.Round(d.Y / GlyphTheme.Ch);
                _w.Manager.Layout(_w);
                AcceptEvent();
            }
            else if (e is InputEventMouseButton) AcceptEvent();
        }

        public override void _Draw()
        {
            if (!(_x && _y)) return;
            var c = _hover || _from != null ? GlyphTheme.Tok("accent") : GlyphTheme.Tok("text", 0.25f);
            float w = Size.X, h = Size.Y, s = MathF.Min(w, h);
            var br = new Vector2(w - s * 0.2f, h - s * 0.25f);
            DrawLine(br + new Vector2(-s * 0.55f, 0), br + new Vector2(0, -s * 0.55f), c, 1.2f, true);
            DrawLine(br + new Vector2(-s * 0.25f, 0), br + new Vector2(0, -s * 0.25f), c, 1.2f, true);
        }
    }
}
