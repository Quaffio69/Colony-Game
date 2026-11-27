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
/// Renders a <see cref="VoxelWorld"/> round the camera; what it costs depends on the view, not on the map's size.
/// <list type="bullet">
/// <item><b>Near window</b> (<see cref="SetView"/>): greedy-meshed sections of <see cref="SectionX"/>x<see cref="SectionY"/>x<see cref="SectionZ"/>
/// tiles exist only on the <see cref="FarBlock"/>² near blocks the window touches (and a margin kept for panning back); further
/// ones are freed. A section re-meshes only when a chunk it covers changes version: snapshots on the main thread, meshing on
/// the thread pool, uploads within a per-frame budget.</item>
/// <item><b>Far blocks</b> draw everything else: coarse heightfields of the visible top (ground or standing water) in a quadtree
/// of five levels (cells of 2 to 32 tiles, blocks of 256 to 4096 columns, <see cref="FarMesher"/>), each level chosen by how
/// many tiles an art pixel covers there, only within the camera's reach. A near block's sections replace its far block once
/// every one is meshed, so panning shows coarse ground first, never holes. Zoomed out (<see cref="FarView"/>) the near window
/// closes and the blocks draw the whole view.</item>
/// <item>The view-level slice is a shader cut plus a texture-driven "cap" quad for the cut surface, so changing level never
/// re-meshes.</item>
/// </list>
/// Also draws dig/stair designations.
/// </summary>
public partial class WorldView : Node3D
{
    public const int SectionX = 32, SectionY = 32, SectionZ = 16;

    /// <summary>Main-thread milliseconds per frame for snapshotting and uploading meshes.</summary>
    [Export] public double MeshBudgetMs { get; set; } = 6.0;
    [Export] public int MaxInFlight { get; set; } = 128;
    /// <summary>Budget while more than <see cref="LoadingBacklog"/> sections are pending (startup, a big jump).</summary>
    [Export] public double LoadingBudgetMs { get; set; } = 40.0;
    private const int LoadingBacklog = 2000;
    private double _budgetMs = 6.0;
    private int _maxInFlight = 128;
    private bool _loaded;

    private sealed class Section
    {
        public required int Index, X0, Y0, Z0, SX, SY, SZ;
        public required Chunk[] Chunks;
        public required NearBlock Block;
        public int[] BuiltVersions = [];
        public bool InFlight, Queued, Live = true, EverBuilt;
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

    /// <summary>A <see cref="FarBlock"/>² block of columns holding sections (in or near the window).</summary>
    private sealed class NearBlock
    {
        public required int Key, X0, Y0;
        public readonly List<Section> Sections = new();
        public int Unbuilt;      // sections never meshed yet
        public bool InWindow;    // wanted now (else kept for panning back, until out of the retained range; not meshed)
        public bool WasInWindow;
        public bool Shown;       // its sections draw instead of its far block
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
    private readonly Dictionary<int, Section> _live = new();
    private readonly Dictionary<int, NearBlock> _nearBlocks = new();
    private readonly List<NearBlock> _nearList = new();
    private readonly ConcurrentQueue<MeshResult> _results = new();
    // Sections waiting to be checked/meshed, fed by the window and VoxelWorld.ChunkChanged: no per-frame scans.
    private readonly Queue<Section> _dirtyQueue = new();
    private int _sectionsX, _sectionsY, _sectionsZ, _blocksX, _blocksY;
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

    // ---- The camera's window (Main sets it every frame) -----------------------------------------------------------

    /// <summary>The near window never reaches further than this (tiles); far blocks draw beyond it.</summary>
    public const float MaxNearRadius = 1024f;
    /// <summary>Near blocks stay meshed this much beyond the window (tiles), so panning back costs nothing.</summary>
    private const float RetainMargin = 384f;

    private Vector2 _center;
    private float _nearRadius = 384f, _farRadius = 2048f, _texel = 0.1f, _rigDistance = 120f;
    private Vector3 _eye;
    private bool _ortho = true, _viewSet;
    private Vector2 _windowCenter = new(float.NaN, float.NaN);
    private float _windowRadius = -1f;
    private bool _windowFar;

    /// <summary>
    /// Where the camera looks: <paramref name="center"/> = the sim column under the view, <paramref name="nearRadius"/> = how far
    /// full sections reach (capped at <see cref="MaxNearRadius"/>), <paramref name="farRadius"/> = how far anything is drawn,
    /// <paramref name="eye"/> = the camera (Godot space), <paramref name="texel"/> = tiles per art pixel at the centre (far blocks
    /// pick their level of detail by it; in perspective it grows with distance past <paramref name="rigDistance"/>).
    /// </summary>
    public void SetView(Vector2 center, float nearRadius, float farRadius, Vector3 eye, float texel, bool ortho, float rigDistance)
    {
        _center = center;
        _nearRadius = Math.Clamp(nearRadius, 64f, MaxNearRadius);
        _farRadius = Math.Max(farRadius, _nearRadius + FarBlock);
        _eye = eye;
        _texel = Math.Max(0.001f, texel);
        _ortho = ortho;
        _rigDistance = Math.Max(1f, rigDistance);
        _viewSet = true;
    }

