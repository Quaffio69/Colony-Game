using Colony.Core.World;

namespace Colony.Core.Navigation;

/// <summary>
/// Tracks which standable tiles are mutually reachable, so "can I get there?" is an O(1) lookup instead of
/// a flood fill. Two layers:
/// <list type="number">
/// <item>Each chunk labels its standable tiles into local regions (flood fill restricted to the chunk) and
/// records every move that leaves the chunk.</item>
/// <item>Those cross-chunk moves become links between regions, and union-find over the links gives global
/// components.</item>
/// </list>
/// <see cref="Update"/> relabels only chunks whose <see cref="Chunk.Version"/> changed, then redoes the cheap
/// union step. Correct for splits as well as merges, because nothing is maintained incrementally.
/// Relies on the move graph being undirected (see <see cref="Movement"/>).
/// </summary>
public sealed class Connectivity
{
    private const int Pad = 2; // moves read up to two tiles below and one around a cell

    private sealed class ChunkNav
    {
        public int LabeledVersion = int.MinValue;
        public ushort[]? Labels;           // 0 = not standable, else local region id (1-based)
        public int RegionCount;
        public List<(ushort From, long TargetCell)> CellLinks = new();
        public List<(ushort From, int OtherChunk, ushort OtherRegion)> RegionLinks = new();
        public int DenseBase;
    }

    private readonly VoxelWorld _world;
    private readonly ChunkNav[] _nav;
    private int[] _componentOfRegion = [];
    private bool _built;

    /// <summary>Increments each time components are recomputed.</summary>
    public int Version { get; private set; }
    public int RegionCount { get; private set; }
    public int ComponentCount { get; private set; }

    /// <summary>Chunks relabelled by the most recent <see cref="Update"/> that did any work.</summary>
    public int LastRelabelCount { get; private set; }

    /// <summary>Phase timings of the most recent <see cref="Update"/> that did any work (diagnostics).</summary>
    public string LastTimings { get; private set; } = "";

    public Connectivity(VoxelWorld world)
    {
        _world = world;
        _nav = new ChunkNav[world.Chunks.Count];
        for (int i = 0; i < _nav.Length; i++) _nav[i] = new ChunkNav();
    }

    /// <summary>
    /// Component id of a standable tile, or -1 if it is not standable (or out of bounds). Two tiles are
    /// mutually reachable exactly when they have the same non-negative id. Reflects the world as of the last
    /// <see cref="Update"/>.
    /// </summary>
    public int ComponentOf(TilePos p)
    {
        if (!_world.InBounds(p)) return -1;
        var nav = _nav[_world.ChunkIndex(p.X >> Chunk.Shift, p.Y >> Chunk.Shift, p.Z >> Chunk.Shift)];
        if (nav.Labels is null) return -1;
        int label = nav.Labels[Chunk.Index(p.X & Chunk.Mask, p.Y & Chunk.Mask, p.Z & Chunk.Mask)];
        return label == 0 ? -1 : _componentOfRegion[nav.DenseBase + label - 1];
    }

    public bool Connected(TilePos a, TilePos b)
    {
        int ca = ComponentOf(a);
        return ca >= 0 && ca == ComponentOf(b);
    }

    /// <summary>Brings components up to date with the world. Cheap when nothing changed.</summary>
    public void Update()
    {
        var chunks = _world.Chunks;
        var dirty = new List<int>();
        for (int i = 0; i < chunks.Count; i++)
            if (chunks[i].Version != _nav[i].LabeledVersion)
                dirty.Add(i);
        if (dirty.Count == 0 && _built) return;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (dirty.Count > 8) Parallel.ForEach(dirty, Relabel);
        else foreach (int i in dirty) Relabel(i);
        LastRelabelCount = dirty.Count;

        // Links into a relabelled chunk must be re-resolved, since its region numbering changed.
        var resolve = new HashSet<int>();
        foreach (int i in dirty)
        {
            var c = chunks[i].Coord;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (_world.GetChunk(c.X + dx, c.Y + dy, c.Z + dz) is { } n)
                    resolve.Add(_world.ChunkIndex(n.Coord.X, n.Coord.Y, n.Coord.Z));
        }
        foreach (int i in resolve) ResolveLinks(i);
        double relabelMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();

        RebuildComponents();
        LastTimings = $"relabel+resolve {relabelMs:0.00} ms, components {clock.Elapsed.TotalMilliseconds:0.00} ms, {_edgeCount} region edges";
        _built = true;
        Version++;
    }

