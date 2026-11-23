using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Orders;
using Colony.Core.World;
using Godot;
using static ColonyGame.MeshKit;

namespace ColonyGame;

/// <summary>
/// Draws thin walls, doors and windows on cell edges (<see cref="EdgeInfo"/>), each wall material with its own build:
/// stacked logs, rough rubble, plank panels, coursed stone blocks, brick; a coping along the top and posts where walls
/// meet. Doors have a frame and a leaf, windows a sill, glass and a cross. Edge blueprints show as translucent panels.
/// Instanced per (kind, material), rebuilt when the world's edges or the blueprints change; sliced by the view level.
/// Models run along local X (one cell long, centred on the edge), floor at y = 0, one level tall.
/// </summary>
public partial class EdgeView : Node3D
{
    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private Shader _shader = null!;
    private readonly List<ShaderMaterial> _materials = new();
    private readonly Dictionary<(EdgeKind Kind, int Material), MultiMeshInstance3D> _pieces = new();
    private readonly Dictionary<int, MultiMeshInstance3D> _posts = new();
    private MultiMeshInstance3D _ghosts = null!;
    private long _drawnEdges = -1;
    private int _drawnSites = -1, _drawnLevel = int.MinValue;
    private Material _darkWood = null!, _plank = null!, _brass = null!, _glass = null!;

