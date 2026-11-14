using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Items;
using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>
/// Draws item piles (one MultiMesh per item type; a pile's height shows how full it is) and stockpile zones
/// (translucent floor plates). Piles rebuild when <see cref="ItemPiles.LayoutVersion"/> changes, zones when
/// <see cref="Stockpiles.Version"/> or the view level changes. Piles use the flora shader for the level cut.
/// </summary>
public partial class ItemView : Node3D
{
    public static readonly Color StockpileColor = new(0.35f, 0.75f, 1f, 0.22f);

    public static Color ItemColor(ItemType t) => t switch
    {
        ItemType.Stone => new Color(0.62f, 0.62f, 0.64f),
        ItemType.Ore => new Color(0.78f, 0.48f, 0.22f),
        ItemType.Log => new Color(0.55f, 0.38f, 0.22f),
        ItemType.Plank => new Color(0.86f, 0.7f, 0.46f),
        ItemType.Berries => new Color(0.75f, 0.12f, 0.3f),
        ItemType.Fiber => new Color(0.78f, 0.74f, 0.42f),
        _ => Colors.Magenta,
    };

    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private readonly MultiMeshInstance3D[] _piles = new MultiMeshInstance3D[ItemInfo.TypeCount];
    private readonly List<ShaderMaterial> _materials = new();
    private MultiMeshInstance3D _zones = null!;
    private int _drawnLayout = -1, _drawnZones = -1, _drawnLevel = int.MinValue;

    public void Initialize(ColonySim sim, WorldView world)
    {
        _sim = sim;
        _world = world;
        var shader = GD.Load<Shader>("res://shaders/flora.gdshader");

        for (int t = 0; t < ItemInfo.TypeCount; t++)
        {
            var type = (ItemType)t;
            var mat = new ShaderMaterial { Shader = shader };
            mat.SetShaderParameter("albedo", ItemColor(type));
            mat.SetShaderParameter("tint_mix", 0.0f);
            _materials.Add(mat);
            _piles[t] = new MultiMeshInstance3D
            {
                Multimesh = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    UseCustomData = true,
                    Mesh = PileMesh(type, mat),
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_piles[t]);
        }

        _zones = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new PlaneMesh { Size = new Vector2(0.96f, 0.96f), Material = Overlay.Material(StockpileColor) },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_zones);
    }

    public override void _Process(double delta)
    {
        if (_sim == null) return;
        int level = _world.ViewLevel;
        if (level != _drawnLevel)
            foreach (var m in _materials) m.SetShaderParameter("cut_height", (float)(level + 1));

        if (_sim.Items.LayoutVersion != _drawnLayout) RedrawPiles();
        if (_sim.Stockpiles.Version != _drawnZones || level != _drawnLevel) RedrawZones(level);
        _drawnLevel = level;
    }

    private void RedrawPiles()
    {
        _drawnLayout = _sim.Items.LayoutVersion;
        var byType = new List<(TilePos Pos, ItemPile Pile)>[ItemInfo.TypeCount];
        for (int t = 0; t < byType.Length; t++) byType[t] = new List<(TilePos, ItemPile)>();
        foreach (var (p, pile) in _sim.Items.All)
            if (pile.Count > 0) byType[(int)pile.Type].Add((p, pile));

        for (int t = 0; t < byType.Length; t++)
        {
            var list = byType[t];
            var mm = _piles[t].Multimesh;
            mm.InstanceCount = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                var (p, pile) = list[i];
                float fill = Mathf.Clamp(pile.Count / (float)ItemInfo.StackLimit(pile.Type), 0.05f, 1.5f);
                var basis = Basis.Identity.Scaled(new Vector3(1, 0.25f + 0.75f * fill, 1));
                mm.SetInstanceTransform(i, new Transform3D(basis, new Vector3(p.X + 0.5f, p.Z, p.Y + 0.5f)));
                mm.SetInstanceCustomData(i, new Color(1, 1, 1));
            }
        }
    }

    private void RedrawZones(int level)
    {
        _drawnZones = _sim.Stockpiles.Version;
        var visible = new List<TilePos>();
        foreach (var c in _sim.Stockpiles.Cells)
            if (c.Z <= level + 1) visible.Add(c);
        var mm = _zones.Multimesh;
        mm.InstanceCount = visible.Count;
        for (int i = 0; i < visible.Count; i++)
        {
            var c = visible[i];
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(c.X + 0.5f, c.Z + 0.02f, c.Y + 0.5f)));
        }
    }

    /// <summary>Unit-height pile shapes (scaled vertically by fill): stacked blocks, or a bundle of logs.</summary>
    private static Mesh PileMesh(ItemType type, Material mat)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        if (type == ItemType.Log)
        {
            var log = new CylinderMesh { TopRadius = 0.11f, BottomRadius = 0.11f, Height = 0.8f, RadialSegments = 6, Rings = 1 };
            var lying = new Basis(Vector3.Forward, Mathf.Pi / 2);
            for (int i = 0; i < 3; i++) st.AppendFrom(log, 0, new Transform3D(lying, new Vector3(0, 0.11f, -0.24f + i * 0.24f)));
            for (int i = 0; i < 2; i++) st.AppendFrom(log, 0, new Transform3D(lying, new Vector3(0, 0.31f, -0.12f + i * 0.24f)));
            st.AppendFrom(log, 0, new Transform3D(lying, new Vector3(0, 0.51f, 0)));
        }
        else if (type == ItemType.Plank)
        {
            // A neat stack of boards, alternating direction.
            var board = new BoxMesh { Size = new Vector3(0.8f, 0.07f, 0.16f) };
            for (int layer = 0; layer < 6; layer++)
            for (int i = 0; i < 3; i++)
            {
                var basis = layer % 2 == 0 ? Basis.Identity : new Basis(Vector3.Up, Mathf.Pi / 2);
                var offset = layer % 2 == 0 ? new Vector3(0, 0, -0.22f + i * 0.22f) : new Vector3(-0.22f + i * 0.22f, 0, 0);
                st.AppendFrom(board, 0, new Transform3D(basis, offset + new Vector3(0, 0.035f + layer * 0.08f, 0)));
            }
        }
        else
        {
            // A rough heap: a wide base block and a smaller top block.
            st.AppendFrom(new BoxMesh { Size = new Vector3(0.72f, 0.4f, 0.72f) }, 0, new Transform3D(Basis.Identity, new Vector3(0, 0.2f, 0)));
            st.AppendFrom(new BoxMesh { Size = new Vector3(0.48f, 0.25f, 0.5f) }, 0,
                new Transform3D(new Basis(Vector3.Up, 0.4f), new Vector3(0.03f, 0.52f, -0.02f)));
        }
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, mat);
        return mesh;
    }
}
