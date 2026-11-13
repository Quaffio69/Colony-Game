namespace Colony.Core.World;

/// <summary>Material occupying a single grid cell. Stored as a byte to keep large worlds compact.</summary>
public enum TileType : byte
{
    Air = 0,
    Grass,
    Soil,
    Stone,
    Ore,
    /// <summary>A solid block carved into a stair step. Climbing onto or off one is much faster than a raw climb.</summary>
    Stairs,
}

public static class TileInfo
{
    /// <summary>Number of <see cref="TileType"/> values (for palettes and lookup tables).</summary>
    public const int Count = 6;

    /// <summary>Solid for movement and picking.</summary>
    public static bool IsSolid(TileType t) => t != TileType.Air;

    /// <summary>A full opaque cube that hides its neighbours' touching faces. Stairs are solid but not full cubes.</summary>
    public static bool Occludes(TileType t) => t != TileType.Air && t != TileType.Stairs;
}
