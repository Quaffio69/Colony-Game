using Colony.Core.World;

namespace Colony.Core.Procedural;

public sealed record WorldGenSettings(int Seed, int Width = 64, int Depth = 64, int Height = 40);

/// <summary>Generates a valley with a central mountain: grass over soil over stone, with ore veins.</summary>
public static class WorldGenerator
{
    public static VoxelWorld Generate(WorldGenSettings s)
    {
        var world = new VoxelWorld(s.Width, s.Depth, s.Height);

        for (int x = 0; x < s.Width; x++)
        for (int y = 0; y < s.Depth; y++)
        {
            int surface = SurfaceHeight(s, x, y);
            int soilDepth = 1 + (int)(Noise.Hash(x, y, 7, s.Seed) * 3); // 1..3 levels of soil

            for (int z = 0; z <= surface; z++)
            {
                TileType type;
                if (z == surface) type = TileType.Grass;
                else if (z > surface - soilDepth) type = TileType.Soil;
                else type = IsOre(s.Seed, x, y, z) ? TileType.Ore : TileType.Stone;

                world.Set(x, y, z, type);
            }
        }

        return world;
    }

    private static int SurfaceHeight(WorldGenSettings s, int x, int y)
    {
        float nx = x / (float)s.Width - 0.5f;
        float ny = y / (float)s.Depth - 0.5f;

        // Mountain mass: strongest at the centre, warped by noise so it isn't a perfect cone.
        float dist = MathF.Sqrt(nx * nx + ny * ny);
        float warp = Noise.Fbm2D(x * 0.05f, y * 0.05f, s.Seed + 31) - 0.5f;
        float mountain = MathF.Max(0f, 1f - (dist + warp * 0.3f) * 2.2f);
        mountain *= mountain * (3 - 2 * mountain); // smoothstep for gentler foothills

        float hills = Noise.Fbm2D(x * 0.08f, y * 0.08f, s.Seed);

        float h = s.Height * (0.2f + mountain * 0.65f + hills * 0.1f);
        return Math.Clamp((int)h, 1, s.Height - 2);
    }

    private static bool IsOre(int seed, int x, int y, int z) =>
        Noise.Value3D(x * 0.18f, y * 0.18f, z * 0.18f, seed + 999) > 0.82f;
}
