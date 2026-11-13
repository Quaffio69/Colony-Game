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

        int lx = x & Chunk.Mask, ly = y & Chunk.Mask, lz = z & Chunk.Mask;
        if (!ChunkAt(x, y, z).Set(lx, ly, lz, type)) return;
        Revision++;
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
