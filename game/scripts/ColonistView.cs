using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Agents;
using Colony.Core.Items;
using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>
/// Draws each colonist as a capsule with a name tag, interpolating between simulation steps: its load on its head, its
/// weapon, shield and body armour, a health bar when hurt, a red marker when drafted; struck down, it lies on the ground.
/// </summary>
public partial class ColonistView : Node3D
{
    private const float BodyHeight = 0.8f;

    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private readonly List<View> _views = new();
    private float _time;
    private static readonly Shader BarShader = GD.Load<Shader>("res://shaders/health_bar.gdshader");
    private ShaderMaterial _barMat = null!;

    private sealed class View
    {
        public Colonist Colonist = null!;
        public Node3D Root = null!, Body = null!;
        public MeshInstance3D Load = null!, Weapon = null!, Shield = null!, Armour = null!, Bar = null!, Marker = null!;
        public ItemType? ShownWeapon, ShownShield, ShownArmour;
    }

    public void Initialize(ColonySim sim, WorldView world)
    {
        _sim = sim;
        _world = world;
        _barMat = new ShaderMaterial { Shader = BarShader };
        foreach (var c in sim.Colonists) AddView(c);
    }

    private void AddView(Colonist c)
    {
        var v = new View { Colonist = c };
        v.Root = new Node3D { Name = $"Colonist_{c.Id}_{c.Name}" };
        v.Body = new Node3D { Name = "Body" };
        v.Root.AddChild(v.Body);
        float hue = Colony.Core.Procedural.Noise.Hash(c.Id, 0, 0, 4242);
        v.Body.AddChild(new MeshInstance3D
        {
            Mesh = new CapsuleMesh
            {
                Radius = 0.22f,
                Height = BodyHeight,
                Material = new StandardMaterial3D { AlbedoColor = Color.FromHsv(hue, 0.65f, 0.9f), Roughness = 0.6f },
            },
            Position = new Vector3(0, BodyHeight / 2f, 0),
        });
        v.Load = Part(v.Body, MeshKit.Box(0.34f, 0.22f, 0.3f), new Vector3(0, BodyHeight + 0.08f, 0));
        // Gear: a plate over the chest, a weapon in the right hand, a shield on the left arm (hidden until worn).
        v.Armour = Part(v.Body, new CapsuleMesh { Radius = 0.245f, Height = 0.5f }, new Vector3(0, 0.42f, 0));
        v.Weapon = Part(v.Body, MeshKit.Box(0.06f, 0.62f, 0.06f), new Vector3(0.27f, 0.45f, 0.05f));
        v.Shield = Part(v.Body, MeshKit.Cyl(0.22f, 0.05f, 12), new Vector3(-0.27f, 0.42f, 0));
        v.Shield.Rotation = new Vector3(0, 0, Mathf.Pi / 2);
        if (_sim.Factions.Count > 1)
            v.Root.AddChild(new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = 0.26f, OuterRadius = 0.36f, Rings = 16, RingSegments = 6 },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = FactionColor(c.Faction), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
                Position = new Vector3(0, 0.04f, 0),
                Scale = new Vector3(1, 0.3f, 1),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        v.Root.AddChild(new Label3D
        {
            Text = c.Name,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 48,
            PixelSize = 0.009f,
            OutlineSize = 10,
            Position = new Vector3(0, BodyHeight + 0.3f, 0),
            NoDepthTest = true,
            Modulate = _sim.Factions.Count > 1 ? FactionColor(c.Faction).Lightened(0.35f) : Colors.White,
        });
        v.Bar = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(0.6f, 0.07f) },
            MaterialOverride = _barMat,
            Position = new Vector3(0, BodyHeight + 0.52f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        v.Root.AddChild(v.Bar);
        v.Marker = new MeshInstance3D
        {
            Mesh = MeshKit.Cone(0.0f, 0.11f, 0.18f, 4),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.2f, 0.15f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Position = new Vector3(0, BodyHeight + 0.7f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        v.Root.AddChild(v.Marker);
        if (Main.StyledRender) // zoomed far out (all symbols), colonists always show as ☺ in their colour
            v.Root.AddChild(new DwarfRender.AsciiMarker { GlyphGroup = DwarfRender.GlyphAtlas.Dwarf, GlyphColour = Color.FromHsv(hue, 0.65f, 0.9f), Position = new Vector3(0, 0.5f, 0) });
        AddChild(v.Root);
        _views.Add(v);
    }

    private static MeshInstance3D Part(Node3D parent, Mesh mesh, Vector3 at)
    {
        var m = new MeshInstance3D { Mesh = mesh, Position = at, Visible = false };
        parent.AddChild(m);
        return m;
    }

    /// <summary>A colony's colour (rings and name tags when several share the world).</summary>
    public static Color FactionColor(int faction) => faction switch
    {
        0 => new Color(1f, 0.78f, 0.3f),
        1 => new Color(0.35f, 0.65f, 1f),
        2 => new Color(0.5f, 0.95f, 0.45f),
        3 => new Color(1f, 0.4f, 0.55f),
        _ => Color.FromHsv(faction * 0.17f % 1f, 0.7f, 1f),
    };

    public override void _Process(double delta)
    {
        if (_sim == null) return;
        _time += (float)delta;

        SyncViews();
        foreach (var v in _views)
        {
            var c = v.Colonist;
            var pos = Foot(c.Position);
            if (c.NextStep is { } next) pos = pos.Lerp(Foot(next), c.StepProgress);

            if (c.State == ColonistState.Working && c.JobTarget is { } t && !c.Downed)
            {
                // Lean into the work with a rhythmic swing.
                var toward = (SimSpace.TileCenter(t) - pos - new Vector3(0, 0.5f, 0)).Normalized();
                pos += toward * 0.15f * Mathf.Abs(Mathf.Sin(_time * 9f + c.Id));
            }
            // A blow: a quick lunge towards the foe, the weapon swung down.
            long since = _sim.Tick - c.LastStrike;
            bool swinging = since is >= 0 and < 8;
            if (swinging && c.Foe is { } foe)
            {
                var d = Foot(foe.Position) - pos;
                d.Y = 0;
                if (d.LengthSquared() > 0.01f)
                {
                    pos += d.Normalized() * 0.18f * (1 - since / 8f);
                    v.Body.Rotation = new Vector3(0, Mathf.Atan2(d.X, d.Z) - Mathf.Pi / 2, 0);
                }
            }

            v.Root.Position = pos;
            v.Root.Scale = Vector3.One * (c.IsChild ? 0.6f : 1f);
            // Struck down: lying on its side.
            v.Body.Rotation = c.Downed ? new Vector3(0, v.Body.Rotation.Y, Mathf.Pi / 2) : new Vector3(0, v.Body.Rotation.Y, 0);
            v.Body.Position = c.Downed ? new Vector3(-0.35f, 0.22f, 0) : Vector3.Zero;
            v.Weapon.Rotation = new Vector3(0, 0, swinging ? -1.2f * (1 - since / 8f) : 0.15f);

            // Carried load: a box on the head, coloured by item type.
            v.Load.Visible = c.CarryingCount > 0 && !c.Downed;
            if (v.Load.Visible && (v.Load.MaterialOverride as StandardMaterial3D)?.AlbedoColor != ItemView.ItemColor(c.CarryingType))
                v.Load.MaterialOverride = new StandardMaterial3D { AlbedoColor = ItemView.ItemColor(c.CarryingType) };
            ShowGear(v.Weapon, c.Weapon, ref v.ShownWeapon, metal: 0.7f);
            ShowGear(v.Shield, c.TwoHanded ? null : c.Shield, ref v.ShownShield, metal: 0.4f);
            ShowGear(v.Armour, c.Armour, ref v.ShownArmour, metal: 0.5f);

            v.Bar.Visible = c.Health < Colonist.MaxHealth;
            if (v.Bar.Visible) v.Bar.SetInstanceShaderParameter("fill", c.Health / (float)Colonist.MaxHealth);
            v.Marker.Visible = c.Drafted;
            if (c.Drafted) v.Marker.Position = new Vector3(0, BodyHeight + 0.7f + 0.04f * Mathf.Sin(_time * 4f), 0);
            // Colonists stand in the cell above the solid they walk on, so show those on the cut surface too.
            v.Root.Visible = c.Position.Z <= _world.ViewLevel + 1;
        }
    }

    private static void ShowGear(MeshInstance3D mesh, ItemType? item, ref ItemType? shown, float metal)
    {
        mesh.Visible = item.HasValue;
        if (item == shown) return;
        shown = item;
        if (item is { } i)
            mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = ItemView.ItemColor(i), Metallic = metal, Roughness = 0.45f };
    }

    /// <summary>Views follow the colony: settlers, newborns and arrivals get one; those who left lose theirs.</summary>
    private void SyncViews()
    {
        if (_views.Count == _sim.Colonists.Count && (_views.Count == 0 || _views[^1].Colonist == _sim.Colonists[^1])) return;
        var present = new HashSet<Colonist>(_sim.Colonists);
        for (int i = _views.Count - 1; i >= 0; i--)
            if (!present.Contains(_views[i].Colonist))
            {
                _views[i].Root.QueueFree();
                _views.RemoveAt(i);
            }
        var shown = new HashSet<Colonist>();
        foreach (var v in _views) shown.Add(v.Colonist);
        foreach (var c in _sim.Colonists)
            if (!shown.Contains(c)) AddView(c);
    }

    private static Vector3 Foot(TilePos p) => new(p.X + 0.5f, p.Z, p.Y + 0.5f);
}
