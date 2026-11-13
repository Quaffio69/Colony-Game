namespace Colony.Core.World;

/// <summary>Material occupying a single grid cell. Stored as a byte to keep large worlds compact.</summary>
public enum TileType : byte
{
    Air = 0,
    Grass,
    Soil,
    Stone,
    Ore,
    /// <summary>Stair cell leading up: connects to a <see cref="DownStair"/> or <see cref="UpDownStair"/> directly above.</summary>
    UpStair,
    /// <summary>Stair cell leading down: connects to an <see cref="UpStair"/> or <see cref="UpDownStair"/> directly below.</summary>
    DownStair,
    /// <summary>Both: stack these for a multi-level shaft (DF-style).</summary>
    UpDownStair,
}

public static class TileInfo
{
    /// <summary>Number of <see cref="TileType"/> values (for palettes and lookup tables).</summary>
    public const int Count = 8;

    /// <summary>Blocks movement: natural rock and earth. Stairs are walk-in cells, not solid.</summary>
    public static bool IsSolid(TileType t) => t is TileType.Grass or TileType.Soil or TileType.Stone or TileType.Ore;

    /// <summary>Any stair cell. An agent can stand in one regardless of what is below (the stair is its footing).</summary>
    public static bool IsStair(TileType t) => t is TileType.UpStair or TileType.DownStair or TileType.UpDownStair;

    public static bool GoesUp(TileType t) => t is TileType.UpStair or TileType.UpDownStair;
    public static bool GoesDown(TileType t) => t is TileType.DownStair or TileType.UpDownStair;

    /// <summary>Carries structural load for cave-ins: everything but air (stairs are carved rock).</summary>
    public static bool IsStructural(TileType t) => t != TileType.Air;

    /// <summary>A full opaque cube that hides its neighbours' touching faces.</summary>
    public static bool Occludes(TileType t) => IsSolid(t);
}