    private void Relabel(int chunkIndex)
    {
        var chunk = _world.Chunks[chunkIndex];
        var nav = _nav[chunkIndex];
        nav.LabeledVersion = chunk.Version;
        nav.CellLinks = new List<(ushort, long)>();
        nav.RegionCount = 0;
        nav.Labels = null;

        // Fully solid chunks have no open cells; empty chunks above empty chunks have no floors.
        if (chunk.SolidCount == Chunk.Size * Chunk.Size * Chunk.Size) return;
        var c = chunk.Coord;
        if (chunk.SolidCount == 0 && (_world.GetChunk(c.X, c.Y, c.Z - 1)?.SolidCount ?? 0) == 0) return;

        var o = chunk.Origin;
        var box = TileBox.Capture(_world, o.X - Pad, o.Y - Pad, o.Z - Pad,
            Chunk.Size + 2 * Pad, Chunk.Size + 2 * Pad, Chunk.Size + 2 * Pad);

        var labels = new ushort[Chunk.Size * Chunk.Size * Chunk.Size];
        var stack = new Stack<int>();
        var moves = new List<(TilePos To, float Cost)>(12);
        int regions = 0;

        for (int lz = 0; lz < Chunk.Size; lz++)
        for (int ly = 0; ly < Chunk.Size; ly++)
        for (int lx = 0; lx < Chunk.Size; lx++)
        {
            int seed = Chunk.Index(lx, ly, lz);
            if (labels[seed] != 0) continue;
            if (!Movement.IsStandable(box, new TilePos(o.X + lx, o.Y + ly, o.Z + lz))) continue;

            ushort region = (ushort)++regions;
            labels[seed] = region;
            stack.Push(seed);
            while (stack.TryPop(out int cell))
            {
                var p = new TilePos(o.X + (cell & Chunk.Mask), o.Y + ((cell >> Chunk.Shift) & Chunk.Mask), o.Z + (cell >> (2 * Chunk.Shift)));
                Movement.GetMoves(box, p, moves);
                foreach (var (q, _) in moves)
                {
                    int qx = q.X - o.X, qy = q.Y - o.Y, qz = q.Z - o.Z;
                    if ((uint)qx < Chunk.Size && (uint)qy < Chunk.Size && (uint)qz < Chunk.Size)
                    {
                        int qi = Chunk.Index(qx, qy, qz);
                        if (labels[qi] != 0) continue;
                        labels[qi] = region;
                        stack.Push(qi);
                    }
                    else
                    {
                        nav.CellLinks.Add((region, CellKey(q)));
                    }
                }
            }
        }

        if (regions == 0) return;
        nav.Labels = labels;
        nav.RegionCount = regions;
    }

    private void ResolveLinks(int chunkIndex)
    {
        var nav = _nav[chunkIndex];
        var seen = new HashSet<(ushort, int, ushort)>();
        var resolved = new List<(ushort, int, ushort)>();
        foreach (var (from, cell) in nav.CellLinks)
        {
            long x = cell % _world.Width, rest = cell / _world.Width;
            long y = rest % _world.Depth, z = rest / _world.Depth;
            int other = _world.ChunkIndex((int)x >> Chunk.Shift, (int)y >> Chunk.Shift, (int)z >> Chunk.Shift);
            var labels = _nav[other].Labels;
            if (labels is null) continue;
            ushort region = labels[Chunk.Index((int)x & Chunk.Mask, (int)y & Chunk.Mask, (int)z & Chunk.Mask)];
            if (region == 0) continue; // the target stopped being standable; this link is stale
            if (seen.Add((from, other, region))) resolved.Add((from, other, region));
        }
        nav.RegionLinks = resolved;
    }

    // Reused across updates: these get large on big maps, and reallocating them every update would put
    // multi-megabyte arrays on the large object heap and trigger full GCs.
    private int[] _parent = [];
    private int[] _edgeA = [], _edgeB = [];
    private int[] _rootToId = [];
    private int[] _regionChunk = [];

