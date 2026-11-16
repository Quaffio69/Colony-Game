using System;
using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Industry;
using Colony.Core.World;
using Godot;
using static ColonyGame.MeshKit;

namespace ColonyGame;

/// <summary>
/// Draws factory rooms: a tinted floor overlay per room cell (each workshop its own colour, each room a slightly
/// different shade so neighbours read apart) and a stand-in model per part. Every station has its own model,
/// growing with its size, built from primitives in <see cref="StationModel"/>; wheels, blades, rollers and
/// crystals spin and fires flare while a worker is at the machine. Rebuilds when <see cref="Rooms.Version"/> or
/// the view level changes; parts are sliced by the flora shader like plants.
/// </summary>
public partial class RoomView : Node3D
{
    public static Color RoomColor(RoomType t) => Rgb(t.Color) with { A = 0.2f };

    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private Shader _shader = null!;
    private readonly List<ShaderMaterial> _materials = new();
    private readonly Dictionary<(PartType, int), Built> _models = new();
    private readonly Material _blueprint = Overlay.Material(new Color(0.45f, 0.75f, 1f, 0.35f));
    private readonly List<(Part Part, List<(Node3D Node, Vector3 Axis, float Speed)> Spinners, Node3D? Active)> _parts = new();
    private Node3D _root = null!;
    private int _drawnVersion = -1, _drawnLevel = int.MinValue, _cutLevel = int.MinValue;

    // Palette.
    private Material _wood = null!, _darkWood = null!, _metal = null!, _darkMetal = null!, _brass = null!, _copper = null!,
        _stone = null!, _darkStone = null!, _brick = null!, _clay = null!, _soot = null!, _cloth = null!, _red = null!,
        _blue = null!, _paper = null!, _glass = null!, _fire = null!, _ember = null!, _aether = null!, _rune = null!,
        _gold = null!, _green = null!, _white = null!, _inputWood = null!, _outputWood = null!, _flame = null!;

    /// <summary>A model: a static mesh, parts that spin while working, and parts shown only while working.</summary>
    private sealed record Built(Mesh Static, List<(Mesh Mesh, Vector3 At, Vector3 Axis, float Speed)> Spinners, Mesh? Active);

    private sealed class Model
    {
        public readonly Parts Static = new();
        public readonly Parts Active = new();
        public readonly List<(Parts Parts, Vector3 At, Vector3 Axis, float Speed)> Spin = new();

        /// <summary>A spinning sub-part built around its own hub at <paramref name="at"/>.</summary>
        public Parts Spinner(Vector3 at, Vector3 axis, float speed)
        {
            var p = new Parts();
            Spin.Add((p, at, axis, speed));
            return p;
        }
    }

    public void Initialize(ColonySim sim, WorldView world)
    {
        _sim = sim;
        _world = world;
        _shader = GD.Load<Shader>("res://shaders/flora.gdshader");
        BuildMaterials();
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

        foreach (var (part, spinners, active) in _parts)
        {
            if (active is not null) active.Visible = part.Working;
            if (!part.Working) continue;
            foreach (var (node, axis, speed) in spinners) node.RotateObjectLocal(axis, (float)delta * speed);
        }
    }

