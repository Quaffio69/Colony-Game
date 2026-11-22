using Colony.Core.World;

namespace Colony.Core.Nature;

/// <summary>Species of trees and surface plants.</summary>
public enum PlantKind : byte
{
    // Trees
    Oak,
    Birch,
    Pine,
    Spruce,
    Willow,
    // Surface plants
    Shrub,
    BerryBush,
    Fern,
    Flowers,
    Reeds,
    // Crops (sown on farm plots; see <see cref="Farms"/>)
    Wheat,
    Herbs,
    Glowcap,
}

/// <summary>
/// One tree or plant, standing in the open cell <see cref="Position"/> on a soil/grass floor. Not a voxel:
/// it doesn't block movement and is drawn as an instanced stand-in.
/// </summary>
/// <param name="Growth">Maturity, 0 (seedling) to 255 (fully grown). Will drive yields once harvesting exists.</param>
/// <param name="Variant">Per-plant random byte for visual variety (rotation, size and tint jitter).</param>
public readonly record struct Plant(TilePos Position, PlantKind Kind, byte Growth, byte Variant);

/// <summary>Static facts about each <see cref="PlantKind"/>.</summary>
public static class PlantInfo
{
    public const int KindCount = 13;

    public static bool IsTree(PlantKind k) => k <= PlantKind.Willow;

    /// <summary>Farmed: sown on plots, harvested whole when fully grown (then sown again).</summary>
    public static bool IsCrop(PlantKind k) => k >= PlantKind.Wheat;

    public static string Name(PlantKind k) => k switch
    {
        PlantKind.Oak => "Oak tree",
        PlantKind.Birch => "Birch tree",
        PlantKind.Pine => "Pine tree",
        PlantKind.Spruce => "Spruce tree",
        PlantKind.Willow => "Willow tree",
        PlantKind.Shrub => "Shrub",
        PlantKind.BerryBush => "Berry bush",
        PlantKind.Fern => "Ferns",
        PlantKind.Flowers => "Wildflowers",
        PlantKind.Reeds => "Reeds",
        PlantKind.Wheat => "Wheat",
        PlantKind.Herbs => "Herb garden",
        PlantKind.Glowcap => "Glowcaps",
        _ => k.ToString(),
    };

    /// <summary>Height in levels when fully grown (stand-in scale; also a hint for future wood yield).</summary>
    public static float MatureHeight(PlantKind k) => k switch
    {
        PlantKind.Oak => 5.5f,
        PlantKind.Birch => 6f,
        PlantKind.Pine => 7.5f,
        PlantKind.Spruce => 6.5f,
        PlantKind.Willow => 4.5f,
        PlantKind.Shrub => 0.8f,
        PlantKind.BerryBush => 0.9f,
        PlantKind.Fern => 0.5f,
        PlantKind.Flowers => 0.35f,
        PlantKind.Reeds => 1.1f,
        PlantKind.Wheat => 0.9f,
        PlantKind.Herbs => 0.5f,
        PlantKind.Glowcap => 0.45f,
        _ => 1f,
    };

    /// <summary>Growth a gatherable plant needs before it can be harvested.</summary>
    public const byte RipeGrowth = 160;
    /// <summary>Growth a plant is cut back to by gathering (it regrows once growth is simulated).</summary>
    public const byte GatheredGrowth = 40;

    /// <summary>What gathering this kind yields, or null if it can't be gathered.</summary>
    public static Items.ItemType? Harvest(PlantKind k) => k switch
    {
        PlantKind.BerryBush => Items.ItemType.Berries,
        PlantKind.Reeds or PlantKind.Shrub => Items.ItemType.Fiber,
        PlantKind.Fern or PlantKind.Flowers or PlantKind.Herbs => Items.ItemType.Herbs,
        PlantKind.Wheat => Items.ItemType.Grain,
        PlantKind.Glowcap => Items.ItemType.Glowcaps,
        _ => null,
    };

    public static bool IsGatherable(Plant p) => Harvest(p.Kind) is not null && p.Growth >= (IsCrop(p.Kind) ? (byte)255 : RipeGrowth);

    /// <summary>Units a ripe plant yields: 2-5, more the more grown it is.</summary>
    public static int HarvestYield(Plant p) => IsCrop(p.Kind) ? Balance.CropYield : 2 + (p.Growth - RipeGrowth) * 3 / (255 - RipeGrowth);

    /// <summary>Ticks a crop takes from sowing to harvest.</summary>
    public static int GrowTicks(PlantKind k) => k switch
    {
        PlantKind.Wheat => Balance.WheatGrowTicks,
        PlantKind.Herbs => Balance.HerbGrowTicks,
        PlantKind.Glowcap => Balance.GlowcapGrowTicks,
        _ => 0,
    };

    /// <summary>
    /// Plants grow in an open cell whose floor is grass or soil; glowcaps grow on any solid floor, but only under
    /// a roof (some solid tile above, within <see cref="RoofSearch"/> levels).
    /// </summary>
    public static bool CanGrowAt<T>(in T tiles, TilePos p, PlantKind kind = PlantKind.Shrub) where T : ITileReader
    {
        if (p.Z <= 0 || !tiles.InBounds(p.X, p.Y, p.Z) || tiles.Get(p.X, p.Y, p.Z) != TileType.Air) return false;
        var floor = tiles.Get(p.X, p.Y, p.Z - 1);
        if (kind != PlantKind.Glowcap) return floor is TileType.Grass or TileType.Soil;
        if (!TileInfo.IsSolid(floor)) return false;
        for (int z = p.Z + 1; z < p.Z + RoofSearch && tiles.InBounds(p.X, p.Y, z); z++)
            if (TileInfo.IsSolid(tiles.Get(p.X, p.Y, z))) return true;
        return false;
    }

    /// <summary>How far above a glowcap a roof may be.</summary>
    public const int RoofSearch = 24;
}
