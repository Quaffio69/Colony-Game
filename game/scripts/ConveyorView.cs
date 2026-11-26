using System;
using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Items;
using Colony.Core.Transport;
using Colony.Core.World;
using Godot;
using static ColonyGame.MeshKit;

namespace ColonyGame;

/// <summary>
/// Conveyor belts (<see cref="Conveyors"/>): a timber frame with a dark belt between its rails and iron rollers under it,
/// run tile to tile (flat runs, ramps climbing in the lower column, lifts as slatted towers), trestle legs down to the
/// ground under raised stretches, a gearbox at each belt's first tile (its drive, or the splitter it branches from) and a
/// bin round a hopper. Planned tiles are translucent plates. Goods ride as little crates in their item's colour, eased
/// from tile to tile. One MultiMesh per piece, rebuilt when belts change; sliced at the view level like flora.
/// </summary>
public partial class ConveyorView : Node3D
{
    /// <summary>Height of the belt's surface over its cell's floor.</summary>
    public const float BeltHeight = 0.14f;

    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private Shader _shader = null!;
    private readonly List<ShaderMaterial> _materials = new();
    private MultiMeshInstance3D _runs = null!, _joints = null!, _lifts = null!, _posts = null!, _drives = null!, _hoppers = null!, _ghosts = null!, _packets = null!;
    private int _drawnVersion = -1, _drawnLevel = int.MinValue;
    private long _drawnStates = -1;
    private int _drawnSteps = -1;
    private long _stepTick;
    private readonly Dictionary<int, (Vector3 From, Vector3 Via, Vector3 To)> _moving = new();
    private readonly Dictionary<int, (int Belt, int Index)> _where = new();
    private readonly Dictionary<int, Vector3> _shown = new();