    private void Rebuild(int level)
    {
        _drawnVersion = _sim.Rooms.Version;
        _drawnLevel = level;
        foreach (var child in _root.GetChildren()) child.QueueFree();
        _parts.Clear();

        foreach (var room in _sim.Rooms.All)
        {
            // Floor overlay (cells on or below the cut surface).
            var cells = new List<TilePos>();
            foreach (var c in room.Cells)
                if (c.Z <= level + 1) cells.Add(c);
            var shade = RoomColor(room.Type);
            float jitter = (Colony.Core.Procedural.Noise.Hash(room.Id, 0, 0, 3) - 0.5f) * 0.2f;
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new PlaneMesh
                {
                    Size = new Vector2(0.98f, 0.98f),
                    Material = Overlay.Material(shade.Lightened(jitter) with { A = shade.A }),
                },
                InstanceCount = cells.Count,
            };
            for (int i = 0; i < cells.Count; i++)
                mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(cells[i].X + 0.5f, cells[i].Z + 0.015f, cells[i].Y + 0.5f)));
            _root.AddChild(new MultiMeshInstance3D { Multimesh = mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

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
        var shadow = part.Built ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
        var model = ModelFor(part.Type, part.Size);
        node.AddChild(new MeshInstance3D { Mesh = model.Static, MaterialOverride = ghost, CastShadow = shadow });
        var spinners = new List<(Node3D, Vector3, float)>();
        foreach (var (mesh, at, axis, speed) in model.Spinners)
        {
            var s = new MeshInstance3D { Mesh = mesh, Position = at, MaterialOverride = ghost, CastShadow = shadow };
            node.AddChild(s);
            spinners.Add((s, axis, speed));
        }
        Node3D? active = null;
        if (model.Active is { } a && part.Built)
        {
            active = new MeshInstance3D { Mesh = a, Visible = part.Working, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            node.AddChild(active);
        }
        _root.AddChild(node);
        _parts.Add((part, spinners, active));
    }

    // ---- Materials ----------------------------------------------------------------------------------------

    private ShaderMaterial Material(Color albedo, float emission = 0f)
    {
        var m = new ShaderMaterial { Shader = _shader };
        m.SetShaderParameter("albedo", albedo);
        m.SetShaderParameter("tint_mix", 0.0f);
        m.SetShaderParameter("instanced", false);
        m.SetShaderParameter("emission", emission);
        _materials.Add(m);
        return m;
    }

    private void BuildMaterials()
    {
        _wood = Material(new Color(0.62f, 0.45f, 0.28f));
        _darkWood = Material(new Color(0.42f, 0.3f, 0.19f));
        _metal = Material(new Color(0.62f, 0.64f, 0.68f));
        _darkMetal = Material(new Color(0.3f, 0.31f, 0.34f));
        _brass = Material(new Color(0.78f, 0.6f, 0.3f));
        _copper = Material(new Color(0.78f, 0.45f, 0.28f));
        _stone = Material(new Color(0.62f, 0.62f, 0.64f));
        _darkStone = Material(new Color(0.42f, 0.42f, 0.45f));
        _brick = Material(new Color(0.66f, 0.33f, 0.24f));
        _clay = Material(new Color(0.62f, 0.42f, 0.3f));
        _soot = Material(new Color(0.16f, 0.15f, 0.15f));
        _cloth = Material(new Color(0.86f, 0.8f, 0.68f));
        _red = Material(new Color(0.72f, 0.2f, 0.22f));
        _blue = Material(new Color(0.25f, 0.4f, 0.7f));
        _paper = Material(new Color(0.95f, 0.92f, 0.85f));
        _glass = Material(new Color(0.7f, 0.88f, 0.95f), 0.15f);
        _fire = Material(new Color(1f, 0.55f, 0.15f), 1.6f);
        _ember = Material(new Color(0.9f, 0.3f, 0.1f), 0.5f);
        _aether = Material(new Color(0.35f, 0.9f, 0.95f), 1.3f);
        _rune = Material(new Color(0.65f, 0.45f, 1f), 1.1f);
        _gold = Material(new Color(0.95f, 0.78f, 0.3f));
        _green = Material(new Color(0.35f, 0.6f, 0.3f));
        _white = Material(new Color(0.92f, 0.9f, 0.86f));
        _inputWood = Material(new Color(0.45f, 0.62f, 0.36f));
        _outputWood = Material(new Color(0.36f, 0.5f, 0.68f));
        _flame = Material(new Color(1f, 0.85f, 0.4f), 1.2f);
    }

    // ---- Models -------------------------------------------------------------------------------------------

    private Built ModelFor(PartType type, int size)
    {
        if (_models.TryGetValue((type, size), out var built)) return built;
        var (w, d) = PartInfo.Footprint(type, size);
        var m = new Model();
        StationModel(type.Model, m, w, d, size);
        var spinners = new List<(Mesh, Vector3, Vector3, float)>();
        foreach (var (parts, at, axis, speed) in m.Spin) spinners.Add((Compose(parts), at, axis, speed));
        built = new Built(Compose(m.Static), spinners, m.Active.Count > 0 ? Compose(m.Active) : null);
        _models[(type, size)] = built;
        return built;
    }

    /// <summary>
    /// Local space: x runs along the part's length (w tiles), z across it (d tiles), y up; the floor centre of
    /// the footprint is the origin. Sizes add copies (more wheels, vats, furnaces) and detail.
    /// </summary>
    private void StationModel(string model, Model m, int w, int d, int size)
    {
        float L = w - 0.2f, D = d - 0.2f;
        var p = m.Static;
        int n = size switch { 1 or 2 => 1, 3 or 4 => 2, _ => 3 }; // repeated units
        // Unit slots spread along the length.
        float Slot(int i, int count) => count == 1 ? 0 : -L / 2 + L * (i + 0.5f) / count;

        switch (model)
        {
            case "saw_bench": SawModel(m, w, d, size); break;
            case "tool_rack": ToolModel(p, w, d, size); break;
            case "lantern":
                p.Add(Box(0.07f, 1.4f, 0.07f), 0, 0.7f, 0, _darkWood);
                p.Add(Box(0.24f, 0.26f, 0.24f), 0, 1.48f, 0, _flame);
                p.Add(Box(0.3f, 0.05f, 0.3f), 0, 1.63f, 0, _metal);
                break;
            case "input_crate": Crate(p, _inputWood); break;
            case "output_crate": Crate(p, _outputWood); break;

            case "joinery_bench":
            {
                Table(p, L, D, 0.8f, _wood, _darkWood);
                p.Add(Box(0.3f, 0.14f, 0.2f), -L / 2 + 0.3f, 0.91f, -D / 2 + 0.2f, _metal); // vice
                for (int i = 0; i < Math.Min(size + 1, 5); i++) p.Add(Box(L * 0.5f, 0.04f, 0.14f), 0.1f, 0.87f + i * 0.045f, D / 2 - 0.25f, _wood);
                Chair(p, L / 2 - 0.35f, 0.85f, 0, 0.6f); // a chair in progress on the bench
                if (size >= 3) // a lathe along the back
                {
                    p.Add(Box(L * 0.7f, 0.1f, 0.12f), 0, 0.45f, -D / 2 + 0.12f, _darkWood);
                    m.Spinner(new Vector3(0, 0.6f, -D / 2 + 0.12f), Vector3.Right, 9f).Add(Cyl(0.07f, L * 0.4f, 8), AlongX(0, 0, 0), _wood);
                }
                if (size >= 5) p.Add(Box(L, 0.08f, 0.08f), 0, 2.0f, 0, _darkWood); // overhead tool rail
                break;
            }
            case "charcoal_clamp":
                for (int i = 0; i < n; i++)
                {
                    float x = Slot(i, n), r = Math.Min(L / n, D) * 0.45f;
                    p.Add(Ball(r, r * 1.1f, 12), x, r * 0.35f, 0, _soot);
                    p.Add(Ball(r * 0.9f, r * 0.9f, 10), x, r * 0.45f, 0, _clay);
                    p.Add(Cyl(0.08f, 0.3f, 6), x, r * 0.85f + 0.1f, 0, _darkStone); // vent
                    m.Active.Add(Ball(0.1f, 0.25f, 6), x, r * 0.85f + 0.35f, 0, _fire);
                    for (int k = 0; k < 4; k++)
                        p.Add(Box(0.08f, 0.06f, 0.08f), x + Mathf.Cos(k * 1.57f) * r * 0.85f, 0.12f, Mathf.Sin(k * 1.57f) * r * 0.85f, _ember);
                }
                LogPile(p, -L / 2 + 0.25f, D / 2 - 0.2f, size);
                break;
            case "kiln":
                for (int i = 0; i < n; i++)
                {
                    float x = Slot(i, n), r = Math.Min(L / n, D) * 0.42f, h = 1.0f + 0.25f * size;
                    p.Add(Cone(r, r * 0.85f, h, 12), x, h / 2, 0, _brick);
                    p.Add(Cone(r * 0.85f, r * 0.25f, 0.6f, 12), x, h + 0.3f, 0, _brick);
                    p.Add(Cyl(r * 0.25f, 0.4f, 8), x, h + 0.75f, 0, _soot);
                    p.Add(Box(0.36f, 0.4f, 0.1f), x, 0.25f, -r + 0.02f, _ember); // the fire mouth
                    m.Active.Add(Box(0.26f, 0.3f, 0.06f), x, 0.24f, -r - 0.04f, _fire);
                }
                break;
            case "stonecutter":
                Table(p, L, D, 0.6f, _darkWood, _darkWood);
                p.Add(Box(L * 0.45f, 0.4f, D * 0.6f), -L * 0.15f, 0.85f, 0, _stone); // a block being cut
                for (int i = 0; i < Math.Min(size, 4); i++) p.Add(Box(0.4f, 0.25f, 0.4f), L / 2 - 0.25f, 0.12f + i * 0.26f, D / 2 - 0.25f, _stone);
                if (size >= 3)
                {
                    float r = 0.3f + 0.04f * size;
                    p.Add(Box(0.1f, 1.3f, 0.1f), L * 0.2f, 0.65f, -D / 2 + 0.1f, _metal);
                    m.Spinner(new Vector3(L * 0.2f, 0.85f + r * 0.3f, -D / 2 + 0.25f), Vector3.Back, 12f).Add(Cyl(r, 0.04f, 16), AlongZ(0, 0, 0), _metal);
                }
                else p.Add(Box(0.04f, 0.3f, D * 0.7f), L * 0.1f, 1.2f, 0, _metal); // a hand frame saw
                break;
            case "carving_block":
                p.Add(Box(L * 0.5f, 0.5f, D * 0.6f), 0, 0.25f, 0, _darkStone); // plinth
                Figure(p, 0, 0.5f, 0, 0.8f + 0.15f * size, _stone);
                for (int i = 0; i < 3; i++) p.Add(Box(0.03f, 0.2f, 0.03f), -L / 2 + 0.15f + i * 0.08f, 0.6f, D / 2 - 0.15f, _metal);
                p.Add(Box(0.3f, 0.5f, 0.3f), -L / 2 + 0.2f, 0.25f, D / 2 - 0.2f, _darkWood); // chisel stand
                if (size >= 4) Scaffold(p, L * 0.7f, D * 0.8f, 1.6f + 0.15f * size);
                break;
            case "glass_furnace":
            {
                float r = Math.Min(L, D) * 0.42f;
                p.Add(Ball(r, r * 1.5f, 14), -L / 2 + r + 0.05f, r * 0.55f, 0, _brick);
                p.Add(Cyl(r * 0.2f, 0.8f, 8), -L / 2 + r + 0.05f, r * 1.25f + 0.3f, 0, _soot);
                int holes = 1 + size / 2;
                for (int i = 0; i < holes; i++)
                {
                    float a = -0.8f + 1.6f * (holes == 1 ? 0.5f : i / (float)(holes - 1));
                    var hole = new Vector3(-L / 2 + r + 0.05f + Mathf.Sin(a) * r * 0.95f, r * 0.6f, -Mathf.Cos(a) * r * 0.95f);
                    p.Add(Ball(0.11f), hole.X, hole.Y, hole.Z, _ember);
                    m.Active.Add(Ball(0.09f), hole.X, hole.Y, hole.Z - 0.04f, _fire);
                }
                if (L > 2 * r + 0.6f) // marver table and blowpipes
                {
                    p.Add(Box(0.6f, 0.75f, 0.5f), L / 2 - 0.4f, 0.37f, 0, _metal);
                    for (int i = 0; i < 3; i++) p.Add(Cyl(0.02f, 1.2f, 5), At(L / 2 - 0.4f, 0.8f + i * 0.03f, -0.15f + i * 0.15f, 0, 0, Mathf.Pi / 2), _darkMetal);
                }
                if (size >= 3) p.Add(Box(L * 0.3f, 0.6f, D * 0.5f), L / 2 - L * 0.15f - 0.05f, 0.3f, D / 2 - D * 0.25f, _brick); // annealing oven
                break;
            }
            case "grinding_wheel":
                for (int i = 0; i < n; i++)
                {
                    float x = Slot(i, n), r = 0.3f + 0.05f * size;
                    p.Add(Box(0.08f, 0.9f, 0.08f), x - 0.2f, 0.45f, 0, _darkWood);
                    p.Add(Box(0.08f, 0.9f, 0.08f), x + 0.2f, 0.45f, 0, _darkWood);
                    p.Add(Box(0.6f, 0.08f, D * 0.6f), x, 0.06f, 0, _darkWood); // treadle base
                    p.Add(Box(0.5f, 0.06f, 0.4f), x, 0.75f, -D / 2 + 0.3f, _wood); // work rest
                    var wheel = m.Spinner(new Vector3(x, 0.9f, 0), Vector3.Right, 10f);
                    wheel.Add(Cyl(r, 0.12f, 16), AlongX(0, 0, 0), _stone);
                    wheel.Add(Box(0.13f, r * 1.6f, 0.04f), 0, 0, 0, _darkStone);
                }
                break;
            case "spinning_wheel":
                for (int i = 0; i < n; i++)
                {
                    float x = Slot(i, n), r = 0.32f + 0.04f * size;
                    p.Add(Box(0.7f, 0.06f, 0.2f), x, 0.35f, 0, _darkWood); // bench
                    foreach (float lx in new[] { -0.3f, 0.3f }) p.Add(Box(0.05f, 0.35f, 0.05f), x + lx, 0.17f, 0, _darkWood);
                    p.Add(Box(0.04f, 0.7f, 0.04f), x - 0.1f, 0.65f, 0.05f, _wood);
                    var wheel = m.Spinner(new Vector3(x - 0.1f, 0.35f + r + 0.1f, 0.08f), Vector3.Back, 6f);
                    wheel.Add(Ring(r - 0.03f, r, 20), AlongZ(0, 0, 0), _wood);
                    wheel.Add(Box(r * 2, 0.025f, 0.025f), 0, 0, 0, _wood);
                    wheel.Add(Box(0.025f, r * 2, 0.025f), 0, 0, 0, _wood);
                    p.Add(Cone(0.08f, 0.02f, 0.4f, 6), x + 0.3f, 0.6f, 0, _cloth); // distaff with fibre
                }
                break;
            case "loom":
            {
                float h = 1.4f + 0.1f * size;
                foreach (float x in new[] { -L / 2 + 0.05f, L / 2 - 0.05f })
                foreach (float z in new[] { -D / 2 + 0.05f, D / 2 - 0.05f })
                    p.Add(Box(0.08f, h, 0.08f), x, h / 2, z, _darkWood);
                p.Add(Box(L, 0.1f, 0.1f), 0, h, -D / 2 + 0.05f, _wood);
                p.Add(Box(L, 0.1f, 0.1f), 0, h, D / 2 - 0.05f, _wood);
                p.Add(Cyl(0.08f, L - 0.1f, 8), AlongX(0, 0.75f, D / 2 - 0.15f), _cloth); // cloth beam
                int threads = 6 + 4 * size;
                for (int i = 0; i < threads; i++)
                    p.Add(Box(0.012f, 0.012f, D - 0.3f), -L / 2 + 0.15f + (L - 0.3f) * i / (threads - 1), 0.78f, 0, _white);
                p.Add(Box(L - 0.2f, 0.06f, 0.06f), 0, 0.9f, 0, _wood); // beater
                p.Add(Box(L * 0.6f, 0.04f, D * 0.35f), 0, 0.8f, D * 0.12f, _red); // woven cloth
                break;
            }
            case "sewing_table":
                Table(p, L, D, 0.78f, _wood, _darkWood);
                p.Add(Box(L * 0.4f, 0.02f, D * 0.5f), At(-L * 0.15f, 0.8f, 0, 0, 0.2f, 0), _blue);
                p.Add(Box(L * 0.25f, 0.02f, D * 0.3f), At(L * 0.15f, 0.81f, 0.05f, 0, -0.3f, 0), _red);
                for (int i = 0; i < 3; i++) p.Add(Cyl(0.04f, 0.08f, 6), L / 2 - 0.2f - i * 0.1f, 0.83f, -D / 2 + 0.15f, i == 1 ? _red : _cloth);
                for (int i = 0; i < Math.Min(size, 3); i++) // dress forms
                {
                    float x = -L / 2 + 0.3f + i * 0.5f, z = D / 2 + 0.05f;
                    p.Add(Cyl(0.02f, 0.9f, 5), x, 0.45f, z, _darkWood);
                    p.Add(Cone(0.18f, 0.14f, 0.45f, 8), x, 1.05f, z, i % 2 == 0 ? _cloth : _blue);
                }
                break;
            case "millstone":
                for (int i = 0; i < n; i++)
                {
                    float x = Slot(i, n), r = Math.Min(L / n, D) * 0.4f;
                    p.Add(Cyl(r, 0.25f, 16), x, 0.12f, 0, _darkStone);
                    var stone = m.Spinner(new Vector3(x, 0.35f, 0), Vector3.Up, 3f);
                    stone.Add(Cyl(r * 0.95f, 0.2f, 16), 0, 0, 0, _stone);
                    stone.Add(Box(r * 1.9f, 0.05f, 0.08f), 0, 0.12f, 0, _darkStone);
                    p.Add(Cone(0.18f, 0.32f, 0.35f, 8), x, 0.9f, 0, _wood); // hopper
                    p.Add(Box(0.05f, 0.5f, 0.05f), x + 0.25f, 0.65f, 0, _darkWood);
                    if (size >= 3) p.Add(Cyl(0.05f, 1.2f, 6), x, 1.0f, 0, _darkWood); // drive shaft
                }
                Sacks(p, L / 2 - 0.2f, D / 2 - 0.2f, size, _cloth);
                break;
            case "oven":
                for (int i = 0; i < n; i++)
                {
                    float x = Slot(i, n), r = Math.Min(L / n, D) * 0.42f;
                    p.Add(Box(r * 2, 0.5f, r * 2), x, 0.25f, 0, _stone);
                    p.Add(Ball(r, r * 1.4f, 12), x, 0.55f, 0, _brick);
                    p.Add(Cyl(0.08f, 0.6f, 6), x + r * 0.4f, 0.5f + r * 1.1f, r * 0.3f, _soot);
                    p.Add(Box(0.3f, 0.22f, 0.1f), x, 0.62f, -r + 0.05f, _ember);
                    m.Active.Add(Box(0.22f, 0.15f, 0.06f), x, 0.62f, -r - 0.02f, _fire);
                }
                p.Add(Cyl(0.02f, 1.2f, 5), At(-L / 2 + 0.1f, 0.6f, D / 2 - 0.1f, 0.3f), _wood); // bread peel
                break;
            case "vats":
            {
                int vats = size switch { 1 => 2, 2 => 4, 3 => 4, 4 => 6, _ => 9 };
                int cols = Math.Max(1, (int)Math.Round(vats / (float)Math.Max(1, d))), rows = (vats + cols - 1) / cols;
                for (int i = 0; i < vats; i++)
                {
                    float x = cols == 1 ? 0 : -L / 2 + L * ((i % cols) + 0.5f) / cols;
                    float z = rows == 1 ? 0 : -D / 2 + D * ((i / cols) + 0.5f) / rows;
                    float r = Math.Min(L / cols, D / rows) * 0.42f, h = 0.7f + 0.08f * size;
                    p.Add(Cyl(r, h, 12), x, h / 2, z, _wood);
                    p.Add(Cyl(r * 1.02f, 0.04f, 12), x, h * 0.25f, z, _darkMetal);
                    p.Add(Cyl(r * 1.02f, 0.04f, 12), x, h * 0.8f, z, _darkMetal);
                    p.Add(Cyl(r * 0.92f, 0.02f, 12), x, h, z, _red); // wine
                }
                break;
            }
            case "bloomery":
                for (int i = 0; i < n; i++)
                {
                    float x = Slot(i, n), r = Math.Min(L / n, D) * 0.38f, h = 1.2f + 0.2f * size;
                    p.Add(Cone(r, r * 0.55f, h, 10), x, h / 2, 0, _clay);
                    p.Add(Cyl(r * 0.5f, 0.1f, 10), x, h, 0, _soot);
                    p.Add(Box(0.25f, 0.25f, 0.1f), x, 0.2f, -r + 0.03f, _ember);
                    m.Active.Add(Ball(r * 0.4f, 0.3f, 8), x, h + 0.1f, 0, _fire);
                    p.Add(Box(0.35f, 0.18f, 0.25f), x + r + 0.12f, 0.25f, 0.1f, _darkWood); // bellows
                    p.Add(Box(0.3f, 0.02f, 0.22f), x + r + 0.12f, 0.36f, 0.1f, _cloth);
                }
                break;
            case "crucible":
            {
                p.Add(Box(L * 0.8f, 0.9f, D * 0.8f), 0, 0.45f, 0, _brick);
                int pots = 1 + size / 2;
                for (int i = 0; i < pots; i++)
                {
                    float x = pots == 1 ? 0 : -L * 0.3f + L * 0.6f * i / (pots - 1);
                    p.Add(Cone(0.18f, 0.24f, 0.3f, 10), x, 1.05f, 0, _darkStone);
                    p.Add(Cyl(0.2f, 0.02f, 10), x, 1.2f, 0, _fire); // molten metal
                }
                p.Add(Box(L * 0.6f, 0.08f, 0.15f), 0, 0.6f, -D * 0.4f - 0.05f, _ember); // pouring channel
                m.Active.Add(Box(L * 0.4f, 0.1f, 0.1f), 0, 0.3f, -D * 0.4f - 0.02f, _fire);
                p.Add(Cyl(0.15f, 1.2f, 8), L * 0.35f, 1.5f, D * 0.3f, _soot);
                break;
            }
            case "aether_furnace":
            {
                float r = Math.Min(L, D) * 0.4f, h = 1.2f + 0.2f * size;
                p.Add(Cyl(r, h, 14), 0, h / 2, 0, _darkMetal);
                for (int k = 0; k < 3; k++) p.Add(Cyl(r * 1.04f, 0.06f, 14), 0, 0.2f + k * h * 0.35f, 0, _brass);
                p.Add(Box(r * 0.8f, h * 0.4f, 0.08f), 0, h * 0.5f, -r, _aether); // window on the core
                var ring = m.Spinner(new Vector3(0, h + 0.4f, 0), Vector3.Up, 2.5f);
                ring.Add(Ring(r * 0.5f, r * 0.6f, 20), At(0, 0, 0, 0.3f), _aether);
                ring.Add(Ball(0.12f), r * 0.55f, 0, 0, _aether);
                for (int k = 0; k < size; k++) // conduits
                    p.Add(Cyl(0.05f, L * 0.4f, 6), AlongX(r + L * 0.15f, 0.3f + k * 0.2f, -D * 0.3f + k * 0.1f), _copper);
                break;
            }
            case "anvil":
                for (int i = 0; i < n; i++)
                {
                    float x = Slot(i, n);
                    p.Add(Cyl(0.2f, 0.45f, 8), x - 0.25f, 0.22f, 0.1f, _darkWood); // stump
                    p.Add(Box(0.4f, 0.12f, 0.16f), x - 0.25f, 0.52f, 0.1f, _darkMetal);
                    p.Add(Box(0.2f, 0.1f, 0.12f), x - 0.25f, 0.62f, 0.1f, _darkMetal);
                    p.Add(Cone(0.06f, 0f, 0.2f, 6), At(x - 0.03f, 0.62f, 0.1f, 0, 0, -Mathf.Pi / 2), _darkMetal); // horn
                    p.Add(Box(0.5f, 0.6f, 0.5f), x + 0.35f, 0.3f, -0.05f, _brick); // hearth
                    p.Add(Box(0.36f, 0.05f, 0.36f), x + 0.35f, 0.62f, -0.05f, _ember);
                    m.Active.Add(Ball(0.12f, 0.2f, 8), x + 0.35f, 0.7f, -0.05f, _fire);
                }
                if (d >= 2) p.Add(Box(L * 0.5f, 0.3f, 0.3f), 0, 0.15f, D / 2 - 0.15f, _darkWood); // quench trough
                if (size >= 4) // a water-driven trip hammer
                {
                    p.Add(Box(0.1f, 1.4f, 0.1f), L / 2 - 0.2f, 0.7f, -D / 2 + 0.15f, _darkWood);
                    m.Spinner(new Vector3(L / 2 - 0.2f, 1.2f, -D / 2 + 0.32f), Vector3.Back, 4f).Add(Ring(0.25f, 0.35f, 12), AlongZ(0, 0, 0), _darkWood);
                }
                break;
            case "rolling_mill":
                for (int i = 0; i < n; i++)
                {
                    float x = Slot(i, n), r = 0.12f + 0.02f * size;
                    foreach (float z in new[] { -D / 2 + 0.1f, D / 2 - 0.1f })
                        p.Add(Box(0.25f, 1.1f, 0.12f), x, 0.55f, z, _darkMetal);
                    foreach (float y in new[] { 0.6f, 0.6f + 2 * r + 0.02f })
                        m.Spinner(new Vector3(x, y, 0), Vector3.Back, y > 0.65f ? -5f : 5f).Add(Cyl(r, D - 0.3f, 12), AlongZ(0, 0, 0), _metal);
                    p.Add(Box(0.2f, 0.25f, 0.2f), x, 1.2f, -D / 2 + 0.1f, _brass); // screw-down housing
                }
                for (int k = 0; k < Math.Min(size + 1, 5); k++) p.Add(Box(0.6f, 0.03f, 0.5f), L / 2 - 0.35f, 0.02f + k * 0.04f, 0, _metal);
                break;
            case "tinker_bench":
                Table(p, L, D, 0.8f, _darkWood, _darkWood);
                for (int k = 0; k < 3 + size; k++)
                {
                    float x = -L / 2 + 0.2f + (L - 0.4f) * k / (2f + size), z = (k % 2 - 0.5f) * D * 0.4f;
                    p.Add(Cyl(0.05f + 0.02f * (k % 3), 0.02f, 10), x, 0.86f, z, k % 2 == 0 ? _brass : _copper);
                }
                p.Add(Box(0.3f, 0.4f, 0.2f), L / 2 - 0.3f, 1.05f, -D / 2 + 0.2f, _darkWood); // a clock
                m.Spinner(new Vector3(L / 2 - 0.3f, 1.1f, -D / 2 + 0.09f), Vector3.Back, 1.5f).Add(Box(0.02f, 0.14f, 0.01f), 0, 0.05f, 0, _brass);
                p.Add(Cyl(0.015f, 0.4f, 5), At(-L / 2 + 0.35f, 1.05f, 0, 0, 0, 0.3f), _brass); // magnifier arm
                p.Add(Ring(0.06f, 0.08f, 12), At(-L / 2 + 0.45f, 1.22f, 0, Mathf.Pi / 2), _brass);
                p.Add(Cyl(0.06f, 0.01f, 12), AlongZ(-L / 2 + 0.45f, 1.22f, 0), _glass);
                if (size >= 3) m.Spinner(new Vector3(0, 1.05f, -D / 2 + 0.15f), Vector3.Back, 3f).Add(Cyl(0.18f, 0.03f, 12), AlongZ(0, 0, 0), _brass);
                break;
            case "assembly_frame":
            {
                float h = 2.0f + 0.15f * size;
                foreach (float x in new[] { -L / 2 + 0.06f, L / 2 - 0.06f })
                foreach (float z in new[] { -D / 2 + 0.06f, D / 2 - 0.06f })
                    p.Add(Box(0.1f, h, 0.1f), x, h / 2, z, _darkMetal);
                p.Add(Box(L, 0.12f, 0.12f), 0, h, -D / 2 + 0.06f, _darkMetal);
                p.Add(Box(L, 0.12f, 0.12f), 0, h, D / 2 - 0.06f, _darkMetal);
                p.Add(Box(0.12f, 0.1f, D), 0, h - 0.05f, 0, _brass); // crane beam
                p.Add(Cyl(0.015f, h * 0.35f, 4), 0, h * 0.8f, 0, _metal); // chain
                Figure(p, 0, 0.1f, 0, 1.2f + 0.08f * size, _brass); // the automaton taking shape
                p.Add(Ball(0.09f), 0, 0.8f + 0.04f * size, -0.12f, _aether); // its core
                for (int k = 0; k < size; k++) p.Add(Box(0.2f, 0.15f, 0.2f), -L / 2 + 0.25f, 0.08f + k * 0.16f, D / 2 - 0.25f, _metal);
                if (size >= 4) p.Add(Cyl(0.04f, h, 6), L / 2 - 0.25f, h / 2, -D / 2 + 0.25f, _aether); // aether conduit
                break;
            }
            case "alembic":
                Table(p, L, D, 0.75f, _darkWood, _darkWood);
                for (int i = 0; i < 1 + size / 2; i++)
                {
                    float x = -L / 2 + 0.35f + i * 0.7f;
                    if (x > L / 2 - 0.2f) break;
                    p.Add(Cyl(0.08f, 0.12f, 8), x, 0.83f, 0, _darkMetal); // burner
                    m.Active.Add(Cone(0.05f, 0f, 0.14f, 6), x, 0.95f, 0, _fire);
                    p.Add(Ball(0.14f), x, 1.06f, 0, _glass);
                    p.Add(Ball(0.1f), x, 1.06f, 0, i % 2 == 0 ? _green : _red); // brew
                    p.Add(Cyl(0.02f, 0.45f, 5), At(x + 0.18f, 1.2f, 0, 0, 0, -1.1f), _glass); // the swan neck
                    p.Add(Ball(0.08f), x + 0.38f, 0.92f, 0, _glass); // receiver
                }
                for (int k = 0; k < 4; k++) p.Add(Cyl(0.04f, 0.12f, 6), L / 2 - 0.12f, 0.82f, -D / 2 + 0.12f + k * 0.1f, _glass);
                break;
            case "mortar":
                for (int i = 0; i < n; i++)
                {
                    float x = Slot(i, n), r = Math.Min(L / n, D) * 0.3f;
                    p.Add(Cone(r * 0.8f, r, 0.5f, 12), x, 0.25f, 0, _stone);
                    p.Add(Cyl(r * 0.75f, 0.02f, 12), x, 0.5f, 0, _rune); // glimmer inside
                    var pestle = m.Spinner(new Vector3(x, 0.5f, 0), Vector3.Up, 2f);
                    pestle.Add(Cyl(0.05f, 0.6f, 6), At(r * 0.3f, 0.25f, 0, 0, 0, 0.4f), _darkStone);
                }
                p.Add(Box(L * 0.8f, 0.05f, 0.25f), 0, 1.2f, D / 2 - 0.12f, _darkWood); // shelf of jars
                for (int k = 0; k < 2 + size; k++)
                    p.Add(Cyl(0.05f, 0.14f, 6), -L * 0.35f + L * 0.7f * k / (1f + size), 1.3f, D / 2 - 0.12f, k % 3 == 0 ? _rune : k % 3 == 1 ? _glass : _clay);
                break;
            case "attunement_circle":
            {
                float r = Math.Min(L, D) * 0.45f;
                p.Add(Cyl(r, 0.06f, 20), 0, 0.03f, 0, _darkStone);
                p.Add(Ring(r * 0.82f, r * 0.9f, 24), 0, 0.07f, 0, _rune);
                int stones = 4 + size;
                for (int k = 0; k < stones; k++)
                {
                    float a = k * Mathf.Tau / stones, h = 0.5f + 0.1f * size;
                    p.Add(Box(0.14f, h, 0.1f), At(Mathf.Cos(a) * r * 0.95f, h / 2, Mathf.Sin(a) * r * 0.95f, 0, -a, 0), _stone);
                    p.Add(Box(0.06f, 0.1f, 0.11f), At(Mathf.Cos(a) * r * 0.95f, h * 0.7f, Mathf.Sin(a) * r * 0.95f, 0, -a, 0), _rune);
                }
                var crystal = m.Spinner(new Vector3(0, 0.9f + 0.1f * size, 0), Vector3.Up, 1.2f);
                crystal.Add(Cone(0.12f, 0f, 0.3f, 6), 0, 0.15f, 0, _rune);
                crystal.Add(Cone(0f, 0.12f, 0.3f, 6), 0, -0.15f, 0, _rune);
                break;
            }
            case "athanor":
            {
                float r = Math.Min(L, D) * 0.38f, h = 1.6f + 0.25f * size;
                p.Add(Cyl(r, h, 12), 0, h / 2, 0, _brick);
                p.Add(Cone(r, r * 0.3f, 0.6f, 12), 0, h + 0.3f, 0, _brick);
                for (int k = 0; k < 3; k++) p.Add(Cyl(r * 1.05f, 0.05f, 12), 0, 0.3f + k * h * 0.35f, 0, _brass);
                p.Add(Box(0.25f, 0.3f, 0.08f), 0, h * 0.45f, -r, _ember);
                m.Active.Add(Box(0.2f, 0.25f, 0.05f), 0, h * 0.45f, -r - 0.03f, _fire);
                p.Add(Ball(0.18f, 0.28f, 10), 0, h + 0.72f, 0, _glass); // the philosophic egg
                p.Add(Ball(0.1f, 0.16f, 8), 0, h + 0.72f, 0, _red);
                if (L > 2 * r + 0.5f) p.Add(Box(0.5f, 0.8f, 0.4f), L / 2 - 0.3f, 0.4f, 0, _darkWood); // fuel bin
                break;
            }
            case "paper_vat":
                p.Add(Box(L * 0.6f, 0.6f, D * 0.8f), -L * 0.18f, 0.3f, 0, _wood);
                p.Add(Box(L * 0.55f, 0.02f, D * 0.72f), -L * 0.18f, 0.58f, 0, _white); // pulp
                p.Add(Box(0.5f, 0.04f, 0.35f), At(-L * 0.18f, 0.75f, 0, 0.5f), _darkWood); // mould
                for (int k = 0; k < 1 + size / 2; k++) // drying line with sheets
                {
                    float z = -D / 2 + 0.15f + k * 0.3f;
                    if (z > D / 2) break;
                    p.Add(Cyl(0.01f, L * 0.35f, 4), AlongX(L * 0.28f, 1.6f, z), _darkWood);
                    for (int s = 0; s < 3; s++) p.Add(Box(0.2f, 0.25f, 0.01f), L * 0.15f + s * 0.12f, 1.47f, z, _paper);
                }
                p.Add(Box(0.06f, 1.65f, 0.06f), L / 2 - 0.06f, 0.82f, -D / 2 + 0.1f, _darkWood);
                break;
            case "colour_mill":
                p.Add(Box(L * 0.7f, 0.75f, D * 0.7f), 0, 0.37f, 0, _darkWood);
                p.Add(Box(L * 0.45f, 0.06f, D * 0.5f), -L * 0.08f, 0.78f, 0, _stone); // grinding slab
                m.Spinner(new Vector3(-L * 0.08f, 0.86f, 0), Vector3.Up, 3f).Add(Cone(0.12f, 0.08f, 0.15f, 8), 0.1f, 0, 0, _darkStone); // muller
                Material[] paints = { _red, _blue, _gold, _green, _soot };
                for (int k = 0; k < 3 + size; k++)
                    p.Add(Cyl(0.06f, 0.1f, 8), L * 0.25f, 0.82f, -D * 0.3f + D * 0.6f * k / (2f + size), paints[k % paints.Length]);
                break;
            case "writing_desk":
                for (int i = 0; i < n; i++)
                {
                    float x = Slot(i, n), dl = Math.Min(L / n, 1.6f) * 0.85f;
                    p.Add(Box(dl, 0.06f, 0.6f), At(x, 0.85f, -0.05f, 0.25f), _wood); // slanted top
                    foreach (float lx in new[] { -dl / 2 + 0.05f, dl / 2 - 0.05f })
                        p.Add(Box(0.06f, 0.8f, 0.5f), x + lx, 0.4f, -0.05f, _darkWood);
                    p.Add(Box(0.3f, 0.02f, 0.22f), At(x - 0.1f, 0.9f, -0.05f, 0.25f), _paper);
                    p.Add(Cyl(0.03f, 0.12f, 6), x + dl / 2 - 0.1f, 0.95f, -0.25f, _white); // candle
                    m.Active.Add(Cone(0.02f, 0f, 0.06f, 5), x + dl / 2 - 0.1f, 1.04f, -0.25f, _flame);
                    p.Add(Cyl(0.15f, 0.45f, 8), x, 0.22f, 0.4f, _darkWood); // stool
                    for (int k = 0; k < 3; k++) p.Add(Box(0.2f, 0.06f, 0.15f), x - dl / 2 + 0.15f, 0.92f + k * 0.065f, -0.25f, k % 2 == 0 ? _red : _blue);
                }
                break;
            case "easel":
                for (int i = 0; i < n; i++)
                {
                    float x = Slot(i, n);
                    p.Add(Box(0.04f, 1.6f, 0.04f), At(x - 0.2f, 0.78f, 0, 0.1f, 0, -0.12f), _wood);
                    p.Add(Box(0.04f, 1.6f, 0.04f), At(x + 0.2f, 0.78f, 0, 0.1f, 0, 0.12f), _wood);
                    p.Add(Box(0.04f, 1.5f, 0.04f), At(x, 0.72f, 0.28f, -0.3f), _wood);
                    p.Add(Box(0.6f, 0.5f, 0.03f), At(x, 1.15f, -0.05f, 0.1f), _white); // canvas
                    p.Add(Box(0.25f, 0.2f, 0.035f), At(x - 0.1f, 1.18f, -0.06f, 0.1f), i % 2 == 0 ? _blue : _green);
                    p.Add(Ball(0.07f), x + 0.12f, 1.1f, -0.07f, _gold);
                }
                if (d >= 2) // paint table
                {
                    p.Add(Box(0.6f, 0.7f, 0.4f), -L / 2 + 0.35f, 0.35f, D / 2 - 0.25f, _darkWood);
                    p.Add(Box(0.35f, 0.02f, 0.25f), -L / 2 + 0.35f, 0.71f, D / 2 - 0.25f, _red);
                }
                break;
            case "lectern":
            {
                p.Add(Cone(0.18f, 0.08f, 1.0f, 6), -L / 2 + 0.3f, 0.5f, 0, _darkWood);
                p.Add(Box(0.45f, 0.04f, 0.35f), At(-L / 2 + 0.3f, 1.05f, 0, 0, 0, -0.35f), _wood);
                p.Add(Box(0.3f, 0.03f, 0.22f), At(-L / 2 + 0.3f, 1.08f, 0, 0, 0, -0.35f), _paper);
                int rows = Math.Max(1, size - 1), per = Math.Max(1, d);
                for (int r = 0; r < rows; r++)
                for (int k = 0; k < per; k++)
                {
                    float x = -L / 2 + 0.85f + r * 0.6f, z = per == 1 ? 0 : -D / 2 + D * (k + 0.5f) / per;
                    if (x > L / 2 - 0.15f) break;
                    p.Add(Box(0.3f, 0.06f, D / per * 0.8f), x, 0.45f, z, _wood); // bench
                    p.Add(Box(0.06f, 0.4f, D / per * 0.8f), x + 0.16f, 0.6f, z, _wood); // backrest
                }
                p.Add(Box(0.06f, 1.0f, D * 0.8f), L / 2 - 0.05f, 1.0f, 0, _soot); // slate board
                break;
            }
            case "observatory":
            {
                float r = Math.Min(L, D) * 0.42f;
                if (size >= 3) // a dome ring overhead
                {
                    p.Add(Ring(r * 0.9f, r, 24), 0, 2.2f, 0, _brass);
                    for (int k = 0; k < 4; k++) p.Add(Box(0.08f, 2.2f, 0.08f), Mathf.Cos(k * 1.57f + 0.78f) * r * 0.95f, 1.1f, Mathf.Sin(k * 1.57f + 0.78f) * r * 0.95f, _darkWood);
                }
                foreach (float a in new[] { 0f, 2.1f, 4.2f }) // tripod
                    p.Add(Cyl(0.03f, 1.0f, 5), At(Mathf.Cos(a) * 0.2f, 0.48f, Mathf.Sin(a) * 0.2f, Mathf.Sin(a) * 0.35f, 0, -Mathf.Cos(a) * 0.35f), _darkWood);
                p.Add(Cyl(0.08f + 0.01f * size, 1.0f + 0.2f * size, 10), At(0.1f, 1.25f, 0, 0, 0, 0.9f), _brass); // telescope
                var sphere = m.Spinner(new Vector3(L / 2 - 0.35f, 1.0f, D / 2 - 0.35f), Vector3.Up, 0.8f); // armillary sphere
                sphere.Add(Ring(0.2f, 0.23f, 16), At(0, 0, 0, Mathf.Pi / 2), _brass);
                sphere.Add(Ring(0.2f, 0.23f, 16), At(0, 0, 0, 0.4f), _brass);
                sphere.Add(Ball(0.05f), 0, 0, 0, _gold);
                p.Add(Cyl(0.03f, 0.75f, 5), L / 2 - 0.35f, 0.38f, D / 2 - 0.35f, _darkWood);
                break;
            }
            default:
                Table(p, L, D, 0.8f, _wood, _darkWood);
                p.Add(Box(0.3f, 0.3f, 0.3f), 0, 0.95f, 0, _metal);
                break;
        }
    }

    // ---- Shared bits --------------------------------------------------------------------------------------

    private static void Table(Parts p, float L, float D, float top, Material board, Material legs)
    {
        p.Add(Box(L, 0.08f, D), 0, top, 0, board);
        int along = Math.Max(2, (int)MathF.Ceiling(L / 1.5f) + 1);
        for (int i = 0; i < along; i++)
        {
            float x = -L / 2 + 0.06f + (L - 0.12f) * i / (along - 1);
            foreach (float z in new[] { -D / 2 + 0.06f, D / 2 - 0.06f })
                p.Add(Box(0.08f, top - 0.04f, 0.08f), x, (top - 0.04f) / 2, z, legs);
        }
    }

    private void Chair(Parts p, float x, float y, float z, float s)
    {
        p.Add(Box(0.4f * s, 0.05f * s, 0.4f * s), x, y + 0.42f * s, z, _wood);
        p.Add(Box(0.4f * s, 0.45f * s, 0.05f * s), x, y + 0.66f * s, z + 0.18f * s, _wood);
        foreach (var (dx, dz) in new[] { (-0.17f, -0.17f), (0.17f, -0.17f), (-0.17f, 0.17f), (0.17f, 0.17f) })
            p.Add(Box(0.05f * s, 0.42f * s, 0.05f * s), x + dx * s, y + 0.21f * s, z + dz * s, _wood);
    }

    /// <summary>A standing figure (statue or automaton) of height h on y0.</summary>
    private static void Figure(Parts p, float x, float y0, float z, float h, Material m)
    {
        p.Add(Cyl(0.05f * h, 0.45f * h, 6), x - 0.07f * h, y0 + 0.22f * h, z, m);
        p.Add(Cyl(0.05f * h, 0.45f * h, 6), x + 0.07f * h, y0 + 0.22f * h, z, m);
        p.Add(Cone(0.12f * h, 0.15f * h, 0.35f * h, 8), x, y0 + 0.62f * h, z, m);
        p.Add(Ball(0.09f * h), x, y0 + 0.9f * h, z, m);
        p.Add(Cyl(0.035f * h, 0.35f * h, 6), At(x - 0.2f * h, y0 + 0.62f * h, z, 0, 0, 0.3f), m);
        p.Add(Cyl(0.035f * h, 0.35f * h, 6), At(x + 0.2f * h, y0 + 0.62f * h, z, 0, 0, -0.3f), m);
    }

    private void Scaffold(Parts p, float L, float D, float h)
    {
        foreach (float x in new[] { -L / 2, L / 2 })
        foreach (float z in new[] { -D / 2, D / 2 })
            p.Add(Box(0.06f, h, 0.06f), x, h / 2, z, _wood);
        p.Add(Box(L, 0.05f, 0.3f), 0, h * 0.6f, -D / 2, _wood);
    }

    private void LogPile(Parts p, float x, float z, int size)
    {
        for (int i = 0; i < Math.Min(size + 1, 4); i++)
            p.Add(Cyl(0.08f, 0.6f, 6), AlongZ(x + (i % 2) * 0.1f, 0.08f + i / 2 * 0.15f, z - 0.3f), _darkWood);
    }

    private static void Sacks(Parts p, float x, float z, int size, Material m)
    {
        for (int i = 0; i < Math.Min(size, 4); i++) p.Add(Ball(0.15f, 0.32f, 8), x - (i % 2) * 0.3f, 0.16f + i / 2 * 0.28f, z, m);
    }

    private static void Crate(Parts p, Material mat)
    {
        p.Add(Box(0.9f, 0.06f, 0.9f), 0, 0.03f, 0, mat);
        p.Add(Box(0.9f, 0.45f, 0.06f), 0, 0.25f, 0.42f, mat);
        p.Add(Box(0.9f, 0.45f, 0.06f), 0, 0.25f, -0.42f, mat);
        p.Add(Box(0.06f, 0.45f, 0.9f), 0.42f, 0.25f, 0, mat);
        p.Add(Box(0.06f, 0.45f, 0.9f), -0.42f, 0.25f, 0, mat);
    }

    private static readonly int[] BladeCount = { 1, 1, 2, 2, 3 };

    /// <summary>
    /// Saws grow from a trestle (saw horse) to a table saw, pit saw and frame saw with an overhead gantry, up to
    /// a mill saw with a log carriage and a drive housing. Blades spin while sawing.
    /// </summary>
    private void SawModel(Model m, int w, int d, int size)
    {
        var parts = m.Static;
        float L = w - 0.15f, D = d - 0.25f, top = 0.72f;
        parts.Add(Box(L, 0.1f, D), 0, top, 0, _wood);
        int legsAlong = Math.Max(2, (int)MathF.Ceiling(L / 1.5f) + 1);
        for (int i = 0; i < legsAlong; i++)
        {
            float x = -L / 2 + 0.08f + (L - 0.16f) * i / (legsAlong - 1);
            foreach (float z in new[] { -D / 2 + 0.08f, D / 2 - 0.08f })
                parts.Add(Box(0.09f, top - 0.04f, 0.09f), x, (top - 0.04f) / 2, z, _darkWood);
        }
        if (size >= 2) parts.Add(Box(L - 0.2f, 0.06f, 0.06f), 0, 0.18f, 0, _darkWood);
        int logs = Math.Min(PartInfo.Batch(PartType.Sawbench, size), 4);
        for (int i = 0; i < logs; i++)
        {
            float z = D > 1 ? -D / 2 + 0.25f + i * 0.24f : 0;
            parts.Add(Cyl(0.09f, L * 0.4f, 6), AlongX(-L * 0.25f, top + 0.14f, z), _darkWood);
        }
        if (size >= 3)
        {
            float h = 1.6f + 0.1f * size;
            foreach (float x in new[] { -L / 2 + 0.2f, L / 2 - 0.2f })
            {
                parts.Add(Box(0.1f, h, 0.1f), x, h / 2, -D / 2 - 0.02f, _wood);
                parts.Add(Box(0.1f, h, 0.1f), x, h / 2, D / 2 + 0.02f, _wood);
                parts.Add(Box(0.12f, 0.12f, D + 0.15f), x, h, 0, _wood);
            }
            parts.Add(Box(L - 0.3f, 0.1f, 0.12f), 0, h, 0, _darkWood);
            if (size >= 4)
                for (int i = 0; i < 3; i++)
                    parts.Add(Box(0.03f, h - top - 0.15f, 0.02f), L * 0.18f + i * 0.12f, (h + top) / 2, 0, _metal);
        }
        if (size >= 5)
        {
            foreach (float z in new[] { -0.35f, 0.35f }) parts.Add(Box(L - 0.1f, 0.05f, 0.06f), 0, top + 0.08f, z, _metal);
            parts.Add(Box(0.7f, 0.9f, 0.9f), L / 2 - 0.4f, 0.45f, D / 2 - 0.5f, _metal);
            parts.Add(Cyl(0.28f, 0.12f, 12), AlongZ(L / 2 - 0.4f, 0.65f, D / 2 - 0.02f), _darkWood);
        }
        // Blades: spread along the table.
        float r = 0.26f + 0.05f * size, span = w - 1.0f;
        int n = BladeCount[size - 1];
        for (int i = 0; i < n; i++)
        {
            float x = n == 1 ? w * 0.12f : -span / 2 + span * (i + 0.5f) / n + 0.2f;
            var blade = m.Spinner(new Vector3(x, 0.78f + r * 0.25f, 0), Vector3.Forward, 14f);
            blade.Add(Cyl(r, 0.03f, 14), AlongZ(0, 0, 0), _metal);
            blade.Add(Box(r * 1.6f, 0.05f, 0.04f), 0, 0, 0, _darkWood); // a spoke, so the spin reads
        }
    }

    /// <summary>
    /// Tools grow from a few pegs to a rack and a full tool wall, then a workbench with a rack behind it, and a
    /// walled tool crib with shelves.
    /// </summary>
    private void ToolModel(Parts parts, int w, int d, int size)
    {
        float W = w - 0.2f, back = d / 2f - 0.15f;
        float boardH = 0.6f + 0.15f * size, boardY = 0.55f + boardH / 2;
        parts.Add(Box(W, boardH, 0.06f), 0, boardY, back, _darkWood);
        int posts = Math.Max(2, w + 1);
        for (int i = 0; i < posts; i++)
        {
            float x = -W / 2 + W * i / (posts - 1);
            parts.Add(Box(0.07f, boardY + boardH / 2 + 0.1f, 0.07f), x, (boardY + boardH / 2 + 0.1f) / 2, back, _wood);
        }
        int rows = size >= 3 ? 2 : 1, perRow = 2 + w * 2;
        for (int r = 0; r < rows; r++)
        for (int i = 0; i < perRow; i++)
        {
            float x = -W / 2 + W * (i + 0.5f) / perRow;
            float len = 0.25f + 0.1f * ((i * 7 + r * 3) % 3);
            float y = boardY + boardH / 2 - 0.12f - r * (boardH / 2) - len / 2;
            parts.Add(Box(0.04f, len, 0.04f), x, y, back - 0.06f, (i + r) % 3 == 0 ? _wood : _metal);
            if ((i + r) % 2 == 0) parts.Add(Box(0.14f, 0.06f, 0.05f), x, y - len / 2, back - 0.06f, _metal);
        }
        if (d >= 2)
        {
            float benchD = d - 0.9f, z = back - 0.15f - benchD / 2;
            parts.Add(Box(W, 0.08f, benchD), 0, 0.8f, z, _wood);
            parts.Add(Box(W - 0.1f, 0.5f, benchD - 0.1f), 0, 0.5f, z, _darkWood);
            for (int i = 0; i < w; i++) parts.Add(Box(0.16f, 0.04f, 0.03f), -W / 2 + W * (i + 0.5f) / w, 0.6f, z - benchD / 2 + 0.03f, _metal);
            parts.Add(Box(0.3f, 0.14f, 0.2f), -W / 4, 0.91f, z, _metal);
        }
        if (size >= 5)
        {
            float h = boardY + boardH / 2 + 0.15f;
            foreach (float x in new[] { -W / 2 - 0.02f, W / 2 + 0.02f }) parts.Add(Box(0.06f, h, d - 0.3f), x, h / 2, 0, _darkWood);
            parts.Add(Box(W + 0.1f, 0.1f, 0.1f), 0, h, -d / 2f + 0.2f, _wood);
            for (int i = 0; i < 3; i++) parts.Add(Box(0.2f, 0.12f, 0.2f), -W / 2 + 0.3f + i * 0.35f, 0.91f, back - 0.4f, _metal);
        }
    }
}
