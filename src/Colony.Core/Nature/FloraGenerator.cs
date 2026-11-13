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

    public static Flora Generate(WorldGenSettings s, Terrain t, VoxelWorld world)
    {
        var flora = new Flora(world);
        int regions = flora.RegionsX * flora.RegionsY;
        var lists = new List<Plant>[regions];
        Parallel.For(0, regions, r =>
        {
            int rx = r % flora.RegionsX, ry = r / flora.RegionsX;
            lists[r] = GenerateRegion(s, t, world, rx * Flora.RegionSize, ry * Flora.RegionSize);
        });
        foreach (var list in lists) flora.AddGenerated(list);
        return flora;
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
                    if (roll < 0.22f) kind = pick < 0.6f ? PlantKind.Fern : pick < 0.85f ? PlantKind.Shrub : PlantKind.BerryBush;
                }
                else if (roll < 0.03f + Math.Max(0f, meadow - 0.5f) * 0.5f)
                {
                    kind = pick < 0.65f ? PlantKind.Flowers : pick < 0.88f ? PlantKind.Shrub : PlantKind.BerryBush;
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
        // Trees: mostly mature, some saplings. Small plants: any stage.
        byte growth = (byte)(mostlyMature ? (g < 0.15f ? 40 + g * 900 : 200 + g * 55) : 80 + g * 175);
        byte variant = (byte)(Noise.Hash(gx, gy, 22, s.Seed + 900) * 255);
        return new Plant(new TilePos(x, y, surface + 1), kind, growth, variant);
    }
}
