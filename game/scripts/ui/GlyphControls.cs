using System;
using System.Collections.Generic;
using Godot;

namespace ColonyGame.UI;

/// <summary>Shared hover hints and messages for the status pill (bottom right).</summary>
public static class GlyphStatus
{
    /// <summary>Hint for the control under the mouse (set on hover, cleared on leave).</summary>
    public static string? Tip;
    private static Control? _tipOwner;
    /// <summary>A message shown in warn colour for a few seconds.</summary>
    public static string? Message { get; private set; }
    public static double MessageUntil { get; private set; }

    public static void Say(string text, double seconds = 4)
    {
        Message = text;
        MessageUntil = Time.GetTicksMsec() / 1000.0 + seconds;
    }

    /// <summary>A banner shown at the mouse cursor straight away (HUD pills: what a thing is, without opening it).</summary>
    public static string? Banner;
    private static Control? _bannerOwner;

    /// <summary>Shows a hint in a banner at the cursor (and the status pill) while the mouse is over the control.</summary>
    public static T WithBanner<T>(this T control, Func<string> tip) where T : Control
    {
        control.MouseEntered += () => { Banner = Tip = tip(); _bannerOwner = control; };
        control.MouseExited += () => { if (_bannerOwner == control) { Banner = Tip = null; _bannerOwner = null; } };
        control.TreeExiting += () => { if (_bannerOwner == control) { Banner = Tip = null; _bannerOwner = null; } };
        return control;
    }

    /// <summary>Shows a hint in the status pill while the mouse is over the control.</summary>
    public static T WithTip<T>(this T control, string? tip) where T : Control
    {
        if (string.IsNullOrEmpty(tip)) return control;
        control.MouseEntered += () => { Tip = tip; _tipOwner = control; };
        control.MouseExited += () => { if (_tipOwner == control) { Tip = null; _tipOwner = null; } };
        control.TreeExiting += () => { if (_tipOwner == control) { Tip = null; _tipOwner = null; } };
        return control;
    }
}

/// <summary>Base for self-drawn Glyph controls: redraws when the theme changes; sizes in cells.</summary>
public abstract partial class GlyphControl : Control
{
    protected float Cw => GlyphTheme.Cw;
    protected float Ch => GlyphTheme.Ch;

    public override void _Notification(int what)
    {
        if (what == NotificationThemeChanged)
        {
            UpdateMinimumSize();
            QueueRedraw();
        }
    }

    protected Color Tok(string name) => GlyphTheme.Tok(name);
}

/// <summary>One cell: a CP437 glyph on a rounded chip in its role colour (a token name or a colour).</summary>
public partial class GlyphChip : GlyphControl
{
    private string _glyph;
    private string? _tone;
    private Color? _color;

    public GlyphChip(string glyph, string tone) { _glyph = glyph; _tone = tone; MouseFilter = MouseFilterEnum.Ignore; }
    public GlyphChip(string glyph, Color color) { _glyph = glyph; _color = color; MouseFilter = MouseFilterEnum.Ignore; }
    public GlyphChip() : this("•", "dim") { }

    public void Set(string glyph, Color color) { _glyph = glyph; _color = color; _tone = null; QueueRedraw(); }
    public override Vector2 _GetMinimumSize() => new(Cw, Ch);
    public override void _Draw() => GlyphDraw.Chip(this, _glyph, new Vector2(0, (Size.Y - Ch) / 2), _color ?? Tok(_tone!));
}

/// <summary>The thin rounded bar: track text @9%, fill by tone (good/warn/bad automatically), optional value in faint.</summary>
public partial class GlyphBar : GlyphControl
{
    private float _value;
    private readonly int _cols;
    private string? _tone;
    private string? _label;

    public GlyphBar(float value, int cols, string? tone = null, string? label = null)
    {
        _value = value; _cols = cols; _tone = tone; _label = label;
        MouseFilter = MouseFilterEnum.Ignore;
        if (cols <= 0) SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }
    public GlyphBar() : this(0, 8) { }

    public void Set(float value, string? label = null, string? tone = null)
    {
        if (value == _value && label == _label && tone == _tone) return;
        _value = value; _label = label; _tone = tone;
        QueueRedraw();
    }

    public override Vector2 _GetMinimumSize() => new(Math.Max(_cols, 2) * Cw + (_label?.Length ?? 0) * Cw, Ch);

