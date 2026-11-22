using System.Collections.Generic;
using System.Linq;
using Colony.Core;
using Colony.Core.Items;
using Colony.Core.World;
using Godot;
using static ColonyGame.MeshKit;

namespace ColonyGame;

/// <summary>
/// Draws item piles (one MultiMesh per item type, its shape picked by the catalog's <c>model</c> key and tinted
/// by its <c>color</c>; a heap grows taller as it fills, a pile of objects grows from a few to a full stack),
/// stockpile zones and farm plots (translucent floor plates). Piles rebuild when
/// <see cref="ItemPiles.LayoutVersion"/> changes, zones when their version or the view level changes. Piles use
/// the flora shader for the level cut.
/// </summary>
public partial class ItemView : Node3D
{
    public static readonly Color StockpileColor = new(0.35f, 0.75f, 1f, 0.22f);
    public static readonly Color FarmColor = new(0.55f, 0.4f, 0.2f, 0.45f);
    /// <summary>Trade depots: where other colonies' traders deliver.</summary>
    public static readonly Color DepotColor = new(1f, 0.78f, 0.25f, 0.3f);

    public static Color ItemColor(ItemType t) => Rgb(t.Def.Color);

    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private Shader _shader = null!;
    private MultiMeshInstance3D[] _piles = [];
    private bool[] _growsTall = [];
    private readonly List<ShaderMaterial> _materials = new();
    private MultiMeshInstance3D _zones = null!, _farms = null!, _depots = null!;
    private int _drawnLayout = -1, _drawnZones = -1, _drawnFarms = -1, _drawnLevel = int.MinValue;
    private Material _wood = null!, _dark = null!, _metal = null!, _cloth = null!, _glass = null!, _paper = null!;

    public void Initialize(ColonySim sim, WorldView world)
    {
        _sim = sim;
        _world = world;
        _shader = GD.Load<Shader>("res://shaders/flora.gdshader");
        _wood = Mat(new Color(0.6f, 0.43f, 0.27f));
        _dark = Mat(new Color(0.3f, 0.24f, 0.18f));
        _metal = Mat(new Color(0.62f, 0.64f, 0.68f));
        _cloth = Mat(new Color(0.82f, 0.76f, 0.62f));
        _glass = Mat(new Color(0.75f, 0.9f, 0.95f));
        _paper = Mat(new Color(0.94f, 0.91f, 0.84f));

        int n = ItemInfo.TypeCount;
        _piles = new MultiMeshInstance3D[n];
        _growsTall = new bool[n];
        for (int t = 0; t < n; t++)
        {
            var type = new ItemType((ushort)t);
            var model = type.Def.Model;
            _growsTall[t] = IsHeap(model);
            _piles[t] = new MultiMeshInstance3D
            {
                Multimesh = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    UseCustomData = true,
                    Mesh = PileMesh(model, Mat(ItemColor(type), type == ItemType.Aetherite || model is "orb" or "cells" ? 0.6f : 0f)),
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_piles[t]);
        }


        _zones = Plates(StockpileColor);
        _farms = Plates(FarmColor);
        _depots = Plates(DepotColor);
    }

    private MultiMeshInstance3D Plates(Color color)
    {
        var mmi = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new PlaneMesh { Size = new Vector2(0.96f, 0.96f), Material = Overlay.Material(color) },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(mmi);
        return mmi;
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
            foreach (var m in _materials) m.SetShaderParameter("cut_height", (float)(level + 1));

        if (_sim.Items.LayoutVersion != _drawnLayout || _sim.Stockpiles.RackVersion != _drawnRacks) RedrawPiles();
        if (_sim.Stockpiles.Version != _drawnZones || level != _drawnLevel)
        {
            _drawnZones = _sim.Stockpiles.Version;
            RedrawPlates(_zones, _sim.Stockpiles.Cells.Where(c => !_sim.Stockpiles.IsDepot(c)), level);
            RedrawPlates(_depots, _sim.Stockpiles.DepotCells, level);
        }
        if (_sim.Farms.Version != _drawnFarms || level != _drawnLevel)
        {
            _drawnFarms = _sim.Farms.Version;
            RedrawPlates(_farms, _sim.Farms.Cells, level);
        }
        _drawnLevel = level;
    }

