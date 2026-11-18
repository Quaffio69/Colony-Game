using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Colony.Core;
using Colony.Core.Procedural;
using Colony.Core.World;
using ColonyGame.UI;
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
    [Export] public int WorldHeight { get; set; } = 384;
    [Export] public int StartingColonists { get; set; } = 7;

    private static readonly int[] Speeds = [1, 3, 10];
    private const double TickSeconds = 1.0 / ColonySim.TicksPerSecond;

    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private FloraView _flora = null!;
    private ItemView _items = null!;
    private WaterView _waterView = null!;
    private DesignationTool _tool = null!;
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
        if (s_launch is { } launched)
        {
            // A world handed over by the test menu (a test's, or the colony coming back from one).
            _sim = launched;
            _test = s_launchResult;
            s_launch = null;
            s_launchResult = null;
            _paused = _test is { Stopped: true }; // watching: start where the test set up, Space to run
        }
        else
        {
            _sim = ColonySim.Generate(new WorldGenSettings(Seed, WorldWidth, WorldDepth, WorldHeight), StartingColonists,
                settleWaterInBackground: true); // settles while the terrain is meshed for the first frames
            GD.Print($"World {WorldWidth}x{WorldDepth}x{WorldHeight} built in {Time.GetTicksMsec() - start} ms: {_sim.StartupReport}");
        }

        _sim.AsyncWater = true; // the water step computes on worker threads while a frame draws
        _world = GetNode<WorldView>("WorldView");
        _world.Initialize(_sim);

        _flora = new FloraView { Name = "FloraView" };
        AddChild(_flora);
        _flora.Initialize(_sim, _world);

        _items = new ItemView { Name = "ItemView" };
        AddChild(_items);
        _items.Initialize(_sim, _world);

        var homes = new HomeView { Name = "HomeView" };
        AddChild(homes);
        homes.Initialize(_sim, _world);

        var edges = new EdgeView { Name = "EdgeView" };
        AddChild(edges);
        edges.Initialize(_sim, _world);

        var covers = new CoverView { Name = "CoverView" };
        AddChild(covers);
        covers.Initialize(_sim, _world);

        var rooms = new RoomView { Name = "RoomView" };
        AddChild(rooms);
        rooms.Initialize(_sim, _world);

        _waterView = new WaterView { Name = "WaterView" };
        AddChild(_waterView);
        _waterView.Initialize(_sim, _world);

        GetNode<ColonistView>("ColonistView").Initialize(_sim, _world);

        _tool = GetNode<DesignationTool>("DesignationTool");
        _tool.Initialize(_sim, _world);

        _rig = GetNode<CameraRig>("CameraRig");
        _rig.GroundHeight = VisibleGround;
        if (_test != null) FocusOnTestWorld();
        else FocusOnColony();

        _ui = new GameUi(this) { Name = "GameUi" };
        AddChild(_ui);
    }

    // ---- What the UI reads and drives ----------------------------------------------------------------

    public ColonySim Sim => _sim;
    public DesignationTool Tool => _tool;
    public WorldView View => _world;
    public bool Paused { get => _paused; set => _paused = value; }
    public static IReadOnlyList<int> SpeedSteps => Speeds;
    public int SpeedIndex { get => _speedIndex; set => _speedIndex = Math.Clamp(value, 0, Speeds.Length - 1); }
    public double SimMs => _simMsThisFrame;
    public TestResult? Test => _test;
    public int PendingSections => _world.PendingSections;

    /// <summary>Looks at a tile from close up (keeps the camera's heading).</summary>
    public void FocusOn(TilePos p) => _rig.Focus(SimSpace.TileCenter(p), 18f);

    /// <summary>The colony, or the whole test world when one is open.</summary>
    public void FocusHome()
    {
        if (_test != null) FocusOnTestWorld();
        else FocusOnColony();
    }

    /// <summary>Starts a new colony: a fresh world from this seed and size (the scene reloads onto it).</summary>
    public void NewColony(int seed, int size)
    {
        s_newWorld = (seed, size, size, WorldHeight);
        s_home = null;
        s_launch = null;
        s_launchResult = null;
        GetTree().ReloadCurrentScene();
    }

    private static (int Seed, int W, int D, int H)? s_newWorld;
    private GameUi _ui = null!;

    // ---- Test menu ------------------------------------------------------------------------------
    // Opening a world (a test's, or the colony again) hands its sim to the next Main through these statics and
    // reloads the scene, so every view starts fresh on it. The colony waits in s_home while you look at tests.

    private static ColonySim? s_launch, s_home;
    private static TestResult? s_launchResult;
    private static bool s_devArgsDone;
    private TestResult? _test; // the test whose world this is, if any

    public bool InTestWorld => _test != null;
    public bool CanRunDemos => _test == null && _sim.Colonists.Count > 0 && _sim.Terrain != null;

    /// <summary>Opens the world a test built (the colony is kept to come back to).</summary>
    public void ShowTestWorld(TestResult result)
    {
        if (result.Sim == null) return;
        if (_test == null) s_home = _sim;
        s_launch = result.Sim;
        s_launchResult = result;
        GetTree().ReloadCurrentScene();
    }

    /// <summary>Back from a test's world to the colony, as it was left.</summary>
    public void ReturnHome()
    {
        if (s_home == null) return;
        s_launch = s_home;
        s_home = null;
        s_launchResult = null;
        GetTree().ReloadCurrentScene();
    }

    /// <summary>Runs a dev demo in the current colony (the same as its --xxx-demo flag).</summary>
    public void RunDemo(string id)
    {
        if (!CanRunDemos) return;
        _sim.WaitForWater();
        _stairsDemoAt = null;
        switch (id)
        {
            case "dig": DigDemo(); break;
            case "stairs": StairsDemo(); break;
            case "haul": HaulDemo(); break;
            case "factory": FactoryDemo(); break;
            case "parts": PartsDemo(); break;
            case "showroom": ShowroomDemo(""); break;
            case "water": WaterDemo(); break;
            case "homes": HomesDemo(); break;
        }
        if (_stairsDemoAt is { } s) _rig.Focus(SimSpace.TileCenter(s), 12f, yaw: 200f, pitch: -55f);
    }

    /// <summary>A test's world is small: frame all of it.</summary>
    private void FocusOnTestWorld()
    {
        var w = _sim.World;
        float size = Math.Max(w.Width, w.Depth);
        _rig.Focus(new Vector3(w.Width / 2f, w.Height / 2f, w.Depth / 2f), size * 1.2f + 12f, yaw: 205f, pitch: -50f);
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
            case Key.Home when !key.Echo: FocusHome(); break;
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

        if (!_paused && !_ui.MenuOpen && _sim.WaterReady) // the world starts once its water has settled (and waits while the main menu is up)
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
            _simMsTotal += _simMsThisFrame;
            _maxSimMs = System.Math.Max(_maxSimMs, _simMsThisFrame);
        }

    }

    /// <summary>
    /// Top of the ground you can see in a column (Godot Y): the highest solid tile at or below the view level,
    /// i.e. the cut surface once levels are peeled away. Null over open void (keeps the camera height).
    /// </summary>
    private float? VisibleGround(float gx, float gz)
    {
        var world = _sim.World;
        int x = (int)Mathf.Floor(gx), y = (int)Mathf.Floor(gz);
        if (x < 0 || y < 0 || x >= world.Width || y >= world.Depth) return null;
        int top = System.Math.Min(_world.ViewLevel, world.Height - 1);
        // Start just above the generated surface when that's lower (digging only lowers ground).
        if (_sim.Terrain is { } t) top = System.Math.Min(top, t.SurfaceAt(x, y) + 8);
        for (int z = top; z >= System.Math.Max(0, top - 128); z--)
            if (world.IsSolid(x, y, z)) return z + 1;
        return null;
    }

    // ---- Dev aids -------------------------------------------------------------------------------
    // godot --path game -- [--size=WxDxH] [--seed=N] [--level=N] [--look=X,Y (camera on that column)] [--dig-demo] [--stairs-demo] [--ticks=N]
    //                      [--zoom=DIST] [--screenshot=out.png] [--bench]
    // With --screenshot or --bench: waits until the world is fully meshed, measures 120 frames, prints a
    // performance report, saves the screenshot (if asked) and quits.

    private const int BenchFrames = 120;
    private int _frames;
    private TilePos? _stairsDemoAt;
    private ulong _startMs, _firstFrameMs;
    private bool _benchActive;
    private ulong _meshedAtMs;
    private readonly List<double> _frameMs = new();
    private double _maxSimMs, _simMsThisFrame, _simMsTotal;

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
        if (s_newWorld is { } fresh) // the main menu's "New colony"
        {
            (Seed, WorldWidth, WorldDepth, WorldHeight) = fresh;
            s_newWorld = null;
            s_devArgsDone = true; // the demos and dev flags were for the first world
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
            _firstFrameMs = Time.GetTicksMsec();
            if (int.TryParse(Arg("--level"), out int level)) _world.ViewLevel = level;
            if (Arg("--test-menu") != null) _ui.OpenWindow("tests");
            if (Arg("--work-menu") != null) _ui.OpenWindow("work");
            if (Arg("--open") is { Length: > 0 } open) foreach (var id in open.Split(',')) _ui.OpenWindow(id);
            if (Arg("--main-menu") != null) _ui.ShowMenu(true);
            if (Arg("--ui-selftest") != null) AddChild(new UiSelfTest(_ui));
            // World-editing flags apply once, to the first world (not again when the test menu reloads the scene).
            if (!s_devArgsDone)
            {
                s_devArgsDone = true;
                // The dev demos below edit the world, which must wait for the water to settle (normal play doesn't).
                if (new[] { "--dig-demo", "--stairs-demo", "--haul-demo", "--factory-demo", "--parts-demo", "--showroom", "--water-demo", "--homes-demo", "--ticks" }.Any(a => Arg(a) != null))
                    _sim.WaitForWater();
                if (Arg("--dig-demo") != null) DigDemo();
                if (Arg("--stairs-demo") != null) StairsDemo();
                if (Arg("--haul-demo") != null) HaulDemo();
                if (Arg("--factory-demo") != null) FactoryDemo();
                if (Arg("--parts-demo") != null) PartsDemo();
                if (Arg("--showroom") is { } showroom) ShowroomDemo(showroom);
                if (Arg("--build-menu") is { } menu) _ui.OpenBuild(menu);
                if (Arg("--water-demo") != null) WaterDemo();
                if (Arg("--homes-demo") != null) HomesDemo();
                if (int.TryParse(Arg("--ticks"), out int ticks))
                    for (int i = 0; i < ticks; i++) _sim.Step();
                if (_stairsDemoAt is { } s) _rig.Focus(SimSpace.TileCenter(s), 12f, yaw: 200f, pitch: -55f);
                // --test=NAME [--test-watch]: run the first test whose name contains NAME and open its world.
                if (Arg("--test") is { Length: > 0 } testName)
                {
                    var test = TestRunner.Discover().FirstOrDefault(c => c.FullName.Contains(testName, StringComparison.OrdinalIgnoreCase));
                    if (test == null) GD.PrintErr($"No test matches '{testName}'");
                    else
                    {
                        var result = TestRunner.RunNow(test, Arg("--test-watch") != null);
                        GD.Print($"Test {test.FullName}: {result.Message} ({result.Ms:0} ms)");
                        if (result.Sim != null)
                        {
                            ShowTestWorld(result);
                            _frames = 0; // this scene is going; the next one runs the bench flags
                            return;
                        }
                    }
                }
            }
            if (float.TryParse(Arg("--zoom"), out float zoom)) _rig.Focus(_world.Center, zoom, yaw: 200f, pitch: -50f);
            if (Arg("--look")?.Split(',') is [var lx, var ly] && int.TryParse(lx, out int lookX) && int.TryParse(ly, out int lookY))
                _rig.Focus(SimSpace.TileCenter(new TilePos(lookX, lookY, _sim.World.SurfaceLevel(lookX, lookY))), float.TryParse(Arg("--zoom"), out float lz) ? lz : 60f, yaw: 200f, pitch: -55f);
            _benchActive = Arg("--screenshot") != null || Arg("--bench") != null;
        }
        if (!_benchActive) return;

        if (_meshedAtMs == 0)
        {
            if (_frames > 2 && _world.PendingSections == 0 && _flora.PendingRegions == 0 && _waterView.PendingSections == 0)
            {
                _meshedAtMs = Time.GetTicksMsec();
                _maxSimMs = 0;
                _waterView.MeshMs = _waterView.CapMs = 0;
                _waterView.Frames = _waterView.SectionsBuilt = 0;
                _simMsTotal = 0;
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
        GD.Print($"[bench] fully meshed {_meshedAtMs - _startMs} ms after start; {_world.SectionCount} sections, {_world.VisibleQuads} quads " +
                 $"(main thread: snapshots {_world.SnapshotMs:0} ms, uploads {_world.UploadMs:0} ms; workers: meshing {_world.WorkerMs:0} ms; first frame at {_firstFrameMs - _startMs} ms)");
        GD.Print($"[bench] frame ms over {BenchFrames} frames: avg {avg:0.00}, p95 {_frameMs[(int)(_frameMs.Count * 0.95)]:0.00}, max {_frameMs[^1]:0.00}; max sim step batch {_maxSimMs:0.00} ms");
        int wf = Math.Max(1, _waterView.Frames);
        GD.Print($"[bench] per frame: sim {_simMsTotal / _frameMs.Count:0.00} ms, water meshing {_waterView.MeshMs / wf:0.00} ms ({_waterView.SectionsBuilt} sections built, backlog {_waterView.Backlog}), water cap {_waterView.CapMs / wf:0.00} ms; world view {_world.SnapshotMs:0} ms snapshots total");
        if (GlyphManager.Instance is { } gm && gm.TickMs.Count > 0) GD.Print("[bench] worst UI window updates (ms): " + string.Join(", ", gm.TickMs.Select(kv => $"{kv.Key} {kv.Value:0.00}")));
        GD.Print($"[bench] draw calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)}, " +
                 $"video mem {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576:0} MB, " +
                 $"static mem {OS.GetStaticMemoryUsage() / 1048576} MB, managed {System.GC.GetTotalMemory(false) / 1048576} MB");

        if (Arg("--screenshot") is { Length: > 0 } path)
        {
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"Saved screenshot to {path}");
        }
        _benchActive = false; // Quit takes effect a few frames later
        GetTree().Quit();
    }

    /// <summary>Designates a room into the mountain's flank at the current level.</summary>
    private void DigDemo()
    {
        int z = _world.ViewLevel, cx = _sim.World.Width / 2, cy = _sim.World.Depth / 2;
        _sim.Designations.DesignateBox(new TilePos(cx - 6, cy - 4, z), new TilePos(cx + 6, cy + 4, z));
    }

    /// <summary>
    /// A whole chain next to the colony: fell the woods, stockpile the logs, a sawmill (two benches, a tool rack,
    /// a lantern, input and output crates) and a stockpile for the planks.
    /// </summary>
    private void FactoryDemo()
    {
        HaulDemo();
        var c = _sim.Colonists[0].Position;
        int g = c.Z - 1; // ground under the colonists
        var rooms = _sim.Rooms;
        int cells = rooms.Paint(Colony.Core.Industry.RoomType.Carpentry, new TilePos(c.X + 2, c.Y - 3, g - 4), new TilePos(c.X + 8, c.Y + 2, g));
        // The ground here is uneven, so put each part on the first floor cell (in a fixed order) where it fits,
        // spreading them out by trying cells from different ends of the room.
        var room = rooms.All.OrderByDescending(r => r.Cells.Count).First();
        var floor = room.Cells.OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
        // Tries the asked size first, then smaller ones if nothing that big fits.
        void Put(Colony.Core.Industry.PartType t, bool fromEnd, int size = 1)
        {
            var order = fromEnd ? Enumerable.Reverse(floor).ToList() : floor;
            for (int s = size; s >= 1; s--)
            foreach (var cell in order)
                if (rooms.Place(t, cell, false, s) != null || rooms.Place(t, cell, true, s) != null) return;
        }
        Put(Colony.Core.Industry.PartType.InputCrate, false);
        Put(Colony.Core.Industry.PartType.OutputCrate, true);
        Put(Colony.Core.Industry.PartType.Sawbench, false);
        Put(Colony.Core.Industry.PartType.Sawbench, true, size: 2); // costs planks: built once the sawmill runs
        Put(Colony.Core.Industry.PartType.Sawbench, true);
        Put(Colony.Core.Industry.PartType.ToolRack, false);
        Put(Colony.Core.Industry.PartType.ToolRack, true);
        Put(Colony.Core.Industry.PartType.Lantern, true);
        int planks = _sim.Stockpiles.AddFloorBox(new TilePos(c.X + 2, c.Y + 5, g - 6), new TilePos(c.X + 6, c.Y + 7, g));
        GD.Print($"Factory demo: sawmill of {cells} cells with {rooms.All.Sum(r => r.Parts.Count)} parts, plank stockpile {planks} cells");
        _stairsDemoAt = new TilePos(c.X + 5, c.Y, c.Z);
    }

    /// <summary>
    /// A showroom next to the colony: levels a pad (dev only: edits tiles directly), paints a sawmill on it and
    /// lays out every size of saw (back row) and tools (front row), with crates and a lantern.
    /// </summary>
    private void PartsDemo()
    {
        var c = _sim.Colonists[0].Position;
        int g = c.Z - 1, x0 = c.X + 10, y0 = c.Y - 6, x1 = x0 + 25, y1 = y0 + 12;
        for (int y = y0 - 8; y <= y1 + 8; y++)
        for (int px = x0 - 8; px <= x1 + 8; px++)
        {
            for (int z = g - 3; z <= g; z++) _sim.World.Set(px, y, z, z == g ? TileType.Grass : TileType.Soil);
            for (int z = g + 1; z <= g + 16; z++)
            {
                _sim.World.Set(px, y, z, TileType.Air);
                _sim.Flora.Remove(new TilePos(px, y, z), out _);
            }
        }
        var rooms = _sim.Rooms;
        rooms.InstantBuild = true; // a showroom, not a construction site
        rooms.Paint(Colony.Core.Industry.RoomType.Carpentry, new TilePos(x0, y0, g), new TilePos(x1, y1, g));
        int f = g + 1, x = x0 + 1;
        for (int s = 1; s <= 5; s++)
        {
            rooms.Place(Colony.Core.Industry.PartType.Sawbench, new TilePos(x, y0 + 1, f), false, s);
            x += Colony.Core.Industry.PartInfo.Footprint(Colony.Core.Industry.PartType.Sawbench, s).W + 1;
        }
        x = x0 + 1;
        for (int s = 1; s <= 5; s++)
        {
            rooms.Place(Colony.Core.Industry.PartType.ToolRack, new TilePos(x, y0 + 7, f), false, s);
            x += Colony.Core.Industry.PartInfo.Footprint(Colony.Core.Industry.PartType.ToolRack, s).W + 1;
        }
        rooms.Place(Colony.Core.Industry.PartType.InputCrate, new TilePos(x0, y1, f), false);
        rooms.Place(Colony.Core.Industry.PartType.OutputCrate, new TilePos(x1, y1, f), false);
        rooms.Place(Colony.Core.Industry.PartType.Lantern, new TilePos(x1, y0 + 7, f), false);
        rooms.InstantBuild = false;
        _rig.Focus(SimSpace.TileCenter(new TilePos((x0 + x1) / 2, (y0 + y1) / 2, f)), 17f, yaw: 200f, pitch: -58f);
        GD.Print($"Parts demo: {rooms.All.Sum(r => r.Parts.Count)} parts, efficiency {rooms.All[0].Efficiency:P0}");
    }

    /// <summary>
    /// A small waterworks next to the colony (dev only: edits tiles directly): a spring-fed channel on a stone
    /// terrace falls onto a water wheel and runs on to a large pond (the spring never stops, so after some minutes
    /// it overflows: dam or drain it); a pump powered by the wheel lifts pond water into a ditch beside a wheat
    /// field (irrigated, so it grows twice as fast).
    /// </summary>
    private void WaterDemo()
    {
        var c = _sim.Colonists[0].Position;
        int g = c.Z - 1, x0 = c.X + 8, yc = c.Y + 12;
        LevelPad(x0 - 30, yc - 30, x0 + 75, yc + 30, g);
        void Set(int x0_, int y0, int z0, int x1, int y1, int z1, TileType t)
        {
            for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            for (int x = x0_; x <= x1; x++) _sim.World.Set(x, y, z, t);
        }
        // Terrace (top at g+4) with a walled channel along y = yc on top; it ends at the terrace edge x0+12.
        Set(x0, yc - 3, g + 1, x0 + 12, yc + 3, g + 4, TileType.Stone);
        Set(x0, yc - 1, g + 5, x0 + 12, yc - 1, g + 6, TileType.Wall);
        Set(x0, yc + 1, g + 5, x0 + 12, yc + 1, g + 6, TileType.Wall);
        Set(x0, yc, g + 5, x0, yc, g + 6, TileType.Wall);
        _sim.Water.AddSpring(new TilePos(x0 + 1, yc, g + 5));
        // Below the fall, a walled channel at ground level runs on to a pond dug three deep.
        Set(x0 + 13, yc - 1, g + 1, x0 + 30, yc - 1, g + 3, TileType.Wall);
        Set(x0 + 13, yc + 1, g + 1, x0 + 30, yc + 1, g + 3, TileType.Wall);
        Set(x0 + 13, yc - 1, g + 4, x0 + 14, yc - 1, g + 6, TileType.Wall); // a flume around the fall
        Set(x0 + 13, yc + 1, g + 4, x0 + 14, yc + 1, g + 6, TileType.Wall);
        Set(x0 + 14, yc, g + 4, x0 + 14, yc, g + 6, TileType.Wall);
        Set(x0 + 31, yc - 6, g - 3, x0 + 50, yc + 6, g, TileType.Air);
        Set(x0 + 31, yc - 6, g - 4, x0 + 50, yc + 6, g - 4, TileType.Stone);
        // The wheel turns in the falling water; the pump in the pond's far corner lifts water east, onto the
        // bank, where it runs into a ditch alongside a wheat field.
        _sim.Waterworks.AddWheel(_sim.Water, new TilePos(x0 + 13, yc, g + 4), 0); // in the free fall, above the pool
        _sim.Waterworks.AddPump(new TilePos(x0 + 50, yc, g), 0);
        Set(x0 + 52, yc, g, x0 + 66, yc, g, TileType.Air);
        _sim.Farms.AddFloorBox(Colony.Core.Nature.PlantKind.Wheat, new TilePos(x0 + 52, yc - 3, g), new TilePos(x0 + 66, yc - 1, g));
        _sim.Farms.AddFloorBox(Colony.Core.Nature.PlantKind.Wheat, new TilePos(x0 + 52, yc + 1, g), new TilePos(x0 + 66, yc + 3, g));
        foreach (var cell in _sim.Farms.Cells)
            if (_sim.Farms.TryGet(cell, out var crop))
                _sim.Flora.Add(new Colony.Core.Nature.Plant(cell, crop, (byte)(60 + (cell.X * 37 + cell.Y * 11) % 120), (byte)(cell.X * 7 + cell.Y)));
        _stairsDemoAt = null;
        _rig.Focus(SimSpace.TileCenter(new TilePos(x0 + 30, yc, g + 1)), 34f, yaw: 200f, pitch: -48f);
        GD.Print($"Water demo: spring at ({x0 + 1},{yc},{g + 5}), wheel, pump, pond and irrigated field");
    }

    /// <summary>
    /// Four walled homes beside the colony (dev only: edits tiles, installs fixtures directly): a dormitory, a cottage,
    /// a house and a manor, each furnished by its policy so the tiers and the home-vs-dormitory split are visible.
    /// </summary>
    private void HomesDemo()
    {
        var c = _sim.Colonists[0].Position;
        int g = c.Z - 1, x = c.X + 6, y0 = c.Y - 4;
        LevelPad(x - 2, y0 - 2, x + 44, y0 + 10, g);
        var housing = _sim.Housing;
        foreach (var (name, w, d, material, floor, roof) in new[] { ("Dormitory", 7, 5, 0, 0, 1), ("Cottage", 4, 3, 2, 1, 2), ("House", 6, 4, 3, 2, 3), ("Manor", 8, 6, 4, 3, 4) })
        {
            // Thin walls round the floor: a door in the middle of the south wall, windows along the north and sides.
            byte wall = EdgeInfo.Make(EdgeKind.Wall, material), window = EdgeInfo.Make(EdgeKind.Window, material);
            int z = g + 1;
            for (int i = 0; i < w; i++)
            {
                _sim.World.SetEdge(new TilePos(x + i, y0 - 1, z), EdgeInfo.North, i == w / 2 ? EdgeInfo.Make(EdgeKind.Door, material) : wall);
                _sim.World.SetEdge(new TilePos(x + i, y0 + d - 1, z), EdgeInfo.North, i % 3 == 1 ? window : wall);
            }
            for (int j = 0; j < d; j++)
            {
                _sim.World.SetEdge(new TilePos(x - 1, y0 + j, z), EdgeInfo.East, j == d / 2 && d > 3 ? window : wall);
                _sim.World.SetEdge(new TilePos(x + w - 1, y0 + j, z), EdgeInfo.East, j == d / 2 ? window : wall);
            }
            for (int i = 0; i < w; i++)
            for (int j = 0; j < d; j++)
            {
                if (floor > 0) _sim.World.SetFloor(new TilePos(x + i, y0 + j, z), floor);
                _sim.World.SetRoof(new TilePos(x + i, y0 + j, z), roof);
            }
            _sim.Rooms.Paint(Colony.Core.Industry.Catalog.Home, new TilePos(x, y0, g), new TilePos(x + w - 1, y0 + d - 1, g));
            _sim.Step(); // housing picks the room up
            var home = housing.HomeOf(_sim.Rooms.RoomAt(new TilePos(x, y0, g + 1))!)!;
            housing.SetPolicy(home, housing.Policies.First(p => p.Name == name));
            _sim.Step();
            var plan = Colony.Core.Agents.Housing.Plan(home);
            for (int s = 0; s < Colony.Core.Agents.Housing.SlotCount; s++)
                if (plan.Count[s] > 0)
                {
                    var item = Colony.Core.Industry.Catalog.Fixtures[Colony.Core.Agents.Housing.Slots[s]].First(i => i.Def.FixtureTier == plan.Tier[s]);
                    housing.Install(_sim, home, item, plan.Count[s]);
                }
            x += w + 2;
        }
        _rig.Focus(SimSpace.TileCenter(new TilePos(c.X + 20, y0 + 2, g + 1)), 22f, yaw: 190f, pitch: -62f);
        GD.Print($"Homes demo: {housing.All.Count} homes, {housing.All.Sum(h => h.Beds)} beds, {housing.All.Sum(h => h.Fixtures.Count)} fixtures, floor level {g + 1} (Z/X to look inside)");
    }

    /// <summary>Dev only: flattens a pad of grass over soil at ground level g (edits tiles directly).</summary>
    private void LevelPad(int x0, int y0, int x1, int y1, int g, int clear = 16)
    {
        for (int y = y0; y <= y1; y++)
        for (int px = x0; px <= x1; px++)
        {
            if (!_sim.World.InBounds(px, y, g)) continue;
            for (int z = g - 3; z <= g; z++) _sim.World.Set(px, y, z, z == g ? TileType.Grass : TileType.Soil);
            // Everything above goes, up to the old surface (hills included), and so do plants that stood on the old
            // ground, higher or lower, which would be left floating or buried.
            int top = Math.Max(g + clear, _sim.World.SurfaceLevel(px, y) + 1);
            for (int z = g + 1; z <= top && z < _sim.World.Height; z++) _sim.World.Set(px, y, z, TileType.Air);
            for (int z = Math.Max(1, g - 48); z <= top + 1 && z < _sim.World.Height; z++) _sim.Flora.Remove(new TilePos(px, y, z), out _);
        }
    }

    /// <summary>
    /// Every workshop in a grid on a levelled pad, each holding every one of its stations in sizes 1-5 (one row
    /// per station), crates, tools and a lantern; a stockpile of raw resources so the first recipes run; and
    /// farm plots for each crop (glowcaps under a stone roof). <c>--showroom=ID</c> focuses one workshop.
    /// </summary>
    private void ShowroomDemo(string focus)
    {
        var c = _sim.Colonists[0].Position;
        int g = c.Z - 1, cols = 4;
        const int cellW = 24, cellD = 22;
        var workshops = Colony.Core.Industry.Catalog.Workshops;
        int rows = (workshops.Count + cols - 1) / cols;
        int ox = c.X + 6, oy = c.Y - rows * cellD / 2;
        LevelPad(ox - 40, oy - 40, ox + cols * cellW + 30, oy + rows * cellD + 30, g); // wide margins: room for the camera
        var rooms = _sim.Rooms;
        rooms.InstantBuild = true; // a showroom, not a construction site
        int f = g + 1;
        TilePos? focusAt = null;
        for (int i = 0; i < workshops.Count; i++)
        {
            var w = workshops[i];
            int x0 = ox + i % cols * cellW, y0 = oy + i / cols * cellD;
            int stations = w.Stations.Count;
            int y1 = y0 + 4 * stations + 2;
            rooms.Paint(w, new TilePos(x0, y0, g), new TilePos(x0 + cellW - 3, y1, g));
            for (int s = 0; s < stations; s++)
            {
                int x = x0 + 1;
                for (int size = 1; size <= Colony.Core.Industry.PartInfo.MaxSize; size++)
                {
                    rooms.Place(w.Stations[s], new TilePos(x, y0 + 1 + s * 4, f), false, size);
                    x += Colony.Core.Industry.PartInfo.Footprint(w.Stations[s], size).W + 1;
                }
            }
            for (int k = 0; k < 4; k++) rooms.Place(Colony.Core.Industry.PartType.InputCrate, new TilePos(x0 + k, y1, f), false);
            for (int k = 0; k < 2; k++) rooms.Place(Colony.Core.Industry.PartType.OutputCrate, new TilePos(x0 + cellW - 3 - k, y1, f), false);
            rooms.Place(Colony.Core.Industry.PartType.ToolRack, new TilePos(x0 + 6, y1, f), false, 3);
            rooms.Place(Colony.Core.Industry.PartType.Lantern, new TilePos(x0 + 10, y1, f), false);
            if (w.Id == focus) focusAt = new TilePos(x0 + 10, y0 + 2 * stations, f);
        }
        rooms.InstantBuild = false;

        // Raw goods next to the colony, so the unskilled first recipes start running.
        int sx = ox - 11, sy = oy;
        _sim.Stockpiles.AddFloorBox(new TilePos(sx, sy, g), new TilePos(sx + 7, sy + 11, g));
        int slot = 0;
        foreach (var item in Colony.Core.Industry.Catalog.Items)
        {
            if (!item.IsRaw) continue;
            _sim.Items.Drop(new TilePos(sx + slot % 8, sy + slot / 8, f), item.Type, item.StackLimit);
            slot++;
        }
        // Farms: grain and herbs in the open, glowcaps under a stone roof.
        _sim.Farms.AddFloorBox(Colony.Core.Nature.PlantKind.Wheat, new TilePos(sx, sy + 13, g), new TilePos(sx + 7, sy + 16, g));
        _sim.Farms.AddFloorBox(Colony.Core.Nature.PlantKind.Herbs, new TilePos(sx, sy + 18, g), new TilePos(sx + 7, sy + 20, g));
        for (int y = sy + 22; y <= sy + 26; y++)
        for (int x = sx; x <= sx + 7; x++) _sim.World.Set(x, y, g + 4, TileType.Stone);
        _sim.Farms.AddFloorBox(Colony.Core.Nature.PlantKind.Glowcap, new TilePos(sx + 1, sy + 23, g), new TilePos(sx + 6, sy + 25, g));
        // Grown crops straight away, so the models show (they are harvested and resown as usual).
        foreach (var cell in _sim.Farms.Cells)
            if (_sim.Farms.TryGet(cell, out var crop))
                _sim.Flora.Add(new Colony.Core.Nature.Plant(cell, crop, (byte)(80 + (cell.X * 37 + cell.Y * 11) % 176), (byte)(cell.X * 7 + cell.Y)));

        var at = focusAt ?? new TilePos(ox + cols * cellW / 2, oy + rows * cellD / 2, f);
        _rig.Focus(SimSpace.TileCenter(at), focusAt is null ? 70f : 20f, yaw: 200f, pitch: -58f);
        GD.Print($"Showroom: {workshops.Count} workshops, {rooms.All.Sum(r => r.Parts.Count)} parts, {_sim.Farms.Count} farm plots");
    }

    /// <summary>Fells the woods around the colony and zones a stockpile next to it.</summary>
    private void HaulDemo()
    {
        var c = _sim.Colonists[0].Position;
        int chopped = _sim.Designations.DesignateBox(new TilePos(c.X - 16, c.Y - 16, c.Z - 8), new TilePos(c.X + 16, c.Y + 16, c.Z + 8),
            Colony.Core.Orders.DesignationKind.Chop);
        // Only surface ground qualifies (a buried tile has no walkable cell above it), so a tall box is fine.
        int cells = _sim.Stockpiles.AddFloorBox(new TilePos(c.X - 7, c.Y - 3, c.Z - 6), new TilePos(c.X - 2, c.Y + 3, c.Z));
        _stairsDemoAt = c;
        GD.Print($"Haul demo: {chopped} trees to chop, {cells} stockpile cells");
    }

    /// <summary>Designates a 1x1 stair shaft 6 levels down next to the colonists, with a small room at the bottom.</summary>
    private void StairsDemo()
    {
        var c = _sim.Colonists[0].Position;
        var a = new TilePos(c.X + 3, c.Y + 3, _sim.World.SurfaceLevel(c.X + 3, c.Y + 3));
        var bottom = a.Offset(0, 0, -5);
        _sim.Designations.DesignateStairs(a, bottom);
        _sim.Designations.DesignateBox(bottom.Offset(1, -1, 0), bottom.Offset(3, 1, 0));
        _stairsDemoAt = bottom.Offset(1, 0, 0);
        GD.Print($"Stairs demo: shaft at ({a.X},{a.Y}), levels {a.Z}..{bottom.Z}, room east of the bottom");
    }
}
