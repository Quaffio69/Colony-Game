using Colony.Core.World;

namespace Colony.Core.Procedural;

/// <summary>The kinds of land and water a generated world is made of.</summary>
public enum Biome : byte
{
    Sea, Lake, River, Beach,
    Snow, Alpine, Volcanic, Tundra, Taiga, Steppe, Grassland, Forest, Jungle, Savanna, Desert, Badlands, Swamp,
}

/// <summary>
/// Biomes of the tectonic generator, column by column, from what shaped them: temperature (latitude, then 4.2 °C lost
/// per tenth of the world's height climbed), rain (the wind's, plus the damp near rivers and lakes), slope, volcanoes,
/// how broken the ground is and how low and flat. Small noise blurs every boundary into an ecotone. Also the ground a
/// biome lays down (top tile, depth of loose ground, what fills it), the soil's acidity (from the rock beneath and the
/// rain, which leaches it), and how good a place it is to land a colony.
/// </summary>
public static class Biomes
{
    /// <summary>What a column is and the climate it has.</summary>
    public readonly record struct Column(Biome Biome, float Temperature, float Rain, int Slope);

    public static string Name(Biome b) => b switch
    {
        Biome.Sea => "sea", Biome.Lake => "lake", Biome.River => "river", Biome.Beach => "beach",
        Biome.Snow => "snowfield", Biome.Alpine => "alpine rock", Biome.Volcanic => "volcanic slopes", Biome.Tundra => "tundra",
        Biome.Taiga => "taiga", Biome.Steppe => "steppe", Biome.Grassland => "grassland", Biome.Forest => "forest",
        Biome.Jungle => "jungle", Biome.Savanna => "savanna", Biome.Desert => "desert", Biome.Badlands => "badlands",
        _ => "swamp",
    };

    /// <summary>The steepest step to a land neighbour (water beside the land is its bank, not a cliff).</summary>
    public static int Slope(Terrain t, int x, int y)
    {
        int w = t.Width, i = y * w + x, s = t.Surface[i], slope = 0;
        int Drop(int nx, int ny) => t.Kind[ny * w + nx] == SurfaceKind.Land ? Math.Abs(t.Surface[ny * w + nx] - s) : 0;
        if (x > 0) slope = Math.Max(slope, Drop(x - 1, y));
        if (x < w - 1) slope = Math.Max(slope, Drop(x + 1, y));
        if (y > 0) slope = Math.Max(slope, Drop(x, y - 1));
        if (y < t.Depth - 1) slope = Math.Max(slope, Drop(x, y + 1));
        return slope;
    }

