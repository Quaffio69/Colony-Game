namespace Colony.Core.World;

/// <summary>
/// 3D tile grid stored as a dense array of <see cref="Chunk"/>s. Coordinates follow the simulation
/// convention: x/y are horizontal, z is the vertical level (z = 0 is the bottom), like Dwarf Fortress
/// z-levels. The renderer is responsible for mapping this to engine axes.
/// World dimensions need not be multiples of the chunk size; edge chunks are simply partly unused.
/// </summary>
public sealed class VoxelWorld : ITileReader
{
    private readonly Chunk[] _chunks;

    public int Width { get; }   // x
    public int Depth { get; }   // y
    public int Height { get; }  // z (levels)

    public int ChunksX { get; }
    public int ChunksY { get; }
    public int ChunksZ { get; }

    public VoxelWorld(int width, int depth, int height)
    {
        if (width <= 0 || depth <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "World dimensions must be positive.");

        Width = width;
        Depth = depth;
        Height = height;
        ChunksX = (width + Chunk.Mask) >> Chunk.Shift;
        ChunksY = (depth + Chunk.Mask) >> Chunk.Shift;
        ChunksZ = (height + Chunk.Mask) >> Chunk.Shift;

        _chunks = new Chunk[ChunksX * ChunksY * ChunksZ];
        for (int cz = 0; cz < ChunksZ; cz++)
        for (int cy = 0; cy < ChunksY; cy++)
        for (int cx = 0; cx < ChunksX; cx++)
            _chunks[ChunkIndex(cx, cy, cz)] = new Chunk(new ChunkCoord(cx, cy, cz));
    }

    public IReadOnlyList<Chunk> Chunks => _chunks;

    /// <summary>What natural rock is where (set by world generation; null in hand-built worlds: all slate, no bedrock).</summary>
    public Procedural.Geology? Geology { get; set; }

    /// <summary>The level shown as 0 (the lowest ground on the map); lower levels read negative.</summary>
    public int ZeroLevel => Geology?.ZeroLevel ?? 0;

    /// <summary>The kind of natural rock at a position (slate without a geology).</summary>
    public Procedural.Rock RockAt(TilePos p) => Geology?.RockAt(p.X, p.Y, p.Z) ?? Procedural.Rock.Slate;

    /// <summary>Undiggable rock at the bottom of the world.</summary>
    public bool IsBedrock(TilePos p) => Geology is { } g && Get(p) == TileType.Stone && g.RockAt(p.X, p.Y, p.Z) == Procedural.Rock.Bedrock;

    /// <summary>What a tile is drawn as: natural rock by its kind, everything else as itself.</summary>
    public TileType LookAt(int x, int y, int z, TileType tile) =>
        tile == TileType.Stone && Geology is { } g ? Procedural.Geology.Look(g.RockAt(x, y, z)) : tile;

    /// <summary>A tile's name, with natural rock named by its kind.</summary>
    public string TileName(TilePos p)
    {
        var t = Get(p);
        return t == TileType.Stone && Geology is { } g ? Procedural.Geology.Name(g.RockAt(p.X, p.Y, p.Z)) : TileInfo.Name(t);
    }

    /// <summary>
    /// Fog of war: ground no one has seen. Solid ground with no seen open cell beside it (above, below or on any side), and
    /// unseen open cells (caverns not yet breached). Tunnels show the rock round them; what lies further in stays dark
    /// until dug to.
    /// </summary>
    public bool IsHidden(int x, int y, int z)
    {
        if (!TileInfo.IsSolid(Get(x, y, z))) return IsUnseen(x, y, z);
        return Closed(x + 1, y, z) && Closed(x - 1, y, z) && Closed(x, y + 1, z) && Closed(x, y - 1, z)
               && Closed(x, y, z + 1) && Closed(x, y, z - 1);
    }

