namespace Colony.Core.Procedural;

/// <summary>What a column's surface is, for picking surface materials (and, later, seeding water).</summary>
public enum SurfaceKind : byte { Land, RiverBed, LakeBed }

/// <summary>
/// The surface heightmap plus its hydrology. Built in three steps:
/// <list type="number">
/// <item>Shape: broad uplands and lowlands, ridged mountain ranges in some regions, gentle hills, all
/// domain-warped so nothing looks grid-aligned. Horizontal scales are in absolute tiles; vertical ones are
/// fractions of the world height.</item>
/// <item>Drainage, on a coarse grid (<see cref="Coarse"/> tiles per cell): a priority flood fills every
/// basin so water can always reach the map edge, which gives each cell a downstream receiver; flow
/// accumulation down that tree finds the rivers. Filled basins deeper than a tile become lakes.</item>
/// <item>Carving, at full resolution: river channels along the drainage tree, and valleys whose floors fall
/// gently towards the river; big rivers flatten wide floodplains, small streams only notch the land.</item>
/// </list>
/// River channels and lake basins are carved dry; <see cref="Water"/> records how deep the water should stand
/// in each column, for the water simulation to fill in later. Fully deterministic from the seed.
/// </summary>
public sealed class Terrain
{
    /// <summary>Tiles per drainage cell along each axis.</summary>
    public const int Coarse = 4;
    /// <summary>Coarse cells of catchment before a stream counts as a river.</summary>
    public const float RiverThreshold = 400f;
    /// <summary>Basins shallower than this (in levels) stay dry land: ponds, not lakes.</summary>
    public const float LakeMinDepth = 3f;
    /// <summary>How deep (as a fraction of world height) rivers may cut through a basin's rim.</summary>
    public const float BreachDepth = 0.12f;
    /// <summary>Share of each basin's depth its outlet is cut by (the rest stays a lake).</summary>
    public const float BreachFraction = 0.85f;

    public int Width { get; }
    public int Depth { get; }
    public int Height { get; }

    /// <summary>Top solid level per column, indexed y * Width + x.</summary>
    public int[] Surface { get; }
    /// <summary>Intended water depth above the surface per column (0 = dry land). Not simulated yet.</summary>
    public byte[] Water { get; }
    public SurfaceKind[] Kind { get; }
    /// <summary>Tiles to the nearest river or lake column, capped at 255 (moisture for vegetation, spawn choice).</summary>
    public byte[] WaterDistance { get; }

    public int SpawnX { get; private set; }
    public int SpawnY { get; private set; }
    public int RiverCells { get; private set; }
    public int LakeCells { get; private set; }
    /// <summary>
    /// Where rivers gain water (fine columns, approximately on their channels) and how much, in units of the flow
    /// that makes a river (<see cref="RiverThreshold"/>): a head gains at least 1, cells downstream what the land
    /// beside them drains into them. Springs go here.
    /// </summary>
    public List<(int X, int Y, float Share)> RiverSources { get; } = new();
    /// <summary>Downstream neighbour of each river tile (0..3: +x, +y, -x, -y) or <see cref="NoFlow"/>.</summary>
    public byte[] FlowDirection { get; }

    private Terrain(int w, int d, int h)
    {
        Width = w;
        Depth = d;
        Height = h;
        Surface = new int[w * d];
        Water = new byte[w * d];
        Kind = new SurfaceKind[w * d];
        WaterDistance = new byte[w * d];
        FlowDirection = new byte[w * d];
    }

    public int SurfaceAt(int x, int y) => Surface[y * Width + x];

    /// <summary>The world's seed (biome noise reads it).</summary>
    public int Seed { get; private set; }

    /// <summary>The plates, climate and erosion the surface came from (generator 2; null for the first generator).</summary>
    public Landform? Landform { get; private set; }
    /// <summary>The sea's surface level (its top water level), or 0 without a sea (first generator, or no oceans).</summary>
    public int SeaLevel { get; private set; }
    /// <summary>Where the fine hydrology spent its time (ms), for the bench.</summary>
    public string Timings { get; private set; } = "";

    /// <summary>River threshold this world used (<see cref="RiverThreshold"/> for the first generator).</summary>
    public float RiverAt { get; private set; } = RiverThreshold;

    /// <summary>True where column i is sea (lake water at sea level, joined to the open ocean).</summary>
    public bool IsSea(int i) => SeaLevel > 0 && Kind[i] == SurfaceKind.LakeBed && Surface[i] + Water[i] == SeaLevel;