    /// <summary>The near window's centre and radius now (0 in the far view).</summary>
    public (Vector2 Center, float Radius) Window => (_center, _farShown ? 0f : _nearRadius);

    /// <summary>The view's centre and how far anything in it shows (far blocks reach this far).</summary>
    public (Vector2 Center, float Reach) View => (_center, _farRadius);

    /// <summary>
    /// How far from the view's centre trees and plants are drawn: where far blocks stay at level 0 (an art pixel covers under
    /// 3 tiles). Beyond, coarse blocks mark woodland instead (each tree would be a fraction of a pixel).
    /// </summary>
    public float FloraReach => _texel >= 3f ? 0f : _ortho ? _farRadius : Math.Min(_farRadius, 3f * _rigDistance / _texel);

    /// <summary>Near blocks whose sections draw (WaterView meshes water there only).</summary>
    public IEnumerable<(int X0, int Y0)> ShownBlocks()
    {
        foreach (var b in _nearList) if (b.Shown) yield return (b.X0, b.Y0);
    }

    /// <summary>Whether the near block over a column draws its sections (else its far block draws there).</summary>
    public bool IsShown(int x, int y) =>
        x >= 0 && y >= 0 && x < World.Width && y < World.Depth && _nearBlocks.TryGetValue(BlockKey(x / FarBlock, y / FarBlock), out var b) && b.Shown;

    /// <summary>A near block started or stopped drawing its sections (x0, y0, shown).</summary>
    public event Action<int, int, bool>? ShownChanged;

    /// <summary>Bumped whenever the set of shown near blocks changes.</summary>
    public int ShownVersion { get; private set; }

    private int BlockKey(int bx, int by) => by * _blocksX + bx;

    // ---- Far blocks ----------------------------------------------------------------------------------------------

    public const int FarBlock = 256, FarCell = 2;
    /// <summary>Levels of detail: level L has cells of FarCell &lt;&lt; L tiles and blocks of FarBlock &lt;&lt; L columns.</summary>
    public const int FarLevels = 5;

    private sealed class FarBlockState
    {
        public required int Level, X0, Y0, Size, Cell;
        public required long Key;
        public MeshInstance3D? Node;
        public MeshInstance3D? Shadow; // the same mesh, shadows only (FarShadows)
        public bool Built, Dirty = true, InFlight, Sampled, Removed, Covered;
        public float Top; // highest sampled top (ring included): the iso camera stands back just far enough to clear it
        public double BuiltAt = -1e9;
        public float Distance;
    }
    private sealed record FarResult(FarBlockState Block, MeshArrays? Arrays);
    private readonly Dictionary<long, FarBlockState> _far = new();
    private readonly HashSet<long> _wantedFar = new();
    private readonly List<FarBlockState> _wantedList = new();
    private readonly ConcurrentQueue<FarResult> _farResults = new();
    private int _farWantedBuilt, _farRefresh;
    private double _now, _farSampledAt, _farSelectedAt = -1e9;
    private Vector2 _farSelectedCenter = new(float.NaN, float.NaN);
    private float _farSelectedTexel = -1f;
    private Node3D _nearRoot = null!, _farRoot = null!;
    private bool _farWanted, _farShown;
    /// <summary>The whole view drawn by far blocks (set each frame by Main; takes effect once every far block in view exists).</summary>
    public bool FarView { get => _farShown; set => _farWanted = value; }
    /// <summary>Main-thread ms spent sampling far blocks (diagnostics).</summary>
    public double FarSampleMs;

    private static long FarKey(int level, int bx, int by) => ((long)level << 56) | ((long)by << 28) | (uint)bx;

    // Zoomed out, the sun's shadows come from the far blocks (shadow-only copies: a few dozen draws a cascade) instead of the
    // sections: every cascade draws its casters again, and at 1440p, 1 tile an art pixel, that was ~5000 of 8300 draw calls
    // (the frame was CPU-bound). The blocks are a 2-tile heightfield of the highest tops, so a shadow's edge moves by up
    // to a cell: a pixel or two at these zooms. Their edits follow within 2 s.
    private bool _farShadowsWanted, _farShadowsOn;
    /// <summary>Far blocks cast the terrain's shadows instead of sections (set each frame by Main).</summary>
    public bool FarShadows { get => _farShadowsOn; set => _farShadowsWanted = value; }