    public override void _Draw()
    {
        float labelW = _label == null ? 0 : (_label.Length + 1) * Cw;
        GlyphDraw.Bar(this, new Rect2(0, 0, Size.X - labelW, Size.Y), _value, _tone == null ? null : Tok(_tone));
        if (_label != null) GlyphDraw.Text(this, _label, new Vector2(Size.X - labelW, (Size.Y - Ch) / 2), labelW, Tok("faint"), align: HorizontalAlignment.Right);
    }
}

/// <summary>A label with a toggle switch on the right; clicking anywhere on the row toggles it.</summary>
public partial class GlyphSwitch : GlyphControl
{
    private readonly string _text;
    private bool _on, _hover;
    private readonly Action<bool> _toggled;

    public GlyphSwitch(string text, bool on, Action<bool> toggled)
    {
        _text = text; _on = on; _toggled = toggled;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        MouseEntered += () => { _hover = true; QueueRedraw(); };
        MouseExited += () => { _hover = false; QueueRedraw(); };
    }
    public GlyphSwitch() : this("", false, _ => { }) { }

    public bool On
    {
        get => _on;
        set { _on = value; QueueRedraw(); }
    }

    public override Vector2 _GetMinimumSize() => new((_text.Length + 5) * Cw, Ch);

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            _on = !_on;
            QueueRedraw();
            AcceptEvent();
            _toggled(_on);
        }
    }

    public override void _Draw()
    {
        if (_hover) GlyphDraw.Round(this, new Rect2(-Cw * 0.5f, Ch * 0.06f, Size.X + Cw, Ch * 0.88f), GlyphTheme.Tok("text", 0.06f), 4);
        GlyphDraw.Text(this, _text, Vector2.Zero, Size.X, Tok("text"));
        float h = MathF.Round(Ch * 0.5f), w = MathF.Round(h * 1.8f);
        var track = new Rect2(Size.X - w - Cw * 0.25f, (Size.Y - h) / 2, w, h);
        GlyphDraw.Round(this, track, _on ? Tok("accent") : GlyphTheme.Tok("text", 0.16f), h / 2);
        float k = h - 4;
        var knob = new Rect2(_on ? track.End.X - k - 2 : track.Position.X + 2, track.Position.Y + 2, k, k);
        GlyphDraw.Round(this, knob, _on ? Tok("accentInk") : GlyphTheme.Light ? Colors.White : Tok("dim"), k / 2);
    }
}

/// <summary>
/// Pills on a track (tabs, option groups, the speed control): the active segment accent @20% with bold hi text
/// (or solid accent with ink when <see cref="Solid"/>), others dim with a hover fill.
/// </summary>
public partial class GlyphSegmented : GlyphControl
{
    private string[] _labels;
    private int _index, _hover = -1;
    private readonly Action<int> _picked;
    public bool Solid { get; init; }
    public string[]? Tips { get; init; }
    /// <summary>Show the segment hints as a banner at the cursor too (HUD).</summary>
    public bool BannerTips { get; init; }

    public GlyphSegmented(string[] labels, int index, Action<int> picked)
    {
        _labels = labels; _index = index; _picked = picked;
        MouseExited += () =>
        {
            _hover = -1;
            if (Tips != null) GlyphStatus.Tip = null;
            if (BannerTips) GlyphStatus.Banner = null;
            QueueRedraw();
        };
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }
    public GlyphSegmented() : this([], 0, _ => { }) { }

    public int Index
    {
        get => _index;
        set { if (_index != value) { _index = value; QueueRedraw(); } }
    }

    private float SegWidth(int i) => (_labels[i].Length + 2) * Cw;
    public override Vector2 _GetMinimumSize()
    {
        float w = 0;
        for (int i = 0; i < _labels.Length; i++) w += SegWidth(i);
        return new Vector2(w, Ch);
    }