    private int _drawnRacks = -1;

    private void RedrawPiles()
    {
        _drawnLayout = _sim.Items.LayoutVersion;
        _drawnRacks = _sim.Stockpiles.RackVersion;
        // One instance per pile, or on a storage rack one per stack it holds, shelf by shelf from the floor up.
        var byType = new List<(Vector3 At, float Fill, float Shrink, float Turn)>[ItemInfo.TypeCount];
        for (int t = 0; t < byType.Length; t++) byType[t] = new List<(Vector3, float, float, float)>();
        foreach (var (p, pile) in _sim.Items.All)
        {
            if (pile.Count <= 0) continue;
            int limit = ItemInfo.StackLimit(pile.Type);
            var foot = new Vector3(p.X + 0.5f, p.Z, p.Y + 0.5f);
            float turn = (p.X * 7 + p.Y * 13) % 4 * Mathf.Pi / 2; // neighbouring piles don't look stamped
            if (_sim.Stockpiles.Racks.TryGetValue(p, out var rack))
            {
                int shelves = Stockpiles.Capacity(rack.Levels);
                for (int s = 0, left = pile.Count; s < shelves && left > 0; s++, left -= limit)
                    byType[pile.Type.Index].Add((foot + new Vector3(0, s * RackView.ShelfStep + (s > 0 ? RackView.BoardThickness : 0), 0), Mathf.Min(1f, left / (float)limit), 0.62f, rack.Facing * Mathf.Pi / 2));
            }
            else byType[pile.Type.Index].Add((foot, Mathf.Clamp(pile.Count / (float)limit, 0.05f, 1.5f), 1f, turn));
        }

        for (int t = 0; t < byType.Length; t++)
        {
            var list = byType[t];
            var mm = _piles[t].Multimesh;
            mm.InstanceCount = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                var (at, fill, shrink, turn) = list[i];
                // Heaps rise as they fill; piles of objects scale as a whole (they'd look squashed otherwise).
                var scale = (_growsTall[t] ? new Vector3(1, 0.25f + 0.75f * fill, 1) : Vector3.One * (0.6f + 0.4f * Mathf.Min(fill, 1f))) * shrink;
                mm.SetInstanceTransform(i, new Transform3D(new Basis(Vector3.Up, turn).Scaled(scale), at));
                mm.SetInstanceCustomData(i, new Color(1, 1, 1));
            }
        }
    }

    private static void RedrawPlates(MultiMeshInstance3D mmi, IEnumerable<TilePos> cells, int level)
    {
        var visible = new List<TilePos>();
        foreach (var c in cells)
            if (c.Z <= level + 1) visible.Add(c);
        var mm = mmi.Multimesh;
        mm.InstanceCount = visible.Count;
        for (int i = 0; i < visible.Count; i++)
        {
            var c = visible[i];
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(c.X + 0.5f, c.Z + 0.02f, c.Y + 0.5f)));
        }
    }

    private static bool IsHeap(string model) => model is "heap" or "lumps" or "ore" or "sacks" or "blocks" or "planks"
        or "paper" or "ingots" or "plates" or "bolts" or "folded" or "books" or "crates" or "panes";

    // ---- Pile shapes ---------------------------------------------------------------------------------------
    // Unit footprint (about 0.8 x 0.8), standing on y = 0, about 0.7 tall when full. `m` is the item's colour.

    private Mesh PileMesh(string model, Material m)
    {
        var p = new Parts();
        switch (model)
        {
            case "logs":
                for (int i = 0; i < 3; i++) p.Add(Cyl(0.11f, 0.8f, 6), AlongX(0, 0.11f, -0.24f + i * 0.24f), m);
                for (int i = 0; i < 2; i++) p.Add(Cyl(0.11f, 0.8f, 6), AlongX(0, 0.31f, -0.12f + i * 0.24f), m);
                p.Add(Cyl(0.11f, 0.8f, 6), AlongX(0, 0.51f, 0), m);
                break;
            case "planks":
                for (int layer = 0; layer < 6; layer++)
                for (int i = 0; i < 3; i++)
                {
                    float y = 0.035f + layer * 0.08f, o = -0.22f + i * 0.22f;
                    p.Add(Box(0.8f, 0.07f, 0.16f), layer % 2 == 0 ? At(0, y, o) : At(o, y, 0, 0, Mathf.Pi / 2), m);
                }
                break;
            case "heap":
                p.Add(Box(0.72f, 0.4f, 0.72f), 0, 0.2f, 0, m);
                p.Add(Box(0.48f, 0.25f, 0.5f), At(0.03f, 0.52f, -0.02f, 0, 0.4f), m);
                break;
            case "lumps":
                foreach (var (x, z, r) in new[] { (-0.2f, -0.2f, 0.2f), (0.2f, -0.15f, 0.22f), (-0.15f, 0.2f, 0.21f), (0.18f, 0.2f, 0.19f), (0f, 0f, 0.24f) })
                    p.Add(Ball(r, r * 1.6f, 7), x, r * 0.8f, z, m);
                p.Add(Ball(0.2f, 0.32f, 7), 0.02f, 0.42f, 0.03f, m);
                break;
            case "ore":
                p.Add(Box(0.7f, 0.35f, 0.7f), 0, 0.17f, 0, _dark);
                foreach (var (x, z, s) in new[] { (-0.18f, -0.15f, 0.28f), (0.2f, 0.12f, 0.3f), (-0.1f, 0.2f, 0.24f), (0.15f, -0.2f, 0.22f) })
                    p.Add(Box(s, s * 0.8f, s), At(x, 0.38f + s * 0.2f, z, 0.3f, x * 3, 0.2f), m);
                break;
            case "crystals":
                p.Add(Box(0.6f, 0.12f, 0.6f), 0, 0.06f, 0, _dark);
                foreach (var (x, z, h, tilt) in new[] { (0f, 0f, 0.6f, 0f), (0.18f, 0.1f, 0.42f, 0.35f), (-0.16f, 0.12f, 0.38f, -0.4f), (0.05f, -0.18f, 0.45f, 0.3f), (-0.15f, -0.12f, 0.3f, -0.25f) })
                    p.Add(Cone(0.08f, 0f, h, 6), At(x, 0.1f + h / 2, z, tilt, 0, tilt * 0.6f), m);
                break;
            case "sacks":
                foreach (var (x, z) in new[] { (-0.2f, -0.2f), (0.2f, -0.2f), (-0.2f, 0.2f), (0.2f, 0.2f) })
                {
                    p.Add(Ball(0.2f, 0.42f, 8), x, 0.21f, z, m);
                    p.Add(Cyl(0.05f, 0.08f, 6), x, 0.44f, z, _cloth);
                }
                p.Add(Ball(0.2f, 0.42f, 8), 0, 0.55f, 0, m);
                break;
            case "bundle":
                foreach (var (x, z) in new[] { (-0.18f, 0f), (0.18f, 0f), (0f, 0.2f), (0f, -0.2f) })
                {
                    p.Add(Cyl(0.13f, 0.6f, 8), AlongX(0, 0.13f, z + x * 0.1f), m);
                    p.Add(Cyl(0.14f, 0.05f, 8), AlongX(0.15f, 0.13f, z + x * 0.1f), _dark);
                }
                p.Add(Cyl(0.13f, 0.6f, 8), AlongX(0, 0.36f, 0), m);
                break;
            case "basket":
                p.Add(Cone(0.28f, 0.34f, 0.32f, 10), 0, 0.16f, 0, _wood);
                p.Add(Ball(0.3f, 0.2f, 10), 0, 0.32f, 0, m);
                for (int i = 0; i < 5; i++) p.Add(Ball(0.07f), -0.15f + i * 0.08f, 0.4f, (i % 2 - 0.5f) * 0.12f, m);
                break;
            case "sheaves":
                foreach (var (x, z) in new[] { (-0.2f, -0.18f), (0.2f, -0.18f), (0f, 0.2f) })
                {
                    p.Add(Cone(0.08f, 0.14f, 0.6f, 7), x, 0.3f, z, m);
                    p.Add(Cyl(0.1f, 0.05f, 7), x, 0.28f, z, _dark);
                }
                break;
            case "blocks":
                for (int layer = 0; layer < 3; layer++)
                for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    p.Add(Box(0.36f, 0.2f, 0.36f), -0.19f + i * 0.38f, 0.1f + layer * 0.22f, -0.19f + j * 0.38f, m);
                break;
            case "coils":
                for (int i = 0; i < 3; i++) p.Add(Ring(0.12f, 0.3f), 0, 0.07f + i * 0.13f, 0, m);
                break;
            case "panes":
                for (int i = 0; i < 6; i++) p.Add(Box(0.7f, 0.6f, 0.03f), At(0, 0.32f, -0.2f + i * 0.08f, 0.15f, 0, 0), m);
                p.Add(Box(0.8f, 0.06f, 0.6f), 0, 0.03f, 0, _wood);
                break;
            case "paper":
                for (int i = 0; i < 2; i++) p.Add(Box(0.62f, 0.3f, 0.45f), At(0, 0.15f + i * 0.31f, 0, 0, i * 0.2f, 0), m);
                p.Add(Box(0.66f, 0.03f, 0.05f), 0, 0.3f, 0, _dark);
                break;
            case "jars":
                foreach (var (x, z) in new[] { (-0.2f, -0.2f), (0.2f, -0.2f), (-0.2f, 0.2f), (0.2f, 0.2f), (0f, 0f) })
                {
                    p.Add(Cyl(0.12f, 0.24f, 8), x, 0.12f, z, m);
                    p.Add(Cyl(0.08f, 0.06f, 8), x, 0.27f, z, _dark);
                }
                break;
            case "spools":
                foreach (var (x, z) in new[] { (-0.2f, -0.2f), (0.2f, -0.2f), (-0.2f, 0.2f), (0.2f, 0.2f) })
                {
                    p.Add(Cyl(0.12f, 0.26f, 8), x, 0.15f, z, m);
                    p.Add(Cyl(0.15f, 0.03f, 8), x, 0.015f, z, _wood);
                    p.Add(Cyl(0.15f, 0.03f, 8), x, 0.29f, z, _wood);
                }
                break;
            case "bolts":
                for (int i = 0; i < 3; i++) p.Add(Cyl(0.12f, 0.75f, 8), AlongX(0, 0.12f, -0.25f + i * 0.25f), m);
                for (int i = 0; i < 2; i++) p.Add(Cyl(0.12f, 0.75f, 8), AlongX(0, 0.34f, -0.12f + i * 0.25f), m);
                break;
            case "folded":
                for (int i = 0; i < 4; i++) p.Add(Box(0.6f, 0.1f, 0.5f), At(0, 0.05f + i * 0.11f, 0, 0, i * 0.15f, 0), m);
                break;
            case "loaves":
                foreach (var (x, z) in new[] { (-0.18f, -0.18f), (0.18f, -0.18f), (-0.18f, 0.18f), (0.18f, 0.18f) })
                    p.Add(Ball(0.16f, 0.2f, 8), At(x, 0.1f, z, 0, x * 4, 0), m);
                p.Add(Ball(0.16f, 0.2f, 8), 0, 0.27f, 0, m);
                break;
            case "barrels":
                foreach (var x in new[] { -0.2f, 0.2f })
                {
                    p.Add(Cyl(0.2f, 0.55f, 10), x, 0.28f, 0, _wood);
                    p.Add(Cyl(0.205f, 0.04f, 10), x, 0.12f, 0, _metal);
                    p.Add(Cyl(0.205f, 0.04f, 10), x, 0.44f, 0, _metal);
                    p.Add(Cyl(0.16f, 0.02f, 10), x, 0.56f, 0, m);
                }
                break;
            case "ingots":
                for (int layer = 0; layer < 4; layer++)
                for (int i = 0; i < 3; i++)
                {
                    float y = 0.05f + layer * 0.1f, o = -0.22f + i * 0.22f;
                    p.Add(Box(0.5f, 0.09f, 0.16f), layer % 2 == 0 ? At(0, y, o) : At(o, y, 0, 0, Mathf.Pi / 2), m);
                }
                break;
            case "plates":
                for (int i = 0; i < 8; i++) p.Add(Box(0.72f, 0.04f, 0.72f), At(0, 0.02f + i * 0.06f, 0, 0, i * 0.08f, 0), m);
                break;
            case "crates":
                p.Add(Box(0.7f, 0.42f, 0.7f), 0, 0.21f, 0, _wood);
                p.Add(Box(0.72f, 0.05f, 0.72f), 0, 0.42f, 0, _dark);
                p.Add(Box(0.3f, 0.12f, 0.3f), 0.12f, 0.5f, 0.1f, m);
                p.Add(Box(0.2f, 0.16f, 0.25f), -0.15f, 0.52f, -0.1f, m);
                break;
            case "gears":
                foreach (var (x, y, z, r) in new[] { (-0.15f, 0.04f, -0.12f, 0.2f), (0.18f, 0.04f, 0.1f, 0.16f), (0f, 0.12f, 0f, 0.22f), (-0.1f, 0.2f, 0.15f, 0.12f) })
                {
                    p.Add(Cyl(r, 0.06f, 12), x, y, z, m);
                    for (int k = 0; k < 6; k++)
                        p.Add(Box(0.06f, 0.06f, 0.06f), x + Mathf.Cos(k * Mathf.Tau / 6) * (r + 0.02f), y, z + Mathf.Sin(k * Mathf.Tau / 6) * (r + 0.02f), m);
                }
                break;
            case "springs":
                foreach (var (x, z) in new[] { (-0.18f, -0.18f), (0.18f, -0.18f), (-0.18f, 0.18f), (0.18f, 0.18f) })
                    for (int k = 0; k < 5; k++) p.Add(Ring(0.06f, 0.1f, 10), x, 0.03f + k * 0.06f, z, m);
                break;
            case "lenses":
                p.Add(Box(0.6f, 0.08f, 0.6f), 0, 0.04f, 0, _wood);
                foreach (var (x, z) in new[] { (-0.15f, -0.15f), (0.15f, -0.15f), (-0.15f, 0.15f), (0.15f, 0.15f) })
                    p.Add(Ball(0.11f, 0.05f, 10), x, 0.11f, z, m);
                break;
            case "pots":
                foreach (var (x, z, h) in new[] { (-0.2f, -0.15f, 0.36f), (0.2f, -0.1f, 0.28f), (0f, 0.2f, 0.4f) })
                {
                    p.Add(Ball(0.15f, h, 10), x, h / 2, z, m);
                    p.Add(Cyl(0.07f, 0.08f, 8), x, h + 0.02f, z, m);
                }
                break;
            case "bed": // a frame with a mattress and pillow
                p.Add(Box(0.5f, 0.14f, 0.78f), 0, 0.07f, 0, _wood);
                p.Add(Box(0.46f, 0.1f, 0.72f), 0, 0.19f, 0, m);
                p.Add(Box(0.3f, 0.06f, 0.14f), 0, 0.27f, -0.27f, _paper);
                break;
            case "lamp": // two lanterns
                foreach (var x in new[] { -0.16f, 0.16f })
                {
                    p.Add(Cyl(0.09f, 0.04f, 8), x, 0.02f, 0, m);
                            p.Add(Cyl(0.07f, 0.2f, 8), x, 0.14f, 0, _glass);
                    p.Add(Cone(0.09f, 0.02f, 0.08f, 8), x, 0.28f, 0, m);
                }
                break;
            case "rug": // rolled up
                for (int i = 0; i < 3; i++) p.Add(Cyl(0.08f, 0.7f, 8), AlongX(0, 0.08f + (i == 2 ? 0.14f : 0), -0.09f + (i % 2) * 0.18f + (i == 2 ? 0.09f : 0)), m);
                break;
            case "tableware": // plates and cups
                for (int i = 0; i < 5; i++) p.Add(Cyl(0.16f, 0.025f, 12), -0.15f, 0.015f + i * 0.03f, 0, m);
                foreach (var (x, z) in new[] { (0.18f, -0.15f), (0.2f, 0.15f) }) p.Add(Cyl(0.06f, 0.12f, 8), x, 0.06f, z, m);
                break;
            case "chairs":
                p.Add(Box(0.4f, 0.05f, 0.4f), 0, 0.42f, 0, m);
                p.Add(Box(0.4f, 0.45f, 0.05f), 0, 0.66f, 0.18f, m);
                foreach (var (x, z) in new[] { (-0.17f, -0.17f), (0.17f, -0.17f), (-0.17f, 0.17f), (0.17f, 0.17f) })
                    p.Add(Box(0.05f, 0.42f, 0.05f), x, 0.21f, z, m);
                break;
            case "books":
                for (int i = 0; i < 6; i++)
                    p.Add(Box(0.42f, 0.08f, 0.3f), At((i % 2) * 0.05f - 0.02f, 0.04f + i * 0.085f, 0, 0, i * 0.25f, 0), m);
                for (int i = 0; i < 6; i++) p.Add(Box(0.4f, 0.06f, 0.29f), At((i % 2) * 0.05f - 0.0f, 0.04f + i * 0.085f, 0.008f, 0, i * 0.25f, 0), _paper);
                break;
            case "bottles":
                foreach (var (x, z) in new[] { (-0.2f, -0.18f), (0.2f, -0.18f), (-0.2f, 0.18f), (0.2f, 0.18f), (0f, 0f) })
                {
                    p.Add(Ball(0.11f, 0.22f, 8), x, 0.11f, z, m);
                    p.Add(Cyl(0.035f, 0.14f, 6), x, 0.28f, z, _glass);
                }
                break;
            case "runes":
                foreach (var (x, z, h) in new[] { (-0.2f, -0.15f, 0.4f), (0.2f, -0.1f, 0.5f), (0f, 0.2f, 0.35f) })
                {
                    p.Add(Box(0.22f, h, 0.12f), At(x, h / 2, z, 0, x * 2, 0), m);
                    p.Add(Box(0.12f, 0.12f, 0.13f), At(x, h * 0.6f, z, 0, x * 2, 0), _glass);
                }
                break;
            case "instruments":
                p.Add(Box(0.7f, 0.06f, 0.5f), 0, 0.03f, 0, _wood);
                p.Add(Cyl(0.06f, 0.6f, 8), At(0, 0.25f, 0, 0, 0, 0.9f), m); // a telescope
                p.Add(Ring(0.12f, 0.16f), At(-0.2f, 0.25f, 0.1f, Mathf.Pi / 2), m); // an astrolabe
                p.Add(Ball(0.06f), 0.2f, 0.12f, -0.12f, _glass);
                break;
            case "cells":
                foreach (var (x, z) in new[] { (-0.18f, -0.16f), (0.18f, -0.16f), (0f, 0.18f) })
                {
                    p.Add(Cyl(0.13f, 0.34f, 10), x, 0.2f, z, m);
                    p.Add(Cyl(0.15f, 0.05f, 10), x, 0.03f, z, _metal);
                    p.Add(Cyl(0.15f, 0.05f, 10), x, 0.38f, z, _metal);
                }
                break;
            case "orb":
                p.Add(Cone(0.22f, 0.12f, 0.2f, 10), 0, 0.1f, 0, _metal);
                p.Add(Ball(0.22f), 0, 0.42f, 0, m);
                break;
            case "frames":
                for (int i = 0; i < 3; i++)
                {
                    p.Add(Box(0.6f, 0.5f, 0.05f), At(0, 0.3f, -0.15f + i * 0.12f, 0.1f, 0, 0), _wood);
                    p.Add(Box(0.5f, 0.4f, 0.06f), At(0, 0.3f, -0.15f + i * 0.12f, 0.1f, 0, 0), m);
                }
                break;
            case "statue":
                p.Add(Box(0.45f, 0.12f, 0.45f), 0, 0.06f, 0, _dark);
                p.Add(Cone(0.14f, 0.12f, 0.45f, 8), 0, 0.35f, 0, m);
                p.Add(Ball(0.1f), 0, 0.66f, 0, m);
                p.Add(Box(0.42f, 0.07f, 0.07f), 0, 0.52f, 0, m);
                break;
            case "weapons":
                p.Add(Box(0.7f, 0.08f, 0.5f), 0, 0.04f, 0, _wood);
                foreach (var z in new[] { -0.15f, 0f, 0.15f })
                {
                    p.Add(Box(0.6f, 0.03f, 0.06f), 0.05f, 0.1f + z * 0.1f + 0.05f, z, m);
                    p.Add(Box(0.04f, 0.05f, 0.16f), -0.25f, 0.13f, z, _dark);
                }
                break;
            case "armor":
                p.Add(Cyl(0.03f, 0.55f, 6), 0, 0.28f, 0, _wood);
                p.Add(Cone(0.16f, 0.22f, 0.36f, 8), 0, 0.5f, 0, m);
                p.Add(Ball(0.12f), 0, 0.78f, 0, m);
                p.Add(Ball(0.08f), -0.22f, 0.64f, 0, m);
                p.Add(Ball(0.08f), 0.22f, 0.64f, 0, m);
                break;
            case "orrery":
                p.Add(Cone(0.2f, 0.06f, 0.25f, 10), 0, 0.12f, 0, _wood);
                p.Add(Ball(0.1f), 0, 0.45f, 0, m);
                p.Add(Ring(0.24f, 0.26f, 20), At(0, 0.45f, 0, 0.3f), m);
                p.Add(Ring(0.17f, 0.19f, 20), At(0, 0.45f, 0, -0.4f, 0.5f), m);
                p.Add(Ball(0.04f), 0.25f, 0.5f, 0, _metal);
                break;
            case "scaffold": // folded down: two ladder-like side frames and the deck boards, lashed together
                foreach (float z in new[] { -0.22f, 0.22f })
                {
                    p.Add(Cyl(0.03f, 0.82f, 6), AlongX(0, 0.05f, z - 0.06f), m);
                    p.Add(Cyl(0.03f, 0.82f, 6), AlongX(0, 0.05f, z + 0.06f), m);
                    for (int i = 0; i < 4; i++) p.Add(Box(0.03f, 0.03f, 0.14f), -0.3f + i * 0.2f, 0.05f, z, m);
                }
                for (int i = 0; i < 3; i++) p.Add(Box(0.8f, 0.04f, 0.16f), 0, 0.12f + i * 0.045f, -0.16f + i * 0.16f, _wood);
                p.Add(Cyl(0.035f, 0.08f, 8), AlongZ(-0.2f, 0.12f, 0), _cloth); // the lashing
                p.Add(Cyl(0.035f, 0.08f, 8), AlongZ(0.2f, 0.12f, 0), _cloth);
                break;
            default:
                p.Add(Box(0.6f, 0.4f, 0.6f), 0, 0.2f, 0, m);
                break;
        }
        return Compose(p);
    }
}