    private void ApplyFarShadows()
    {
        var cast = _farShadowsOn ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On;
        foreach (var s in _live.Values) if (s.Instance != null) s.Instance.CastShadow = cast;
        foreach (var b in _far.Values) RefreshFarVisibility(b);
    }

    public VoxelWorld World => _sim.World;
    public int VisibleQuads { get; private set; }
    /// <summary>Startup diagnostics: main-thread ms spent snapshotting / uploading, worker ms spent meshing.</summary>
    public double SnapshotMs, UploadMs;
    private long _workerTicks;
    public double WorkerMs => _workerTicks * 1000.0 / Stopwatch.Frequency;
    /// <summary>Sections that exist now (in and near the window).</summary>
    public int SectionCount => _live.Count;
    /// <summary>Sections waiting to be (re)meshed or uploaded. Zero once the window is fully built.</summary>
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
        TileType.Garnet => new Color(0.62f, 0.14f, 0.22f),
        TileType.Sapphire => new Color(0.20f, 0.32f, 0.78f),
        TileType.Ruby => new Color(0.84f, 0.12f, 0.24f),
        TileType.Emerald => new Color(0.14f, 0.66f, 0.36f),
        TileType.Diamond => new Color(0.90f, 0.96f, 1.00f, 0.5f), // a faint sparkle
        TileType.Sandstone => new Color(0.74f, 0.60f, 0.42f),
        TileType.Limestone => new Color(0.72f, 0.70f, 0.62f),
        TileType.Granite => new Color(0.58f, 0.50f, 0.50f),
        TileType.Basalt => new Color(0.30f, 0.30f, 0.33f),
        TileType.Bedrock => new Color(0.16f, 0.14f, 0.16f),
        TileInfo.Hidden => new Color(0.02f, 0.018f, 0.024f), // unexplored rock (fog of war)
        FarMesher.Water => new Color(0.24f, 0.46f, 0.62f), // standing water in far blocks
        FarMesher.Forest => new Color(0.20f, 0.38f, 0.17f), // woodland in coarse far blocks
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
        _farRoot = new Node3D { Name = "Far" };
        AddChild(_nearRoot);
        AddChild(_farRoot);
        _nearRoot.AddToGroup(GameLooks.SweepSkip); // built with the terrain material: nothing for the look's sweep to convert
        _farRoot.AddToGroup(GameLooks.SweepSkip);

        _sectionsX = (World.Width + SectionX - 1) / SectionX;
        _sectionsY = (World.Depth + SectionY - 1) / SectionY;
        _sectionsZ = (World.Height + SectionZ - 1) / SectionZ;
        _blocksX = (World.Width + FarBlock - 1) / FarBlock;
        _blocksY = (World.Depth + FarBlock - 1) / FarBlock;
        _center = new Vector2(World.Width / 2f, World.Depth / 2f);
        World.ChunkChanged += OnChunkChanged;
        World.ChunkRevealed += OnChunkChanged; // a cavern breached: redraw it (and the cut surface over it)

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
        _now += delta;

        if (_appliedViewLevel != _viewLevel) ApplyViewLevel();

        var clock = Stopwatch.StartNew();
        UpdateWindow();
        // While the window (or a huge change) is still being built, frames are a loading screen anyway:
        // spend most of each one meshing instead of trickling sections in.
        // Only before the first view is complete: later the far blocks cover what is not built yet, so play stays smooth.
        bool loading = !_loaded && PendingSections > LoadingBacklog;
        if (_viewSet && PendingSections == 0 && _nearList.Count > 0) _loaded = true;
        _budgetMs = loading ? LoadingBudgetMs : MeshBudgetMs;
        _maxInFlight = loading ? MaxInFlight * 8 : MaxInFlight;
        ApplyResults(clock);
        ScheduleDirty(clock);
        UpdateFar();
        UpdateShown();
        if (_capDirty.Count > 0) UpdateCap();

        var designations = _sim.Designations;
        if (designations.Version != _designationsDrawnVersion || _viewLevel != _designationsDrawnLevel)
            RedrawDesignations();
    }

