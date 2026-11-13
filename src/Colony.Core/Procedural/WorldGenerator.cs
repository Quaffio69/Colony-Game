using Colony.Core.World;

namespace Colony.Core.Procedural;

public sealed record WorldGenSettings(int Seed, int Width = 64, int Depth = 64, int Height = 40);

/// <summary>A generated world plus the surface/hydrology data it was built from.</summary>
public sealed record GeneratedWorld(VoxelWorld World, Terrain Terrain);

/// <summary>
/// Turns a <see cref="Terrain"/> heightmap into voxels: grass (or bare rock on steep or high ground, mud in
/// river and lake beds) over a few levels of soil over deep stone, with clustered ore deposits that get richer
/// with depth. Chunks wholly below the soil are written as uniform stone without per-tile storage, so world
/// depth costs almost no memory. Chunk columns are filled in parallel; output depends only on the seed.
/// </summary>
public static class WorldGenerator
{
    /// <summary>Edge length of the grid cells ore deposits are scattered over (at most one deposit per cell).</summary>
    public const int DepositCell = 20;
    private const float MaxDepositRadius = 4.5f;

    public static VoxelWorld Generate(WorldGenSettings s) => GenerateWithTerrain(s).World;

    public static GeneratedWorld GenerateWithTerrain(WorldGenSettings s)
    {
        var terrain = Terrain.Generate(s);
        var world = new VoxelWorld(s.Width, s.Depth, s.Height);
        Parallel.For(0, world.ChunksX * world.ChunksY, column =>
        {
            int cx = column % world.ChunksX, cy = column / world.ChunksX;
            FillColumn(s, terrain, world, cx, cy);
            PlaceOre(s, world, cx, cy);
        });
        return new GeneratedWorld(world, terrain);
    }

    /// <summary>Levels above which mountain tops are bare rock.</summary>
    public static int RockLine(WorldGenSettings s) => (int)(s.Height * 0.78f);

    private static void FillColumn(WorldGenSettings s, Terrain t, VoxelWorld world, int cx, int cy)
    {
        int x0 = cx * Chunk.Size, y0 = cy * Chunk.Size;
        int x1 = Math.Min(x0 + Chunk.Size, s.Width), y1 = Math.Min(y0 + Chunk.Size, s.Depth);
        int n = Chunk.Size * Chunk.Size;
        Span<int> surface = stackalloc int[n], stoneTop = stackalloc int[n];
        Span<byte> top = stackalloc byte[n];
        int minStone = int.MaxValue, maxSurface = -1;

        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int i = (y - y0) * Chunk.Size + (x - x0);
            int sz = t.SurfaceAt(x, y);
            var (topType, soil) = SurfaceMaterial(s, t, x, y, sz);
            surface[i] = sz;
            top[i] = (byte)topType;
            stoneTop[i] = sz - soil;
            minStone = Math.Min(minStone, stoneTop[i]); // every level <= stoneTop is stone
            maxSurface = Math.Max(maxSurface, sz);
        }

