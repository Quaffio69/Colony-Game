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

    public const int Volume = Size * Size * Size;

    // Null while every tile equals _fill (a "uniform" chunk: sky, or deep solid rock). That makes deep worlds
    // cheap: only chunks with variety store an array. The first differing write materialises it.
    private TileType[]? _tiles;
    private TileType _fill;

    public ChunkCoord Coord { get; }
    public int Version { get; private set; }

    /// <summary>True while every tile in the chunk is <see cref="UniformType"/> (no per-tile storage).</summary>
    public bool IsUniform => _tiles is null;
    public TileType UniformType => _fill;

    /// <summary>Number of non-air tiles; lets meshers and other passes skip empty chunks outright.</summary>
    public int NonAirCount { get; private set; }

    /// <summary>Number of movement-blocking tiles (<see cref="TileInfo.IsSolid"/>; stairs excluded).</summary>
    public int SolidCount { get; private set; }

    /// <summary>World-space tile position of this chunk's (0,0,0) corner.</summary>
    public TilePos Origin => new(Coord.X << Shift, Coord.Y << Shift, Coord.Z << Shift);

    public Chunk(ChunkCoord coord) => Coord = coord;

    /// <summary>Raw storage, or null for a uniform chunk (see <see cref="UniformType"/>). Indexed by <see cref="Index"/>.</summary>
    internal TileType[]? Raw => _tiles;

    public TileType Get(int lx, int ly, int lz) =>
        _tiles is null ? _fill : _tiles[Index(lx, ly, lz)];

    /// <returns>True if the tile actually changed.</returns>
    internal bool Set(int lx, int ly, int lz, TileType type)
    {
        if (_tiles is null)
        {
            if (type == _fill) return false;
            _tiles = new TileType[Volume];
            if (_fill != TileType.Air) Array.Fill(_tiles, _fill);
        }

        int i = Index(lx, ly, lz);
        var old = _tiles[i];
        if (old == type) return false;

        if (old == TileType.Air) NonAirCount++;
        else if (type == TileType.Air) NonAirCount--;
        if (TileInfo.IsSolid(old)) SolidCount--;
        if (TileInfo.IsSolid(type)) SolidCount++;

        _tiles[i] = type;
        Version++;
        return true;
    }

    internal void Touch() => Version++;

    /// <summary>World generation: make the whole chunk one tile type without allocating storage.</summary>
    internal void InitUniform(TileType type)
    {
        _tiles = null;
        _fill = type;
        NonAirCount = type == TileType.Air ? 0 : Volume;
        SolidCount = TileInfo.IsSolid(type) ? Volume : 0;
    }

    /// <summary>World generation: adopt a filled tile array (kept only if the tiles actually vary).</summary>
    internal void InitTiles(TileType[] tiles)
    {
        int nonAir = 0, solid = 0;
        var first = tiles[0];
        bool uniform = true;
        foreach (var t in tiles)
        {
            if (t != TileType.Air) nonAir++;
            if (TileInfo.IsSolid(t)) solid++;
            uniform &= t == first;
        }
        if (uniform)
        {
            InitUniform(first);
            return;
        }
        _tiles = tiles;
        NonAirCount = nonAir;
        SolidCount = solid;
    }

    /// <summary>Drops the tile array if every tile is the same (after generation passes that add variety).</summary>
    internal void Compact()
    {
        if (_tiles is null) return;
        var first = _tiles[0];
        foreach (var t in _tiles)
            if (t != first) return;
        InitUniform(first);
    }

    internal static int Index(int lx, int ly, int lz) => (lz << (2 * Shift)) | (ly << Shift) | lx;
}
