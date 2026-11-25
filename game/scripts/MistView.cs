using System;
using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Fluids;
using Godot;

namespace ColonyGame;

/// <summary>
/// Banks of waterfall mist, drawn from the sim's mist field (<see cref="Mist"/>: the falls feed it, it builds up, drifts
/// downwind and thins away), so the clouds the player sees are the mist the colonists enjoy. Around the camera, four
/// times a second, each misty cell gets up to three cloud puffs (one, then more as the mist thickens), each eased toward
/// the field's density so clouds swell and fade rather than pop. The puffs loop and drift in the shader
/// (mist_cloud.gdshader), dithered so they stay pixel art, with a capped opacity so even the thickest bank leaves the
/// scene readable. Costs nothing while there is no mist; one bulk buffer upload per refresh otherwise.
/// </summary>
public partial class MistView : Node3D
{
    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private CameraRig _rig = null!;
    private MultiMeshInstance3D _clouds = null!;
    private ShaderMaterial _material = null!;
    /// <summary>Density drawn per cell (eased toward the field's), and the refresh that last saw it in the field.</summary>
    private readonly Dictionary<int, (float Shown, int Seen)> _cells = new();
    private readonly List<int> _drop = new(), _fade = new();
    private float[] _buffer = Array.Empty<float>();
    private int _capacity, _refresh;
    private double _refreshIn;
    private int _drawnLevel = int.MinValue;
    private Vector2 _wind, _windTarget;
    private const int MaxPuffs = 6000, Floats = 16;
    /// <summary>Drift speed, tiles a second per unit of <see cref="Mist.Wind"/>.</summary>
    private const float Drift = 0.12f;
    /// <summary>Density over which each of a cell's puffs fades in (the second and third for thicker mist).</summary>
    private static readonly (float From, float To)[] Layers = [(20f, 160f), (260f, 650f), (900f, 1700f)];

