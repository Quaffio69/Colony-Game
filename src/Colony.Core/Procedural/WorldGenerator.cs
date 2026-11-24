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
        var world = new VoxelWorld(s.Width, s.Depth, s.Height) { Geology = Geology.For(s, terrain) };
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
        Span<byte> top = stackalloc byte[n], sub = stackalloc byte[n];
        int minStone = int.MaxValue, maxSurface = -1;

        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int i = (y - y0) * Chunk.Size + (x - x0);
            int sz = t.SurfaceAt(x, y);
            var (topType, soil, subType) = SurfaceMaterial(s, t, x, y, sz);
            sub[i] = (byte)subType;
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
                    var type = z == surface[i] ? (TileType)top[i] : z > stoneTop[i] ? (TileType)sub[i] : TileType.Stone;
                    tiles[Chunk.Index(x - x0, y - y0, z - zLo)] = type;
                }
            }
            chunk.InitTiles(tiles);
        }
    }

    /// <summary>
    /// Top tile, how many levels of loose ground (top tile included) sit on the stone, and what fills those
    /// levels under the top. River channels are sand over clay, lake basins clay; flat ground right by the
    /// water is beach sand; elsewhere soil, with clay beds in patches of it.
    /// </summary>
    private static (TileType Top, int Soil, TileType Sub) SurfaceMaterial(WorldGenSettings s, Terrain t, int x, int y, int surface)
    {
        int i = y * s.Width + x;
        var kind = t.Kind[i];
        if (kind == SurfaceKind.RiverBed) return (TileType.Sand, 3, TileType.Clay);
        if (kind == SurfaceKind.LakeBed) return (TileType.Clay, 3, TileType.Clay);

        int slope = 0;
        // A river or lake bed beside the land is its bank, not a cliff: only land neighbours count.
        int Drop(int nx, int ny) => t.Kind[ny * s.Width + nx] == SurfaceKind.Land ? Math.Abs(t.SurfaceAt(nx, ny) - surface) : 0;
        if (x > 0) slope = Math.Max(slope, Drop(x - 1, y));
        if (x < s.Width - 1) slope = Math.Max(slope, Drop(x + 1, y));
        if (y > 0) slope = Math.Max(slope, Drop(x, y - 1));
        if (y < s.Depth - 1) slope = Math.Max(slope, Drop(x, y + 1));

        if (slope >= 3 || surface >= RockLine(s)) return (TileType.Stone, 0, TileType.Stone); // cliffs and peaks: bare rock
        int soil = 1 + (slope <= 1 ? 2 : 0) + (int)(Noise.Hash(x, y, 7, s.Seed) * 2); // 1..4
        if (t.WaterDistance[i] <= 1 && slope <= 1) return (TileType.Sand, soil, TileType.Sand); // beach
        // Clay beds: patches of the soil layer, commoner in damp ground near water.
        float damp = 1f - Math.Min(1f, t.WaterDistance[i] / 40f);
        bool clay = Noise.Value2D(x / 23f, y / 23f, s.Seed + 811) > 0.74f - 0.14f * damp;
        return (TileType.Grass, Math.Max(soil, clay ? 3 : 1), clay ? TileType.Clay : TileType.Soil);
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
            if (Noise.Hash(gx, gy, gz, s.Seed + 501) > 0.07f + 0.16f * depth01) continue;
            var mineral = DepositMineral(1f - depth01, Noise.Hash(gx, gy, gz, s.Seed + 509));

            float px = (gx + Noise.Hash(gx, gy, gz, s.Seed + 502)) * DepositCell;
            float py = (gy + Noise.Hash(gx, gy, gz, s.Seed + 503)) * DepositCell;
            float pz = (gz + Noise.Hash(gx, gy, gz, s.Seed + 504)) * DepositCell;
            // Deeper deposits are bigger (the reward for digging down); gems are small pockets.
            float scale = (0.8f + 0.5f * depth01) * (TileInfo.IsGem(mineral) ? 0.45f : 1f);
            float rx = (1.5f + Noise.Hash(gx, gy, gz, s.Seed + 505) * 3f) * scale;
            float ry = (1.5f + Noise.Hash(gx, gy, gz, s.Seed + 506) * 3f) * scale;
            float rz = (1.2f + Noise.Hash(gx, gy, gz, s.Seed + 507) * 1.8f) * scale; // flattish, like seams

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
                if (world.Get(x, y, z) == TileType.Stone && z > world.Geology!.BedrockTop(x, y)) world.SetForGeneration(x, y, z, mineral);
            }
        }
    }

    /// <summary>
    /// Which mineral a deposit is, by height in the world (0 = bottom, 1 = top) and a random pick weighted by
    /// stratum: copper and tin near the surface, iron in the middle, silver, gold and sulfur deep, aetherite
    /// only at the very bottom; quartz geodes anywhere; gems from the middle down, the finest (diamond) only near the bottom.
    /// </summary>
    public static TileType DepositMineral(float height01, float pick)
    {
        Span<(TileType Type, float Weight)> table =
        [
            (TileType.Quartz, 1f),
            (TileType.CopperOre, height01 > 0.45f ? 3f : 0.3f),
            (TileType.TinOre, height01 > 0.45f ? 2f : 0.2f),
            (TileType.IronOre, height01 is > 0.25f and < 0.8f ? 3f : 0.4f),
            (TileType.SilverOre, height01 < 0.5f ? 1.5f : 0f),
            (TileType.GoldOre, height01 < 0.4f ? 1f : 0f),
            (TileType.Sulfur, height01 < 0.35f ? 1.5f : 0f),
            (TileType.Aetherite, height01 < 0.18f ? 2f : 0f),
            // Gems: the deeper, the rarer and finer.
            (TileType.Garnet, height01 < 0.45f ? 1.2f : 0f),
            (TileType.Sapphire, height01 < 0.3f ? 0.8f : 0f),
            (TileType.Ruby, height01 < 0.3f ? 0.8f : 0f),
            (TileType.Emerald, height01 < 0.25f ? 0.7f : 0f),
            (TileType.Diamond, height01 < 0.12f ? 0.9f : 0f),
        ];
        float total = 0;
        foreach (var (_, w) in table) total += w;
        float at = pick * total;
        foreach (var (type, w) in table)
        {
            if (at < w) return type;
            at -= w;
        }
        return TileType.Quartz;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
