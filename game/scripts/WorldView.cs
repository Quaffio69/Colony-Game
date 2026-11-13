using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Colony.Core;
using Colony.Core.Meshing;
using Colony.Core.Orders;
using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>
/// Renders a <see cref="VoxelWorld"/> as greedy-meshed sections of
/// <see cref="SectionX"/>x<see cref="SectionY"/>x<see cref="SectionZ"/> tiles.
/// <list type="bullet">
/// <item>Sections re-mesh only when a chunk they cover changes version. Snapshots are taken on the main
/// thread (cheap), meshing runs on the thread pool, and uploads are spread over frames under a time budget.</item>
/// <item>The view-level slice is a shader cut plus a texture-driven "cap" quad for the cut surface, so
/// changing level never re-meshes.</item>
/// </list>
/// Also draws dig/stair designations.
/// </summary>
public partial class WorldView : Node3D
{
    public const int SectionX = 32, SectionY = 32, SectionZ = 16;

    /// <summary>Main-thread milliseconds per frame for snapshotting and uploading meshes.</summary>
    [Export] public double MeshBudgetMs { get; set; } = 6.0;
    [Export] public int MaxInFlight { get; set; } = 128;

    private sealed class Section
    {
        public required int X0, Y0, Z0, SX, SY, SZ;
        public required Chunk[] Chunks;
        public int[] BuiltVersions = [];
        public bool InFlight;
        public MeshInstance3D? Instance;
        public int QuadCount;

        public bool IsDirty()
        {
            if (BuiltVersions.Length != Chunks.Length) return true;
            for (int i = 0; i < Chunks.Length; i++)
                if (Chunks[i].Version != BuiltVersions[i]) return true;
            return false;
        }

        public int[] CurrentVersions() => Array.ConvertAll(Chunks, c => c.Version);
    }

    private sealed record MeshResult(Section Section, int[] Versions, Vector3[] Verts, Vector3[] Normals, Color[] Colors, int[] Indices, int Quads);

    private ColonySim _sim = null!;
    private readonly List<Section> _sections = new();
    private readonly ConcurrentQueue<MeshResult> _results = new();
    private int _scanCursor;
    private int _inFlight;
    private ShaderMaterial _terrainMaterial = null!;

    // Cut-surface cap.
    private ShaderMaterial _capMaterial = null!;
    private MeshInstance3D _cap = null!;
    private Image _capImage = null!;
    private ImageTexture _capTexture = null!;
    private byte[] _capData = [];
    private readonly Dictionary<int, int> _capChunkVersions = new();
    private int _capLevel = -1;

    private readonly Dictionary<DesignationKind, MultiMeshInstance3D> _designationMarks = new();
    private int _designationsDrawnVersion = -1, _designationsDrawnLevel = -1;

    private int _viewLevel;
    private int _appliedViewLevel = -1;

    public VoxelWorld World => _sim.World;
    public int VisibleQuads { get; private set; }
    public int SectionCount => _sections.Count;
    /// <summary>Sections waiting to be (re)meshed or uploaded. Zero once the world is fully built.</summary>
    public int PendingSections { get; private set; }

    public int ViewLevel
    {
        get => _viewLevel;
        set => _viewLevel = Math.Clamp(value, 0, World.Height - 1);
    }

    public Vector3 Center => new(World.Width / 2f, World.Height * 0.4f, World.Depth / 2f);

    public static Color TileColor(TileType t) => t switch
    {
        TileType.Grass => new Color(0.36f, 0.62f, 0.28f),
        TileType.Soil => new Color(0.48f, 0.34f, 0.22f),
        TileType.Stone => new Color(0.52f, 0.52f, 0.55f),
        TileType.Ore => new Color(0.80f, 0.55f, 0.25f),
        TileType.Stairs => new Color(0.70f, 0.62f, 0.48f),
        _ => Colors.Magenta,
    };