    public void Initialize(ColonySim sim, WorldView world, CameraRig rig)
    {
        _sim = sim;
        _world = world;
        _rig = rig;
        AddToGroup(GameLooks.SweepSkip); // its own shader material
        _material = new ShaderMaterial { Shader = GameLooks.Shader("res://shaders/mist_cloud.gdshader") };
        GameLooks.Styled(_material);
        _clouds = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = new QuadMesh { Size = Vector2.One } },
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 8, // puffs drift and swell a few tiles from where they are placed (in the shader)
        };
        AddChild(_clouds);
        var (wx, wy) = Mist.Wind(sim.Tick);
        _wind = _windTarget = new Vector2(wx, wy) * Drift;
        _material.SetShaderParameter("wind", _wind);
    }

    public override void _Process(double delta)
    {
        if (_sim == null) return;
        int level = _world.ViewLevel;
        if (level != _drawnLevel)
        {
            _drawnLevel = level;
            _material.SetShaderParameter("cut_height", (float)(level + 1));
            _refreshIn = 0; // mist above the cut comes and goes with it
        }
        if (_wind != _windTarget)
        {
            // The breeze veers over several seconds, so drifting puffs bend round rather than jump.
            _wind = _wind.DistanceTo(_windTarget) < 0.002f ? _windTarget : _wind.Lerp(_windTarget, 1f - Mathf.Exp(-(float)delta / 6f));
            _material.SetShaderParameter("wind", _wind);
        }
        if ((_refreshIn -= delta) > 0) return;
        _refreshIn = 0.25;
        Refresh();
    }

    private void Refresh()
    {
        var mist = _sim.Mist;
        if (mist.ActiveCells == 0 && _cells.Count == 0)
        {
            if (_clouds.Multimesh.VisibleInstanceCount != 0) _clouds.Multimesh.VisibleInstanceCount = 0;
            return;
        }
        var (wx, wy) = Mist.Wind(_sim.Tick);
        _windTarget = new Vector2(wx, wy) * Drift;

        // The field round the camera (its clouds are big and soft, so a little farther out than the falls).
        var pivot = _rig.GlobalPosition;
        int range = (int)Math.Clamp(_rig.Distance * 2f, 48f, 256f);
        int c = Mist.Cell;
        int cx0 = Math.Max(0, ((int)pivot.X - range) / c), cx1 = Math.Min(mist.CellsWide - 1, ((int)pivot.X + range) / c);
        int cy0 = Math.Max(0, ((int)pivot.Z - range) / c), cy1 = Math.Min(mist.CellsDeep - 1, ((int)pivot.Z + range) / c);
        int stamp = ++_refresh;
        int view = _drawnLevel;
        if (mist.ActiveCells > 0)
        for (int cy = cy0; cy <= cy1; cy++)
        for (int cx = cx0; cx <= cx1; cx++)
        {
            var (d, level) = mist.CellAt(cx, cy);
            if (d == 0 || level > view) continue; // none, or all above the cut
            int x = Math.Min(cx * c + c / 2, _sim.World.Width - 1), y = Math.Min(cy * c + c / 2, _sim.World.Depth - 1);
            if (_sim.World.IsUnseen(x, y, level)) continue; // in a cavern nobody has seen yet
            int key = cy * mist.CellsWide + cx;
            float shown = _cells.TryGetValue(key, out var was) ? was.Shown : 0f;
            _cells[key] = (shown + (d - shown) * 0.3f, stamp);
        }
        // Cells the field no longer has (or that left the view) fade out; gone ones are dropped.
        _drop.Clear();
        foreach (var (key, (shown, seen)) in _cells)
        {
            if (seen == stamp) continue;
            int cx = key % mist.CellsWide, cy = key / mist.CellsWide;
            bool inView = cx >= cx0 && cx <= cx1 && cy >= cy0 && cy <= cy1;
            if (!inView || shown < Layers[0].From) _drop.Add(key);
            else _fade.Add(key);
        }
        foreach (int key in _drop) _cells.Remove(key);
        foreach (int key in _fade) { var (shown, seen) = _cells[key]; _cells[key] = (shown * 0.7f, seen); }
        _fade.Clear();
        int n = 0;
        EnsureCapacity(Math.Min(MaxPuffs, _cells.Count * Layers.Length));
        foreach (var (key, (shown, _)) in _cells)
        {
            if (n >= _capacity) break;
            int cx = key % mist.CellsWide, cy = key / mist.CellsWide;
            int level = mist.CellAt(cx, cy).Level;
            float thick = Math.Min(shown / Mist.Full, 2f);
            // Capped: one puff over thin mist is a faint haze, the thickest banks cover a little over half of what they cross.
            float opacity = Math.Min(0.46f, 0.2f + 0.22f * MathF.Sqrt(thick));
            float size = c * (1.05f + 0.2f * Math.Min(thick, 1f));
            for (int k = 0; k < Layers.Length && n < _capacity; k++)
            {
                var (from, to) = Layers[k];
                float w = Mathf.Clamp((shown - from) / (to - from), 0f, 1f);
                if (w <= 0.02f) break;
                float seed = Colony.Core.Procedural.Noise.Hash(cx, cy, k, 0x1157);
                float jx = (Colony.Core.Procedural.Noise.Hash(cx, cy, k, 0x51) - 0.5f) * c * 0.6f;
                float jy = (Colony.Core.Procedural.Noise.Hash(cx, cy, k, 0x52) - 0.5f) * c * 0.6f;
                int o = n++ * Floats;
                // Identity basis (the shader faces it to the camera), origin over the cell; thicker mist stacks higher.
                _buffer[o] = 1; _buffer[o + 1] = 0; _buffer[o + 2] = 0; _buffer[o + 3] = cx * c + c / 2f + jx;
                _buffer[o + 4] = 0; _buffer[o + 5] = 1; _buffer[o + 6] = 0; _buffer[o + 7] = level + 0.7f + k * 1.1f;
                _buffer[o + 8] = 0; _buffer[o + 9] = 0; _buffer[o + 10] = 1; _buffer[o + 11] = cy * c + c / 2f + jy;
                _buffer[o + 12] = opacity * w;
                _buffer[o + 13] = size * (1f - 0.12f * k);
                _buffer[o + 14] = seed;
                _buffer[o + 15] = (seed * 3.7f + k / (float)Layers.Length) % 1f;
            }
        }
        var mm = _clouds.Multimesh;
        if (n > 0) mm.Buffer = _buffer;
        mm.VisibleInstanceCount = n;
    }

    /// <summary>Grows the instance pool in steps (never shrinks), so the buffer is reallocated only now and then.</summary>
    private void EnsureCapacity(int want)
    {
        if (want <= _capacity) return;
        _capacity = Math.Min(MaxPuffs, Math.Max(256, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)want)));
        _buffer = new float[_capacity * Floats];
        _clouds.Multimesh.InstanceCount = _capacity;
        _clouds.Multimesh.VisibleInstanceCount = 0;
    }
}
