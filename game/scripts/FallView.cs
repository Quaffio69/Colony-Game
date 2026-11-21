using System;
using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Fluids;
using Godot;

namespace ColonyGame;

/// <summary>
/// Waterfalls: the water simulation pours water over a drop straight from one column to the next, so the air between
/// the lip and where it lands holds no water to draw. Around the camera (re-read four times a second) every
/// <see cref="Water.Falls"/> gets a foaming curtain from the lip down, a churning splash where it lands and a few spray
/// droplets thrown out in arcs, and on falls of a level and a half or more, mist: dithered puffs that rise off the foot,
/// swell and drift out on a light breeze before fading (mist.gdshader). All motion is in the shaders, so nothing moves
/// on the CPU per frame.
/// Strong flows draw a solid sheet, trickles a lacy one.
/// </summary>
public partial class FallView : Node3D
{
    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private CameraRig _rig = null!;
    private MultiMeshInstance3D _sheets = null!, _splashes = null!, _spray = null!, _mist = null!;
    private readonly List<(Transform3D At, Color Data)> _puffs = new();
    private readonly List<ShaderMaterial> _materials = new();
    private readonly List<Fall> _falls = new();
    private double _refreshIn;
    private int _drawnLevel = int.MinValue;
    private const int SprayPerFall = 4, MaxFalls = 1500, MistPerFall = 4, MaxPuffs = 1600;
    /// <summary>Falls at least this tall (levels) throw up mist.</summary>
    private const float MistDrop = 1.5f;

    public void Initialize(ColonySim sim, WorldView world, CameraRig rig)
    {
        _sim = sim;
        _world = world;
        _rig = rig;
        AddToGroup(GameLooks.SweepSkip); // its own shader materials; nothing to convert
        var shader = GameLooks.Shader("res://shaders/waterfall.gdshader");
        _sheets = Multi(new QuadMesh { Size = Vector2.One }, Mat(shader, 0));
        _splashes = Multi(new SphereMesh { Radius = 0.32f, Height = 0.3f, RadialSegments = 8, Rings = 4 }, Mat(shader, 1));
        _spray = Multi(new BoxMesh { Size = Vector3.One * 0.09f }, Mat(shader, 2));
        var mist = new ShaderMaterial { Shader = GameLooks.Shader("res://shaders/mist.gdshader") };
        GameLooks.Styled(mist);
        _materials.Add(mist);
        _mist = Multi(new QuadMesh { Size = Vector2.One }, mist);
        _mist.ExtraCullMargin = 4; // puffs drift a few tiles from where they are placed (in the shader)
    }

    private ShaderMaterial Mat(Shader shader, int mode)
    {
        var m = new ShaderMaterial { Shader = shader };
        m.SetShaderParameter("mode", mode);
        GameLooks.Styled(m);
        _materials.Add(m);
        return m;
    }

    private MultiMeshInstance3D Multi(Mesh mesh, Material material)
    {
        var mmi = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = mesh },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(mmi);
        return mmi;
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
        if ((_refreshIn -= delta) > 0) return;
        _refreshIn = 0.25;
        Refresh();
    }

    private void Refresh()
    {
        // Falls within reach of the view (farther out they are a few pixels at most).
        var pivot = _rig.GlobalPosition;
        int range = (int)Math.Clamp(_rig.Distance * 1.6f, 40f, 180f);
        int cx = (int)pivot.X, cy = (int)pivot.Z;
        _falls.Clear();
        _sim.Water.Falls(cx - range, cy - range, cx + range, cy + range, _falls, max: MaxFalls);

        var sheets = _sheets.Multimesh;
        var splashes = _splashes.Multimesh;
        var spray = _spray.Multimesh;
        sheets.InstanceCount = _falls.Count;
        splashes.InstanceCount = _falls.Count;
        spray.InstanceCount = _falls.Count * SprayPerFall;
        _puffs.Clear();
        for (int i = 0; i < _falls.Count; i++)
        {
            var f = _falls[i];
            // Outward along the side it pours over (sim +x, -x, +y, -y = Godot +x, -x, +z, -z).
            var outward = f.Side switch { 0 => Vector3.Right, 1 => Vector3.Left, 2 => Vector3.Back, _ => Vector3.Forward };
            float drop = f.Top - f.Bottom;
            float strength = Mathf.Clamp(f.Rate * 12f, 0.05f, 1f);
            float seed = Colony.Core.Procedural.Noise.Hash(f.X, f.Y, f.Side, 0xFA11);
            var lip = new Vector3(f.X + 0.5f, 0, f.Y + 0.5f) + outward * 0.53f;
            // The curtain faces out from the lip (a QuadMesh faces +Z), a full cell wide so falls side by side join up;
            // how strong the flow is decides how lacy it is (in the shader).
            var facing = Basis.LookingAt(-outward, Vector3.Up);
            sheets.SetInstanceTransform(i, new Transform3D(facing.Scaled(new Vector3(1.02f, drop, 1)), lip + new Vector3(0, (f.Top + f.Bottom) / 2, 0)));
            sheets.SetInstanceCustomData(i, new Color(strength, drop, seed));
            var foot = lip + outward * 0.22f + new Vector3(0, f.Bottom, 0);
            splashes.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(Vector3.One * (0.7f + 0.6f * strength)), foot));
            splashes.SetInstanceCustomData(i, new Color(strength, drop, seed));
            for (int k = 0; k < SprayPerFall; k++)
            {
                // Each droplet arcs out along the facing's +Z (out from the cliff); phases spread by seed.
                float s = (seed + k * 0.27f) % 1f;
                spray.SetInstanceTransform(i * SprayPerFall + k, new Transform3D(Basis.LookingAt(-outward, Vector3.Up), foot + new Vector3(0, 0.05f, 0)));
                spray.SetInstanceCustomData(i * SprayPerFall + k, new Color(strength, drop, s));
            }
            // Mist: a few puffs on staggered loops, drifting off the foot of the taller falls.
            if (drop >= MistDrop && _puffs.Count < MaxPuffs)
            for (int k = 0; k < MistPerFall; k++)
            {
                float s = (seed * 7.3f + k * 0.41f) % 1f;
                _puffs.Add((new Transform3D(facing, foot), new Color(strength, drop, s, (seed + k / (float)MistPerFall) % 1f)));
            }
        }
        var mm = _mist.Multimesh;
        mm.InstanceCount = _puffs.Count;
        for (int i = 0; i < _puffs.Count; i++)
        {
            mm.SetInstanceTransform(i, _puffs[i].At);
            mm.SetInstanceCustomData(i, _puffs[i].Data);
        }
    }
}
