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
        if (_coverCells > 0)
        {
            // A cell filled in loses its coverings; a floor whose ground is dug away goes with it.
            var p = new TilePos(x, y, z);
            if (TileInfo.IsSolid(type) && CoverAt(p) != 0) SetCover(p, 0);
            var above = new TilePos(x, y, z + 1);
            if (!TileInfo.IsSolid(type) && InBounds(above) && FloorAt(above) != 0) SetFloor(above, 0);
        }
    }

    /// <summary>Notifies the chunk holding (x, y, z) and the neighbouring chunks a change there can affect.</summary>
    private void Changed(int x, int y, int z)
    {
        int lx = x & Chunk.Mask, ly = y & Chunk.Mask, lz = z & Chunk.Mask;
        int cx = x >> Chunk.Shift, cy = y >> Chunk.Shift, cz = z >> Chunk.Shift;
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
                    var raw = chunk.Raw;
                    if (raw != null)
                        Array.Copy(raw, Chunk.Index(lx, wy & Chunk.Mask, wz & Chunk.Mask), dest, (z * sy + y) * sx + x, run);
                    else if (chunk.UniformType != TileType.Air)
                        dest.AsSpan((z * sy + y) * sx + x, run).Fill(chunk.UniformType);
                    x += run;
                }
            }
        }
    }

    private Chunk ChunkAt(int x, int y, int z) =>
        _chunks[ChunkIndex(x >> Chunk.Shift, y >> Chunk.Shift, z >> Chunk.Shift)];
}