    /// <summary>The biome and climate of a column (generator 2; the first generator's worlds read as temperate).</summary>
    public static Column At(Terrain t, int x, int y)
    {
        int i = y * t.Width + x, z = t.Surface[i];
        var lf = t.Landform;
        if (lf == null)
        {
            var kind0 = t.Kind[i] switch { SurfaceKind.RiverBed => Biome.River, SurfaceKind.LakeBed => Biome.Lake, _ => Biome.Grassland };
            return new Column(kind0, 9f, 0.8f, t.Kind[i] == SurfaceKind.Land ? Slope(t, x, y) : 0);
        }
        int seed = t.Seed;
        float temp = lf.TemperatureAt(x, y, z) + (Noise.Fbm2D(x / 70f, y / 70f, seed + 301, 2) - 0.5f) * 3f;
        float rain = lf.RainAt(x, y) * (0.85f + 0.3f * Noise.Fbm2D(x / 90f, y / 90f, seed + 302, 2));
        if (t.Kind[i] == SurfaceKind.RiverBed) return new Column(Biome.River, temp, rain, 0);
        if (t.Kind[i] == SurfaceKind.LakeBed) return new Column(t.IsSea(i) ? Biome.Sea : Biome.Lake, temp, rain, 0);

        int slope = Slope(t, x, y);
        int wd = t.WaterDistance[i];
        rain += 0.5f * MathF.Max(0f, 1f - wd / 10f); // rivers and lakes green their banks (oases in the desert)
        float volcanic = lf.At(lf.Volcanic, x, y), rugged = lf.At(lf.Rugged, x, y);
        int low = (t.SeaLevel > 0 ? t.SeaLevel : lf.BaseLevel) + (int)(0.07f * t.Height);

        if (temp < -3f) return new Column(Biome.Snow, temp, rain, slope);
        if (temp < 0f && slope >= 2) return new Column(Biome.Alpine, temp, rain, slope);
        if (volcanic > 0.75f || volcanic > 0.55f && slope >= 1) return new Column(Biome.Volcanic, temp, rain, slope);
        bool shore = t.SeaLevel > 0 && wd <= 4 && z <= t.SeaLevel + 2 || wd <= 1;
        if (shore && slope <= 1) return new Column(Biome.Beach, temp, rain, slope);
        if (temp < 1.5f) return new Column(Biome.Tundra, temp, rain, slope);
        if (temp >= 4f && rain >= 0.75f && wd <= 14 && slope <= 1 && z <= low && rugged < 0.2f &&
            Noise.Fbm2D(x / 110f, y / 110f, seed + 303, 3) > 0.45f)
            return new Column(Biome.Swamp, temp, rain, slope);
        if (temp < 6f) return new Column(rain < 0.3f ? Biome.Steppe : Biome.Taiga, temp, rain, slope);
        if (rain < 0.2f && temp >= 11f) return new Column(rugged > 0.25f ? Biome.Badlands : Biome.Desert, temp, rain, slope);
        if (rain < 0.36f) return new Column(temp >= 17f ? Biome.Savanna : temp < 10f ? Biome.Steppe : Biome.Grassland, temp, rain, slope);
        if (temp >= 19f && rain >= 0.95f) return new Column(Biome.Jungle, temp, rain, slope);
        if (temp >= 17f && rain < 0.72f) return new Column(Biome.Savanna, temp, rain, slope);
        if (rain >= 0.72f) return new Column(Biome.Forest, temp, rain, slope);
        return new Column(Biome.Grassland, temp, rain, slope);
    }

    /// <summary>
    /// The ground of a column: its top tile, how many levels of loose ground (top included) lie on the rock, and what
    /// fills them under the top. Cliffs (3+ levels) are bare rock everywhere.
    /// </summary>
    public static (TileType Top, int Soil, TileType Sub) Ground(Terrain t, int x, int y, in Column c)
    {
        int seed = t.Seed, i = y * t.Width + x;
        float patch = Noise.Fbm2D(x / 23f, y / 23f, seed + 811, 2);
        int soil = 1 + (c.Slope <= 1 ? 2 : 0) + (int)(Noise.Hash(x, y, 7, seed) * 2); // 1..4
        bool cold = c.Temperature < 0f;
        switch (c.Biome)
        {
            case Biome.River: return cold ? (TileType.Gravel, 3, TileType.Clay) : (TileType.Sand, 3, TileType.Clay);
            case Biome.Lake: return (TileType.Clay, 3, TileType.Clay);
            case Biome.Sea: return t.Water[i] <= 4 ? (cold ? TileType.Gravel : TileType.Sand, 3, TileType.Sand) : (TileType.Clay, 2, TileType.Sand);
        }
        if (c.Slope >= 3) return (TileType.Stone, 0, TileType.Stone);
        switch (c.Biome)
        {
            case Biome.Snow: return c.Slope >= 2 ? (TileType.Snow, 1, TileType.Stone) : (TileType.Snow, 2, TileType.Gravel);
            case Biome.Alpine: return c.Slope >= 2 ? (TileType.Stone, 0, TileType.Stone) : (TileType.Gravel, 2, TileType.Gravel);
            case Biome.Volcanic: return c.Slope >= 2 ? (TileType.Stone, 0, TileType.Stone) : (TileType.Gravel, 2, TileType.Gravel);
            case Biome.Beach: return cold ? (TileType.Gravel, 3, TileType.Gravel) : (TileType.Sand, Math.Max(soil, 3), TileType.Sand);
            case Biome.Tundra:
                if (c.Temperature < -1f && patch > 0.55f) return (TileType.Snow, 2, TileType.Soil);
                return patch < 0.36f ? (TileType.Gravel, 2, TileType.Soil) : (TileType.DryGrass, 2, TileType.Soil);
            case Biome.Steppe:
            case Biome.Savanna:
                return (TileType.DryGrass, soil, patch > 0.68f ? TileType.Clay : TileType.Soil);
            case Biome.Desert:
                if (c.Slope >= 2) return (TileType.Stone, 0, TileType.Stone);
                return patch < 0.3f ? (TileType.Gravel, 2, TileType.Sand) : (TileType.Sand, 4 + (int)(Noise.Hash(x, y, 8, seed) * 3), TileType.Sand);
            case Biome.Badlands:
                if (c.Slope >= 2) return (TileType.Stone, 0, TileType.Stone);
                return patch < 0.45f ? (TileType.Clay, 2, TileType.Clay) : (TileType.Sand, 2, TileType.Clay);
            case Biome.Swamp: return (TileType.Mud, 3, TileType.Clay);
        }
        // Grassland, forest, jungle, taiga: grass over soil, clay beds in patches (commoner in damp ground).
        float damp = 1f - Math.Min(1f, t.WaterDistance[i] / 40f);
        bool clay = patch > 0.74f - 0.14f * damp;
        return (TileType.Grass, Math.Max(soil, clay ? 3 : 1), clay ? TileType.Clay : TileType.Soil);
    }

