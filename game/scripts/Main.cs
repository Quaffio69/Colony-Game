using System.Collections.Generic;
using System.Text;
using Colony.Core;
using Colony.Core.Procedural;
using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>
/// Scene root: creates the simulation, runs it at a fixed tick rate, wires the views and tools to it,
/// and sets up lighting and the HUD.
/// </summary>
public partial class Main : Node3D
{
    [Export] public int Seed { get; set; } = 12345;
    [Export] public int WorldWidth { get; set; } = 2048;
    [Export] public int WorldDepth { get; set; } = 2048;
    [Export] public int WorldHeight { get; set; } = 128;
    [Export] public int StartingColonists { get; set; } = 7;

    private static readonly int[] Speeds = [1, 3, 10];
    private const double TickSeconds = 1.0 / ColonySim.TicksPerSecond;

    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private DesignationTool _tool = null!;
    private Label _hud = null!;
    private CameraRig _rig = null!;

    private int _speedIndex;
    private bool _paused;
    private double _tickAccumulator;

    public override void _Ready()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.45f, 0.6f, 0.8f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.65f, 0.68f, 0.75f),
            AmbientLightEnergy = 0.55f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };
        AddChild(new WorldEnvironment { Environment = env });
        // Distant terrain is mostly 1-tile terraces; without AA their edges alias into speckle.
        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, 35, 0),
            LightEnergy = 1.1f,
            ShadowEnabled = true,
        });

        ulong start = Time.GetTicksMsec();
        _sim = ColonySim.Generate(new WorldGenSettings(Seed, WorldWidth, WorldDepth, WorldHeight), StartingColonists);
        GD.Print($"World {WorldWidth}x{WorldDepth}x{WorldHeight} built in {Time.GetTicksMsec() - start} ms: {_sim.StartupReport}");

        _world = GetNode<WorldView>("WorldView");
        _world.Initialize(_sim);

        GetNode<ColonistView>("ColonistView").Initialize(_sim, _world);

        _tool = GetNode<DesignationTool>("DesignationTool");
        _tool.Initialize(_sim, _world);

        _rig = GetNode<CameraRig>("CameraRig");
        FocusOnColony();

        var ui = new CanvasLayer();
        _hud = new Label { Position = new Vector2(12, 10) };
        _hud.AddThemeFontSizeOverride("font_size", 17);
        _hud.AddThemeConstantOverride("outline_size", 4);
        _hud.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.6f));
        ui.AddChild(_hud);
        AddChild(ui);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key) return;

        switch (key.Keycode)
        {
            case Key.Pageup or Key.X: _world.ViewLevel++; break;
            case Key.Pagedown or Key.Z: _world.ViewLevel--; break;
            case Key.Space when !key.Echo: _paused = !_paused; break;
            case Key.Key1: _speedIndex = 0; _paused = false; break;
            case Key.Key2: _speedIndex = 1; _paused = false; break;
            case Key.Key3: _speedIndex = 2; _paused = false; break;
            case Key.C when !key.Echo: FocusOnColony(); break;
        }
    }

    /// <summary>Points the camera at the colonists' average position, close enough to see them.</summary>
    private void FocusOnColony()
    {
        if (_sim.Colonists.Count == 0)
        {
            _rig.Focus(_world.Center);
            return;
        }
        var sum = Vector3.Zero;
        foreach (var c in _sim.Colonists) sum += SimSpace.TileCenter(c.Position);
        // Colonists start south of the mountain; look at them from the valley so the slope does not block the view.
        _rig.Focus(sum / _sim.Colonists.Count, 22f, yaw: 200f);
    }

    public override void _Process(double delta)
    {
        HandleDevArgs(delta);

        if (!_paused)
        {
            // Fixed-rate simulation; cap catch-up so a long frame can't snowball.
            _tickAccumulator += delta * Speeds[_speedIndex];
            int budget = 20 * Speeds[_speedIndex];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (_tickAccumulator >= TickSeconds && budget-- > 0)
            {
                _sim.Step();
                _tickAccumulator -= TickSeconds;
            }
            if (budget <= 0) _tickAccumulator = 0;
            _simMsThisFrame = clock.Elapsed.TotalMilliseconds;
            _maxSimMs = System.Math.Max(_maxSimMs, _simMsThisFrame);
        }

        UpdateHud();
    }

    private void UpdateHud()
    {
        var sb = new StringBuilder();
        string hover = _tool.Hovered is { } h ? $"{_sim.World.Get(h)} at {h}" : "-";
        string time = _paused ? "PAUSED" : $"{Speeds[_speedIndex]}x";
        string tool = _tool.Mode == DesignationTool.ToolMode.Stairs ? $"Stairs (depth {_tool.StairDepth})" : "Dig";
        double seconds = _sim.Tick / (double)ColonySim.TicksPerSecond;

        sb.AppendLine($"Level {_world.ViewLevel} / {_sim.World.Height - 1}    Time {seconds:0}s [{time}]    " +
                      $"{Engine.GetFramesPerSecond()} fps, sim {_simMsThisFrame:0.0} ms" +
                      (_world.PendingSections > 0 ? $"    building terrain: {_world.PendingSections} sections left" : ""));
        sb.AppendLine($"Tool: {tool}    Hover: {hover}");
        sb.AppendLine($"Designations: {_sim.Designations.Count}    {_tool.LastAction}");
        if (_sim.RecentCaveIns.Count > 0)
        {
            var last = _sim.RecentCaveIns[^1];
            double ago = (_sim.Tick - last.Tick) / (double)ColonySim.TicksPerSecond;
            if (ago < 20)
                sb.AppendLine($"CAVE-IN! {last.Tiles} tiles fell {last.Drop} levels near {last.Origin} ({ago:0}s ago)");
        }
        sb.AppendLine();
        foreach (var c in _sim.Colonists)
            sb.AppendLine($"  {c.Name,-8} {c.Describe()}");
        sb.AppendLine();
        sb.AppendLine("Tab: dig/stairs tool   [ ]: stair depth   LMB drag: designate   RMB drag: cancel");
        sb.AppendLine("Z/X: level down/up   Space: pause   1/2/3: speed   Enter: (debug) complete all");
        sb.AppendLine("WASD: pan   Q/E: rotate   Wheel: zoom   Middle-drag: orbit   C: find colonists");
        // StringBuilder uses \r\n on Windows, which Label renders as an extra blank line.
        _hud.Text = sb.ToString().Replace("\r", "").TrimEnd();
    }

    // ---- Dev aids -------------------------------------------------------------------------------
    // godot --path game -- [--size=WxDxH] [--seed=N] [--level=N] [--dig-demo] [--stairs-demo] [--ticks=N]
    //                      [--zoom=DIST] [--screenshot=out.png] [--bench]
    // With --screenshot or --bench: waits until the world is fully meshed, measures 120 frames, prints a
    // performance report, saves the screenshot (if asked) and quits.

    private const int BenchFrames = 120;
    private int _frames;
    private TilePos? _stairsDemoAt;
    private ulong _startMs;
    private bool _benchActive;
    private ulong _meshedAtMs;
    private readonly List<double> _frameMs = new();
    private double _maxSimMs, _simMsThisFrame;

    public override void _EnterTree()
    {
        _startMs = Time.GetTicksMsec();
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--size="))
            {
                var parts = arg["--size=".Length..].Split('x');
                if (parts.Length == 3 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int d) && int.TryParse(parts[2], out int h))
                    (WorldWidth, WorldDepth, WorldHeight) = (w, d, h);
            }
            if (arg.StartsWith("--seed=") && int.TryParse(arg["--seed=".Length..], out int seed)) Seed = seed;
        }
    }

    private static string? Arg(string name)
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg == name) return "";
            if (arg.StartsWith(name + "=")) return arg[(name.Length + 1)..];
        }
        return null;
    }

    private void HandleDevArgs(double delta)
    {
        _frames++;
        if (_frames == 1)
        {
            if (int.TryParse(Arg("--level"), out int level)) _world.ViewLevel = level;
            if (Arg("--dig-demo") != null) DigDemo();
            if (Arg("--stairs-demo") != null) StairsDemo();
            if (int.TryParse(Arg("--ticks"), out int ticks))
                for (int i = 0; i < ticks; i++) _sim.Step();
            if (_stairsDemoAt is { } s) _rig.Focus(SimSpace.TileCenter(s), 12f, yaw: 200f, pitch: -55f);
            if (float.TryParse(Arg("--zoom"), out float zoom)) _rig.Focus(_world.Center, zoom, yaw: 200f, pitch: -50f);
            _benchActive = Arg("--screenshot") != null || Arg("--bench") != null;
        }
        if (!_benchActive) return;

        if (_meshedAtMs == 0)
        {
            if (_frames > 2 && _world.PendingSections == 0)
            {
                _meshedAtMs = Time.GetTicksMsec();
                _maxSimMs = 0;
            }
            return;
        }

        _frameMs.Add(delta * 1000.0);
        if (_frameMs.Count < BenchFrames) return;

        _frameMs.Sort();
        double avg = 0;
        foreach (var f in _frameMs) avg += f;
        avg /= _frameMs.Count;
        GD.Print($"[bench] world {WorldWidth}x{WorldDepth}x{WorldHeight}: {_sim.StartupReport}");
        GD.Print($"[bench] fully meshed {_meshedAtMs - _startMs} ms after start; {_world.SectionCount} sections, {_world.VisibleQuads} quads");
        GD.Print($"[bench] frame ms over {BenchFrames} frames: avg {avg:0.00}, p95 {_frameMs[(int)(_frameMs.Count * 0.95)]:0.00}, max {_frameMs[^1]:0.00}; max sim step batch {_maxSimMs:0.00} ms");
        GD.Print($"[bench] draw calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)}, " +
                 $"video mem {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576:0} MB, " +
                 $"static mem {OS.GetStaticMemoryUsage() / 1048576} MB, managed {System.GC.GetTotalMemory(false) / 1048576} MB");

        if (Arg("--screenshot") is { Length: > 0 } path)
        {
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"Saved screenshot to {path}");
        }
        GetTree().Quit();
    }

    /// <summary>Designates a room into the mountain's flank at the current level.</summary>
    private void DigDemo()
    {
        int z = _world.ViewLevel, cx = _sim.World.Width / 2, cy = _sim.World.Depth / 2;
        _sim.Designations.DesignateBox(new TilePos(cx - 6, cy - 4, z), new TilePos(cx + 6, cy + 4, z));
    }

    /// <summary>Designates a 2x2 staircase in the ground right next to the colonists.</summary>
    private void StairsDemo()
    {
        var c = _sim.Colonists[0].Position;
        var a = new TilePos(c.X + 3, c.Y + 3, _sim.World.SurfaceLevel(c.X + 3, c.Y + 3));
        _sim.Designations.DesignateStairs(a, a.Offset(1, 1, 0), a.Z, 6);
        _stairsDemoAt = a;
        GD.Print($"Stairs demo: footprint {a}..{a.Offset(1, 1, 0)}, levels {a.Z}..{a.Z - 5}");
    }
}
