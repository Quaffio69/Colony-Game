using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
/// <item>Far out (<see cref="FarView"/>: one art pixel covers several tiles) the sections give way to far blocks: a
/// coarse heightfield of ground and standing water per <see cref="FarBlock"/>² columns (<see cref="FarMesher"/>), a few
/// dozen draws for the whole map instead of ten thousand. They are built in the background once the world is meshed,
/// rebuilt (throttled) where chunks change and refreshed in turn for the water; the switch waits until all exist.</item>
/// </list>
/// Also draws dig/stair designations.
/// </summary>
public partial class WorldView : Node3D
{
    public const int SectionX = 32, SectionY = 32, SectionZ = 16;

    /// <summary>Main-thread milliseconds per frame for snapshotting and uploading meshes.</summary>
    [Export] public double MeshBudgetMs { get; set; } = 6.0;
    [Export] public int MaxInFlight { get; set; } = 128;
    /// <summary>Budget while more than <see cref="LoadingBacklog"/> sections are pending (startup).</summary>
    [Export] public double LoadingBudgetMs { get; set; } = 40.0;
    private const int LoadingBacklog = 2000;
    private double _budgetMs = 6.0;
    private int _maxInFlight = 128;

    private sealed class Section
    {
        public required int Index, X0, Y0, Z0, SX, SY, SZ;
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

    private sealed record MeshResult(Section Section, int[] Versions, MeshArrays Arrays);

