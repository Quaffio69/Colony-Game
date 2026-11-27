using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Nature;

/// <summary>
/// Scatters trees and surface plants over generated terrain. Placement uses jittered grids (one candidate
/// per cell, so plants never clump onto the same tile) accepted by a density built from:
/// <list type="bullet">
/// <item>forest patches (low-frequency noise): woods, groves and open meadows;</item>
/// <item>moisture (distance to rivers and lakes): denser and greener near water, reeds and willows on banks;</item>
/// <item>elevation: broadleaf in the lowlands, conifers higher up, nothing above the treeline;</item>
/// <item>footing: grass or soil only, never in river or lake beds or on steep, bare rock.</item>
/// </list>
/// Regions are generated in parallel and merged in a fixed order; output depends only on the seed.
/// </summary>
public static class FloraGenerator
{
    private const int TreeCell = 3;  // at most one tree per 3x3 tiles
    private const int PlantCell = 2; // at most one surface plant per 2x2 tiles

    public static Flora Generate(WorldGenSettings s, Terrain t, VoxelWorld world, WorldGenProgress? progress = null)
    {
        var flora = new Flora(world);
        int regions = flora.RegionsX * flora.RegionsY;
        var lists = new List<Plant>[regions];
        int done = 0, trees = 0;
        Parallel.For(0, regions, r =>
        {
            int rx = r % flora.RegionsX, ry = r / flora.RegionsX;
            lists[r] = t.Landform != null
                ? GenerateBiomeRegion(s, t, world, rx * Flora.RegionSize, ry * Flora.RegionSize)
                : GenerateRegion(s, t, world, rx * Flora.RegionSize, ry * Flora.RegionSize);
            CaveGlowcaps(s, world, rx * Flora.RegionSize, ry * Flora.RegionSize, lists[r]);
            if (progress == null) return;
            int grown = Interlocked.Add(ref trees, lists[r].Count(p => PlantInfo.IsTree(p.Kind)));
            int n = Interlocked.Increment(ref done);
            if ((n & 15) == 0 || n == regions)
            {
                progress.Retitle($"Growing {grown:N0} trees");
                progress.Within((float)n / regions);
            }
        });
        foreach (var list in lists) flora.AddGenerated(list);
        if (progress != null) progress.Retitle($"Growing {lists.Sum(l => l.Count(p => PlantInfo.IsTree(p.Kind))):N0} trees");
        return flora;
    }