    private int At(float x)
    {
        float s = 0;
        for (int i = 0; i < _labels.Length; i++)
        {
            s += SegWidth(i);
            if (x < s) return i;
        }
        return -1;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion m)
        {
            int h = At(m.Position.X);
            if (h != _hover)
            {
                _hover = h;
                QueueRedraw();
                GlyphStatus.Tip = h >= 0 && Tips != null && h < Tips.Length ? Tips[h] : null;
                if (BannerTips) GlyphStatus.Banner = GlyphStatus.Tip;
            }
        }
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } b && At(b.Position.X) is >= 0 and var i)
        {
            _index = i;
            QueueRedraw();
            AcceptEvent();
            _picked(i);
        }
    }

    public override void _Draw()
    {
        float y = (Size.Y - Ch) / 2;
        var full = _GetMinimumSize();
        GlyphDraw.Round(this, new Rect2(0, y + Ch * 0.1f, full.X, Ch * 0.8f), GlyphTheme.Tok("text", 0.045f, 0.06f), Ch * 0.4f);
        float x = 0;
        for (int i = 0; i < _labels.Length; i++)
        {
            float w = SegWidth(i);
            var r = new Rect2(x, y + Ch * 0.12f, w, Ch * 0.76f);
            bool on = i == _index;
            if (on) GlyphDraw.Round(this, r, Solid ? Tok("accent") : GlyphTheme.Tok("accent", 0.2f, 0.16f), Ch * 0.38f);
            else if (i == _hover) GlyphDraw.Round(this, r, GlyphTheme.Tok("text", 0.07f), Ch * 0.38f);
            var color = on ? (Solid ? Tok("accentInk") : Tok("hi")) : Tok("dim");
            DrawLabel(_labels[i], new Vector2(x + Cw, y), color, on && !Solid);
            x += w;
        }
    }

    /// <summary>Labels may hold CP437 glyphs (‖ ►): drawn cell by cell so they come from the bitmaps.</summary>
    private void DrawLabel(string text, Vector2 at, Color color, bool bold)
    {
        bool glyphs = false;
        foreach (char c in text) if (GlyphDraw.HasBitmap(c.ToString())) glyphs = true;
        if (!glyphs)
        {
            GlyphDraw.Text(this, text, at, text.Length * Cw + 1, color, bold);
            return;
        }
        for (int i = 0; i < text.Length; i++)
            GlyphDraw.Glyph(this, text[i].ToString(), new Rect2(at.X + i * Cw, at.Y, Cw, Ch), color, bold);
    }
}

/// <summary>Section header: UPPERCASE dim bold label and a 1 px rule to the right edge.</summary>
public partial class GlyphSection : GlyphControl
{
    private readonly string _title;
    public GlyphSection(string title) { _title = title.ToUpperInvariant(); SizeFlagsHorizontal = SizeFlags.ExpandFill; MouseFilter = MouseFilterEnum.Ignore; }
    public GlyphSection() : this("") { }
    public override Vector2 _GetMinimumSize() => new((_title.Length + 2) * Cw, Ch);

    public override void _Draw()
    {
        GlyphDraw.Text(this, _title, Vector2.Zero, Size.X, Tok("dim"), bold: true);
        float x = GlyphDraw.TextWidth(_title, true) + Cw;
        if (x < Size.X) DrawLine(new Vector2(x, MathF.Round(Size.Y / 2) + 0.5f), new Vector2(Size.X, MathF.Round(Size.Y / 2) + 0.5f), GlyphTheme.Tok("text", 0.1f), 1);
    }
}

/// <summary>A │ divider three cells wide, drawn as a 1 px line (box-drawing never comes from the font).</summary>
public partial class VRule : GlyphControl
{
    public VRule() { MouseFilter = MouseFilterEnum.Ignore; }
    public override Vector2 _GetMinimumSize() => new(3 * Cw, Ch);
    public override void _Draw()
    {
        float x = MathF.Floor(Size.X / 2) + 0.5f;
        DrawLine(new Vector2(x, 0), new Vector2(x, Size.Y), Tok("faint"), 1);
    }
}

/// <summary>Key/value row: chip, name, faint · leaders, value right-aligned in bold hi.</summary>
public partial class GlyphKV : GlyphControl
{
    private string? _glyph;
    private Color _glyphColor;
    private string _name, _value;
    private string _valueTone;

    public GlyphKV(string? glyph, Color glyphColor, string name, string value, string valueTone = "hi")
    {
        _glyph = glyph; _glyphColor = glyphColor; _name = name; _value = value; _valueTone = valueTone;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Pass;
    }
    public GlyphKV() : this(null, Colors.White, "", "") { }

    public void Set(string name, string value, string valueTone = "hi")
    {
        if (name == _name && value == _value && valueTone == _valueTone) return;
        _name = name; _value = value; _valueTone = valueTone;
        UpdateMinimumSize();
        QueueRedraw();
    }

    // Long names give way (truncated with …) rather than widening the window.
    public override Vector2 _GetMinimumSize() => new(((_glyph != null ? 2 : 0) + Math.Min(_name.Length, 12) + _value.Length + 2) * Cw, Ch);