    public static Color DesignationColor(DesignationKind kind) => kind switch
    {
        DesignationKind.CarveStairs => new Color(0.3f, 0.85f, 1f, 0.45f),
        _ => new Color(1f, 0.85f, 0.2f, 0.45f),
    };

    public void Initialize(ColonySim sim)
    {
        _sim = sim;
        _viewLevel = World.Height - 1;

        _terrainMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };

        for (int z0 = 0; z0 < World.Height; z0 += SectionZ)
        for (int y0 = 0; y0 < World.Depth; y0 += SectionY)
        for (int x0 = 0; x0 < World.Width; x0 += SectionX)
        {
            var chunks = new List<Chunk>();
            for (int cz = z0 >> Chunk.Shift; cz <= (Math.Min(z0 + SectionZ, World.Height) - 1) >> Chunk.Shift; cz++)
            for (int cy = y0 >> Chunk.Shift; cy <= (Math.Min(y0 + SectionY, World.Depth) - 1) >> Chunk.Shift; cy++)
            for (int cx = x0 >> Chunk.Shift; cx <= (Math.Min(x0 + SectionX, World.Width) - 1) >> Chunk.Shift; cx++)
                chunks.Add(World.GetChunk(cx, cy, cz)!);

            _sections.Add(new Section
            {
                X0 = x0, Y0 = y0, Z0 = z0,
                SX = Math.Min(SectionX, World.Width - x0),
                SY = Math.Min(SectionY, World.Depth - y0),
                SZ = Math.Min(SectionZ, World.Height - z0),
                Chunks = chunks.ToArray(),
            });
        }

        BuildCap();