    /// <summary>
    /// Flora of the tectonic generator: trees and undergrowth by biome (dense jungle and taiga, woods and groves in the
    /// temperate lands, lone trees on the savanna, next to nothing in deserts and on the ice), species by temperature,
    /// water and soil acidity (<see cref="Biomes.Acidity"/>: conifers, birch and berries on acid ground, oaks, shrubs
    /// and flowers on alkaline), reeds along every warm shore.
    /// </summary>
    private static List<Plant> GenerateBiomeRegion(WorldGenSettings s, Terrain t, VoxelWorld world, int x0, int y0)
    {
        var result = new List<Plant>();
        var trees = new HashSet<(int, int)>();
        int x1 = Math.Min(x0 + Flora.RegionSize, s.Width), y1 = Math.Min(y0 + Flora.RegionSize, s.Depth);

        for (int gy = y0 / TreeCell; gy * TreeCell < y1; gy++)
        for (int gx = x0 / TreeCell; gx * TreeCell < x1; gx++)
        {
            int x = gx * TreeCell + (int)(Noise.Hash(gx, gy, 1, s.Seed + 700) * TreeCell);
            int y = gy * TreeCell + (int)(Noise.Hash(gx, gy, 2, s.Seed + 700) * TreeCell);
            if (x < x0 || y < y0 || x >= x1 || y >= y1) continue;
            if (!Growable(s, t, world, x, y, out int surface)) continue;
            var c = Biomes.At(t, x, y);
            float forest = Forest(s, x, y);
            int wd = t.WaterDistance[y * s.Width + x];
            float density = c.Biome switch
            {
                Biome.Jungle => 0.86f,
                Biome.Taiga => 0.45f + 0.35f * forest,
                Biome.Forest => 0.38f + 0.5f * forest,
                Biome.Swamp => 0.35f,
                Biome.Grassland => 0.015f + 0.6f * MathF.Max(0f, forest - 0.55f), // open land with groves
                Biome.Savanna => 0.045f,
                Biome.Steppe => 0.008f,
                Biome.Tundra => c.Temperature > 0f ? 0.02f : 0f,
                Biome.Beach => 0.02f,
                _ => 0f,
            };
            if (density <= 0) continue;
            density = Math.Clamp(density + 0.15f * MathF.Max(0f, 1f - wd / 30f), 0f, 0.92f);
            density *= Math.Clamp((c.Temperature + 1f) / 4f, 0f, 1f); // the tree line
            if (Noise.Hash(gx, gy, 3, s.Seed + 700) >= density) continue;

            float roll = Noise.Hash(gx, gy, 4, s.Seed + 700);
            float acid = Biomes.Acidity(t, world, x, y, c);
            PlantKind kind;
            if (c.Biome == Biome.Swamp && roll < 0.7f || wd <= 5 && roll < 0.5f && c.Temperature > 3f) kind = PlantKind.Willow;
            else if (c.Biome is Biome.Taiga or Biome.Tundra || c.Temperature < 6f)
                kind = roll < 0.55f + (6f - acid) * 0.1f ? PlantKind.Spruce : PlantKind.Pine;
            else if (c.Biome == Biome.Jungle) kind = roll < 0.75f ? PlantKind.Oak : PlantKind.Willow;
            else if (c.Temperature < 10f && roll < 0.45f) kind = acid < 5.8f ? PlantKind.Pine : PlantKind.Spruce;
            else if (acid < 5.8f && roll < 0.55f || Noise.Fbm2D(x / 90f, y / 90f, s.Seed + 92, 3) > 0.6f) kind = PlantKind.Birch;
            else kind = PlantKind.Oak;
            result.Add(MakePlant(s, x, y, surface, kind, gx, gy, mostlyMature: true));
            trees.Add((x, y));
        }

        for (int gy = y0 / PlantCell; gy * PlantCell < y1; gy++)
        for (int gx = x0 / PlantCell; gx * PlantCell < x1; gx++)
        {
            int x = gx * PlantCell + (int)(Noise.Hash(gx, gy, 11, s.Seed + 800) * PlantCell);
            int y = gy * PlantCell + (int)(Noise.Hash(gx, gy, 12, s.Seed + 800) * PlantCell);
            if (x < x0 || y < y0 || x >= x1 || y >= y1 || trees.Contains((x, y))) continue;
            if (!Growable(s, t, world, x, y, out int surface)) continue;
            var c = Biomes.At(t, x, y);
            float roll = Noise.Hash(gx, gy, 13, s.Seed + 800);
            float pick = Noise.Hash(gx, gy, 14, s.Seed + 800);
            int wd = t.WaterDistance[y * s.Width + x];
            PlantKind? kind = null;
            if (wd <= 2 && c.Temperature > 2f)
            {
                if (roll < 0.55f) kind = PlantKind.Reeds;
            }
            else
            {
                float acid = Biomes.Acidity(t, world, x, y, c);
                float meadow = Noise.Fbm2D(x / 70f, y / 70f, s.Seed + 81, 3);
                switch (c.Biome)
                {
                    case Biome.Jungle:
                        if (roll < 0.45f) kind = pick < 0.65f ? PlantKind.Fern : pick < 0.85f ? PlantKind.Shrub : PlantKind.BerryBush;
                        break;
                    case Biome.Forest:
                    case Biome.Taiga:
                        if (roll < 0.22f) kind = pick < 0.45f ? PlantKind.Fern : pick < (acid < 6f ? 0.6f : 0.8f) ? PlantKind.Shrub : PlantKind.BerryBush;
                        break;
                    case Biome.Swamp:
                        if (roll < 0.45f) kind = pick < 0.65f ? PlantKind.Reeds : PlantKind.Fern;
                        break;
                    case Biome.Grassland:
                        if (roll < 0.03f + Math.Max(0f, meadow - 0.5f) * 0.5f)
                            kind = pick < (acid > 7f ? 0.7f : 0.5f) ? PlantKind.Flowers : pick < 0.8f ? PlantKind.Shrub : PlantKind.BerryBush;
                        break;
                    case Biome.Savanna:
                    case Biome.Steppe:
                        if (roll < 0.06f) kind = pick < 0.75f ? PlantKind.Shrub : PlantKind.Flowers;
                        break;
                    case Biome.Tundra:
                        if (roll < 0.08f) kind = pick < 0.55f ? PlantKind.Shrub : PlantKind.BerryBush;
                        break;
                    case Biome.Beach:
                    case Biome.Alpine:
                    case Biome.Volcanic:
                        if (roll < 0.03f) kind = PlantKind.Shrub;
                        break;
                }
            }
            if (kind is { } k) result.Add(MakePlant(s, x, y, surface, k, gx, gy, mostlyMature: false));
        }
        return result;
    }