    public override void _Draw()
    {
        float x = 0;
        if (_glyph != null)
        {
            GlyphDraw.Chip(this, _glyph, Vector2.Zero, _glyphColor);
            x = 2 * Cw;
        }
        float valueW = GlyphDraw.TextWidth(_value, true);
        float valueX = Size.X - valueW;
        int room = Math.Max(1, (int)((valueX - x - Cw) / Cw));
        string name = _name.Length > room ? _name[..Math.Max(0, room - 1)] + "…" : _name;
        GlyphDraw.Text(this, name, new Vector2(x, 0), Size.X - x, Tok("text"));
        float nameEnd = x + GlyphDraw.TextWidth(name) + Cw;
        int dots = (int)((valueX - Cw - nameEnd) / Cw);
        if (dots > 0)
            GlyphDraw.Text(this, new string('·', dots), new Vector2(valueX - Cw - dots * Cw, 0), dots * Cw + 2, Tok("faint"));
        GlyphDraw.Text(this, _value, new Vector2(valueX, 0), valueW + 2, Tok(_valueTone), bold: true);
    }
}

/// <summary>Eighth-block sparkline (62% of the row), good when rising and bad when falling, optional signed delta.</summary>
public partial class GlyphSparkline : GlyphControl
{
    private float[] _values;
    private readonly int _cols;

    public GlyphSparkline(float[] values, int cols) { _values = values; _cols = cols; MouseFilter = MouseFilterEnum.Ignore; }
    public GlyphSparkline() : this([], 8) { }

    public void Set(float[] values) { _values = values; QueueRedraw(); }
    public override Vector2 _GetMinimumSize() => new(_cols * Cw, Ch);

    public override void _Draw()
    {
        if (_values.Length == 0) return;
        int n = Math.Min(_cols, _values.Length);
        var shown = _values[^n..];
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in shown) { lo = MathF.Min(lo, v); hi = MathF.Max(hi, v); }
        var color = shown[^1] >= shown[0] ? Tok("good") : Tok("bad");
        for (int i = 0; i < n; i++)
        {
            int level = hi - lo < 1e-3f ? 3 : (int)MathF.Round((shown[i] - lo) / (hi - lo) * 7);
            GlyphDraw.Glyph(this, GlyphDraw.Eighths[level].ToString(), new Rect2((_cols - n + i) * Cw, (Size.Y - Ch) / 2, Cw, Ch), color);
        }
    }
}

/// <summary>A list row: children laid out in a row; hover text @6%, selected accent @16% (half a cell beyond each side).</summary>
public partial class GlyphRow : HBoxContainer
{
    private bool _hover, _selected;
    public event Action? Pressed;
    public event Action? RightPressed;

    public GlyphRow()
    {
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(0, GlyphTheme.Ch);
        MouseEntered += () => { _hover = true; QueueRedraw(); };
        MouseExited += () => { _hover = false; QueueRedraw(); };
    }

    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; QueueRedraw(); } }
    }

    public bool Clickable
    {
        set => MouseDefaultCursorShape = value ? CursorShape.PointingHand : CursorShape.Arrow;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true } b)
        {
            if (b.ButtonIndex == MouseButton.Left && Pressed != null) { AcceptEvent(); Pressed(); }
            else if (b.ButtonIndex == MouseButton.Right && RightPressed != null) { AcceptEvent(); RightPressed(); }
        }
    }

    public override void _Draw()
    {
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        var r = new Rect2(-cw * 0.5f, Size.Y * 0.06f, Size.X + cw, Size.Y * 0.88f);
        if (_selected) GlyphDraw.Round(this, r, GlyphTheme.Tok("accent", 0.16f, 0.14f), 4);
        else if (_hover && Pressed != null) GlyphDraw.Round(this, r, GlyphTheme.Tok("text", 0.06f), 4);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationThemeChanged)
        {
            CustomMinimumSize = new Vector2(CustomMinimumSize.X, GlyphTheme.Ch);
            QueueRedraw();
        }
    }
}