    public void Initialize(ColonySim sim, WorldView world)
    {
        _sim = sim;
        _world = world;
        _shader = GD.Load<Shader>("res://shaders/flora.gdshader");
        _darkWood = Mat(new Color(0.32f, 0.21f, 0.13f));
        _plank = Mat(new Color(0.62f, 0.45f, 0.28f));
        _brass = Mat(new Color(0.85f, 0.66f, 0.3f));
        _glass = Mat(new Color(0.62f, 0.82f, 0.92f), 0.25f);
        _ghosts = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new BoxMesh { Size = new Vector3(1f, 1f, 0.12f), Material = Overlay.Material(new Color(0.75f, 0.85f, 1f, 0.35f)) },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_ghosts);
    }

    private ShaderMaterial Mat(Color albedo, float emission = 0f)
    {
        var m = new ShaderMaterial { Shader = _shader };
        m.SetShaderParameter("albedo", albedo);
        m.SetShaderParameter("tint_mix", 0.0f);
        m.SetShaderParameter("emission", emission);
        // Made after the level was set (the first wall of a material built later): sliced like the rest from the start.
        if (_drawnLevel != int.MinValue) m.SetShaderParameter("cut_height", (float)(_drawnLevel + 1));
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
        if (_sim.World.EdgeRevision != _drawnEdges) RedrawEdges();
        if (_sim.Constructions.Version != _drawnSites) RedrawGhosts();
    }

    /// <summary>Where an edge's piece stands: the middle of the cell side, turned to run along it.</summary>
    public static Transform3D EdgeTransform(TilePos cell, int side) => side == EdgeInfo.East
        ? new Transform3D(new Basis(Vector3.Up, -Mathf.Pi / 2), new Vector3(cell.X + 1, cell.Z, cell.Y + 0.5f))
        : new Transform3D(Basis.Identity, new Vector3(cell.X + 0.5f, cell.Z, cell.Y + 1));

    private void RedrawEdges()
    {
        _drawnEdges = _sim.World.EdgeRevision;
        var pieces = new Dictionary<(EdgeKind, int), List<Transform3D>>();
        var posts = new Dictionary<(int X, int Y, int Z), int>(); // grid corners touched by an edge, and a material for the post
        foreach (var (cell, side, info) in _sim.World.AllEdges())
        {
            var key = (EdgeInfo.Kind(info), WallMaterials.Clamp(EdgeInfo.Material(info)));
            if (!pieces.TryGetValue(key, out var list)) pieces[key] = list = new List<Transform3D>();
            list.Add(EdgeTransform(cell, side));
            int m = key.Item2;
            if (side == EdgeInfo.East)
            {
                posts[(cell.X + 1, cell.Y, cell.Z)] = m;
                posts[(cell.X + 1, cell.Y + 1, cell.Z)] = m;
            }
            else
            {
                posts[(cell.X, cell.Y + 1, cell.Z)] = m;
                posts[(cell.X + 1, cell.Y + 1, cell.Z)] = m;
            }
        }
        foreach (var mmi in _pieces.Values) mmi.Multimesh.InstanceCount = 0;
        foreach (var (key, list) in pieces)
        {
            if (!_pieces.TryGetValue(key, out var mmi)) _pieces[key] = mmi = Instances(PieceMesh(key.Item1, key.Item2));
            Fill(mmi, list);
        }
        var byMat = new Dictionary<int, List<Transform3D>>();
        foreach (var ((x, y, z), m) in posts)
        {
            if (!byMat.TryGetValue(m, out var list)) byMat[m] = list = new List<Transform3D>();
            list.Add(new Transform3D(Basis.Identity, new Vector3(x, z, y)));
        }
        foreach (var mmi in _posts.Values) mmi.Multimesh.InstanceCount = 0;
        foreach (var (m, list) in byMat)
        {
            if (!_posts.TryGetValue(m, out var mmi)) _posts[m] = mmi = Instances(PostMesh(m));
            Fill(mmi, list);
        }
    }

    private void RedrawGhosts()
    {
        _drawnSites = _sim.Constructions.Version;
        var list = new List<Transform3D>();
        foreach (var bp in _sim.Constructions.All)
            if (bp.IsEdge) list.Add(EdgeTransform(bp.EdgeCell, bp.Side).Translated(new Vector3(0, 0.5f, 0)));
        Fill(_ghosts, list, customData: false);
    }

    private MultiMeshInstance3D Instances(Mesh mesh)
    {
        var mmi = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = mesh },
        };
        AddChild(mmi);
        return mmi;
    }

    private static void Fill(MultiMeshInstance3D mmi, List<Transform3D> list, bool customData = true)
    {
        var mm = mmi.Multimesh;
        mm.InstanceCount = list.Count;
        for (int i = 0; i < list.Count; i++)
        {
            mm.SetInstanceTransform(i, list[i]);
            if (customData) mm.SetInstanceCustomData(i, new Color(1, 1, 1));
        }
    }

    // ---- Models ---------------------------------------------------------------------------------------------

    private readonly Dictionary<int, (Material Main, Material Dark)> _wallMats = new();

    private (Material Main, Material Dark) WallMats(int material)
    {
        if (_wallMats.TryGetValue(material, out var m)) return m;
        var c = Rgb(WallMaterials.All[material].Color);
        m = (Mat(c), Mat(c.Darkened(material == 4 ? -0.6f : 0.3f))); // brick's mortar is pale
        _wallMats[material] = m;
        return m;
    }

    /// <summary>A full-height stretch of the material's wall, from <paramref name="y0"/> to <paramref name="y1"/>, <paramref name="width"/> long.</summary>
    private void WallSegment(Parts p, int material, float x, float width, float y0, float y1)
    {
        var (main, dark) = WallMats(material);
        float h = y1 - y0, cy = (y0 + y1) / 2;
        switch (material)
        {
            case 0: // logs, stacked
                for (float y = y0 + 0.11f; y < y1 - 0.05f; y += 0.23f)
                    p.Add(Cyl(0.115f, width, 7), AlongX(x, y, 0), main);
                break;
            case 1: // rubble: a dark core with uneven stones
                p.Add(Box(width, h, 0.16f), x, cy, 0, dark);
                int row = 0;
                for (float y = y0 + 0.1f; y < y1 - 0.05f; y += 0.2f, row++)
                    for (float sx = x - width / 2 + 0.12f + (row % 2) * 0.1f; sx < x + width / 2 - 0.08f; sx += 0.26f)
                        p.Add(Box(0.22f + (row * 7 + (int)(sx * 10)) % 3 * 0.03f, 0.17f, 0.2f), sx, y, 0, main);
                break;
            case 2: // plank panel with rails
                p.Add(Box(width, h, 0.1f), x, cy, 0, main);
                for (float bx = x - width / 2 + 0.2f; bx < x + width / 2 - 0.05f; bx += 0.2f) p.Add(Box(0.015f, h, 0.112f), bx, cy, 0, dark);
                p.Add(Box(width, 0.06f, 0.13f), x, y0 + 0.03f, 0, dark);
                break;
            case 3: // coursed stone blocks over mortar
                p.Add(Box(width, h, 0.17f), x, cy, 0, dark);
                row = 0;
                for (float y = y0 + 0.12f; y < y1 - 0.05f; y += 0.245f, row++)
                    for (float sx = x - width / 2 + (row % 2 == 0 ? 0.25f : 0.0f); sx < x + width / 2 + 0.01f; sx += 0.5f)
                    {
                        float l = System.Math.Min(sx + 0.24f, x + width / 2) - System.Math.Max(sx - 0.24f, x - width / 2);
                        if (l > 0.04f) p.Add(Box(l, 0.225f, 0.19f), (System.Math.Min(sx + 0.24f, x + width / 2) + System.Math.Max(sx - 0.24f, x - width / 2)) / 2, y, 0, main);
                    }
                break;
            default: // brick courses over pale mortar
                p.Add(Box(width, h, 0.15f), x, cy, 0, dark);
                row = 0;
                for (float y = y0 + 0.06f; y < y1 - 0.03f; y += 0.125f, row++)
                    for (float sx = x - width / 2 + (row % 2 == 0 ? 0.125f : 0f); sx < x + width / 2 + 0.01f; sx += 0.25f)
                    {
                        float a = System.Math.Max(sx - 0.115f, x - width / 2), b = System.Math.Min(sx + 0.115f, x + width / 2);
                        if (b - a > 0.03f) p.Add(Box(b - a, 0.105f, 0.17f), (a + b) / 2, y, 0, main);
                    }
                break;
        }
    }

    private Mesh PieceMesh(EdgeKind kind, int material)
    {
        var (main, dark) = WallMats(material);
        var p = new Parts();
        switch (kind)
        {
            case EdgeKind.Door:
                WallSegment(p, material, -0.45f, 0.1f, 0, 1);              // jambs in the wall's material
                WallSegment(p, material, 0.45f, 0.1f, 0, 1);
                WallSegment(p, material, 0, 0.8f, 0.84f, 1);              // lintel
                p.Add(Box(0.8f, 0.04f, 0.2f), 0, 0.84f, 0, _darkWood);
                p.Add(Box(0.76f, 0.82f, 0.05f), 0, 0.42f, 0, _plank);     // the leaf
                p.Add(Box(0.76f, 0.05f, 0.06f), 0, 0.2f, 0, _darkWood);
                p.Add(Box(0.76f, 0.05f, 0.06f), 0, 0.64f, 0, _darkWood);
                p.Add(Ball(0.025f), 0.28f, 0.42f, 0.04f, _brass);
                p.Add(Ball(0.025f), 0.28f, 0.42f, -0.04f, _brass);
                break;
            case EdgeKind.Window:
                WallSegment(p, material, 0, 1f, 0, 0.36f);               // below the sill
                WallSegment(p, material, 0, 1f, 0.8f, 1f);              // above the frame
                WallSegment(p, material, -0.44f, 0.12f, 0.36f, 0.8f);
                WallSegment(p, material, 0.44f, 0.12f, 0.36f, 0.8f);
                p.Add(Box(0.84f, 0.04f, 0.24f), 0, 0.37f, 0, _darkWood); // sill
                p.Add(Box(0.76f, 0.42f, 0.02f), 0, 0.58f, 0, _glass);
                p.Add(Box(0.03f, 0.42f, 0.04f), 0, 0.58f, 0, _darkWood);
                p.Add(Box(0.76f, 0.03f, 0.04f), 0, 0.58f, 0, _darkWood);
                break;
            default:
                WallSegment(p, material, 0, 1f, 0, 1f);
                break;
        }
        p.Add(Box(1.02f, 0.05f, 0.22f), 0, 1.0f, 0, dark); // coping
        return Compose(p);
    }

    private Mesh PostMesh(int material)
    {
        var (main, dark) = WallMats(material);
        var p = new Parts();
        p.Add(material == 0 ? Cyl(0.13f, 1.06f, 8) : Box(0.22f, 1.04f, 0.22f), 0, 0.52f, 0, material == 0 ? main : dark);
        return Compose(p);
    }
}