    /// <summary>Dry land with a fertile top (grass, dry grass, soil, mud) and open air above.</summary>
    private static bool Growable(WorldGenSettings s, Terrain t, VoxelWorld world, int x, int y, out int surface)
    {
        int i = y * s.Width + x;
        surface = t.Surface[i];
        return t.Kind[i] == SurfaceKind.Land && surface + 1 < s.Height && TileInfo.IsFertile(world.Get(x, y, surface));
    }

    /// <summary>
    /// Wild glowcaps on cavern floors, in patches (ripe: gather them once the cavern is found). Read from the cavern spans,
    /// so only cells the generator really carved (open, rock beneath) get one.
    /// </summary>
    private static void CaveGlowcaps(WorldGenSettings s, VoxelWorld world, int x0, int y0, List<Plant> into)
    {
        if (world.Geology is not { CavernLayers.Length: > 0 } g) return;
        int x1 = Math.Min(x0 + Flora.RegionSize, s.Width), y1 = Math.Min(y0 + Flora.RegionSize, s.Depth);
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            if (Noise.Hash(x, y, 3, s.Seed + 740) > 0.18f) continue;
            for (int layer = 0; layer < g.CavernLayers.Length; layer++)
            {
                if (Noise.Value2D(x / 14f, y / 14f, s.Seed + 741 + layer) < 0.62f) continue; // patches
                if (!g.CavernSpan(x, y, layer, out int floor, out _)) continue;
                var p = new TilePos(x, y, floor);
                if (world.Get(p) == TileType.Air && world.IsUnseen(p) && TileInfo.IsSolid(world.Get(x, y, floor - 1)))
                    into.Add(new Plant(p, PlantKind.Glowcap, 255, (byte)(Noise.Hash(x, y, 4, s.Seed + 740) * 4)));
            }
        }
    }

    /// <summary>Highest surface level trees grow on.</summary>
    public static int TreeLine(WorldGenSettings s) => (int)(s.Height * 0.74f);

    private static List<Plant> GenerateRegion(WorldGenSettings s, Terrain t, VoxelWorld world, int x0, int y0)
    {
        var result = new List<Plant>();
        var trees = new HashSet<(int, int)>();
        int x1 = Math.Min(x0 + Flora.RegionSize, s.Width), y1 = Math.Min(y0 + Flora.RegionSize, s.Depth);
        int treeLine = TreeLine(s);

        // Trees. A grid cell can straddle two regions; its (hash-fixed) position lies in exactly one of them,
        // and only that region keeps it.
        for (int gy = y0 / TreeCell; gy * TreeCell < y1; gy++)
        for (int gx = x0 / TreeCell; gx * TreeCell < x1; gx++)
        {
            int x = gx * TreeCell + (int)(Noise.Hash(gx, gy, 1, s.Seed + 700) * TreeCell);
            int y = gy * TreeCell + (int)(Noise.Hash(gx, gy, 2, s.Seed + 700) * TreeCell);
            if (x < x0 || y < y0 || x >= x1 || y >= y1) continue;
            if (!Fertile(s, t, world, x, y, out int surface)) continue;
            if (surface > treeLine) continue;

            float forest = Forest(s, x, y);
            float moisture = Moisture(t, x, y);
            float altitude = (float)surface / s.Height;
            float taper = Math.Clamp((treeLine - surface) / 12f, 0f, 1f); // thinning towards the treeline
            float density = Math.Clamp(forest * 0.8f + moisture * 0.25f, 0f, 0.92f) * taper;
            if (Noise.Hash(gx, gy, 3, s.Seed + 700) >= density) continue;

            var kind = TreeSpecies(s, x, y, altitude, t.WaterDistance[y * s.Width + x], Noise.Hash(gx, gy, 4, s.Seed + 700));
            result.Add(MakePlant(s, x, y, surface, kind, gx, gy, mostlyMature: true));
            trees.Add((x, y));
        }

        // Surface plants: undergrowth in the woods, flowers and shrubs in the open, reeds on the banks.
        for (int gy = y0 / PlantCell; gy * PlantCell < y1; gy++)
        for (int gx = x0 / PlantCell; gx * PlantCell < x1; gx++)
        {
            int x = gx * PlantCell + (int)(Noise.Hash(gx, gy, 11, s.Seed + 800) * PlantCell);
            int y = gy * PlantCell + (int)(Noise.Hash(gx, gy, 12, s.Seed + 800) * PlantCell);
            if (x < x0 || y < y0 || x >= x1 || y >= y1 || trees.Contains((x, y))) continue;
            if (!Fertile(s, t, world, x, y, out int surface)) continue;

            float roll = Noise.Hash(gx, gy, 13, s.Seed + 800);
            float pick = Noise.Hash(gx, gy, 14, s.Seed + 800);
            int water = t.WaterDistance[y * s.Width + x];
            PlantKind? kind = null;
            if (water <= 2)
            {
                if (roll < 0.55f) kind = PlantKind.Reeds;
            }
            else if (surface > treeLine)
            {
                if (roll < 0.04f) kind = PlantKind.Shrub;
            }
            else
            {
                float forest = Forest(s, x, y);
                float meadow = Noise.Fbm2D(x / 70f, y / 70f, s.Seed + 81, 3);
                if (forest > 0.5f)
                {
                    if (roll < 0.22f) kind = pick < 0.5f ? PlantKind.Fern : pick < 0.7f ? PlantKind.Shrub : PlantKind.BerryBush;
                }
                else if (roll < 0.03f + Math.Max(0f, meadow - 0.5f) * 0.5f)
                {
                    kind = pick < 0.55f ? PlantKind.Flowers : pick < 0.78f ? PlantKind.Shrub : PlantKind.BerryBush;
                }
            }
            if (kind is { } k) result.Add(MakePlant(s, x, y, surface, k, gx, gy, mostlyMature: false));
        }
        return result;
    }

    /// <summary>Dry land with a grass top and open air above.</summary>
    private static bool Fertile(WorldGenSettings s, Terrain t, VoxelWorld world, int x, int y, out int surface)
    {
        int i = y * s.Width + x;
        surface = t.Surface[i];
        return t.Kind[i] == SurfaceKind.Land && surface + 1 < s.Height && world.Get(x, y, surface) == TileType.Grass;
    }

    /// <summary>0..1: how wooded this area is (patches a few hundred tiles across, with some open glades).</summary>
    private static float Forest(WorldGenSettings s, int x, int y)
    {
        float n = Noise.Fbm2D(x / 240f, y / 240f, s.Seed + 71, 4);
        return Math.Clamp((n - 0.42f) / 0.2f, 0f, 1f);
    }

    /// <summary>0..1: wetter near rivers and lakes.</summary>
    private static float Moisture(Terrain t, int x, int y) =>
        1f - Math.Min(1f, t.WaterDistance[y * t.Width + x] / 60f);

    private static PlantKind TreeSpecies(WorldGenSettings s, int x, int y, float altitude, int waterDistance, float roll)
    {
        if (waterDistance <= 5 && roll < 0.6f) return PlantKind.Willow;
        // Conifers take over with altitude; patchy noise mixes the boundary.
        float conifer = Math.Clamp((altitude - 0.50f) / 0.14f, 0f, 1f)
                        + (Noise.Fbm2D(x / 180f, y / 180f, s.Seed + 91, 3) - 0.5f) * 0.8f;
        if (roll < conifer) return altitude > 0.62f || roll < conifer * 0.4f ? PlantKind.Spruce : PlantKind.Pine;
        // Broadleaf: birch groves within oak woods.
        return Noise.Fbm2D(x / 90f, y / 90f, s.Seed + 92, 3) > 0.56f ? PlantKind.Birch : PlantKind.Oak;
    }

    private static Plant MakePlant(WorldGenSettings s, int x, int y, int surface, PlantKind kind, int gx, int gy, bool mostlyMature)
    {
        float g = Noise.Hash(gx, gy, 21, s.Seed + (int)kind * 31);
        // Trees: mostly mature, some saplings. Small plants: late summer, most of them ripe (wild food to start on).
        byte growth = (byte)(mostlyMature ? (g < 0.15f ? 40 + g * 900 : 200 + g * 55) : 120 + g * 135);
        byte variant = (byte)(Noise.Hash(gx, gy, 22, s.Seed + 900) * 255);
        return new Plant(new TilePos(x, y, surface + 1), kind, growth, variant);
    }
}