    // Out of the world counts as closed (the map's sides and bottom are no view), except the sky above the top; so does
    // cavern air no one has seen.
    private bool Closed(int x, int y, int z) => z < Height && (!InBounds(x, y, z) || TileInfo.IsSolid(Get(x, y, z)) || IsUnseen(x, y, z));

    // ---- Unseen open cells (caverns before they are breached) ------------------------------------------------------

    private int _unseenCells;

    /// <summary>Unseen open cells in the world (0: nothing to reveal, and the checks cost nothing).</summary>
    public int UnseenCells => _unseenCells;

    /// <summary>Increments whenever cells are revealed (for views).</summary>
    public long RevealRevision { get; private set; }

    /// <summary>Raised with each chunk whose unseen cells were revealed (views redraw it; nothing else changed there).</summary>
    public event Action<int>? ChunkRevealed;

    /// <summary>An open cell no one has seen yet: drawn and picked as rock until a dig opens beside its cavern.</summary>
    public bool IsUnseen(int x, int y, int z) =>
        _unseenCells > 0 && InBounds(x, y, z) && ChunkAt(x, y, z).IsUnseen(Chunk.Index(x & Chunk.Mask, y & Chunk.Mask, z & Chunk.Mask));

    public bool IsUnseen(TilePos p) => IsUnseen(p.X, p.Y, p.Z);

    /// <summary>World generation: marks a carved cell unseen (no events).</summary>
    internal void SetUnseenForGeneration(int x, int y, int z)
    {
        if (ChunkAt(x, y, z).SetUnseen(Chunk.Index(x & Chunk.Mask, y & Chunk.Mask, z & Chunk.Mask), true))
            Interlocked.Increment(ref _unseenCells);
    }

    /// <summary>A tile changed: an opening beside unseen cells reveals their whole connected space; rock put into an unseen
    /// cell (a fallen mass) simply fills it.</summary>
    private void RevealAround(int x, int y, int z, TileType type)
    {
        var own = ChunkAt(x, y, z);
        int i = Chunk.Index(x & Chunk.Mask, y & Chunk.Mask, z & Chunk.Mask);
        if (TileInfo.IsSolid(type))
        {
            if (own.SetUnseen(i, false)) _unseenCells--;
            return;
        }
        if (own.IsUnseen(i)) Reveal(x, y, z);
        Reveal(x + 1, y, z); Reveal(x - 1, y, z);
        Reveal(x, y + 1, z); Reveal(x, y - 1, z);
        Reveal(x, y, z + 1); Reveal(x, y, z - 1);
    }

