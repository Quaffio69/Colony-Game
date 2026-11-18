using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace ColonyGame.UI;

/// <summary>User appearance options (saved with the window layout in user://glyph_ui.json).</summary>
public sealed class GlyphOptions
{
    public string Theme { get; set; } = "hearth";
    public string Font { get; set; } = "JetBrains Mono";
    public int Size { get; set; } = 14;
    public string Density { get; set; } = "cozy";
    public int Radius { get; set; } = 8;
    public string Frame { get; set; } = "hairline";
    public bool Glass { get; set; } = true;
    public bool Shadow { get; set; } = true;
    public bool Chips { get; set; } = true;
    public float Scale { get; set; } = 1f;
    public Dictionary<string, WindowState> Windows { get; set; } = new();
}

/// <summary>A window's rect in cells and whether it is open / minimised.</summary>
public sealed class WindowState
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public bool Open { get; set; }
    public bool Min { get; set; }
    /// <summary>False until first placed on a known grid: a negative default X counts from the right edge.</summary>
    public bool Placed { get; set; }
}

/// <summary>
/// Glyph UI theme (docs/ui/GLYPH_UI.md): colour tokens from glyph_themes.json, the user's options, the fonts, the
/// character-cell metrics and the generated Godot <see cref="Theme"/>. <see cref="Changed"/> fires when any of it
/// changes; the UI root re-applies the theme, which notifies every control (custom ones redraw).
/// </summary>
public static class GlyphTheme
{
    public sealed record FontInfo(string Label, string Regular, string? Bold, int BoldWeight, float Scale);

    public static readonly Dictionary<string, FontInfo> Fonts = new()
    {
        ["JetBrains Mono"] = new("JetBrains", "res://fonts/JetBrainsMono.ttf", null, 600, 1f),
        ["IBM Plex Mono"] = new("Plex", "res://fonts/IBMPlexMono-Regular.ttf", "res://fonts/IBMPlexMono-SemiBold.ttf", 600, 1f),
        ["Space Mono"] = new("Space", "res://fonts/SpaceMono-Regular.ttf", "res://fonts/SpaceMono-Bold.ttf", 700, 0.95f),
        ["VT323"] = new("VT323", "res://fonts/VT323-Regular.ttf", null, 400, 1.35f),
    };

    public static readonly Dictionary<string, float> Densities = new() { ["compact"] = 1.30f, ["cozy"] = 1.55f, ["airy"] = 1.80f };
    public static readonly string[] Frames = ["hairline", "double", "glow", "none"];
    public static readonly int[] Radii = [0, 4, 8, 12, 16];

    public sealed class ThemeData
    {
        public string Id = "", Label = "", Blurb = "";
        public bool Light;
        public readonly Dictionary<string, Color> Tokens = new();
        public Color[] People = [];
    }

    public static readonly List<ThemeData> Themes = new();
    public static GlyphOptions Options { get; private set; } = new();
    public static ThemeData Current { get; private set; } = null!;
    public static Theme Theme { get; private set; } = null!;
    public static Font Regular { get; private set; } = null!;
    public static Font Bold { get; private set; } = null!;
    public static int FontSize { get; private set; }
    /// <summary>One character cell: advance width of M, and the line height (font size x density).</summary>
    public static Vector2 Cell { get; private set; }
    public static float Cw => Cell.X;
    public static float Ch => Cell.Y;
    public static bool Light => Current.Light;
    public static event Action? Changed;

