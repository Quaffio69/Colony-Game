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
    /// <summary>Built stone wall (dams, canal sides): solid, carries load, digs back out as stone.</summary>
    Wall,
    /// <summary>
    /// An open cell with a built floor of its own (a house's upper storey, DF's constructed floor): stood on whatever is
    /// below, like a stair; its floor's material is the cell's floor cover. Not solid, carries no rock.
    /// </summary>
    Deck,
    /// <summary>Garnet: gem pockets from the middle strata down.</summary>
    Garnet,
    /// <summary>Sapphire: deep gem pockets.</summary>
    Sapphire,
    /// <summary>Ruby: deep gem pockets.</summary>
    Ruby,
    /// <summary>Emerald: deep gem pockets.</summary>
    Emerald,
    /// <summary>Diamond: the rarest gem, only just over the bedrock.</summary>
    Diamond,

    // Rock looks: palette entries for the kinds of natural rock (Procedural.Geology). Never stored in the world: natural
    // rock is Stone there (its look is slate) and its kind comes from the geology, by position.
    Sandstone,
    Limestone,
    Granite,
    Basalt,
    Bedrock,

    // Ground of the biomes (generator 2).
    /// <summary>Snow over the ground of cold peaks and polar lands: loose, dug like earth, leaves nothing.</summary>
    Snow,
    /// <summary>Dry grass of steppe, savanna and tundra: grows plants like grass.</summary>
    DryGrass,
    /// <summary>Swamp mud: wet ground that grows plants; dug like clay.</summary>
    Mud,
    /// <summary>Loose stones of scree, alpine slopes, cold shores and river beds: dug like sand, leaves stone now and then.</summary>
    Gravel,
}

public static class TileInfo
{
    /// <summary>Number of <see cref="TileType"/> values (for palettes and lookup tables).</summary>
    public const int Count = 33;

    /// <summary>Palette entry of unexplored rock (fog of war) on the cut surface; not a tile type.</summary>
    public const TileType Hidden = (TileType)62;

    /// <summary>Blocks movement: natural rock and earth. Stairs and decks are walk-in cells, not solid.</summary>
    public static bool IsSolid(TileType t) => t != TileType.Air && t != TileType.Deck && !IsStair(t);

    /// <summary>A walk-in cell that is its own footing: a stair or a deck.</summary>
    public static bool IsFooting(TileType t) => t == TileType.Deck || IsStair(t);

    /// <summary>Any stair cell. An agent can stand in one regardless of what is below (the stair is its footing).</summary>
    public static bool IsStair(TileType t) => t is TileType.UpStair or TileType.DownStair or TileType.UpDownStair;

    public static bool GoesUp(TileType t) => t is TileType.UpStair or TileType.UpDownStair;
    public static bool GoesDown(TileType t) => t is TileType.DownStair or TileType.UpDownStair;

    /// <summary>Carries structural load for cave-ins: everything but air (stairs are carved rock).</summary>
    public static bool IsStructural(TileType t) => t != TileType.Air && t != TileType.Deck;

    /// <summary>A full opaque cube that hides its neighbours' touching faces.</summary>
    public static bool Occludes(TileType t) => IsSolid(t);

    /// <summary>Metal ores and minerals (mined slowly; always leave their item).</summary>
    public static bool IsMineral(TileType t) => t is TileType.CopperOre or TileType.TinOre or TileType.IronOre or TileType.SilverOre
        or TileType.GoldOre or TileType.Quartz or TileType.Sulfur or TileType.Aetherite || IsGem(t);

    /// <summary>Gem pockets: deep, small and slow to work free; always leave their rough gem.</summary>
    public static bool IsGem(TileType t) => t is TileType.Garnet or TileType.Sapphire or TileType.Ruby or TileType.Emerald or TileType.Diamond;

    /// <summary>Loose earth: dug quickly.</summary>
    public static bool IsEarth(TileType t) => t is TileType.Grass or TileType.Soil or TileType.Clay or TileType.Sand
        or TileType.Snow or TileType.DryGrass or TileType.Mud or TileType.Gravel;

    /// <summary>Ground plants grow in: grass, dry grass, soil and mud.</summary>
    public static bool IsFertile(TileType t) => t is TileType.Grass or TileType.DryGrass or TileType.Soil or TileType.Mud;

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
        TileType.Deck => "Upper floor",
        TileType.DryGrass => "Dry grass",
        _ => t.ToString(),
    };
}
