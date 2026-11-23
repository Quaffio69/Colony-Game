using System;
using System.Collections.Generic;
using Colony.Core;
using Colony.Core.World;
using Godot;
using static ColonyGame.MeshKit;

namespace ColonyGame;

/// <summary>
/// Draws floors and roofs (<see cref="CoverInfo"/>). Floors are patterned plates on the ground (plank boards, flagstones
/// in grout, two-tone tiles), instanced per material. Roofs are a height field over each level's roofed cells: every
/// vertex rises with its distance (in cells) from the roof's edge, so any outline gets sloping sides and a ridge (hip
/// roofs) on its own; one mesh per material, rebuilt when coverings change. Roofs sit above their cell (from its top),
/// so looking into a home at its own level slices them away. Blueprints show as translucent plates.
/// </summary>
public partial class CoverView : Node3D
{
    private const float Pitch = 0.45f; // rise per cell in from the eaves

    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private Shader _shader = null!;
    // Floor plates and stair flights: sliced at the view level itself, so an upper storey's floor (at the top of the viewed
    // level) doesn't lid the storey being looked into. Roofs: at the level above (they sit over their cell).
    private readonly List<ShaderMaterial> _materials = new();
    private readonly List<ShaderMaterial> _roofMaterials = new();
    private readonly Dictionary<int, MultiMeshInstance3D> _floors = new();
    private MeshInstance3D _roofs = null!;
    private MultiMeshInstance3D _ghosts = null!;
    private long _drawnCovers = -1;
    private int _drawnSites = -1, _drawnLevel = int.MinValue;