    private const string SavePath = "user://glyph_ui.json";
    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        LoadThemes();
        LoadOptions();
        Rebuild();
    }

    public static Color Tok(string name)
    {
        if (Current.Tokens.TryGetValue(name, out var c)) return c;
        return Current.Tokens["text"];
    }

    public static Color Tok(string name, float alpha) => Tok(name) with { A = alpha };
    /// <summary>Alpha for an overlay fill, a little stronger on light themes (dark, light).</summary>
    public static Color Tok(string name, float dark, float light) => Tok(name) with { A = Light ? light : dark };
    public static Color Person(int id) => Current.People.Length == 0 ? Tok("accent") : Current.People[Math.Abs(id) % Current.People.Length];

    public static Vector2 Cells(float cols, float rows) => new(cols * Cw, rows * Ch);

    /// <summary>Applies a change to the options, rebuilds the theme and saves.</summary>
    public static void Set(Action<GlyphOptions> change)
    {
        change(Options);
        RebuildSoon();
    }

    public static void ResetAppearance()
    {
        var layout = Options.Windows;
        Options = new GlyphOptions { Theme = Options.Theme, Windows = layout };
        RebuildSoon();
    }

    private static bool _rebuildQueued;

    /// <summary>Rebuilds after the current input event: windows rebuild their controls, which may include the one clicked.</summary>
    private static void RebuildSoon()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Callable.From(() =>
        {
            _rebuildQueued = false;
            Rebuild();
            Save();
        }).CallDeferred();
    }

    // ---- Loading ----------------------------------------------------------------------------------------

    private static void LoadThemes()
    {
        using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString("res://ui/glyph_themes.json"));
        foreach (var t in doc.RootElement.GetProperty("themes").EnumerateObject())
        {
            var data = new ThemeData { Id = t.Name };
            foreach (var p in t.Value.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "label": data.Label = p.Value.GetString()!; break;
                    case "blurb": data.Blurb = p.Value.GetString()!; break;
                    case "light": data.Light = p.Value.GetBoolean(); break;
                    case "dwarves": data.People = p.Value.EnumerateArray().Select(c => Color.FromHtml(c.GetString()!)).ToArray(); break;
                    case "map":
                        foreach (var m in p.Value.EnumerateObject()) data.Tokens[m.Name] = Color.FromHtml(m.Value.GetString()!);
                        break;
                    default:
                        if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.StartsWith('#'))
                            data.Tokens[p.Name] = Color.FromHtml(p.Value.GetString()!);
                        break;
                }
            }
            Themes.Add(data);
        }
    }

    private static void LoadOptions()
    {
        try
        {
            if (Godot.FileAccess.FileExists(SavePath))
                Options = JsonSerializer.Deserialize<GlyphOptions>(Godot.FileAccess.GetFileAsString(SavePath)) ?? new GlyphOptions();
        }
        catch (Exception e)
        {
            GD.PrintErr($"Glyph UI: ignoring unreadable {SavePath}: {e.Message}");
            Options = new GlyphOptions();
        }
        if (!Fonts.ContainsKey(Options.Font)) Options.Font = "JetBrains Mono";
        if (!Densities.ContainsKey(Options.Density)) Options.Density = "cozy";
        if (!Frames.Contains(Options.Frame)) Options.Frame = "hairline";
        Options.Size = Math.Clamp(Options.Size, 10, 28);
        Options.Scale = Math.Clamp(Options.Scale, 0.75f, 3f);
    }

    public static void Save()
    {
        using var f = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(JsonSerializer.Serialize(Options, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static readonly Dictionary<string, FontFile> FontFiles = new();

    private static FontFile File(string path)
    {
        if (!FontFiles.TryGetValue(path, out var f))
        {
            f = GD.Load<FontFile>(path);
            FontFiles[path] = f;
        }
        return f;
    }

    private static Font Weighted(FontFile file, int weight)
    {
        var tag = TextServerManager.GetPrimaryInterface().NameToTag("wght");
        return new FontVariation { BaseFont = file, VariationOpentype = new Godot.Collections.Dictionary { [tag] = weight } };
    }

    // ---- Building the theme -----------------------------------------------------------------------------

    public static void Rebuild()
    {
        Current = Themes.FirstOrDefault(t => t.Id == Options.Theme) ?? Themes[0];
        var info = Fonts[Options.Font];
        var regular = File(info.Regular);
        if (info.Bold == null && info.Regular.Contains("JetBrains"))
        {
            Regular = Weighted(regular, 400);
            Bold = Weighted(regular, info.BoldWeight);
        }
        else
        {
            Regular = regular;
            Bold = info.Bold != null ? File(info.Bold) : new FontVariation { BaseFont = regular, VariationEmbolden = 0.6f };
        }
        FontSize = (int)MathF.Round(Options.Size * info.Scale);
        float cw = MathF.Round(Regular.GetStringSize("M", HorizontalAlignment.Left, -1, FontSize).X);
        Cell = new Vector2(Math.Max(4, cw), Math.Max(6, MathF.Round(Options.Size * Densities[Options.Density])));
        Theme = BuildTheme();
        Changed?.Invoke();
    }

    /// <summary>Pill / rounded StyleBoxFlat with optional border and content margins in pixels.</summary>
    public static StyleBoxFlat Box(Color bg, float radius, Color? border = null, float marginX = 0, float marginY = 0)
    {
        int r = (int)radius;
        var sb = new StyleBoxFlat
        {
            BgColor = bg, AntiAliasing = true,
            CornerRadiusTopLeft = r, CornerRadiusTopRight = r, CornerRadiusBottomLeft = r, CornerRadiusBottomRight = r,
            ContentMarginLeft = marginX, ContentMarginRight = marginX, ContentMarginTop = marginY, ContentMarginBottom = marginY,
        };
        if (border is { } b)
        {
            sb.BorderColor = b;
            sb.BorderWidthLeft = sb.BorderWidthRight = sb.BorderWidthTop = sb.BorderWidthBottom = 1;
        }
        return sb;
    }

    /// <summary>Label colour variations: Dim, Faint, Hi, Accent, Good, Warn, Bad, Info (and Bold forms: HiBold ...).</summary>
    public static readonly string[] TextTones = ["Text", "Dim", "Faint", "Hi", "Accent", "Good", "Warn", "Bad", "Info"];

    private static Theme BuildTheme()
    {
        var t = new Theme { DefaultFont = Regular, DefaultFontSize = FontSize };
        Color text = Tok("text"), dim = Tok("dim"), faint = Tok("faint"), accent = Tok("accent"), ink = Tok("accentInk");
        float fontH = Regular.GetHeight(FontSize);
        float pill = Ch;
        float padY = Math.Max(0, (Ch * 0.86f - fontH) / 2f);

        // Labels: a row is one cell tall; the extra line height goes between wrapped lines.
        t.SetColor("font_color", "Label", text);
        t.SetConstant("line_spacing", "Label", (int)Math.Max(0, Ch - fontH));
        foreach (var tone in TextTones)
        {
            var c = Tok(tone.ToLowerInvariant());
            if (tone != "Text")
            {
                t.SetTypeVariation(tone, "Label");
                t.SetColor("font_color", tone, c);
            }
            t.SetTypeVariation(tone + "Bold", "Label");
            t.SetColor("font_color", tone + "Bold", c);
            t.SetFont("font", tone + "Bold", Bold);
        }

        // Buttons: pill, accent @13% with an accent @35% outline; hover fills accent with ink text.
        var normal = Box(Tok("accent", 0.13f, 0.10f), pill, Tok("accent", 0.35f), Cw, padY);
        var hover = Box(accent, pill, accent, Cw, padY);
        var pressed = Box(accent.Darkened(0.12f), pill, accent, Cw, padY);
        var disabled = Box(Tok("text", 0.05f), pill, Tok("text", 0.12f), Cw, padY);
        t.SetStylebox("normal", "Button", normal);
        t.SetStylebox("hover", "Button", hover);
        t.SetStylebox("pressed", "Button", pressed);
        t.SetStylebox("hover_pressed", "Button", hover);
        t.SetStylebox("disabled", "Button", disabled);
        t.SetStylebox("focus", "Button", new StyleBoxEmpty());
        t.SetColor("font_color", "Button", accent);
        t.SetColor("font_hover_color", "Button", ink);
        t.SetColor("font_pressed_color", "Button", ink);
        t.SetColor("font_hover_pressed_color", "Button", ink);
        t.SetColor("font_focus_color", "Button", accent);
        t.SetColor("font_disabled_color", "Button", faint);
        // Primary: solid accent.
        t.SetTypeVariation("Primary", "Button");
        t.SetStylebox("normal", "Primary", Box(accent with { A = 0.9f }, pill, null, Cw, padY));
        t.SetColor("font_color", "Primary", ink);
        t.SetColor("font_focus_color", "Primary", ink);
        // Ghost: a bare text button (list rows, grid cells): hover is text @6%.
        t.SetTypeVariation("Ghost", "Button");
        t.SetStylebox("normal", "Ghost", Box(Colors.Transparent, 4, null, Cw * 0.5f, padY));
        t.SetStylebox("hover", "Ghost", Box(Tok("text", 0.06f, 0.08f), 4, null, Cw * 0.5f, padY));
        t.SetStylebox("pressed", "Ghost", Box(Tok("accent", 0.16f, 0.14f), 4, null, Cw * 0.5f, padY));
        t.SetStylebox("hover_pressed", "Ghost", Box(Tok("accent", 0.2f, 0.16f), 4, null, Cw * 0.5f, padY));
        t.SetStylebox("disabled", "Ghost", Box(Colors.Transparent, 4, null, Cw * 0.5f, padY));
        t.SetColor("font_color", "Ghost", text);
        t.SetColor("font_hover_color", "Ghost", Tok("hi"));
        t.SetColor("font_pressed_color", "Ghost", Tok("hi"));
        t.SetColor("font_hover_pressed_color", "Ghost", Tok("hi"));
        t.SetColor("font_focus_color", "Ghost", text);
        t.SetColor("font_disabled_color", "Ghost", faint);

        // Text fields.
        t.SetStylebox("normal", "LineEdit", Box(Tok("text", 0.05f, 0.06f), 6, Tok("text", 0.11f, 0.16f), Cw, padY));
        t.SetStylebox("focus", "LineEdit", Box(Colors.Transparent, 6, Tok("accent", 0.55f), Cw, padY));
        t.SetColor("font_color", "LineEdit", text);
        t.SetColor("font_placeholder_color", "LineEdit", faint);
        t.SetColor("caret_color", "LineEdit", accent);
        t.SetColor("selection_color", "LineEdit", Tok("accent", 0.3f));

        // Scrollbars: a thin track in the last column, no arrows.
        float barW = Math.Max(3, Cw * 0.28f);
        foreach (var type in new[] { "VScrollBar", "HScrollBar" })
        {
            t.SetStylebox("scroll", type, Box(Tok("text", 0.07f), barW, null, barW / 2, barW / 2));
            t.SetStylebox("scroll_focus", type, Box(Tok("text", 0.07f), barW, null, barW / 2, barW / 2));
            t.SetStylebox("grabber", type, Box(Tok("text", 0.35f), barW, null, barW / 2, barW / 2));
            t.SetStylebox("grabber_highlight", type, Box(accent, barW, null, barW / 2, barW / 2));
            t.SetStylebox("grabber_pressed", type, Box(accent, barW, null, barW / 2, barW / 2));
            var none = new ImageTexture();
            foreach (var icon in new[] { "increment", "increment_highlight", "increment_pressed", "decrement", "decrement_highlight", "decrement_pressed" })
                t.SetIcon(icon, type, none);
        }
        t.SetStylebox("panel", "ScrollContainer", new StyleBoxEmpty());
        t.SetStylebox("panel", "PanelContainer", new StyleBoxEmpty());
        t.SetStylebox("panel", "Panel", new StyleBoxEmpty());

        // Tree (the test list): rows like list rows.
        t.SetStylebox("panel", "Tree", Box(Tok("text", 0.03f, 0.04f), 6));
        t.SetStylebox("focus", "Tree", new StyleBoxEmpty());
        t.SetStylebox("selected", "Tree", Box(Tok("accent", 0.16f, 0.14f), 4));
        t.SetStylebox("selected_focus", "Tree", Box(Tok("accent", 0.16f, 0.14f), 4));
        t.SetStylebox("hovered", "Tree", Box(Tok("text", 0.06f), 4));
        t.SetStylebox("cursor", "Tree", new StyleBoxEmpty());
        t.SetStylebox("cursor_unfocused", "Tree", new StyleBoxEmpty());
        t.SetColor("font_color", "Tree", text);
        t.SetColor("font_selected_color", "Tree", Tok("hi"));
        t.SetColor("font_hovered_color", "Tree", Tok("hi"));
        t.SetColor("guide_color", "Tree", Colors.Transparent);
        t.SetConstant("v_separation", "Tree", (int)Math.Max(0, Ch - fontH));
        t.SetConstant("draw_guides", "Tree", 0);

        t.SetStylebox("panel", "TooltipPanel", Box(Tok("panel"), 6, Tok("text", 0.11f, 0.16f), Cw, padY));
        t.SetColor("font_color", "TooltipLabel", text);
        t.SetStylebox("separator", "HSeparator", Box(Tok("text", 0.1f), 0));
        t.SetConstant("separation", "HSeparator", (int)Ch / 2);
        t.SetConstant("separation", "VBoxContainer", 0);
        t.SetConstant("separation", "HBoxContainer", (int)Cw);
        t.SetConstant("h_separation", "GridContainer", (int)Cw);
        t.SetConstant("v_separation", "GridContainer", 0);
        return t;
    }
}
