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

    /// <summary>Number of stair cells (views find stairs without scanning every chunk).</summary>
    public int StairCount { get; private set; }

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
        if (TileInfo.IsStair(old)) StairCount--;
        if (TileInfo.IsStair(type)) StairCount++;

        _tiles[i] = type;
        Version++;
        return true;
    }

    internal void Touch() => Version++;

    /// <summary>Thin walls on each cell's east and north edges (see <see cref="EdgeInfo"/>); null until the first one.</summary>
    internal ushort[]? Edges;
    /// <summary>Cells with any edge set.</summary>
    internal int EdgeCells;

    /// <summary>Floor and roof coverings per cell (see <see cref="CoverInfo"/>); null until the first one.</summary>
    internal byte[]? Covers;
    internal int CoverCells;

    internal bool SetCover(int lx, int ly, int lz, byte value)
    {
        Covers ??= new byte[Volume];
        int i = Index(lx, ly, lz);
        if (Covers[i] == value) return false;
        if (Covers[i] == 0) CoverCells++;
        else if (value == 0) CoverCells--;
        Covers[i] = value;
        Version++;
        return true;
    }

    internal bool SetEdges(int lx, int ly, int lz, ushort value)
    {
        Edges ??= new ushort[Volume];
        int i = Index(lx, ly, lz);
        if (Edges[i] == value) return false;
        if (Edges[i] == 0) EdgeCells++;
        else if (value == 0) EdgeCells--;
        Edges[i] = value;
        Version++;
        return true;
    }

    /// <summary>
    /// Open cells no one has seen (cavern air before it is breached), one bit a cell; null when the chunk has none. Drawn
    /// and picked as rock; a dig that opens beside one reveals the whole connected cavern (<see cref="VoxelWorld"/>).
    /// </summary>
    internal ulong[]? Unseen;
    internal int UnseenCount;

    /// <summary>Unseen open cells in the chunk (cavern air before it is breached).</summary>
    public int UnseenCells => UnseenCount;

    internal bool IsUnseen(int i) => Unseen is { } u && (u[i >> 6] >> (i & 63) & 1UL) != 0;

    /// <returns>True if the bit changed.</returns>
    internal bool SetUnseen(int i, bool value)
    {
        if (value == IsUnseen(i)) return false;
        Unseen ??= new ulong[Volume / 64];
        if (value) { Unseen[i >> 6] |= 1UL << (i & 63); UnseenCount++; }
        else { Unseen[i >> 6] &= ~(1UL << (i & 63)); UnseenCount--; }
        if (UnseenCount == 0) Unseen = null;
        return true;
    }

    /// <summary>World generation: make the whole chunk one tile type without allocating storage.</summary>
    internal void InitUniform(TileType type)
    {
        _tiles = null;
        _fill = type;
        NonAirCount = type == TileType.Air ? 0 : Volume;
        SolidCount = TileInfo.IsSolid(type) ? Volume : 0;
        StairCount = TileInfo.IsStair(type) ? Volume : 0;
    }

    /// <summary>World generation: adopt a filled tile array (kept only if the tiles actually vary).</summary>
    internal void InitTiles(TileType[] tiles)
    {
        int nonAir = 0, solid = 0, stairs = 0;
        var first = tiles[0];
        bool uniform = true;
        foreach (var t in tiles)
        {
            if (t != TileType.Air) nonAir++;
            if (TileInfo.IsSolid(t)) solid++;
            if (TileInfo.IsStair(t)) stairs++;
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
        StairCount = stairs;
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

    // ---- Snapshot -----------------------------------------------------------------------------------------

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(Version);
        w.Write(NonAirCount);
        w.Write(SolidCount);
        w.Write((byte)_fill);
        w.Write(_tiles != null);
        if (_tiles != null) w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_tiles.AsSpan()));
        w.Write(Edges != null);
        if (Edges != null) w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Edges.AsSpan()));
        w.Write(EdgeCells);
        w.Write(Covers != null);
        if (Covers != null) w.Write(Covers);
        w.Write(CoverCells);
        w.Write(Unseen != null);
        if (Unseen != null) w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Unseen.AsSpan()));
    }

    internal void SnapLoad(BinaryReader r)
    {
        Version = r.ReadInt32();
        NonAirCount = r.ReadInt32();
        SolidCount = r.ReadInt32();
        _fill = (TileType)r.ReadByte();
        _tiles = null;
        if (r.ReadBoolean())
        {
            _tiles = new TileType[Volume];
            r.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_tiles.AsSpan()));
        }
        StairCount = 0; // derived (not saved)
        if (_tiles is null) StairCount = TileInfo.IsStair(_fill) ? Volume : 0;
        else foreach (var t in _tiles) if (TileInfo.IsStair(t)) StairCount++;
        Edges = null;
        if (r.ReadBoolean())
        {
            Edges = new ushort[Volume];
            r.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Edges.AsSpan()));
        }
        EdgeCells = r.ReadInt32();
        Covers = null;
        if (r.ReadBoolean())
        {
            Covers = new byte[Volume];
            r.BaseStream.ReadExactly(Covers);
        }
        CoverCells = r.ReadInt32();
        if (Net.SnapIO.Before(8)) return; // older snapshots: the generated caverns stay as generated
        Unseen = null;
        UnseenCount = 0;
        if (r.ReadBoolean())
        {
            Unseen = new ulong[Volume / 64];
            r.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Unseen.AsSpan()));
            foreach (ulong v in Unseen) UnseenCount += System.Numerics.BitOperations.PopCount(v);
            if (UnseenCount == 0) Unseen = null;
        }
    }
}
