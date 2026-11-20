using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using DwarfRender;
using Godot;

namespace ColonyGame.UI;

/// <summary>A draggable value bar: label-less (rows add their own), fill + knob, the formatted value on the right.</summary>
public partial class GlyphSlider : GlyphControl
{
    private readonly double _min, _max, _step;
    private double _value;
    private readonly Func<double, string> _format;
    private readonly Action<double> _changed;
    private bool _drag, _hover;
    private const int ValueCols = 10;

    public GlyphSlider(double min, double max, double step, double value, Func<double, string> format, Action<double> changed)
    {
        _min = min; _max = max; _step = step <= 0 ? 0.01 : step; _value = value; _format = format; _changed = changed;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        MouseEntered += () => { _hover = true; QueueRedraw(); };
        MouseExited += () => { _hover = false; QueueRedraw(); };
    }
    public GlyphSlider() : this(0, 1, 0.01, 0, v => v.ToString("0.##"), _ => { }) { }

    public double Value
    {
        get => _value;
        set { _value = value; QueueRedraw(); }
    }

    private float BarWidth => Math.Max(Cw * 4, Size.X - ValueCols * Cw);

    public override Vector2 _GetMinimumSize() => new((12 + ValueCols) * Cw, Ch);

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                _drag = b.Pressed;
                if (b.Pressed) SetFrom(b.Position.X);
                AcceptEvent();
                break;
            case InputEventMouseMotion m when _drag:
                SetFrom(m.Position.X);
                AcceptEvent();
                break;
        }
    }

    private void SetFrom(float x)
    {
        double t = Math.Clamp(x / BarWidth, 0, 1);
        double v = _min + Math.Round(t * (_max - _min) / _step) * _step;
        v = Math.Clamp(Math.Round(v, 6), _min, _max);
        if (v == _value) return;
        _value = v;
        QueueRedraw();
        _changed(v);
    }

    public override void _Draw()
    {
        float w = BarWidth;
        float t = (float)Math.Clamp((_value - _min) / Math.Max(1e-9, _max - _min), 0, 1);
        GlyphDraw.Bar(this, new Rect2(0, 0, w, Size.Y), t, Tok(_hover || _drag ? "accent" : "dim"));
        float knob = MathF.Round(Ch * 0.42f);
        GlyphDraw.Round(this, new Rect2(t * (w - knob), (Size.Y - knob) / 2, knob, knob), Tok(_drag ? "hi" : "accent"), knob / 2);
        GlyphDraw.Text(this, _format(_value), new Vector2(w, 0), Size.X - w, Tok("hi"), align: HorizontalAlignment.Right);
    }
}

/// <summary>◄ value ►: click the left half for the previous option, the right half for the next.</summary>
public partial class GlyphCycler : GlyphControl
{
    private readonly string[] _labels;
    private int _index;
    private readonly Action<int> _picked;
    private bool _hover;

    public GlyphCycler(string[] labels, int index, Action<int> picked)
    {
        _labels = labels.Length == 0 ? [""] : labels; _index = Math.Clamp(index, 0, _labels.Length - 1); _picked = picked;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        MouseEntered += () => { _hover = true; QueueRedraw(); };
        MouseExited += () => { _hover = false; QueueRedraw(); };
    }
    public GlyphCycler() : this([], 0, _ => { }) { }

    public int Index
    {
        get => _index;
        set { _index = Math.Clamp(value, 0, _labels.Length - 1); QueueRedraw(); }
    }

    public override Vector2 _GetMinimumSize() => new((Math.Min(24, _labels.Max(l => l.Length)) + 4) * Cw, Ch);

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } b || b.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;
        int step = b.ButtonIndex == MouseButton.Right || b.Position.X < Size.X / 2 ? -1 : 1;
        _index = (_index + step + _labels.Length) % _labels.Length;
        QueueRedraw();
        AcceptEvent();
        _picked(_index);
    }

    public override void _Draw()
    {
        if (_hover) GlyphDraw.Round(this, new Rect2(0, Ch * 0.06f, Size.X, Ch * 0.88f), GlyphTheme.Tok("text", 0.06f), 4);
        GlyphDraw.Text(this, "◄", Vector2.Zero, Cw, Tok("accent"));
        GlyphDraw.Text(this, "►", new Vector2(Size.X - Cw, 0), Cw, Tok("accent"));
        GlyphDraw.Text(this, _labels[_index], new Vector2(Cw * 1.5f, 0), Size.X - Cw * 3, Tok("hi"), align: HorizontalAlignment.Center);
    }
}

