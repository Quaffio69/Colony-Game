using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Agents;
using Colony.Core.Items;
using Godot;
using static ColonyGame.MeshKit;

namespace ColonyGame;

/// <summary>
/// Draws homes' arranged fixtures (<see cref="Home.Layout"/>): a stand-in model per role and tier, tinted by the item
/// (a straw pallet, a framed bed, a canopy bed; a stool or a chair; a plank table or a fine one; wall lanterns, standing
/// lamps and candles that glow; rugs, tapestries, paintings, statues), instanced per role/item and sliced by the view level.
/// Models face +Z (the piece's front, away from its wall); a bed reaches from its head cell over its foot cell.
/// </summary>
public partial class HomeView : Node3D
{
    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private Shader _shader = null!;
    private readonly List<ShaderMaterial> _materials = new();
    private readonly Dictionary<(FixtureRole, int Item, int Tier), MultiMeshInstance3D> _sets = new();
    private readonly Dictionary<(FixtureRole, int, int), List<Transform3D>> _scratch = new();
    private int _drawnVersion = -1, _drawnLevel = int.MinValue;
    private Material _wood = null!, _darkWood = null!, _cloth = null!, _metal = null!, _brass = null!, _stone = null!, _flame = null!, _paper = null!;

    public void Initialize(ColonySim sim, WorldView world)
    {
        _sim = sim;
        _world = world;
        _shader = GD.Load<Shader>("res://shaders/flora.gdshader");
        _wood = Mat(new Color(0.58f, 0.42f, 0.27f));
        _darkWood = Mat(new Color(0.33f, 0.2f, 0.13f));
        _cloth = Mat(new Color(0.9f, 0.87f, 0.78f));
        _metal = Mat(new Color(0.55f, 0.57f, 0.6f));
        _brass = Mat(new Color(0.85f, 0.66f, 0.3f));
        _stone = Mat(new Color(0.72f, 0.72f, 0.74f));
        _paper = Mat(new Color(0.95f, 0.92f, 0.85f));
        _flame = Mat(new Color(1f, 0.78f, 0.4f), 1.6f);
    }

    private ShaderMaterial Mat(Color albedo, float emission = 0f)
    {
        var m = new ShaderMaterial { Shader = _shader };
        m.SetShaderParameter("albedo", albedo);
        m.SetShaderParameter("tint_mix", 0.0f);
        m.SetShaderParameter("emission", emission);
        _materials.Add(m);
        return m;
    }