    // ---- Near window ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Re-picks the near blocks when the window has moved by a sixteenth of its radius (or changed size, or the far view
    /// switched): blocks it touches get their sections (nearest first), blocks beyond the retained range lose theirs.
    /// </summary>
    private void UpdateWindow()
    {
        bool far = _farShown || _farWanted; // asked for: the far blocks draw everything, nothing more to mesh
        float radius = far ? 0f : _nearRadius;
        float moved = float.IsNaN(_windowCenter.X) ? float.MaxValue : _windowCenter.DistanceTo(_center);
        if (moved < Math.Max(16f, _nearRadius / 16f) && Math.Abs(radius - _windowRadius) <= _windowRadius * 0.05f && _windowFar == far) return;
        _windowCenter = _center;
        _windowRadius = radius;
        _windowFar = far;

        // Blocks the window touches.
        var wanted = new List<(NearBlock Block, float Dist)>();
        foreach (var b in _nearList) (b.WasInWindow, b.InWindow) = (b.InWindow, false);
        if (radius > 0)
        {
            int bx0 = Math.Max(0, (int)((_center.X - radius) / FarBlock)), bx1 = Math.Min(_blocksX - 1, (int)((_center.X + radius) / FarBlock));
            int by0 = Math.Max(0, (int)((_center.Y - radius) / FarBlock)), by1 = Math.Min(_blocksY - 1, (int)((_center.Y + radius) / FarBlock));
            for (int by = by0; by <= by1; by++)
            for (int bx = bx0; bx <= bx1; bx++)
            {
                float d = DistanceToRect(_center, bx * FarBlock, by * FarBlock, FarBlock);
                if (d > radius) continue;
                int key = BlockKey(bx, by);
                if (!_nearBlocks.TryGetValue(key, out var nb))
                {
                    nb = new NearBlock { Key = key, X0 = bx * FarBlock, Y0 = by * FarBlock };
                    _nearBlocks[key] = nb;
                    _nearList.Add(nb);
                    wanted.Add((nb, d));
                }
                nb.InWindow = true;
                if (!nb.WasInWindow) foreach (var s in nb.Sections) Enqueue(s); // back in the window: whatever changed meanwhile
            }
        }
        // New blocks' sections, nearest block first.
        wanted.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        foreach (var (nb, _) in wanted) CreateSections(nb);

        // Blocks out of the retained range go (their far block draws there).
        float retain = _nearRadius + RetainMargin;
        for (int i = _nearList.Count - 1; i >= 0; i--)
        {
            var nb = _nearList[i];
            if (nb.InWindow || DistanceToRect(_center, nb.X0, nb.Y0, FarBlock) <= retain) continue;
            DropBlock(nb);
            _nearList.RemoveAt(i);
            _nearBlocks.Remove(nb.Key);
        }
    }