    public void Initialize(ColonySim sim, WorldView world)
    {
        _sim = sim;
        _world = world;
        _shader = GameLooks.Shader("res://shaders/flora.gdshader");
        _roofs = new MeshInstance3D();
        AddChild(_roofs);
        _ghosts = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new BoxMesh { Size = new Vector3(0.94f, 0.06f, 0.94f), Material = Overlay.Material(new Color(0.75f, 0.85f, 1f, 0.35f)) },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_ghosts);
        var ghostMat = Overlay.Material(new Color(0.75f, 0.85f, 1f, 0.35f));
        _stairGhosts = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = Flight(ghostMat, ghostMat) },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_stairGhosts);
        _stairs = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true },
        };
        AddChild(_stairs);
    }

    // ---- Stairs: a flight of steps in every stair cell that leads up (a down stair is the top of the flight below) ----

    private MultiMeshInstance3D _stairs = null!, _stairGhosts = null!;
    private long _drawnStairs = -1;
    private double _stairsCheckedAt = -1e9, _now;

    /// <summary>Four treads rising across the cell from its floor to the next level, on two stringers.</summary>
    private static Mesh Flight(Material tread, Material side)
    {
        var p = new Parts();
        for (int i = 0; i < 4; i++)
            p.Add(Box(0.8f, 0.1f, 0.25f), 0, 0.2f + i * 0.25f, -0.375f + i * 0.25f, tread);
        var slope = new Basis(Vector3.Right, -Mathf.Pi / 4); // rising to the far side (+z)
        p.Add(Box(0.08f, 0.1f, 1.38f), new Transform3D(slope, new Vector3(-0.42f, 0.5f, 0)), side);
        p.Add(Box(0.08f, 0.1f, 1.38f), new Transform3D(slope, new Vector3(0.42f, 0.5f, 0)), side);
        return Compose(p);
    }

    private void RedrawStairs()
    {
        _drawnStairs = _sim.World.Revision;
        var mm = _stairs.Multimesh;
        if (mm.Mesh == null)
        {
            var wood = new Color(0.62f, 0.46f, 0.3f);
            mm.Mesh = Flight(Mat(wood), Mat(wood.Darkened(0.25f)));
        }
        var list = new List<Transform3D>();
        foreach (var (cell, type) in _sim.World.AllStairs())
            if (TileInfo.GoesUp(type))
                list.Add(new Transform3D(Basis.Identity, new Vector3(cell.X + 0.5f, cell.Z, cell.Y + 0.5f)));
        mm.InstanceCount = list.Count;
        for (int i = 0; i < list.Count; i++)
        {
            mm.SetInstanceTransform(i, list[i]);
            mm.SetInstanceCustomData(i, new Color(1, 1, 1));
        }
    }

    private ShaderMaterial Mat(Color albedo, bool instanced = true)
    {
        var m = new ShaderMaterial { Shader = _shader };
        m.SetShaderParameter("albedo", albedo);
        m.SetShaderParameter("tint_mix", 0.0f);
        m.SetShaderParameter("instanced", instanced);
        m.SetShaderParameter("object_id", (float)GameLooks.Construction);
        GameLooks.Styled(m);
        m.SetShaderParameter("cut_height", (float)_drawnLevel);
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
            foreach (var m in _materials) m.SetShaderParameter("cut_height", (float)level);
            foreach (var m in _roofMaterials) m.SetShaderParameter("cut_height", (float)(level + 1));
        }
        if (_sim.World.CoverRevision != _drawnCovers) Redraw();
        if (_sim.Constructions.Version != _drawnSites) RedrawGhosts();
        // Stairs follow tile changes, checked twice a second (only chunks with stairs are read).
        _now += delta;
        if (_sim.World.Revision != _drawnStairs && _now - _stairsCheckedAt > 0.5)
        {
            _stairsCheckedAt = _now;
            RedrawStairs();
        }
    }

    private void Redraw()
    {
        _drawnCovers = _sim.World.CoverRevision;
        var floors = new Dictionary<int, List<Transform3D>>();
        var roofs = new Dictionary<TilePos, int>();
        foreach (var (cell, cover) in _sim.World.AllCovers())
        {
            int f = CoverInfo.Floor(cover), r = CoverInfo.Roof(cover);
            if (f > 0)
            {
                if (!floors.TryGetValue(f, out var list)) floors[f] = list = new List<Transform3D>();
                float turn = f == 1 ? 0 : (cell.X * 7 + cell.Y * 13) % 4 * Mathf.Pi / 2; // boards run one way; stones vary
                list.Add(new Transform3D(new Basis(Vector3.Up, turn), new Vector3(cell.X + 0.5f, cell.Z, cell.Y + 0.5f)));
            }
            if (r > 0) roofs[cell] = r;
        }
        foreach (var mmi in _floors.Values) mmi.Multimesh.InstanceCount = 0;
        foreach (var (f, list) in floors)
        {
            if (!_floors.TryGetValue(f, out var mmi))
            {
                mmi = new MultiMeshInstance3D
                {
                    Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = FloorMesh(f) },
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                AddChild(mmi);
                _floors[f] = mmi;
            }
            var mm = mmi.Multimesh;
            mm.InstanceCount = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                mm.SetInstanceTransform(i, list[i]);
                mm.SetInstanceCustomData(i, new Color(1, 1, 1));
            }
        }
        _roofs.Mesh = RoofMesh(roofs);
    }

    private void RedrawGhosts()
    {
        _drawnSites = _sim.Constructions.Version;
        var list = new List<Transform3D>();
        var stairs = new List<Transform3D>();
        foreach (var bp in _sim.Constructions.All)
        {
            if (bp.IsCover || bp.Kind == Colony.Core.Orders.StructureKind.Deck) // a deck: a plate at its own level
                list.Add(new Transform3D(Basis.Identity, new Vector3(bp.Target.X + 0.5f, bp.Target.Z + (bp.Kind == Colony.Core.Orders.StructureKind.Roof ? 1.03f : 0.03f), bp.Target.Y + 0.5f)));
            else if (bp.Kind == Colony.Core.Orders.StructureKind.Stair && TileInfo.GoesUp((TileType)bp.Material))
                stairs.Add(new Transform3D(Basis.Identity, new Vector3(bp.Target.X + 0.5f, bp.Target.Z, bp.Target.Y + 0.5f)));
        }
        var mm = _ghosts.Multimesh;
        mm.InstanceCount = list.Count;
        for (int i = 0; i < list.Count; i++) mm.SetInstanceTransform(i, list[i]);
        var sm = _stairGhosts.Multimesh;
        sm.InstanceCount = stairs.Count;
        for (int i = 0; i < stairs.Count; i++) sm.SetInstanceTransform(i, stairs[i]);
    }

    // ---- Floors ---------------------------------------------------------------------------------------------

    private readonly Dictionary<int, Mesh> _floorMeshes = new();

    private Mesh FloorMesh(int floor)
    {
        if (_floorMeshes.TryGetValue(floor, out var cached)) return cached;
        var c = Rgb(FloorMaterials.All[floor].Color);
        var main = Mat(c);
        var p = new Parts();
        switch (floor)
        {
            case 1: // boards with seams
                p.Add(Box(1f, 0.02f, 1f), 0, 0.01f, 0, Mat(c.Darkened(0.45f)));
                for (int i = 0; i < 5; i++)
                {
                    float shade = (i * 37 % 5) * 0.04f;
                    p.Add(Box(0.185f, 0.035f, 1f), -0.4f + i * 0.2f, 0.018f, 0, i % 2 == 0 ? main : Mat(c.Darkened(0.08f + shade)));
                }
                break;
            case 2: // flagstones in grout
                p.Add(Box(1f, 0.02f, 1f), 0, 0.01f, 0, Mat(c.Darkened(0.4f)));
                foreach (var (x, z, w, d) in new[] { (-0.24f, -0.27f, 0.47f, 0.42f), (0.26f, -0.2f, 0.44f, 0.56f), (-0.27f, 0.24f, 0.42f, 0.48f), (0.24f, 0.31f, 0.48f, 0.34f) })
                    p.Add(Box(w, 0.035f, d), x, 0.018f, z, (int)(x * 10 + z * 7) % 2 == 0 ? main : Mat(c.Lightened(0.08f)));
                break;
            default: // two-tone tiles
                p.Add(Box(1f, 0.02f, 1f), 0, 0.01f, 0, Mat(new Color(0.85f, 0.82f, 0.75f)));
                var light = Mat(c.Lightened(0.35f));
                for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    p.Add(Box(0.23f, 0.035f, 0.23f), -0.375f + i * 0.25f, 0.018f, -0.375f + j * 0.25f, (i + j) % 2 == 0 ? main : light);
                break;
        }
        var mesh = Compose(p);
        _floorMeshes[floor] = mesh;
        return mesh;
    }

    // ---- Roofs ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The roofed cells of each level as one height field per material: a vertex's height is the pitch times its
    /// distance in from the roof's edge (the smallest edge distance of the four cells round it, 0 beside an unroofed
    /// cell), on top of the cell's top. Each cell is two triangles over its four corners; flat-shaded.
    /// </summary>
    private ArrayMesh? RoofMesh(Dictionary<TilePos, int> roofs)
    {
        if (roofs.Count == 0) return null;
        // Distance of each roofed cell from the roof's edge (1 = on the edge), by flood from the outside.
        var dist = new Dictionary<TilePos, int>();
        var queue = new Queue<TilePos>();
        foreach (var c in roofs.Keys)
        {
            bool edge = !roofs.ContainsKey(c.Offset(1, 0, 0)) || !roofs.ContainsKey(c.Offset(-1, 0, 0)) ||
                        !roofs.ContainsKey(c.Offset(0, 1, 0)) || !roofs.ContainsKey(c.Offset(0, -1, 0));
            if (!edge) continue;
            dist[c] = 1;
            queue.Enqueue(c);
        }
        while (queue.TryDequeue(out var c))
            foreach (var n in new[] { c.Offset(1, 0, 0), c.Offset(-1, 0, 0), c.Offset(0, 1, 0), c.Offset(0, -1, 0) })
                if (roofs.ContainsKey(n) && !dist.ContainsKey(n))
                {
                    dist[n] = dist[c] + 1;
                    queue.Enqueue(n);
                }
        int D(int x, int y, int z) => dist.TryGetValue(new TilePos(x, y, z), out int d) ? d : 0;
        float H(int vx, int vy, int z) => Pitch * Math.Min(Math.Min(D(vx - 1, vy - 1, z), D(vx, vy - 1, z)), Math.Min(D(vx - 1, vy, z), D(vx, vy, z)));

        var byMat = new Dictionary<int, (List<Vector3> V, List<Vector3> N)>();
        foreach (var (c, m) in roofs)
        {
            if (!byMat.TryGetValue(m, out var lists)) byMat[m] = lists = (new List<Vector3>(), new List<Vector3>());
            float top = c.Z + 1.02f;
            var v00 = new Vector3(c.X, top + H(c.X, c.Y, c.Z), c.Y);
            var v10 = new Vector3(c.X + 1, top + H(c.X + 1, c.Y, c.Z), c.Y);
            var v11 = new Vector3(c.X + 1, top + H(c.X + 1, c.Y + 1, c.Z), c.Y + 1);
            var v01 = new Vector3(c.X, top + H(c.X, c.Y + 1, c.Z), c.Y + 1);
            // Split along the diagonal that keeps the ridge sharp.
            bool flip = Math.Abs(v00.Y - v11.Y) > Math.Abs(v10.Y - v01.Y);
            if (flip)
            {
                Tri(lists, v00, v10, v01);
                Tri(lists, v10, v11, v01);
            }
            else
            {
                Tri(lists, v00, v10, v11);
                Tri(lists, v00, v11, v01);
            }
        }
        var mesh = new ArrayMesh();
        foreach (var (m, (v, n)) in byMat)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = n.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, RoofMaterial(m));
        }
        return mesh;
    }

    private static void Tri((List<Vector3> V, List<Vector3> N) lists, Vector3 a, Vector3 b, Vector3 c)
    {
        var n = (c - a).Cross(b - a).Normalized();
        if (n.Y < 0)
        {
            (b, c) = (c, b);
            n = -n;
        }
        lists.V.Add(a);
        lists.V.Add(b);
        lists.V.Add(c);
        for (int i = 0; i < 3; i++) lists.N.Add(n);
    }

    private readonly Dictionary<int, Material> _roofMats = new();
    private Shader? _roofShader;

    /// <summary>Thatch in fine close courses, shingle and slate in rows, clay tiles in fewer, rounder ones.</summary>
    private Material RoofMaterial(int roof)
    {
        if (_roofMats.TryGetValue(roof, out var m)) return m;
        _roofShader ??= GameLooks.Shader("res://shaders/roof.gdshader");
        var mat = new ShaderMaterial { Shader = _roofShader };
        mat.SetShaderParameter("albedo", Rgb(RoofMaterials.All[roof].Color));
        mat.SetShaderParameter("course", roof switch { 1 => 11f, 4 => 5f, _ => 7f });
        mat.SetShaderParameter("joints", roof switch { 1 => 9f, 3 => 2.5f, 4 => 3.5f, _ => 3f });
        mat.SetShaderParameter("cut_height", (float)(_drawnLevel + 1));
        GameLooks.Styled(mat);
        _roofMaterials.Add(mat);
        _roofMats[roof] = mat;
        return mat;
    }
}