    public override void _Process(double delta)
    {
        if (_sim == null) return;
        int level = _world.ViewLevel;
        if (level != _drawnLevel)
        {
            _drawnLevel = level;
            foreach (var m in _materials) m.SetShaderParameter("cut_height", (float)(level + 1));
        }
        if (_sim.Housing.Version == _drawnVersion) return;
        _drawnVersion = _sim.Housing.Version;
        foreach (var list in _scratch.Values) list.Clear();
        foreach (var home in _sim.Housing.All)
            foreach (var p in home.Layout)
            {
                var key = (p.Role, (int)p.Item.Index, p.Tier);
                if (!_scratch.TryGetValue(key, out var list)) _scratch[key] = list = new List<Transform3D>();
                list.Add(Place(p));
            }
        foreach (var (key, list) in _scratch)
        {
            if (!_sets.TryGetValue(key, out var mmi))
            {
                mmi = new MultiMeshInstance3D
                {
                    Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = Model(key.Item1, new ItemType((ushort)key.Item2), key.Item3) },
                };
                AddChild(mmi);
                _sets[key] = mmi;
            }
            var mm = mmi.Multimesh;
            mm.InstanceCount = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                mm.SetInstanceTransform(i, list[i]);
                mm.SetInstanceCustomData(i, new Color(1, 1, 1));
            }
        }
    }

    private static readonly float[] Angle = [Mathf.Pi / 2, -Mathf.Pi / 2, 0, Mathf.Pi];

    private static Transform3D Place(Placement p) =>
        new(new Basis(Vector3.Up, Angle[p.Facing]), new Vector3(p.Cell.X + 0.5f + p.X, p.Cell.Z + p.Height, p.Cell.Y + 0.5f + p.Y));

    // ---- Models (unit cell, floor at y = 0, front towards +Z, wall at z = -0.5) ----------------------------------

    private Mesh Model(FixtureRole role, ItemType item, int tier)
    {
        var c = Mat(Rgb(item.Def.Color));
        var p = new Parts();
        var wood = tier >= 3 ? _darkWood : _wood;
        switch (role)
        {
            case FixtureRole.Bed:
                if (tier <= 1)
                {
                    p.Add(Box(0.78f, 0.13f, 1.75f), 0, 0.065f, 0.5f, c);           // a straw pallet on the floor
                    p.Add(Box(0.42f, 0.07f, 0.22f), 0, 0.16f, -0.22f, _cloth);
                    break;
                }
                p.Add(Box(0.88f, 0.16f, 1.86f), 0, 0.2f, 0.5f, wood);               // frame
                foreach (var (x, z) in new[] { (-0.4f, -0.4f), (0.4f, -0.4f), (-0.4f, 1.4f), (0.4f, 1.4f) })
                    p.Add(Box(0.07f, 0.2f, 0.07f), x, 0.1f, z, wood);
                p.Add(Box(0.88f, 0.6f, 0.07f), 0, 0.42f, -0.43f, wood);             // headboard
                p.Add(Box(0.8f, 0.12f, 1.72f), 0, 0.34f, 0.52f, _cloth);             // mattress
                p.Add(Box(0.82f, 0.05f, 1.1f), 0, 0.42f, 0.82f, c);                  // blanket
                p.Add(Box(0.5f, 0.08f, 0.24f), 0, 0.44f, -0.2f, _cloth);             // pillow
                if (tier >= 3)
                {
                    foreach (var (x, z) in new[] { (-0.42f, -0.42f), (0.42f, -0.42f), (-0.42f, 1.42f), (0.42f, 1.42f) })
                        p.Add(Cyl(0.035f, 1.6f, 6), x, 0.8f, z, wood);
                    p.Add(Box(0.92f, 0.03f, 1.92f), 0, 1.6f, 0.5f, c);               // canopy
                    p.Add(Box(0.92f, 0.5f, 0.02f), 0, 1.35f, -0.45f, c);
                }
                break;
            case FixtureRole.Table:
                p.Add(Box(tier <= 1 ? 0.76f : 0.84f, 0.06f, tier <= 1 ? 0.76f : 0.84f), 0, 0.43f, 0, tier <= 1 ? _wood : c);
                foreach (var (x, z) in new[] { (-0.32f, -0.32f), (0.32f, -0.32f), (-0.32f, 0.32f), (0.32f, 0.32f) })
                    p.Add(Box(0.06f, 0.42f, 0.06f), x, 0.21f, z, wood);
                if (tier >= 3) p.Add(Box(0.86f, 0.02f, 0.86f), 0, 0.4f, 0, _brass); // gilt edge
                break;
            case FixtureRole.Chair:
                if (tier <= 1)
                {
                    p.Add(Cyl(0.15f, 0.05f, 8), 0, 0.3f, 0.12f, _wood);           // a stool by the table
                    foreach (var (x, z) in new[] { (-0.09f, 0.05f), (0.09f, 0.05f), (0f, 0.2f) })
                        p.Add(Cyl(0.025f, 0.3f, 5), x, 0.14f, z, _wood);
                    break;
                }
                p.Add(Box(0.34f, 0.05f, 0.34f), 0, 0.3f, 0.12f, c);
                p.Add(Box(0.34f, 0.4f, 0.05f), 0, 0.52f, -0.04f, wood);
                foreach (var (x, z) in new[] { (-0.14f, -0.02f), (0.14f, -0.02f), (-0.14f, 0.26f), (0.14f, 0.26f) })
                    p.Add(Box(0.04f, 0.3f, 0.04f), x, 0.15f, z, wood);
                break;
            case FixtureRole.Chest:
                p.Add(Box(0.7f, 0.34f, 0.42f), 0, 0.17f, -0.25f, tier <= 1 ? _wood : c);
                p.Add(Box(0.72f, 0.06f, 0.44f), 0, 0.37f, -0.25f, wood);
                p.Add(Box(0.08f, 0.1f, 0.02f), 0, 0.3f, -0.03f, _metal);
                break;
            case FixtureRole.Shelf:
                foreach (float x in new[] { -0.38f, 0.38f }) p.Add(Box(0.05f, 1.3f, 0.3f), x, 0.65f, -0.33f, wood);
                for (int i = 0; i < 4; i++) p.Add(Box(0.8f, 0.04f, 0.3f), 0, 0.05f + i * 0.4f, -0.33f, wood);
                for (int i = 0; i < 5; i++) p.Add(Box(0.07f, 0.24f, 0.2f), -0.25f + i * 0.09f, 0.57f, -0.35f, i % 2 == 0 ? c : _paper);
                break;
            case FixtureRole.Tableware:
                if (tier <= 1)
                {
                    p.Add(Cyl(0.08f, 0.045f, 8), 0, 0.022f, 0, c);                 // a wooden bowl
                    break;
                }
                p.Add(Cyl(0.1f, 0.015f, 12), 0, 0.008f, 0, c);                       // plate
                p.Add(tier >= 3 ? Cone(0.035f, 0.045f, 0.14f, 8) : Cyl(0.035f, 0.07f, 8), 0.1f, tier >= 3 ? 0.07f : 0.035f, -0.06f, c);
                break;
            case FixtureRole.TableLamp:
                p.Add(Cyl(0.04f, 0.02f, 8), 0, 0.01f, 0, _metal);
                p.Add(Cyl(0.015f, 0.12f, 6), 0, 0.08f, 0, c);
                p.Add(Ball(0.022f), 0, 0.16f, 0, _flame);
                break;
            case FixtureRole.WallLamp:
                p.Add(Box(0.04f, 0.04f, 0.16f), 0, 0, -0.42f, _metal);              // bracket
                p.Add(Cyl(0.06f, 0.14f, 8), 0, -0.04f, -0.34f, _flame);              // glowing glass
                p.Add(Cone(0.075f, 0.02f, 0.06f, 8), 0, 0.06f, -0.34f, c);
                break;
            case FixtureRole.FloorLamp:
                p.Add(Cyl(0.13f, 0.04f, 10), 0, 0.02f, 0, c);
                p.Add(Cyl(0.025f, 1.2f, 6), 0, 0.62f, 0, _brass);
                p.Add(Ball(0.11f), 0, 1.3f, 0, Mat(Rgb(item.Def.Color), 1.4f));
                break;
            case FixtureRole.Rug:
                p.Add(Box(0.95f, 0.012f, 1.35f), 0, 0.006f, 0, c);
                p.Add(Box(0.75f, 0.014f, 1.15f), 0, 0.008f, 0, tier >= 2 ? _brass : _cloth);
                p.Add(Box(0.6f, 0.016f, 1.0f), 0, 0.009f, 0, c);
                break;
            case FixtureRole.WallHanging:
                if (item.Def.Model == "frames")
                {
                    p.Add(Box(0.62f, 0.46f, 0.04f), 0, 0, -0.47f, _brass);           // a painting
                    p.Add(Box(0.52f, 0.36f, 0.02f), 0, 0, -0.445f, c);
                    break;
                }
                p.Add(Box(0.76f, 0.03f, 0.03f), 0, 0.42f, -0.47f, wood);            // a tapestry on its rod
                p.Add(Box(0.7f, 0.85f, 0.015f), 0, 0, -0.475f, c);
                break;
            case FixtureRole.Statue:
                p.Add(Box(0.42f, 0.32f, 0.42f), 0, 0.16f, 0, _stone);
                p.Add(Cyl(0.11f, 0.5f, 8), 0, 0.57f, 0, c);
                p.Add(Ball(0.1f), 0, 0.9f, 0, c);
                break;
        }
        return Compose(p);
    }
}
