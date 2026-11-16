namespace Colony.Core.World;

/// <summary>Material occupying a single grid cell. Stored as a byte to keep large worlds compact.</summary>
public enum TileType : byte
{
    Air = 0,
    Grass,
    Soil,
    Stone,
    /// <summary>Copper veins: the upper strata.</summary>
    CopperOre,
    /// <summary>Stair cell leading up: connects to a <see cref="DownStair"/> or <see cref="UpDownStair"/> directly above.</summary>
    UpStair,
    /// <summary>Stair cell leading down: connects to an <see cref="UpStair"/> or <see cref="UpDownStair"/> directly below.</summary>
    DownStair,
    /// <summary>Both: stack these for a multi-level shaft (DF-style).</summary>
    UpDownStair,
    /// <summary>Clay beds: lake beds, under riverbanks, and lenses in the soil.</summary>
    Clay,
    /// <summary>Sand: riverbeds and beaches.</summary>
    Sand,
    TinOre,
    /// <summary>Iron veins: the middle strata.</summary>
    IronOre,
    /// <summary>Silver veins: deep.</summary>
    SilverOre,
    /// <summary>Gold veins: deep.</summary>
    GoldOre,
    /// <summary>Quartz geodes, at any depth.</summary>
    Quartz,
    /// <summary>Sulfur pockets in deep, hot rock.</summary>
    Sulfur,
    /// <summary>Aetherite: the deepest strata, faintly glowing.</summary>
    Aetherite,
}

public static class TileInfo
{
    /// <summary>Number of <see cref="TileType"/> values (for palettes and lookup tables).</summary>
    public const int Count = 17;

    /// <summary>Blocks movement: natural rock and earth. Stairs are walk-in cells, not solid.</summary>
    public static bool IsSolid(TileType t) => t != TileType.Air && !IsStair(t);

    /// <summary>Any stair cell. An agent can stand in one regardless of what is below (the stair is its footing).</summary>
    public static bool IsStair(TileType t) => t is TileType.UpStair or TileType.DownStair or TileType.UpDownStair;

    public static bool GoesUp(TileType t) => t is TileType.UpStair or TileType.UpDownStair;
    public static bool GoesDown(TileType t) => t is TileType.DownStair or TileType.UpDownStair;

    /// <summary>Carries structural load for cave-ins: everything but air (stairs are carved rock).</summary>
    public static bool IsStructural(TileType t) => t != TileType.Air;

    /// <summary>A full opaque cube that hides its neighbours' touching faces.</summary>
    public static bool Occludes(TileType t) => IsSolid(t);

    /// <summary>Metal ores and minerals (mined slowly; always leave their item).</summary>
    public static bool IsMineral(TileType t) => t is TileType.CopperOre or TileType.TinOre or TileType.IronOre or TileType.SilverOre
        or TileType.GoldOre or TileType.Quartz or TileType.Sulfur or TileType.Aetherite;

    /// <summary>Loose earth: dug quickly.</summary>
    public static bool IsEarth(TileType t) => t is TileType.Grass or TileType.Soil or TileType.Clay or TileType.Sand;

    public static string Name(TileType t) => t switch
    {
        TileType.CopperOre => "Copper ore",
        TileType.TinOre => "Tin ore",
        TileType.IronOre => "Iron ore",
        TileType.SilverOre => "Silver ore",
        TileType.GoldOre => "Gold ore",
        TileType.UpStair => "Up stair",
        TileType.DownStair => "Down stair",
        TileType.UpDownStair => "Up/down stair",
        _ => t.ToString(),
    };
}
