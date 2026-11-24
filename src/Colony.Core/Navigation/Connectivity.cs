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

    /// <summary>A move from local region <see cref="From"/> into region <see cref="Region"/> of chunk <see cref="Other"/>.</summary>
    private readonly record struct Link(ushort From, ushort Region, int Other);

    private sealed class ChunkNav
    {
        public int LabeledVersion = int.MinValue;
        public ushort[]? Labels;           // 0 = not standable, else local region id (1-based)
        public int RegionCount;
        public List<(ushort From, long TargetCell)> CellLinks = new();
        public Link[] RegionLinks = [];
        public int DenseBase;
    }

    private readonly VoxelWorld _world;
    private readonly ChunkNav[] _nav;
    private int[] _componentOfRegion = [];
    private bool _built;
    private readonly List<int> _active = new();
    private bool _activeDirty = true;

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
        _baseOf = new int[_nav.Length];
        _countOf = new int[_nav.Length];
        world.ChunkChanged += i => _changed.Add(i);
        world.ChunkRevealed += i => _changed.Add(i); // a breached cavern's floors become standable
    }

    // Chunks changed since the last update (fed by VoxelWorld.ChunkChanged), so updates never scan the world.
    private readonly HashSet<int> _changed = new();

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
        if (_built && _changed.Count == 0) return; // O(1) when the world is unchanged

        var chunks = _world.Chunks;
        List<int> dirty;
        if (_built)
        {
            dirty = new List<int>(_changed);
            dirty.Sort(); // deterministic order (results don't depend on it, but keep runs identical)
        }
        else
        {
            dirty = new List<int>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++) dirty.Add(i);
        }
        _changed.Clear();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var hadRegions = new bool[dirty.Count];
        for (int i = 0; i < dirty.Count; i++) hadRegions[i] = _nav[dirty[i]].RegionCount > 0;
        if (dirty.Count > 8) Parallel.ForEach(dirty, Relabel);
        else foreach (int i in dirty) Relabel(i);
        LastRelabelCount = dirty.Count;
        for (int i = 0; i < dirty.Count && !_activeDirty; i++)
            _activeDirty = hadRegions[i] != _nav[dirty[i]].RegionCount > 0;
        if (_activeDirty)
        {
            // Chunks with walkable regions, in index order (keeps dense region ids deterministic). Only a
            // small share of a deep world's chunks: the rest are solid rock or open sky.
            _active.Clear();
            for (int i = 0; i < _nav.Length; i++)
                if (_nav[i].RegionCount > 0) _active.Add(i);
            _activeDirty = false;
        }

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
        if (resolve.Count > 32) Parallel.ForEach(resolve, ResolveLinks); // each writes only its own chunk
        else foreach (int i in resolve) ResolveLinks(i);
        double relabelMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();

        RebuildComponents();
        LastTimings = $"relabel+resolve {relabelMs:0.00} ms, components {clock.Elapsed.TotalMilliseconds:0.00} ms ({_componentPhases}), {_edgeCount} region edges";
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
        _countOf[chunkIndex] = 0;
        nav.Labels = null;

        // Fully solid chunks have no open cells (stairs are open, so they don't count as solid); empty chunks
        // (no stairs either) above chunks with nothing solid have no floors.
        if (chunk.SolidCount + chunk.UnseenCells == Chunk.Size * Chunk.Size * Chunk.Size) return; // unseen cavern air is rock until found
        var c = chunk.Coord;
        if (chunk.NonAirCount == 0 && (_world.GetChunk(c.X, c.Y, c.Z - 1)?.SolidCount ?? 0) == 0) return;

        var o = chunk.Origin;
        // As known: unseen cavern floors are not standable (no one can reach them, and labelling every cavern chunk cost
        // ~100 MB on a big map); a breach reveals and relabels them.
        var box = TileBox.CaptureSeen(_world, o.X - Pad, o.Y - Pad, o.Z - Pad,
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
        _countOf[chunkIndex] = regions;
    }

    private void ResolveLinks(int chunkIndex)
    {
        var nav = _nav[chunkIndex];
        if (nav.CellLinks.Count == 0)
        {
            nav.RegionLinks = [];
            return;
        }
        var seen = new HashSet<long>();
        var resolved = new List<Link>();
        foreach (var (from, cell) in nav.CellLinks)
        {
            long x = cell % _world.Width, rest = cell / _world.Width;
            long y = rest % _world.Depth, z = rest / _world.Depth;
            int other = _world.ChunkIndex((int)x >> Chunk.Shift, (int)y >> Chunk.Shift, (int)z >> Chunk.Shift);
            var labels = _nav[other].Labels;
            if (labels is null) continue;
            ushort region = labels[Chunk.Index((int)x & Chunk.Mask, (int)y & Chunk.Mask, (int)z & Chunk.Mask)];
            if (region == 0) continue; // the target stopped being standable; this link is stale
            if (seen.Add(((long)other << 32) | ((long)region << 16) | from)) resolved.Add(new Link(from, region, other));
        }
        nav.RegionLinks = resolved.ToArray();
    }

    // Reused across updates: these get large on big maps, and reallocating them every update would put
    // multi-megabyte arrays on the large object heap and trigger full GCs.
    private int[] _queue = [];
    private int[] _edgeA = [], _edgeB = [];
    private int[] _regionChunk = [];
    private readonly int[] _baseOf, _countOf; // per chunk index: dense id of its first region; region count (kept by Relabel)

    private string _componentPhases = "";

    private void RebuildComponents()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int total = 0;
        int edgeCount = 0;
        foreach (int c in _active)
        {
            var nav = _nav[c];
            nav.DenseBase = total;
            _baseOf[c] = total;
            total += nav.RegionCount;
            edgeCount += nav.RegionLinks.Length;
        }
        RegionCount = total;

        Grow(ref _queue, total);
        Grow(ref _componentOfRegion, total);
        Grow(ref _regionChunk, total);
        foreach (int c in _active)
            for (int r = 0; r < _nav[c].RegionCount; r++) _regionChunk[_nav[c].DenseBase + r] = c;
        Grow(ref _edgeA, edgeCount);
        Grow(ref _edgeB, edgeCount);
        double tPrep = sw.Elapsed.TotalMilliseconds;

        int edges = 0;
        var baseOf = _baseOf;
        var countOf = _countOf;
        foreach (int c in _active)
        {
            int baseA = baseOf[c] - 1;
            foreach (var link in _nav[c].RegionLinks)
            {
                if (link.Region > countOf[link.Other]) continue; // defensive: stale link to a relabelled chunk
                _edgeA[edges] = baseA + link.From;
                _edgeB[edges] = baseOf[link.Other] + link.Region - 1;
                edges++;
            }
        }
        double tEdges = sw.Elapsed.TotalMilliseconds;
        BuildRegionGraph(total, edges);
        double tGraph = sw.Elapsed.TotalMilliseconds;

        // Components: breadth-first over the region graph, numbered in order of their lowest region id.
        // Linear in regions + edges (union-find with naive linking built long chains and was ~5x slower).
        var comp = _componentOfRegion;
        Array.Fill(comp, -1, 0, total);
        var queue = _queue;
        int components = 0;
        for (int i = 0; i < total; i++)
        {
            if (comp[i] >= 0) continue;
            int id = components++, head = 0, tail = 0;
            comp[i] = id;
            queue[tail++] = i;
            while (head < tail)
            {
                int r = queue[head++];
                for (int k = _adjStart[r]; k < _adjStart[r + 1]; k++)
                {
                    int n = _adj[k];
                    if (comp[n] >= 0) continue;
                    comp[n] = id;
                    queue[tail++] = n;
                }
            }
        }
        ComponentCount = components;
        _componentPhases = $"prep {tPrep:0.00}, edges {tEdges - tPrep:0.00}, graph {tGraph - tEdges:0.00}, bfs {sw.Elapsed.TotalMilliseconds - tGraph:0.00}";
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
        var chain = RegionChain(start, goals);
        if (chain is null) return null;
        return Widen(chain, 0, chain.Count);
    }

    /// <summary>Regions <paramref name="from"/>..<paramref name="to"/> (exclusive) of a chain plus their neighbours.</summary>
    public HashSet<int> Widen(List<int> chain, int from, int to)
    {
        var corridor = new HashSet<int>();
        for (int k = from; k < to; k++)
        {
            int r = chain[k];
            corridor.Add(r);
            for (int i = _adjStart[r]; i < _adjStart[r + 1]; i++) corridor.Add(_adj[i]);
        }
        return corridor;
    }

    /// <summary>
    /// The fewest-hops chain of regions from <paramref name="start"/>'s region to the nearest region holding one
    /// of <paramref name="goals"/> (breadth-first over the region graph), start first. Null if none is reachable.
    /// </summary>
    public List<int>? RegionChain(TilePos start, IEnumerable<TilePos> goals)
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

        var chain = new List<int>();
        for (int r = end; r >= 0; r = parent[r]) chain.Add(r);
        chain.Reverse();
        return chain;
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

    /// <summary>Forgets the labelling: the next <see cref="Update"/> labels the whole world from scratch (snapshots do this on both sides).</summary>
    internal void Reset()
    {
        _built = false;
        _changed.Clear();
        _activeDirty = true;
    }
}
