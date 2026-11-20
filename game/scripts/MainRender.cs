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
            Settings = LoadLook() ?? GameDefaults(),
            GameCamera = _rig.Camera.GetPath(),
            SunLight = _sun.GetPath(),
            CreateTuningPanel = false, // the Graphics window (F10) is in the game's own UI
        };
        AddChild(_renderer);
        // Dev overrides for benches (never saved): the game's defaults, a projection, a pixel size.
        if (Arg("--default-look") != null) _renderer.Settings = GameDefaults();
        if (Arg("--ortho") != null) _renderer.Settings.Orthographic = true;
        if (Arg("--persp") != null) _renderer.Settings.Orthographic = false;
        if (float.TryParse(Arg("--pixel"), out float px)) _renderer.Settings.PixelSize = px;
        if (Arg("--no-shadows") != null) _renderer.Settings.SunShadows = false;
        _renderer.ApplySettings();
        if (Arg("--profiler") != null) _renderer.Settings.ShowProfiler = true; // dev: the profiler from the start
        GameLooks.Watch(this);
    }

    /// <summary>The diorama's defaults; zoom 1x is 45 tiles from the pivot (glyphs from ~18 in, symbols from ~50 out).</summary>
    public static DwarfRenderSettings GameDefaults() => new() { ReferenceDistance = 45f };

    public const string LookPath = "user://dwarf_render_settings.tres";

    /// <summary>The look in use (null in the classic renderer).</summary>
    public DwarfRenderSettings? Look => _renderer?.Settings;
    public DwarfRenderer? Renderer => _renderer;

    private static DwarfRenderSettings? LoadLook() =>
        FileAccess.FileExists(LookPath) ? ResourceLoader.Load<DwarfRenderSettings>(LookPath, null, ResourceLoader.CacheMode.Ignore) : null;

    /// <summary>Saves the look (it loads with every world from then on).</summary>
    public void SaveLook() { if (_renderer != null) ResourceSaver.Save(_renderer.Settings, LookPath); }

    /// <summary>Puts a look in use (a saved one, defaults, or a changed copy) and pushes it to the shaders.</summary>
    public void UseLook(DwarfRenderSettings look)
    {
        if (_renderer == null) return;
        _renderer.Settings = look;
        _renderer.ApplySettings();
    }

    /// <summary>The saved look again (false if none was saved).</summary>
    public bool RevertLook()
    {
        if (LoadLook() is not { } saved) return false;
        UseLook(saved);
        return true;
    }

    private int _shadowMapApplied;
    private float _ceiling;

    /// <summary>Each frame: the renderer zooms about the rig's pivot (the ground under the view), not where the ray lands.</summary>
    private void UpdateRenderer()
    {
        if (_renderer == null) return;
        var look = _renderer.Settings;
        _renderer.FocusOverride = _rig.GlobalPosition;
        _renderer.ZoomOverride = look.ReferenceDistance / Mathf.Max(1f, _rig.Distance); // same zoom whatever the projection
        _rig.Orthographic = look.Orthographic;
        _rig.OrbitStep = look.OrbitStep;
        _rig.TopDownAt = look.TopDownZoom > 0 ? look.ReferenceDistance / look.TopDownZoom : 0f; // the map view, eased in from half that distance
        _rig.TopDownFrom = _rig.TopDownAt * 0.5f;
        // The iso camera stands back to clear the highest drawable terrain: the local tops (far block samples round the view;
        // the world top only made it, and its shadow cascades, reach hundreds of tiles through empty air, losing shadows and
        // drawing more), never above the cut. Rises at once (nothing gets sliced), lowers gently (shadows don't twitch).
        float ceiling = Mathf.Min(_world.ViewLevel + 1, _world.TopAround(_rig.GlobalPosition.X, _rig.GlobalPosition.Z, _rig.Distance * 2.5f + 32f) + 2f);
        _ceiling = ceiling >= _ceiling ? ceiling : Mathf.Lerp(_ceiling, ceiling, 1f - Mathf.Exp(-(float)GetProcessDeltaTime() * 2f));
        _rig.Ceiling = _ceiling;
        _flora.RangeOffset = _rig.CameraBack - _rig.Distance;            // detail ranges measure from the pivot, not the iso camera
        // Far out and mostly symbols, full-detail trees can't be seen (each is ~1k triangles): show the light markers instead.
        // Full-detail plants only while a tile is a few pixels across (a tree is ~1k triangles; in iso every tree on screen is
        // the same size, so past this the whole map's 130k trees drew in full and again into the shadows: 570M triangles), and
        // not once the far view is mostly symbols. Hysteresis so a zoom near the edge doesn't flip it.
        _flora.Detail = _renderer.TexelWorld < (_flora.Detail ? 0.75f : 0.6f) && !(_renderer.Zoom < 1f && _renderer.AsciiAmount > 0.6f);
        // Once an art pixel covers more than a tile, coarse far blocks (cells of 2 tiles) draw the terrain and water instead
        // of ten thousand sections. On from 1.5 tiles a pixel, off below 1.2, so a zoom near the edge doesn't flip it.
        _world.FarView = _renderer.TexelWorld > (_world.FarView ? 1.2f : 1.5f);
        // Perspective sees to the horizon: blocks beyond the distance where an art pixel covers 1.5 tiles draw coarse (plus
        // half a block's diagonal, so a block's near edge is never coarse). Iso has one scale: the switch above does it.
        _world.FarRange = look.Orthographic ? 1e9f
            : 1.5f * _rig.Distance / Mathf.Max(0.01f, _renderer.TexelWorld) + WorldView.FarBlock * 0.71f;
        _renderer.TimeOfDay = _sim.Tick % Colony.Core.Agents.Needs.Day / (float)Colony.Core.Agents.Needs.Day;
        // Shadows reach as far as the view does (Godot measures from the camera, which ortho puts further back).
        // Iso: the farthest ground in view is ~0.65 x the zoom past the pivot (Godot extends casters towards the sun itself),
        // so camera + 1 x zoom; 2 x drew a much bigger shadow region for nothing. Perspective keeps 3 x.
        _sun.DirectionalShadowMaxDistance = Mathf.Clamp(_rig.CameraBack + _rig.Distance * (look.Orthographic ? 1f : 2f), 100f, 2500f);
        if ((int)look.ShadowMap != _shadowMapApplied)
        {
            _shadowMapApplied = (int)look.ShadowMap;
            RenderingServer.DirectionalShadowAtlasSetSize(_shadowMapApplied, true);
        }
    }
}