    public void Initialize(ColonySim sim, WorldView world)
    {
        _sim = sim;
        _world = world;
        _shader = GameLooks.Shader("res://shaders/flora.gdshader");
        var wood = Mat(new Color(0.56f, 0.41f, 0.25f));
        var dark = Mat(new Color(0.36f, 0.27f, 0.18f));
        var belt = Mat(new Color(0.22f, 0.19f, 0.17f), 0.55f);
        var iron = Mat(new Color(0.42f, 0.43f, 0.46f));
        var brass = Mat(new Color(0.72f, 0.56f, 0.28f));
        _runs = Add(RunMesh(wood, belt, iron));
        _joints = Add(JointMesh(wood, belt));
        _lifts = Add(LiftMesh(wood, dark, belt));
        _posts = Add(PostMesh(dark));
        _drives = Add(DriveMesh(iron, brass, dark));
        _hoppers = Add(HopperMesh(wood, dark));
        var crate = Mat(new Color(1, 1, 1), 1f);
        _packets = Add(PacketMesh(crate));
        _ghosts = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new BoxMesh { Size = new Vector3(0.7f, 0.1f, 0.7f), Material = Overlay.Material(new Color(1f, 0.75f, 0.4f, 0.32f)) },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_ghosts);
    }

    private MultiMeshInstance3D Add(Mesh mesh)
    {
        var m = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = mesh },
        };
        AddChild(m);
        return m;
    }

    private ShaderMaterial Mat(Color albedo, float tint = 0f)
    {
        var m = new ShaderMaterial { Shader = _shader };
        m.SetShaderParameter("albedo", albedo);
        m.SetShaderParameter("tint_mix", tint);
        m.SetShaderParameter("object_id", (float)GameLooks.Furniture);
        GameLooks.Styled(m);
        _materials.Add(m);
        return m;
    }

    // ---- Models (Godot space: +X along the run, +Y up; one unit long) -----------------------------------------------

    private static Mesh RunMesh(Material wood, Material belt, Material iron)
    {
        var p = new Parts();
        p.Add(Box(1.0f, 0.035f, 0.5f), 0.5f, -0.018f, 0, belt);           // the belt
        p.Add(Box(1.0f, 0.1f, 0.06f), 0.5f, -0.02f, 0.29f, wood);           // side rails
        p.Add(Box(1.0f, 0.1f, 0.06f), 0.5f, -0.02f, -0.29f, wood);
        foreach (float x in new[] { 0.25f, 0.75f })
            p.Add(Cyl(0.035f, 0.54f, 6), AlongZ(x, -0.06f, 0), iron);         // rollers under it
        return Compose(p);
    }

    /// <summary>The tile's own plate under a bend or a join (runs meet at tile centres).</summary>
    private static Mesh JointMesh(Material wood, Material belt)
    {
        var p = new Parts();
        p.Add(Box(0.52f, 0.036f, 0.52f), 0, -0.018f, 0, belt);
        p.Add(Box(0.64f, 0.05f, 0.64f), 0, -0.07f, 0, wood);
        return Compose(p);
    }

    /// <summary>A bucket lift one unit tall: four corner posts, slats every half level, the belt up its middle.</summary>
    private static Mesh LiftMesh(Material wood, Material dark, Material belt)
    {
        var p = new Parts();
        foreach (float x in new[] { -0.27f, 0.27f })
        foreach (float z in new[] { -0.27f, 0.27f })
            p.Add(Box(0.06f, 1f, 0.06f), x, 0.5f, z, wood);
        foreach (float y in new[] { 0.25f, 0.75f })
        {
            p.Add(Box(0.6f, 0.04f, 0.04f), 0, y, 0.27f, dark);
            p.Add(Box(0.6f, 0.04f, 0.04f), 0, y, -0.27f, dark);
            p.Add(Box(0.04f, 0.04f, 0.6f), 0.27f, y, 0, dark);
            p.Add(Box(0.04f, 0.04f, 0.6f), -0.27f, y, 0, dark);
        }
        p.Add(Box(0.3f, 1f, 0.03f), 0, 0.5f, 0, belt);
        return Compose(p);
    }

    /// <summary>A trestle leg one unit tall with a cross-tie at the top.</summary>
    private static Mesh PostMesh(Material dark)
    {
        var p = new Parts();
        p.Add(Box(0.08f, 1f, 0.08f), 0, 0.5f, 0, dark);
        p.Add(Box(0.5f, 0.05f, 0.08f), 0, 0.97f, 0, dark);
        return Compose(p);
    }

    /// <summary>The drive (or a splitter): an iron gearbox with a brass wheel on top.</summary>
    private static Mesh DriveMesh(Material iron, Material brass, Material dark)
    {
        var p = new Parts();
        p.Add(Box(0.66f, 0.12f, 0.66f), 0, -0.08f, 0, dark);
        p.Add(Box(0.22f, 0.26f, 0.5f), 0, 0.13f, 0.38f, iron);
        p.Add(Cyl(0.17f, 0.06f, 12), AlongX(0, 0.3f, 0.38f), brass);
        p.Add(Cyl(0.05f, 0.1f, 6), AlongX(0, 0.3f, 0.38f), iron);
        return Compose(p);
    }

    /// <summary>A plank bin round a hopper tile (its goods lie inside).</summary>
    private static Mesh HopperMesh(Material wood, Material dark)
    {
        var p = new Parts();
        p.Add(Box(0.92f, 0.36f, 0.06f), 0, 0.18f, 0.45f, wood);
        p.Add(Box(0.92f, 0.36f, 0.06f), 0, 0.18f, -0.45f, wood);
        p.Add(Box(0.06f, 0.36f, 0.92f), 0.45f, 0.18f, 0, wood);
        p.Add(Box(0.06f, 0.36f, 0.92f), -0.45f, 0.18f, 0, wood);
        foreach (float x in new[] { -0.45f, 0.45f })
        foreach (float z in new[] { -0.45f, 0.45f })
            p.Add(Box(0.09f, 0.42f, 0.09f), x, 0.21f, z, dark);
        return Compose(p);
    }

    private static Mesh PacketMesh(Material crate)
    {
        var p = new Parts();
        p.Add(Box(0.3f, 0.2f, 0.3f), 0, 0.1f, 0, crate);
        return Compose(p);
    }

    // ---- Geometry ---------------------------------------------------------------------------------------------------

    /// <summary>The middle of a belt tile's floor-level surface (Godot space).</summary>
    public static Vector3 Point(TilePos c) => new(c.X + 0.5f, c.Z + BeltHeight, c.Y + 0.5f);

    private static readonly (int X, int Y)[] Dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    /// <summary>Horizontal direction (0..3) from <paramref name="c"/> towards a neighbour, or -1 if straight above or below.</summary>
    private static int DirTo(TilePos c, TilePos n) => Array.IndexOf(Dirs, (n.X - c.X, n.Y - c.Y));

    /// <summary>
    /// How tile <paramref name="i"/> of a route is laid: where it enters and leaves (an edge midpoint, or none at an end or
    /// on a lift), and the surface height (over its floor) at each and at its centre. A ramp climbs across the lower tile
    /// of the pair, edge to edge at 45 degrees (in at the floor, out a level up), so a run of ramps is one smooth slope, a
    /// ramp can turn (entering on one side, leaving up on another), and nothing cuts into the hill it climbs.
    /// </summary>
    public static (Vector3? In, Vector3 Centre, Vector3? Out) Tile(IReadOnlyList<TilePos> cells, int i)
    {
        var c = cells[i];
        float hIn = 0, hOut = 0;
        int dIn = -1, dOut = -1;
        if (i > 0)
        {
            dIn = DirTo(c, cells[i - 1]);
            if (Conveyors.StepOf(cells[i - 1], c) == Conveyors.Step.RampDown) hIn = 1; // comes down across this tile
        }
        if (i + 1 < cells.Count)
        {
            dOut = DirTo(c, cells[i + 1]);
            if (Conveyors.StepOf(c, cells[i + 1]) == Conveyors.Step.RampUp) hOut = 1; // climbs across this tile
        }
        float hMid = dIn >= 0 && dOut >= 0 ? (hIn + hOut) / 2 : dIn >= 0 ? hIn : dOut >= 0 ? hOut * 0.5f : 0;
        Vector3 At(float dx, float dy, float h) => new(c.X + 0.5f + dx, c.Z + h + BeltHeight, c.Y + 0.5f + dy);
        var centre = At(0, 0, hMid);
        Vector3? entry = dIn >= 0 ? At(Dirs[dIn].X * 0.5f, Dirs[dIn].Y * 0.5f, hIn) : null;
        Vector3? exit = dOut >= 0 ? At(Dirs[dOut].X * 0.5f, Dirs[dOut].Y * 0.5f, hOut) : null;
        return (entry, centre, exit);
    }

    /// <summary>A unit run piece stretched from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static Transform3D Piece(Vector3 a, Vector3 b)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return new Transform3D(Basis.Identity.Scaled(Vector3.Zero), a);
        var x = d / len;
        var side = x.Cross(Vector3.Up);
        side = side.LengthSquared() < 1e-6f ? Vector3.Back : side.Normalized();
        var up = side.Cross(x).Normalized();
        return new Transform3D(new Basis(x * len, up, side), a);
    }

    /// <summary>
    /// The joint plate's basis on a turning ramp: its square stays square seen from above (edges along the two runs) and is
    /// sheared so its top follows the plane through the entry, the centre and the exit.
    /// </summary>
    private static Basis SlopedPlate(Vector3 entry, Vector3 centre, Vector3 exit)
    {
        var a = (centre - entry) * 2; // one unit across, rising with the run in
        var b = (exit - centre) * 2;  // one unit across, rising with the run out
        var up = b.Cross(a);
        if (up.Y < 0) { (a, b) = (b, a); up = -up; }
        return new Basis(a, up.Normalized(), b);
    }

    /// <summary>Per belt: each tile's centre and the point where it hands over to the next (the shared edge), for the goods.</summary>
    private readonly Dictionary<int, (Vector3[] Centre, Vector3[] Hand)> _geo = new();

    // ---- Drawing ----------------------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_sim == null) return;
        int level = _world.ViewLevel;
        if (level != _drawnLevel)
        {
            _drawnLevel = level;
            foreach (var m in _materials) m.SetShaderParameter("cut_height", (float)(level + 1));
        }
        var belts = _sim.Conveyors;
        long states = 0;
        foreach (var b in belts.All) states = states * 31 + (long)b.Status + 7 * (b.Blocked + 1);
        if (belts.Version != _drawnVersion || states != _drawnStates) Rebuild(states);
        DrawPackets((float)delta);
    }

    private readonly List<Transform3D> _runList = new(), _jointList = new(), _liftList = new(), _postList = new(), _driveList = new(), _hopperList = new(), _ghostList = new();
    private readonly List<Color> _runTint = new(), _jointTint = new();

    private void Rebuild(long states)
    {
        _drawnVersion = _sim.Conveyors.Version;
        _drawnStates = states;
        foreach (var l in new[] { _runList, _jointList, _liftList, _postList, _driveList, _hopperList, _ghostList }) l.Clear();
        _runTint.Clear();
        _jointTint.Clear();
        _geo.Clear();
        var w = _sim.World;
        foreach (var b in _sim.Conveyors.All)
        {
            // A belt that can't run is tinted: rust red when filled in or without power.
            var tint = b.Status is BeltStatus.Blocked or BeltStatus.NoPower ? new Color(1f, 0.45f, 0.4f) : new Color(1, 1, 1);
            var centres = new Vector3[b.Length];
            var hands = new Vector3[b.Length];
            for (int i = 0; i < b.Length; i++)
            {
                var c = b.Cells[i];
                var (entry, centre, exit) = Tile(b.Cells, i);
                centres[i] = centre;
                hands[i] = exit ?? centre;
                if (!b.IsBuilt(i))
                {
                    _ghostList.Add(new Transform3D(Basis.Identity, centre));
                    continue;
                }
                bool liftBelow = i > 0 && Conveyors.StepOf(b.Cells[i - 1], c) == Conveyors.Step.Lift;
                bool liftAbove = i + 1 < b.Length && Conveyors.StepOf(c, b.Cells[i + 1]) == Conveyors.Step.Lift;
                if (entry is { } e) { _runList.Add(Piece(e, centre)); _runTint.Add(tint); }
                if (exit is { } x) { _runList.Add(Piece(centre, x)); _runTint.Add(tint); }
                // A level plate under the centre where the belt turns, starts or ends (on a slope the runs meet cleanly).
                bool level = MathF.Abs((entry?.Y ?? centre.Y) - centre.Y) < 0.01f && MathF.Abs((exit?.Y ?? centre.Y) - centre.Y) < 0.01f;
                bool turns = entry is { } en && exit is { } ex && MathF.Abs(((en - centre) with { Y = 0 }).Dot((ex - centre) with { Y = 0 })) < 0.01f;
                if (level && (turns || entry is null || exit is null) && !(liftBelow && liftAbove))
                {
                    _jointList.Add(new Transform3D(Basis.Identity, centre));
                    _jointTint.Add(tint);
                }
                else if (turns)
                {
                    // A ramp that turns: its two halves lie in one tilted plane (in at one edge, out a level away at the
                    // other), so the plate is sheared into that plane and closes the corner between them.
                    _jointList.Add(new Transform3D(SlopedPlate(entry!.Value, centre, exit!.Value), centre));
                    _jointTint.Add(tint);
                }
                if (i == 0)
                {
                    _driveList.Add(new Transform3D(Basis.Identity, centre));
                    if (b.HasHopper && Colony.Core.Navigation.Movement.IsStandable(w, c)) _hopperList.Add(new Transform3D(Basis.Identity, new Vector3(c.X + 0.5f, c.Z, c.Y + 0.5f)));
                }
                // Trestle legs under a raised tile (not inside a lift's tower), down to the ground.
                if (!liftBelow)
                {
                    int drop = 0;
                    for (int z = c.Z - 1; z > 0 && drop < 64 && !TileInfo.IsSolid(w.Get(c.X, c.Y, z)); z--) drop++;
                    if (drop > 0)
                    {
                        float top = centre.Y - 0.08f, bottom = c.Z - drop;
                        _postList.Add(new Transform3D(Basis.Identity.Scaled(new Vector3(1, top - bottom, 1)), new Vector3(c.X + 0.5f, bottom, c.Y + 0.5f)));
                    }
                }
                // A lift tower rises from the lower tile of each pair, a level up from its centre.
                if (liftAbove && b.IsBuilt(i + 1))
                {
                    var lower = b.Cells[i + 1].Z > c.Z ? centre : Tile(b.Cells, i + 1).Centre;
                    _liftList.Add(new Transform3D(Basis.Identity, lower));
                }
            }
            _geo[b.Id] = (centres, hands);
        }
        Fill(_runs, _runList, _runTint);
        Fill(_joints, _jointList, _jointTint);
        Fill(_lifts, _liftList, null);
        Fill(_posts, _postList, null);
        Fill(_drives, _driveList, null);
        Fill(_hoppers, _hopperList, null);
        var gm = _ghosts.Multimesh;
        gm.InstanceCount = _ghostList.Count;
        for (int i = 0; i < _ghostList.Count; i++) gm.SetInstanceTransform(i, _ghostList[i]);
    }

    private static void Fill(MultiMeshInstance3D mmi, List<Transform3D> list, List<Color>? tints)
    {
        var mm = mmi.Multimesh;
        mm.InstanceCount = list.Count;
        for (int i = 0; i < list.Count; i++)
        {
            mm.SetInstanceTransform(i, list[i]);
            mm.SetInstanceCustomData(i, tints?[i] ?? new Color(1, 1, 1));
        }
    }

    /// <summary>Most goods drawn at once (a crate a tile).</summary>
    private const int MaxPackets = 20_000;

    /// <summary>
    /// Each crate eases from the tile it was on to the one it is on now over a belt step (the sim moves them a whole tile
    /// at a time), then a little smoothing hides the 20 Hz ticks.
    /// </summary>
    private void DrawPackets(float dt)
    {
        var belts = _sim.Conveyors;
        bool stepped = belts.Steps != _drawnSteps;
        if (stepped)
        {
            _drawnSteps = belts.Steps;
            _stepTick = _sim.Tick;
        }
        float t = Math.Clamp((_sim.Tick - _stepTick + 0.5f) / Balance.ConveyorStepTicks, 0, 1);
        var mm = _packets.Multimesh;
        int n = 0;
        var seen = stepped ? new HashSet<int>() : null;
        foreach (var b in belts.All)
        {
            if (b.Packets == 0 || !_geo.TryGetValue(b.Id, out var geo) || geo.Centre.Length != b.Length) continue;
            for (int i = 0; i < b.Length && n < MaxPackets; i++)
            {
                var p = b.PacketAt(i);
                if (p.Empty) continue;
                var at = geo.Centre[i];
                if (stepped)
                {
                    seen!.Add(p.Serial);
                    // Along its own belt it follows the belt (over the shared edge, up or down a ramp); onto another, straight.
                    var from = _moving.TryGetValue(p.Serial, out var was) ? was.To : at;
                    var via = _where.TryGetValue(p.Serial, out var w) && w.Belt == b.Id && w.Index == i - 1 ? geo.Hand[i - 1] : (from + at) / 2;
                    _moving[p.Serial] = (from, via, at);
                    _where[p.Serial] = (b.Id, i);
                }
                var (f, v, to) = _moving.TryGetValue(p.Serial, out var mv) ? mv : (at, at, at);
                var goal = t < 0.5f ? f.Lerp(v, t * 2) : v.Lerp(to, t * 2 - 1);
                var shown = _shown.TryGetValue(p.Serial, out var s) ? s.Lerp(goal, 1f - MathF.Exp(-dt * 30f)) : goal;
                _shown[p.Serial] = shown;
                if (n >= mm.InstanceCount) mm.InstanceCount = Math.Min(MaxPackets, Math.Max(64, mm.InstanceCount * 2));
                float yaw = (p.Serial % 7) * 0.2f;
                mm.SetInstanceTransform(n, new Transform3D(new Basis(Vector3.Up, yaw), shown));
                mm.SetInstanceCustomData(n, ItemView.ItemColor(p.Type).SrgbToLinear());
                n++;
            }
        }
        if (stepped)
        {
            // Forget crates that left the belts.
            var gone = new List<int>();
            foreach (var k in _moving.Keys) if (!seen!.Contains(k)) gone.Add(k);
            foreach (var k in gone) { _moving.Remove(k); _shown.Remove(k); _where.Remove(k); }
        }
        mm.VisibleInstanceCount = n;
    }
}
