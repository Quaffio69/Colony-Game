using System;
using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Industry;
using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>
/// Draws factory rooms: a tinted floor overlay per room cell (each room a slightly different shade, so
/// neighbouring rooms read apart) and a stand-in model per part. Sawbench blades spin while a worker is at
/// them. Rebuilds when <see cref="Rooms.Version"/> or the view level changes; parts are sliced by the
/// flora shader like plants.
/// </summary>
public partial class RoomView : Node3D
{
    public static Color RoomColor(RoomType t) => t switch
    {
        RoomType.Sawmill => new Color(1f, 0.68f, 0.3f, 0.2f),
        _ => new Color(1f, 1f, 1f, 0.2f),
    };

    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private Shader _shader = null!;
    private readonly List<ShaderMaterial> _materials = new();
    private readonly Dictionary<(PartType, int), Mesh> _meshes = new();
    private readonly Dictionary<int, Mesh> _blades = new(); // by size
    private Material _wood = null!, _darkWood = null!, _metal = null!, _inputWood = null!, _outputWood = null!, _flame = null!;
    private readonly Material _blueprint = Overlay.Material(new Color(0.45f, 0.75f, 1f, 0.35f));
    private readonly List<(Part Part, Node3D Node, List<Node3D> Blades)> _parts = new();
    private readonly List<MultiMeshInstance3D> _floors = new();
    private Node3D _root = null!;
    private int _drawnVersion = -1, _drawnLevel = int.MinValue, _cutLevel = int.MinValue;

    public void Initialize(ColonySim sim, WorldView world)
    {
        _sim = sim;
        _world = world;
        _shader = GD.Load<Shader>("res://shaders/flora.gdshader");
        BuildMeshes();
        _root = new Node3D { Name = "Rooms" };
        AddChild(_root);
    }

    public override void _Process(double delta)
    {
        if (_sim == null) return;
        int level = _world.ViewLevel;
        if (level != _cutLevel)
        {
            _cutLevel = level;
            foreach (var m in _materials) m.SetShaderParameter("cut_height", (float)(level + 1));
        }
        if (_sim.Rooms.Version != _drawnVersion || level != _drawnLevel) Rebuild(level);

        foreach (var (part, _, blades) in _parts)
            if (part.Working)
                foreach (var blade in blades) blade.RotateObjectLocal(Vector3.Forward, (float)delta * 14f);
    }