        for (int cz = 0; cz < world.ChunksZ; cz++)
        {
            var chunk = world.GetChunk(cx, cy, cz)!;
            int zLo = cz * Chunk.Size, zHi = zLo + Chunk.Size - 1;
            if (zHi <= minStone)
            {
                chunk.InitUniform(TileType.Stone);
                continue;
            }
            if (zLo > maxSurface) continue; // sky: uniform air already

            var tiles = new TileType[Chunk.Volume];
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y - y0) * Chunk.Size + (x - x0);
                int zEnd = Math.Min(zHi, surface[i]);
                for (int z = zLo; z <= zEnd; z++)
                {
                    var type = z == surface[i] ? (TileType)top[i] : z > stoneTop[i] ? TileType.Soil : TileType.Stone;
                    tiles[Chunk.Index(x - x0, y - y0, z - zLo)] = type;
                }
            }
            chunk.InitTiles(tiles);
        }
    }

    /// <summary>Top tile and how many levels of soil (top tile included) sit on the stone.</summary>
    private static (TileType Top, int Soil) SurfaceMaterial(WorldGenSettings s, Terrain t, int x, int y, int surface)
    {
        var kind = t.Kind[y * s.Width + x];
        if (kind != SurfaceKind.Land) return (TileType.Soil, 2); // mud under (future) water

        int slope = 0;
        if (x > 0) slope = Math.Max(slope, Math.Abs(t.SurfaceAt(x - 1, y) - surface));
        if (x < s.Width - 1) slope = Math.Max(slope, Math.Abs(t.SurfaceAt(x + 1, y) - surface));
        if (y > 0) slope = Math.Max(slope, Math.Abs(t.SurfaceAt(x, y - 1) - surface));
        if (y < s.Depth - 1) slope = Math.Max(slope, Math.Abs(t.SurfaceAt(x, y + 1) - surface));

        if (slope >= 3 || surface >= RockLine(s)) return (TileType.Stone, 0); // cliffs and peaks: bare rock
        int soil = 1 + (slope <= 1 ? 2 : 0) + (int)(Noise.Hash(x, y, 7, s.Seed) * 2); // 1..4
        return (TileType.Grass, soil);
    }

    /// <summary>
    /// Ore deposits: lumpy ellipsoids, at most one per <see cref="DepositCell"/>³ cell, more likely deeper down.
    /// Each chunk column writes only its own tiles, enumerating every deposit that could overlap it, so
    /// columns run in parallel and the result does not depend on scheduling.
    /// </summary>
    private static void PlaceOre(WorldGenSettings s, VoxelWorld world, int cx, int cy)
    {
        int x0 = cx * Chunk.Size, y0 = cy * Chunk.Size;
        int x1 = Math.Min(x0 + Chunk.Size, s.Width) - 1, y1 = Math.Min(y0 + Chunk.Size, s.Depth) - 1;
        int r = (int)MathF.Ceiling(MaxDepositRadius);
        int gx0 = FloorDiv(x0 - r, DepositCell), gx1 = FloorDiv(x1 + r, DepositCell);
        int gy0 = FloorDiv(y0 - r, DepositCell), gy1 = FloorDiv(y1 + r, DepositCell);
        int gz1 = s.Height / DepositCell;

        for (int gz = 0; gz <= gz1; gz++)
        for (int gy = gy0; gy <= gy1; gy++)
        for (int gx = gx0; gx <= gx1; gx++)
        {
            float depth01 = 1f - Math.Clamp((gz + 0.5f) * DepositCell / s.Height, 0f, 1f);
            if (Noise.Hash(gx, gy, gz, s.Seed + 501) > 0.02f + 0.12f * depth01) continue;

            float px = (gx + Noise.Hash(gx, gy, gz, s.Seed + 502)) * DepositCell;
            float py = (gy + Noise.Hash(gx, gy, gz, s.Seed + 503)) * DepositCell;
            float pz = (gz + Noise.Hash(gx, gy, gz, s.Seed + 504)) * DepositCell;
            float rx = 1.5f + Noise.Hash(gx, gy, gz, s.Seed + 505) * 3f;
            float ry = 1.5f + Noise.Hash(gx, gy, gz, s.Seed + 506) * 3f;
            float rz = 1.2f + Noise.Hash(gx, gy, gz, s.Seed + 507) * 1.8f; // flattish, like seams

            int bx0 = Math.Max(x0, (int)MathF.Floor(px - rx)), bx1 = Math.Min(x1, (int)MathF.Ceiling(px + rx));
            int by0 = Math.Max(y0, (int)MathF.Floor(py - ry)), by1 = Math.Min(y1, (int)MathF.Ceiling(py + ry));
            int bz0 = Math.Max(1, (int)MathF.Floor(pz - rz)), bz1 = Math.Min(s.Height - 1, (int)MathF.Ceiling(pz + rz));
            for (int z = bz0; z <= bz1; z++)
            for (int y = by0; y <= by1; y++)
            for (int x = bx0; x <= bx1; x++)
            {
                float dx = (x + 0.5f - px) / rx, dy = (y + 0.5f - py) / ry, dz = (z + 0.5f - pz) / rz;
                float lumpy = 0.75f + 0.5f * Noise.Hash(x, y, z, s.Seed + 508);
                if (dx * dx + dy * dy + dz * dz > lumpy) continue;
                if (world.Get(x, y, z) == TileType.Stone) world.SetForGeneration(x, y, z, TileType.Ore);
            }
        }
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
