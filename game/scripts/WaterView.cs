using System;
using System.Collections.Generic;
using System.Diagnostics;
using Colony.Core;
using Colony.Core.Fluids;
using Colony.Core.Orders;
using Colony.Core.World;
using Godot;
using static ColonyGame.MeshKit;

namespace ColonyGame;

/// <summary>
/// Draws water, water wheels, pumps and structure blueprints.
/// <para>
/// Water is meshed per section of 2x2 water blocks (32x32 columns, every level): for each wet open space in a
/// column, a surface at its water height, its corners smoothed towards the neighbouring columns' surfaces nearby
/// (rivers slope instead of stepping), and side faces where the water beside is lower (falls, a pour's edge).
/// Darker where deeper, foamy on falls; the flow goes to the shader as UV. A section rebuilds only when its water
/// visibly changes (<see cref="Water.BlockVersion"/>), within a per-frame budget. Sliced at the view level by the shader.
/// </para>
/// Wheels turn at a speed set by the power they make; blueprints are translucent ghosts.
/// </summary>
public partial class WaterView : Node3D
{
    private const double BuildBudgetMs = 3;

    private ColonySim _sim = null!;
    private WorldView _worldView = null!;
    private ShaderMaterial _water = null!;
    private Shader _flora = null!;
    private readonly List<ShaderMaterial> _sliced = new();
    private readonly Dictionary<(int X, int Y, int Z), (MeshInstance3D Node, long Signature)> _sections = new();
    private readonly Dictionary<(int X, int Y, int Z), long> _wanted = new();
    private readonly Dictionary<(int X, int Y, int Z), long> _border = new();
    private readonly List<(int X, int Y, int Z)> _todo = new();
    private int _turn;
    private Node3D _structures = null!;
    private readonly List<(WaterWheel Wheel, Node3D Rotor)> _wheels = new();
    private int _drawnWorks = -1, _drawnSites = -1, _cutLevel = int.MinValue;
    private Material _wood = null!, _darkWood = null!, _metal = null!, _stone = null!, _brass = null!;
    private readonly Material _ghost = Overlay.Material(new Color(0.45f, 0.75f, 1f, 0.35f));
    private readonly Material _wallGhost = Overlay.Material(new Color(0.75f, 0.72f, 0.68f, 0.45f));

    public void Initialize(ColonySim sim, WorldView worldView)
    {
        _sim = sim;
        _worldView = worldView;
        _water = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water.gdshader") };
        _flora = GD.Load<Shader>("res://shaders/flora.gdshader");
        _wood = Mat(new Color(0.6f, 0.43f, 0.27f));
        _darkWood = Mat(new Color(0.4f, 0.29f, 0.18f));
        _metal = Mat(new Color(0.6f, 0.62f, 0.66f));
        _stone = Mat(new Color(0.6f, 0.6f, 0.62f));
        _brass = Mat(new Color(0.78f, 0.6f, 0.3f));
        _structures = new Node3D { Name = "Waterworks" };
        AddChild(_structures);

        var world = sim.World;
        _capData = new byte[world.Width * world.Depth];
        _capImage = Image.CreateFromData(world.Width, world.Depth, false, Image.Format.R8, _capData);
        _capTexture = ImageTexture.CreateFromImage(_capImage);
        var capMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water_cap.gdshader") };
        capMaterial.SetShaderParameter("cells", _capTexture);
        _cap = new MeshInstance3D
        {
            Name = "WaterCap",
            Mesh = new PlaneMesh { Size = new Vector2(world.Width, world.Depth) },
            MaterialOverride = capMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_cap);
    }

    // ---- Water cut by the view level ---------------------------------------------------------------------
    // The slice hides every water surface above the cut, so a lake or flooded tunnel seen from a level below its
    // surface would vanish. A quad at the cut (like the terrain's cap) draws the water's cross-section there from a
    // map-sized R8 texture: per column, how far its water stands above the cut (1/16 levels). Rebuilt over the wet
    // blocks when the level changes, patched per block when a block's water visibly changes, uploaded at most a few
    // times a second.