    private void Rebuild(int level)
    {
        _drawnVersion = _sim.Rooms.Version;
        _drawnLevel = level;
        foreach (var child in _root.GetChildren()) child.QueueFree();
        _parts.Clear();
        _floors.Clear();

        foreach (var room in _sim.Rooms.All)
        {
            // Floor overlay (cells on or below the cut surface).
            var cells = new List<TilePos>();
            foreach (var c in room.Cells)
                if (c.Z <= level + 1) cells.Add(c);
            var shade = RoomColor(room.Type);
            float jitter = (Colony.Core.Procedural.Noise.Hash(room.Id, 0, 0, 3) - 0.5f) * 0.25f;
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new PlaneMesh
                {
                    Size = new Vector2(0.98f, 0.98f),
                    Material = Overlay.Material(new Color(shade.R, Mathf.Clamp(shade.G + jitter, 0, 1), shade.B, shade.A)),
                },
                InstanceCount = cells.Count,
            };
            for (int i = 0; i < cells.Count; i++)
                mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(cells[i].X + 0.5f, cells[i].Z + 0.015f, cells[i].Y + 0.5f)));
            var floor = new MultiMeshInstance3D { Multimesh = mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _root.AddChild(floor);
            _floors.Add(floor);

            foreach (var part in room.Parts) AddPart(part);
        }
    }

    private void AddPart(Part part)
    {
        var last = part.Cells[^1];
        var centre = new Vector3((part.Origin.X + last.X + 1) / 2f, part.Origin.Z, (part.Origin.Y + last.Y + 1) / 2f);
        var node = new Node3D { Position = centre, Rotation = new Vector3(0, part.Rotated ? Mathf.Pi / 2 : 0, 0) };
        // Blueprints are translucent ghosts until built.
        var ghost = part.Built ? null : _blueprint;
        node.AddChild(new MeshInstance3D { Mesh = MeshFor(part.Type, part.Size), MaterialOverride = ghost, CastShadow = part.Built ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off });
        var blades = new List<Node3D>();
        if (part.Type == PartType.Sawbench)
            foreach (var at in BladePositions(part.Size))
            {
                var blade = new MeshInstance3D { Mesh = Blade(part.Size), Position = at, MaterialOverride = ghost };
                node.AddChild(blade);
                blades.Add(blade);
            }
        _root.AddChild(node);
        _parts.Add((part, node, blades));
    }

    // ---- Meshes -----------------------------------------------------------------------------------------

    private ShaderMaterial Material(Color albedo)
    {
        var m = new ShaderMaterial { Shader = _shader };
        m.SetShaderParameter("albedo", albedo);
        m.SetShaderParameter("tint_mix", 0.0f);
        m.SetShaderParameter("instanced", false);
        _materials.Add(m);
        return m;
    }

    private static Transform3D At(float x, float y, float z) => new(Basis.Identity, new Vector3(x, y, z));

    private static ArrayMesh Compose(params (PrimitiveMesh Mesh, Transform3D At, Material Mat)[] parts)
    {
        var mesh = new ArrayMesh();
        var groups = new List<(Material Mat, List<(PrimitiveMesh, Transform3D)> Items)>();
        foreach (var (m, at, mat) in parts)
        {
            int i = groups.FindIndex(g => g.Mat == mat);
            if (i < 0)
            {
                groups.Add((mat, new List<(PrimitiveMesh, Transform3D)>()));
                i = groups.Count - 1;
            }
            groups[i].Items.Add((m, at));
        }
        foreach (var (mat, items) in groups)
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var (m, at) in items) st.AppendFrom(m, 0, at);
            st.Commit(mesh);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
        }
        return mesh;
    }

    private static BoxMesh Box(float x, float y, float z) => new() { Size = new Vector3(x, y, z) };

    private void BuildMeshes()
    {
        _wood = Material(new Color(0.62f, 0.45f, 0.28f));
        _darkWood = Material(new Color(0.42f, 0.3f, 0.19f));
        _metal = Material(new Color(0.62f, 0.64f, 0.68f));
        _inputWood = Material(new Color(0.45f, 0.62f, 0.36f));
        _outputWood = Material(new Color(0.36f, 0.5f, 0.68f));
        _flame = Material(new Color(1f, 0.85f, 0.4f));
    }

    private Mesh MeshFor(PartType type, int size)
    {
        if (_meshes.TryGetValue((type, size), out var mesh)) return mesh;
        mesh = type switch
        {
            PartType.Sawbench => SawMesh(size),
            PartType.ToolRack => ToolMesh(size),
            // Lantern: a post with a glowing head.
            PartType.Lantern => Compose(
                (Box(0.07f, 1.4f, 0.07f), At(0, 0.7f, 0), _darkWood),
                (Box(0.24f, 0.26f, 0.24f), At(0, 1.48f, 0), _flame),
                (Box(0.3f, 0.05f, 0.3f), At(0, 1.63f, 0), _metal)),
            // Crates: open boxes, tinted green (in) and blue (out). Their contents are drawn by ItemView.
            PartType.InputCrate => Crate(_inputWood),
            _ => Crate(_outputWood),
        };
        _meshes[(type, size)] = mesh;
        return mesh;
    }

    private static readonly int[] BladeCount = { 1, 1, 2, 2, 3 };
    private static float BladeRadius(int size) => 0.26f + 0.05f * size;

    /// <summary>Blade hubs in the part's local space (x along the machine's length), spread along the table.</summary>
    private static IEnumerable<Vector3> BladePositions(int size)
    {
        var (w, _) = PartInfo.Footprint(PartType.Sawbench, size);
        int n = BladeCount[size - 1];
        float span = w - 1.0f;
        for (int i = 0; i < n; i++)
        {
            float x = n == 1 ? w * 0.12f : -span / 2 + span * (i + 0.5f) / n + 0.2f;
            yield return new Vector3(x, 0.78f + BladeRadius(size) * 0.25f, 0);
        }
    }

    private Mesh Blade(int size)
    {
        if (_blades.TryGetValue(size, out var mesh)) return mesh;
        float r = BladeRadius(size);
        var disc = new CylinderMesh { TopRadius = r, BottomRadius = r, Height = 0.03f, RadialSegments = 14, Rings = 1 };
        mesh = Compose(
            (disc, new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), Vector3.Zero), _metal),
            (Box(r * 1.6f, 0.05f, 0.04f), At(0, 0, 0), _darkWood)); // a spoke, so the spin reads
        _blades[size] = mesh;
        return mesh;
    }

    /// <summary>
    /// Saws grow from a trestle (saw horse) to a table saw, pit saw and frame saw with an overhead gantry, up to
    /// a mill saw with a log carriage and a drive housing. Blades are separate spinning nodes.
    /// </summary>
    private ArrayMesh SawMesh(int size)
    {
        var (w, d) = PartInfo.Footprint(PartType.Sawbench, size);
        float L = w - 0.15f, D = d - 0.25f, top = 0.72f;
        var parts = new List<(PrimitiveMesh, Transform3D, Material)>
        {
            (Box(L, 0.1f, D), At(0, top, 0), _wood),
        };
        // Legs every ~1.5 tiles along each long side.
        int legsAlong = Math.Max(2, (int)MathF.Ceiling(L / 1.5f) + 1);
        for (int i = 0; i < legsAlong; i++)
        {
            float x = -L / 2 + 0.08f + (L - 0.16f) * i / (legsAlong - 1);
            foreach (float z in new[] { -D / 2 + 0.08f, D / 2 - 0.08f })
                parts.Add((Box(0.09f, top - 0.04f, 0.09f), At(x, (top - 0.04f) / 2, z), _darkWood));
        }
        if (size >= 2) // a stretcher low between the legs
            parts.Add((Box(L - 0.2f, 0.06f, 0.06f), At(0, 0.18f, 0), _darkWood));

        // Logs waiting on the table: more and longer with the batch.
        int logs = Math.Min(PartInfo.Batch(PartType.Sawbench, size), 4);
        for (int i = 0; i < logs; i++)
        {
            float z = D > 1 ? -D / 2 + 0.25f + i * 0.24f : 0;
            parts.Add((new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.09f, Height = L * 0.4f, RadialSegments = 6, Rings = 1 },
                new Transform3D(new Basis(Vector3.Forward, Mathf.Pi / 2), new Vector3(-L * 0.25f, top + 0.14f, z)), _darkWood));
        }

        if (size >= 3)
        {
            // Overhead gantry: two portal frames and a beam along the blades.
            float h = 1.6f + 0.1f * size;
            foreach (float x in new[] { -L / 2 + 0.2f, L / 2 - 0.2f })
            {
                parts.Add((Box(0.1f, h, 0.1f), At(x, h / 2, -D / 2 - 0.02f), _wood));
                parts.Add((Box(0.1f, h, 0.1f), At(x, h / 2, D / 2 + 0.02f), _wood));
                parts.Add((Box(0.12f, 0.12f, D + 0.15f), At(x, h, 0), _wood));
            }
            parts.Add((Box(L - 0.3f, 0.1f, 0.12f), At(0, h, 0), _darkWood));
            if (size >= 4) // tensioned frame blades hanging from the beam
                for (int i = 0; i < 3; i++)
                    parts.Add((Box(0.03f, h - top - 0.15f, 0.02f), At(L * 0.18f + i * 0.12f, (h + top) / 2, 0), _metal));
        }
        if (size >= 5)
        {
            // Log carriage rails along the table and a drive housing at one end.
            foreach (float z in new[] { -0.35f, 0.35f })
                parts.Add((Box(L - 0.1f, 0.05f, 0.06f), At(0, top + 0.08f, z), _metal));
            parts.Add((Box(0.7f, 0.9f, 0.9f), At(L / 2 - 0.4f, 0.45f, D / 2 - 0.5f), _metal));
            parts.Add((new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.28f, Height = 0.12f, RadialSegments = 12, Rings = 1 },
                new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), new Vector3(L / 2 - 0.4f, 0.65f, D / 2 - 0.02f)), _darkWood));
        }
        return Compose(parts.ToArray());
    }

    /// <summary>
    /// Tools grow from a few pegs to a rack and a full tool wall, then a workbench with a rack behind it, and a
    /// walled tool crib with shelves.
    /// </summary>
    private ArrayMesh ToolMesh(int size)
    {
        var (w, d) = PartInfo.Footprint(PartType.ToolRack, size);
        float W = w - 0.2f, back = d / 2f - 0.15f;
        float boardH = 0.6f + 0.15f * size, boardY = 0.55f + boardH / 2;
        var parts = new List<(PrimitiveMesh, Transform3D, Material)>
        {
            (Box(W, boardH, 0.06f), At(0, boardY, back), _darkWood),
        };
        int posts = Math.Max(2, w + 1);
        for (int i = 0; i < posts; i++)
        {
            float x = -W / 2 + W * i / (posts - 1);
            parts.Add((Box(0.07f, boardY + boardH / 2 + 0.1f, 0.07f), At(x, (boardY + boardH / 2 + 0.1f) / 2, back), _wood));
        }
        // Hanging tools: handles of varying length plus a few heads, in rows on bigger boards.
        int rows = size >= 3 ? 2 : 1, perRow = 2 + w * 2;
        for (int r = 0; r < rows; r++)
        for (int i = 0; i < perRow; i++)
        {
            float x = -W / 2 + W * (i + 0.5f) / perRow;
            float len = 0.25f + 0.1f * ((i * 7 + r * 3) % 3);
            float y = boardY + boardH / 2 - 0.12f - r * (boardH / 2) - len / 2;
            parts.Add((Box(0.04f, len, 0.04f), At(x, y, back - 0.06f), (i + r) % 3 == 0 ? _wood : _metal));
            if ((i + r) % 2 == 0) parts.Add((Box(0.14f, 0.06f, 0.05f), At(x, y - len / 2, back - 0.06f), _metal));
        }
        if (d >= 2)
        {
            // Workbench in front of the board, with drawer pulls.
            float benchD = d - 0.9f, z = back - 0.15f - benchD / 2;
            parts.Add((Box(W, 0.08f, benchD), At(0, 0.8f, z), _wood));
            parts.Add((Box(W - 0.1f, 0.5f, benchD - 0.1f), At(0, 0.5f, z), _darkWood));
            for (int i = 0; i < w; i++)
                parts.Add((Box(0.16f, 0.04f, 0.03f), At(-W / 2 + W * (i + 0.5f) / w, 0.6f, z - benchD / 2 + 0.03f), _metal));
            parts.Add((Box(0.3f, 0.14f, 0.2f), At(-W / 4, 0.91f, z), _metal)); // a vice
        }
        if (size >= 5)
        {
            // Crib: side walls joined by a lintel, and shelves stocked with parts.
            float h = boardY + boardH / 2 + 0.15f;
            foreach (float x in new[] { -W / 2 - 0.02f, W / 2 + 0.02f })
                parts.Add((Box(0.06f, h, d - 0.3f), At(x, h / 2, 0), _darkWood));
            parts.Add((Box(W + 0.1f, 0.1f, 0.1f), At(0, h, -d / 2f + 0.2f), _wood)); // front lintel (open top, so the tools show)
            for (int i = 0; i < 3; i++)
                parts.Add((Box(0.2f, 0.12f, 0.2f), At(-W / 2 + 0.3f + i * 0.35f, 0.91f, back - 0.4f), _metal));
        }
        return Compose(parts.ToArray());
    }

    private static ArrayMesh Crate(Material mat) => Compose(
        (Box(0.9f, 0.06f, 0.9f), At(0, 0.03f, 0), mat),
        (Box(0.9f, 0.45f, 0.06f), At(0, 0.25f, 0.42f), mat),
        (Box(0.9f, 0.45f, 0.06f), At(0, 0.25f, -0.42f), mat),
        (Box(0.06f, 0.45f, 0.9f), At(0.42f, 0.25f, 0), mat),
        (Box(0.06f, 0.45f, 0.9f), At(-0.42f, 0.25f, 0), mat));
}
