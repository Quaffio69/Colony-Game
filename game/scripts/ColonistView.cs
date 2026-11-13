using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Agents;
using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>Draws each colonist as a capsule with a name tag, interpolating between simulation steps.</summary>
public partial class ColonistView : Node3D
{
    private const float BodyHeight = 0.8f;

    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private readonly List<(Colonist Colonist, Node3D Root)> _views = new();
    private float _time;

    public void Initialize(ColonySim sim, WorldView world)
    {
        _sim = sim;
        _world = world;
        foreach (var c in sim.Colonists) AddView(c);
    }

    private void AddView(Colonist c)
    {
        var root = new Node3D { Name = $"Colonist_{c.Id}_{c.Name}" };
        float hue = Colony.Core.Procedural.Noise.Hash(c.Id, 0, 0, 4242);
        root.AddChild(new MeshInstance3D
        {
            Mesh = new CapsuleMesh
            {
                Radius = 0.22f,
                Height = BodyHeight,
                Material = new StandardMaterial3D { AlbedoColor = Color.FromHsv(hue, 0.65f, 0.9f), Roughness = 0.6f },
            },
            Position = new Vector3(0, BodyHeight / 2f, 0),
        });
        root.AddChild(new Label3D
        {
            Text = c.Name,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 48,
            PixelSize = 0.009f,
            OutlineSize = 10,
            Position = new Vector3(0, BodyHeight + 0.3f, 0),
            NoDepthTest = true,
        });
        AddChild(root);
        _views.Add((c, root));
    }

    public override void _Process(double delta)
    {
        if (_sim == null) return;
        _time += (float)delta;

        foreach (var (c, root) in _views)
        {
            var pos = Foot(c.Position);
            if (c.NextStep is { } next) pos = pos.Lerp(Foot(next), c.StepProgress);

            if (c.State == ColonistState.Working && c.JobTarget is { } t)
            {
                // Lean into the work with a rhythmic swing.
                var toward = (SimSpace.TileCenter(t) - pos - new Vector3(0, 0.5f, 0)).Normalized();
                pos += toward * 0.15f * Mathf.Abs(Mathf.Sin(_time * 9f + c.Id));
            }

            root.Position = pos;
            // Colonists stand in the cell above the solid they walk on, so show those on the cut surface too.
            root.Visible = c.Position.Z <= _world.ViewLevel + 1;
        }
    }

    private static Vector3 Foot(TilePos p) => new(p.X + 0.5f, p.Z, p.Y + 0.5f);
}