    private const double CapUploadSeconds = 0.2;
    private byte[] _capData = [];
    private Image _capImage = null!;
    private ImageTexture _capTexture = null!;
    private MeshInstance3D _cap = null!;
    private int _capLevel = int.MinValue;
    private readonly Dictionary<int, int> _capVersions = new(); // wet block -> the version drawn into the cap
    private readonly HashSet<int> _capBlocks = new();           // blocks with water above the cut
    private readonly HashSet<int> _wetNow = new();
    private readonly List<int> _capGone = new();
    private bool _capChanged;
    private double _capWait;

    private void UpdateCap(double delta)
    {
        var water = _sim.Water;
        int level = _worldView.ViewLevel;
        if (level != _capLevel)
        {
            _capLevel = level;
            Array.Clear(_capData);
            _capBlocks.Clear();
            _capVersions.Clear();
            _capChanged = true;
            _capWait = 0; // show the new level at once
            _cap.Position = new Vector3(_sim.World.Width / 2f, level + 1.02f, _sim.World.Depth / 2f); // just over a floor at the cut
        }
        _wetNow.Clear();
        foreach (int bi in water.WetBlockIndices())
        {
            _wetNow.Add(bi);
            int v = water.BlockVersion(bi);
            if (_capVersions.TryGetValue(bi, out int had) && had == v) continue;
            _capVersions[bi] = v;
            _capChanged |= CapBlock(bi, dry: false);
        }
        if (_capVersions.Count > _wetNow.Count) // some blocks dried out
        {
            _capGone.Clear();
            foreach (int bi in _capVersions.Keys)
                if (!_wetNow.Contains(bi)) _capGone.Add(bi);
            foreach (int bi in _capGone)
            {
                _capVersions.Remove(bi);
                _capChanged |= CapBlock(bi, dry: true);
            }
        }
        _capWait -= delta;
        if (!_capChanged || _capWait > 0) return;
        _capChanged = false;
        _capWait = CapUploadSeconds;
        _capImage.SetData(_sim.World.Width, _sim.World.Depth, false, Image.Format.R8, _capData);
        _capTexture.Update(_capImage);
        _cap.Visible = _capBlocks.Count > 0;
    }

    /// <summary>Redraws one block's 16x16 columns into the cap; true if a texel changed.</summary>
    private bool CapBlock(int bi, bool dry)
    {
        var world = _sim.World;
        var water = _sim.Water;
        int x0 = bi % world.ChunksX * Chunk.Size, y0 = bi / world.ChunksX * Chunk.Size;
        float cut = _capLevel + 1;
        bool changed = false, any = false;
        for (int y = y0; y < Math.Min(y0 + Chunk.Size, world.Depth); y++)
        for (int x = x0; x < Math.Min(x0 + Chunk.Size, world.Width); x++)
        {
            byte value = 0;
            int spans = dry ? 0 : water.SpanCount(x, y);
            for (int k = 0; k < spans; k++)
            {
                var (floor, _, surface) = water.Span(x, y, k);
                if (floor > cut) break; // spans come lowest first: the rest are above the cut (sliced away)
                // The open space the cut passes through (or that rests on the cut level's floor), wet above the cut.
                if (surface is { } s && s > cut + 0.03f) value = (byte)Math.Clamp((int)MathF.Round((s - cut) * 16f), 1, 255);
            }
            ref byte texel = ref _capData[y * world.Width + x];
            changed |= texel != value;
            texel = value;
            any |= value != 0;
        }
        if (any) _capBlocks.Add(bi);
        else _capBlocks.Remove(bi);
        return changed;
    }

    private ShaderMaterial Mat(Color c)
    {
        var m = new ShaderMaterial { Shader = _flora };
        m.SetShaderParameter("albedo", c);
        m.SetShaderParameter("tint_mix", 0f);
        m.SetShaderParameter("instanced", false);
        _sliced.Add(m);
        return m;
    }

