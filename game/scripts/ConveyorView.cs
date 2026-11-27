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
    private readonly MultiMeshInstance3D[] _shapes = new MultiMeshInstance3D[ShapeCount];
    private readonly List<Transform3D>[] _shapeList = new List<Transform3D>[ShapeCount];
    private readonly List<Color>[] _shapeTint = new List<Color>[ShapeCount];
    private MultiMeshInstance3D _joints = null!, _lifts = null!, _posts = null!, _drives = null!, _hoppers = null!, _ghosts = null!, _packets = null!;
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
        for (int k = 0; k < ShapeCount; k++) _shapes[k] = Add(ShapeMesh(k, belt, wood, iron));
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

    // ---- Models (Godot space, +Y up; the runs themselves are swept shapes, below) ------------------------------------

    /// <summary>The plate under a belt's first or last tile, where it starts or stops.</summary>
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

    /// <summary>A tile's directions (0..3, -1 for none: an end, or straight up or down a lift) and the surface height over
    /// its floor where it comes in and goes out (1 where a ramp crosses it a level up).</summary>
    private static (int DIn, int DOut, float HIn, float HOut) Lay(IReadOnlyList<TilePos> cells, int i)
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
        return (dIn, dOut, hIn, hOut);
    }

    /// <summary>
    /// How tile <paramref name="i"/> of a route is laid: where it enters and leaves (an edge midpoint, or none at an end or
    /// on a lift) and the middle of its run. A ramp climbs across the lower tile of the pair, edge to edge (in at the
    /// floor, out a level up), so a run of ramps is one smooth slope and nothing cuts into the hill it climbs; a turn is a
    /// quarter circle between the two edges, climbing evenly round it when it is a ramp too (<see cref="ShapeMesh"/>).
    /// </summary>
    public static (Vector3? In, Vector3 Centre, Vector3? Out) Tile(IReadOnlyList<TilePos> cells, int i)
    {
        var c = cells[i];
        var (dIn, dOut, hIn, hOut) = Lay(cells, i);
        float hMid = dIn >= 0 && dOut >= 0 ? (hIn + hOut) / 2 : dIn >= 0 ? hIn : dOut >= 0 ? hOut * 0.5f : 0;
        Vector3 At(float dx, float dy, float h) => new(c.X + 0.5f + dx, c.Z + h + BeltHeight, c.Y + 0.5f + dy);
        var centre = At(0, 0, hMid);
        if (dIn >= 0 && dOut >= 0 && (dIn + dOut) % 2 == 1)
        {
            // A turn: the middle of the quarter circle round the corner between the two edges.
            float k = 0.5f - 0.5f * MathF.Sqrt(0.5f);
            centre = At((Dirs[dIn].X + Dirs[dOut].X) * k, (Dirs[dIn].Y + Dirs[dOut].Y) * k, hMid);
        }
        Vector3? entry = dIn >= 0 ? At(Dirs[dIn].X * 0.5f, Dirs[dIn].Y * 0.5f, hIn) : null;
        Vector3? exit = dOut >= 0 ? At(Dirs[dOut].X * 0.5f, Dirs[dOut].Y * 0.5f, hOut) : null;
        return (entry, centre, exit);
    }

    // ---- Run shapes: the belt and its rails swept along each tile's path ---------------------------------------------
    // A tile's run is one of a few shapes, laid out with its way forward along +X from the tile's centre (turned to face
    // the run when drawn): straight across, a quarter turn in from the left or the right side, or half a tile at a start
    // or an end; each flat or climbing. Its cross-section stands upright (a belt between two rails) and is swept along
    // the path, so every tile meets its neighbours with the same section at the shared edge, sloping or not.

    private enum ShapeKind { Straight, FromLeft, FromRight, Start, End }

    private const int ShapeCount = 20;

    private static int ShapeKey(ShapeKind kind, float hIn, float hOut) => (int)kind * 4 + (hIn > 0 ? 2 : 0) + (hOut > 0 ? 1 : 0);

    /// <summary>A tile's shape and the turn that faces it along its run, or null inside a lift.</summary>
    private static (int Key, Basis Turn)? Shape(IReadOnlyList<TilePos> cells, int i)
    {
        var (dIn, dOut, hIn, hOut) = Lay(cells, i);
        if (dIn < 0 && dOut < 0) return null;
        int forward = dOut >= 0 ? dOut : (dIn + 2) % 4;
        var kind = dOut < 0 ? ShapeKind.End : dIn < 0 ? ShapeKind.Start
            : ((dIn - dOut + 4) % 4) switch { 2 => ShapeKind.Straight, 1 => ShapeKind.FromLeft, _ => ShapeKind.FromRight };
        return (ShapeKey(kind, hIn, hOut), new Basis(Vector3.Up, -forward * Mathf.Pi / 2));
    }

    /// <summary>
    /// The point at t (0..1) along a shape's path and its level way forward, in the shape's own space (+X forward;
    /// <see cref="ShapeKind.FromLeft"/> comes in from +Z, which is a tile's direction 1 turned with it).
    /// </summary>
    private static (Vector3 At, Vector3 Ahead) PathAt(ShapeKind kind, float hIn, float hOut, float t)
    {
        switch (kind)
        {
            case ShapeKind.FromLeft:
            case ShapeKind.FromRight:
            {
                float e = kind == ShapeKind.FromLeft ? 1 : -1, phi = t * Mathf.Pi / 2;
                var at = new Vector3(0.5f - 0.5f * MathF.Cos(phi), hIn + (hOut - hIn) * t, e * (0.5f - 0.5f * MathF.Sin(phi)));
                return (at, new Vector3(MathF.Sin(phi), 0, -e * MathF.Cos(phi)));
            }
            case ShapeKind.Start: return (new Vector3(0.5f * t, hOut * 0.5f * (1 + t), 0), Vector3.Right);
            case ShapeKind.End: return (new Vector3(-0.5f + 0.5f * t, hIn, 0), Vector3.Right);
            default: return (new Vector3(-0.5f + t, hIn + (hOut - hIn) * t, 0), Vector3.Right);
        }
    }

    /// <summary>The cross-section, as (side from, side to, height from, height to, material: 0 belt, 1 wood).</summary>
    private static readonly (float S0, float S1, float U0, float U1, int Mat)[] Section =
    [
        (-0.25f, 0.25f, -0.035f, 0f, 0),  // the belt
        (0.26f, 0.32f, -0.07f, 0.03f, 1), // side rails
        (-0.32f, -0.26f, -0.07f, 0.03f, 1),
    ];

    private static Mesh ShapeMesh(int key, Material belt, Material wood, Material iron)
    {
        var kind = (ShapeKind)(key / 4);
        float hIn = (key & 2) != 0 ? 1 : 0, hOut = (key & 1) != 0 ? 1 : 0;
        int steps = kind is ShapeKind.FromLeft or ShapeKind.FromRight ? 12 : 1;
        var tools = new SurfaceTool[3];
        for (int m = 0; m < 3; m++)
        {
            tools[m] = new SurfaceTool();
            tools[m].Begin(Mesh.PrimitiveType.Triangles);
        }
        // Rings along the path: the point, the level side (right of the way forward) and the way along the slope.
        var rings = new (Vector3 At, Vector3 Side, Vector3 Tangent)[steps + 1];
        for (int k = 0; k <= steps; k++)
        {
            float t = k / (float)steps;
            var (at, ahead) = PathAt(kind, hIn, hOut, t);
            var a0 = PathAt(kind, hIn, hOut, Math.Max(0, t - 0.01f)).At;
            var a1 = PathAt(kind, hIn, hOut, Math.Min(1, t + 0.01f)).At;
            rings[k] = (at, ahead.Cross(Vector3.Up).Normalized(), (a1 - a0).Normalized());
        }
        foreach (var (s0, s1, u0, u1, mat) in Section)
        {
            var st = tools[mat];
            Vector3 P(int k, float s, float u) => rings[k].At + rings[k].Side * s + Vector3.Up * u;
            for (int k = 0; k < steps; k++)
            {
                var n0 = TopNormal(rings[k]);
                var n1 = TopNormal(rings[k + 1]);
                var r0 = rings[k].Side;
                var r1 = rings[k + 1].Side;
                Quad(st, P(k, s0, u1), P(k, s1, u1), P(k + 1, s1, u1), P(k + 1, s0, u1), n0, n0, n1, n1);     // top
                Quad(st, P(k, s0, u0), P(k, s1, u0), P(k + 1, s1, u0), P(k + 1, s0, u0), -n0, -n0, -n1, -n1); // bottom
                Quad(st, P(k, s1, u0), P(k, s1, u1), P(k + 1, s1, u1), P(k + 1, s1, u0), r0, r0, r1, r1);     // right
                Quad(st, P(k, s0, u0), P(k, s0, u1), P(k + 1, s0, u1), P(k + 1, s0, u0), -r0, -r0, -r1, -r1); // left
            }
            // End caps (inside the next tile where the belt goes on; its end where it stops).
            var back = -rings[0].Tangent with { Y = 0 };
            var front = rings[steps].Tangent with { Y = 0 };
            Quad(st, P(0, s0, u0), P(0, s1, u0), P(0, s1, u1), P(0, s0, u1), back, back, back, back);
            Quad(st, P(steps, s0, u0), P(steps, s1, u0), P(steps, s1, u1), P(steps, s0, u1), front, front, front, front);
        }
        // Iron rollers across under the belt, two to a tile (one on half a tile).
        foreach (float t in kind is ShapeKind.Start or ShapeKind.End ? new[] { 0.5f } : new[] { 0.25f, 0.75f })
        {
            var (at, ahead) = PathAt(kind, hIn, hOut, t);
            var side = ahead.Cross(Vector3.Up).Normalized();
            tools[2].AppendFrom(Cyl(0.035f, 0.54f, 6), 0, new Transform3D(new Basis(ahead, side, ahead.Cross(side)), at + Vector3.Up * -0.06f));
        }
        var mesh = new ArrayMesh();
        var mats = new[] { belt, wood, iron };
        for (int m = 0; m < 3; m++)
        {
            tools[m].Commit(mesh);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mats[m]);
        }
        return mesh;
    }

    private static Vector3 TopNormal((Vector3 At, Vector3 Side, Vector3 Tangent) ring)
    {
        var n = ring.Side.Cross(ring.Tangent).Normalized();
        return n.Y < 0 ? -n : n;
    }

    /// <summary>A quad a-b-c-d with a normal per corner, wound to face its normals (Godot's front faces are clockwise).</summary>
    private static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 na, Vector3 nb, Vector3 nc, Vector3 nd)
    {
        bool flip = (b - a).Cross(c - a).Dot(na + nb + nc + nd) > 0;
        void V(Vector3 p, Vector3 n)
        {
            st.SetNormal(n);
            st.AddVertex(p);
        }
        if (flip) { V(a, na); V(c, nc); V(b, nb); V(a, na); V(d, nd); V(c, nc); }
        else { V(a, na); V(b, nb); V(c, nc); V(a, na); V(c, nc); V(d, nd); }
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

    private readonly List<Transform3D> _jointList = new(), _liftList = new(), _postList = new(), _driveList = new(), _hopperList = new(), _ghostList = new();
    private readonly List<Color> _jointTint = new();

    private void Rebuild(long states)
    {
        _drawnVersion = _sim.Conveyors.Version;
        _drawnStates = states;
        foreach (var l in new[] { _jointList, _liftList, _postList, _driveList, _hopperList, _ghostList }) l.Clear();
        for (int k = 0; k < ShapeCount; k++)
        {
            (_shapeList[k] ??= new()).Clear();
            (_shapeTint[k] ??= new()).Clear();
        }
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
                if (Shape(b.Cells, i) is var (key, turn))
                {
                    _shapeList[key].Add(new Transform3D(turn, new Vector3(c.X + 0.5f, c.Z + BeltHeight, c.Y + 0.5f)));
                    _shapeTint[key].Add(tint);
                }
                // A level plate under the centre where the belt starts or stops.
                bool level = MathF.Abs((entry?.Y ?? centre.Y) - centre.Y) < 0.01f && MathF.Abs((exit?.Y ?? centre.Y) - centre.Y) < 0.01f;
                if (level && (entry is null || exit is null) && !(liftBelow && liftAbove))
                {
                    _jointList.Add(new Transform3D(Basis.Identity, centre));
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
        for (int k = 0; k < ShapeCount; k++) Fill(_shapes[k], _shapeList[k], _shapeTint[k]);
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
