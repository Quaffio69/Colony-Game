using System;
using System.Collections.Generic;
using System.Diagnostics;
using Colony.Core;
using Colony.Core.Nature;
using Godot;

namespace ColonyGame;

/// <summary>
/// Draws trees and surface plants as instanced low-poly stand-ins. Per <see cref="Flora"/> region:
/// <list type="bullet">
/// <item>one MultiMesh per plant kind with a detailed mesh, shown only near the camera;</item>
/// <item>one MultiMesh of simple coloured markers for all of the region's trees, shown at a distance.</item>
/// </list>
/// Regions rebuild only when their flora version changes, within a per-frame time budget. The view-level
/// slice is a shader cut (no rebuild on level change).
/// </summary>
public partial class FloraView : Node3D
{
    private const float NearTreeRange = 320f, StyledTreeRange = 200f;
    /// <summary>Full-detail trees within this distance of the camera, markers beyond (the styled look's trees are heavier).</summary>
    private float _treeRange = NearTreeRange;
    private bool _detail = true;
    private readonly List<(MultiMeshInstance3D Node, bool Tree, bool Marker)> _ranged = new();
    private const float NearPlantRange = 180f;
    private const double BuildBudgetMs = 4;

    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private Flora _flora = null!;
    private readonly Mesh[] _meshes = new Mesh[PlantInfo.KindCount];
    private Mesh _farMesh = null!;
    private Shader _shader = null!;
    private readonly List<ShaderMaterial> _materials = new();
    private Node3D?[] _regionNodes = [];
    private int[] _builtVersions = [];
    private int _appliedLevel = int.MinValue;

    public void Initialize(ColonySim sim, WorldView world)
    {
        _sim = sim;
        _world = world;
        _flora = sim.Flora;
        _shader = GameLooks.Shader("res://shaders/flora.gdshader");
        BuildMeshes();
        int regions = _flora.RegionsX * _flora.RegionsY;
        _regionNodes = new Node3D?[regions];
        _builtVersions = new int[regions];
        Array.Fill(_builtVersions, -1);
    }

    public override void _Process(double delta)
    {
        if (_sim == null) return;

        if (_world.ViewLevel != _appliedLevel)
        {
            _appliedLevel = _world.ViewLevel;
            foreach (var m in _materials) m.SetShaderParameter("cut_height", (float)(_appliedLevel + 1));
        }

        var clock = Stopwatch.StartNew();
        for (int ry = 0; ry < _flora.RegionsY; ry++)
        for (int rx = 0; rx < _flora.RegionsX; rx++)
        {
            int r = ry * _flora.RegionsX + rx;
            int v = _flora.RegionVersion(rx, ry);
            if (v == _builtVersions[r]) continue;
            if (clock.Elapsed.TotalMilliseconds > BuildBudgetMs) return;
            BuildRegion(rx, ry);
            _builtVersions[r] = v;
        }
    }

    private float TreeEnd => _detail ? _treeRange : 0.01f;   // 0 would mean "no limit"
    private float PlantEnd => _detail ? NearPlantRange : 0.01f;

    /// <summary>
    /// Full-detail plants (false: trees only as their light distant markers, plants hidden). The styled look turns detail
    /// off once its far view is mostly symbols, where the 3D detail can't be seen but still costs millions of triangles.
    /// </summary>
    public bool Detail
    {
        get => _detail;
        set
        {
            if (_detail == value) return;
            _detail = value;
            ApplyRanges();
        }
    }

    private void ApplyRanges()
    {
        _ranged.RemoveAll(r => !IsInstanceValid(r.Node));
        foreach (var (node, tree, marker) in _ranged)
        {
            if (marker) node.VisibilityRangeBegin = TreeEnd;
            else node.VisibilityRangeEnd = tree ? TreeEnd : PlantEnd;
        }
    }

    /// <summary>Plants still waiting to be drawn (for loading progress).</summary>
    public int PendingRegions
    {
        get
        {
            int n = 0;
            for (int ry = 0; ry < _flora.RegionsY; ry++)
            for (int rx = 0; rx < _flora.RegionsX; rx++)
                if (_flora.RegionVersion(rx, ry) != _builtVersions[ry * _flora.RegionsX + rx]) n++;
            return n;
        }
    }

    // ---- Regions --------------------------------------------------------------------------------------