    /// <summary>
    /// Reveals the unseen cell at (x, y, z) and every unseen cell connected to it (face neighbours): breaking into a cavern
    /// shows all of it. Deterministic (the revealed set does not depend on order). Returns how many cells.
    /// </summary>
    public int Reveal(int x, int y, int z)
    {
        if (!IsUnseen(x, y, z)) return 0;
        var stack = new Stack<TilePos>();
        var touched = new HashSet<int>();
        stack.Push(new TilePos(x, y, z));
        ChunkAt(x, y, z).SetUnseen(Chunk.Index(x & Chunk.Mask, y & Chunk.Mask, z & Chunk.Mask), false);
        int n = 0;
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            n++;
            touched.Add(ChunkIndex(p.X >> Chunk.Shift, p.Y >> Chunk.Shift, p.Z >> Chunk.Shift));
            void Visit(int nx, int ny, int nz)
            {
                if (!InBounds(nx, ny, nz)) return;
                var c = ChunkAt(nx, ny, nz);
                if (c.Unseen is null) return;
                if (c.SetUnseen(Chunk.Index(nx & Chunk.Mask, ny & Chunk.Mask, nz & Chunk.Mask), false)) stack.Push(new TilePos(nx, ny, nz));
            }
            Visit(p.X + 1, p.Y, p.Z); Visit(p.X - 1, p.Y, p.Z);
            Visit(p.X, p.Y + 1, p.Z); Visit(p.X, p.Y - 1, p.Z);
            Visit(p.X, p.Y, p.Z + 1); Visit(p.X, p.Y, p.Z - 1);
        }
        _unseenCells -= n;
        RevealRevision++;
        // The walls round the revealed cells may sit in neighbouring chunks: those redraw too.
        var notify = new SortedSet<int>();
        foreach (int ci in touched)
        {
            var cc = _chunks[ci].Coord;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (GetChunk(cc.X + dx, cc.Y + dy, cc.Z + dz) is not null) notify.Add(ChunkIndex(cc.X + dx, cc.Y + dy, cc.Z + dz));
        }
        foreach (int ci in touched) _modified?.Add(ci); // snapshots carry what was revealed
        foreach (int ci in notify)
        {
            _chunks[ci].Touch();
            ChunkRevealed?.Invoke(ci);
        }
        return n;
    }

    /// <summary>Overwrites unseen cells in a copied box (see <see cref="CopyBox"/>) with rock, so views draw what is known only.</summary>
    public void MaskUnseen(int x0, int y0, int z0, int sx, int sy, int sz, TileType[] dest)
    {
        if (_unseenCells == 0) return;
        int cx0 = Math.Max(0, x0) >> Chunk.Shift, cx1 = Math.Min(Width - 1, x0 + sx - 1) >> Chunk.Shift;
        int cy0 = Math.Max(0, y0) >> Chunk.Shift, cy1 = Math.Min(Depth - 1, y0 + sy - 1) >> Chunk.Shift;
        int cz0 = Math.Max(0, z0) >> Chunk.Shift, cz1 = Math.Min(Height - 1, z0 + sz - 1) >> Chunk.Shift;
        for (int cz = cz0; cz <= cz1; cz++)
        for (int cy = cy0; cy <= cy1; cy++)
        for (int cx = cx0; cx <= cx1; cx++)
        {
            var chunk = _chunks[ChunkIndex(cx, cy, cz)];
            if (chunk.Unseen is null) continue;
            var o = chunk.Origin;
            for (int lz = 0; lz < Chunk.Size; lz++)
            {
                int z = o.Z + lz - z0;
                if ((uint)z >= (uint)sz) continue;
                for (int ly = 0; ly < Chunk.Size; ly++)
                {
                    int y = o.Y + ly - y0;
                    if ((uint)y >= (uint)sy) continue;
                    for (int lx = 0; lx < Chunk.Size; lx++)
                    {
                        int x = o.X + lx - x0;
                        if ((uint)x < (uint)sx && chunk.IsUnseen(Chunk.Index(lx, ly, lz))) dest[(z * sy + y) * sx + x] = TileType.Stone;
                    }
                }
            }
        }
    }

    /// <summary>Increments on every tile change, so views can skip per-chunk scans when nothing changed.</summary>
    public long Revision { get; private set; }

    public bool InBounds(int x, int y, int z) =>
        (uint)x < (uint)Width && (uint)y < (uint)Depth && (uint)z < (uint)Height;

    public bool InBounds(TilePos p) => InBounds(p.X, p.Y, p.Z);

    /// <summary>Returns the tile at a position; out-of-bounds reads return Air.</summary>
    public TileType Get(int x, int y, int z)
    {
        if (!InBounds(x, y, z)) return TileType.Air;
        return ChunkAt(x, y, z).Get(x & Chunk.Mask, y & Chunk.Mask, z & Chunk.Mask);
    }

    public TileType Get(TilePos p) => Get(p.X, p.Y, p.Z);

    public void Set(int x, int y, int z, TileType type)
    {
        if (!InBounds(x, y, z))
            throw new ArgumentOutOfRangeException(nameof(x), $"({x},{y},{z}) is outside the world.");

        if (!ChunkAt(x, y, z).Set(x & Chunk.Mask, y & Chunk.Mask, z & Chunk.Mask, type)) return;
        Revision++;
        Changed(x, y, z);
        if (_unseenCells > 0) RevealAround(x, y, z, type);
        if (_coverCells > 0)
        {
            // A cell filled in loses its coverings; a floor whose ground is dug away goes with it, unless it is a deck (a
            // built floor of its own), and a deck taken down takes its floor with it.
            var p = new TilePos(x, y, z);
            if (TileInfo.IsSolid(type) && CoverAt(p) != 0) SetCover(p, 0);
            var above = new TilePos(x, y, z + 1);
            if (!TileInfo.IsSolid(type) && InBounds(above) && FloorAt(above) != 0 && Get(above) != TileType.Deck) SetFloor(above, 0);
            if (type == TileType.Air && FloorAt(p) != 0 && (z == 0 || !TileInfo.IsSolid(Get(x, y, z - 1)))) SetFloor(p, 0);
        }
    }

    /// <summary>Notifies the chunk holding (x, y, z) and the neighbouring chunks a change there can affect.</summary>
    private void Changed(int x, int y, int z)
    {
        int lx = x & Chunk.Mask, ly = y & Chunk.Mask, lz = z & Chunk.Mask;
        int cx = x >> Chunk.Shift, cy = y >> Chunk.Shift, cz = z >> Chunk.Shift;
        _modified?.Add(ChunkIndex(cx, cy, cz));
        ChunkChanged?.Invoke(ChunkIndex(cx, cy, cz));

        // A tile near a chunk boundary can change faces or walkability in neighbouring chunks (including
        // diagonal ones). Walking reads up to two levels down (dropping onto a ledge checks its floor), so a
        // change in the top two layers also affects the chunk above.
        int x0 = lx == 0 ? -1 : 0, x1 = lx == Chunk.Mask ? 1 : 0;
        int y0 = ly == 0 ? -1 : 0, y1 = ly == Chunk.Mask ? 1 : 0;
        int z0 = lz == 0 ? -1 : 0, z1 = lz >= Chunk.Mask - 1 ? 1 : 0;
        for (int dz = z0; dz <= z1; dz++)
        for (int dy = y0; dy <= y1; dy++)
        for (int dx = x0; dx <= x1; dx++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            if (GetChunk(cx + dx, cy + dy, cz + dz) is not { } n) continue;
            n.Touch();
            _modified?.Add(ChunkIndex(cx + dx, cy + dy, cz + dz));
            ChunkChanged?.Invoke(ChunkIndex(cx + dx, cy + dy, cz + dz));
        }
    }

    /// <summary>
    /// Raised (synchronously, from <see cref="Set"/>) with the index of every chunk whose
    /// <see cref="Chunk.Version"/> changed, so consumers track exactly what changed instead of scanning every
    /// chunk. Not raised during world generation.
    /// </summary>
    public event Action<int>? ChunkChanged;

    public void Set(TilePos p, TileType type) => Set(p.X, p.Y, p.Z, type);

    // ---- Thin walls on cell edges (see EdgeInfo) ----------------------------------------------------------

    private int _edgeCells;

    /// <summary>Increments whenever any edge changes (for views that draw walls).</summary>
    public long EdgeRevision { get; private set; }
    public bool HasEdges => _edgeCells > 0;

    /// <summary>A cell's east (low byte) and north (high byte) edges; 0 when there are none (or out of bounds).</summary>
    public ushort EdgesAt(int x, int y, int z)
    {
        if (_edgeCells == 0 || !InBounds(x, y, z)) return 0;
        var edges = ChunkAt(x, y, z).Edges;
        return edges is null ? (ushort)0 : edges[Chunk.Index(x & Chunk.Mask, y & Chunk.Mask, z & Chunk.Mask)];
    }

    /// <summary>The edge on a cell's <see cref="EdgeInfo.East"/> or <see cref="EdgeInfo.North"/> side.</summary>
    public byte Edge(TilePos cell, int side)
    {
        ushort e = EdgesAt(cell.X, cell.Y, cell.Z);
        return side == EdgeInfo.East ? EdgeInfo.EastOf(e) : EdgeInfo.NorthOf(e);
    }

    /// <summary>
    /// The edge between two orthogonally adjacent cells on the same level (whichever of them owns it), or 0.
    /// </summary>
    public byte EdgeBetween(TilePos a, TilePos b) =>
        b.X > a.X ? Edge(a, EdgeInfo.East) : b.X < a.X ? Edge(b, EdgeInfo.East) : b.Y > a.Y ? Edge(a, EdgeInfo.North) : Edge(b, EdgeInfo.North);

    /// <summary>Puts (or with 0 removes) a thin wall, door or window on a cell's east or north edge.</summary>
    public void SetEdge(TilePos cell, int side, byte info)
    {
        if (!InBounds(cell)) throw new ArgumentOutOfRangeException(nameof(cell), $"{cell} is outside the world.");
        ushort old = EdgesAt(cell.X, cell.Y, cell.Z);
        ushort value = side == EdgeInfo.East ? (ushort)((old & 0xff00) | info) : (ushort)((old & 0x00ff) | info << 8);
        var chunk = ChunkAt(cell.X, cell.Y, cell.Z);
        int before = chunk.EdgeCells;
        if (!chunk.SetEdges(cell.X & Chunk.Mask, cell.Y & Chunk.Mask, cell.Z & Chunk.Mask, value)) return;
        _edgeCells += chunk.EdgeCells - before;
        Revision++;
        EdgeRevision++;
        Changed(cell.X, cell.Y, cell.Z);
        var other = side == EdgeInfo.East ? cell.Offset(1, 0, 0) : cell.Offset(0, 1, 0);
        if (InBounds(other)) Changed(other.X, other.Y, other.Z);
    }

    // ---- Floors and roofs (see CoverInfo) --------------------------------------------------------------------

    private int _coverCells;

    /// <summary>Increments whenever a floor or roof changes (for views and homes).</summary>
    public long CoverRevision { get; private set; }

    public byte CoverAt(TilePos p)
    {
        if (_coverCells == 0 || !InBounds(p)) return 0;
        var covers = ChunkAt(p.X, p.Y, p.Z).Covers;
        return covers is null ? (byte)0 : covers[Chunk.Index(p.X & Chunk.Mask, p.Y & Chunk.Mask, p.Z & Chunk.Mask)];
    }

    public int FloorAt(TilePos p) => CoverInfo.Floor(CoverAt(p));
    public int RoofAt(TilePos p) => CoverInfo.Roof(CoverAt(p));

    public void SetFloor(TilePos p, int floor) => SetCover(p, CoverInfo.WithFloor(CoverAt(p), floor));
    public void SetRoof(TilePos p, int roof) => SetCover(p, CoverInfo.WithRoof(CoverAt(p), roof));

    private void SetCover(TilePos p, byte value)
    {
        if (!InBounds(p)) throw new ArgumentOutOfRangeException(nameof(p), $"{p} is outside the world.");
        var chunk = ChunkAt(p.X, p.Y, p.Z);
        int before = chunk.CoverCells;
        if (!chunk.SetCover(p.X & Chunk.Mask, p.Y & Chunk.Mask, p.Z & Chunk.Mask, value)) return;
        _coverCells += chunk.CoverCells - before;
        CoverRevision++;
    }

    /// <summary>Every covered cell (floor or roof): (cell, cover byte). Scans only chunks that have covers.</summary>
    public IEnumerable<(TilePos Cell, byte Cover)> AllCovers()
    {
        if (_coverCells == 0) yield break;
        foreach (var chunk in _chunks)
        {
            if (chunk.Covers is not { } covers || chunk.CoverCells == 0) continue;
            var o = chunk.Origin;
            for (int i = 0; i < covers.Length; i++)
                if (covers[i] != 0)
                    yield return (new TilePos(o.X + (i & Chunk.Mask), o.Y + ((i >> Chunk.Shift) & Chunk.Mask), o.Z + (i >> (2 * Chunk.Shift))), covers[i]);
        }
    }

    /// <summary>Every stair cell and its type. Scans only chunks that have stairs (views: built flights of steps).</summary>
    public IEnumerable<(TilePos Cell, TileType Type)> AllStairs()
    {
        foreach (var chunk in _chunks)
        {
            if (chunk.StairCount == 0) continue;
            var o = chunk.Origin;
            for (int lz = 0; lz < Chunk.Size; lz++)
            for (int ly = 0; ly < Chunk.Size; ly++)
            for (int lx = 0; lx < Chunk.Size; lx++)
            {
                var t = chunk.Get(lx, ly, lz);
                if (TileInfo.IsStair(t)) yield return (new TilePos(o.X + lx, o.Y + ly, o.Z + lz), t);
            }
        }
    }

    /// <summary>Every edge with something on it: (cell, side, info). Scans only chunks that have edges.</summary>
    public IEnumerable<(TilePos Cell, int Side, byte Info)> AllEdges()
    {
        if (_edgeCells == 0) yield break;
        foreach (var chunk in _chunks)
        {
            if (chunk.Edges is not { } edges || chunk.EdgeCells == 0) continue;
            var o = chunk.Origin;
            for (int i = 0; i < edges.Length; i++)
            {
                if (edges[i] == 0) continue;
                var p = new TilePos(o.X + (i & Chunk.Mask), o.Y + ((i >> Chunk.Shift) & Chunk.Mask), o.Z + (i >> (2 * Chunk.Shift)));
                if (EdgeInfo.EastOf(edges[i]) is var e and > 0) yield return (p, EdgeInfo.East, e);
                if (EdgeInfo.NorthOf(edges[i]) is var n and > 0) yield return (p, EdgeInfo.North, n);
            }
        }
    }

    /// <summary>Copies the edges in a box (laid out like <see cref="CopyBox"/>); false (nothing copied) if it has none.</summary>
    public bool CopyEdges(int x0, int y0, int z0, int sx, int sy, int sz, ushort[] dest)
    {
        if (_edgeCells == 0) return false;
        bool any = false;
        for (int cz = Math.Max(0, z0) >> Chunk.Shift; cz <= (Math.Min(Height, z0 + sz) - 1) >> Chunk.Shift; cz++)
        for (int cy = Math.Max(0, y0) >> Chunk.Shift; cy <= (Math.Min(Depth, y0 + sy) - 1) >> Chunk.Shift; cy++)
        for (int cx = Math.Max(0, x0) >> Chunk.Shift; cx <= (Math.Min(Width, x0 + sx) - 1) >> Chunk.Shift; cx++)
        {
            var chunk = _chunks[ChunkIndex(cx, cy, cz)];
            if (chunk.Edges is not { } edges || chunk.EdgeCells == 0) continue;
            var o = chunk.Origin;
            for (int i = 0; i < edges.Length; i++)
            {
                if (edges[i] == 0) continue;
                int x = o.X + (i & Chunk.Mask) - x0, y = o.Y + ((i >> Chunk.Shift) & Chunk.Mask) - y0, z = o.Z + (i >> (2 * Chunk.Shift)) - z0;
                if ((uint)x >= (uint)sx || (uint)y >= (uint)sy || (uint)z >= (uint)sz) continue;
                if (!any) Array.Clear(dest, 0, sx * sy * sz);
                any = true;
                dest[(z * sy + y) * sx + x] = edges[i];
            }
        }
        return any;
    }

    /// <summary>Fast write for world generation: no bounds check and no neighbour notification.
    /// Safe to call from several threads as long as each thread owns distinct chunks.</summary>
    internal void SetForGeneration(int x, int y, int z, TileType type) =>
        ChunkAt(x, y, z).Set(x & Chunk.Mask, y & Chunk.Mask, z & Chunk.Mask, type);

    /// <summary>Natural rock/earth that blocks movement (see <see cref="TileInfo.IsSolid"/>); stairs are not solid.</summary>
    public bool IsSolid(int x, int y, int z) => TileInfo.IsSolid(Get(x, y, z));

    /// <summary>Highest solid level in a column, or -1 if the column is empty.</summary>
    public int SurfaceLevel(int x, int y)
    {
        for (int z = Height - 1; z >= 0; z--)
            if (IsSolid(x, y, z))
                return z;
        return -1;
    }

    public Chunk? GetChunk(int cx, int cy, int cz) =>
        (uint)cx < (uint)ChunksX && (uint)cy < (uint)ChunksY && (uint)cz < (uint)ChunksZ
            ? _chunks[ChunkIndex(cx, cy, cz)]
            : null;

    public int ChunkIndex(int cx, int cy, int cz) => (cz * ChunksY + cy) * ChunksX + cx;

    /// <summary>
    /// Copies the box starting at (x0, y0, z0) with the given size into <paramref name="dest"/>, laid out as
    /// ((z * sy) + y) * sx + x. Cells outside the world read as Air. Copies whole chunk rows at a time.
    /// </summary>
    public void CopyBox(int x0, int y0, int z0, int sx, int sy, int sz, TileType[] dest)
    {
        if (dest.Length < sx * sy * sz) throw new ArgumentException("Destination too small.", nameof(dest));
        Array.Clear(dest, 0, sx * sy * sz);

        int xStart = Math.Max(0, -x0), xEnd = Math.Min(sx, Width - x0);
        for (int z = 0; z < sz; z++)
        {
            int wz = z0 + z;
            if ((uint)wz >= (uint)Height) continue;
            for (int y = 0; y < sy; y++)
            {
                int wy = y0 + y;
                if ((uint)wy >= (uint)Depth) continue;
                int x = xStart;
                while (x < xEnd)
                {
                    int wx = x0 + x;
                    int lx = wx & Chunk.Mask;
                    int run = Math.Min(Chunk.Size - lx, xEnd - x);
                    var chunk = ChunkAt(wx, wy, wz);
                    if (!chunk.IsUniform || chunk.UniformType != TileType.Air)
                        chunk.CopyRun(Chunk.Index(lx, wy & Chunk.Mask, wz & Chunk.Mask), dest.AsSpan((z * sy + y) * sx + x, run));
                    x += run;
                }
            }
        }
    }

    private Chunk ChunkAt(int x, int y, int z) =>
        _chunks[ChunkIndex(x >> Chunk.Shift, y >> Chunk.Shift, z >> Chunk.Shift)];

    // ---- Snapshot (see Net.Snapshot) ----------------------------------------------------------------------

    private HashSet<int>? _modified;

    /// <summary>Generation is done: from now on remember which chunks change (a snapshot stores only those).</summary>
    internal void Seal() => _modified = new HashSet<int>();

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(Revision);
        w.Write(EdgeRevision);
        w.Write(CoverRevision);
        w.Write(_edgeCells);
        w.Write(_coverCells);
        var list = _modified is { } m ? m.OrderBy(i => i).ToList() : Enumerable.Range(0, _chunks.Length).ToList();
        w.Write(list.Count);
        foreach (int i in list)
        {
            w.Write(i);
            _chunks[i].SnapSave(w);
        }
    }

    /// <summary>Lays the changed chunks back over a freshly generated world.</summary>
    internal void SnapLoad(BinaryReader r)
    {
        Revision = r.ReadInt64();
        EdgeRevision = r.ReadInt64();
        CoverRevision = r.ReadInt64();
        _edgeCells = r.ReadInt32();
        _coverCells = r.ReadInt32();
        int n = r.ReadInt32();
        _modified = new HashSet<int>();
        for (int k = 0; k < n; k++)
        {
            int i = r.ReadInt32();
            _chunks[i].SnapLoad(r);
            _modified.Add(i);
        }
        _unseenCells = 0;
        foreach (var c in _chunks) _unseenCells += c.UnseenCount;
    }
}
