namespace Colony.Core.World;

/// <summary>
/// A cubic block of <see cref="Size"/>³ tiles. Storage is allocated lazily, so empty sky chunks cost
/// almost nothing. <see cref="Version"/> increases whenever anything that affects this chunk's
/// appearance or navigation changes (its own tiles, or a nearby tile in a neighbouring chunk), which lets
/// renderers rebuild only what changed.
/// </summary>
public sealed class Chunk
{
    public const int Shift = 4;
    public const int Size = 1 << Shift; // 16
    public const int Mask = Size - 1;

    private TileType[]? _tiles;

    public ChunkCoord Coord { get; }
    public int Version { get; private set; }

    /// <summary>Number of non-air tiles; lets meshers and other passes skip empty chunks outright.</summary>
    public int SolidCount { get; private set; }

    /// <summary>World-space tile position of this chunk's (0,0,0) corner.</summary>
    public TilePos Origin => new(Coord.X << Shift, Coord.Y << Shift, Coord.Z << Shift);

    public Chunk(ChunkCoord coord) => Coord = coord;

    /// <summary>Raw storage (null when the chunk has never held a solid tile). Indexed by <see cref="Index"/>.</summary>
    internal TileType[]? Raw => _tiles;

    public TileType Get(int lx, int ly, int lz) =>
        _tiles is null ? TileType.Air : _tiles[Index(lx, ly, lz)];

    /// <returns>True if the tile actually changed.</returns>
    internal bool Set(int lx, int ly, int lz, TileType type)
    {
        if (_tiles is null)
        {
            if (type == TileType.Air) return false;
            _tiles = new TileType[Size * Size * Size];
        }

        int i = Index(lx, ly, lz);
        var old = _tiles[i];
        if (old == type) return false;

        if (old == TileType.Air) SolidCount++;
        else if (type == TileType.Air) SolidCount--;

        _tiles[i] = type;
        Version++;
        return true;
    }

    internal void Touch() => Version++;

    internal static int Index(int lx, int ly, int lz) => (lz << (2 * Shift)) | (ly << Shift) | lx;
}
