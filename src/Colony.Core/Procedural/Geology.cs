using Colony.Core.World;

namespace Colony.Core.Procedural;

/// <summary>The kinds of natural rock, top of the world to bottom.</summary>
public enum Rock : byte
{
    /// <summary>Soft banded sediment near the top of the world.</summary>
    Sandstone,
    /// <summary>Sediment, banded with the sandstone.</summary>
    Limestone,
    /// <summary>The common middle rock (the plain <see cref="TileType.Stone"/> look).</summary>
    Slate,
    /// <summary>Hard deep rock.</summary>
    Granite,
    /// <summary>The hardest rock that can be dug, just over the bedrock.</summary>
    Basalt,
    /// <summary>The floor of the world: cannot be dug.</summary>
    Bedrock,
}

/// <summary>
/// What natural rock is where: layered, warped strata (sandstone and limestone bands at the top, slate, granite, basalt
/// deeper) over an uneven bedrock floor, plus the world's level zero. Not stored per tile: natural rock is
/// <see cref="TileType.Stone"/> in the world and its kind is a function of position (from the seed), so deep worlds stay
/// uniform chunks and cost no memory, and every peer agrees. Layer tops are kept on a coarse grid and interpolated in
/// integers (deterministic, cheap enough for meshing).
/// </summary>
public sealed class Geology
{
    /// <summary>Coarse grid spacing (tiles) of the layer tops.</summary>
    public const int Grid = 8;

    /// <summary>The level shown as 0: the lowest ground on the map. Levels below it read negative ("below zero").</summary>
    public int ZeroLevel { get; }
    /// <summary>The lowest and highest the bedrock floor reaches (it varies by column, within this range).</summary>
    public int BedrockMin { get; }
    public int BedrockMax { get; }

    private readonly int _gw, _gd, _seed;
    // Per coarse node: the top level of each layer (bedrock, basalt, granite, slate) and the tilt of the sediment bands.
    private readonly short[] _bedrock, _basalt, _granite, _slate, _tilt;

    public Geology(int seed, int width, int depth, int height, int zeroLevel)
    {
        _seed = seed;
        ZeroLevel = zeroLevel;
        _gw = width / Grid + 2;
        _gd = depth / Grid + 2;
        int n = _gw * _gd;
        _bedrock = new short[n]; _basalt = new short[n]; _granite = new short[n]; _slate = new short[n]; _tilt = new short[n];
        float h = height;
        bool deep = height >= 96; // small test worlds keep only z = 0 as bedrock
        int bMin = int.MaxValue, bMax = 0;
        for (int gy = 0; gy < _gd; gy++)
        for (int gx = 0; gx < _gw; gx++)
        {
            int i = gy * _gw + gx;
            float x = gx * Grid, y = gy * Grid;
            float n1 = Noise.Fbm2D(x / 90f, y / 90f, seed + 901, 3);
            float n2 = Noise.Fbm2D(x / 260f, y / 260f, seed + 902, 3);
            float n3 = Noise.Fbm2D(x / 340f, y / 340f, seed + 903, 3);
            float n4 = Noise.Fbm2D(x / 300f, y / 300f, seed + 904, 3);
            int bedrock = deep ? 2 + (int)(Smooth(n1) * h * 0.035f) : 0;
            int basalt = Math.Max(bedrock + 2, bedrock + (int)(h * 0.09f + (n2 - 0.5f) * h * 0.10f));
            int granite = Math.Max(basalt + 2, (int)(h * 0.24f + (n3 - 0.5f) * h * 0.12f));
            int slate = Math.Max(granite + 2, (int)(h * 0.37f + (n4 - 0.5f) * h * 0.12f));
            _bedrock[i] = (short)bedrock; _basalt[i] = (short)basalt; _granite[i] = (short)granite; _slate[i] = (short)slate;
            _tilt[i] = (short)((Noise.Fbm2D(x / 140f, y / 140f, seed + 905, 2) - 0.5f) * 40f);
            bMin = Math.Min(bMin, bedrock);
            bMax = Math.Max(bMax, bedrock);
        }
        BedrockMin = bMin;
        BedrockMax = bMax;
        // Cavern layers: bands below level zero (deep worlds only), each well over the bedrock.
        var layers = new List<int>();
        if (deep)
            foreach (float f in new[] { 0.07f, 0.19f, 0.30f })
            {
                // Aligned to a chunk layer: a cavern then fills (makes varied) one layer of chunks, never two or three.
                int band = (zeroLevel - (int)(h * f)) / World.Chunk.Size * World.Chunk.Size;
                if (band > bMax + 4 && band + World.Chunk.Size < zeroLevel) layers.Add(band);
            }
        CavernLayers = layers.ToArray();
    }

    /// <summary>The geology of a generated world: level zero is the lowest ground on the map.</summary>
    public static Geology For(WorldGenSettings s, Terrain t)
    {
        int lowest = int.MaxValue;
        foreach (int z in t.Surface) lowest = Math.Min(lowest, z);
        return new Geology(s.Seed, s.Width, s.Depth, s.Height, lowest == int.MaxValue ? 0 : lowest);
    }