/// <summary>
/// F10: every knob of the pixel-art look (the Dwarf Render kit's settings, built from its [Export]s by group, with the
/// diorama's labels and value formats): presets, then pixels, voxels, palette, foliage, outlines, lighting (sun angle,
/// warmth, ambient, shadows, day/night with the game clock), grade, art effects, ASCII (every zoom threshold), camera
/// (iso projection, what zoom 1x means) and performance (shadow map, profiler). Changes apply live; Save keeps them for
/// every world, Revert goes back to the saved look, Defaults to the game's.
/// </summary>
public partial class GraphicsWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private readonly List<Action> _refresh = new();
    private Label _readout = null!;

    private static readonly HashSet<string> Hidden = ["ReverseZ", "FocusPlaneY", "ReferenceOrthoSize"];

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["PixelSize"] = "Pixel size", ["CameraSnapping"] = "Camera snapping", ["TexturePack"] = "Texture pack",
        ["PaletteEnabled"] = "Palette", ["Palette"] = "Palette set", ["PaletteSize"] = "Colours", ["Dither"] = "Dither strength",
        ["FoliageTechnique"] = "Technique", ["WorldScreenDither"] = "World ↔ screen dither", ["BackLightGlow"] = "Back-light glow",
        ["OutlinesEnabled"] = "Outlines", ["InnerCrease"] = "Inner crease (− glint / + ink)", ["TorchGlow"] = "Torch & forge glow",
        ["SunShadows"] = "Sun shadows", ["DayNightCycle"] = "Day / night cycle (game clock)", ["PaintWobble"] = "Paint wobble",
        ["WatercolourEdges"] = "Watercolour edges", ["AnimatedGrain"] = "Animated film grain", ["Crt"] = "CRT screen",
        ["AsciiMode"] = "ASCII mode", ["ZoomedOutSymbols"] = "Zoomed-out symbols", ["ZoomedInSymbols"] = "Zoomed-in symbols",
        ["DwarfsAlwaysShow"] = "Colonists always show as ☺", ["DissolveCrossfade"] = "Dissolve ↔ crossfade",
        ["ZoomInStart"] = "Glyphs from zoom", ["ZoomInLength"] = "Glyph fade-in length",
        ["ZoomStep"] = "Redraw scale every", ["PixelGrowStart"] = "Pixels grow from zoom",
        ["PixelGrowth"] = "Pixel growth", ["MaxPixelSize"] = "Largest pixel", ["OrbitStep"] = "Turn in steps of", ["TopDownZoom"] = "Top-down map from zoom",
        ["ZoomOutStart"] = "Zoom out: transition starts at", ["ZoomOutFullyText"] = "Zoom out: fully text at",
        ["ZoomOutCharSize"] = "Zoom out: final character size", ["AlwaysCharSize"] = "Always mode: character size",
        ["SkipShadowsWhenAllText"] = "Far: skip shadows when all text", ["SceneColourVsMono"] = "Scene colour ↔ mono",
        ["MonoGlyphColour"] = "Mono glyph colour", ["AsciiBackground"] = "Background", ["PerSurfaceSymbols"] = "Per-surface symbols",
        ["EdgeStrokes"] = "Edge strokes | / - \\", ["InvertDensity"] = "Invert (dark = dense)", ["Orthographic"] = "Orthographic (iso)",
        ["ReferenceDistance"] = "Zoom 1× = tiles from the view", ["ShadowMap"] = "Shadow map", ["ShowProfiler"] = "Show profiler",
    };

    public GraphicsWindow(GameUi ui) : base("graphics", "Graphics", "▲", "accent", "F10")
    {
        _ui = ui;
        MinCols = 52;
        MinRows = 12;
        Default = new WindowState { X = -58, Y = 2, W = 56, H = 34 };
    }
    public GraphicsWindow() : this(null!) { }

    private DwarfRenderSettings? S => _ui.Main.Look;

    protected override void Build()
    {
        _refresh.Clear();
        if (S is null)
        {
            Content.AddChild(G.Para("The pixel-art renderer is off. Turn it on in Appearance (F2) to tune the look here.", "Dim"));
            return;
        }
        _readout = G.Text("", "Dim");
        Header.AddChild(_readout);
        Header.AddChild(G.Gap(0.25f));
        Header.AddChild(Row("Style preset", Preset(DwarfRenderSettings.StylePresets, (s, n) => s.ApplyStylePreset(n))));
        Header.AddChild(Row("Foliage preset", Preset(DwarfRenderSettings.FoliagePresets, (s, n) => s.ApplyFoliagePreset(n))));
        Header.AddChild(Row("ASCII look", Preset(DwarfRenderSettings.AsciiPresets, (s, n) => s.ApplyAsciiPreset(n))));
        var buttons = new HBoxContainer();
        buttons.AddChild(G.Button(" Save ", () => { _ui.Main.SaveLook(); GlyphStatus.Say("Look saved: every world opens with it"); }, "Keep this look for every world", "Primary"));
        buttons.AddChild(G.Button(" Revert ", () =>
        {
            GlyphStatus.Say(_ui.Main.RevertLook() ? "Back to the saved look" : "No saved look yet");
            RefreshAll();
        }, "Back to the look you saved last"));
        buttons.AddChild(G.Button(" Defaults ", () => { _ui.Main.UseLook(Main.GameDefaults()); RefreshAll(); }, "The game's default look"));
        Header.AddChild(buttons);
        Header.AddChild(G.Gap(0.25f));

        var props = typeof(DwarfRenderSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<ExportAttribute>() != null && p.CanWrite && !Hidden.Contains(p.Name))
            .OrderBy(p => p.MetadataToken);
        foreach (var p in props)
        {
            if (p.GetCustomAttribute<ExportGroupAttribute>() is { } g)
            {
                Content.AddChild(G.Gap(0.25f));
                Content.AddChild(new GlyphSection(g.Name));
            }
            if (Control(p) is { } c) Content.AddChild(c);
        }
        Footer.AddChild(G.Para("Changes apply at once. Save keeps the look for every world.", "Faint"));
    }

    private void Changed()
    {
        _ui.Main.Renderer?.ApplySettings();
    }

    /// <summary>A preset picker (applies the preset to the current look).</summary>
    private Control Preset(string[] names, Action<DwarfRenderSettings, string> apply) =>
        new GlyphCycler(["(choose)", .. names], 0, i =>
        {
            if (i == 0 || S is not { } s) return;
            apply(s, names[i - 1]);
            Changed();
            RefreshAll();
        });

    /// <summary>Puts the controls back in step with the look (after presets, revert, defaults). Deferred: a click may rebuild.</summary>
    private void RefreshAll() => Callable.From(() => { foreach (var r in _refresh) r(); }).CallDeferred();

    private Control Row(string label, Control control)
    {
        var row = new HBoxContainer();
        row.AddChild(G.Text(label, "Text", cols: 24));
        row.AddChild(control);
        return row;
    }

    private Control? Control(PropertyInfo p)
    {
        var ex = p.GetCustomAttribute<ExportAttribute>()!;
        string label = Labels.GetValueOrDefault(p.Name) ?? Nice(p.Name);
        var t = p.PropertyType;
        if (t == typeof(bool))
        {
            var sw = new GlyphSwitch(label, (bool)p.GetValue(S)!, on => { p.SetValue(S, on); Changed(); });
            _refresh.Add(() => sw.On = (bool)p.GetValue(S)!);
            return sw;
        }
        if (t == typeof(float) || t == typeof(int))
        {
            double min = 0, max = t == typeof(int) ? 100 : 10, step = t == typeof(int) ? 1 : 0.01;
            if (ex.Hint == PropertyHint.Range)
            {
                var parts = ex.HintString.Split(',');
                min = double.Parse(parts[0], CultureInfo.InvariantCulture);
                max = double.Parse(parts[1], CultureInfo.InvariantCulture);
                if (parts.Length > 2) step = double.Parse(parts[2], CultureInfo.InvariantCulture);
            }
            var format = Format(p.Name, t == typeof(int));
            var slider = new GlyphSlider(min, max, step, Convert.ToDouble(p.GetValue(S)), format, v =>
            {
                if (t == typeof(int)) p.SetValue(S, (int)Math.Round(v)); else p.SetValue(S, (float)v);
                Changed();
            });
            _refresh.Add(() => slider.Value = Convert.ToDouble(p.GetValue(S)));
            return Row(label, slider);
        }
        if (t.IsEnum)
        {
            var values = Enum.GetValues(t).Cast<object>().ToArray();
            var names = values.Select(v => EnumLabel(v)).ToArray();
            var cycler = new GlyphCycler(names, Array.IndexOf(values, p.GetValue(S)), i => { p.SetValue(S, values[i]); Changed(); });
            _refresh.Add(() => cycler.Index = Array.IndexOf(values, p.GetValue(S)));
            return Row(label, cycler);
        }
        if (t == typeof(Color))
        {
            var picker = new ColorPickerButton
            {
                Color = (Color)p.GetValue(S)!, EditAlpha = false, FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(6 * GlyphTheme.Cw, GlyphTheme.Ch * 0.9f),
            };
            picker.ColorChanged += c => { p.SetValue(S, c); Changed(); };
            _refresh.Add(() => picker.Color = (Color)p.GetValue(S)!);
            var row = new HBoxContainer();
            row.AddChild(G.Text(label, "Text", cols: 24));
            row.AddChild(G.Spacer());
            row.AddChild(picker);
            return row;
        }
        return null;
    }

    /// <summary>Value formats from the diorama: zoom factors with ×, character sizes in px, the sun with its time of day.</summary>
    private static Func<double, string> Format(string name, bool integer) => name switch
    {
        "PixelSize" => v => $"{v:0.#} px",
        "ZoomInStart" or "ZoomOutStart" or "ZoomOutFullyText" or "PixelGrowStart" => v => $"{v:0.00}×",
        "ZoomStep" => v => v <= 1.001 ? "off" : $"{v:0.00}× zoom",
        "PixelGrowth" => v => $"{v * 100:0}%",
        "MaxPixelSize" => v => $"{v:0} px",
        "OrbitStep" => v => v <= 0 ? "smooth" : $"{v:0}°",
        "TopDownZoom" => v => v <= 0 ? "off" : $"{v:0.00}×",
        "ZoomInLength" => v => $"{v:0.0}× zoom",
        "ZoomOutCharSize" or "AlwaysCharSize" => v => $"{v:0} px",
        "GlyphDensity" => v => $"{1 / Math.Max(0.01, v):0.00}",
        "ReferenceDistance" => v => $"{v:0} tiles",
        "SunAngle" => v => { double s = Math.Sin(v * Math.PI / 180); return $"{v:0}° {(s > 0.25 ? "day" : s > -0.05 ? "dusk" : "night")}"; },
        "EdgeRounding" => v => v == 0 ? "off" : $"{v:0.00}",
        _ => integer ? v => $"{v:0}" : v => $"{v:0.00}",
    };

    private static string EnumLabel(object v) => v switch
    {
        ShadowMapSize s => $"{(int)s}",
        DitherPattern d => $"{(int)d}×{(int)d}",
        _ => Nice(v.ToString()!),
    };

    private static string Nice(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && (char.IsUpper(c) || char.IsDigit(c) && !char.IsDigit(name[i - 1])) && !char.IsUpper(name[i - 1]) ? " " + char.ToLowerInvariant(c) : c.ToString()));

    public override void Tick()
    {
        if (S is null || _ui.Main.Renderer is not { } r) return;
        if (_readout == null) { Rebuild(); return; } // opened (from the saved layout) before the renderer existed
        var art = r.ArtSize;
        _readout.Text = $"now: zoom {r.Zoom:0.00}× · ASCII {r.AsciiAmount * 100:0}% · art {art.X}×{art.Y} px · pixel {r.PixelScreen:0.0} px · {Engine.GetFramesPerSecond():0} fps";
    }
}
