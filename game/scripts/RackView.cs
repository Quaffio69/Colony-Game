using System;
using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.World;
using Godot;
using static ColonyGame.MeshKit;

namespace ColonyGame;

/// <summary>
/// Storage racks (<see cref="Stockpiles.Racks"/>): timber uprights with two shelves a level, a braced back and a rail
/// along the open front, one MultiMesh per height. Racks two or more levels tall standing side by side with the same
/// front make a run, and each run has a ladder hooked on its rail that slides along it to the colonist working there
/// (warehouse style). Rack blueprints are translucent boxes. Goods on the shelves are ItemView's (one pile model per
/// stack, shelf by shelf). Sliced at the view level like flora.
/// </summary>
public partial class RackView : Node3D
{
    /// <summary>Height between shelves (two a level) and the boards' thickness: ItemView stacks goods on them.</summary>
    public const float ShelfStep = 1f / Stockpiles.ShelvesPerLevel, BoardThickness = 0.04f;

    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private Shader _shader = null!;
    private readonly List<ShaderMaterial> _materials = new();
    private readonly MultiMeshInstance3D[] _racks = new MultiMeshInstance3D[Stockpiles.MaxRackLevels + 1];
    private MultiMeshInstance3D _ladders = null!, _ghosts = null!;
    private int _drawnRacks = -1, _drawnSites = -1, _drawnLevel = int.MinValue;

    /// <summary>A row of tall racks sharing a front: the ladder slides between its ends.</summary>
    private sealed class Run
    {
        public TilePos First;          // the rack at the low end of the run
        public int Length, Levels, Facing;
        public float Shown, Target;    // ladder position along the run (0 = the first rack's centre)
    }
    private readonly List<Run> _runs = new();

