using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Combat;
using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>
/// Draws wild creatures (one MultiMesh for every giant ant: there can be hundreds) and their nests (a soil mound with a
/// dark hole and a tag). Ants lean into a bite and turn red-brown while fighting; positions interpolate between ticks.
/// </summary>
public partial class CreatureView : Node3D
{
    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private MultiMeshInstance3D _ants = null!;
    private readonly Dictionary<int, float> _heading = new();
    private readonly Dictionary<int, (Node3D Root, Label3D Tag)> _nests = new();
    private int _nestVersion = -1;
    private float _time;

    private static readonly Color Calm = new(0.16f, 0.09f, 0.06f);
    private static readonly Color Angry = new(0.42f, 0.06f, 0.04f);

    private System.Func<int> _player = () => 0;

    public void Initialize(ColonySim sim, WorldView world, System.Func<int> player)
    {
        _sim = sim;
        _world = world;
        _player = player;
        var mat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.4f, Metallic = 0f, MetallicSpecular = 0.7f };
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = AntMesh(mat),
            InstanceCount = 256,
            VisibleInstanceCount = 0,
        };
        _ants = new MultiMeshInstance3D { Multimesh = mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        AddChild(_ants);
    }

    /// <summary>A giant ant, nose along +X: head, thorax, abdomen, six legs, two feelers (white: tinted per instance).</summary>
    private static Mesh AntMesh(Material m)
    {
        var p = new MeshKit.Parts();
        p.Add(MeshKit.Ball(0.13f, 0.2f), 0.26f, 0.2f, 0, m);       // head
        p.Add(MeshKit.Ball(0.1f, 0.16f), 0.08f, 0.2f, 0, m);       // thorax
        p.Add(MeshKit.Ball(0.2f, 0.3f), -0.2f, 0.24f, 0, m);       // abdomen
        p.Add(MeshKit.Box(0.08f, 0.04f, 0.06f), 0.4f, 0.2f, 0.05f, m); // mandibles
        p.Add(MeshKit.Box(0.08f, 0.04f, 0.06f), 0.4f, 0.2f, -0.05f, m);
        for (int i = 0; i < 3; i++)
        {
            float x = 0.16f - i * 0.1f, swing = (i - 1) * 0.5f;
            p.Add(MeshKit.Box(0.03f, 0.03f, 0.42f), MeshKit.At(x, 0.12f, 0.18f, 0.7f, swing, 0), m);
            p.Add(MeshKit.Box(0.03f, 0.03f, 0.42f), MeshKit.At(x, 0.12f, -0.18f, -0.7f, -swing, 0), m);
        }
        p.Add(MeshKit.Box(0.22f, 0.02f, 0.02f), MeshKit.At(0.42f, 0.32f, 0.07f, 0, -0.5f, 0.5f), m); // feelers
        p.Add(MeshKit.Box(0.22f, 0.02f, 0.02f), MeshKit.At(0.42f, 0.32f, -0.07f, 0, 0.5f, 0.5f), m);
        return MeshKit.Compose(p);
    }

    public override void _Process(double delta)
    {
        if (_sim == null) return;
        _time += (float)delta;
        SyncNests();
        DrawAnts();
    }

    private void DrawAnts()
    {
        var mm = _ants.Multimesh;
        var creatures = _sim.Wildlife.Creatures;
        if (creatures.Count > mm.InstanceCount) mm.InstanceCount = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)creatures.Count);
        int n = 0, level = _world.ViewLevel + 1;
        foreach (var c in creatures)
        {
            if (c.Dead || c.Position.Z > level) continue;
            var from = Foot(c.Position);
            var pos = from;
            if (c.NextStep is { } next)
            {
                var to = Foot(next);
                pos = from.Lerp(to, c.StepProgress);
                var d = to - from;
                if (d.X * d.X + d.Z * d.Z > 0.01f) _heading[c.Id] = Mathf.Atan2(-d.Z, d.X);
            }
            float yaw = _heading.TryGetValue(c.Id, out float h) ? h : c.Id * 1.7f;
            if (c.Prey is { } t)
            {
                var d = Foot(t.Position) - pos;
                if (d.X * d.X + d.Z * d.Z > 0.01f) yaw = _heading[c.Id] = Mathf.Atan2(-d.Z, d.X);
            }
            var basis = new Basis(Vector3.Up, yaw);
            // A scuttle (a little bob while walking) and a lunge on a bite.
            if (c.NextStep is not null) pos.Y += 0.03f * Mathf.Abs(Mathf.Sin(_time * 22f + c.Id));
            long since = _sim.Tick - c.LastStrike;
            if (since is >= 0 and < 6) pos += basis * new Vector3(0.25f * (1 - since / 6f), 0, 0);
            mm.SetInstanceTransform(n, new Transform3D(basis.Scaled(Vector3.One * 1.1f), pos));
            mm.SetInstanceColor(n, c.Fighting ? Angry : Calm);
            n++;
        }
        mm.VisibleInstanceCount = n;
        if (_heading.Count > creatures.Count * 2 + 64) _heading.Clear();
    }

    private void SyncNests()
    {
        var wildlife = _sim.Wildlife;
        if (wildlife.Version != _nestVersion)
        {
            _nestVersion = wildlife.Version;
            var alive = new HashSet<int>();
            foreach (var n in wildlife.Nests)
            {
                alive.Add(n.Id);
                if (!_nests.ContainsKey(n.Id)) _nests[n.Id] = MakeNest(n);
            }
            foreach (var id in new List<int>(_nests.Keys))
                if (!alive.Contains(id))
                {
                    _nests[id].Root.QueueFree();
                    _nests.Remove(id);
                }
        }
        int level = _world.ViewLevel + 1;
        foreach (var n in wildlife.Nests)
        {
            if (!_nests.TryGetValue(n.Id, out var view)) continue;
            view.Root.Visible = n.Entrance.Z <= level;
            bool hostile = n.HostileTo(_player(), _sim.Tick);
            view.Tag.Text = $"{n.Kind.Name} nest · {n.Total}" + (n.Health < n.Kind.NestHealth ? $" · mound {n.Health * 100 / n.Kind.NestHealth}%" : "");
            view.Tag.Modulate = hostile ? new Color(1f, 0.45f, 0.35f) : new Color(0.92f, 0.85f, 0.7f);
        }
    }

    private (Node3D, Label3D) MakeNest(Nest n)
    {
        var soil = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.33f, 0.22f), Roughness = 0.95f };
        var dark = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.05f, 0.04f), Roughness = 1f };
        var p = new MeshKit.Parts();
        p.Add(MeshKit.Cone(1.25f, 0.35f, 0.55f, 14), 0, 0.27f, 0, soil);
        p.Add(MeshKit.Cone(0.6f, 0.1f, 0.35f, 10), 0.55f, 0.17f, 0.45f, soil);
        p.Add(MeshKit.Cone(0.5f, 0.1f, 0.3f, 10), -0.6f, 0.15f, -0.35f, soil);
        p.Add(MeshKit.Cyl(0.24f, 0.06f, 10), 0, 0.55f, 0, dark); // the hole
        var root = new Node3D { Name = $"Nest_{n.Id}", Position = Foot(n.Entrance) };
        root.AddChild(new MeshInstance3D { Mesh = MeshKit.Compose(p) });
        var tag = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 40,
            PixelSize = 0.009f,
            OutlineSize = 8,
            Position = new Vector3(0, 1.3f, 0),
            VisibilityRangeEnd = 90,
        };
        root.AddChild(tag);
        AddChild(root);
        return (root, tag);
    }

    private static Vector3 Foot(TilePos p) => new(p.X + 0.5f, p.Z, p.Y + 0.5f);
}