        foreach (var kind in Enum.GetValues<DesignationKind>())
        {
            var marks = new MultiMeshInstance3D
            {
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Multimesh = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    Mesh = new BoxMesh { Size = Vector3.One * 1.04f, Material = Overlay.Material(DesignationColor(kind)) },
                },
            };
            AddChild(marks);
            _designationMarks[kind] = marks;
        }
    }

    public override void _Process(double delta)
    {
        if (_sim == null) return;

        if (_appliedViewLevel != _viewLevel) ApplyViewLevel();

        var clock = Stopwatch.StartNew();
        ApplyResults(clock);
        ScheduleDirty(clock);
        UpdateCap();

        var designations = _sim.Designations;
        if (designations.Version != _designationsDrawnVersion || _viewLevel != _designationsDrawnLevel)
            RedrawDesignations();
    }

    // ---- Sections ------------------------------------------------------------------------------------

    private void ApplyViewLevel()
    {
        _appliedViewLevel = _viewLevel;
        _terrainMaterial.SetShaderParameter("cut_height", (float)(_viewLevel + 1));
        foreach (var s in _sections)
            if (s.Instance != null) s.Instance.Visible = s.Z0 <= _viewLevel;
        RebuildCapFull();
    }

    /// <summary>Snapshots dirty sections and hands them to worker threads, within the frame budget.</summary>
    private void ScheduleDirty(Stopwatch clock)
    {
        int pending = 0;
        int n = _sections.Count, start = _scanCursor;
        for (int step = 0; step < n; step++)
        {
            var s = _sections[(start + step) % n];
            if (s.InFlight) { pending++; continue; }
            if (!s.IsDirty()) continue;

            // Nothing solid at all: no geometry, no snapshot needed.
            bool empty = true;
            foreach (var c in s.Chunks) empty &= c.SolidCount == 0;
            if (empty)
            {
                s.BuiltVersions = s.CurrentVersions();
                SetMesh(s, null, 0);
                continue;
            }

            pending++;
            if (_inFlight >= MaxInFlight || clock.Elapsed.TotalMilliseconds > MeshBudgetMs) continue;

            var versions = s.CurrentVersions();
            var box = TileBox.Capture(World, s.X0 - SectionMesher.Pad, s.Y0 - SectionMesher.Pad, s.Z0 - SectionMesher.Pad,
                s.SX + 2 * SectionMesher.Pad, s.SY + 2 * SectionMesher.Pad, s.SZ + 2 * SectionMesher.Pad);
            s.InFlight = true;
            _inFlight++;
            Task.Run(() => _results.Enqueue(BuildMesh(s, versions, box)));
            _scanCursor = (start + step + 1) % n; // next frame resumes after the last scheduled section
        }
        PendingSections = pending + _results.Count;
    }

    private static MeshResult BuildMesh(Section s, int[] versions, TileBox box)
    {
        var quads = new List<Quad>();
        SectionMesher.Build(box, s.X0, s.Y0, s.Z0, s.SX, s.SY, s.SZ, quads);

        int n = quads.Count;
        var verts = new Vector3[n * 4];
        var normals = new Vector3[n * 4];
        var colors = new Color[n * 4];
        var indices = new int[n * 6];
        for (int i = 0; i < n; i++)
        {
            var q = quads[i];
            var (nx, ny, nz) = SectionMesher.Normal(q.Dir);
            var normal = SimSpace.ToGodot(nx, ny, nz);
            var color = TileColor(q.Tile);
            int v = i * 4;
            verts[v] = SimSpace.ToGodot(q.A.X, q.A.Y, q.A.Z);
            verts[v + 1] = SimSpace.ToGodot(q.B.X, q.B.Y, q.B.Z);
            verts[v + 2] = SimSpace.ToGodot(q.C.X, q.C.Y, q.C.Z);
            verts[v + 3] = SimSpace.ToGodot(q.D.X, q.D.Y, q.D.Z);
            for (int k = 0; k < 4; k++)
            {
                normals[v + k] = normal;
                colors[v + k] = color;
            }
            int t = i * 6;
            indices[t] = v; indices[t + 1] = v + 1; indices[t + 2] = v + 2;
            indices[t + 3] = v; indices[t + 4] = v + 2; indices[t + 5] = v + 3;
        }
        return new MeshResult(s, versions, verts, normals, colors, indices, n);
    }

    private void ApplyResults(Stopwatch clock)
    {
        while (clock.Elapsed.TotalMilliseconds < MeshBudgetMs && _results.TryDequeue(out var r))
        {
            var s = r.Section;
            s.InFlight = false;
            _inFlight--;
            s.BuiltVersions = r.Versions; // if the world moved on meanwhile, IsDirty() will requeue it

            if (r.Quads == 0)
            {
                SetMesh(s, null, 0);
                continue;
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = r.Verts;
            arrays[(int)Mesh.ArrayType.Normal] = r.Normals;
            arrays[(int)Mesh.ArrayType.Color] = r.Colors;
            arrays[(int)Mesh.ArrayType.Index] = r.Indices;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            SetMesh(s, mesh, r.Quads);
        }
    }

    private void SetMesh(Section s, ArrayMesh? mesh, int quads)
    {
        VisibleQuads += quads - s.QuadCount;
        s.QuadCount = quads;
        if (mesh == null)
        {
            if (s.Instance != null) s.Instance.Mesh = null;
            return;
        }
        if (s.Instance == null)
        {
            s.Instance = new MeshInstance3D { MaterialOverride = _terrainMaterial };
            AddChild(s.Instance);
        }
        s.Instance.Mesh = mesh;
        s.Instance.Visible = s.Z0 <= _viewLevel;
    }

    // ---- Cut-surface cap -----------------------------------------------------------------------------

    private void BuildCap()
    {
        _capData = new byte[World.Width * World.Depth];
        _capImage = Image.CreateFromData(World.Width, World.Depth, false, Image.Format.R8, _capData);
        _capTexture = ImageTexture.CreateFromImage(_capImage);

        var palette = new Vector3[8];
        for (int i = 0; i < TileInfo.Count; i++)
        {
            var c = TileColor((TileType)i);
            palette[i] = new Vector3(c.R, c.G, c.B);
        }

        _capMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/cap.gdshader") };
        _capMaterial.SetShaderParameter("tiles", _capTexture);
        _capMaterial.SetShaderParameter("palette", palette);

        _cap = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(World.Width, World.Depth) },
            MaterialOverride = _capMaterial,
        };
        AddChild(_cap);
    }

    /// <summary>Cap texel = tile type at the view level where the tile above is also solid (its top is hidden by the cut).</summary>
    private void RebuildCapFull()
    {
        int level = _viewLevel;
        _capLevel = level;
        _cap.Position = new Vector3(World.Width / 2f, level + 1, World.Depth / 2f);
        _capMaterial.SetShaderParameter("level", (float)level);

        var layers = new TileType[World.Width * World.Depth * 2];
        World.CopyBox(0, 0, level, World.Width, World.Depth, 2, layers);
        int plane = World.Width * World.Depth;
        for (int i = 0; i < plane; i++)
            _capData[i] = layers[i] != TileType.Air && layers[plane + i] != TileType.Air ? (byte)layers[i] : (byte)0;

        _capChunkVersions.Clear();
        foreach (var (key, chunk) in CapChunks()) _capChunkVersions[key] = chunk.Version;
        UploadCap();
    }

    /// <summary>Patches the cap where chunks on the view level (or the one above it) changed.</summary>
    private void UpdateCap()
    {
        bool changed = false;
        foreach (var (key, chunk) in CapChunks())
        {
            if (_capChunkVersions.TryGetValue(key, out int v) && v == chunk.Version) continue;
            _capChunkVersions[key] = chunk.Version;

            int x0 = chunk.Origin.X, y0 = chunk.Origin.Y;
            int sx = Math.Min(Chunk.Size, World.Width - x0), sy = Math.Min(Chunk.Size, World.Depth - y0);
            var layers = new TileType[sx * sy * 2];
            World.CopyBox(x0, y0, _capLevel, sx, sy, 2, layers);
            for (int y = 0; y < sy; y++)
            for (int x = 0; x < sx; x++)
            {
                var t = layers[y * sx + x];
                var above = layers[sx * sy + y * sx + x];
                _capData[(y0 + y) * World.Width + x0 + x] = t != TileType.Air && above != TileType.Air ? (byte)t : (byte)0;
            }
            changed = true;
        }
        if (changed) UploadCap();
    }

    private IEnumerable<(int Key, Chunk Chunk)> CapChunks()
    {
        int czLo = _capLevel >> Chunk.Shift, czHi = Math.Min(World.ChunksZ - 1, (_capLevel + 1) >> Chunk.Shift);
        for (int cz = czLo; cz <= czHi; cz++)
        for (int cy = 0; cy < World.ChunksY; cy++)
        for (int cx = 0; cx < World.ChunksX; cx++)
            yield return (World.ChunkIndex(cx, cy, cz), World.GetChunk(cx, cy, cz)!);
    }

    private void UploadCap()
    {
        _capImage.SetData(World.Width, World.Depth, false, Image.Format.R8, _capData);
        _capTexture.Update(_capImage);
    }

    // ---- Designations --------------------------------------------------------------------------------

    private void RedrawDesignations()
    {
        _designationsDrawnVersion = _sim.Designations.Version;
        _designationsDrawnLevel = _viewLevel;

        foreach (var (kind, marks) in _designationMarks)
        {
            var visible = new List<TilePos>();
            foreach (var (p, k) in _sim.Designations.All)
                if (k == kind && p.Z <= _viewLevel) visible.Add(p);

            var mm = marks.Multimesh;
            mm.InstanceCount = visible.Count;
            for (int i = 0; i < visible.Count; i++)
                mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, SimSpace.TileCenter(visible[i])));
        }
    }
}

/// <summary>Shared look for translucent, unlit overlay markers.</summary>
public static class Overlay
{
    public static StandardMaterial3D Material(Color color) => new()
    {
        AlbedoColor = color,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
}
