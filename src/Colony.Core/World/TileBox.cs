namespace Colony.Core.World;

/// <summary>Read access to tiles. Implemented by the live world and by snapshots, so algorithms can run on either.</summary>
public interface ITileReader
{
    TileType Get(int x, int y, int z);
    bool InBounds(int x, int y, int z);
    /// <summary>A cell's thin-wall edges (east low byte, north high byte; see <see cref="EdgeInfo"/>), 0 if none.</summary>
    ushort EdgesAt(int x, int y, int z);
}

/// <summary>
/// A copied box of tiles in world coordinates. Cheap to read from (no chunk lookups) and safe to hand to
/// worker threads while the live world keeps changing. Reads outside the box return Air, so capture
/// enough padding for whatever the consumer looks at.
/// </summary>
public readonly struct TileBox : ITileReader
{
    private readonly TileType[] _tiles;
    private readonly ushort[]? _edges; // null when the box holds no thin walls
    private readonly int _worldW, _worldD, _worldH;

    public int X0 { get; }
    public int Y0 { get; }
    public int Z0 { get; }
    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }

    private TileBox(TileType[] tiles, ushort[]? edges, int x0, int y0, int z0, int sx, int sy, int sz, int w, int d, int h)
    {
        _tiles = tiles;
        _edges = edges;
        X0 = x0; Y0 = y0; Z0 = z0;
        SizeX = sx; SizeY = sy; SizeZ = sz;
        _worldW = w; _worldD = d; _worldH = h;
    }

    public static TileBox Capture(VoxelWorld world, int x0, int y0, int z0, int sx, int sy, int sz)
    {
        var tiles = new TileType[sx * sy * sz];
        world.CopyBox(x0, y0, z0, sx, sy, sz, tiles);
        ushort[]? edges = null;
        if (world.HasEdges)
        {
            edges = new ushort[sx * sy * sz];
            if (!world.CopyEdges(x0, y0, z0, sx, sy, sz, edges)) edges = null;
        }
        return new TileBox(tiles, edges, x0, y0, z0, sx, sy, sz, world.Width, world.Depth, world.Height);
    }

    public TileType Get(int x, int y, int z)
    {
        int lx = x - X0, ly = y - Y0, lz = z - Z0;
        if ((uint)lx >= (uint)SizeX || (uint)ly >= (uint)SizeY || (uint)lz >= (uint)SizeZ) return TileType.Air;
        return _tiles[(lz * SizeY + ly) * SizeX + lx];
    }

    public ushort EdgesAt(int x, int y, int z)
    {
        if (_edges is null) return 0;
        int lx = x - X0, ly = y - Y0, lz = z - Z0;
        if ((uint)lx >= (uint)SizeX || (uint)ly >= (uint)SizeY || (uint)lz >= (uint)SizeZ) return 0;
        return _edges[(lz * SizeY + ly) * SizeX + lx];
    }

    public bool InBounds(int x, int y, int z) =>
        (uint)x < (uint)_worldW && (uint)y < (uint)_worldD && (uint)z < (uint)_worldH;
}
