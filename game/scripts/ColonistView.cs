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
        public MeshInstance3D Load = null!, Weapon = null!, Shield = null!, Armour = null!, Bar = null!, Marker = null!, Scaffold = null!;
        public MeshInstance3D? Drill; // the excavator's drill bit (spins while it digs)
        public float Heading; // a machine faces where it drives
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
        if (c.IsMachine) AddMachineBody(v);
        else
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
        v.Load = c.Machine == MachineKind.PowerWagon
            ? Part(v.Body, MeshKit.Box(0.46f, 0.2f, 0.4f), new Vector3(0.17f, 0.44f, 0)) // heaped in the wagon's bed
            : Part(v.Body, MeshKit.Box(0.34f, 0.22f, 0.3f), new Vector3(0, BodyHeight + 0.08f, 0));
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
        v.Scaffold = new MeshInstance3D { Mesh = ScaffoldMesh, Visible = false, Name = "Scaffold" };
        v.Root.AddChild(v.Scaffold);
        if (Main.StyledRender) // zoomed far out (all symbols), colonists always show as ☺ in their colour
            v.Root.AddChild(new DwarfRender.AsciiMarker { GlyphGroup = DwarfRender.GlyphAtlas.Dwarf, GlyphColour = Color.FromHsv(hue, 0.65f, 0.9f), Position = new Vector3(0, 0.5f, 0) });
        AddChild(v.Root);
        _views.Add(v);
    }

    // ---- Heavy machinery: dwarven steam engines on tracks and wheels (facing +x, turned by Heading) ----------------

    private static Mesh? _excavatorMesh, _wagonMesh, _drillMesh;

    private static StandardMaterial3D Mat(string hex, float metal = 0f, float rough = 0.6f) =>
        new() { AlbedoColor = Color.FromHtml(hex), Metallic = metal, Roughness = rough };

    private static StandardMaterial3D Glow(string hex) =>
        new() { AlbedoColor = Color.FromHtml(hex), EmissionEnabled = true, Emission = Color.FromHtml(hex), EmissionEnergyMultiplier = 2.5f };

    private void AddMachineBody(View v)
    {
        bool excavator = v.Colonist.Machine == MachineKind.Excavator;
        v.Body.Scale = Vector3.One * 1.3f; // heavy machinery: bigger than a person, still within its cell
        v.Body.AddChild(new MeshInstance3D { Mesh = excavator ? _excavatorMesh ??= BuildExcavator() : _wagonMesh ??= BuildWagon() });
        if (!excavator) return;
        v.Drill = new MeshInstance3D { Mesh = _drillMesh ??= BuildDrill(), Position = new Vector3(0.5f, 0.4f, 0), Rotation = new Vector3(0, 0, -Mathf.Pi / 2) };
        v.Body.AddChild(v.Drill);
    }

    /// <summary>The steam excavator: iron tracks, a riveted chassis, a copper boiler with brass bands, a smokestack, a glowing
    /// firebox at the back and a brass arm holding the drill out front.</summary>
    private static Mesh BuildExcavator()
    {
        var iron = Mat("#3a3634", 0.4f, 0.7f);
        var chassis = Mat("#5e554e", 0.5f, 0.55f);
        var copper = Mat("#b8733a", 0.8f, 0.35f);
        var brass = Mat("#d0a040", 0.85f, 0.3f);
        var soot = Mat("#262222", 0.3f, 0.8f);
        var p = new MeshKit.Parts();
        foreach (float z in new[] { -0.27f, 0.27f })
        {
            p.Add(MeshKit.Box(0.9f, 0.2f, 0.18f), 0, 0.1f, z, iron); // tracks
            foreach (float x in new[] { -0.32f, 0f, 0.32f }) p.Add(MeshKit.Cyl(0.08f, 0.2f, 10), MeshKit.AlongZ(x, 0.1f, z), brass); // road wheels
        }
        p.Add(MeshKit.Box(0.72f, 0.12f, 0.46f), 0, 0.26f, 0, chassis);
        p.Add(MeshKit.Cyl(0.2f, 0.4f, 14), -0.1f, 0.52f, 0, copper); // boiler
        foreach (float y in new[] { 0.4f, 0.62f }) p.Add(MeshKit.Cyl(0.215f, 0.035f, 14), -0.1f, y, 0, brass);
        p.Add(MeshKit.Ball(0.2f, 0.16f, 14), -0.1f, 0.72f, 0, copper); // dome
        p.Add(MeshKit.Cyl(0.05f, 0.34f, 8), -0.26f, 0.86f, 0.12f, soot); // smokestack
        p.Add(MeshKit.Cone(0.05f, 0.09f, 0.08f, 8), -0.26f, 1.06f, 0.12f, soot);
        p.Add(MeshKit.Box(0.04f, 0.09f, 0.16f), -0.37f, 0.38f, 0, Glow("#ff7a2a")); // firebox
        p.Add(MeshKit.Box(0.4f, 0.08f, 0.09f), MeshKit.At(0.26f, 0.44f, 0, 0, 0, -0.12f), brass); // arm
        p.Add(MeshKit.Box(0.1f, 0.14f, 0.14f), 0.44f, 0.41f, 0, chassis); // drill housing
        return MeshKit.Compose(p);
    }

    /// <summary>The drill bit: a steel cone with a spiral of brass flutes (spun about its axis while digging).</summary>
    private static Mesh BuildDrill()
    {
        var steel = Mat("#a8b0b8", 0.9f, 0.25f);
        var brass = Mat("#d0a040", 0.85f, 0.3f);
        var p = new MeshKit.Parts();
        p.Add(MeshKit.Cone(0.11f, 0.0f, 0.3f, 10), 0, 0.15f, 0, steel);
        for (int i = 0; i < 3; i++) p.Add(MeshKit.Box(0.02f, 0.2f, 0.17f - i * 0.05f), MeshKit.At(0, 0.06f + i * 0.08f, 0, 0, i * 1.1f, 0), brass);
        return MeshKit.Compose(p);
    }

    /// <summary>The power wagon: a plank bed on four iron-rimmed wheels with a copper boiler up front, its smokestack and firebox.</summary>
    private static Mesh BuildWagon()
    {
        var wood = Mat("#8c613a", 0f, 0.8f);
        var plank = Mat("#a8804e", 0f, 0.8f);
        var iron = Mat("#3a3634", 0.5f, 0.6f);
        var copper = Mat("#b8733a", 0.8f, 0.35f);
        var brass = Mat("#d0a040", 0.85f, 0.3f);
        var soot = Mat("#262222", 0.3f, 0.8f);
        var p = new MeshKit.Parts();
        foreach (float x in new[] { -0.26f, 0.3f })
        foreach (float z in new[] { -0.28f, 0.28f })
        {
            p.Add(MeshKit.Cyl(0.16f, 0.06f, 14), MeshKit.AlongZ(x, 0.16f, z), iron); // wheel
            p.Add(MeshKit.Cyl(0.06f, 0.08f, 8), MeshKit.AlongZ(x, 0.16f, z), brass); // hub
        }
        p.Add(MeshKit.Box(0.84f, 0.08f, 0.5f), 0.04f, 0.3f, 0, wood); // bed
        foreach (float z in new[] { -0.24f, 0.24f }) p.Add(MeshKit.Box(0.56f, 0.14f, 0.04f), 0.17f, 0.41f, z, plank); // sides
        p.Add(MeshKit.Box(0.04f, 0.14f, 0.5f), 0.45f, 0.41f, 0, plank); // tailboard
        p.Add(MeshKit.Cyl(0.15f, 0.34f, 14), MeshKit.AlongX(-0.24f, 0.5f, 0), copper); // boiler, lying along the wagon
        foreach (float x in new[] { -0.36f, -0.12f }) p.Add(MeshKit.Cyl(0.16f, 0.03f, 14), MeshKit.AlongX(x, 0.5f, 0), brass);
        p.Add(MeshKit.Cyl(0.045f, 0.3f, 8), -0.34f, 0.78f, 0, soot); // smokestack
        p.Add(MeshKit.Cone(0.045f, 0.08f, 0.07f, 8), -0.34f, 0.96f, 0, soot);
        p.Add(MeshKit.Box(0.04f, 0.08f, 0.14f), -0.42f, 0.38f, 0, Glow("#ff7a2a")); // firebox
        return MeshKit.Compose(p);
    }

    private static Mesh? _scaffoldMesh;
    /// <summary>A one-level scaffold standing in the colonist's cell: four poles, cross braces and a plank deck on top.</summary>
    private static Mesh ScaffoldMesh => _scaffoldMesh ??= BuildScaffold();

    private static Mesh BuildScaffold()
    {
        var pole = new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.47f, 0.3f), Roughness = 0.8f };
        var deck = new StandardMaterial3D { AlbedoColor = new Color(0.74f, 0.6f, 0.4f), Roughness = 0.8f };
        var rope = new StandardMaterial3D { AlbedoColor = new Color(0.82f, 0.76f, 0.6f), Roughness = 0.9f };
        var p = new MeshKit.Parts();
        foreach (float x in new[] { -0.38f, 0.38f })
        foreach (float z in new[] { -0.38f, 0.38f })
        {
            p.Add(MeshKit.Cyl(0.03f, 1.0f, 6), x, 0.5f, z, pole);
            p.Add(MeshKit.Cyl(0.04f, 0.05f, 6), x, 0.97f, z, rope); // lashing at the top
        }
        float len = Mathf.Sqrt(0.76f * 0.76f + 0.9f * 0.9f), a = Mathf.Atan2(0.76f, 0.9f);
        foreach (float z in new[] { -0.38f, 0.38f }) p.Add(MeshKit.Box(0.03f, len, 0.03f), MeshKit.At(0, 0.5f, z, 0, 0, a), pole); // X braces on two sides
        foreach (float x in new[] { -0.38f, 0.38f }) p.Add(MeshKit.Box(0.03f, len, 0.03f), MeshKit.At(x, 0.5f, 0, -a, 0, 0), pole);
        for (int i = 0; i < 4; i++) p.Add(MeshKit.Box(0.86f, 0.04f, 0.2f), 0, 1.0f, -0.3f + i * 0.2f, deck);
        return MeshKit.Compose(p);
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

            if (c.IsMachine)
            {
                // Machines turn to face where they drive (or what they dig), and the drill spins while it bites.
                Vector3? toward = c.NextStep is { } ns ? Foot(ns) - Foot(c.Position) : c.State == ColonistState.Working && c.JobTarget is { } jt ? SimSpace.TileCenter(jt) - pos : null;
                if (toward is { } d && new Vector2(d.X, d.Z).LengthSquared() > 0.001f) v.Heading = Mathf.LerpAngle(v.Heading, Mathf.Atan2(-d.Z, d.X), Mathf.Min(1f, (float)delta * 8f));
                v.Body.Rotation = new Vector3(0, v.Heading, 0);
                if (v.Drill != null && c.State == ColonistState.Working) v.Drill.Rotation = new Vector3(_time * 18f, 0, -Mathf.Pi / 2);
            }
            v.Root.Position = pos;
            v.Root.Scale = Vector3.One * (c.IsChild ? 0.6f : 1f);
            // Struck down: lying on its side.
            v.Body.Rotation = c.Downed ? new Vector3(0, v.Body.Rotation.Y, Mathf.Pi / 2) : new Vector3(0, v.Body.Rotation.Y, 0);
            // Working from a scaffold: it rises under the colonist as it is put up, and the colonist stands on its deck.
            float scaffold = c.ScaffoldHeight;
            v.Scaffold.Visible = scaffold > 0.01f;
            if (v.Scaffold.Visible) v.Scaffold.Scale = new Vector3(1, scaffold, 1);
            v.Body.Position = c.Downed ? new Vector3(-0.35f, 0.22f, 0) : new Vector3(0, scaffold > 0.01f ? scaffold * 1.02f : 0, 0);
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
