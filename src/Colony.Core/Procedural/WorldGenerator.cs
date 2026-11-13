using Colony.Core.World;

namespace Colony.Core.Procedural;

public sealed record WorldGenSettings(int Seed, int Width = 64, int Depth = 64, int Height = 40);

/// <summary>
/// Generates rolling valleys with a central mountain plus scattered ranges: grass over soil over stone,
/// with ore veins. Feature sizes are in absolute tiles (not proportional to the map), so bigger maps get
/// more terrain rather than stretched terrain. Columns are generated in parallel, one chunk column per
/// work item; the output depends only on the seed.
/// </summary>
public static class WorldGenerator
{
    public static VoxelWorld Generate(WorldGenSettings s)
    {
        var world = new VoxelWorld(s.Width, s.Depth, s.Height);

        Parallel.For(0, world.ChunksX * world.ChunksY, column =>
        {
            int x0 = (column % world.ChunksX) * Chunk.Size;
            int y0 = (column / world.ChunksX) * Chunk.Size;
            int x1 = Math.Min(x0 + Chunk.Size, s.Width), y1 = Math.Min(y0 + Chunk.Size, s.Depth);

            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int surface = SurfaceHeight(s, x, y);
                int soilDepth = 1 + (int)(Noise.Hash(x, y, 7, s.Seed) * 3); // 1..3 levels of soil

                for (int z = 0; z <= surface; z++)
                {
                    TileType type;
                    if (z == surface) type = TileType.Grass;
                    else if (z > surface - soilDepth) type = TileType.Soil;
                    else type = IsOre(s.Seed, x, y, z) ? TileType.Ore : TileType.Stone;

                    world.SetForGeneration(x, y, z, type);
                }
            }
        });

        return world;
    }

    /// <summary>Radius of the central mountain, in tiles.</summary>
    public static float CentralMountainRadius(WorldGenSettings s) => MathF.Min(MathF.Min(s.Width, s.Depth) * 0.3f, 140f);

    /// <summary>A spot in the valley just south of the central mountain's foot.</summary>
    public static (int X, int Y) SuggestedSpawn(WorldGenSettings s)
    {
        float r = CentralMountainRadius(s);
        return (s.Width / 2, Math.Max(2, (int)(s.Depth / 2f - r * 1.3f)));
    }

    private static int SurfaceHeight(WorldGenSettings s, int x, int y)
    {
        float r = CentralMountainRadius(s);

        // Central mountain: strongest at the centre, warped by noise so it isn't a perfect cone.
        float nx = (x - s.Width / 2f) / r, ny = (y - s.Depth / 2f) / r;
        float dist = MathF.Sqrt(nx * nx + ny * ny);
        float warp = Noise.Fbm2D(x / r * 3f, y / r * 3f, s.Seed + 31) - 0.5f;
        float central = Smooth(Math.Clamp(1f - (dist + warp * 0.6f) * 1.1f, 0f, 1f));

        // Ranges elsewhere: low-frequency noise thresholded into separate massifs.
        float ranges = Noise.Fbm2D(x / 220f, y / 220f, s.Seed + 77);
        float range = Smooth(Math.Clamp((ranges - 0.56f) / 0.2f, 0f, 1f)) * 0.8f;

        float mountain = MathF.Max(central, range);
        float hills = Noise.Fbm2D(x * 0.03f, y * 0.03f, s.Seed);
        float detail = Noise.Fbm2D(x * 0.12f, y * 0.12f, s.Seed + 5);

        float h = s.Height * (0.16f + mountain * 0.62f + hills * 0.08f + detail * 0.02f);
        return Math.Clamp((int)h, 1, s.Height - 2);
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);

    private static bool IsOre(int seed, int x, int y, int z) =>
        Noise.Value3D(x * 0.18f, y * 0.18f, z * 0.18f, seed + 999) > 0.82f;
}