    private int Lerp(short[] a, int x, int y)
    {
        int gx = Math.Clamp(x, 0, (_gw - 2) * Grid) / Grid, gy = Math.Clamp(y, 0, (_gd - 2) * Grid) / Grid;
        int fx = Math.Clamp(x - gx * Grid, 0, Grid), fy = Math.Clamp(y - gy * Grid, 0, Grid);
        int i = gy * _gw + gx;
        int v = a[i] * (Grid - fx) * (Grid - fy) + a[i + 1] * fx * (Grid - fy) + a[i + _gw] * (Grid - fx) * fy + a[i + _gw + 1] * fx * fy;
        return (v + Grid * Grid / 2) / (Grid * Grid);
    }

    /// <summary>The highest level of bedrock in a column (that cell and every one below it can't be dug).</summary>
    public int BedrockTop(int x, int y) => Lerp(_bedrock, x, y);

    /// <summary>The kind of natural rock at a position (whatever the tile there is now).</summary>
    public Rock RockAt(int x, int y, int z)
    {
        if (z <= 0) return Rock.Bedrock;
        if (z <= Lerp(_bedrock, x, y)) return Rock.Bedrock;
        if (z <= Lerp(_basalt, x, y)) return Rock.Basalt;
        if (z <= Lerp(_granite, x, y)) return Rock.Granite;
        if (z <= Lerp(_slate, x, y)) return Rock.Slate;
        // Sediment: bands 4-9 levels thick, tilted across the land, each sandstone, limestone or a slate parting.
        int band = Math.DivRem(z + Lerp(_tilt, x, y) + 400, 6, out _);
        float pick = Noise.Hash(band, 0, 77, _seed + 906);
        return pick < 0.48f ? Rock.Sandstone : pick < 0.88f ? Rock.Limestone : Rock.Slate;
    }

    // ---- Caverns ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The bottom level of each cavern layer's band, top first (none in small worlds). A band is one chunk layer
    /// (<see cref="World.Chunk.Size"/> levels): a cavern's ground, floor and roof all lie in it, so caverns cost one layer
    /// of varied chunks (memory) where they open.
    /// </summary>
    public int[] CavernLayers { get; } = [];

    /// <summary>Share of the map (roughly) a cavern layer opens under.</summary>
    public const float CavernThreshold = 0.61f;

    /// <summary>
    /// The open span of a cavern layer in a column: cells from <paramref name="floor"/> up to floor + height - 1 are open
    /// (the cell under the floor is the cavern's ground). Caverns are big blobs (roughly a sixth of the map in each layer), their floor rolling
    /// a few levels, their roof highest in the middle and closing in at the edges, with rock pillars here and there. False
    /// where the layer is solid.
    /// </summary>
    public bool CavernSpan(int x, int y, int layer, out int floor, out int height)
    {
        floor = height = 0;
        int s = _seed + 910 + layer * 31;
        float m = Noise.Fbm2D(x / 120f, y / 120f, s, 3);
        if (m <= CavernThreshold) return false;
        float edge = Math.Clamp((m - CavernThreshold) / 0.05f, 0f, 1f);
        if (Noise.Value2D(x / 3.3f, y / 3.3f, s + 7) > 0.84f) return false; // a pillar
        // The floor rolls 0-6 levels over the band's bottom (which stays ground); the roof stays inside the band.
        floor = CavernLayers[layer] + 1 + Math.Clamp((int)(Noise.Fbm2D(x / 48f, y / 48f, s + 5, 2) * 13f) - 3, 0, 6);
        height = Math.Min((int)(edge * edge * (3f + 6f * Noise.Fbm2D(x / 26f, y / 26f, s + 3, 2))), CavernLayers[layer] + World.Chunk.Size - floor);
        if (height < 2) return false; // too low to stand in: leave rock
        return true;
    }

    private static float Smooth(float t)
    {
        t = Math.Clamp((t - 0.25f) * 2f, 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Display name of a rock.</summary>
    public static string Name(Rock r) => r.ToString();

    /// <summary>The tile look (palette entry) a rock is drawn with.</summary>
    public static TileType Look(Rock r) => r switch
    {
        Rock.Sandstone => TileType.Sandstone,
        Rock.Limestone => TileType.Limestone,
        Rock.Granite => TileType.Granite,
        Rock.Basalt => TileType.Basalt,
        Rock.Bedrock => TileType.Bedrock,
        _ => TileType.Stone,
    };

    /// <summary>Work ticks to dig a rock out (before the depth factor).</summary>
    public static int DigTicks(Rock r) => r switch
    {
        Rock.Sandstone => Balance.DigSandstone,
        Rock.Limestone => Balance.DigLimestone,
        Rock.Granite => Balance.DigGranite,
        Rock.Basalt => Balance.DigBasalt,
        _ => Balance.DigStone,
    };
}