/// <summary>Factory for themed plain controls (labels in cell widths, pill buttons that never take keyboard focus).</summary>
public static class G
{
    /// <summary>A label in a tone (Text, Dim, Faint, Hi, Accent, Good, Warn, Bad, Info), optionally bold and a fixed number of cells wide.</summary>
    public static Label Text(string text, string tone = "Text", bool bold = false, int cols = 0, HorizontalAlignment align = HorizontalAlignment.Left, bool expand = false)
    {
        var l = new Label
        {
            Text = text,
            ThemeTypeVariation = bold ? tone + "Bold" : tone == "Text" ? "" : tone,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = cols > 0,
            CustomMinimumSize = new Vector2(cols * GlyphTheme.Cw, GlyphTheme.Ch),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        if (expand) l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return l;
    }

    /// <summary>Wrapping paragraph text.</summary>
    public static Label Para(string text, string tone = "Dim")
    {
        var l = Text(text, tone);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        l.CustomMinimumSize = new Vector2(8 * GlyphTheme.Cw, GlyphTheme.Ch);
        return l;
    }

    public static void Retone(Label l, string tone, bool bold = false) =>
        l.ThemeTypeVariation = bold ? tone + "Bold" : tone == "Text" ? "" : tone;

    /// <summary>A pill button (" Label "): default accent outline, or primary (solid), or ghost (bare text).</summary>
    public static Button Button(string text, Action pressed, string? tip = null, string variation = "")
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, ThemeTypeVariation = variation };
        b.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        b.Pressed += pressed;
        b.WithTip(tip);
        return b;
    }

    public static GlyphRow Row(params Control[] children)
    {
        var r = new GlyphRow();
        foreach (var c in children) r.AddChild(c);
        return r;
    }

    public static Control Gap(float rows = 0.5f) => new Control { CustomMinimumSize = new Vector2(0, rows * GlyphTheme.Ch), MouseFilter = Control.MouseFilterEnum.Ignore };
    public static Control Spacer() => new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };

    public static void Clear(Node n)
    {
        foreach (var c in n.GetChildren())
        {
            n.RemoveChild(c);
            c.QueueFree();
        }
    }

    public static string Plural(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";
}

/// <summary>
/// Key hints on one row: "[Drag] a box  [Shift] remove  [RMB] done", each key a keycap followed by what it does in
/// dim text. Shared by the tool card and the Build window's footer so controls read the same everywhere.
/// </summary>
public partial class KeyHints : GlyphControl
{
    private (string Key, string What)[] _hints = [];
    public int Cells { get; private set; }

    public KeyHints() => MouseFilter = MouseFilterEnum.Ignore;

    public static int CellsOf((string Key, string What)[] hints)
    {
        int n = 0;
        foreach (var (k, w) in hints) n += GlyphDraw.KeycapCells(k) + 1 + w.Length + 2;
        return Math.Max(0, n - 2);
    }

    public void Set((string Key, string What)[] hints)
    {
        if (hints.Length == _hints.Length && System.Linq.Enumerable.SequenceEqual(hints, _hints)) return;
        _hints = hints;
        Cells = CellsOf(hints);
        UpdateMinimumSize();
        QueueRedraw();
    }

    public override Vector2 _GetMinimumSize() => new(Cells * Cw, Ch);

    public override void _Draw()
    {
        float x = 0;
        foreach (var (k, w) in _hints)
        {
            if (x + (GlyphDraw.KeycapCells(k) + 1 + w.Length) * Cw > Size.X + 1) break; // what doesn't fit is left off whole
            x += GlyphDraw.Keycap(this, k, x) * Cw + Cw;
            GlyphDraw.Text(this, w, new Vector2(x, 0), w.Length * Cw + 2, Tok("dim"));
            x += (w.Length + 2) * Cw;
        }
    }
}

/// <summary>
/// Wrapped text on the cell grid for tooltips and cards: the first line bold in hi when there are several paragraphs
/// (a title), the rest in text.
/// </summary>
public partial class TipText : GlyphControl
{
    private List<string> _lines = new();
    private int _titleLines;
    public int Cols { get; private set; }
    public int Rows => _lines.Count;

    public TipText() => MouseFilter = MouseFilterEnum.Ignore;

    public void Set(string text, int maxCols)
    {
        int br = text.IndexOf('\n');
        _titleLines = br > 0 ? GlyphDraw.Wrap(text[..br], maxCols).Count : 0;
        _lines = GlyphDraw.Wrap(text, maxCols);
        Cols = 0;
        foreach (var l in _lines) Cols = Math.Max(Cols, l.Length);
        QueueRedraw();
    }

    public override void _Draw()
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            bool title = i < _titleLines;
            GlyphDraw.Text(this, _lines[i], new Vector2(0, i * Ch), Size.X, title ? Tok("hi") : Tok(_titleLines > 0 ? "text" : "hi"), title);
        }
    }
}