    public void Initialize(ColonySim sim, WorldView world)
    {
        _sim = sim;
        _world = world;
        _shader = GameLooks.Shader("res://shaders/flora.gdshader");
        var wood = Mat(new Color(0.55f, 0.4f, 0.25f));
        var dark = Mat(new Color(0.36f, 0.27f, 0.18f));
        var board = Mat(new Color(0.7f, 0.55f, 0.36f));
        var iron = Mat(new Color(0.42f, 0.43f, 0.46f));
        for (int levels = 1; levels <= Stockpiles.MaxRackLevels; levels++)
        {
            _racks[levels] = Multi(RackMesh(levels, wood, dark, board, iron));
            AddChild(_racks[levels]);
        }
        _ladders = Multi(LadderMesh(wood, iron));
        AddChild(_ladders);
        _ghosts = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new BoxMesh { Size = Vector3.One, Material = Overlay.Material(new Color(1f, 0.75f, 0.4f, 0.28f)) },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_ghosts);
    }

    private static MultiMeshInstance3D Multi(Mesh mesh) => new()
    {
        Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = mesh },
    };

    private ShaderMaterial Mat(Color albedo)
    {
        var m = new ShaderMaterial { Shader = _shader };
        m.SetShaderParameter("albedo", albedo);
        m.SetShaderParameter("tint_mix", 0.0f);
        m.SetShaderParameter("object_id", (float)GameLooks.Furniture);
        GameLooks.Styled(m);
        _materials.Add(m);
        return m;
    }

    /// <summary>A rack of this many levels in its cell, its open front towards +x (turned by facing).</summary>
    private static Mesh RackMesh(int levels, Material wood, Material dark, Material board, Material iron)
    {
        var p = new Parts();
        float h = levels;
        foreach (float x in new[] { -0.44f, 0.44f })
        foreach (float z in new[] { -0.46f, 0.46f })
            p.Add(Box(0.07f, h, 0.07f), x, h / 2, z, wood); // uprights
        for (int s = 1; s <= levels * Stockpiles.ShelvesPerLevel; s++)
        {
            float y = s * ShelfStep;
            p.Add(Box(0.9f, BoardThickness, 0.94f), 0, y, 0, board); // shelf boards
            p.Add(Box(0.05f, 0.06f, 0.98f), 0.44f, y - 0.02f, 0, dark); // front beam
        }
        for (int l = 0; l < levels; l++)
        {
            // A braced back: an X across each level.
            float y = l + 0.5f, len = MathF.Sqrt(1f + 0.92f * 0.92f);
            float a = MathF.Atan2(1f, 0.92f);
            p.Add(Box(0.03f, len, 0.04f), At(-0.46f, y, 0, a, 0, 0), dark);
            p.Add(Box(0.03f, len, 0.04f), At(-0.46f, y, 0, -a, 0, 0), dark);
        }
        if (levels >= 2) p.Add(Cyl(0.025f, 1.0f, 6), AlongZ(0.52f, h - 0.06f, 0), iron); // the ladder rail along the front
        return Compose(p);
    }

    /// <summary>A rolling ladder one unit tall (scaled to the run's height), hooked at the top, leaning out at the foot.</summary>
    private static Mesh LadderMesh(Material wood, Material iron)
    {
        var p = new Parts();
        const float lean = 0.14f;
        float tilt = MathF.Atan2(lean, 1f);
        foreach (float z in new[] { -0.2f, 0.2f })
            p.Add(Box(0.05f, 1.01f, 0.05f), At(lean / 2, 0.5f, z, 0, 0, tilt), wood);
        for (int r = 1; r <= 7; r++)
        {
            float y = r / 8f;
            p.Add(Cyl(0.018f, 0.4f, 5), AlongZ(lean * (1 - y), y, 0), wood);
        }
        p.Add(Box(0.06f, 0.05f, 0.46f), 0f, 0.985f, 0, iron); // hooks over the rail
        p.Add(Cyl(0.03f, 0.04f, 8), AlongZ(lean, 0.02f, -0.2f), iron); // wheels
        p.Add(Cyl(0.03f, 0.04f, 8), AlongZ(lean, 0.02f, 0.2f), iron);
        return Compose(p);
    }

    /// <summary>Godot yaw for a facing (0..3: +x, +y, -x, -y in sim space).</summary>
    private static float Yaw(int facing) => -facing * Mathf.Pi / 2;

    /// <summary>Sim step along a run for a front facing: runs go across the front.</summary>
    private static (int X, int Y) Along(int facing) => facing % 2 == 0 ? (0, 1) : (1, 0);
    private static (int X, int Y) Front(int facing) => facing switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };

    public override void _Process(double delta)
    {
        if (_sim == null) return;
        int level = _world.ViewLevel;
        if (level != _drawnLevel)
        {
            _drawnLevel = level;
            foreach (var m in _materials) m.SetShaderParameter("cut_height", (float)(level + 1));
        }
        if (_sim.Stockpiles.RackVersion != _drawnRacks) Rebuild();
        if (_sim.Constructions.Version != _drawnSites) RebuildGhosts();
        MoveLadders((float)delta);
    }

    private void Rebuild()
    {
        _drawnRacks = _sim.Stockpiles.RackVersion;
        var byLevels = new List<Transform3D>[Stockpiles.MaxRackLevels + 1];
        for (int l = 1; l <= Stockpiles.MaxRackLevels; l++) byLevels[l] = new List<Transform3D>();
        var racks = _sim.Stockpiles.Racks;
        foreach (var (p, rack) in racks)
            byLevels[rack.Levels].Add(new Transform3D(new Basis(Vector3.Up, Yaw(rack.Facing)), new Vector3(p.X + 0.5f, p.Z, p.Y + 0.5f)));
        for (int l = 1; l <= Stockpiles.MaxRackLevels; l++)
        {
            var mm = _racks[l].Multimesh;
            mm.InstanceCount = byLevels[l].Count;
            for (int i = 0; i < byLevels[l].Count; i++)
            {
                mm.SetInstanceTransform(i, byLevels[l][i]);
                mm.SetInstanceCustomData(i, new Color(1, 1, 1));
            }
        }

        // Runs: walk each tall rack back to the start of its row, then forward to its end (same front and height).
        var old = new Dictionary<(TilePos, int), float>();
        foreach (var r in _runs) old[(r.First, r.Facing)] = r.Shown;
        _runs.Clear();
        bool Same(TilePos c, Rack rack) => racks.TryGetValue(c, out var o) && o.Facing == rack.Facing && o.Levels == rack.Levels;
        foreach (var (p, rack) in racks)
        {
            if (rack.Levels < 2) continue;
            var (ax, ay) = Along(rack.Facing);
            if (Same(p.Offset(-ax, -ay, 0), rack)) continue; // not the start of its run
            int n = 1;
            while (Same(p.Offset(ax * n, ay * n, 0), rack)) n++;
            float at = old.GetValueOrDefault((p, rack.Facing), (n - 1) / 2f);
            _runs.Add(new Run { First = p, Length = n, Levels = rack.Levels, Facing = rack.Facing, Shown = at, Target = at });
        }
        _ladders.Multimesh.InstanceCount = _runs.Count;
        for (int i = 0; i < _runs.Count; i++) _ladders.Multimesh.SetInstanceCustomData(i, new Color(1, 1, 1));
    }

    /// <summary>Each run's ladder rolls towards the colonist nearest its front (within a tile and a half), else stays put.</summary>
    private void MoveLadders(float dt)
    {
        for (int i = 0; i < _runs.Count; i++)
        {
            var r = _runs[i];
            var (ax, ay) = Along(r.Facing);
            var (fx, fy) = Front(r.Facing);
            float best = 1.6f;
            foreach (var c in _sim.Colonists)
            {
                var pos = c.Position;
                if (pos.Z != r.First.Z) continue;
                // Along the run and out from its front, in tiles (the front cell row is at 1 out).
                float along = (pos.X - r.First.X) * ax + (pos.Y - r.First.Y) * ay;
                float outward = (pos.X - r.First.X) * fx + (pos.Y - r.First.Y) * fy;
                if (along < -1 || along > r.Length || outward is < 0 or > 2) continue;
                float d = MathF.Abs(outward - 1) + MathF.Max(0, -along) + MathF.Max(0, along - (r.Length - 1));
                if (d < best)
                {
                    best = d;
                    r.Target = Math.Clamp(along, 0, r.Length - 1);
                }
            }
            r.Shown = Mathf.MoveToward(r.Shown, r.Target, dt * 2.5f); // rolls at a walking pace
            var at = new Vector3(r.First.X + 0.5f + ax * r.Shown + fx * 0.56f, r.First.Z, r.First.Y + 0.5f + ay * r.Shown + fy * 0.56f);
            var basis = new Basis(Vector3.Up, Yaw(r.Facing)).Scaled(new Vector3(1, r.Levels - 0.04f, 1));
            _ladders.Multimesh.SetInstanceTransform(i, new Transform3D(basis, at));
        }
    }

    private void RebuildGhosts()
    {
        _drawnSites = _sim.Constructions.Version;
        var list = new List<Blueprint>();
        foreach (var bp in _sim.Constructions.All)
            if (bp.Kind == StructureKind.Rack) list.Add(bp);
        var mm = _ghosts.Multimesh;
        mm.InstanceCount = list.Count;
        for (int i = 0; i < list.Count; i++)
        {
            var bp = list[i];
            float h = Math.Max(1, bp.Material);
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(new Vector3(0.92f, h, 0.92f)), new Vector3(bp.Cell.X + 0.5f, bp.Cell.Z + h / 2, bp.Cell.Y + 0.5f)));
        }
    }
}