    /// <summary>
    /// Soil pH (about 4.5 .. 8.2): the rock under the soil sets it (granite and sandstone acid, limestone alkaline, slate
    /// and basalt between), heavy rain leaches it towards acid, and a little noise. Acid soil favours conifers, birch
    /// and berry bushes; alkaline soil oaks, shrubs and meadow flowers.
    /// </summary>
    public static float Acidity(Terrain t, VoxelWorld world, int x, int y, in Column c)
    {
        int z = Math.Max(1, t.Surface[y * t.Width + x] - 3);
        float rock = world.Geology?.RockAt(x, y, z) switch
        {
            Rock.Granite => 5.2f, Rock.Sandstone => 5.5f, Rock.Limestone => 7.8f, Rock.Basalt => 6.6f, _ => 6.2f,
        };
        return rock - 0.9f * Math.Clamp(c.Rain - 0.4f, 0f, 1.2f) + (Noise.Fbm2D(x / 60f, y / 60f, t.Seed + 305, 2) - 0.5f) * 0.8f;
    }

    /// <summary>How good a biome is to land a colony in (0..1): mild, green, buildable places first.</summary>
    public static float Habitability(Biome b) => b switch
    {
        Biome.Grassland or Biome.Forest => 1f, Biome.Savanna => 0.7f, Biome.Taiga => 0.6f, Biome.Steppe => 0.5f,
        Biome.Jungle => 0.5f, Biome.Beach => 0.35f, Biome.Badlands => 0.2f, Biome.Tundra => 0.2f, Biome.Desert => 0.15f,
        Biome.Swamp or Biome.Volcanic => 0.1f, _ => 0f,
    };

    /// <summary>Map colour of a biome (loading-screen map), RGB 0..1.</summary>
    public static (float R, float G, float B) Colour(Biome b) => b switch
    {
        Biome.Sea => (0.16f, 0.34f, 0.55f), Biome.Lake => (0.22f, 0.45f, 0.66f), Biome.River => (0.26f, 0.52f, 0.74f),
        Biome.Beach => (0.86f, 0.79f, 0.56f), Biome.Snow => (0.93f, 0.95f, 0.98f), Biome.Alpine => (0.58f, 0.56f, 0.55f),
        Biome.Volcanic => (0.30f, 0.27f, 0.28f), Biome.Tundra => (0.62f, 0.62f, 0.50f), Biome.Taiga => (0.20f, 0.38f, 0.28f),
        Biome.Steppe => (0.70f, 0.66f, 0.40f), Biome.Grassland => (0.47f, 0.66f, 0.30f), Biome.Forest => (0.24f, 0.47f, 0.20f),
        Biome.Jungle => (0.12f, 0.42f, 0.16f), Biome.Savanna => (0.76f, 0.68f, 0.36f), Biome.Desert => (0.90f, 0.78f, 0.50f),
        Biome.Badlands => (0.72f, 0.43f, 0.28f), _ => (0.33f, 0.40f, 0.26f),
    };
}
