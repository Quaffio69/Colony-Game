namespace Colony.Core.World;

/// <summary>
/// Dense 3D tile grid. Coordinates follow the simulation convention:
/// x/y are horizontal, z is the vertical level (z = 0 is the bottom), like Dwarf Fortress z-levels.
/// The renderer is responsible for mapping this to engine axes.
/// </summary>
public sealed class VoxelWorld
{
    private readonly TileType[] _tiles;

    public int Width { get; }   // x
    public int Depth { get; }   // y
    public int Height { get; }  // z (levels)

    public VoxelWorld(int width, int depth, int height)
    {
        if (width <= 0 || depth <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "World dimensions must be positive.");

        Width = width;
        Depth = depth;
        Height = height;
        _tiles = new TileType[width * depth * height];
    }

    public bool InBounds(int x, int y, int z) =>
        (uint)x < (uint)Width && (uint)y < (uint)Depth && (uint)z < (uint)Height;

    /// <summary>Returns the tile at a position; out-of-bounds reads return Air.</summary>
    public TileType Get(int x, int y, int z) =>
        InBounds(x, y, z) ? _tiles[Index(x, y, z)] : TileType.Air;

    public void Set(int x, int y, int z, TileType type)
    {
        if (!InBounds(x, y, z))
            throw new ArgumentOutOfRangeException(nameof(x), $"({x},{y},{z}) is outside the world.");
        _tiles[Index(x, y, z)] = type;
    }

    public bool IsSolid(int x, int y, int z) => Get(x, y, z) != TileType.Air;

    /// <summary>Highest solid level in a column, or -1 if the column is empty.</summary>
    public int SurfaceLevel(int x, int y)
    {
        for (int z = Height - 1; z >= 0; z--)
            if (IsSolid(x, y, z))
                return z;
        return -1;
    }

    private int Index(int x, int y, int z) => (z * Depth + y) * Width + x;
}
