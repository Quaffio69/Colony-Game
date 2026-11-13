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

    private Terrain(int w, int d, int h)
    {
        Width = w;
        Depth = d;
        Height = h;
        Surface = new int[w * d];
        Water = new byte[w * d];
        Kind = new SurfaceKind[w * d];
        WaterDistance = new byte[w * d];
    }

    public int SurfaceAt(int x, int y) => Surface[y * Width + x];

    public static Terrain Generate(WorldGenSettings s)
    {
        var t = new Terrain(s.Width, s.Depth, s.Height);
        int w = s.Width, d = s.Depth;

        // 1. Shape.
        var h = new float[w * d];
        Parallel.For(0, d, y =>
        {
            for (int x = 0; x < w; x++) h[y * w + x] = BaseHeight(s, x, y);
        });

        // 2. Drainage on the coarse grid.
        int cw = (w + Coarse - 1) / Coarse, cd = (d + Coarse - 1) / Coarse;
        var ch = new float[cw * cd];
        for (int cy = 0; cy < cd; cy++)
        for (int cx = 0; cx < cw; cx++)
            ch[cy * cw + cx] = h[CoarseCentreY(cy, d) * w + CoarseCentreX(cx, w)];
        var (filled, _, _) = PriorityFlood(ch, cw, cd);
        // Rivers cut through the rims of most basins: breach each basin's outlet (up to a limit), then
        // re-flood. Shallow basins drain into river valleys; only deep ones remain, as smaller lakes.
        // Grouped basins hide smaller dips inside them, so repeat: the first round cuts every basin's outlet by
        // most of its depth (deep basins keep a smaller lake); later rounds fully drain the leftover small dips.
        for (int round = 0; round < 4; round++)
        {
            if (!Breach(ch, filled, cw, cd, maxDepth: s.Height * BreachDepth, firstRound: round == 0)) break;
            (filled, _, _) = PriorityFlood(ch, cw, cd);
        }
        (filled, var receiver, var order) = PriorityFlood(ch, cw, cd);

        var acc = new float[cw * cd];
        Array.Fill(acc, 1f);
        for (int i = order.Length - 1; i >= 0; i--)
        {
            int c = order[i];
            if (receiver[c] >= 0) acc[receiver[c]] += acc[c];
        }
        var lake = new bool[cw * cd];
        foreach (var basin in Basins(ch, filled, cw, cd))
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

        for (int c = 0; c < acc.Length; c++)
        {
            if (acc[c] < RiverThreshold || lake[c]) continue;
            t.RiverCells++;
            float strength = Math.Clamp(MathF.Log2(acc[c] / RiverThreshold) / 6f, 0f, 1f);
            float radius = 0.7f + 3.3f * strength;
            byte depth = (byte)(1 + (int)MathF.Round(2.5f * strength));
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

        // Final surface: pull the land towards the nearby river level (only ever lowering it), then cut channels.
        Parallel.For(0, d, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float ground = h[i];
                if (influence[i] > 0)
                {
                    float k = Smooth(Math.Clamp(influence[i], 0f, 1f)) * 0.92f;
                    float target = level[i] + 0.04f * dist[i]; // floodplains rise gently away from the river
                    if (target < ground) ground += (target - ground) * k;
                }

                int surface;
                if (channelDepth[i] > 0)
                {
                    int waterTop = (int)MathF.Floor(MathF.Min(channelLevel[i], ground));
                    surface = waterTop - channelDepth[i];
                    t.Kind[i] = SurfaceKind.RiverBed;
                }
                else
                {
                    surface = (int)MathF.Floor(ground);
                }
                surface = Math.Clamp(surface, 1, s.Height - 2);
                if (t.Kind[i] == SurfaceKind.RiverBed)
                    t.Water[i] = (byte)Math.Clamp((int)MathF.Floor(MathF.Min(channelLevel[i], ground)) - surface, 1, 255);

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

        ComputeWaterDistance(t);
        (t.SpawnX, t.SpawnY) = ChooseSpawn(t);
        return t;
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
    private static float Ridged(float x, float y, int seed, int octaves)
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
    private static (float[] Filled, int[] Receiver, int[] Order) PriorityFlood(float[] h, int cw, int cd)
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
            if (x != 0 && y != 0 && x != cw - 1 && y != cd - 1) continue;
            filled[c] = h[c];
            receiver[c] = -1;
            done[c] = true;
            open.Enqueue(c, (h[c], c));
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
    private static List<Basin> Basins(float[] h, float[] filled, int cw, int cd)
    {
        const float wet = 0.25f;
        var basins = new List<Basin>();
        var seen = new bool[h.Length];
        var stack = new Stack<int>();
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
    private static bool Breach(float[] h, float[] filled, int cw, int cd, float maxDepth, bool firstRound)
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

        var basins = Basins(h, filled, cw, cd);
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

    /// <summary>Distance (tiles, Manhattan, capped at 255) from each column to the nearest river or lake.</summary>
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