    public static Terrain Generate(WorldGenSettings s, WorldGenProgress? progress = null)
    {
        var t = new Terrain(s.Width, s.Depth, s.Height);
        t.Seed = s.Seed;
        int w = s.Width, d = s.Depth;

        // 1. Shape: the first generator's noise, or the tectonic landform (plates, climate, erosion) with fine detail.
        var lf = s.Shape.IsLegacy ? null : Landform.Build(s, progress);
        t.Landform = lf;
        t.SeaLevel = lf?.SeaLevel ?? 0;
        if (lf != null)
        {
            t.RiverAt = 300f * s.Shape.Rivers switch { Rivers.Few => 1.9f, Rivers.Many => 0.5f, _ => 1f };
            progress?.Step("Shaping the land", "Hills, ridges, dunes and mesas, a tile at a time", 0.30f, 0.38f);
        }
        float riverAt = t.RiverAt;
        var h = new float[w * d];
        int rowsDone = 0;
        Parallel.For(0, d, y =>
        {
            if (lf != null) for (int x = 0; x < w; x++) h[y * w + x] = lf.FineHeight(x, y);
            else for (int x = 0; x < w; x++) h[y * w + x] = BaseHeight(s, x, y);
            int done = Interlocked.Increment(ref rowsDone);
            if ((done & 63) == 0) progress?.Within((float)done / d);
        });

        // 2. Drainage on the coarse grid.
        if (lf != null) progress?.Step("Carving rivers", "Streams gather where the rain runs off", 0.38f, 0.46f);
        int cw = (w + Coarse - 1) / Coarse, cd = (d + Coarse - 1) / Coarse;
        var ch = new float[cw * cd];
        for (int cy = 0; cy < cd; cy++)
        for (int cx = 0; cx < cw; cx++)
            ch[cy * cw + cx] = h[CoarseCentreY(cy, d) * w + CoarseCentreX(cx, w)];
        // The sea (open water below sea level joined to the edge) is an outlet like the map edge: rivers end in it.
        var ocean = t.SeaLevel > 0 ? CoarseSea(ch, cw, cd, t.SeaLevel) : null;
        float seaLevel = t.SeaLevel;
        var (filled, _, _) = PriorityFlood(ch, cw, cd, ocean, seaLevel);
        // Rivers cut through the rims of most basins: breach each basin's outlet (up to a limit), then
        // re-flood. Shallow basins drain into river valleys; only deep ones remain, as smaller lakes.
        // Grouped basins hide smaller dips inside them, so repeat: the first round cuts every basin's outlet by
        // most of its depth (deep basins keep a smaller lake); later rounds fully drain the leftover small dips.
        for (int round = 0; round < 4; round++)
        {
            if (!Breach(ch, filled, cw, cd, maxDepth: s.Height * BreachDepth, firstRound: round == 0, ocean)) break;
            (filled, _, _) = PriorityFlood(ch, cw, cd, ocean, seaLevel);
        }
        (filled, var receiver, var order) = PriorityFlood(ch, cw, cd, ocean, seaLevel);
        progress?.Within(0.4f);

        var acc = new float[cw * cd];
        if (lf == null) Array.Fill(acc, 1f);
        else // rain-weighted: wet catchments make rivers sooner, deserts hardly any
            for (int c = 0; c < acc.Length; c++)
                acc[c] = ocean != null && ocean[c] ? 0f : MathF.Max(0.05f, lf.RainAt(CoarseCentreX(c % cw, w), CoarseCentreY(c / cw, d)));
        for (int i = order.Length - 1; i >= 0; i--)
        {
            int c = order[i];
            if (receiver[c] >= 0) acc[receiver[c]] += acc[c];
        }
        var lake = new bool[cw * cd];
        foreach (var basin in Basins(ch, filled, cw, cd, ocean))
        {
            if (basin.MaxDepth <= LakeMinDepth) continue;
            foreach (int c in basin.Cells) lake[c] = true;
            t.LakeCells += basin.Cells.Count;
        }

        // 3. Carving. Seeds: river channel tiles carry an influence (how strongly they shape the land around
        // them) and a water level; both spread outwards, the influence fading with distance.
        var influence = new float[w * d];
        var level = new float[w * d];
        var dist = new float[w * d];
        var channelDepth = new byte[w * d];
        var channelLevel = new float[w * d];
        Array.Fill(channelLevel, float.MaxValue);

        // Where rivers get their water (springs go here): each river cell gains what drains into it from the land
        // around it, i.e. its flow minus what arrives from rivers (and lakes) upstream. Heads gain their whole flow.
        var arriving = new float[acc.Length];
        for (int c = 0; c < acc.Length; c++)
            if (acc[c] >= riverAt && receiver[c] >= 0) arriving[receiver[c]] += acc[c];
        for (int c = 0; c < acc.Length; c++)
        {
            if (acc[c] < riverAt || lake[c] || ocean != null && ocean[c]) continue;
            float gain = acc[c] - arriving[c];
            if (gain > 0) t.RiverSources.Add(((int)CoarseCentreX(c % cw, w), (int)CoarseCentreY(c / cw, d), gain / riverAt));
        }

        for (int c = 0; c < acc.Length; c++)
        {
            if (acc[c] < riverAt || ocean != null && ocean[c]) continue;
            // Lakes aren't carved, except at their spill point: the outflow channel starts inside the lake, or a strip
            // of uncut land between lake and river would dam it (the lake rises and floods its shores).
            // (Cells of one basin share its filled level; a lower receiving lake cell is the next lake down a chain.)
            if (lake[c] && (receiver[c] < 0 || lake[receiver[c]] && filled[receiver[c]] >= filled[c] - 0.01f)) continue;
            if (!lake[c]) t.RiverCells++;
            float strength = Math.Clamp(MathF.Log2(acc[c] / riverAt) / 6f, 0f, 1f);
            float radius = 0.7f + 3.3f * strength;
            byte depth = (byte)(strength >= 0.5f ? 2 : 1); // shallow: water up to the bank, bigger rivers a level deeper
            float seed = 0.3f + 0.7f * strength;

            int cx = c % cw, cy = c / cw;
            float ax = CoarseCentreX(cx, w) + 0.5f, ay = CoarseCentreY(cy, d) + 0.5f;
            float bx, by, levelB;
            if (receiver[c] >= 0)
            {
                int r = receiver[c];
                bx = CoarseCentreX(r % cw, w) + 0.5f;
                by = CoarseCentreY(r / cw, d) + 0.5f;
                levelB = filled[r];
            }
            else
            {
                // Map edge: run the channel off the nearest edge.
                (bx, by) = NearestEdge(ax, ay, w, d);
                levelB = filled[c];
            }

            float len = MathF.Max(MathF.Abs(bx - ax), MathF.Abs(by - ay));
            int steps = Math.Max(1, (int)(len * 2));
            for (int i = 0; i <= steps; i++)
            {
                float f = i / (float)steps;
                float px = ax + (bx - ax) * f, py = ay + (by - ay) * f;
                float lv = filled[c] + (levelB - filled[c]) * f;
                StampDisc(t, px, py, radius, depth, lv, seed, influence, level, channelDepth, channelLevel);
            }
        }

        Propagate(influence, level, dist, w, d, falloff: 1f / 150f);

        // Final surface: pull the land towards the nearby river level (only ever lowering it). River tiles keep the
        // valley floor for now; SetWaterLevels decides each one's water level and cuts its bed below it.
        var riverDepth = new byte[w * d];
        Parallel.For(0, d, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float ground = h[i];
                if (influence[i] > 0 && lf == null) // tectonic worlds: erosion made the valleys (this pull creased the land where two rivers' reaches met)
                {
                    float k = Smooth(Math.Clamp(influence[i], 0f, 1f)) * 0.92f;
                    float target = level[i] + 0.04f * dist[i]; // floodplains rise gently away from the river
                    if (target < ground) ground += (target - ground) * k;
                }

                if (channelDepth[i] > 0)
                {
                    ground = MathF.Min(channelLevel[i], ground);
                    t.Kind[i] = SurfaceKind.RiverBed;
                    riverDepth[i] = channelDepth[i];
                }
                int surface = Math.Clamp((int)MathF.Floor(ground), 1, s.Height - 2);

                int cell = Math.Min(y / Coarse, cd - 1) * cw + Math.Min(x / Coarse, cw - 1);
                if (lake[cell] && t.Kind[i] == SurfaceKind.Land)
                {
                    int lakeTop = (int)MathF.Floor(filled[cell]);
                    if (lakeTop > surface)
                    {
                        t.Kind[i] = SurfaceKind.LakeBed;
                        t.Water[i] = (byte)Math.Min(255, lakeTop - surface);
                    }
                }
                t.Surface[i] = surface;
            }
        });

        if (t.SeaLevel > 0)
        {
            progress?.Step("Filling oceans", "The sea comes in over everything below its level", 0.46f, 0.50f);
            FillSea(t, s.Height);
        }
        if (lf != null) progress?.Step("Filling lakes", "Basins brim, rivers find their level and their way to the sea", 0.50f, 0.56f);

        // Fine-grid hydrology at whole levels, so the generated water is already at rest: see SetWaterLevels.
        var lap = System.Diagnostics.Stopwatch.StartNew();
        var times = new System.Text.StringBuilder();
        void Lap(string what) { times.Append($"{what} {lap.ElapsedMilliseconds}, "); lap.Restart(); }
        ExtendLakesToTheirShores(t);
        JoinCornerContacts(t, riverDepth);
        Lap("shores");
        ConnectWaterToEdges(t, riverDepth, receiver, cw, cd);
        Lap("outlets");
        SetWaterLevels(t, riverDepth);
        Lap("levels");
        MarkSpringPools(t);
        BuildLakeSills(t);
        ExtendLakesToTheirShores(t); // rivers turned into backwater may have land below their level beside them
        Lap("pools, sills, shores");
        // Banks: the first generator graded 5 tiles, which left a wall where a river sat deep below its valley; now the
        // land rises a level a tile, then two, as far as it needs to (valley sides, no walls: see GradeValleySides).
        ShapeBanks(t, lf == null ? BankReach : -1);
        ComputeWaterDistance(t);
        Lap("banks");
        t.Timings = times.ToString();
        (t.SpawnX, t.SpawnY) = lf == null ? ChooseSpawn(t) : ChooseHome(t);
        return t;
    }

    /// <summary>Coarse cells below sea level joined to the map edge (4-way): the open sea.</summary>
    private static bool[] CoarseSea(float[] ch, int cw, int cd, int sea)
    {
        var ocean = new bool[cw * cd];
        var queue = new Queue<int>();
        for (int c = 0; c < ocean.Length; c++)
        {
            int x = c % cw, y = c / cw;
            if ((x == 0 || y == 0 || x == cw - 1 || y == cd - 1) && ch[c] < sea) { ocean[c] = true; queue.Enqueue(c); }
        }
        while (queue.TryDequeue(out int c))
        {
            int x = c % cw, y = c / cw;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if ((uint)nx >= (uint)cw || (uint)ny >= (uint)cd) continue;
                int m = ny * cw + nx;
                if (ocean[m] || ch[m] >= sea) continue;
                ocean[m] = true;
                queue.Enqueue(m);
            }
        }
        return ocean;
    }

    /// <summary>Most levels of water over the sea floor (a shelf: deeper ground is raised to it).</summary>
    public static int SeaDepth(int height) => Math.Max(2, (int)(height * 0.085f));

    /// <summary>
    /// The sea: every column whose ground is below sea level and joined to the map edge through such columns (4-way)
    /// becomes sea water (lake water at <see cref="SeaLevel"/>), whatever was there (rivers end in it); the floor is
    /// kept within <see cref="SeaDepth"/> of the surface.
    /// </summary>
    private static void FillSea(Terrain t, int height)
    {
        int w = t.Width, d = t.Depth, sea = t.SeaLevel, floor = sea - SeaDepth(height);
        var seen = new bool[w * d];
        var queue = new Queue<int>();
        for (int i = 0; i < w * d; i++)
        {
            int x = i % w, y = i / w;
            if ((x == 0 || y == 0 || x == w - 1 || y == d - 1) && t.Surface[i] < sea) { seen[i] = true; queue.Enqueue(i); }
        }
        while (queue.TryDequeue(out int c))
        {
            t.Kind[c] = SurfaceKind.LakeBed;
            t.Surface[c] = Math.Max(Math.Max(1, floor), t.Surface[c]);
            t.Water[c] = (byte)(sea - t.Surface[c]);
            int x = c % w, y = c / w;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)d) continue;
                int m = ny * w + nx;
                if (seen[m] || t.Surface[m] >= sea) continue;
                seen[m] = true;
                queue.Enqueue(m);
            }
        }
    }

    // ---- Shape -------------------------------------------------------------------------------------------

    /// <summary>Raw surface height (in levels, fractional) before any river carving.</summary>
    public static float BaseHeight(WorldGenSettings s, float x, float y)
    {
        float hgt = s.Height;
        // Domain warp: bends every feature below into natural, non-grid-aligned shapes.
        float wx = x + (Noise.Fbm2D(x / 520f, y / 520f, s.Seed + 11, 3) - 0.5f) * 260f;
        float wy = y + (Noise.Fbm2D(x / 520f, y / 520f, s.Seed + 12, 3) - 0.5f) * 260f;

        float continent = Noise.Fbm2D(wx / 1700f, wy / 1700f, s.Seed + 13, 3);  // uplands vs lowlands
        float ranges = Smoothstep(0.45f, 0.62f, Noise.Fbm2D(wx / 1000f, wy / 1000f, s.Seed + 14, 3));
        float ridges = Ridged(wx / 320f, wy / 320f, s.Seed + 15, 5);            // sharp crests, broad flanks
        float hills = Noise.Fbm2D(wx / 170f, wy / 170f, s.Seed + 16, 4);
        float detail = Noise.Fbm2D(x / 30f, y / 30f, s.Seed + 17, 2) - 0.5f;

        return hgt * (0.46f
                      + (continent - 0.5f) * 0.22f
                      + (hills - 0.5f) * 0.20f
                      + ranges * (0.06f + ridges * 0.40f))
               + detail * 3f;
    }

    /// <summary>Ridged multifractal noise in about [0, 1]: crests where the base noise crosses its midpoint.</summary>
    internal static float Ridged(float x, float y, int seed, int octaves)
    {
        float sum = 0, amp = 1, norm = 0, freq = 1, weight = 1;
        for (int i = 0; i < octaves; i++)
        {
            float n = 1f - MathF.Abs(Noise.Value2D(x * freq, y * freq, seed + i * 131) * 2f - 1f);
            n *= n * weight;
            weight = Math.Clamp(n * 1.5f, 0f, 1f); // detail concentrates on the ridges
            sum += n * amp;
            norm += amp;
            amp *= 0.5f;
            freq *= 2.03f;
        }
        return sum / norm;
    }

    private static float Smoothstep(float a, float b, float v) => Smooth(Math.Clamp((v - a) / (b - a), 0f, 1f));
    private static float Smooth(float t) => t * t * (3 - 2 * t);

    // ---- Drainage ----------------------------------------------------------------------------------------

    private static int CoarseCentreX(int cx, int w) => Math.Min(cx * Coarse + Coarse / 2, w - 1);
    private static int CoarseCentreY(int cy, int d) => Math.Min(cy * Coarse + Coarse / 2, d - 1);

    /// <summary>
    /// Priority-flood depression filling (Barnes et al.): floods inwards from the map edge in height order.
    /// Each cell's receiver is the cell that flooded it, so following receivers always reaches the edge, and
    /// the processing order lists every receiver before its donors.
    /// </summary>
    private static (float[] Filled, int[] Receiver, int[] Order) PriorityFlood(float[] h, int cw, int cd, bool[]? outlet = null, float outletLevel = 0)
    {
        const float epsilon = 1e-3f; // keeps filled basins draining towards their outlet
        int n = cw * cd;
        var filled = new float[n];
        var receiver = new int[n];
        var order = new int[n];
        var done = new bool[n];
        var open = new PriorityQueue<int, (float, int)>(); // ties broken by cell index: deterministic
        int count = 0;

        for (int c = 0; c < n; c++)
        {
            int x = c % cw, y = c / cw;
            bool sea = outlet != null && outlet[c];
            if (!sea && x != 0 && y != 0 && x != cw - 1 && y != cd - 1) continue;
            filled[c] = sea ? MathF.Max(h[c], outletLevel) : h[c];
            receiver[c] = -1;
            done[c] = true;
            open.Enqueue(c, (filled[c], c));
        }

        ReadOnlySpan<(int, int)> dirs = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];
        while (open.TryDequeue(out int c, out _))
        {
            order[count++] = c;
            int x = c % cw, y = c / cw;
            foreach (var (dx, dy) in dirs)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx >= (uint)cw || (uint)ny >= (uint)cd) continue;
                int m = ny * cw + nx;
                if (done[m]) continue;
                done[m] = true;
                filled[m] = MathF.Max(h[m], filled[c] + epsilon);
                receiver[m] = c;
                open.Enqueue(m, (filled[m], m));
            }
        }
        return (filled, receiver, order);
    }

    private sealed record Basin(List<int> Cells, int Lowest, float MaxDepth);

    /// <summary>Connected (8-way) groups of cells the flood raised, i.e. would-be lakes.</summary>
    private static List<Basin> Basins(float[] h, float[] filled, int cw, int cd, bool[]? sea = null)
    {
        const float wet = 0.25f;
        var basins = new List<Basin>();
        var seen = new bool[h.Length];
        var stack = new Stack<int>();
        if (sea != null) Array.Copy(sea, seen, sea.Length); // the sea is no basin
        for (int c = 0; c < h.Length; c++)
        {
            if (seen[c] || filled[c] - h[c] <= wet) continue;
            var cells = new List<int>();
            int lowest = c;
            seen[c] = true;
            stack.Push(c);
            while (stack.TryPop(out int p))
            {
                cells.Add(p);
                if (h[p] < h[lowest]) lowest = p;
                int x = p % cw, y = p / cw;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)cw || (uint)ny >= (uint)cd) continue;
                    int q = ny * cw + nx;
                    if (seen[q] || filled[q] - h[q] <= wet) continue;
                    seen[q] = true;
                    stack.Push(q);
                }
            }
            basins.Add(new Basin(cells, lowest, filled[lowest] - h[lowest]));
        }
        return basins;
    }

    /// <summary>
    /// Lowers each basin's outlet by up to <paramref name="maxDepth"/>. From the basin's lowest cell, a
    /// least-cost search finds the cheapest channel to land outside the basin that already lies below the
    /// breached level (or the map edge). Cost grows with how much ground must be cut, so the channel winds
    /// along low ground like a real river; every cell on it is cut down to the level, sloping downstream.
    /// </summary>
    /// <returns>True if any basin was cut.</returns>
    private static bool Breach(float[] h, float[] filled, int cw, int cd, float maxDepth, bool firstRound, bool[]? sea = null)
    {
        if (maxDepth <= 0) return false;
        bool any = false;
        int n = h.Length;
        var cost = new float[n];
        var parent = new int[n];
        var stamp = new int[n];      // search generation that last touched a cell (avoids clearing arrays)
        var basinOf = new int[n];    // 1-based basin id per cell, 0 = none
        var open = new PriorityQueue<int, (float, int)>();
        ReadOnlySpan<(int, int, float)> dirs =
            [(1, 0, 1f), (-1, 0, 1f), (0, 1, 1f), (0, -1, 1f), (1, 1, 1.414f), (1, -1, 1.414f), (-1, 1, 1.414f), (-1, -1, 1.414f)];

        var basins = Basins(h, filled, cw, cd, sea);
        for (int b = 0; b < basins.Count; b++)
            foreach (int c in basins[b].Cells) basinOf[c] = b + 1;

        for (int b = 0; b < basins.Count; b++)
        {
            var basin = basins[b];
            int gen = b + 1;
            float cutDepth = firstRound ? MathF.Min(maxDepth, basin.MaxDepth * BreachFraction)
                : basin.MaxDepth <= LakeMinDepth * 2 ? basin.MaxDepth : 0f;
            if (cutDepth <= 0) continue;
            float level = filled[basin.Lowest] - cutDepth;
            open.Clear();
            stamp[basin.Lowest] = gen;
            cost[basin.Lowest] = 0;
            parent[basin.Lowest] = -1;
            open.Enqueue(basin.Lowest, (0, basin.Lowest));
            int exit = -1, budget = 400_000;

            while (open.TryDequeue(out int c, out var pri) && budget-- > 0)
            {
                if (pri.Item1 > cost[c]) continue;
                int x = c % cw, y = c / cw;
                bool edge = x == 0 || y == 0 || x == cw - 1 || y == cd - 1;
                if (basinOf[c] != gen && (h[c] <= level || edge)) { exit = c; break; }
                foreach (var (dx, dy, step) in dirs)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)cw || (uint)ny >= (uint)cd) continue;
                    int q = ny * cw + nx;
                    float g = cost[c] + step + 4f * MathF.Max(0f, h[q] - level);
                    if (stamp[q] == gen && cost[q] <= g) continue;
                    stamp[q] = gen;
                    cost[q] = g;
                    parent[q] = c;
                    open.Enqueue(q, (g, q));
                }
            }
            if (exit < 0) continue; // no way out within budget: stays a lake

            var path = new List<int>();
            for (int c = exit; c >= 0; c = parent[c]) path.Add(c);
            for (int i = path.Count - 1, k = 0; i >= 0; i--, k++)
            {
                float cut = level - 0.02f * k;
                if (h[path[i]] > cut)
                {
                    h[path[i]] = cut;
                    any = true;
                }
            }
        }
        return any;
    }
    private static (float X, float Y) NearestEdge(float x, float y, int w, int d)
    {
        float left = x, right = w - x, down = y, up = d - y;
        float m = MathF.Min(MathF.Min(left, right), MathF.Min(down, up));
        if (m == left) return (-1, y);
        if (m == right) return (w + 1, y);
        if (m == down) return (x, -1);
        return (x, d + 1);
    }

    private static void StampDisc(Terrain t, float px, float py, float radius, byte depth, float waterLevel, float seed,
        float[] influence, float[] level, byte[] channelDepth, float[] channelLevel)
    {
        int w = t.Width, d = t.Depth;
        int x0 = Math.Max(0, (int)MathF.Floor(px - radius)), x1 = Math.Min(w - 1, (int)MathF.Ceiling(px + radius));
        int y0 = Math.Max(0, (int)MathF.Floor(py - radius)), y1 = Math.Min(d - 1, (int)MathF.Ceiling(py + radius));
        float r2 = radius * radius;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float dx = x + 0.5f - px, dy = y + 0.5f - py;
            if (dx * dx + dy * dy > r2) continue;
            int i = y * w + x;
            if (depth > channelDepth[i]) channelDepth[i] = depth;
            if (waterLevel < channelLevel[i]) channelLevel[i] = waterLevel;
            if (seed > influence[i] || (seed == influence[i] && waterLevel < level[i]))
            {
                influence[i] = seed;
                level[i] = waterLevel;
            }
        }
    }

    /// <summary>
    /// Spreads (influence, level) outwards from the seeds with a two-pass chamfer sweep: each tile keeps the
    /// strongest influence reaching it (minus <paramref name="falloff"/> per tile travelled), and that seed's
    /// level and distance. Two forward/backward rounds settle it well enough for terrain.
    /// </summary>
    private static void Propagate(float[] influence, float[] level, float[] dist, int w, int d, float falloff)
    {
        const float diag = 1.41421356f;
        for (int round = 0; round < 2; round++)
        {
            for (int y = 0; y < d; y++)
            for (int x = 0; x < w; x++)
            {
                Relax(x, y, x - 1, y, 1f);
                Relax(x, y, x - 1, y - 1, diag);
                Relax(x, y, x, y - 1, 1f);
                Relax(x, y, x + 1, y - 1, diag);
            }
            for (int y = d - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                Relax(x, y, x + 1, y, 1f);
                Relax(x, y, x + 1, y + 1, diag);
                Relax(x, y, x, y + 1, 1f);
                Relax(x, y, x - 1, y + 1, diag);
            }
        }

        void Relax(int x, int y, int fx, int fy, float step)
        {
            if ((uint)fx >= (uint)w || (uint)fy >= (uint)d) return;
            int from = fy * w + fx, to = y * w + x;
            float v = influence[from] - falloff * step;
            if (v <= influence[to]) return;
            influence[to] = v;
            level[to] = level[from];
            dist[to] = dist[from] + step;
        }
    }

    // ---- Spawn ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Water flows between orthogonal neighbours only, so two water tiles touching just at a corner are dammed
    /// apart; thin rivers drawn along diagonals are full of such joints. Wherever that happens, one of the two
    /// tiles they both border becomes river too, so every channel is 4-connected (levels come later).
    /// </summary>
    private static void JoinCornerContacts(Terrain t, byte[] riverDepth)
    {
        int w = t.Width, d = t.Depth;
        bool Wet(int x, int y) => t.Kind[y * w + x] != SurfaceKind.Land;
        for (int y = 0; y < d - 1; y++)
        for (int x = 0; x < w; x++)
        {
            if (!Wet(x, y)) continue;
            foreach (int dx in new[] { -1, 1 })
            {
                int nx = x + dx, ny = y + 1;
                if (nx < 0 || nx >= w || !Wet(nx, ny) || Wet(nx, y) || Wet(x, ny)) continue;
                int a = y * w + x, b = ny * w + nx, bridge = y * w + nx;
                t.Kind[bridge] = SurfaceKind.RiverBed;
                riverDepth[bridge] = (byte)Math.Max(1, Math.Min((int)riverDepth[a], riverDepth[b]));
            }
        }
    }

    /// <summary>
    /// Every river and lake must drain to the map edge, or the water arriving in it piles up and floods its
    /// shores. Turning the coarse drainage into fine channels can leave a system sealed off (a strip of land
    /// between a lake and its outflow, say). For each river/lake system that can't reach the edge through water
    /// tiles, follow the coarse drainage from its lowest tile and mark a one-tile channel until it
    /// meets water that does, or the edge. Deterministic (systems found in scan order).
    /// </summary>
    /// <summary>
    /// A channel from a sealed water system to water that drains (or the map edge) along the cheapest way: a step costs
    /// a tile plus 6 for every level of ground above the system's water, so it follows valleys. Marks the land it
    /// crosses as a one-tile river. False if nothing was found within the search budget.
    /// </summary>
    private static bool LowestOutlet(Terrain t, byte[] riverDepth, List<int> system, bool[] inSystem, bool[] drains, int level)
    {
        int w = t.Width, d = t.Depth;
        int exit;
        var cost = new Dictionary<int, float>();
        var parent = new Dictionary<int, int>();
        var open = new PriorityQueue<int, (float, int)>();
        float Toward(int c) => 0.9f * Math.Min(Math.Min(c % w, w - 1 - c % w), Math.Min(c / w, d - 1 - c / w));
        foreach (int c in system)
        {
            cost[c] = 0;
            parent[c] = -1;
            open.Enqueue(c, (Toward(c), c));
        }
        // Searched best-first with a pull towards the nearest map edge (each step costs at least 1, so the pull keeps it
        // near the cheapest way while it stops flooding every low flat round a big lake).
        exit = -1;
        int budget = 250_000;
        while (open.TryDequeue(out int c, out var pri) && budget-- > 0)
        {
            if (pri.Item1 > cost[c] + Toward(c)) continue;
            int x = c % w, y = c / w;
            if (!inSystem[c] && (drains[c] || x == 0 || y == 0 || x == w - 1 || y == d - 1)) { exit = c; break; }
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)d) continue;
                int m = ny * w + nx;
                if (inSystem[m]) continue;
                float g = cost[c] + 1f + (t.Kind[m] == SurfaceKind.Land ? 6f * Math.Max(0, t.Surface[m] - level) : 0f);
                if (cost.TryGetValue(m, out float have) && have <= g) continue;
                cost[m] = g;
                parent[m] = c;
                open.Enqueue(m, (g + Toward(m), m));
            }
        }
        if (exit < 0) return false;
        for (int c = exit; c >= 0 && !inSystem[c]; c = parent[c])
        {
            drains[c] = true; // the new channel drains (or the scan would take it for a sealed system of its own)
            if (t.Kind[c] != SurfaceKind.Land) continue;
            t.Kind[c] = SurfaceKind.RiverBed;
            riverDepth[c] = 1;
        }
        return true;
    }

    private static void ConnectWaterToEdges(Terrain t, byte[] riverDepth, int[] receiver, int cw, int cd)
    {
        int w = t.Width, d = t.Depth;
        bool Wet(int i) => t.Kind[i] != SurfaceKind.Land;
        var drains = new bool[w * d];
        var stack = new Stack<int>();
        void Spread(int from)
        {
            drains[from] = true;
            stack.Push(from);
            while (stack.TryPop(out int c))
            {
                int cx = c % w, cy = c / w;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx != 0) == (dy != 0)) continue; // orthogonal neighbours only: water can't pass corners
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= d) continue;
                    int n = ny * w + nx;
                    if (drains[n] || !Wet(n)) continue;
                    drains[n] = true;
                    stack.Push(n);
                }
            }
        }
        for (int x = 0; x < w; x++)
        {
            if (Wet(x) && !drains[x]) Spread(x);
            int b = (d - 1) * w + x;
            if (Wet(b) && !drains[b]) Spread(b);
        }
        for (int y = 0; y < d; y++)
        {
            if (Wet(y * w) && !drains[y * w]) Spread(y * w);
            int r = y * w + w - 1;
            if (Wet(r) && !drains[r]) Spread(r);
        }

        var system = new List<int>();
        var inSystem = new bool[w * d];
        for (int start = 0; start < w * d; start++)
        {
            if (!Wet(start) || drains[start] || inSystem[start]) continue;
            // Gather the sealed system and its lowest water surface.
            system.Clear();
            inSystem[start] = true;
            stack.Push(start);
            while (stack.TryPop(out int c))
            {
                system.Add(c);
                int cx = c % w, cy = c / w;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx != 0) == (dy != 0)) continue;
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= d) continue;
                    int n = ny * w + nx;
                    if (inSystem[n] || !Wet(n)) continue;
                    inSystem[n] = true;
                    stack.Push(n);
                }
            }
            int lowest = system[0];
            foreach (int c in system)
                if (t.Surface[c] + t.Water[c] < t.Surface[lowest] + t.Water[lowest] ||
                    t.Surface[c] + t.Water[c] == t.Surface[lowest] + t.Water[lowest] && c < lowest) lowest = c;

            // Tectonic worlds: the outlet winds along the lowest ground to water that drains (a straight cut across the
            // coarse drainage ran through hills and SetWaterLevels sank it into a canyon).
            if (t.Landform != null && LowestOutlet(t, riverDepth, system, inSystem, drains, t.Surface[lowest] + t.Water[lowest]))
            {
                foreach (int c in system) drains[c] = true;
                continue;
            }

            // Walk the coarse drainage from there, marking a one-tile channel (SetWaterLevels makes it descend).
            int px = lowest % w, py = lowest / w;
            int cell = Math.Min(py / Coarse, cd - 1) * cw + Math.Min(px / Coarse, cw - 1);
            for (int guard = 0; guard < cw * cd; guard++)
            {
                int next = receiver[cell];
                int tx, ty;
                if (next < 0) (tx, ty) = (px < w / 2 ? 0 : w - 1, py); // a coarse outlet: run off the nearest side
                else (tx, ty) = (CoarseCentreX(next % cw, w), CoarseCentreY(next / cw, d));
                bool joined = false;
                // Step one axis at a time, so the channel is 4-connected (water can't flow through corners).
                int x = px, y = py;
                while ((x != tx || y != ty) && !joined)
                {
                    if (Math.Abs(tx - x) >= Math.Abs(ty - y)) x += Math.Sign(tx - x);
                    else y += Math.Sign(ty - y);
                    int i = y * w + x;
                    if (drains[i]) { joined = true; break; }
                    if (Wet(i)) continue;
                    t.Kind[i] = SurfaceKind.RiverBed;
                    riverDepth[i] = 1;
                    if (x == 0 || y == 0 || x == w - 1 || y == d - 1) joined = true;
                }
                if (joined || next < 0) break;
                (px, py, cell) = (tx, ty, next);
            }
            foreach (int c in system) drains[c] = true; // connected now (or as connected as the drainage allows)
        }
    }

    /// <summary>
    /// Lakes are found on the coarse grid, so fine land inside a basin can sit below the lake's surface without
    /// being marked as lake; the water would spread over it later and look like a flood. Grow each lake over the
    /// connected land below its surface (8-way, within <see cref="LakeShoreReach"/> tiles), so it starts at its
    /// true shoreline. Deterministic (scan-order seeding, FIFO growth, lowest surface wins).
    /// </summary>
    private static void ExtendLakesToTheirShores(Terrain t)
    {
        int w = t.Width, d = t.Depth;
        var level = new int[w * d];
        var reach = new int[w * d];
        var queue = new Queue<int>();
        for (int i = 0; i < w * d; i++)
        {
            if (t.Kind[i] != SurfaceKind.LakeBed) continue;
            level[i] = t.Surface[i] + t.Water[i];
            queue.Enqueue(i);
        }
        while (queue.TryDequeue(out int c))
        {
            if (reach[c] >= LakeShoreReach) continue;
            int cx = c % w, cy = c / w;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= d) continue;
                int n = ny * w + nx;
                if (t.Kind[n] != SurfaceKind.Land || t.Surface[n] >= level[c]) continue;
                t.Kind[n] = SurfaceKind.LakeBed;
                t.Water[n] = (byte)Math.Min(255, level[c] - t.Surface[n]);
                level[n] = level[c];
                reach[n] = reach[c] + 1;
                queue.Enqueue(n);
            }
        }
    }

    /// <summary>How far (tiles) a lake may spread past its coarse cells to reach its shore.</summary>
    public const int LakeShoreReach = 48;

    /// <summary>
    /// Gives every water tile a whole-level water surface (the level of its top water cell) so the generated water
    /// is already at rest, then cuts river beds below it.
    /// <list type="bullet">
    /// <item>A river wants its surface level with its banks: the lowest land within two tiles, smoothed over the
    /// river within two tiles so a reach is level across its width (no side-by-side steps).</item>
    /// <item>Levels never rise downstream: a priority flood walks upstream from the outlets (water on the map
    /// edge), lowest first, and where a tile wants to sit lower than the way out, the river downstream is lowered
    /// to it. Rivers so become level reaches joined by drops across their whole width.</item>
    /// <item>Lakes keep their level. A river whose banks sit below the lake it flows into becomes part of that lake
    /// (backwater) instead of being flooded by it.</item>
    /// </list>
    /// Fills <see cref="FlowDirection"/> with each river tile's downstream neighbour. Deterministic.
    /// </summary>
    private static void SetWaterLevels(Terrain t, byte[] riverDepth)
    {
        int w = t.Width, d = t.Depth, n = w * d;
        bool River(int i) => t.Kind[i] == SurfaceKind.RiverBed;
        bool Lake(int i) => t.Kind[i] == SurfaceKind.LakeBed;

        // What each water tile wants: lakes their level, rivers their banks' height.
        var bank = new int[n];
        Parallel.For(0, d, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!River(i)) continue;
                int b = t.Surface[i];
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= d) continue;
                    int j = ny * w + nx;
                    if (t.Kind[j] == SurfaceKind.Land) b = Math.Min(b, t.Surface[j]);
                }
                bank[i] = b;
            }
        });
        var want = new int[n];
        Parallel.For(0, d, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (Lake(i)) want[i] = t.Surface[i] + t.Water[i];
                if (!River(i)) continue;
                int b = bank[i];
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= d) continue;
                    int j = ny * w + nx;
                    if (River(j)) b = Math.Min(b, bank[j]);
                }
                want[i] = Math.Max(riverDepth[i] + 1, b - 1); // a level of bank above the water: room for its flow
            }
        });

        var top = new int[n];
        var parent = new int[n];
        var seen = new bool[n];
        var queue = new PriorityQueue<int, (int, int)>();
        for (int y = 0; y < d; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            if (t.Kind[i] == SurfaceKind.Land || x != 0 && y != 0 && x != w - 1 && y != d - 1) continue;
            seen[i] = true;
            parent[i] = -1;
            top[i] = want[i];
            queue.Enqueue(i, (top[i], i));
        }
        Span<int> nbr = stackalloc int[4];
        while (queue.TryDequeue(out int c, out _))
        {
            int cx = c % w, cy = c / w;
            nbr[0] = cx + 1 < w ? c + 1 : -1;
            nbr[1] = cx > 0 ? c - 1 : -1;
            nbr[2] = cy + 1 < d ? c + w : -1;
            nbr[3] = cy > 0 ? c - w : -1;
            foreach (int m in nbr)
            {
                if (m < 0 || seen[m] || t.Kind[m] == SurfaceKind.Land) continue;
                seen[m] = true;
                parent[m] = c;
                int v = want[m];
                if (Lake(m))
                {
                    // A lake is flat and keeps the level its basin fills to. Reached from its outflow it spills into
                    // it (a drop at the outlet); an outflow standing higher is cut down to the lake.
                    top[m] = Lake(c) ? top[c] : v;
                    if (!Lake(c) && v < top[c])
                        for (int p = c; p >= 0 && River(p) && top[p] > v; p = parent[p]) top[p] = v;
                }
                else if (v >= top[c]) top[m] = v;
                else if (River(m) && Lake(c))
                {
                    // Backwater: the river's banks are lower than the lake it runs into, so it is part of the lake.
                    t.Kind[m] = SurfaceKind.LakeBed;
                    top[m] = top[c];
                }
                else
                {
                    // m is lower than the way out: lower the river downstream to it. If that river runs into a lake
                    // standing higher (lakes keep their level), the valley here is below the lake: it fills, so m
                    // joins the lake's water instead (and the land around it below that level joins it later).
                    int q = c;
                    while (q >= 0 && River(q) && top[q] > v) q = parent[q];
                    if (q >= 0 && Lake(q) && top[q] > v)
                    {
                        t.Kind[m] = SurfaceKind.LakeBed;
                        top[m] = top[c];
                    }
                    else
                    {
                        for (int p = c; p >= 0 && River(p) && top[p] > v; p = parent[p]) top[p] = v;
                        top[m] = v;
                    }
                }
                queue.Enqueue(m, (top[m], m));
            }
        }

        // Side by side, water is level: a river tile beside a lower river or lake tile that isn't up/downstream of
        // it takes that lower level (and everything downstream of it follows), so a river only drops across its
        // whole width and never stands a level above the water beside it. Levels only ever go down.
        var rivers = new List<int>();
        for (int i = 0; i < n; i++) if (River(i) && seen[i]) rivers.Add(i); // (kinds don't change below: only levels)
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (int i in rivers)
            {
                int x = i % w, y = i / w;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= w || ny >= d) continue;
                    int m = ny * w + nx;
                    if (t.Kind[m] == SurfaceKind.Land || !seen[m] || top[m] >= top[i] || parent[i] == m || parent[m] == i && River(m)) continue;
                    // Not if the river below runs into a lake standing higher: it would end up under that lake.
                    int q = i;
                    while (q >= 0 && River(q) && top[q] > top[m]) q = parent[q];
                    if (q >= 0 && Lake(q) && top[q] > top[m]) continue;
                    for (int p = i; p >= 0 && River(p) && top[p] > top[m]; p = parent[p]) top[p] = top[m];
                    changed = true;
                }
            }
        }
        var converted = new List<(int Tile, int Bed)>();
        // A lake keeps its level, so where a river runs past it lower down (not its outlet), the lake's edge
        // there becomes shore at the lake's level: a bank between them instead of water beside lower water.
        for (int i = 0; i < n; i++)
        {
            if (!Lake(i) || !seen[i]) continue;
            int x = i % w, y = i / w;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= d) continue;
                int m = ny * w + nx;
                if (!River(m) || !seen[m] || top[m] >= top[i] || parent[i] == m) continue;
                converted.Add((i, t.Surface[i]));
                t.Kind[i] = SurfaceKind.Land;
                t.Surface[i] = top[i];
                t.Water[i] = 0;
                break;
            }
        }
        // ...unless that shore cuts water off from the map edge (a narrow neck of lake): give back any shore tile
        // between water that reaches the edge and water that doesn't, until everything reaches it again.
        if (converted.Count > 0)
        {
            var reach = new bool[n];
            var queue2 = new Queue<int>();
            void Flood(int from)
            {
                reach[from] = true;
                queue2.Enqueue(from);
                while (queue2.TryDequeue(out int c))
                {
                    int x = c % w, y = c / w;
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if (nx < 0 || ny < 0 || nx >= w || ny >= d) continue;
                        int m = ny * w + nx;
                        if (reach[m] || t.Kind[m] == SurfaceKind.Land) continue;
                        reach[m] = true;
                        queue2.Enqueue(m);
                    }
                }
            }
            for (int i = 0; i < n; i++)
            {
                int x = i % w, y = i / w;
                if ((x == 0 || y == 0 || x == w - 1 || y == d - 1) && t.Kind[i] != SurfaceKind.Land && !reach[i]) Flood(i);
            }
            for (bool restored = true; restored;)
            {
                restored = false;
                foreach (var (i, bed) in converted)
                {
                    if (t.Kind[i] != SurfaceKind.Land) continue;
                    int x = i % w, y = i / w;
                    bool reached = false, cut = false;
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if (nx < 0 || ny < 0 || nx >= w || ny >= d) continue;
                        int m = ny * w + nx;
                        if (t.Kind[m] == SurfaceKind.Land) continue;
                        if (reach[m]) reached = true; else cut = true;
                    }
                    if (!reached || !cut) continue;
                    t.Kind[i] = SurfaceKind.LakeBed;
                    t.Surface[i] = bed;
                    Flood(i);
                    restored = true;
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            t.FlowDirection[i] = NoFlow;
            if (River(i))
            {
                int lvl = seen[i] ? top[i] : want[i];
                t.Surface[i] = Math.Max(1, lvl - riverDepth[i]);
                t.Water[i] = (byte)(lvl - t.Surface[i]);
                if (seen[i] && parent[i] >= 0)
                    t.FlowDirection[i] = (byte)(parent[i] == i + 1 ? 0 : parent[i] == i - 1 ? 2 : parent[i] == i + w ? 1 : 3);
            }
            else if (Lake(i) && seen[i])
            {
                // A lake that sank (to its outlet, or a lower river it touches) keeps its extent: where its bed
                // would stand above the new level it is dug a level below it, so the rivers through it stay joined.
                if (top[i] <= t.Surface[i]) t.Surface[i] = Math.Max(1, top[i] - 1);
                t.Water[i] = (byte)Math.Min(255, top[i] - t.Surface[i]);
            }
        }
    }

    /// <summary>
    /// A stream that just starts out of the grass reads as a river going nowhere. Where a narrow river begins
    /// (nothing flows into it and it has at most four other water tiles around it), the land around its first tile
    /// becomes a small spring pool at its level. <see cref="ShapeBanks"/> later keeps the land around it at least
    /// that high, so it holds. Returns the number of springs marked. Deterministic.
    /// </summary>
    private static int MarkSpringPools(Terrain t)
    {
        int w = t.Width, d = t.Depth, count = 0;
        var heads = new List<int>();
        for (int y = 1; y < d - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            int i = y * w + x;
            if (t.Kind[i] != SurfaceKind.RiverBed || t.FlowDirection[i] == NoFlow) continue;
            int water = 0;
            bool fed = false;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int j = i + dy * w + dx;
                if (t.Kind[j] == SurfaceKind.Land) continue;
                water++;
                if ((dx == 0) != (dy == 0) && t.Kind[j] == SurfaceKind.RiverBed &&
                    t.FlowDirection[j] == (dx == 1 ? 2 : dx == -1 ? 0 : dy == 1 ? 3 : 1)) fed = true;
            }
            if (!fed && water <= 4) heads.Add(i); // the end of a stream up to two tiles wide (wide rivers' unfed edges have more water around)
        }
        foreach (int i in heads)
        {
            int x = i % w, y = i / w, level = t.Surface[i] + t.Water[i];
            for (int pass = 0; pass < 2; pass++) // corners join once the edges beside them have
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int j = i + dy * w + dx;
                if (t.Kind[j] != SurfaceKind.Land || x + dx <= 0 || y + dy <= 0 || x + dx >= w - 1 || y + dy >= d - 1) continue;
                // Only where it touches water at its own level (no ledge beside the stream's drop below).
                bool level0 = true, touches = false;
                foreach (int o in (ReadOnlySpan<int>)[j + 1, j - 1, j + w, j - w])
                    if (t.Kind[o] != SurfaceKind.Land) { touches = true; if (t.Surface[o] + t.Water[o] != level) level0 = false; }
                if (!level0 || !touches) continue;
                t.Kind[j] = SurfaceKind.LakeBed;
                t.Surface[j] = Math.Max(1, level - 1);
                t.Water[j] = (byte)(level - t.Surface[j]);
            }
            count++;
        }
        return count;
    }

    /// <summary>
    /// A lake is held at its level by a sill where it spills out: lake tiles beside a lower river (its outlets) get
    /// their bed raised to a level below the lake's surface, so the lake drains only down to the sill (a level below
    /// its shore) instead of emptying into the river below.
    /// </summary>
    private static void BuildLakeSills(Terrain t)
    {
        int w = t.Width, d = t.Depth;
        for (int y = 1; y < d - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            int i = y * w + x;
            if (t.Kind[i] != SurfaceKind.LakeBed) continue;
            int level = t.Surface[i] + t.Water[i];
            bool outlet = false;
            foreach (int j in (ReadOnlySpan<int>)[i + 1, i - 1, i + w, i - w])
                if (t.Kind[j] == SurfaceKind.RiverBed && t.Surface[j] + t.Water[j] < level) outlet = true;
            if (!outlet || t.Surface[i] >= level - 1) continue;
            t.Surface[i] = level - 1;
            t.Water[i] = 1;
        }
    }

    /// <summary>No flow direction (land, lakes, or water that doesn't reach an outlet).</summary>
    public const byte NoFlow = 255;

    /// <summary>How far (tiles) river banks are graded down towards the water.</summary>
    public const int BankReach = 5;

    /// <summary>
    /// Grades the land beside rivers: within <see cref="BankReach"/> tiles it rises at most one level per tile from
    /// the water's level (the first tile a level above it: the bank), so rivers run in shallow valleys with a bank to
    /// the bank instead of at the bottom of a cut. Land beside water is never left below that water's surface
    /// (raised if need be), so nothing spills. Only reshapes land tiles. Deterministic.
    /// </summary>
    /// <summary>
    /// The tectonic generator's banks (<see cref="ShapeBanks"/> with a negative reach): from the bank the land may rise a
    /// level a tile for the first <see cref="BankReach"/> tiles, then two a tile, with no limit of distance, so a river
    /// deep in a mountain valley gets steep valley sides instead of a wall where grading stopped. A bucket queue
    /// (levels are small integers) keeps it linear. Continues <paramref name="cap"/> and <paramref name="dist"/> from
    /// the seeded banks.
    /// </summary>
    private static void GradeValleySides(Terrain t, int[] cap, byte[] dist)
    {
        int w = t.Width, d = t.Depth, n = w * d, top = t.Height + 2;
        var buckets = new List<int>[top + 3];
        for (int i = 0; i < n; i++)
            if (cap[i] < top) (buckets[cap[i]] ??= new List<int>()).Add(i);
        for (int v = 0; v < top; v++)
        {
            if (buckets[v] is not { } list) continue;
            for (int q = 0; q < list.Count; q++)
            {
                int c = list[q];
                if (cap[c] != v) continue;
                int next = v + (dist[c] < BankReach ? 1 : 2);
                if (next >= top) continue;
                int x = c % w, y = c / w;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)d) continue;
                    int j = ny * w + nx;
                    if (t.Kind[j] != SurfaceKind.Land || cap[j] <= next) continue;
                    cap[j] = next;
                    dist[j] = (byte)Math.Min(255, dist[c] + 1);
                    (buckets[next] ??= new List<int>()).Add(j);
                }
            }
            buckets[v] = null;
        }
    }

    private static void ShapeBanks(Terrain t, int reach)
    {
        int w = t.Width, d = t.Depth, n = w * d;
        var cap = new int[n];
        var dist = new byte[n];
        Array.Fill(cap, int.MaxValue);
        var queue = new PriorityQueue<int, (int, int)>();
        for (int i = 0; i < n; i++)
        {
            if (t.Kind[i] != SurfaceKind.RiverBed) continue;
            int x = i % w, y = i / w, lvl = t.Surface[i] + t.Water[i];
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= d) continue;
                int j = ny * w + nx;
                if (t.Kind[j] != SurfaceKind.Land || cap[j] <= lvl + 1) continue;
                cap[j] = lvl + 1; // the bank: a level above the river's water
                dist[j] = 1;
                queue.Enqueue(j, (lvl + 1, j));
            }
        }
        while (queue.TryDequeue(out int c, out var pri))
        {
            if (pri.Item1 > cap[c] || dist[c] >= reach) continue;
            int x = c % w, y = c / w;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= d) continue;
                int j = ny * w + nx;
                if (t.Kind[j] != SurfaceKind.Land || cap[j] <= cap[c] + 1) continue;
                cap[j] = cap[c] + 1;
                dist[j] = (byte)(dist[c] + 1);
                queue.Enqueue(j, (cap[j], j));
            }
        }
        if (reach < 0) GradeValleySides(t, cap, dist);
        var lowered = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (t.Kind[i] != SurfaceKind.Land)
            {
                lowered[i] = t.Surface[i];
                continue;
            }
            int s = Math.Min(t.Surface[i], cap[i]);
            // Never below the water beside it.
            int x = i % w, y = i / w;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= d) continue;
                int j = ny * w + nx;
                if (t.Kind[j] != SurfaceKind.Land) s = Math.Max(s, t.Surface[j] + t.Water[j]);
            }
            lowered[i] = s;
        }
        if (reach < 0) BuildLevees(t, lowered);
        Array.Copy(lowered, t.Surface, n);
    }

    /// <summary>
    /// The other half of the tectonic generator's grading: land next to water stands at least at the water's level, and
    /// from there may fall a level a tile for <see cref="BankReach"/> tiles, then two, wherever the ground (already
    /// graded down towards lower water) would be lower. A river running above the land round it (it kept the level of its
    /// reach while a lower river's valley sides cut the ground away beside it) gets a sloping levee instead of a one-tile
    /// dyke with a wall behind it, and the same for a lake's rim. Raises only, land only; a bucket queue from the highest
    /// water down.
    /// </summary>
    private static void BuildLevees(Terrain t, int[] ground)
    {
        int w = t.Width, d = t.Depth, n = w * d, top = t.Height + 2;
        var floor = new int[n];
        var dist = new byte[n];
        var buckets = new List<int>[top + 1];
        for (int i = 0; i < n; i++)
        {
            if (t.Kind[i] != SurfaceKind.Land) continue;
            int x = i % w, y = i / w, need = 0;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)d) continue;
                int j = ny * w + nx;
                if (t.Kind[j] != SurfaceKind.Land) need = Math.Max(need, t.Surface[j] + t.Water[j]);
            }
            if (need <= 0) continue;
            floor[i] = Math.Min(need, top);
            dist[i] = 1;
            (buckets[floor[i]] ??= new List<int>()).Add(i);
        }
        for (int v = top; v > 0; v--)
        {
            if (buckets[v] is not { } list) continue;
            for (int q = 0; q < list.Count; q++)
            {
                int c = list[q];
                if (floor[c] != v) continue;
                int next = v - (dist[c] < BankReach ? 1 : 2);
                if (next <= 0) continue;
                int x = c % w, y = c / w;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)d) continue;
                    int j = ny * w + nx;
                    // Only where the ground is lower than the levee would be (higher ground already holds the water).
                    if (t.Kind[j] != SurfaceKind.Land || next <= ground[j] || next <= floor[j]) continue;
                    floor[j] = next;
                    dist[j] = (byte)Math.Min(255, dist[c] + 1);
                    (buckets[next] ??= new List<int>()).Add(j);
                }
            }
            buckets[v] = null;
        }
        for (int i = 0; i < n; i++)
            if (floor[i] > ground[i]) ground[i] = floor[i];
    }


    private static void ComputeWaterDistance(Terrain t)
    {
        int w = t.Width, d = t.Depth;
        var dist = t.WaterDistance;
        for (int i = 0; i < dist.Length; i++) dist[i] = t.Water[i] > 0 ? (byte)0 : byte.MaxValue;
        for (int y = 0; y < d; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            if (x > 0) dist[i] = Math.Min(dist[i], (byte)Math.Min(255, dist[i - 1] + 1));
            if (y > 0) dist[i] = Math.Min(dist[i], (byte)Math.Min(255, dist[i - w] + 1));
        }
        for (int y = d - 1; y >= 0; y--)
        for (int x = w - 1; x >= 0; x--)
        {
            int i = y * w + x;
            if (x < w - 1) dist[i] = Math.Min(dist[i], (byte)Math.Min(255, dist[i + 1] + 1));
            if (y < d - 1) dist[i] = Math.Min(dist[i], (byte)Math.Min(255, dist[i + w] + 1));
        }
    }

    /// <summary>
    /// The tectonic generator's landing: like <see cref="ChooseSpawn"/> (flat dry land near the middle, high ground to dig
    /// into, water within a walk) but weighing how livable the biome is (<see cref="Biomes.Habitability"/>), and looking
    /// further out when the middle is sea or ice.
    /// </summary>
    private static (int X, int Y) ChooseHome(Terrain t)
    {
        int w = t.Width, d = t.Depth;
        int cx = w / 2, cy = d / 2;
        float best = float.MinValue;
        (int, int) choice = (cx, cy);
        int limit = Math.Min(w, d) / 2 - 8;
        if (limit < 16) return ChooseSpawn(t);
        for (int radius = Math.Min(limit, 320); ; radius = Math.Min(limit, radius * 2))
        {
            int stepXY = Math.Max(4, radius / 80);
            for (int y = cy - radius; y <= cy + radius; y += stepXY)
            for (int x = cx - radius; x <= cx + radius; x += stepXY)
            {
                if (x < 4 || y < 4 || x >= w - 4 || y >= d - 4) continue;
                int i = y * w + x, s = t.Surface[i];
                if (t.Kind[i] != SurfaceKind.Land) continue;
                bool flat = true;
                for (int dy = -3; dy <= 3 && flat; dy++)
                for (int dx = -3; dx <= 3 && flat; dx++)
                    flat = Math.Abs(t.Surface[i + dy * w + dx] - s) <= 1 && t.Kind[i + dy * w + dx] == SurfaceKind.Land;
                if (!flat) continue;
                var biome = Biomes.At(t, x, y);
                int high = s;
                for (int dy = -48; dy <= 48; dy += 6)
                for (int dx = -48; dx <= 48; dx += 6)
                {
                    int qx = x + dx, qy = y + dy;
                    if ((uint)qx < (uint)w && (uint)qy < (uint)d) high = Math.Max(high, t.Surface[qy * w + qx]);
                }
                float score = Math.Min(high - s, 40) / 40f * 2f + 3f * Biomes.Habitability(biome.Biome);
                float wd = t.WaterDistance[i];
                if (wd < 120) score += 1.5f * (1f - Math.Abs(wd - 30f) / 120f);
                score -= MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / 320f;
                if (score > best)
                {
                    best = score;
                    choice = (x, y);
                }
            }
            if (best > 1f || radius >= limit) break;
        }
        return choice;
    }

    /// <summary>
    /// A small map of the finished world for the loading screen (RGB, north up, at most <paramref name="size"/> pixels
    /// across): biome colours, water, light from the northwest.
    /// </summary>
    public (byte[] Rgb, int Width, int Height) Picture(int size = 256)
    {
        int step = Math.Max(1, (Math.Max(Width, Depth) + size - 1) / size);
        int pw = (Width + step - 1) / step, ph = (Depth + step - 1) / step;
        var rgb = new byte[pw * ph * 3];
        Parallel.For(0, ph, py =>
        {
            for (int px = 0; px < pw; px++)
            {
                int x = Math.Min(Width - 1, px * step + step / 2), y = Math.Min(Depth - 1, py * step + step / 2);
                int i = y * Width + x;
                var (r, g, b) = Biomes.Colour(Biomes.At(this, x, y).Biome);
                float shade = 1f;
                if (Kind[i] == SurfaceKind.Land)
                {
                    int xl = Math.Max(0, x - step), yu = Math.Max(0, y - step), xr = Math.Min(Width - 1, x + step), yd = Math.Min(Depth - 1, y + step);
                    float slope = (Surface[y * Width + xl] - Surface[y * Width + xr] + Surface[yu * Width + x] - Surface[yd * Width + x]) / (2f * step);
                    shade = Math.Clamp(1f + slope * 0.35f, 0.6f, 1.35f);
                }
                int o = (py * pw + px) * 3;
                rgb[o] = (byte)Math.Clamp(r * shade * 255f, 0f, 255f);
                rgb[o + 1] = (byte)Math.Clamp(g * shade * 255f, 0f, 255f);
                rgb[o + 2] = (byte)Math.Clamp(b * shade * 255f, 0f, 255f);
            }
        });
        return (rgb, pw, ph);
    }

    /// <summary>
    /// A starting spot near the map centre on flat, dry land, preferably with high ground (something to dig
    /// into) and a river within walking distance. Falls back to the centre.
    /// </summary>
    private static (int X, int Y) ChooseSpawn(Terrain t)
    {
        int w = t.Width, d = t.Depth;
        int cx = w / 2, cy = d / 2;
        int radius = Math.Min(Math.Min(w, d) / 2 - 8, 320);
        if (radius < 16) return (cx, cy);
        var waterDist = t.WaterDistance;

        float best = float.MinValue;
        (int, int) choice = (cx, cy);
        for (int y = cy - radius; y <= cy + radius; y += 4)
        for (int x = cx - radius; x <= cx + radius; x += 4)
        {
            if (x < 4 || y < 4 || x >= w - 4 || y >= d - 4) continue;
            int i = y * w + x, s = t.Surface[i];
            if (t.Kind[i] != SurfaceKind.Land) continue;

            // Flat enough for the starting party: at most one level of variation within 3 tiles.
            bool flat = true;
            for (int dy = -3; dy <= 3 && flat; dy++)
            for (int dx = -3; dx <= 3 && flat; dx++)
                flat = Math.Abs(t.Surface[i + dy * w + dx] - s) <= 1 && t.Kind[i + dy * w + dx] == SurfaceKind.Land;
            if (!flat) continue;

            // Relief: highest ground within ~48 tiles.
            int high = s;
            for (int dy = -48; dy <= 48; dy += 6)
            for (int dx = -48; dx <= 48; dx += 6)
            {
                int qx = x + dx, qy = y + dy;
                if ((uint)qx < (uint)w && (uint)qy < (uint)d) high = Math.Max(high, t.Surface[qy * w + qx]);
            }

            float score = Math.Min(high - s, 40) / 40f * 2f;                       // a hillside to dig into
            float wd = waterDist[i];
            if (wd < 120) score += 1.5f * (1f - Math.Abs(wd - 30f) / 120f);        // water nearby, not too near
            score -= MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / radius; // stay central
            if (score > best)
            {
                best = score;
                choice = (x, y);
            }
        }
        return choice;
    }
}
