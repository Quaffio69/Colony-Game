using DwarfRender;
using Godot;

namespace ColonyGame;

/// <summary>
/// The look (the Dwarf Render kit in <c>addons/dwarf_render</c>): the 3D world is drawn at low resolution through the
/// kit's post pass (outlines, grade, palette, dither) and presented under the UI, turning into glyphs when zoomed right
/// in and Dwarf-Fortress symbols when zoomed far out. Our own camera rig, picking and UI are untouched. F10 tunes it
/// (Save keeps the look in user://dwarf_render_settings.tres). --classic-render keeps the old full-resolution look.
/// </summary>
public partial class Main
{
    private DirectionalLight3D _sun = null!;
    private DwarfRenderer? _renderer;

    /// <summary>The kit is on (not --classic-render): views use its materials and the terrain its textures.</summary>
    public static bool StyledRender
    {
        get
        {
            UI.GlyphTheme.EnsureLoaded();
            return Arg("--classic-render") == null && UI.GlyphTheme.Options.PixelArt;
        }
    }

    /// <summary>Rebuilds the scene on the same world (a renderer switch): the sim carries on untouched.</summary>
    public void ReloadView()
    {
        s_launch = _sim;
        s_launchResult = null;
        GetTree().ReloadCurrentScene();
    }

    private void SetUpRenderer()
    {
        if (Arg("--no-sun-shadows") != null) _sun.ShadowEnabled = false; // dev: compare looks and cost
        if (!StyledRender) return;
        GameLooks.RegisterBlocks();
        _renderer = new DwarfRenderer
        {
            Name = "DwarfRenderer",
            Settings = GameDefaults(),
            GameCamera = _rig.Camera.GetPath(),
            SunLight = _sun.GetPath(),
        };
        AddChild(_renderer);
        foreach (var c in _renderer.GetChildren())
            if (c is SettingsPanel panel) panel.PanelTheme = UI.GlyphTheme.Theme; // F10 in the Glyph UI's font and colours
        GameLooks.Watch(this);
    }

    /// <summary>
    /// The kit's defaults, retuned for this camera: zoom 1 is 40 tiles from the pivot; glyphs take over closer than about
    /// 13 tiles; the far view turns to symbols from 160 tiles out and is all symbols past about 450.
    /// </summary>
    private static DwarfRenderSettings GameDefaults() => new()
    {
        ReferenceDistance = 40f,
        ZoomInStart = 3f,
        ZoomOutStart = 0.25f,
        ZoomOutFullyText = 0.09f,
    };

    /// <summary>Each frame: the renderer zooms about the rig's pivot (the ground under the view), not where the ray lands.</summary>
    private void UpdateRenderer()
    {
        if (_renderer == null) return;
        _renderer.FocusOverride = _rig.GlobalPosition;
    }
}
