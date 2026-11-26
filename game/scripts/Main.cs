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
    /// <summary>How many colonies the world starts with (one per player; --colonies=N).</summary>
    [Export] public int Colonies { get; set; } = 1;

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
        DwarfRender.DwarfMaterials.ClearTerrains(); // the views below register this scene's tile materials
        GetViewport().Disable3D = false; // the pixel-art renderer turns it off; a reload into the classic look needs it back
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

        AddChild(_sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, 215, 0), // behind the default view (rig yaw 200): we see the lit faces
            LightEnergy = 1.1f,
            ShadowEnabled = true,
            Name = "Sun",
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
                settleWaterInBackground: true, factions: Colonies); // settles while the terrain is meshed for the first frames
            GD.Print($"World {WorldWidth}x{WorldDepth}x{WorldHeight} built in {Time.GetTicksMsec() - start} ms: {_sim.StartupReport}");
            RememberSettings();
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

        var racks = new RackView { Name = "RackView" };
        AddChild(racks);
        racks.Initialize(_sim, _world);
        var belts = new ConveyorView { Name = "ConveyorView" };
        AddChild(belts);
        belts.Initialize(_sim, _world);

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
        var creatures = new CreatureView { Name = "CreatureView" };
        AddChild(creatures);
        creatures.Initialize(_sim, _world, () => Player);

        _tool = GetNode<DesignationTool>("DesignationTool");
        _tool.Initialize(_sim, _world);
        _tool.StructurePreview = _waterView.ShowPreview;
        Player = _tool.Player = Math.Clamp(s_player, 0, _sim.Factions.Count - 1); // kept across test-world visits

        _rig = GetNode<CameraRig>("CameraRig");
        _rig.GroundHeight = VisibleGround;
        if (_test != null) FocusOnTestWorld();
        else FocusOnColony();

        Selection = new SelectionView { Name = "SelectionView" };
        AddChild(Selection);
        Selection.Initialize(_sim, _world, _rig, _tool);

        var falls = new FallView { Name = "FallView" };
        AddChild(falls);
        falls.Initialize(_sim, _world, _rig);
        var mistView = new MistView { Name = "MistView" };
        AddChild(mistView);
        mistView.Initialize(_sim, _world, _rig);

        _ui = new GameUi(this) { Name = "GameUi" };
        AddChild(_ui);
        ReadyNet();
        SetUpRenderer();
    }

    // ---- What the UI reads and drives ----------------------------------------------------------------

    public ColonySim Sim => _sim;
    public DesignationTool Tool => _tool;
    public WorldView View => _world;
    /// <summary>What the player clicked in the world (Inspect shows it).</summary>
    public SelectionView Selection { get; private set; } = null!;
    public bool Paused { get => _paused; set => _paused = value; }
    public static IReadOnlyList<int> SpeedSteps => Speeds;
    public int SpeedIndex { get => _speedIndex; set => _speedIndex = Math.Clamp(value, 0, Speeds.Length - 1); }
    public double SimMs => _simMsThisFrame;
    public TestResult? Test => _test;
    public int PendingSections => _world.PendingSections;

    /// <summary>Looks at a tile from close up (keeps the camera's heading).</summary>
    public void FocusOn(TilePos p) => _rig.Focus(SimSpace.TileCenter(p), 18f);

    /// <summary>The colony, or the whole test world when one is open.</summary>
    /// <summary>The colony this player controls (commands are issued as it; the UI shows it).</summary>
    public int Player { get; private set; }

    /// <summary>Hot-seat (F4, until networking): play as another colony in this world.</summary>
    public void PlayAs(int faction)
    {
        Player = s_player = Math.Clamp(faction, 0, _sim.Factions.Count - 1);
        _tool.Player = Player;
        _tool.CancelPlacement();
        UI.GlyphStatus.Say($"Playing as {_sim.Factions[Player].Name} (F4: next colony)");
        FocusOnColony();
    }

    public void FocusHome()
    {
        if (_test != null) FocusOnTestWorld();
        else FocusOnColony();
    }

    /// <summary>Starts a new colony: a fresh world from this seed and size (the scene reloads onto it).</summary>
    public void NewColony(int seed, int size, int colonies = 1)
    {
        s_newWorld = (seed, size, size, WorldHeight, colonies);
        s_player = 0;
        s_home = null;
        s_launch = null;
        s_launchResult = null;
        GetTree().ReloadCurrentScene();
    }

    private static (int Seed, int W, int D, int H, int Colonies)? s_newWorld;
    private static int s_player;
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
        if (!CanRunDemos || Online) return;
        _sim.WaitForWater();
        _stairsDemoAt = null;
        switch (id)
        {
            case "dig": DigDemo(""); break;
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
            case Key.Pageup or Key.X when !key.ShiftPressed: _world.ViewLevel++; break; // Shift+X: the cancel order
            case Key.Pagedown or Key.Z when !key.ShiftPressed: _world.ViewLevel--; break;
            case Key.Space when !key.Echo && s_client == null: _paused = !_paused; break;
            case Key.Key1 when s_client == null: _speedIndex = 0; _paused = false; break;
            case Key.Key2 when s_client == null: _speedIndex = 1; _paused = false; break;
            case Key.Key3 when s_client == null: _speedIndex = 2; _paused = false; break;
            case Key.Home when !key.Echo: FocusHome(); break;
            case Key.F5 when !key.Echo: QuickSave(); break;
            case Key.F9 when !key.Echo && s_client == null: QuickLoad(); break;
            case Key.F4 when !key.Echo && _sim.Factions.Count > 1: PlayAs((Player + 1) % _sim.Factions.Count); break;
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
        var mine = _sim.Colonists.Where(c => c.Faction == Player).ToList();
        if (mine.Count == 0) mine = _sim.Colonists.ToList();
        foreach (var c in mine) sum += SimSpace.TileCenter(c.Position);
        // Colonists start south of the mountain; look at them from the valley so the slope does not block the view.
        _rig.Focus(sum / mine.Count, 22f, yaw: 200f);
    }

    public override void _Process(double delta)
    {
        StutterLog(delta);
        OrbitCheck();
        HandleDevArgs(delta);
        UpdateRenderer();
        GameLooks.Tick(delta);

        // A client's sim runs only what the host confirms; everyone else runs at their own speed (a host also streams turns).
        if (!ClientFrame())
        {
            s_host?.Poll();
            // The world starts once its water has settled; it waits while the main menu is up (unless others play in it).
            if (!_paused && (!_ui.MenuOpen || Online) && _sim.WaterReady)
            {
                // Fixed-rate simulation; cap catch-up so a long frame can't snowball.
                _tickAccumulator += delta * Speeds[_speedIndex];
                int budget = 20 * Speeds[_speedIndex];
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (_tickAccumulator >= TickSeconds && budget-- > 0)
                {
                    _sim.Step();
                    s_host?.AfterTick();
                    _tickAccumulator -= TickSeconds;
                }
                if (budget <= 0) _tickAccumulator = 0;
                _simMsThisFrame = clock.Elapsed.TotalMilliseconds;
                _simMsTotal += _simMsThisFrame;
                _maxSimMs = System.Math.Max(_maxSimMs, _simMsThisFrame);
            }
            s_host?.Broadcast();
            HostNetCheckQuit();
        }
        NetOrders();
        SaveFrame();
        SaveDevArgs();
        NetCheck();
        NetDesyncTest();
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

    public override void _ExitTree() => UnhookNet();

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
            if (arg.StartsWith("--colonies=") && int.TryParse(arg["--colonies=".Length..], out int colonies)) Colonies = Math.Clamp(colonies, 1, 8);
        }
        if (s_newWorld is { } fresh) // the main menu's "New colony"
        {
            (Seed, WorldWidth, WorldDepth, WorldHeight, Colonies) = fresh;
            s_newWorld = null;
            s_devArgsDone = true; // the demos and dev flags were for the first world
        }
    }

    internal static string? Arg(string name)
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
            if (Arg("--main-menu") is { } menuPage) _ui.ShowMenu(true, menuPage);
            if (Arg("--orders") != null) _ui.Ribbon.Pinned = true; // the orders ribbon open
            // UI checks: a tool in hand (--tool=Dig, --tool=Edge, ...) and the mouse parked on the screen (for the hover card).
            if (Arg("--tool") is { Length: > 0 } toolName && Enum.TryParse<DesignationTool.ToolMode>(toolName, true, out var toolMode))
            {
                if (toolMode == DesignationTool.ToolMode.Edge) Tool.SelectWall(Colony.Core.Orders.StructureKind.EdgeWall, 2);
                else Tool.SelectOrder(toolMode);
            }
            if (Arg("--mouse")?.Split(',') is [var mx, var my] && float.TryParse(mx, out float mouseX) && float.TryParse(my, out float mouseY))
                GetViewport().WarpMouse(new Vector2(mouseX, mouseY));
            if (Arg("--tip") is { Length: > 0 } tip) UI.GlyphStatus.Tip = tip.Replace("\\n", "\n"); // the tooltip box, at the mouse
            if (int.TryParse(Arg("--play-as"), out int playAs)) PlayAs(playAs);
            if (Arg("--dev") != null) Tool.Issue(new Colony.Core.Commands.SetDevMode(true, true)); // dev mode from the start
            if (Arg("--ui-selftest") != null) AddChild(new UiSelfTest(_ui));
            // World-editing flags apply once, to the first world (not again when the test menu reloads the scene).
            if (!s_devArgsDone)
            {
                s_devArgsDone = true;
                // The dev demos below edit the world, which must wait for the water to settle (normal play doesn't).
                if (new[] { "--dig-demo", "--stairs-demo", "--haul-demo", "--factory-demo", "--parts-demo", "--showroom", "--water-demo", "--homes-demo", "--ants-demo", "--storage-demo", "--house-demo", "--falls-demo", "--mist-demo", "--mine-demo", "--machines-demo", "--conveyor-demo", "--ticks" }.Any(a => Arg(a) != null))
                    _sim.WaitForWater();
                if (Arg("--dig-demo") is { } digDemo) DigDemo(digDemo);
                if (Arg("--stairs-demo") != null) StairsDemo();
                if (Arg("--haul-demo") != null) HaulDemo();
                if (Arg("--factory-demo") != null) FactoryDemo();
                if (Arg("--parts-demo") != null) PartsDemo();
                if (Arg("--showroom") is { } showroom) ShowroomDemo(showroom);
                if (Arg("--build-menu") is { } menu) _ui.OpenBuild(menu);
                if (Arg("--water-demo") != null) WaterDemo();
                if (Arg("--homes-demo") != null) HomesDemo();
                if (Arg("--storage-demo") is { } storageDemo) StorageDemo(storageDemo);
                if (Arg("--house-demo") is { } houseDemo) HouseDemo(houseDemo);
                if (Arg("--falls-demo") != null) FallsDemo();
                if (Arg("--mist-demo") != null) MistDemo();
                if (Arg("--ants-demo") is { } antsDemo) AntsDemo(antsDemo);
                if (Arg("--trade-demo") != null) TradeDemo();
                if (Arg("--mine-demo") is { } mineDemo) MineDemo(mineDemo);
                if (Arg("--machines-demo") != null) MachinesDemo();
                if (Arg("--conveyor-demo") != null) ConveyorDemo();
                if (int.TryParse(Arg("--ticks"), out int ticks))
                    for (int i = 0; i < ticks; i++) _sim.Step();
                if (_stairsDemoAt is { } s) _rig.Focus(SimSpace.TileCenter(s), 12f, yaw: 200f, pitch: -55f);
                NetDevArgs(); // --host, --join (a join reloads the scene onto the host's world)
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
            float benchYaw = float.TryParse(Arg("--yaw"), System.Globalization.CultureInfo.InvariantCulture, out float by) ? by : 200f; // dev: compare turns
            float benchPitch = float.TryParse(Arg("--pitch"), System.Globalization.CultureInfo.InvariantCulture, out float bp) ? bp : -50f; // dev: low views
            if (float.TryParse(Arg("--zoom"), out float zoom)) _rig.Focus(_world.Center, zoom, yaw: benchYaw, pitch: benchPitch);
            else if (Arg("--pitch") != null) _rig.Focus(_rig.Position, pitch: benchPitch);
            if (Arg("--no-flora") != null) _flora.Visible = false; // dev: what the trees cost
            if (Arg("--look")?.Split(',') is [var lx, var ly] && int.TryParse(lx, out int lookX) && int.TryParse(ly, out int lookY))
                _rig.Focus(SimSpace.TileCenter(new TilePos(lookX, lookY, _sim.World.SurfaceLevel(lookX, lookY))), float.TryParse(Arg("--zoom"), out float lz) ? lz : 60f, yaw: benchYaw, pitch: -55f);
            _benchActive = Arg("--screenshot") != null || Arg("--bench") != null;
        }
        if (!_benchActive) return;

        if (_meshedAtMs == 0)
        {
            if (_frames > 2 && _world.PendingSections == 0 && _flora.PendingRegions == 0 && (_waterView.PendingSections == 0 || _world.FarView) &&
                (_renderer == null || _world.FarProgress.Built == _world.FarProgress.Total)) // and the far blocks (play has them)
            {
                _meshedAtMs = Time.GetTicksMsec();
                _maxSimMs = 0;
                _waterView.MeshMs = _waterView.CapMs = 0;
                _waterView.Frames = _waterView.SectionsBuilt = 0;
                _simMsTotal = 0;
            }
            return;
        }
        if (Arg("--level-sweep") is { } sweep) { LevelSweep(sweep, delta); return; }
        if (Arg("--bench-zooms") is { Length: > 0 } zooms) { ZoomSweep(zooms, delta); return; }
        if (Arg("--orbit-check") != null) return; // OrbitCheck runs (and quits) instead

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
        // Where the draw calls come from: visible geometry instances per view (each costs a draw per pass that sees it).
        var counts = new List<string>();
        foreach (var child in GetChildren())
            if (child is Node3D n3 && CountDrawables(n3) is var k and > 0) counts.Add($"{n3.Name} {k}");
        GD.Print("[bench] visible instances: " + string.Join(", ", counts) +
                 (_renderer != null ? $"; art {_renderer.ArtSize.X}x{_renderer.ArtSize.Y}, {_renderer.TexelWorld:0.00} tiles/texel, pixel {_renderer.PixelScreen:0.0} px" : "") +
                 $"; far view {(_world.FarView ? "on" : "off")}, {_world.FarProgress.Built}/{_world.FarProgress.Total} blocks, sampling {_world.FarSampleMs:0} ms total");

        if (Arg("--screenshot") is { Length: > 0 } path)
        {
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"Saved screenshot to {path}");
        }
        _benchActive = false; // Quit takes effect a few frames later
        GetTree().Quit();
    }

    private int _sweepIndex = -1, _sweepFrame;
    private readonly List<double> _sweepMs = new();
    private long _sweepDraws, _sweepPrims;
    private double _sweepGpu, _sweepCpu;

    /// <summary>GPU ms of the last frame's art pass (the world) and the render setup CPU ms (the bench's split of a frame).</summary>
    private (double Gpu, double Cpu) FrameSplit()
    {
        double gpu = 0;
        if (_renderer?.ArtViewport is { } art)
        {
            var rid = art.GetViewportRid();
            RenderingServer.ViewportSetMeasureRenderTime(rid, true);
            gpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid);
        }
        return (gpu, RenderingServer.GetFrameSetupTimeCpu());
    }

    /// <summary>
    /// --bench-zooms=8,30,120,...: after the world is built, steps the camera through rig distances (40 frames to settle,
    /// 90 measured each) and prints frame time, draws, primitives and the renderer's state per zoom, then quits.
    /// </summary>
    private int _levelSweepStep = -1, _levelSweepFrom;
    private readonly List<(int Level, double Frame, double World)> _levelSweep = new();
    private readonly List<double> _levelSweepRest = new();

    /// <summary>
    /// --level-sweep[=N] (with --bench): once the world is built, steps the view down a level every other frame, N levels
    /// (default 40), and prints each change's frame time, the world view's share of it, and the worst, then quits.
    /// </summary>
    private void LevelSweep(string arg, double delta)
    {
        int levels = int.TryParse(arg, out int n) && n > 0 ? n : 40;
        if (_levelSweepStep < 0) { _levelSweepStep = 0; _levelSweepFrom = _world.ViewLevel; if (Arg("--no-vsync") != null) { DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); Engine.MaxFps = 0; } }
        else if (_levelSweepStep % 2 == 1) _levelSweep.Add((_world.ViewLevel, delta * 1000.0, _world.LevelMs)); // the frame that applied it
        else if (_levelSweepStep > 0) _levelSweepRest.Add(delta * 1000.0); // a frame with no change, for comparison
        if (_levelSweepStep / 2 >= levels || _world.ViewLevel <= 0)
        {
            var frames = _levelSweep.Select(s => s.Frame).OrderBy(f => f).ToArray();
            foreach (var s in _levelSweep) GD.Print($"[levels] {WorldInfo.Level(_sim, s.Level),5}: frame {s.Frame,6:0.0} ms, world view {s.World,6:0.0} ms");
            if (frames.Length > 0)
                GD.Print($"[levels] {frames.Length} level changes from {WorldInfo.Level(_sim, _levelSweepFrom)} on {WorldWidth}x{WorldDepth}x{WorldHeight}: " +
                         $"frame avg {frames.Average():0.0} ms, p95 {frames[(int)(frames.Length * 0.95)]:0.0}, max {frames[^1]:0.0}; world view avg {_levelSweep.Average(s => s.World):0.0} ms; frames between changes avg {(_levelSweepRest.Count > 0 ? _levelSweepRest.Average() : 0):0.0} ms");
            _benchActive = false;
            GetTree().Quit();
            return;
        }
        if (_levelSweepStep % 2 == 0) _world.ViewLevel--;
        _levelSweepStep++;
    }

    private void ZoomSweep(string list, double delta)
    {
        var zooms = list.Split(',').Select(z => float.Parse(z, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (_sweepIndex < 0 && Arg("--no-vsync") != null) { DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); Engine.MaxFps = 0; } // real frame times
        if (_breakdown != null)
        {
            if (!_breakdown.MoveNext()) { _breakdown = null; _sweepFrame = 131; }
            return;
        }
        if (_sweepIndex < 0 || _sweepFrame >= 130)
        {
            if (_sweepIndex >= 0 && _sweepFrame == 130)
            {
                _sweepMs.Sort();
                double avg = _sweepMs.Average();
                string look = _renderer != null
                    ? $"art {_renderer.ArtSize.X}x{_renderer.ArtSize.Y}, {_renderer.TexelWorld:0.00} t/px, far {(_world.FarView ? "on" : "off")}, ascii {_renderer.AsciiAmount * 100:0}%"
                    : "classic";
                GD.Print($"[zoom] {zooms[_sweepIndex],5:0} tiles: avg {avg,6:0.00} ms, p95 {_sweepMs[(int)(_sweepMs.Count * 0.95)],6:0.00}, max {_sweepMs[^1],6:0.00}; " +
                         $"world GPU {_sweepGpu / _sweepMs.Count:0.00} ms, render CPU {_sweepCpu / _sweepMs.Count:0.00} ms; draws {_sweepDraws / _sweepMs.Count}, prims {_sweepPrims / _sweepMs.Count / 1000}k; {look}; shadow dist {_sun.DirectionalShadowMaxDistance:0} ({(_sun.ShadowEnabled ? "on" : "off")})");
                if (Arg("--ids-check") != null && _renderer != null) GD.Print("[ids] " + _renderer.CompareIds());
                if (Arg("--tree-count") != null) { var (n, tris) = _flora.FullTreesNear(_rig.Camera.GlobalPosition); GD.Print($"[trees] {n} full trees in range, {tris / 1000}k LOD-0 triangles"); }
                if (Arg("--bench-breakdown") != null) { _breakdown = BreakdownSteps(); return; }
            }
            _sweepIndex++;
            _sweepFrame = 0;
            _sweepMs.Clear();
            _sweepDraws = _sweepPrims = 0;
            _sweepGpu = _sweepCpu = 0;
            if (_sweepIndex >= zooms.Length) { _benchActive = false; GetTree().Quit(); return; }
            _rig.Focus(_rig.Position, zooms[_sweepIndex]);
            return;
        }
        _sweepFrame++;
        if (_sweepFrame <= 40) return;
        _sweepMs.Add(delta * 1000.0);
        _sweepDraws += (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
        _sweepPrims += (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame);
        var (g, c) = FrameSplit();
        _sweepGpu += g; _sweepCpu += c;
    }

    // --bench-breakdown: after each zoom of the sweep, hides each view (and then the sun's shadows) in turn and prints what the
    // frame costs without it: where the draws, primitives and milliseconds go.
    private IEnumerator<bool>? _breakdown;

    private IEnumerator<bool> BreakdownSteps()
    {
        var parts = new List<(string Name, Action<bool> Show)>();
        foreach (var child in GetChildren())
            if (child is Node3D n3 && n3 != _rig && n3.Visible && CountDrawables(n3) > 0) parts.Add((n3.Name, v => n3.Visible = v));
        parts.Add(("sun shadows", v => _sun.ShadowEnabled = v));
        var line = new System.Text.StringBuilder("[parts] without:");
        foreach (var (name, show) in parts)
        {
            show(false);
            for (int i = 0; i < 12; i++) yield return true;
            double ms = 0, gpu = 0; long draws = 0, prims = 0;
            const int N = 30;
            for (int i = 0; i < N; i++)
            {
                ms += GetProcessDeltaTime() * 1000.0;
                gpu += FrameSplit().Gpu;
                draws += (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
                prims += (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame);
                yield return true;
            }
            show(true);
            line.Append($" {name} {ms / N:0.00} ms (gpu {gpu / N:0.00}) {draws / N} draws {prims / N / 1000}k;");
        }
        GD.Print(line.ToString());
    }

    private int _orbitFrame = -1;
    private float _orbitLast, _orbitWorst;

    /// <summary>
    /// --orbit-check: half way into the top-down map view, turns 2° a frame for 400 frames like a steady mouse drag and prints
    /// the biggest change of the shown turn in one frame (a jolt; a steady turn glides a few degrees a frame), then quits.
    /// </summary>
    private void OrbitCheck()
    {
        if (Arg("--orbit-check") == null || _meshedAtMs == 0) return;
        if (_orbitFrame < 0) { _rig.Focus(_rig.Position, Mathf.Sqrt(_rig.TopDownFrom * _rig.TopDownAt)); _orbitFrame = 0; _orbitLast = _rig.ShownYawDegrees; return; }
        _rig.DevTurn(2f);
        float shown = _rig.ShownYawDegrees;
        if (++_orbitFrame > 5) _orbitWorst = Mathf.Max(_orbitWorst, Mathf.Abs(Mathf.Wrap(shown - _orbitLast, -180f, 180f)));
        _orbitLast = shown;
        if (_orbitFrame < 400) return;
        GD.Print($"[orbit] top-down {_rig.TopDown:0.00}: worst turn in one frame {_orbitWorst:0.0}°");
        GetTree().Quit();
    }

    private int _gc0, _gc1, _gc2;
    private double _sweepSeen;

    /// <summary>
    /// --stutter-log: prints every frame over 50 ms with what ran in the previous frame (sim ticks, the material sweep,
    /// garbage collections), to find hitches that only show in play.
    /// </summary>
    private void StutterLog(double delta)
    {
        if (Arg("--stutter-log") == null) return;
        int g0 = System.GC.CollectionCount(0), g1 = System.GC.CollectionCount(1), g2 = System.GC.CollectionCount(2);
        bool swept = GameLooks.LastSweepMs != _sweepSeen;
        if (delta > 0.05)
            GD.Print($"[stutter] {delta * 1000:0} ms frame at tick {_sim.Tick}: sim {_simMsThisFrame:0.0} ms, " +
                     $"sweep {(swept ? $"{GameLooks.LastSweepMs:0.0} ms" : "-")}, GC {g0 - _gc0}/{g1 - _gc1}/{g2 - _gc2} (gen0/1/2), " +
                     $"world queue {_world.PendingSections}, far {(_world.FarView ? "on" : "off")}");
        else if (swept && GameLooks.LastSweepMs > 5)
            GD.Print($"[stutter] sweep {GameLooks.LastSweepMs:0.0} ms (frame {delta * 1000:0} ms)");
        _sweepSeen = GameLooks.LastSweepMs;
        (_gc0, _gc1, _gc2) = (g0, g1, g2);
    }

    /// <summary>Visible mesh / multimesh instances with something to draw under a node (bench diagnostics).</summary>
    private static int CountDrawables(Node3D root)
    {
        if (!root.Visible) return 0;
        int n = root switch
        {
            MeshInstance3D { Mesh: not null } => 1,
            MultiMeshInstance3D { Multimesh: { } mm } when mm.VisibleInstanceCount != 0 && mm.InstanceCount > 0 => 1,
            _ => 0,
        };
        foreach (var child in root.GetChildren())
            if (child is Node3D c) n += CountDrawables(c);
        return n;
    }

    /// <summary>Designates a room into the mountain's flank at the current level.</summary>
    private void DigDemo(string mode)
    {
        int z = _world.ViewLevel, cx = _sim.World.Width / 2, cy = _sim.World.Depth / 2;
        if (mode == "huge") // a huge box: stops at the order limit (a whole-map box once hung the game)
        {
            var c = _sim.Colonists[0].Position; // 240x240 round the colony, every level below it: past the limit
            GD.Print(_sim.Commands.Execute(new Colony.Core.Commands.DesignateBox(new TilePos(c.X - 120, c.Y - 120, 0), new TilePos(c.X + 120, c.Y + 120, c.Z - 1), Colony.Core.Orders.DesignationKind.Dig)));
        }
        else _sim.Designations.DesignateBox(new TilePos(cx - 6, cy - 4, z), new TilePos(cx + 6, cy + 4, z));
    }

    /// <summary>
    /// --mine-demo[=N]: a stair shaft from beside the colony down to N levels below zero (default 20; "deep": just over the
    /// bedrock), and a gallery there (a cross of tunnels and a hall), dug at once. Views that level, so its walls show the
    /// rock and what it holds, and the rock further in stays dark (fog of war).
    /// </summary>
    private void MineDemo(string arg)
    {
        var w = _sim.World;
        var c = _sim.Colonists[0].Position;
        int sx = c.X + 3, sy = c.Y + 3;
        int cavernFloor = -1;
        if (arg == "cavern" && w.Geology is { CavernLayers.Length: > 0 } geo)
        {
            // The nearest column over a roomy part of the first cavern layer: the shaft breaks into it.
            for (int r = 0; r < 400 && cavernFloor < 0; r += 2)
            for (int k = 0; k < 16 && cavernFloor < 0; k++)
            {
                int x = c.X + (int)(r * Math.Cos(k * Math.PI / 8)), y = c.Y + (int)(r * Math.Sin(k * Math.PI / 8));
                if (!w.InBounds(x, y, 0) || !geo.CavernSpan(x, y, 0, out int f, out int h) || h < 5 || !w.IsUnseen(x, y, f)) continue;
                (sx, sy, cavernFloor) = (x, y, f);
            }
        }
        int top = w.SurfaceLevel(sx, sy);
        int bottom = cavernFloor >= 0 ? cavernFloor : arg == "deep" && w.Geology is { } g ? g.BedrockTop(sx, sy) + 2
            : w.ZeroLevel - (int.TryParse(arg, out int n) ? n : 20);
        bottom = Math.Clamp(bottom, 2, top - 4);
        for (int z = top; z >= bottom; z--)
        {
            var p = new TilePos(sx, sy, z);
            _sim.Flora.Remove(p.Offset(0, 0, 1), out _);
            w.Set(p, z == top ? TileType.DownStair : z == bottom ? TileType.UpStair : TileType.UpDownStair);
        }
        void Open(int x, int y)
        {
            if (w.InBounds(x, y, bottom) && !w.IsBedrock(new TilePos(x, y, bottom)) && !(x == sx && y == sy)) w.Set(x, y, bottom, TileType.Air);
        }
        if (cavernFloor < 0) // a gallery (a cavern needs none: the shaft breaks into it)
        {
            for (int d = 1; d <= 26; d++) { Open(sx + d, sy); Open(sx - d, sy); Open(sx, sy + d); Open(sx, sy - d); }
            for (int y = sy + 8; y <= sy + 16; y++)
            for (int x = sx + 8; x <= sx + 16; x++) Open(x, y);
            for (int d = 1; d < 8; d++) Open(sx + 12, sy + d);
        }
        _sim.CheckSupport(new TilePos(sx, sy, bottom));
        var rocks = new System.Text.StringBuilder();
        for (int z = top; z >= 0; z -= 12) rocks.Append($" {z - w.ZeroLevel}:{w.RockAt(new TilePos(sx, sy, z))}");
        GD.Print($"[mine-demo] {(cavernFloor >= 0 ? $"into a cavern ({w.UnseenCells} cells still unseen); " : "")}shaft at {sx},{sy} from level {top - w.ZeroLevel} to {bottom - w.ZeroLevel} (zero is z {w.ZeroLevel}); rock down the shaft:{rocks}");
        _world.ViewLevel = bottom;
        float zoom = float.TryParse(Arg("--zoom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float zf) ? zf : 34f;
        _rig.Focus(SimSpace.TileCenter(cavernFloor >= 0 ? new TilePos(sx, sy, bottom) : new TilePos(sx + 6, sy + 6, bottom)), zoom, yaw: 205f, pitch: -62f);
    }

    /// <summary>
    /// --machines-demo: an excavator and a power wagon beside the colony, a dig box for the one, rubble and a stockpile for
    /// the other (and charcoal to burn); runs a while and frames them close.
    /// </summary>
    private void MachinesDemo()
    {
        var c = _sim.Colonists[0].Position;
        int g = c.Z - 1;
        // Level a yard: ground at g, open above (plants on it go), so the machines have room.
        for (int y = c.Y - 8; y <= c.Y + 8; y++)
        for (int x = c.X - 2; x <= c.X + 16; x++)
        {
            if (!_sim.World.InBounds(x, y, g)) continue;
            for (int z = g + 1; z <= g + 6 && z < _sim.World.Height; z++) { _sim.Flora.Remove(new TilePos(x, y, z), out _); _sim.World.Set(x, y, z, TileType.Air); }
            for (int z = g - 3; z <= g; z++) _sim.World.Set(x, y, z, z == g ? TileType.Grass : TileType.Soil);
        }
        var ex = _sim.AddMachine(Colony.Core.Agents.MachineKind.Excavator, new TilePos(c.X + 2, c.Y - 4, g + 1));
        var wagon = _sim.AddMachine(Colony.Core.Agents.MachineKind.PowerWagon, new TilePos(c.X + 2, c.Y + 4, g + 1));
        _sim.Commands.Execute(new Colony.Core.Commands.DesignateBox(new TilePos(c.X + 6, c.Y - 7, g), new TilePos(c.X + 14, c.Y - 2, g - 2), Colony.Core.Orders.DesignationKind.Dig));
        GD.Print("[machines-demo] " + _sim.Commands.Execute(new Colony.Core.Commands.AddStockpile(new TilePos(c.X + 8, c.Y + 3, g), new TilePos(c.X + 14, c.Y + 7, g + 1))));
        for (int i = 0; i < 6; i++) _sim.Items.Drop(new TilePos(c.X + 3 + i, c.Y + 1, g + 1), Colony.Core.Items.ItemType.Stone, 20);
        _sim.Items.Drop(new TilePos(c.X - 1, c.Y + 6, g + 1), Colony.Core.Items.ItemType.Charcoal, 30);
        int ticks = int.TryParse(Arg("--machines-ticks"), out int t) ? t : 500;
        for (int i = 0; i < ticks; i++) _sim.Step();
        int stored = _sim.Stockpiles.Cells.Sum(s => _sim.Items.TryGet(s, out var p) && p.Type == Colony.Core.Items.ItemType.Stone ? p.Count : 0);
        GD.Print($"[machines-demo] {ex.Name}: {ex.Describe()}, fuel {ex.Fuel}; {wagon.Name}: {wagon.Describe()}, carrying {wagon.Driver?.CarryingCount ?? 0}; stone stockpiled {stored}; orders left {_sim.Designations.Count}");
        _world.ViewLevel = g + 1;
        _rig.Focus(SimSpace.TileCenter(new TilePos(c.X + 7, c.Y, g + 1)), float.TryParse(Arg("--machines-zoom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z0) ? z0 : 14f, yaw: 215f, pitch: -42f);
    }

    /// <summary>
    /// --conveyor-demo: a levelled yard by the colony with a carpentry's output crate full of planks, a rock ridge and a
    /// trench, and over them a belt to a stockpile, a splitter branch to a joinery bench's input crate, and a hopper with
    /// stone round it belted to the stockpile too; built at once (powered free) and run a while, framed close.
    /// </summary>
    private void ConveyorDemo()
    {
        // Dev mode: belts build the tick they are planned and run without water wheels.
        GD.Print("[conveyor-demo] " + _sim.Commands.Execute(new Colony.Core.Commands.SetDevMode(true, false) { Player = Player }));
        var c = _sim.Colonists[0].Position;
        int g = c.Z - 1;
        for (int y = c.Y - 10; y <= c.Y + 10; y++)
        for (int x = c.X - 4; x <= c.X + 26; x++)
        {
            if (!_sim.World.InBounds(x, y, g)) continue;
            for (int z = g + 1; z <= g + 8 && z < _sim.World.Height; z++) { _sim.Flora.Remove(new TilePos(x, y, z), out _); _sim.World.Set(x, y, z, TileType.Air); }
            for (int z = g - 3; z <= g; z++) _sim.World.Set(x, y, z, z == g ? TileType.Grass : TileType.Soil);
        }
        // A ridge three high and a trench two deep between the yard's halves.
        for (int y = c.Y - 10; y <= c.Y + 10; y++)
        {
            for (int z = g + 1; z <= g + 3; z++) _sim.World.Set(c.X + 9, y, z, TileType.Stone);
            for (int z = g - 1; z <= g; z++) { _sim.World.Set(c.X + 14, y, z, TileType.Air); _sim.World.Set(c.X + 15, y, z, TileType.Air); }
        }
        var q = _sim.Commands;
        q.Execute(new Colony.Core.Commands.PaintRoom("carpentry", new TilePos(c.X - 3, c.Y - 3, g), new TilePos(c.X + 2, c.Y + 3, g)) { Player = Player });
        q.Execute(new Colony.Core.Commands.PaintRoom("carpentry", new TilePos(c.X + 19, c.Y - 9, g), new TilePos(c.X + 25, c.Y - 4, g)) { Player = Player });
        q.Execute(new Colony.Core.Commands.AddStockpile(new TilePos(c.X + 19, c.Y + 2, g), new TilePos(c.X + 24, c.Y + 7, g)) { Player = Player });
        _sim.Rooms.InstantBuild = true;
        var outCrate = new TilePos(c.X + 2, c.Y, g + 1);
        var inCrate = new TilePos(c.X + 19, c.Y - 6, g + 1);
        _sim.Rooms.Place(Colony.Core.Industry.PartType.OutputCrate, outCrate, false);
        _sim.Rooms.Place(Colony.Core.Industry.PartType.InputCrate, inCrate, false);
        if (_sim.Rooms.Place(Colony.Core.Industry.Catalog.Part("carpentry.joinery_bench"), new TilePos(c.X + 22, c.Y - 7, g + 1), false) is { } bench)
            _sim.Rooms.SetRecipe(bench, Colony.Core.Industry.Catalog.RecipeFor(Colony.Core.Industry.Catalog.Item("wooden_gears"))!);
        _sim.Rooms.InstantBuild = _sim.DevMode;
        _sim.Items.Drop(outCrate, Colony.Core.Items.ItemType.Plank, 28); // under three quarters full: haulers leave it to the belt
        string Lay(TilePos from, TilePos to)
        {
            var a = Colony.Core.Transport.Conveyors.PortAt(_sim, Player, from, true, out var whyA);
            var b = Colony.Core.Transport.Conveyors.PortAt(_sim, Player, to, false, out var whyB);
            if (a is null || b is null) return $"no port: {whyA ?? whyB}";
            var route = Colony.Core.Transport.ConveyorRouter.Route(_sim, Colony.Core.Transport.Conveyors.EndCells(_sim, a.Value, true), Colony.Core.Transport.Conveyors.EndCells(_sim, b.Value, false), out int nodes);
            if (route is null) return $"no route ({nodes} cells searched)";
            return q.Execute(new Colony.Core.Commands.PlaceConveyor(route[0], Colony.Core.Transport.Conveyors.Encode(route), a.Value.Kind, from, b.Value.Kind, to) { Player = Player }) + $" ({nodes} cells searched)";
        }
        GD.Print("[conveyor-demo] main: " + Lay(outCrate, new TilePos(c.X + 19, c.Y + 4, g + 1)));
        if (_sim.Conveyors.All.Count > 0)
        {
            var main = _sim.Conveyors.All[^1];
            int at = Array.FindIndex(main.Cells, p => p.X >= c.X + 17);
            if (at > 0) GD.Print("[conveyor-demo] branch: " + Lay(main.Cells[at], inCrate));
        }
        var hopper = new TilePos(c.X + 4, c.Y + 8, g + 1);
        GD.Print("[conveyor-demo] hopper: " + Lay(hopper, new TilePos(c.X + 20, c.Y + 7, g + 1)));
        // A stepped hill, and a belt that turns as it climbs it: a ramp leaving at a right angle to the way it came in.
        for (int x = c.X + 4; x <= c.X + 6; x++)
        {
            _sim.World.Set(x, c.Y - 6, g + 1, TileType.Stone);
            for (int y = c.Y - 9; y <= c.Y - 7; y++) { _sim.World.Set(x, y, g + 1, TileType.Stone); _sim.World.Set(x, y, g + 2, TileType.Stone); }
        }
        var climb = new System.Collections.Generic.List<TilePos> { new(c.X + 3, c.Y - 5, g + 1), new(c.X + 4, c.Y - 5, g + 1), new(c.X + 4, c.Y - 6, g + 2), new(c.X + 4, c.Y - 7, g + 3), new(c.X + 4, c.Y - 8, g + 3), new(c.X + 5, c.Y - 8, g + 3) };
        GD.Print("[conveyor-demo] climb: " + q.Execute(new Colony.Core.Commands.PlaceConveyor(climb[0], Colony.Core.Transport.Conveyors.Encode(climb), Colony.Core.Transport.PortKind.Ground, climb[0], Colony.Core.Transport.PortKind.Ground, climb[^1]) { Player = Player }));
        for (int i = 0; i < 6; i++) _sim.Items.Drop(new TilePos(c.X + 2 + i, c.Y + 6, g + 1), Colony.Core.Items.ItemType.Stone, 8);
        int ticks = int.TryParse(Arg("--conveyor-ticks"), out int t) ? t : 400;
        for (int i = 0; i < ticks; i++) _sim.Step();
        foreach (var b in _sim.Conveyors.All)
            GD.Print($"[conveyor-demo] belt {b.Id}: {b.Length} tiles, {b.Status}, {b.Packets} on it, {b.Delivered} delivered; {_sim.Conveyors.Explain(_sim, b)}");
        _world.ViewLevel = g + 4;
        var focus = Arg("--conveyor-focus") == "climb" ? climb[1] : new TilePos(c.X + 11, c.Y, g + 1);
        _rig.Focus(SimSpace.TileCenter(focus), float.TryParse(Arg("--conveyor-zoom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z0) ? z0 : 22f, yaw: float.TryParse(Arg("--conveyor-yaw"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y0) ? y0 : 205f, pitch: -48f);
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
    /// field (irrigated, so it grows half again as fast).
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
        _sim.Farms.AddFloorBox(Colony.Core.Nature.PlantKind.Flax, new TilePos(sx + 9, sy + 13, g), new TilePos(sx + 14, sy + 20, g));
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

    /// <summary>--falls-demo: a block of rock beside the colony with a spring on top: water pours off its open edges.</summary>
    private void FallsDemo()
    {
        var c = _sim.Colonists.First(k => k.Faction == Player).Position;
        int g = _sim.World.SurfaceLevel(c.X, c.Y);
        int x0 = c.X + 6, y0 = c.Y - 3;
        for (int y = y0 - 4; y <= y0 + 10; y++)
        for (int x = x0 - 4; x <= x0 + 12; x++)
        {
            for (int z = g - 2; z <= g; z++) _sim.World.Set(new TilePos(x, y, z), TileType.Soil);
            for (int z = g + 1; z <= g + 10; z++) _sim.World.Set(new TilePos(x, y, z), TileType.Air);
            _sim.Flora.Remove(new TilePos(x, y, g + 1), out _);
        }
        for (int y = y0; y <= y0 + 6; y++)
        for (int x = x0; x <= x0 + 6; x++)
        for (int z = g + 1; z <= g + 6; z++) _sim.World.Set(new TilePos(x, y, z), TileType.Stone);
        _sim.Water.AddSpring(new TilePos(x0 + 3, y0 + 3, g + 7), 300, head: 1);
        for (int t = 0; t < 1500; t++) _sim.Step();
        var falls = new List<Colony.Core.Fluids.Fall>();
        _sim.Water.Falls(x0 - 2, y0 - 2, x0 + 8, y0 + 8, falls);
        GD.Print($"Falls demo: {falls.Count} falls off the block at ({x0},{y0})");
        _stairsDemoAt = new TilePos(x0 + 8, y0 + 3, g + 1);
    }

    /// <summary>
    /// <summary>
    /// --mist-demo: a tall mesa beside the colony, a step at its foot, and springs on top pouring off both: many big falls,
    /// played on until their mist (<see cref="Colony.Core.Fluids.Mist"/>) has built into banks round the foot.
    /// </summary>
    private void MistDemo()
    {
        var c = _sim.Colonists.First(k => k.Faction == Player).Position;
        int g = _sim.World.SurfaceLevel(c.X, c.Y);
        int x0 = c.X + 8, y0 = c.Y - 12;
        for (int y = y0 - 10; y <= y0 + 34; y++)
        for (int x = x0 - 14; x <= x0 + 30; x++)
        {
            for (int z = g - 2; z <= g; z++) _sim.World.Set(new TilePos(x, y, z), TileType.Soil);
            for (int z = g - 6; z <= g + 14; z++) _sim.Flora.Remove(new TilePos(x, y, z), out _); // trees rooted on any slope
            for (int z = g + 1; z <= g + 14; z++) _sim.World.Set(new TilePos(x, y, z), TileType.Air);
        }
        for (int y = y0; y <= y0 + 24; y++)
        for (int x = x0 + 8; x <= x0 + 20; x++)
        for (int z = g + 1; z <= g + (x < x0 + 11 ? 4 : 11); z++) _sim.World.Set(new TilePos(x, y, z), TileType.Stone);
        for (int k = 0; k < 4; k++) _sim.Water.AddSpring(new TilePos(x0 + 16, y0 + 3 + k * 6, g + 12), 600, head: 1);
        for (int t = 0; t < 2400; t++) _sim.Step();
        GD.Print($"Mist demo: {_sim.Mist.ActiveCells} misty cells, {_sim.Mist.Total} mist in all");
        _rig.Focus(SimSpace.TileCenter(new TilePos(x0 + 6, y0 + 12, g + 1)), 34f, yaw: 235f, pitch: -42f);
    }

    /// <summary>
    /// --trade-demo (with --colonies=2 or more): a trade depot by each colony's people, an agreed deal under way (our
    /// logs for their berries) and a fresh offer from the next colony waiting on us; opens Trade (F6). Commands only, so
    /// it works online too.
    /// </summary>
    private void TradeDemo()
    {
        if (_sim.Factions.Count < 2) { GD.Print("[trade-demo] needs --colonies=2 or more"); return; }
        int other = Player == 0 ? 1 : 0;
        foreach (int f in new[] { Player, other })
        {
            var c = _sim.Colonists.First(k => k.Faction == f).Position;
            int g = _sim.World.SurfaceLevel(c.X + 4, c.Y + 4);
            _sim.Commands.Execute(new Colony.Core.Commands.AddTradeDepot(new TilePos(c.X + 3, c.Y + 3, g - 3), new TilePos(c.X + 6, c.Y + 6, g + 3)) { Player = f });
        }
        static Colony.Core.Trade.TradeTerm T(int from, string item, int n) => new(from, item, n);
        _sim.Commands.Execute(new Colony.Core.Commands.ProposeTrade(other, new([T(Player, "logs", 8), T(other, "berries", 20)])) { Player = Player });
        _sim.Commands.Execute(new Colony.Core.Commands.AcceptTrade(_sim.Trades.All[^1].Id) { Player = other });
        _sim.Commands.Execute(new Colony.Core.Commands.ProposeTrade(Player, new([T(other, "bread", 12), T(Player, "logs", 6), T(Player, "stone", 10)])) { Player = other });
        _ui.OpenWindow("trade");
    }

    /// <summary>
    /// --house-demo[=STOREYS]: levels ground beside the colony, plans a house (2 storeys by default) with the House tool's
    /// command, drops the planks and thatch it needs and plays until it is built (or 60k ticks); a second house beside it
    /// stays planned, to show its blueprints. The camera frames both.
    /// </summary>
    private void HouseDemo(string storeysArg)
    {
        int storeys = int.TryParse(storeysArg, out int s) ? Math.Clamp(s, 1, Colony.Core.Orders.Houses.MaxStoreys) : 2;
        var c = _sim.Colonists.First(k => k.Faction == Player).Position;
        int g = _sim.World.SurfaceLevel(c.X, c.Y);
        int x0 = c.X + 3, y0 = c.Y - 2;
        for (int y = y0 - 2; y <= y0 + 8; y++)
        for (int x = x0 - 2; x <= x0 + 14; x++)
        {
            for (int z = g - 3; z < g; z++) _sim.World.Set(new TilePos(x, y, z), TileType.Soil);
            _sim.World.Set(new TilePos(x, y, g), TileType.Grass);
            for (int z = g + 1; z <= g + storeys + 3; z++)
            {
                _sim.World.Set(new TilePos(x, y, z), TileType.Air);
                _sim.Flora.Validate(new TilePos(x, y, z));
            }
            _sim.Flora.Remove(new TilePos(x, y, g + 1), out _);
        }
        _sim.Items.Drop(new TilePos(x0 - 2, y0 - 2, g + 1), Colony.Core.Items.ItemType.Plank, 150);
        _sim.Items.Drop(new TilePos(x0 - 1, y0 - 2, g + 1), Colony.Core.Items.ItemType.Plank, 150);
        _sim.Items.Drop(new TilePos(x0, y0 - 2, g + 1), Colony.Core.Items.ItemType.Fiber, 60);
        string built = _sim.Commands.Execute(new Colony.Core.Commands.PlaceHouse(new TilePos(x0, y0, g), new TilePos(x0 + 4, y0 + 3, g), storeys, 2, 1, 1) { Player = Player });
        GD.Print($"[house-demo] {built}");
        int t = 0;
        for (; t < 60_000 && _sim.Constructions.Count > 0; t++) _sim.Step();
        GD.Print($"[house-demo] built in {t} ticks; {_sim.Constructions.Count} pieces left; ground at {g}");
        for (int z = g; z <= g + storeys + 1; z++)
        {
            var mid = new TilePos(x0 + 1, y0 + 1, z);
            GD.Print($"[house-demo]   level {z}: {_sim.World.Get(mid)}, floor {_sim.World.FloorAt(mid)}, roof {_sim.World.RoofAt(mid)}, stair corner {_sim.World.Get(new TilePos(x0 + 4, y0 + 3, z))}");
        }
        GD.Print("[house-demo] " + _sim.Commands.Execute(new Colony.Core.Commands.PlaceHouse(new TilePos(x0 + 7, y0, g), new TilePos(x0 + 11, y0 + 3, g), storeys, 3, 2, 3) { Player = Player }));
        _rig.Focus(SimSpace.TileCenter(new TilePos(x0 + 6, y0 + 2, g + 1)), float.TryParse(Arg("--house-zoom"), out float zd) ? zd : 14f, yaw: 205f, pitch: -38f);
    }

    /// --storage-demo: a stockpile with storage racks planned on it (planks and loose stone to fill it), and beside it a
    /// stone ceiling two levels over flat ground with one tile to dig and a scaffold to reach it. Plays until the racks
    /// are up and someone stands on the scaffold (or 20k ticks).
    /// </summary>
    private void StorageDemo(string mode)
    {
        var c = _sim.Colonists.First(k => k.Faction == Player).Position;
        int g = _sim.World.SurfaceLevel(c.X, c.Y);
        // Level the ground: soil up to g, grass on top, air above (plants on it go).
        void Flatten(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                for (int z = g - 3; z < g; z++) _sim.World.Set(new TilePos(x, y, z), TileType.Soil);
                _sim.World.Set(new TilePos(x, y, g), TileType.Grass);
                for (int z = g + 1; z <= g + 6; z++)
                {
                    _sim.World.Set(new TilePos(x, y, z), TileType.Air);
                    _sim.Flora.Validate(new TilePos(x, y, z));
                }
                _sim.Flora.Remove(new TilePos(x, y, g + 1), out _);
            }
        }
        int rx = c.X + 3, ry = c.Y + 3;
        Flatten(rx - 1, ry - 1, rx + 8, ry + 6);
        var a = new TilePos(rx, ry, g);
        _sim.Commands.Execute(new Colony.Core.Commands.AddStockpile(a, a.Offset(5, 3, 0)) { Player = Player });
        _sim.Commands.Execute(new Colony.Core.Commands.PlaceRacks(a.Offset(0, 1, 0), a.Offset(5, 1, 0), 3) { Player = Player });
        _sim.Commands.Execute(new Colony.Core.Commands.PlaceRacks(a.Offset(0, 2, 0), a.Offset(5, 2, 0), 1) { Player = Player });
        _sim.Items.Drop(new TilePos(rx + 2, ry + 5, g + 1), Colony.Core.Items.ItemType.Plank, 120);
        _sim.Items.Drop(new TilePos(rx + 6, ry + 5, g + 1), Colony.Core.Items.ItemType.Stone, 150);
        _sim.Items.Drop(new TilePos(rx + 7, ry + 1, g + 1), Colony.Core.Items.ItemType.Log, 60);

        // The ceiling: a 3x3 slab at g+3 held up by a pillar, its middle tile designated.
        int cx = c.X - 6, cy = c.Y + 4;
        Flatten(cx - 3, cy - 3, cx + 3, cy + 3);
        for (int y = cy - 1; y <= cy + 1; y++)
        for (int x = cx - 1; x <= cx + 1; x++) _sim.World.Set(new TilePos(x, y, g + 3), TileType.Stone);
        for (int z = g + 1; z <= g + 3; z++) _sim.World.Set(new TilePos(cx + 2, cy, z), TileType.Stone);
        var target = new TilePos(cx, cy, g + 3);
        _sim.Items.Drop(new TilePos(cx - 2, cy - 2, g + 1), Colony.Core.Agents.Colonist.ScaffoldItem, 1);

        // Racks first; then the ceiling job, played until someone stands on the scaffold.
        int t = 0;
        for (; t < 20_000 && _sim.Stockpiles.Racks.Count < 12; t++) _sim.Step();
        _sim.Commands.Execute(new Colony.Core.Commands.DesignateBox(target, target, Colony.Core.Orders.DesignationKind.Dig) { Player = Player });
        for (; t < 30_000 && !_sim.Colonists.Any(k => k.OnScaffold && k.WorkDone * 2 > k.WorkRequired); t++) _sim.Step();
        _stairsDemoAt = mode == "racks" ? new TilePos(rx + 3, ry + 5, g + 1) : new TilePos((rx + cx) / 2, cy, g + 1);
        if (mode == "racks") for (int k = 0; k < 6000; k++) _sim.Step(); // let the haulers fill the shelves
        if (mode == "racks")
        {
            // Show the UI at work: the zone selected in Inspect, a tool in hand, paused.
            Selection.Select(new PickZone(a.Offset(4, 3, 1), false));
            _ui.OpenWindow("inspect");
            _tool.SelectOrder(DesignationTool.ToolMode.Dig);
            _paused = true;
        }
        GD.Print($"Storage demo: {_sim.Stockpiles.Racks.Count} racks up, scaffold in use: {_sim.Colonists.Any(k => k.OnScaffold)}, after {t} ticks");
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

public partial class Main
{
    /// <summary>
    /// --ants-demo[=calm]: a full giant ant nest a short walk from the colony, arms and armour handed out, everyone drafted
    /// and sent to destroy it (=calm: just the nest, to watch them forage and see that they leave colonists alone).
    /// </summary>
    private void AntsDemo(string mode)
    {
        var home = _sim.Colonists.First(c => c.Faction == Player).Position;
        Colony.Core.Navigation.Connectivity conn = _sim.Connectivity;
        conn.Update();
        TilePos? site = null;
        for (int r = 16; r < 40 && site is null; r++)
        for (int k = 0; k < 8 && site is null; k++)
        {
            int x = home.X + (int)(r * Math.Cos(k * Math.PI / 4)), y = home.Y + (int)(r * Math.Sin(k * Math.PI / 4));
            var p = new TilePos(x, y, _sim.World.SurfaceLevel(x, y) + 1);
            if (Colony.Core.Navigation.Movement.IsStandable(_sim.World, p) && conn.Connected(p, home) && _sim.Water.MassAt(p) < Colony.Core.Fluids.Water.MinMass) site = p;
        }
        if (site is not { } at)
        {
            GD.PrintErr("Ants demo: no spot for a nest near the colony");
            return;
        }
        var nest = _sim.Wildlife.AddNest(_sim, Colony.Core.Combat.CreatureKinds.GiantAnt, at, 40);
        _stairsDemoAt = at;
        if (mode == "calm")
        {
            GD.Print($"Ants demo: a calm nest at {at}");
            return;
        }
        string[] arms = ["war_axe", "spear", "sword", "bow", "spear", "sword", "war_axe"];
        int i = 0;
        foreach (var c in _sim.Colonists.Where(c => c.Faction == Player && !c.IsChild))
        {
            c.PutOn(_sim, Colony.Core.Industry.Catalog.Item(arms[i % arms.Length]));
            c.PutOn(_sim, Colony.Core.Industry.Catalog.Item(i % 3 == 0 ? "plate_armor" : i % 3 == 1 ? "chitin_armor" : "padded_jerkin"));
            if (i % 2 == 1) c.PutOn(_sim, Colony.Core.Industry.Catalog.Item("wooden_shield"));
            i++;
        }
        _tool.Issue(new Colony.Core.Commands.SetDrafted(-1, true));
        _tool.Issue(new Colony.Core.Commands.SquadAttack(nest.Id));
        GD.Print($"Ants demo: nest of {nest.Total} at {at}, squad of {i} sent against it");
    }
}
