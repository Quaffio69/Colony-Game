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

    // Storage, smallest that fits:
    //  - uniform: no array while every tile equals _fill (sky, deep solid rock), so deep worlds cost almost nothing;
    //  - packed: _bits (1, 2 or 4) per tile into _packed, indexing _palette (2, 4 or 16 types). A chunk rarely holds more
    //    than a handful of kinds (air, grass, soil, a rock or two): tiles were 1.1 GB at 4096 as a byte each;
    //  - bytes: _tiles, a byte per tile, once a chunk holds more than 16 kinds.
    // A write that needs more room widens the storage; Compact narrows it again.
    private TileType[]? _tiles;
    private ulong[]? _packed;
    private TileType[]? _palette;
    private int _bits, _paletteCount;
    private TileType _fill;

    public ChunkCoord Coord { get; }
    public int Version { get; private set; }

    /// <summary>True while every tile in the chunk is <see cref="UniformType"/> (no per-tile storage).</summary>
    public bool IsUniform => _tiles is null && _packed is null;
    public TileType UniformType => _fill;

    /// <summary>Bytes of per-tile storage (diagnostics).</summary>
    public int StorageBytes => _tiles != null ? _tiles.Length : _packed != null ? _packed.Length * 8 + _palette!.Length : 0;

    /// <summary>Number of non-air tiles; lets meshers and other passes skip empty chunks outright.</summary>
    public int NonAirCount { get; private set; }

    /// <summary>Number of movement-blocking tiles (<see cref="TileInfo.IsSolid"/>; stairs excluded).</summary>
    public int SolidCount { get; private set; }

    /// <summary>Number of stair cells (views find stairs without scanning every chunk).</summary>
    public int StairCount { get; private set; }

    /// <summary>World-space tile position of this chunk's (0,0,0) corner.</summary>
    public TilePos Origin => new(Coord.X << Shift, Coord.Y << Shift, Coord.Z << Shift);

    public Chunk(ChunkCoord coord) => Coord = coord;

    public TileType Get(int lx, int ly, int lz) => At(Index(lx, ly, lz));

    /// <summary>The tile at storage index <paramref name="i"/> (see <see cref="Index"/>).</summary>
    internal TileType At(int i)
    {
        if (_packed != null)
        {
            int bit = i * _bits;
            return _palette![(int)(_packed[bit >> 6] >> (bit & 63)) & ((1 << _bits) - 1)];
        }
        return _tiles != null ? _tiles[i] : _fill;
    }

    /// <summary>Copies <paramref name="dest"/>.Length tiles from storage index <paramref name="i"/> (a run along x within one row).</summary>
    internal void CopyRun(int i, Span<TileType> dest)
    {
        if (_tiles != null) _tiles.AsSpan(i, dest.Length).CopyTo(dest);
        else if (_packed == null) dest.Fill(_fill);
        else for (int k = 0; k < dest.Length; k++) dest[k] = At(i + k);
    }

    /// <summary>Every tile, unpacked, in storage order (snapshots keep this format).</summary>
    internal TileType[] Unpacked()
    {
        var all = new TileType[Volume];
        CopyRun(0, all);
        return all;
    }

    /// <returns>True if the tile actually changed.</returns>
    internal bool Set(int lx, int ly, int lz, TileType type)
    {
        int i = Index(lx, ly, lz);
        var old = At(i);
        if (old == type) return false;
        Store(i, type);

        if (old == TileType.Air) NonAirCount++;
        else if (type == TileType.Air) NonAirCount--;
        if (TileInfo.IsSolid(old)) SolidCount--;
        if (TileInfo.IsSolid(type)) SolidCount++;
        if (TileInfo.IsStair(old)) StairCount--;
        if (TileInfo.IsStair(type)) StairCount++;

        Version++;
        return true;
    }

    /// <summary>Writes a tile at a storage index, widening the storage if it has no room for the type.</summary>
    private void Store(int i, TileType type)
    {
        if (_tiles != null)
        {
            _tiles[i] = type;
            return;
        }
        if (_packed == null) // uniform: becomes a two-kind palette
        {
            _bits = 1;
            _palette = new TileType[2];
            _palette[0] = _fill;
            _paletteCount = 1;
            _packed = new ulong[Volume / 64];
        }
        int index = Array.IndexOf(_palette!, type, 0, _paletteCount);
        if (index < 0)
        {
            if (_paletteCount == 1 << _bits)
            {
                if (_bits == 4)
                {
                    var all = Unpacked();
                    all[i] = type;
                    _tiles = all;
                    _packed = null;
                    _palette = null;
                    _bits = _paletteCount = 0;
                    return;
                }
                Repack(_bits * 2);
            }
            index = _paletteCount++;
            _palette![index] = type;
        }
        int bit = i * _bits;
        ulong mask = ((1UL << _bits) - 1) << (bit & 63);
        _packed![bit >> 6] = (_packed[bit >> 6] & ~mask) | ((ulong)index << (bit & 63));
    }

    /// <summary>Re-encodes the packed tiles at <paramref name="bits"/> per tile (same palette order).</summary>
    private void Repack(int bits)
    {
        var packed = new ulong[Volume * bits / 64];
        int oldBits = _bits, oldMask = (1 << oldBits) - 1;
        for (int i = 0; i < Volume; i++)
        {
            int ob = i * oldBits, nb = i * bits;
            ulong v = (_packed![ob >> 6] >> (ob & 63)) & (ulong)oldMask;
            packed[nb >> 6] |= v << (nb & 63);
        }
        var palette = new TileType[1 << bits];
        Array.Copy(_palette!, palette, _paletteCount);
        _packed = packed;
        _palette = palette;
        _bits = bits;
    }

    /// <summary>Stores a full tile array as compactly as it fits (uniform, packed or bytes). Counters are the caller's.</summary>
    private void Adopt(TileType[] tiles)
    {
        _tiles = null;
        _packed = null;
        _palette = null;
        _bits = _paletteCount = 0;
        Span<TileType> kinds = stackalloc TileType[16];
        int count = 0;
        foreach (var t in tiles)
        {
            int seen = 0;
            while (seen < count && kinds[seen] != t) seen++;
            if (seen < count) continue;
            if (count == 16) { _tiles = tiles; return; }
            kinds[count++] = t;
        }
        _fill = kinds[0];
        if (count == 1) return;
        _bits = count <= 2 ? 1 : count <= 4 ? 2 : 4;
        _palette = new TileType[1 << _bits];
        kinds[..count].CopyTo(_palette);
        _paletteCount = count;
        _packed = new ulong[Volume * _bits / 64];
        for (int i = 0; i < Volume; i++)
        {
            int index = Array.IndexOf(_palette, tiles[i], 0, count), bit = i * _bits;
            _packed[bit >> 6] |= (ulong)index << (bit & 63);
        }
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
        _packed = null;
        _palette = null;
        _bits = _paletteCount = 0;
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
        Adopt(tiles);
        NonAirCount = nonAir;
        SolidCount = solid;
        StairCount = stairs;
    }

    /// <summary>Stores the tiles as compactly as they fit again: uniform if every tile is the same, else packed when 16
    /// kinds or fewer (after generation passes that add variety, or edits that brought in kinds that have gone again).</summary>
    internal void Compact()
    {
        if (IsUniform) return;
        var all = Unpacked();
        var first = all[0];
        bool uniform = true;
        foreach (var t in all) uniform &= t == first;
        if (uniform) InitUniform(first);
        else Adopt(all);
    }

    internal static int Index(int lx, int ly, int lz) => (lz << (2 * Shift)) | (ly << Shift) | lx;

    // ---- Snapshot -----------------------------------------------------------------------------------------

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(Version);
        w.Write(NonAirCount);
        w.Write(SolidCount);
        // Tiles a byte each whatever the storage (a loaded copy packs them again); the fill only means something when uniform.
        w.Write((byte)(IsUniform ? _fill : TileType.Air));
        w.Write(!IsUniform);
        if (!IsUniform) w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Unpacked().AsSpan()));
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
        int nonAir = r.ReadInt32(), solid = r.ReadInt32();
        InitUniform((TileType)r.ReadByte());
        NonAirCount = nonAir;
        SolidCount = solid;
        if (r.ReadBoolean())
        {
            var tiles = new TileType[Volume];
            r.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(tiles.AsSpan()));
            Adopt(tiles);
        }
        StairCount = 0; // derived (not saved)
        if (IsUniform) StairCount = TileInfo.IsStair(_fill) ? Volume : 0;
        else for (int i = 0; i < Volume; i++) if (TileInfo.IsStair(At(i))) StairCount++;
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