    private static float DistanceToRect(Vector2 p, float x0, float y0, float size)
    {
        float dx = Math.Max(0, Math.Max(x0 - p.X, p.X - (x0 + size)));
        float dy = Math.Max(0, Math.Max(y0 - p.Y, p.Y - (y0 + size)));
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private void CreateSections(NearBlock nb)
    {
        int sx0 = nb.X0 / SectionX, sy0 = nb.Y0 / SectionY;
        int sx1 = Math.Min(_sectionsX, (nb.X0 + FarBlock) / SectionX), sy1 = Math.Min(_sectionsY, (nb.Y0 + FarBlock) / SectionY);
        for (int sy = sy0; sy < sy1; sy++)
        for (int sx = sx0; sx < sx1; sx++)
        for (int sz = 0; sz < _sectionsZ; sz++)
        {
            int x0 = sx * SectionX, y0 = sy * SectionY, z0 = sz * SectionZ;
            var chunks = new List<Chunk>(4);
            for (int cz = z0 >> Chunk.Shift; cz <= (Math.Min(z0 + SectionZ, World.Height) - 1) >> Chunk.Shift; cz++)
            for (int cy = y0 >> Chunk.Shift; cy <= (Math.Min(y0 + SectionY, World.Depth) - 1) >> Chunk.Shift; cy++)
            for (int cx = x0 >> Chunk.Shift; cx <= (Math.Min(x0 + SectionX, World.Width) - 1) >> Chunk.Shift; cx++)
                chunks.Add(World.GetChunk(cx, cy, cz)!);
            var s = new Section
            {
                Index = (sz * _sectionsY + sy) * _sectionsX + sx,
                X0 = x0, Y0 = y0, Z0 = z0,
                SX = Math.Min(SectionX, World.Width - x0),
                SY = Math.Min(SectionY, World.Depth - y0),
                SZ = Math.Min(SectionZ, World.Height - z0),
                Chunks = chunks.ToArray(),
                Block = nb,
            };
            _live[s.Index] = s;
            nb.Sections.Add(s);
            nb.Unbuilt++;
            Enqueue(s);
        }
    }

    private void DropBlock(NearBlock nb)
    {
        if (nb.Shown) SetShown(nb, false);
        foreach (var s in nb.Sections)
        {
            s.Live = false;
            _live.Remove(s.Index);
            VisibleQuads -= s.QuadCount;
            s.QuadCount = 0;
            s.Instance?.QueueFree();
            s.Instance = null;
        }
        nb.Sections.Clear();
    }

    /// <summary>
    /// A near block draws its sections once all of them are meshed and its far block exists (so taking it away never leaves
    /// a hole), and stops when it leaves the window or the far view comes on.
    /// </summary>
    private void UpdateShown()
    {
        foreach (var nb in _nearList)
        {
            bool shown = nb.InWindow && nb.Unbuilt == 0 && _far.TryGetValue(FarKey(0, nb.X0 / FarBlock, nb.Y0 / FarBlock), out var fb) && fb.Built;
            if (shown != nb.Shown) SetShown(nb, shown);
        }
    }

    private void SetShown(NearBlock nb, bool shown)
    {
        nb.Shown = shown;
        foreach (var s in nb.Sections)
            if (s.Instance != null) s.Instance.Visible = shown && s.Z0 <= _viewLevel;
        if (_far.TryGetValue(FarKey(0, nb.X0 / FarBlock, nb.Y0 / FarBlock), out var fb)) RefreshFarVisibility(fb);
        ShownVersion++;
        ShownChanged?.Invoke(nb.X0, nb.Y0, shown);
    }

    // ---- Sections ------------------------------------------------------------------------------------

    /// <summary>Main-thread ms the last level change took here (--level-sweep).</summary>
    public double LevelMs { get; private set; }

    private void ApplyViewLevel()
    {
        long t0 = Stopwatch.GetTimestamp();
        _appliedViewLevel = _viewLevel;
        _terrainMaterial.SetShaderParameter("cut_height", (float)(_viewLevel + 1));
        foreach (var s in _live.Values)
            if (s.Instance != null) s.Instance.Visible = s.Block.Shown && s.Z0 <= _viewLevel;
        RebuildCapFull();
        LevelMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private void Enqueue(Section s)
    {
        if (s.Queued) return;
        s.Queued = true;
        _dirtyQueue.Enqueue(s);
    }

    // The test menu reloads the scene onto another sim and may bring this one back later: let go of its world.
    public override void _ExitTree()
    {
        if (_sim == null) return;
        World.ChunkChanged -= OnChunkChanged;
        World.ChunkRevealed -= OnChunkChanged;
    }

    /// <summary>A chunk's tiles (or a neighbour's border tiles) changed: requeue its section if it exists, mark the far blocks over it, patch the cap.</summary>
    private void OnChunkChanged(int chunkIndex)
    {
        var c = World.Chunks[chunkIndex].Coord;
        int x = c.X << Chunk.Shift, y = c.Y << Chunk.Shift;
        int sx = x / SectionX, sy = y / SectionY, sz = (c.Z << Chunk.Shift) / SectionZ;
        if (_live.TryGetValue((sz * _sectionsY + sy) * _sectionsX + sx, out var s)) Enqueue(s);
        for (int level = 0; level < FarLevels; level++)
            if (_far.TryGetValue(FarKey(level, x / (FarBlock << level), y / (FarBlock << level)), out var fb)) fb.Dirty = true;
        if (c.Z >= (_capLevel - 1) >> Chunk.Shift && c.Z <= (_capLevel + 1) >> Chunk.Shift) _capDirty.Add(chunkIndex);
    }

    /// <summary>Snapshots queued sections and hands them to worker threads, within the frame budget.</summary>
    private void ScheduleDirty(Stopwatch clock)
    {
        while (_dirtyQueue.TryPeek(out var s))
        {
            // Gone, in flight (its result's arrival re-checks it) or clean: nothing to do.
            if (!s.Live || !s.Block.InWindow || s.InFlight || !s.IsDirty()) // out of the window: requeued when it comes back
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
            var box = TileBox.CaptureSeen(World, s.X0 - SectionMesher.Pad, s.Y0 - SectionMesher.Pad, s.Z0 - SectionMesher.Pad,
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

        void Dequeue() => _dirtyQueue.Dequeue().Queued = false;
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
            if (World.GetChunk(cx, cy, cz) is not { } c || c.SolidCount + c.UnseenCells != Chunk.Volume) return false; // unseen cavern air counts as rock
        return true;
    }

    private MeshResult BuildMesh(Section s, int[] versions, TileBox box)
    {
        var quads = new List<Quad>();
        SectionMesher.Build(box, s.X0, s.Y0, s.Z0, s.SX, s.SY, s.SZ, quads, World.Geology); // natural rock by its kind
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
            if (!s.Live) continue; // its block left the window meanwhile
            s.BuiltVersions = r.Versions;
            if (s.IsDirty()) Enqueue(s); // the world moved on while it was being meshed
            SetMesh(s, r.Arrays.Quads == 0 ? null : r.Arrays.ToMesh(), r.Arrays.Quads);
        }
        UploadMs += (Stopwatch.GetTimestamp() - u0) * 1000.0 / Stopwatch.Frequency;
    }

    private void SetMesh(Section s, ArrayMesh? mesh, int quads)
    {
        if (!s.EverBuilt)
        {
            s.EverBuilt = true;
            s.Block.Unbuilt--;
        }
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
            if (_farShadowsOn) s.Instance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            _nearRoot.AddChild(s.Instance);
        }
        s.Instance.Mesh = mesh;
        s.Instance.Visible = s.Block.Shown && s.Z0 <= _viewLevel;
    }

    // ---- Far blocks ----------------------------------------------------------------------------------

    /// <summary>
    /// Every quarter second (or when the view moves or zooms enough): picks the far blocks the view wants, a quadtree split
    /// down to the level of detail each place calls for (level 0 wherever the near window reaches), within the far radius.
    /// Blocks no longer wanted stay until what replaces them is built, then go. Then samples wanted blocks that are missing
    /// or changed, nearest first, within a budget: sampling reads the live world and water, so it runs on the main thread
    /// (a couple of ms a block at any level); meshing runs on workers. Changed blocks rebuild at most every 2 s; one block
    /// every half second is refreshed in turn (water moves without chunk changes).
    /// </summary>
    private void UpdateFar()
    {
        while (_farResults.TryDequeue(out var r)) ApplyFar(r);
        if (!_viewSet && _far.Count > 0) return;

        if (_now - _farSelectedAt > 0.25 || float.IsNaN(_farSelectedCenter.X) || _farSelectedCenter.DistanceTo(_center) > 64f ||
            Math.Abs(_texel - _farSelectedTexel) > _farSelectedTexel * 0.25f)
            SelectFar();

        bool all = _farWantedBuilt == _wantedList.Count;
        bool show = _farWanted && all;
        if (show != _farShown) _farShown = show;
        if ((_farShadowsWanted && all) != _farShadowsOn)
        {
            _farShadowsOn = _farShadowsWanted && all;
            ApplyFarShadows();
        }

        if (!_sim.WaterReady) return;
        if (_now - _farSampledAt > 0.5 && _wantedList.Count > 0)
        {
            _farSampledAt = _now;
            var turn = _wantedList[_farRefresh++ % _wantedList.Count];
            if (turn.Built && turn.Node is { Visible: true }) { turn.Dirty = true; turn.BuiltAt = -1e9; }
        }
        var clock = Stopwatch.StartNew();
        double budget = !all ? 8.0 : 1.5;
        foreach (var b in _wantedList) // nearest first
        {
            if (clock.Elapsed.TotalMilliseconds > budget) break;
            if (b.InFlight || !b.Dirty || (b.Built && _now - b.BuiltAt < 2.0)) continue;
            // A hidden level-0 block under shown sections waits until it shows again (or casts the shadows).
            if (b.Built && b.Level == 0 && !_farShadowsOn && IsShown(b.X0, b.Y0)) continue;
            SampleFar(b);
        }
    }

    private void SampleFar(FarBlockState b)
    {
        b.Dirty = false;
        b.InFlight = true;
        b.BuiltAt = _now;
        int cell = b.Cell;
        int cx = Math.Min(b.Size, World.Width - b.X0 + cell - 1) / cell;
        int cy = Math.Min(b.Size, World.Depth - b.Y0 + cell - 1) / cell;
        var heights = new float[(cx + 2) * (cy + 2)];
        var types = new TileType[heights.Length];
        long t0 = Stopwatch.GetTimestamp();
        // Coarse levels mark woodland: the trees themselves are drawn only where blocks are at level 0 (FloraReach).
        FarMesher.Sample(World, _sim.WaterReady ? _sim.Water : null, b.X0, b.Y0, cx, cy, cell, heights, types, b.Level > 0 ? _sim.Flora : null);
        float top = 0;
        foreach (float hgt in heights) top = Math.Max(top, hgt);
        b.Top = top;
        b.Sampled = true;
        FarSampleMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        float skirt = cell * 2f; // blocks of other levels meet at its border, each with its own heights across the seam
        Task.Run(() =>
        {
            var quads = new List<Quad>();
            FarMesher.Build(heights, types, cx, cy, b.X0, b.Y0, cell, quads, skirt);
            _farResults.Enqueue(new FarResult(b, quads.Count > 0 ? ToArrays(quads) : null));
        });
    }

    private void ApplyFar(FarResult r)
    {
        var b = r.Block;
        b.InFlight = false;
        if (b.Removed) return;
        if (!b.Built)
        {
            b.Built = true;
            if (_wantedFar.Contains(b.Key)) _farWantedBuilt++;
        }
        if (r.Arrays is not { Quads: > 0 } arrays)
        {
            if (b.Node != null) b.Node.Mesh = null;
            if (b.Shadow != null) b.Shadow.Mesh = null;
            RemoveCovered();
            return;
        }
        if (b.Node == null)
        {
            b.Node = new MeshInstance3D { Name = $"Far{b.Level}_{b.X0}_{b.Y0}", MaterialOverride = _terrainMaterial };
            _farRoot.AddChild(b.Node);
            b.Shadow = new MeshInstance3D
            {
                Name = $"FarShadow{b.Level}_{b.X0}_{b.Y0}", MaterialOverride = _terrainMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
            };
            _farRoot.AddChild(b.Shadow);
        }
        b.Node.Mesh = arrays.ToMesh();
        b.Shadow!.Mesh = b.Node.Mesh;
        RefreshFarVisibility(b);
        RemoveCovered();
    }

    /// <summary>
    /// The level of detail a block wants: cells of 2 &lt;&lt; L tiles while an art pixel covers about 1.5 x 2^L to 3 x 2^L
    /// tiles there (iso: the same everywhere; perspective: growing with the distance from the camera).
    /// </summary>
    private int DesiredLevel(int x0, int y0, int size)
    {
        float texel = _texel;
        if (!_ortho)
        {
            float dx = Math.Max(0, Math.Max(x0 - _eye.X, _eye.X - (x0 + size)));
            float dz = Math.Max(0, Math.Max(y0 - _eye.Z, _eye.Z - (y0 + size)));
            float dist = MathF.Sqrt(dx * dx + dz * dz + _eye.Y * _eye.Y * 0.25f);
            texel *= Math.Max(1f, dist / _rigDistance);
        }
        return Math.Clamp((int)MathF.Floor(MathF.Log2(texel / 1.5f)), 0, FarLevels - 1);
    }

    private void SelectFar()
    {
        _farSelectedAt = _now;
        _farSelectedCenter = _center;
        _farSelectedTexel = _texel;
        // Level 0 wherever sections may draw; none forced once the far view is asked for (it waits for these blocks, so
        // keying on whether it shows would flip between the two sets).
        float near = _farWanted ? 0f : _nearRadius;
        _wantedFar.Clear();
        int top = FarLevels - 1, topSize = FarBlock << top;
        int bx0 = Math.Max(0, (int)((_center.X - _farRadius) / topSize)), bx1 = Math.Min((World.Width - 1) / topSize, (int)((_center.X + _farRadius) / topSize));
        int by0 = Math.Max(0, (int)((_center.Y - _farRadius) / topSize)), by1 = Math.Min((World.Depth - 1) / topSize, (int)((_center.Y + _farRadius) / topSize));
        for (int by = by0; by <= by1; by++)
        for (int bx = bx0; bx <= bx1; bx++)
            Visit(top, bx, by);

        void Visit(int level, int bx, int by)
        {
            int size = FarBlock << level, x0 = bx * size, y0 = by * size;
            if (x0 >= World.Width || y0 >= World.Depth) return;
            float d = DistanceToRect(_center, x0, y0, size);
            if (d > _farRadius) return;
            if (level > 0 && ((near > 0 && d <= near) || DesiredLevel(x0, y0, size) < level)) // no window: d = 0 is no reason
            {
                for (int k = 0; k < 4; k++) Visit(level - 1, bx * 2 + (k & 1), by * 2 + (k >> 1));
                return;
            }
            _wantedFar.Add(FarKey(level, bx, by));
        }

        // Wanted blocks (new ones made), nearest first.
        _wantedList.Clear();
        _farWantedBuilt = 0;
        foreach (long key in _wantedFar)
        {
            if (!_far.TryGetValue(key, out var b))
            {
                int level = (int)(key >> 56), by = (int)((key >> 28) & 0xFFFFFFF), bx = (int)(key & 0xFFFFFFF);
                int size = FarBlock << level;
                b = new FarBlockState { Level = level, X0 = bx * size, Y0 = by * size, Size = size, Cell = FarCell << level, Key = key };
                _far[key] = b;
            }
            b.Distance = DistanceToRect(_center, b.X0, b.Y0, b.Size);
            _wantedList.Add(b);
            if (b.Built) _farWantedBuilt++;
        }
        _wantedList.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        RemoveCovered();
    }

    private readonly List<FarBlockState> _farScratch = new();

    /// <summary>
    /// Blocks no longer wanted go once every wanted block over their ground is built (or none is: out of reach). A wanted
    /// block stays hidden while an old, coarser block still covers it (they'd draw over each other).
    /// </summary>
    private void RemoveCovered()
    {
        _farScratch.Clear();
        foreach (var b in _far.Values)
            if (!_wantedFar.Contains(b.Key) && Replaced(b)) _farScratch.Add(b);
        foreach (var b in _farScratch)
        {
            b.Removed = true;
            b.Node?.QueueFree();
            b.Shadow?.QueueFree();
            _far.Remove(b.Key);
        }
        foreach (var b in _wantedList)
        {
            bool covered = false;
            for (int level = b.Level + 1; level < FarLevels && !covered; level++)
                covered = _far.ContainsKey(FarKey(level, b.X0 / (FarBlock << level), b.Y0 / (FarBlock << level)));
            if (covered != b.Covered)
            {
                b.Covered = covered;
                RefreshFarVisibility(b);
            }
        }
    }

    /// <summary>Whether every wanted block over an unwanted block's ground is built (coarser ones above it, finer ones within).</summary>
    private bool Replaced(FarBlockState b)
    {
        for (int level = b.Level + 1; level < FarLevels; level++)
        {
            long up = FarKey(level, b.X0 / (FarBlock << level), b.Y0 / (FarBlock << level));
            if (_wantedFar.Contains(up)) return _far[up].Built;
        }
        return FinerBuilt(b.Level, b.X0 / b.Size, b.Y0 / b.Size);

        bool FinerBuilt(int level, int bx, int by)
        {
            if (level == 0) return true;
            for (int k = 0; k < 4; k++)
            {
                int cx = bx * 2 + (k & 1), cy = by * 2 + (k >> 1);
                long key = FarKey(level - 1, cx, cy);
                if (_wantedFar.Contains(key)) { if (!_far[key].Built) return false; }
                else if (!FinerBuilt(level - 1, cx, cy)) return false;
            }
            return true;
        }
    }

    /// <summary>A far block draws unless an old coarser block still covers it or (level 0) its near block's sections draw.</summary>
    private void RefreshFarVisibility(FarBlockState b)
    {
        if (b.Node == null) return;
        bool visible = !b.Covered && !(b.Level == 0 && IsShown(b.X0, b.Y0));
        b.Node.Visible = visible;
        if (b.Shadow != null) b.Shadow.Visible = _farShadowsOn && !b.Covered;
    }

    /// <summary>Far blocks in view built so far, of all wanted (loading progress, diagnostics).</summary>
    public (int Built, int Total) FarProgress => (_farWantedBuilt, _wantedList.Count);

    /// <summary>Diagnostics: far blocks per level (wanted/built/visible, and kept unwanted), the window and reach.</summary>
    public string FarSummary()
    {
        var s = new System.Text.StringBuilder($"near {_nearRadius:0}, reach {_farRadius:0}, texel {_texel:0.00}, {(_ortho ? "iso" : "persp")}, far wanted {_farWanted}; levels");
        for (int level = 0; level < FarLevels; level++)
        {
            int wanted = 0, built = 0, visible = 0, kept = 0;
            foreach (var b in _far.Values)
            {
                if (b.Level != level) continue;
                if (_wantedFar.Contains(b.Key)) { wanted++; if (b.Built) built++; } else kept++;
                if (b.Node is { Visible: true }) visible++;
            }
            s.Append($" L{level} {wanted}/{built}/{visible}+{kept}");
        }
        return s.ToString();
    }

    /// <summary>
    /// The highest terrain top (Godot Y) over blocks within <paramref name="radius"/> of (x, z): sampled per far block, so
    /// edits show after the block's rebuild (a couple of seconds). The world's height until a block there is sampled.
    /// </summary>
    public float TopAround(float x, float z, float radius)
    {
        float top = 0;
        bool any = false;
        foreach (var b in _far.Values)
        {
            if (!b.Sampled || b.X0 > x + radius || b.X0 + b.Size < x - radius || b.Y0 > z + radius || b.Y0 + b.Size < z - radius) continue;
            top = Math.Max(top, b.Top);
            any = true;
        }
        return any ? top : World.Height;
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
        var fog = TileColor(TileInfo.Hidden);
        palette[(int)TileInfo.Hidden] = new Vector4(fog.R, fog.G, fog.B, 1f);

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

    /// <summary>
    /// Cap texel = what the tile at the view level shows where the tile above is also solid (its top is hidden by the cut):
    /// its look (natural rock by its kind), or unexplored dark (fog of war) when no open cell touches it.
    /// </summary>
    private void RebuildCapFull()
    {
        int level = _viewLevel;
        _capLevel = level;
        _cap.Position = new Vector3(World.Width / 2f, level + 1, World.Depth / 2f);
        _capMaterial.SetShaderParameter("level", (float)level);
        CutCap.FillAll(World, level, _capData);
        _capDirty.Clear();
        UploadCap();
    }

    /// <summary>Patches the cap where chunks on the view level (or the ones above and below it) changed.</summary>
    private void UpdateCap()
    {
        foreach (int key in _capDirty)
        {
            var chunk = World.Chunks[key];
            int x0 = chunk.Origin.X, y0 = chunk.Origin.Y;
            CutCap.Fill(World, _capLevel, x0, y0, Math.Min(Chunk.Size, World.Width - x0), Math.Min(Chunk.Size, World.Depth - y0), _capData);
        }
        bool changed = _capDirty.Count > 0;
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