    private void BuildRegion(int rx, int ry)
    {
        int r = ry * _flora.RegionsX + rx;
        _regionNodes[r]?.QueueFree();
        _regionNodes[r] = null;
        var plants = _flora.InRegion(rx, ry);
        if (plants.Count == 0) return;

        // Instances are positioned relative to the region centre, so visibility ranges measure from there.
        var origin = new Vector3((rx + 0.5f) * Flora.RegionSize, 0, (ry + 0.5f) * Flora.RegionSize);
        float avgY = 0;
        foreach (var p in plants) avgY += p.Position.Z;
        origin.Y = avgY / plants.Count;

        var node = new Node3D { Name = $"Flora_{rx}_{ry}", Position = origin };
        var byKind = new List<Plant>[PlantInfo.KindCount];
        for (int k = 0; k < byKind.Length; k++) byKind[k] = new List<Plant>();
        var trees = new List<Plant>();
        foreach (var p in plants)
        {
            byKind[(int)p.Kind].Add(p);
            if (PlantInfo.IsTree(p.Kind)) trees.Add(p);
        }

        for (int k = 0; k < byKind.Length; k++)
        {
            var list = byKind[k];
            if (list.Count == 0) continue;
            bool tree = PlantInfo.IsTree((PlantKind)k);
            var mm = NewMultiMesh(_meshes[k], list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                mm.SetInstanceTransform(i, InstanceTransform(list[i], origin));
                mm.SetInstanceCustomData(i, Tint(list[i]));
            }
            var near = new MultiMeshInstance3D
            {
                Multimesh = mm,
                VisibilityRangeEnd = tree ? TreeEnd : PlantEnd,
                VisibilityRangeEndMargin = 16,
                CastShadow = tree ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            };
            node.AddChild(near);
            _ranged.Add((near, tree, false));
        }

        if (trees.Count > 0)
        {
            var far = NewMultiMesh(_farMesh, trees.Count);
            for (int i = 0; i < trees.Count; i++)
            {
                far.SetInstanceTransform(i, InstanceTransform(trees[i], origin));
                far.SetInstanceCustomData(i, CanopyColor(trees[i].Kind).SrgbToLinear() * Brightness(trees[i]));
            }
            var markers = new MultiMeshInstance3D
            {
                Multimesh = far,
                VisibilityRangeBegin = TreeEnd,
                VisibilityRangeBeginMargin = 16,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            node.AddChild(markers);
            _ranged.Add((markers, true, true));
        }

        AddChild(node);
        _regionNodes[r] = node;
    }

    private static MultiMesh NewMultiMesh(Mesh mesh, int count) => new()
    {
        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
        UseCustomData = true,
        Mesh = mesh,
        InstanceCount = count,
    };

    private static Transform3D InstanceTransform(Plant p, Vector3 origin)
    {
        float j1 = Jitter(p.Variant, 1), j2 = Jitter(p.Variant, 2), j3 = Jitter(p.Variant, 3);
        float growth = 0.35f + 0.65f * (p.Growth / 255f);
        float scale = growth * (0.85f + 0.3f * j1);
        var basis = new Basis(Vector3.Up, j2 * Mathf.Tau).Scaled(Vector3.One * scale);
        var pos = new Vector3(p.Position.X + 0.5f + (j3 - 0.5f) * 0.4f, p.Position.Z, p.Position.Y + 0.5f + (j1 - 0.5f) * 0.4f);
        return new Transform3D(basis, pos - origin);
    }

    private static float Jitter(byte variant, int salt) => Colony.Core.Procedural.Noise.Hash(variant, salt, 0, 77);

    private static float Brightness(Plant p) => 0.85f + 0.27f * Jitter(p.Variant, 4);

    private static Color Tint(Plant p)
    {
        if (p.Kind == PlantKind.Wheat) // green shoots ripening to gold
            return new Color(0.45f, 0.75f, 0.3f).Lerp(new Color(1f, 0.85f, 0.45f), p.Growth / 255f).SrgbToLinear();
        if (p.Kind == PlantKind.Flowers)
        {
            Color[] petals = [new(1f, 0.82f, 0.2f), new(0.62f, 0.38f, 0.92f), new(1f, 1f, 1f), new(0.95f, 0.3f, 0.3f)];
            return petals[p.Variant % petals.Length].SrgbToLinear();
        }
        float b = Brightness(p);
        float warm = (Jitter(p.Variant, 5) - 0.5f) * 0.12f; // slight autumn/yellow-green drift
        return new Color(b * (1 + warm), b, b * (1 - warm));
    }

    // ---- Meshes ---------------------------------------------------------------------------------------

    private static readonly Color Bark = new(0.40f, 0.29f, 0.20f);
    private static readonly Color BirchBark = new(0.88f, 0.86f, 0.80f);

    /// <summary>Main foliage colour per kind (also the far-LOD marker colour).</summary>
    private static Color CanopyColor(PlantKind k) => k switch
    {
        PlantKind.Oak => new Color(0.30f, 0.52f, 0.22f),
        PlantKind.Birch => new Color(0.52f, 0.70f, 0.30f),
        PlantKind.Pine => new Color(0.20f, 0.42f, 0.26f),
        PlantKind.Spruce => new Color(0.16f, 0.34f, 0.25f),
        PlantKind.Willow => new Color(0.50f, 0.66f, 0.34f),
        PlantKind.Shrub => new Color(0.32f, 0.50f, 0.22f),
        PlantKind.BerryBush => new Color(0.24f, 0.42f, 0.20f),
        PlantKind.Fern => new Color(0.36f, 0.62f, 0.24f),
        PlantKind.Flowers => new Color(1f, 1f, 1f),
        PlantKind.Reeds => new Color(0.62f, 0.64f, 0.38f),
        PlantKind.Wheat => new Color(0.86f, 0.72f, 0.3f),
        PlantKind.Herbs => new Color(0.35f, 0.6f, 0.3f),
        PlantKind.Glowcap => new Color(0.36f, 0.86f, 0.8f),
        _ => Colors.Magenta,
    };

    private ShaderMaterial Material(Color albedo, float tintMix = 0.35f, float emission = 0f, int glyph = DwarfRender.GlyphAtlas.Bush)
    {
        var m = new ShaderMaterial { Shader = _shader };
        m.SetShaderParameter("object_id", (float)glyph); // styled look: what ASCII draws (trees outline as foliage)
        GameLooks.Styled(m);
        m.SetShaderParameter("albedo", albedo);
        m.SetShaderParameter("tint_mix", tintMix);
        m.SetShaderParameter("emission", emission);
        _materials.Add(m);
        return m;
    }

    private static SphereMesh Ball(float radius, float height) =>
        new() { Radius = radius, Height = height, RadialSegments = 8, Rings = 4 };

    private static CylinderMesh Rod(float bottom, float top, float height, int segments = 6) =>
        new() { BottomRadius = bottom, TopRadius = top, Height = height, RadialSegments = segments, Rings = 1 };

    private static Transform3D At(float x, float y, float z, float tiltZ = 0, float tiltX = 0) =>
        new(Basis.FromEuler(new Vector3(tiltX, 0, tiltZ)), new Vector3(x, y, z));

    /// <summary>Combines primitive parts into one mesh, one surface per material.</summary>
    private static ArrayMesh Compose(params (PrimitiveMesh Mesh, Transform3D At, Material Mat)[] parts)
    {
        var mesh = new ArrayMesh();
        var byMaterial = new Dictionary<Material, List<(PrimitiveMesh, Transform3D)>>();
        var order = new List<Material>();
        foreach (var (m, at, mat) in parts)
        {
            if (!byMaterial.TryGetValue(mat, out var list))
            {
                byMaterial[mat] = list = new List<(PrimitiveMesh, Transform3D)>();
                order.Add(mat);
            }
            list.Add((m, at));
        }
        foreach (var mat in order)
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var (m, at) in byMaterial[mat]) st.AppendFrom(m, 0, at);
            st.Commit(mesh);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
        }
        return mesh;
    }

    private void BuildMeshes()
    {
        var bark = Material(Bark, 0.1f, glyph: DwarfRender.GlyphAtlas.Trunk);
        var birchBark = Material(BirchBark, 0.1f, glyph: DwarfRender.GlyphAtlas.Trunk);
        ShaderMaterial Leaves(PlantKind k) => Material(CanopyColor(k), glyph: k switch
        {
            PlantKind.Pine or PlantKind.Spruce => DwarfRender.GlyphAtlas.Pine,
            PlantKind.Birch => DwarfRender.GlyphAtlas.Birch,
            _ => DwarfRender.GlyphAtlas.Oak,
        });

        _meshes[(int)PlantKind.Oak] = Compose(
            (Rod(0.2f, 0.15f, 2.6f), At(0, 1.3f, 0), bark),
            (Ball(1.6f, 2.6f), At(0, 3.6f, 0), Leaves(PlantKind.Oak)),
            (Ball(1.1f, 1.8f), At(0.7f, 4.3f, 0.3f), Leaves(PlantKind.Oak)));
        _meshes[(int)PlantKind.Birch] = Compose(
            (Rod(0.12f, 0.09f, 3.6f), At(0, 1.8f, 0), birchBark),
            (Ball(0.95f, 3.2f), At(0, 4.4f, 0), Leaves(PlantKind.Birch)));
        _meshes[(int)PlantKind.Pine] = Compose(
            (Rod(0.16f, 0.12f, 3.2f), At(0, 1.6f, 0), bark),
            (Rod(1.15f, 0f, 4.6f, 7), At(0, 5.0f, 0), Leaves(PlantKind.Pine)));
        var spruce = Leaves(PlantKind.Spruce);
        _meshes[(int)PlantKind.Spruce] = Compose(
            (Rod(0.15f, 0.12f, 1.4f), At(0, 0.7f, 0), bark),
            (Rod(1.45f, 0f, 3.2f, 7), At(0, 2.7f, 0), spruce),
            (Rod(1.05f, 0f, 2.6f, 7), At(0, 4.3f, 0), spruce),
            (Rod(0.65f, 0f, 1.8f, 7), At(0, 5.6f, 0), spruce));
        var willow = Leaves(PlantKind.Willow);
        _meshes[(int)PlantKind.Willow] = Compose(
            (Rod(0.24f, 0.18f, 2.4f), At(0, 1.2f, 0), bark),
            (Ball(2.0f, 2.0f), At(0, 3.3f, 0), willow),
            (Rod(2.15f, 1.85f, 1.8f, 9), At(0, 2.2f, 0), willow)); // drooping curtain

        _meshes[(int)PlantKind.Shrub] = Compose((Ball(0.45f, 0.75f), At(0, 0.33f, 0), Leaves(PlantKind.Shrub)));
        var berries = Material(new Color(0.75f, 0.12f, 0.18f), 0.1f);
        _meshes[(int)PlantKind.BerryBush] = Compose(
            (Ball(0.45f, 0.8f), At(0, 0.38f, 0), Leaves(PlantKind.BerryBush)),
            (Ball(0.08f, 0.16f), At(0.38f, 0.45f, 0.1f), berries),
            (Ball(0.08f, 0.16f), At(-0.2f, 0.62f, 0.3f), berries),
            (Ball(0.08f, 0.16f), At(0.05f, 0.5f, -0.4f), berries),
            (Ball(0.08f, 0.16f), At(-0.35f, 0.3f, -0.15f), berries));
        _meshes[(int)PlantKind.Fern] = Compose(
            (Ball(0.42f, 0.32f), At(0, 0.14f, 0), Leaves(PlantKind.Fern)),
            (Ball(0.25f, 0.3f), At(0.1f, 0.3f, 0.05f), Leaves(PlantKind.Fern)));
        var stem = Material(new Color(0.30f, 0.52f, 0.22f), 0.1f);
        var petals = Material(new Color(1f, 1f, 1f), 1f);
        _meshes[(int)PlantKind.Flowers] = Compose(
            (Ball(0.22f, 0.18f), At(0, 0.07f, 0), stem),
            (Ball(0.08f, 0.1f), At(0.12f, 0.24f, 0.05f), petals),
            (Ball(0.08f, 0.1f), At(-0.1f, 0.3f, 0.1f), petals),
            (Ball(0.08f, 0.1f), At(0.02f, 0.2f, -0.14f), petals));
        var reed = Leaves(PlantKind.Reeds);
        _meshes[(int)PlantKind.Reeds] = Compose(
            (Rod(0.03f, 0.02f, 1.1f, 4), At(0, 0.55f, 0, 0.08f), reed),
            (Rod(0.03f, 0.02f, 1.0f, 4), At(0.15f, 0.5f, 0.1f, -0.12f, 0.1f), reed),
            (Rod(0.03f, 0.02f, 0.9f, 4), At(-0.12f, 0.45f, -0.08f, 0.15f, -0.1f), reed),
            (Rod(0.03f, 0.02f, 1.2f, 4), At(0.05f, 0.6f, -0.18f, -0.05f, 0.12f), reed));

        // Crops. Wheat: a clump of stalks with ears, coloured per instance (green, ripening to gold).
        var stalk = Material(new Color(1f, 1f, 1f), 1f);
        var wheat = new List<(PrimitiveMesh, Transform3D, Material)>();
        for (int i = 0; i < 7; i++)
        {
            float a = i * 2.4f, r = 0.08f + 0.22f * (i % 3) / 2f, tz = Mathf.Cos(a) * 0.12f, tx = Mathf.Sin(a) * 0.12f;
            wheat.Add((Rod(0.015f, 0.012f, 0.8f, 4), At(Mathf.Cos(a) * r, 0.4f, Mathf.Sin(a) * r, tz, tx), stalk));
            wheat.Add((Rod(0.035f, 0.01f, 0.2f, 5), At(Mathf.Cos(a) * r + tz * 0.4f, 0.88f, Mathf.Sin(a) * r - tx * 0.4f, tz, tx), stalk));
        }
        _meshes[(int)PlantKind.Wheat] = Compose(wheat.ToArray());
        // Herb garden: a leafy mound with sprigs and small violet flowers.
        var herb = Leaves(PlantKind.Herbs);
        var bloom = Material(new Color(0.62f, 0.45f, 0.9f), 0.1f);
        _meshes[(int)PlantKind.Herbs] = Compose(
            (Ball(0.35f, 0.3f), At(0, 0.13f, 0), herb),
            (Rod(0.02f, 0.01f, 0.4f, 4), At(0.1f, 0.35f, 0.05f, 0.2f), herb),
            (Rod(0.02f, 0.01f, 0.35f, 4), At(-0.12f, 0.32f, -0.06f, -0.25f, 0.1f), herb),
            (Ball(0.05f, 0.06f), At(0.14f, 0.55f, 0.05f), bloom),
            (Ball(0.05f, 0.06f), At(-0.16f, 0.5f, -0.06f), bloom),
            (Ball(0.05f, 0.06f), At(0.02f, 0.3f, 0.25f), bloom));
        // Glowcaps: a cluster of pale stems under softly glowing caps.
        var stem2 = Material(new Color(0.85f, 0.85f, 0.78f), 0.1f);
        var cap = Material(CanopyColor(PlantKind.Glowcap), 0.2f, emission: 0.9f);
        var caps = new List<(PrimitiveMesh, Transform3D, Material)>();
        foreach (var (x, z, h) in new[] { (0f, 0f, 0.4f), (0.18f, 0.1f, 0.28f), (-0.15f, 0.12f, 0.24f), (0.05f, -0.18f, 0.32f), (-0.12f, -0.12f, 0.18f) })
        {
            caps.Add((Rod(0.035f, 0.03f, h, 5), At(x, h / 2, z), stem2));
            caps.Add((Ball(0.1f + h * 0.2f, 0.09f), At(x, h, z), cap));
        }
        _meshes[(int)PlantKind.Glowcap] = Compose(caps.ToArray());

        // Far LOD: a two-cone spindle on a short stalk, coloured per instance.
        var marker = Material(new Color(1f, 1f, 1f), 1f, glyph: DwarfRender.GlyphAtlas.Oak); // distant trees still count as trees
        _farMesh = Compose(
            (Rod(2.1f, 0f, 3.6f, 5), At(0, 3.8f, 0), marker),
            (Rod(0f, 2.1f, 1.4f, 5), At(0, 1.3f, 0), marker));
        if (Main.StyledRender)
        {
            _treeRange = StyledTreeRange;
            KitTrees();
        }
    }

    /// <summary>
    /// The Dwarf Render look: trees and bushes from the kit's TreeBuilder (dithered canopy shells, bark with branches),
    /// canopy and bark as two surfaces of one mesh so the MultiMesh instancing stays as it is. Sliced like the rest.
    /// </summary>
    private void KitTrees()
    {
        var canopy = DwarfRender.DwarfMaterials.Foliage;
        var bark = DwarfRender.DwarfMaterials.CreateObject(Colors.White, DwarfRender.GlyphAtlas.Trunk, null, useVertexColor: true);
        _materials.Add(canopy);
        _materials.Add(bark);
        Mesh Tree(DwarfRender.TreeSpecies species, float scale, int seed) =>
            DwarfRender.TreeBuilder.Build(species, scale, seed, into: new ArrayMesh(), canopyMaterial: canopy, barkMaterial: bark,
                leafCards: _sim.World != null && DwarfRender.DwarfRenderer.Instance?.Settings.FoliageTechnique == DwarfRender.FoliageMode.LeafCards).canopy;
        _meshes[(int)PlantKind.Oak] = Tree(DwarfRender.TreeSpecies.Oak, 0.9f, 1);
        _meshes[(int)PlantKind.Willow] = Tree(DwarfRender.TreeSpecies.Oak, 1.05f, 7);
        _meshes[(int)PlantKind.Birch] = Tree(DwarfRender.TreeSpecies.Birch, 0.85f, 2);
        _meshes[(int)PlantKind.Pine] = Tree(DwarfRender.TreeSpecies.Pine, 0.85f, 3);
        _meshes[(int)PlantKind.Spruce] = Tree(DwarfRender.TreeSpecies.Pine, 0.75f, 5);
        _meshes[(int)PlantKind.Shrub] = Tree(DwarfRender.TreeSpecies.Bush, 0.55f, 4);
        _meshes[(int)PlantKind.BerryBush] = Tree(DwarfRender.TreeSpecies.Bush, 0.6f, 6);
    }
}