    /// <summary>Water sections still waiting to be meshed (for loading progress).</summary>
    public int PendingSections { get; private set; }

    public override void _Process(double delta)
    {
        if (_sim == null) return;
        int level = _worldView.ViewLevel;
        if (level != _cutLevel)
        {
            _cutLevel = level;
            _water.SetShaderParameter("cut_height", (float)(level + 1));
            foreach (var m in _sliced) m.SetShaderParameter("cut_height", (float)(level + 1));
        }
        if (_sim.WaterReady) // nothing to mesh until the generated water has settled
        {
            UpdateWater();
            UpdateCap(delta);
        }
        if (_sim.Waterworks.Version != _drawnWorks || _sim.Constructions.Version != _drawnSites) RebuildStructures();
        foreach (var (wheel, rotor) in _wheels)
            rotor.RotateObjectLocal(Vector3.Forward, (float)delta * (0.2f + wheel.Power * 0.25f));
    }

    // ---- Water -------------------------------------------------------------------------------------------

    private void UpdateWater()
    {
        var water = _sim.Water;
        var world = _sim.World;
        // What each wet section (2x2 blocks of 16x16 columns) should look like now: its blocks' versions, and its
        // neighbouring blocks' (surfaces are smoothed across section borders, and falls drawn against them).
        _wanted.Clear();
        foreach (int bi in water.WetBlockIndices())
        {
            int bx = bi % world.ChunksX, by = bi / world.ChunksX;
            var key = (bx >> 1, by >> 1, 0);
            _wanted[key] = _wanted.GetValueOrDefault(key, 17) * 31 + water.BlockVersion(bi) * 7919L + bi;
        }
        _border.Clear();
        foreach (var (key, _) in _wanted)
        {
            long sig = 0;
            for (int by = key.Y * 2 - 1; by <= key.Y * 2 + 2; by++)
            for (int bx = key.X * 2 - 1; bx <= key.X * 2 + 2; bx++)
            {
                if (bx < 0 || by < 0 || bx >= world.ChunksX || by >= world.ChunksY) continue;
                if (bx >> 1 == key.X && by >> 1 == key.Y) continue;
                sig = sig * 37 + water.BlockVersion(by * world.ChunksX + bx);
            }
            _border[key] = sig;
        }
        foreach (var (key, extra) in _border) _wanted[key] = _wanted[key] * 41 + extra;
        // Dried-out sections go.
        var gone = new List<(int, int, int)>();
        foreach (var (key, (node, _)) in _sections)
            if (!_wanted.ContainsKey(key))
            {
                node.QueueFree();
                gone.Add(key);
            }
        foreach (var key in gone) _sections.Remove(key);

        // Sections never drawn first, then changed ones taking turns (a busy river can't starve the rest).
        _todo.Clear();
        foreach (var (key, sig) in _wanted)
            if (!_sections.TryGetValue(key, out var have) || have.Signature != sig) _todo.Add(key);
        _todo.Sort((a, b) =>
        {
            bool na = !_sections.ContainsKey(a), nb = !_sections.ContainsKey(b);
            if (na != nb) return na ? -1 : 1;
            return a.CompareTo(b);
        });
        int unbuilt = 0;
        while (unbuilt < _todo.Count && !_sections.ContainsKey(_todo[unbuilt])) unbuilt++;
        if (unbuilt == 0 && _todo.Count > 0)
        {
            int start = _turn++ % _todo.Count;
            _todo.AddRange(_todo.GetRange(0, start));
            _todo.RemoveRange(0, start);
        }
        var clock = Stopwatch.StartNew();
        int pending = 0;
        foreach (var key in _todo)
        {
            long sig = _wanted[key];
            _sections.TryGetValue(key, out var have);
            if (clock.Elapsed.TotalMilliseconds > BuildBudgetMs)
            {
                pending++;
                continue;
            }
            var mesh = BuildSection(key.X, key.Y);
            if (have.Node is null)
            {
                have.Node = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                AddChild(have.Node);
            }
            have.Node.Mesh = mesh;
            _sections[key] = (have.Node, sig);
        }
        PendingSections = pending;
    }