    private void RebuildComponents()
    {
        int total = 0;
        int edgeCount = 0;
        foreach (var nav in _nav)
        {
            nav.DenseBase = total;
            total += nav.RegionCount;
            edgeCount += nav.RegionLinks.Count;
        }
        RegionCount = total;

        Grow(ref _parent, total);
        Grow(ref _rootToId, total);
        Grow(ref _componentOfRegion, total);
        Grow(ref _regionChunk, total);
        for (int c = 0; c < _nav.Length; c++)
            for (int r = 0; r < _nav[c].RegionCount; r++) _regionChunk[_nav[c].DenseBase + r] = c;
        Grow(ref _edgeA, edgeCount);
        Grow(ref _edgeB, edgeCount);
        var parent = _parent;
        for (int i = 0; i < total; i++) parent[i] = i;

        int Find(int a)
        {
            while (parent[a] != a)
            {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            return a;
        }

        int edges = 0;
        foreach (var nav in _nav)
        foreach (var (from, other, region) in nav.RegionLinks)
        {
            var on = _nav[other];
            if (region > on.RegionCount) continue; // defensive: stale link to a relabelled chunk
            int ra = nav.DenseBase + from - 1, rb = on.DenseBase + region - 1;
            _edgeA[edges] = ra;
            _edgeB[edges] = rb;
            edges++;
            int a = Find(ra), b = Find(rb);
            if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
        }
        BuildRegionGraph(total, edges);

        // Compact component ids to 0..ComponentCount-1.
        Array.Fill(_rootToId, -1, 0, total);
        int components = 0;
        for (int i = 0; i < total; i++)
        {
            int root = Find(i);
            if (_rootToId[root] < 0) _rootToId[root] = components++;
            _componentOfRegion[i] = _rootToId[root];
        }
        ComponentCount = components;
    }

    private static void Grow(ref int[] array, int size)
    {
        if (array.Length < size) array = new int[Math.Max(size, array.Length * 3 / 2)];
    }

    private long CellKey(TilePos p) => ((long)p.Z * _world.Depth + p.Y) * _world.Width + p.X;

    // ---- Region graph (for hierarchical pathfinding) ---------------------------------------------------

    private int[] _adjStart = [0];
    private int[] _adj = [];

    /// <summary>
    /// Region id (dense, valid until the next <see cref="Update"/>) of a standable tile, or -1. Regions are
    /// connected pieces of walkable space within one chunk; adjacent regions share at least one legal move.
    /// </summary>
    public int RegionOf(TilePos p)
    {
        if (!_world.InBounds(p)) return -1;
        var nav = _nav[_world.ChunkIndex(p.X >> Chunk.Shift, p.Y >> Chunk.Shift, p.Z >> Chunk.Shift)];
        if (nav.Labels is null) return -1;
        int label = nav.Labels[Chunk.Index(p.X & Chunk.Mask, p.Y & Chunk.Mask, p.Z & Chunk.Mask)];
        return label == 0 ? -1 : nav.DenseBase + label - 1;
    }

    /// <summary>
    /// Regions a path from <paramref name="start"/> to any of <paramref name="goals"/> can stay within: the
    /// fewest-hops chain of regions between them (breadth-first over the region graph), widened by one ring
    /// of neighbouring regions so the tile-level path isn't forced through awkward squeezes. Every region on
    /// the chain links to the next with a real move, so a path inside the corridor always exists.
    /// Returns null if no goal region is reachable.
    /// </summary>
    public HashSet<int>? Corridor(TilePos start, IEnumerable<TilePos> goals)
    {
        int from = RegionOf(start);
        if (from < 0) return null;
        var goalRegions = new HashSet<int>();
        foreach (var g in goals)
        {
            int r = RegionOf(g);
            if (r >= 0) goalRegions.Add(r);
        }
        if (goalRegions.Count == 0) return null;

        var parent = new Dictionary<int, int> { [from] = -1 };
        var queue = new Queue<int>([from]);
        int end = -1;
        while (queue.TryDequeue(out int r))
        {
            if (goalRegions.Contains(r)) { end = r; break; }
            for (int i = _adjStart[r]; i < _adjStart[r + 1]; i++)
                if (parent.TryAdd(_adj[i], r)) queue.Enqueue(_adj[i]);
        }
        if (end < 0) return null;

        var corridor = new HashSet<int>();
        for (int r = end; r >= 0; r = parent[r])
        {
            corridor.Add(r);
            for (int i = _adjStart[r]; i < _adjStart[r + 1]; i++) corridor.Add(_adj[i]);
        }
        return corridor;
    }

    private int[] _fill = [];

    /// <summary>Tile-space bounding box (inclusive min, exclusive max) of the chunks containing these regions.</summary>
    public (TilePos Min, TilePos Max) RegionBounds(IEnumerable<int> regions)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
        foreach (int r in regions)
        {
            var c = _world.Chunks[_regionChunk[r]].Coord;
            x0 = Math.Min(x0, c.X); y0 = Math.Min(y0, c.Y); z0 = Math.Min(z0, c.Z);
            x1 = Math.Max(x1, c.X); y1 = Math.Max(y1, c.Y); z1 = Math.Max(z1, c.Z);
        }
        return (new TilePos(x0 << Chunk.Shift, y0 << Chunk.Shift, z0 << Chunk.Shift),
                new TilePos((x1 + 1) << Chunk.Shift, (y1 + 1) << Chunk.Shift, (z1 + 1) << Chunk.Shift));
    }

    /// <summary>Compressed adjacency lists (CSR) over dense region ids, both directions, from the edge buffers.
    /// Duplicate edges are harmless for BFS.</summary>
    private void BuildRegionGraph(int regions, int edgeCount)
    {
        Grow(ref _adjStart, regions + 1);
        Grow(ref _fill, regions + 1);
        Grow(ref _adj, edgeCount * 2);
        var start = _adjStart;
        Array.Clear(start, 0, regions + 1);
        for (int e = 0; e < edgeCount; e++)
        {
            start[_edgeA[e] + 1]++;
            start[_edgeB[e] + 1]++;
        }
        for (int i = 0; i < regions; i++) start[i + 1] += start[i];
        Array.Copy(start, _fill, regions + 1);
        for (int e = 0; e < edgeCount; e++)
        {
            _adj[_fill[_edgeA[e]]++] = _edgeB[e];
            _adj[_fill[_edgeB[e]]++] = _edgeA[e];
        }
        _edgeCount = edgeCount;
    }

    private int _edgeCount;
}