    /// <summary>Vertex arrays for a list of quads (built on a worker, uploaded on the main thread).</summary>
    private sealed record MeshArrays(Vector3[] Verts, Vector3[] Normals, float[] Tangents, Color[] Colors, Vector2[] Uvs, int[] Indices, int Quads)
    {
        public ArrayMesh ToMesh()
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = Verts;
            arrays[(int)Mesh.ArrayType.Normal] = Normals;
            arrays[(int)Mesh.ArrayType.Tangent] = Tangents;
            arrays[(int)Mesh.ArrayType.Color] = Colors;
            arrays[(int)Mesh.ArrayType.TexUV] = Uvs;
            arrays[(int)Mesh.ArrayType.Index] = Indices;
            var mesh = new ArrayMesh();
            // Compressed: positions 16-bit within the mesh bounds, normal+tangent and UVs 16-bit (Godot needs tangents for this).
            // Roughly halves terrain and far-block video memory.
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, flags: Mesh.ArrayFormat.FlagCompressAttributes);
            return mesh;
        }
    }

    private ColonySim _sim = null!;
    private readonly List<Section> _sections = new();
    private readonly ConcurrentQueue<MeshResult> _results = new();
    // Sections waiting to be checked/meshed, fed by VoxelWorld.ChunkChanged: no per-frame scans of every section.
    private readonly Queue<int> _dirtyQueue = new();
    private bool[] _queued = [];
    private int _sectionsX, _sectionsY;
    private readonly HashSet<int> _capDirty = new(); // changed chunks on the cap's two levels
    private int _inFlight;
    private ShaderMaterial _terrainMaterial = null!;

    // Cut-surface cap.
    private ShaderMaterial _capMaterial = null!;
    private MeshInstance3D _cap = null!;
    private Image _capImage = null!;
    private ImageTexture _capTexture = null!;
    private byte[] _capData = [];
    private int _capLevel = -1;

    private readonly Dictionary<DesignationKind, MultiMeshInstance3D> _designationMarks = new();
    private int _designationsDrawnVersion = -1, _designationsDrawnLevel = -1;

    private int _viewLevel;
    private int _appliedViewLevel = -1;

    // Far view.
    public const int FarBlock = 256, FarCell = 2;
    private sealed class FarBlockState
    {
        public required int X0, Y0;
        public MeshInstance3D? Node;
        public bool Built, Dirty = true, InFlight;
        public bool Sampled;
        public float Top; // highest sampled top (ring included): the iso camera stands back just far enough to clear it
        public double BuiltAt = -1e9;
    }
    private sealed record FarResult(FarBlockState Block, MeshArrays? Arrays);
    private readonly List<FarBlockState> _farBlocks = new();
    private readonly ConcurrentQueue<FarResult> _farResults = new();
    private int _farBlocksX, _farBuilt, _farInFlight, _farRefresh;
    private double _now, _farSampledAt;
    private Node3D _nearRoot = null!, _farRoot = null!;
    private bool _farWanted, _farShown;
    /// <summary>Far blocks drawn instead of sections (set each frame by Main; takes effect once every far block exists).</summary>
    public bool FarView { get => _farShown; set => _farWanted = value; }
    /// <summary>Main-thread ms spent sampling far blocks (diagnostics).</summary>
    public double FarSampleMs;

    public VoxelWorld World => _sim.World;
    public int VisibleQuads { get; private set; }
    /// <summary>Startup diagnostics: main-thread ms spent snapshotting / uploading, worker ms spent meshing.</summary>
    public double SnapshotMs, UploadMs;
    private long _workerTicks;
    public double WorkerMs => _workerTicks * 1000.0 / Stopwatch.Frequency;
    public int SectionCount => _sections.Count;
    /// <summary>Sections waiting to be (re)meshed or uploaded. Zero once the world is fully built.</summary>
    public int PendingSections { get; private set; }

    public int ViewLevel
    {
        get => _viewLevel;
        set => _viewLevel = Math.Clamp(value, 0, World.Height - 1);
    }

    public Vector3 Center => new(World.Width / 2f, World.Height * 0.4f, World.Depth / 2f);

    /// <summary>The kit look: tile tables and the shared atlas/lighting values for a tile material (no-op when classic).</summary>
    private static void StyleTiles(ShaderMaterial m)
    {
        if (!Main.StyledRender) return;
        var (palette, groups, capGroups) = GameLooks.TileTables();
        m.SetShaderParameter("dr_cap_group", capGroups);
        m.SetShaderParameter("dr_palette", palette);
        m.SetShaderParameter("dr_group", groups);
        GameLooks.Styled(m);
    }

    public static Color TileColor(TileType t) => t switch
    {
        TileType.Grass => new Color(0.36f, 0.62f, 0.28f),
        TileType.Soil => new Color(0.48f, 0.34f, 0.22f),
        TileType.Stone => new Color(0.52f, 0.52f, 0.55f),
        TileType.CopperOre => new Color(0.76f, 0.47f, 0.27f),
        TileType.Clay => new Color(0.69f, 0.45f, 0.32f),
        TileType.Sand => new Color(0.86f, 0.79f, 0.55f),
        TileType.TinOre => new Color(0.66f, 0.70f, 0.71f),
        TileType.IronOre => new Color(0.55f, 0.33f, 0.27f),
        TileType.SilverOre => new Color(0.80f, 0.82f, 0.88f),
        TileType.GoldOre => new Color(0.90f, 0.75f, 0.25f),
        TileType.Quartz => new Color(0.93f, 0.92f, 0.97f),
        TileType.Sulfur => new Color(0.88f, 0.85f, 0.25f),
        TileType.Aetherite => new Color(0.36f, 0.88f, 0.93f, 0.2f), // alpha < 1: glows (see tiles.gdshaderinc)
        TileType.Wall => new Color(0.66f, 0.63f, 0.58f),
        TileType.UpStair => new Color(0.74f, 0.64f, 0.46f),
        TileType.DownStair => new Color(0.62f, 0.54f, 0.42f),
        TileType.UpDownStair => new Color(0.80f, 0.72f, 0.56f),
        FarMesher.Water => new Color(0.24f, 0.46f, 0.62f), // standing water in far blocks
        _ => Colors.Magenta,
    };

    public static Color DesignationColor(DesignationKind kind) => kind switch
    {
        DesignationKind.UpStair => new Color(0.3f, 1f, 0.55f, 0.45f),
        DesignationKind.DownStair => new Color(0.3f, 0.55f, 1f, 0.45f),
        DesignationKind.UpDownStair => new Color(0.3f, 0.85f, 1f, 0.45f),
        DesignationKind.Chop => new Color(0.45f, 1f, 0.25f, 0.4f),
        DesignationKind.Gather => new Color(1f, 0.4f, 0.75f, 0.4f),
        DesignationKind.Sow => new Color(0.7f, 0.55f, 0.3f, 0.35f),
        _ => new Color(1f, 0.85f, 0.2f, 0.45f),
    };

    public void Initialize(ColonySim sim)
    {
        _sim = sim;
        _viewLevel = World.Height - 1;

        _terrainMaterial = new ShaderMaterial { Shader = GameLooks.Shader("res://shaders/terrain.gdshader") };
        StyleTiles(_terrainMaterial);
        _nearRoot = new Node3D { Name = "Near" };
        _farRoot = new Node3D { Name = "Far" }; // blocks show by range (FarRange), or all of them in the far view
        AddChild(_nearRoot);
        AddChild(_farRoot);
        _nearRoot.AddToGroup(GameLooks.SweepSkip); // built with the terrain material: nothing for the look's sweep to convert
        _farRoot.AddToGroup(GameLooks.SweepSkip);
        _farBlocksX = (World.Width + FarBlock - 1) / FarBlock;
        for (int y0 = 0; y0 < World.Depth; y0 += FarBlock)
        for (int x0 = 0; x0 < World.Width; x0 += FarBlock)
            _farBlocks.Add(new FarBlockState { X0 = x0, Y0 = y0 });

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
                Index = _sections.Count,
                X0 = x0, Y0 = y0, Z0 = z0,
                SX = Math.Min(SectionX, World.Width - x0),
                SY = Math.Min(SectionY, World.Depth - y0),
                SZ = Math.Min(SectionZ, World.Height - z0),
                Chunks = chunks.ToArray(),
            });
        }

        _sectionsX = (World.Width + SectionX - 1) / SectionX;
        _sectionsY = (World.Depth + SectionY - 1) / SectionY;
        _queued = new bool[_sections.Count];
        for (int i = 0; i < _sections.Count; i++) Enqueue(i);
        World.ChunkChanged += OnChunkChanged;

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
        // While the initial world (or a huge change) is still being built, frames are a loading screen anyway:
        // spend most of each one meshing instead of trickling sections in.
        bool loading = PendingSections > LoadingBacklog;
        _budgetMs = loading ? LoadingBudgetMs : MeshBudgetMs;
        _maxInFlight = loading ? MaxInFlight * 8 : MaxInFlight;
        ApplyResults(clock);
        ScheduleDirty(clock);
        UpdateFar(delta);
        if (_capDirty.Count > 0) UpdateCap();

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
            if (s.Instance != null && s.Instance.Visible != (s.Z0 <= _viewLevel))
            {
                s.Instance.Visible = s.Z0 <= _viewLevel;
                LinkFar(s);
            }
        RebuildCapFull();
    }

    private void Enqueue(int section)
    {
        if (_queued[section]) return;
        _queued[section] = true;
        _dirtyQueue.Enqueue(section);
    }

    /// <summary>A chunk's tiles (or a neighbour's border tiles) changed: requeue its section, patch the cap.</summary>
    // The test menu reloads the scene onto another sim and may bring this one back later: let go of its world.
    public override void _ExitTree()
    {
        if (_sim != null) World.ChunkChanged -= OnChunkChanged;
    }

    private void OnChunkChanged(int chunkIndex)
    {
        var c = World.Chunks[chunkIndex].Coord;
        int sx = (c.X << Chunk.Shift) / SectionX, sy = (c.Y << Chunk.Shift) / SectionY, sz = (c.Z << Chunk.Shift) / SectionZ;
        Enqueue((sz * _sectionsY + sy) * _sectionsX + sx);
        _farBlocks[((c.Y << Chunk.Shift) / FarBlock) * _farBlocksX + (c.X << Chunk.Shift) / FarBlock].Dirty = true;
        if (c.Z >= _capLevel >> Chunk.Shift && c.Z <= (_capLevel + 1) >> Chunk.Shift) _capDirty.Add(chunkIndex);
    }

    /// <summary>Snapshots queued sections and hands them to worker threads, within the frame budget.</summary>
    private void ScheduleDirty(Stopwatch clock)
    {
        while (_dirtyQueue.TryPeek(out int index))
        {
            var s = _sections[index];
            // In flight: its result's arrival re-checks it. Clean: nothing to do.
            if (s.InFlight || !s.IsDirty())
            {
                Dequeue();
                continue;
            }

            // Nothing solid at all, or solid rock buried in solid rock: no geometry, no snapshot needed.
            bool empty = true;
            foreach (var c in s.Chunks) empty &= c.NonAirCount == 0;
            if (empty || IsBuried(s))
            {
                Dequeue();
                s.BuiltVersions = s.CurrentVersions();
                SetMesh(s, null, 0);
                continue;
            }

            if (_inFlight >= _maxInFlight || clock.Elapsed.TotalMilliseconds > _budgetMs) break; // next frame
            Dequeue();
            long t0 = Stopwatch.GetTimestamp();
            var versions = s.CurrentVersions();
            var box = TileBox.Capture(World, s.X0 - SectionMesher.Pad, s.Y0 - SectionMesher.Pad, s.Z0 - SectionMesher.Pad,
                s.SX + 2 * SectionMesher.Pad, s.SY + 2 * SectionMesher.Pad, s.SZ + 2 * SectionMesher.Pad);
            s.InFlight = true;
            _inFlight++;
            SnapshotMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Task.Run(() =>
            {
                long w0 = Stopwatch.GetTimestamp();
                var result = BuildMesh(s, versions, box);
                Interlocked.Add(ref _workerTicks, Stopwatch.GetTimestamp() - w0);
                _results.Enqueue(result);
            });
        }
        PendingSections = _dirtyQueue.Count + _inFlight + _results.Count;

        void Dequeue() => _queued[_dirtyQueue.Dequeue()] = false;
    }

    /// <summary>
    /// True if the section and a one-tile shell around it are all solid, inside the world (the world's sides
    /// and top are drawn, its underside never is), so it can have no visible faces. O(chunks), no tile reads.
    /// </summary>
    private bool IsBuried(Section s)
    {
        if (s.X0 == 0 || s.Y0 == 0 || s.X0 + s.SX >= World.Width || s.Y0 + s.SY >= World.Depth ||
            s.Z0 + s.SZ >= World.Height) return false;
        int cx0 = (s.X0 - 1) >> Chunk.Shift, cx1 = (s.X0 + s.SX) >> Chunk.Shift;
        int cy0 = (s.Y0 - 1) >> Chunk.Shift, cy1 = (s.Y0 + s.SY) >> Chunk.Shift;
        int cz0 = Math.Max(0, s.Z0 - 1) >> Chunk.Shift, cz1 = (s.Z0 + s.SZ) >> Chunk.Shift;
        for (int cz = cz0; cz <= cz1; cz++)
        for (int cy = cy0; cy <= cy1; cy++)
        for (int cx = cx0; cx <= cx1; cx++)
            if (World.GetChunk(cx, cy, cz) is not { SolidCount: Chunk.Volume }) return false;
        return true;
    }

    private static MeshResult BuildMesh(Section s, int[] versions, TileBox box)
    {
        var quads = new List<Quad>();
        SectionMesher.Build(box, s.X0, s.Y0, s.Z0, s.SX, s.SY, s.SZ, quads);
        return new MeshResult(s, versions, ToArrays(quads));
    }

    private static MeshArrays ToArrays(List<Quad> quads)
    {
        int n = quads.Count;
        var verts = new Vector3[n * 4];
        var normals = new Vector3[n * 4];
        var tangents = new float[n * 16];
        var colors = new Color[n * 4];
        var uvs = new Vector2[n * 4]; // x = tile type (the styled terrain shader looks its texture and glyph up by it)
        var indices = new int[n * 6];
        for (int i = 0; i < n; i++)
        {
            var q = quads[i];
            var (nx, ny, nz) = SectionMesher.Normal(q.Dir);
            var normal = SimSpace.ToGodot(nx, ny, nz);
            var tangent = nx != 0 ? new Vector3(0, 0, 1) : new Vector3(1, 0, 0); // any direction in the face
            var color = TileColor(q.Tile);
            int v = i * 4;
            verts[v] = SimSpace.ToGodot(q.A.X, q.A.Y, q.A.Z);
            verts[v + 1] = SimSpace.ToGodot(q.B.X, q.B.Y, q.B.Z);
            verts[v + 2] = SimSpace.ToGodot(q.C.X, q.C.Y, q.C.Z);
            verts[v + 3] = SimSpace.ToGodot(q.D.X, q.D.Y, q.D.Z);
            for (int k = 0; k < 4; k++)
            {
                normals[v + k] = normal;
                tangents[(v + k) * 4] = tangent.X; tangents[(v + k) * 4 + 1] = tangent.Y; tangents[(v + k) * 4 + 2] = tangent.Z; tangents[(v + k) * 4 + 3] = 1f;
                colors[v + k] = color;
                uvs[v + k] = new Vector2((int)q.Tile, 0);
            }
            int t = i * 6;
            indices[t] = v; indices[t + 1] = v + 1; indices[t + 2] = v + 2;
            indices[t + 3] = v; indices[t + 4] = v + 2; indices[t + 5] = v + 3;
        }
        return new MeshArrays(verts, normals, tangents, colors, uvs, indices, n);
    }

    private void ApplyResults(Stopwatch clock)
    {
        long u0 = Stopwatch.GetTimestamp();
        while (clock.Elapsed.TotalMilliseconds < _budgetMs && _results.TryDequeue(out var r))
        {
            var s = r.Section;
            s.InFlight = false;
            _inFlight--;
            s.BuiltVersions = r.Versions;
            if (s.IsDirty()) Enqueue(s.Index); // the world moved on while it was being meshed

            if (r.Arrays.Quads == 0)
            {
                SetMesh(s, null, 0);
                continue;
            }
            SetMesh(s, r.Arrays.ToMesh(), r.Arrays.Quads);
        }
        UploadMs += (Stopwatch.GetTimestamp() - u0) * 1000.0 / Stopwatch.Frequency;
    }

    private void SetMesh(Section s, ArrayMesh? mesh, int quads)
    {
        VisibleQuads += quads - s.QuadCount;
        s.QuadCount = quads;
        if (mesh == null)
        {
            if (s.Instance != null) { s.Instance.VisibilityParent = new NodePath(); s.Instance.Mesh = null; } // see LinkFar
            return;
        }
        if (s.Instance == null)
        {
            s.Instance = new MeshInstance3D { MaterialOverride = _terrainMaterial };
            _nearRoot.AddChild(s.Instance);
        }
        s.Instance.Mesh = mesh;
        s.Instance.Visible = s.Z0 <= _viewLevel;
        LinkFar(s);
    }

    // ---- Far view ------------------------------------------------------------------------------------

    /// <summary>
    /// Far blocks: all built in the background once the near world is meshed (or as soon as the far view is wanted),
    /// before the first switch. While the far view shows, changed blocks rebuild at most every 2 s each, and one block
    /// every half second is refreshed in turn (water moves without chunk changes). Sampling reads the live world and
    /// water, so it runs on the main thread (a couple of ms a block); meshing runs on workers.
    /// </summary>
    private void UpdateFar(double delta)
    {
        _now += delta;
        while (_farResults.TryDequeue(out var r))
        {
            var b = r.Block;
            b.InFlight = false;
            _farInFlight--;
            if (!b.Built) { b.Built = true; _farBuilt++; }
            if (r.Arrays is not { Quads: > 0 } arrays)
            {
                if (b.Node != null) b.Node.Mesh = null;
                continue;
            }
            if (b.Node == null)
            {
                b.Node = new MeshInstance3D { Name = $"Far_{b.X0}_{b.Y0}", MaterialOverride = _terrainMaterial };
                _farRoot.AddChild(b.Node);
                b.Node.VisibilityRangeBegin = _farShown ? AlwaysNear : _farRange;
                LinkSections(b);
                FarBlockAdded?.Invoke(b.X0, b.Y0, b.Node);
            }
            b.Node.Mesh = arrays.ToMesh();
        }
        bool all = _farBuilt == _farBlocks.Count;
        bool show = _farWanted && all;
        if (show != _farShown)
        {
            _farShown = show; // the sections hide by visibility parent (hiding their root upsets Godot's dependency lists)
            ApplyFarRanges();
        }

        if (!_sim.WaterReady || (PendingSections > 0 && !_farWanted)) return; // the near world first
        if (_farShown && _now - _farSampledAt > 0.5)
        {
            _farSampledAt = _now;
            var turn = _farBlocks[_farRefresh++ % _farBlocks.Count];
            turn.Dirty = true;
            turn.BuiltAt = -1e9;
        }
        var clock = Stopwatch.StartNew();
        double budget = _farWanted && !all ? 8.0 : 1.5;
        foreach (var b in _farBlocks)
        {
            if (clock.Elapsed.TotalMilliseconds > budget) break;
            if (b.InFlight || !b.Dirty || (b.Built && (!_farShown || _now - b.BuiltAt < 2.0))) continue;
            b.Dirty = false;
            b.InFlight = true;
            _farInFlight++;
            b.BuiltAt = _now;
            int cx = Math.Min(FarBlock, World.Width - b.X0 + FarCell - 1) / FarCell;
            int cy = Math.Min(FarBlock, World.Depth - b.Y0 + FarCell - 1) / FarCell;
            var heights = new float[(cx + 2) * (cy + 2)];
            var types = new TileType[heights.Length];
            long t0 = Stopwatch.GetTimestamp();
            FarMesher.Sample(World, _sim.Water, b.X0, b.Y0, cx, cy, FarCell, heights, types);
            float top = 0;
            foreach (float hgt in heights) top = Math.Max(top, hgt);
            b.Top = top;
            b.Sampled = true;
            FarSampleMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Task.Run(() =>
            {
                var quads = new List<Quad>();
                FarMesher.Build(heights, types, cx, cy, b.X0, b.Y0, FarCell, quads);
                _farResults.Enqueue(new FarResult(b, quads.Count > 0 ? ToArrays(quads) : null));
            });
        }
    }

    /// <summary>Far blocks built so far, of all (loading progress, diagnostics).</summary>
    public (int Built, int Total) FarProgress => (_farBuilt, _farBlocks.Count);

    // ---- Far blocks as level of detail (perspective) ------------------------------------------------------
    // A far block is the visibility parent of the sections (and water sections) on its columns: beyond FarRange from the
    // camera Godot shows the block and hides them. In perspective that draws the horizon coarse and the near ground in
    // full; in iso every block is the same scale, so the range is off and the whole-view switch (FarView) does it.
    private float _farRange = 1e9f;

    /// <summary>Camera distance beyond which a block draws coarse (1e9: never). Set each frame by Main; applied on a 5% change.</summary>
    public float FarRange
    {
        get => _farRange;
        set
        {
            if (Mathf.Abs(value - _farRange) <= _farRange * 0.05f) return;
            _farRange = value;
            ApplyFarRanges();
        }
    }

    /// <summary>Blocks begin at FarRange; in the whole-view far view at once (a range that starts at the camera: every block
    /// in range, so every section hidden; 0 would switch the range off).</summary>
    private void ApplyFarRanges()
    {
        foreach (var b in _farBlocks) if (b.Node != null) b.Node.VisibilityRangeBegin = _farShown ? AlwaysNear : _farRange;
    }

    private const float AlwaysNear = 0.001f;

    /// <summary>A far block's node was created (WaterView links its sections there).</summary>
    public event Action<int, int, MeshInstance3D>? FarBlockAdded;

    private FarBlockState FarBlockAt(int x, int y) => _farBlocks[(y / FarBlock) * _farBlocksX + x / FarBlock];

    /// <summary>
    /// The highest terrain top (Godot Y) over blocks within <paramref name="radius"/> of (x, z): sampled per far block, so
    /// edits show after the block's rebuild (a couple of seconds). The world's height until every block there is sampled.
    /// </summary>
    public float TopAround(float x, float z, float radius)
    {
        int bx0 = Math.Max(0, (int)((x - radius) / FarBlock)), bx1 = Math.Min(_farBlocksX - 1, (int)((x + radius) / FarBlock));
        int by0 = Math.Max(0, (int)((z - radius) / FarBlock)), by1 = Math.Min(_farBlocks.Count / _farBlocksX - 1, (int)((z + radius) / FarBlock));
        float top = 0;
        for (int by = by0; by <= by1; by++)
        for (int bx = bx0; bx <= bx1; bx++)
        {
            var b = _farBlocks[by * _farBlocksX + bx];
            if (!b.Sampled) return World.Height;
            top = Math.Max(top, b.Top);
        }
        return top;
    }

    /// <summary>The far block node over a column, to parent other views' nodes to (null until built).</summary>
    public MeshInstance3D? FarParent(int x, int y) => x >= 0 && y >= 0 && x < World.Width && y < World.Depth ? FarBlockAt(x, y).Node : null;

    private void LinkSections(FarBlockState b)
    {
        for (int sz = 0; sz * SectionZ < World.Height; sz++)
        for (int sy = b.Y0 / SectionY; sy < Math.Min(_sectionsY, (b.Y0 + FarBlock) / SectionY); sy++)
        for (int sx = b.X0 / SectionX; sx < Math.Min(_sectionsX, (b.X0 + FarBlock) / SectionX); sx++)
            LinkFar(_sections[(sz * _sectionsY + sy) * _sectionsX + sx]);
    }

    /// <summary>
    /// A section hides where its far block shows (visibility parent) only while it is drawn: Godot errors on a
    /// dependency whose instance has no mesh or is hidden (sections above the view level are).
    /// </summary>
    private void LinkFar(Section s)
    {
        if (s.Instance == null) return;
        bool drawn = s.Instance.Mesh != null && s.Instance.Visible;
        var path = drawn && FarBlockAt(s.X0, s.Y0).Node is { } block ? block.GetPath() : new NodePath();
        if (s.Instance.VisibilityParent.ToString() != path.ToString()) s.Instance.VisibilityParent = path;
    }

    // ---- Cut-surface cap -----------------------------------------------------------------------------

    private void BuildCap()
    {
        _capData = new byte[World.Width * World.Depth];
        _capImage = Image.CreateFromData(World.Width, World.Depth, false, Image.Format.R8, _capData);
        _capTexture = ImageTexture.CreateFromImage(_capImage);

        var palette = new Vector4[32];
        for (int i = 0; i < TileInfo.Count; i++)
        {
            var c = TileColor((TileType)i);
            palette[i] = new Vector4(c.R, c.G, c.B, c.A);
        }

        _capMaterial = new ShaderMaterial { Shader = GameLooks.Shader("res://shaders/cap.gdshader") };
        StyleTiles(_capMaterial);
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
            _capData[i] = TileInfo.IsSolid(layers[i]) && TileInfo.IsSolid(layers[plane + i]) ? (byte)layers[i] : (byte)0;

        _capDirty.Clear();
        UploadCap();
    }

    /// <summary>Patches the cap where chunks on the view level (or the one above it) changed.</summary>
    private void UpdateCap()
    {
        bool changed = false;
        foreach (int key in _capDirty)
        {
            var chunk = World.Chunks[key];
            int x0 = chunk.Origin.X, y0 = chunk.Origin.Y;
            int sx = Math.Min(Chunk.Size, World.Width - x0), sy = Math.Min(Chunk.Size, World.Depth - y0);
            var layers = new TileType[sx * sy * 2];
            World.CopyBox(x0, y0, _capLevel, sx, sy, 2, layers);
            for (int y = 0; y < sy; y++)
            for (int x = 0; x < sx; x++)
            {
                var t = layers[y * sx + x];
                var above = layers[sx * sy + y * sx + x];
                _capData[(y0 + y) * World.Width + x0 + x] = TileInfo.IsSolid(t) && TileInfo.IsSolid(above) ? (byte)t : (byte)0;
            }
            changed = true;
        }
        _capDirty.Clear();
        if (changed) UploadCap();
    }

    private void UploadCap()
    {
        _capImage.SetData(World.Width, World.Depth, false, Image.Format.R8, _capData);
        _capTexture.Update(_capImage);
    }

    // ---- Designations --------------------------------------------------------------------------------

    /// <summary>Per kind: instance buffer (12 floats a mark, reused; grown by doubling) and marks in it.</summary>
    private readonly Dictionary<DesignationKind, (float[] Buffer, int Count)> _designationBuffers = new();
    private double _designationsDrawnAt = -1;
    /// <summary>Order changes redraw the marks at most this often (each finished dig changes them).</summary>
    private const double DesignationRedrawSeconds = 0.25;

    /// <summary>
    /// Redraws the order marks in one pass over the orders, writing each kind's instance buffer in place (one
    /// <c>SetInstanceTransform</c> call a mark made a box of 100k orders take seconds per finished dig). Level
    /// changes redraw at once; order changes at most every <see cref="DesignationRedrawSeconds"/>.
    /// </summary>
    private void RedrawDesignations()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (_viewLevel == _designationsDrawnLevel && now - _designationsDrawnAt < DesignationRedrawSeconds) return;
        _designationsDrawnAt = now;
        _designationsDrawnVersion = _sim.Designations.Version;
        _designationsDrawnLevel = _viewLevel;

        foreach (var kind in _designationMarks.Keys)
            _designationBuffers[kind] = (_designationBuffers.TryGetValue(kind, out var b) ? b.Buffer : new float[12 * 64], 0);
        foreach (var (p, k) in _sim.Designations.All)
        {
            // Plant orders always show: that work happens on the surface whatever level is in view. Farms place
            // their own sow and harvest orders; those stay unmarked (the plot overlay already shows the farm).
            if (!(Designations.IsPlantOrder(k) || p.Z <= _viewLevel) || _sim.Farms.Contains(p)) continue;
            var (buffer, count) = _designationBuffers[k];
            if ((count + 1) * 12 > buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            var c = SimSpace.TileCenter(p);
            int i = count * 12;
            buffer[i] = 1; buffer[i + 1] = 0; buffer[i + 2] = 0; buffer[i + 3] = c.X;
            buffer[i + 4] = 0; buffer[i + 5] = 1; buffer[i + 6] = 0; buffer[i + 7] = c.Y;
            buffer[i + 8] = 0; buffer[i + 9] = 0; buffer[i + 10] = 1; buffer[i + 11] = c.Z;
            _designationBuffers[k] = (buffer, count + 1);
        }
        foreach (var (kind, marks) in _designationMarks)
        {
            var (buffer, count) = _designationBuffers[kind];
            var mm = marks.Multimesh;
            if (mm.InstanceCount != buffer.Length / 12) mm.InstanceCount = buffer.Length / 12;
            mm.VisibleInstanceCount = count;
            if (count > 0) mm.Buffer = buffer;
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