    /// <summary>
    /// The water surface a neighbouring column shows at about height <paramref name="h"/>: the surface of its open
    /// space around that height, if it's wet and within half a level (closer is the same sheet of water; further
    /// is a fall or a pool below, which shouldn't be smoothed into this one).
    /// </summary>
    private float? NearSurface(int x, int y, float h)
    {
        if (!_sim.World.InBounds(x, y, 0)) return null;
        int z = Math.Max(0, (int)MathF.Floor(h - 0.01f));
        var s = _sim.Water.SurfaceAt(x, y, z) ?? (z > 0 ? _sim.Water.SurfaceAt(x, y, z - 1) : null);
        return s is { } v && MathF.Abs(v - h) < 0.5f ? v : null;
    }

    /// <summary>
    /// A section's water: for every wet open space in each column, a surface at its water height whose corners are
    /// averaged with the neighbouring columns' surfaces nearby (so a river's surface slopes smoothly instead of in
    /// steps), and side faces where the water beside it is lower or there is none (falls, the edge of a pour).
    /// Darker where deeper; foamy on falls and where water pours over them; UV carries the flow for the shader.
    /// </summary>
    private ArrayMesh? BuildSection(int sx, int sy)
    {
        var world = _sim.World;
        var water = _sim.Water;
        var faces = new Faces();
        int x0 = sx * 32, y0 = sy * 32;
        for (int y = y0; y < Math.Min(y0 + 32, world.Depth); y++)
        for (int x = x0; x < Math.Min(x0 + 32, world.Width); x++)
        {
            int spans = water.SpanCount(x, y);
            for (int k = 0; k < spans; k++)
            {
                var (floor, ceil, surface) = water.Span(x, y, k);
                if (surface is not { } h) continue;
                float depth = h - floor;
                if (depth < 0.02f) continue;
                var (fx, fy) = water.FlowAt(x, y, floor);
                var flow = new Vector2(fx, fy);
                // Sides first: where the water beside is lower (or absent), and whether it pours over the edge.
                bool pours = false;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (!world.InBounds(nx, ny, 0)) continue;
                    int zTop = Math.Max(floor, (int)MathF.Floor(h - 0.01f));
                    if (TileInfo.IsSolid(world.Get(nx, ny, zTop))) continue; // a bank: the ground shows through
                    // The neighbour's water (or its floor) at the top of ours.
                    float lo;
                    if (water.SurfaceAt(nx, ny, zTop) is { } hn) lo = hn;
                    else
                    {
                        int fz = zTop;
                        while (fz > floor && !TileInfo.IsSolid(world.Get(nx, ny, fz - 1)) && water.SurfaceAt(nx, ny, fz - 1) is null) fz--;
                        lo = water.SurfaceAt(nx, ny, fz - 1) is { } below && fz > floor ? below : fz;
                    }
                    lo = MathF.Max(lo, floor);
                    if (lo >= h - 0.03f) continue;
                    bool fall = h - lo > 0.35f;
                    pours |= fall;
                    var c = new Color(Shallow(h - lo), 0, 0, fall ? 0.7f : 0.15f);
                    Side(faces, x, y, d, lo, h, c, new Vector2(0, -1.2f)); // falling water streams down
                }
                // Surface, smoothed at the corners towards neighbours on the same sheet of water.
                if (h >= ceil - 0.01f) continue; // a flooded tunnel: no open surface
                float Corner(int cx, int cy)
                {
                    float sum = h;
                    int n = 1;
                    for (int dy = -1; dy <= 0; dy++)
                    for (int dx = -1; dx <= 0; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx == x && ny == y) continue;
                        if (NearSurface(nx, ny, h) is { } s)
                        {
                            sum += s;
                            n++;
                        }
                    }
                    return sum / n;
                }
                float h00 = Corner(x, y), h10 = Corner(x + 1, y), h11 = Corner(x + 1, y + 1), h01 = Corner(x, y + 1);
                var color = new Color(Shallow(depth), 0, 0, pours ? 0.35f : 0f);
                faces.Add(new Vector3(x, h00, y), new Vector3(x + 1, h10, y), new Vector3(x + 1, h11, y + 1), new Vector3(x, h01, y + 1),
                    Vector3.Up, color, flow);
            }
        }
        if (faces.Count == 0) return null;
        var mesh = new ArrayMesh();
        faces.AddTo(mesh);
        mesh.SurfaceSetMaterial(0, _water);
        return mesh;
    }

    /// <summary>Vertex colour red: 1 for shallow water, falling towards 0 (deep blue) by about four levels down.</summary>
    private static float Shallow(float depth) => Mathf.Clamp(1f - depth / 4f, 0f, 1f);

    private static void Side(Faces faces, int x, int y, int d, float lo, float hi, Color c, Vector2 uv)
    {
        switch (d)
        {
            case 0: faces.Add(new(x + 1, lo, y), new(x + 1, lo, y + 1), new(x + 1, hi, y + 1), new(x + 1, hi, y), Vector3.Right, c, uv); break;
            case 1: faces.Add(new(x, lo, y + 1), new(x, lo, y), new(x, hi, y), new(x, hi, y + 1), Vector3.Left, c, uv); break;
            case 2: faces.Add(new(x + 1, lo, y + 1), new(x, lo, y + 1), new(x, hi, y + 1), new(x + 1, hi, y + 1), Vector3.Back, c, uv); break;
            default: faces.Add(new(x, lo, y), new(x + 1, lo, y), new(x + 1, hi, y), new(x, hi, y), Vector3.Forward, c, uv); break;
        }
    }

    /// <summary>Quads collected for one mesh surface.</summary>
    private sealed class Faces
    {
        private readonly List<Vector3> _verts = new(), _normals = new();
        private readonly List<Color> _colors = new();
        private readonly List<Vector2> _uvs = new();
        public int Count => _verts.Count;

        public void Add(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color color, Vector2 uv)
        {
            _verts.Add(a); _verts.Add(b); _verts.Add(c); _verts.Add(d);
            for (int k = 0; k < 4; k++)
            {
                _normals.Add(normal);
                _colors.Add(color);
                _uvs.Add(uv);
            }
        }

        public void AddTo(ArrayMesh mesh)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _verts.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = _uvs.ToArray();
            var indices = new int[_verts.Count / 4 * 6];
            for (int q = 0, v = 0; v < _verts.Count; q += 6, v += 4)
            {
                indices[q] = v; indices[q + 1] = v + 1; indices[q + 2] = v + 2;
                indices[q + 3] = v; indices[q + 4] = v + 2; indices[q + 5] = v + 3;
            }
            arrays[(int)Mesh.ArrayType.Index] = indices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }
    }

    // ---- Wheels, pumps, blueprints ------------------------------------------------------------------------

    private void RebuildStructures()
    {
        _drawnWorks = _sim.Waterworks.Version;
        _drawnSites = _sim.Constructions.Version;
        foreach (var child in _structures.GetChildren()) child.QueueFree();
        _wheels.Clear();
        foreach (var wheel in _sim.Waterworks.Wheels)
        {
            var (node, rotor) = WheelNode(wheel.Cell, wheel.Facing, null);
            _structures.AddChild(node);
            _wheels.Add((wheel, rotor));
        }
        foreach (var pump in _sim.Waterworks.Pumps) _structures.AddChild(PumpNode(pump.Cell, pump.Facing, null));
        foreach (var bp in _sim.Constructions.All)
        {
            switch (bp.Kind)
            {
                case StructureKind.Wall:
                    _structures.AddChild(new MeshInstance3D
                    {
                        Mesh = new BoxMesh { Size = Vector3.One * 0.96f },
                        MaterialOverride = _wallGhost,
                        Position = SimSpace.TileCenter(bp.Cell),
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    });
                    break;
                case StructureKind.WaterWheel:
                    _structures.AddChild(WheelNode(bp.Cell, bp.Facing, _ghost).Node);
                    break;
                default:
                    _structures.AddChild(PumpNode(bp.Cell, bp.Facing, _ghost));
                    break;
            }
        }
    }

    /// <summary>Godot yaw for a facing (0..3: +x, +y, -x, -y in sim space).</summary>
    private static float Yaw(int facing) => -facing * Mathf.Pi / 2;

    /// <summary>A paddle wheel whose axle runs across the facing direction, on a timber frame.</summary>
    private (Node3D Node, Node3D Rotor) WheelNode(TilePos cell, int facing, Material? ghost)
    {
        var node = new Node3D { Position = new Vector3(cell.X + 0.5f, cell.Z, cell.Y + 0.5f), Rotation = new Vector3(0, Yaw(facing), 0) };
        var frame = new Parts();
        foreach (float z in new[] { -0.45f, 0.45f })
        {
            frame.Add(Box(0.1f, 1.0f, 0.1f), At(0, 0.5f, z), _darkWood);
            frame.Add(Box(0.14f, 0.14f, 0.14f), At(0, 0.95f, z), _metal);
        }
        node.AddChild(new MeshInstance3D { Mesh = Compose(frame), MaterialOverride = ghost });
        var rotorParts = new Parts();
        rotorParts.Add(Cyl(0.06f, 0.95f, 8), AlongZ(0, 0, 0), _metal);
        rotorParts.Add(Ring(0.7f, 0.78f, 18), At(0, 0, -0.3f, Mathf.Pi / 2), _wood);
        rotorParts.Add(Ring(0.7f, 0.78f, 18), At(0, 0, 0.3f, Mathf.Pi / 2), _wood);
        for (int k = 0; k < 8; k++)
        {
            float a = k * Mathf.Tau / 8;
            rotorParts.Add(Box(0.05f, 0.72f, 0.05f), At(Mathf.Cos(a) * 0.36f, Mathf.Sin(a) * 0.36f, 0, 0, 0, a - Mathf.Pi / 2), _darkWood);
            rotorParts.Add(Box(0.08f, 0.22f, 0.66f), At(Mathf.Cos(a) * 0.78f, Mathf.Sin(a) * 0.78f, 0, 0, 0, a), _wood); // paddle
        }
        var rotor = new MeshInstance3D { Mesh = Compose(rotorParts), Position = new Vector3(0, 0.95f, 0), MaterialOverride = ghost };
        node.AddChild(rotor);
        return (node, rotor);
    }

    /// <summary>An Archimedes screw in a trough, rising from the cell towards the outlet ahead and one level up.</summary>
    private Node3D PumpNode(TilePos cell, int facing, Material? ghost)
    {
        var node = new Node3D { Position = new Vector3(cell.X + 0.5f, cell.Z, cell.Y + 0.5f), Rotation = new Vector3(0, Yaw(facing), 0) };
        var p = new Parts();
        float tilt = Mathf.Atan2(1f, 1f); // one up per one ahead
        var along = Basis.FromEuler(new Vector3(0, 0, -Mathf.Pi / 2 + tilt));
        p.Add(Cyl(0.16f, 1.5f, 10), new Transform3D(along, new Vector3(0.5f, 0.6f, 0)), _wood); // the tube
        for (int k = 0; k < 6; k++) // helix bands
            p.Add(Ring(0.15f, 0.2f, 12), new Transform3D(along, new Vector3(0.5f, 0.6f, 0) + along.Y * (-0.6f + k * 0.24f)), _brass);
        p.Add(Box(0.5f, 0.3f, 0.5f), At(-0.05f, 0.15f, 0), _stone); // intake housing
        p.Add(Box(0.4f, 0.12f, 0.5f), At(1.0f, 1.12f, 0), _darkWood); // outlet spout
        p.Add(Cyl(0.12f, 0.2f, 10), AlongZ(1.0f, 1.25f, 0.28f), _metal); // gear box
        node.AddChild(new MeshInstance3D { Mesh = Compose(p), MaterialOverride = ghost });
        return node;
    }
}
