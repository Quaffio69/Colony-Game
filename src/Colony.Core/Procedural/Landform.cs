namespace Colony.Core.Procedural;

/// <summary>
/// The world's large-scale shape and climate (generator 2), worked out on a coarse grid (<see cref="Cell"/> tiles a node,
/// about 256 nodes across whatever the map size) in the order nature does it:
/// <list type="number">
/// <item><b>Plates.</b> Jittered seeds on a lattice (<see cref="PlateSpacing"/> apart, boundaries bent by a domain warp)
/// split the land into continental and oceanic plates, each drifting its own way. Their crust sets the base level
/// (continents stand above the sea, oceanic plates below it, blended across the boundary); where two meet, how they move
/// decides what grows there: colliding continents fold up a mountain range on the seam, an oceanic plate diving under a
/// continent raises a coastal range inland of a trench (with a chain of volcanoes behind it), two oceanic plates an
/// island arc, plates pulling apart a rift valley with raised shoulders (lakes gather in it), and plates sliding past a
/// fault trough. Every effect is a smooth function of the distance to the seam, the same from both sides, so the
/// seams carry no steps. Ranges rise and fall along their length (passes and massifs), carry ridged crests, and a few
/// hotspot volcanoes stand inside plates.</item>
/// <item><b>Climate.</b> Temperature falls with latitude (north is up and colder) and with height. A prevailing wind
/// carries moisture across the map: it picks up water over the sea, rains out on slopes it climbs (wet windward
/// mountains) and arrives dry behind them (rain shadows, where deserts form), recovering slowly over the land.</item>
/// <item><b>Erosion.</b> Rain-weighted stream-power erosion (implicit, receivers from a priority flood towards the sea
/// and the map edge) cuts branching valleys whose depth follows the rain that falls upstream; slopes steeper than the
/// angle of repose slump (no walls), soil creeps, and basins slowly silt up.</item>
/// </list>
/// The fine heightmap (<see cref="FineHeight"/>) is this surface interpolated smoothly to every tile plus detail scaled
/// by how rugged the ground is, wind-aligned dunes in sandy deserts and stepped mesas in arid uplands. Deterministic
/// from the seed (hashes and value noise; ordered searches with index tie-breaks).
/// </summary>
public sealed class Landform
{
    /// <summary>Tiles between grid nodes.</summary>
    public int Cell { get; }
    /// <summary>Grid nodes across and down: node (i, j) stands at tile (i·Cell, j·Cell), covering the whole map.</summary>
    public int GW { get; }
    public int GD { get; }
    /// <summary>Surface height (levels) per node, after erosion.</summary>
    public float[] Height { get; }
    /// <summary>Levels the plates pushed the ground up (mountain building), before erosion.</summary>
    public float[] Uplift { get; }
    /// <summary>Levels erosion carried away.</summary>
    public float[] Eroded { get; }
    /// <summary>0..1: on or near a volcano.</summary>
    public float[] Volcanic { get; }
    /// <summary>Rain index per node: about 1 in a humid temperate land, under 0.25 in a desert, 2+ on wet windward slopes.</summary>
    public float[] Rain { get; }
    /// <summary>Mean temperature (°C) at sea level per node (latitude and regional variation; height comes later).</summary>
    public float[] Temperature { get; }
    /// <summary>0..1: how broken the ground is (detail amplitude).</summary>
    public float[] Rugged { get; }
    /// <summary>True where the node is sea (below <see cref="SeaLevel"/> and open to the map edge).</summary>
    public bool[] Sea { get; }
    /// <summary>The sea's surface level (the top water level), or 0 for a world without sea.</summary>
    public int SeaLevel { get; }
    /// <summary>The reference level plates are built around (the sea's, also in worlds without one).</summary>
    public int BaseLevel { get; }
    /// <summary>Prevailing wind direction (unit vector; moisture travels along it).</summary>
    public float WindX { get; }
    public float WindY { get; }
    /// <summary>Distance between plate seeds (tiles).</summary>
    public float PlateSpacing { get; }
    /// <summary>Plates whose ground the map holds.</summary>
    public int PlateCount { get; private set; }
    /// <summary>Volcanoes raised (centres in tiles).</summary>
    public List<(float X, float Y, float Radius)> Volcanoes { get; } = new();

    private readonly WorldGenSettings _s;
    private readonly int _w, _d, _h;
    private readonly float _belt; // half-width of a mountain belt (tiles)

    /// <summary>Degrees Celsius lost per world height climbed.</summary>
    public const float Lapse = 40f;

    private Landform(WorldGenSettings s)
    {
        _s = s;
        _w = s.Width;
        _d = s.Depth;
        _h = s.Height;
        int size = Math.Max(_w, _d);
        Cell = Math.Max(4, (size + 255) / 256);
        GW = (_w + Cell - 1) / Cell + 1;
        GD = (_d + Cell - 1) / Cell + 1;
        int n = GW * GD;
        Height = new float[n];
        Uplift = new float[n];
        Eroded = new float[n];
        Volcanic = new float[n];
        Rain = new float[n];
        Temperature = new float[n];
        Rugged = new float[n];
        Sea = new bool[n];
        PlateSpacing = Math.Clamp(size * 0.5f, 320f, 1500f);
        _belt = 0.17f * PlateSpacing;
        BaseLevel = (int)MathF.Round(_h * 0.45f);
        SeaLevel = s.Shape.Oceans == Oceans.None ? 0 : BaseLevel;
        float wind = Noise.Hash(3, 1, 4, s.Seed + 57) * MathF.PI * 2f;
        WindX = MathF.Cos(wind);
        WindY = MathF.Sin(wind);
    }

    /// <summary>Builds the landform for a world (plates, climate, erosion), reporting to <paramref name="progress"/>.</summary>
    public static Landform Build(WorldGenSettings s, WorldGenProgress? progress = null)
    {
        var lf = new Landform(s);
        lf.BuildPlates(progress);
        lf.BuildClimate(progress);
        lf.Erode(progress);
        lf.Finish();
        return lf;
    }

    // ---- Plates ------------------------------------------------------------------------------------------------

    private readonly record struct Plate(int Index, float X, float Y, bool Continental, float Vx, float Vy, float Base);

    private Plate[] _plates = [];
    private int _latticeW, _latticeH;

    private float Relief01 => _s.Shape.Relief switch { Relief.Gentle => 0.35f, Relief.Rolling => 0.65f, Relief.Alpine => 1.3f, _ => 1f };

    private void MakePlates()
    {
        float p = PlateSpacing;
        _latticeW = (int)MathF.Ceiling(_w / p) + 5;
        _latticeH = (int)MathF.Ceiling(_d / p) + 5;
        _plates = new Plate[_latticeW * _latticeH];
        float land = _s.Shape.Oceans switch { Oceans.None => 1f, Oceans.Coasts => 0.8f, Oceans.Islands => 0.3f, _ => 0.62f };
        int seed = _s.Seed;
        for (int j = 0; j < _latticeH; j++)
        for (int i = 0; i < _latticeW; i++)
        {
            int li = i - 2, lj = j - 2;
            float x = (li + 0.15f + 0.7f * Noise.Hash(li, lj, 1, seed + 201)) * p;
            float y = (lj + 0.15f + 0.7f * Noise.Hash(li, lj, 2, seed + 201)) * p;
            bool continental = Noise.Hash(li, lj, 3, seed + 201) < land;
            float angle = Noise.Hash(li, lj, 4, seed + 201) * MathF.PI * 2f;
            float speed = 0.45f + 0.55f * Noise.Hash(li, lj, 5, seed + 201);
            float lift = (Noise.Hash(li, lj, 6, seed + 201) - 0.5f) * 0.04f * _h;
            _plates[j * _latticeW + i] = new Plate(j * _latticeW + i, x, y, continental, MathF.Cos(angle) * speed, MathF.Sin(angle) * speed, lift);
        }
        // The plate under the middle of the map is always land: the colony lands near the middle.
        var (cx, cy) = Warp(_w / 2f, _d / 2f);
        Span<int> near = stackalloc int[3];
        Span<float> dist = stackalloc float[3];
        Nearest(cx, cy, near, dist);
        _plates[near[0]] = _plates[near[0]] with { Continental = true };
    }

    /// <summary>Where a point falls in plate space: bent by two octaves of warp, so seams wander.</summary>
    private (float X, float Y) Warp(float x, float y)
    {
        float p = PlateSpacing;
        int seed = _s.Seed;
        float wx = x + (Noise.Fbm2D(x / (0.55f * p), y / (0.55f * p), seed + 211, 3) - 0.5f) * 0.6f * p
                     + (Noise.Fbm2D(x / (0.13f * p), y / (0.13f * p), seed + 213, 2) - 0.5f) * 0.12f * p;
        float wy = y + (Noise.Fbm2D(x / (0.55f * p), y / (0.55f * p), seed + 212, 3) - 0.5f) * 0.6f * p
                     + (Noise.Fbm2D(x / (0.13f * p), y / (0.13f * p), seed + 214, 2) - 0.5f) * 0.12f * p;
        return (wx, wy);
    }

    /// <summary>The three nearest plate seeds to a warped point (indices and distances, nearest first).</summary>
    private void Nearest(float x, float y, Span<int> idx, Span<float> dist)
    {
        dist.Fill(float.MaxValue);
        idx.Fill(0);
        float p = PlateSpacing;
        int ci = (int)MathF.Floor(x / p) + 2, cj = (int)MathF.Floor(y / p) + 2;
        for (int j = cj - 2; j <= cj + 2; j++)
        for (int i = ci - 2; i <= ci + 2; i++)
        {
            if ((uint)i >= (uint)_latticeW || (uint)j >= (uint)_latticeH) continue;
            int k = j * _latticeW + i;
            float dx = _plates[k].X - x, dy = _plates[k].Y - y;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d < dist[2])
            {
                if (d < dist[1])
                {
                    dist[2] = dist[1]; idx[2] = idx[1];
                    if (d < dist[0]) { dist[1] = dist[0]; idx[1] = idx[0]; dist[0] = d; idx[0] = k; }
                    else { dist[1] = d; idx[1] = k; }
                }
                else { dist[2] = d; idx[2] = k; }
            }
        }
    }

    /// <summary>What the plates do at a point: base level (relative to <see cref="BaseLevel"/>), seam effects, uplift.</summary>
    private (float Base, float Seam, float Uplift, float SeamDistance) Tectonics(float tx, float ty)
    {
        var (x, y) = Warp(tx, ty);
        Span<int> idx = stackalloc int[3];
        Span<float> dist = stackalloc float[3];
        Nearest(x, y, idx, dist);
        float p = PlateSpacing, w = _belt, h = _h;
        var a = _plates[idx[0]];

        // Crust: continents above the sea, oceanic plates below it, blended softly across the seam.
        float contBase = _s.Shape.Oceans == Oceans.Islands ? 0.022f * h : 0.05f * h;
        float sum = 0, weight = 0;
        for (int k = 0; k < 3; k++)
        {
            var pl = _plates[idx[k]];
            float wk = MathF.Exp(-(dist[k] - dist[0]) / (0.06f * p));
            sum += wk * ((pl.Continental ? contBase : -0.09f * h) + pl.Base);
            weight += wk;
        }
        float baseLevel = sum / weight;

        float hm = 0.5f * h * Relief01;
        float mod = 0.2f + 1.3f * Smoothstep(0.3f, 0.7f, Noise.Fbm2D(tx / (0.3f * p), ty / (0.3f * p), _s.Seed + 231, 3));
        float seam = 0, uplift = 0, nearest = float.MaxValue;
        for (int k = 1; k < 3; k++)
        {
            var b = _plates[idx[k]];
            float lx = b.X - a.X, ly = b.Y - a.Y, len = MathF.Sqrt(lx * lx + ly * ly);
            if (len < 1e-3f) continue;
            float s = (dist[k] * dist[k] - dist[0] * dist[0]) / (2f * len); // distance to the seam, on a's side
            nearest = MathF.Min(nearest, s);
            if (s > 2.4f * w) continue;
            float nx = lx / len, ny = ly / len;
            float rx = a.Vx - b.Vx, ry = a.Vy - b.Vy;
            float conv = rx * nx + ry * ny;                // > 0: closing
            float shear = MathF.Abs(rx * -ny + ry * nx);
            if (conv > 0)
            {
                float c = MathF.Sqrt(MathF.Min(conv, 1.2f));
                float e;
                if (a.Continental && b.Continental)
                    e = c * hm * mod * G(s / w); // collision: a range on the seam
                else if (a.Continental != b.Continental)
                {
                    float u = a.Continental ? s : -s; // > 0 on the continent
                    e = c * hm * mod * 0.85f * G((u - 0.5f * w) / (0.42f * w));
                    uplift += MathF.Max(0, e);
                    e -= c * 0.07f * h * G((u + 0.25f * w) / (0.22f * w)); // the trench
                    seam += e;
                    continue;
                }
                else
                {
                    float u = a.Index < b.Index ? s : -s;
                    e = c * hm * mod * 0.6f * G((u - 0.25f * w) / (0.3f * w)); // island arc
                    uplift += e;
                    seam += e - c * 0.05f * h * G((u + 0.2f * w) / (0.2f * w));
                    continue;
                }
                uplift += e;
                seam += e;
            }
            else
            {
                float c = MathF.Min(-conv, 1.2f);
                if (a.Continental && b.Continental) // rift: a valley between raised shoulders
                    seam += -c * 0.11f * h * G(s / (0.3f * w)) + c * 0.045f * h * G((s - 0.6f * w) / (0.25f * w));
                else if (!a.Continental && !b.Continental)
                    seam += c * 0.05f * h * G(s / (0.35f * w)); // mid-ocean ridge
            }
            if (a.Continental && b.Continental) // sliding past: a fault trough
                seam -= 0.035f * h * MathF.Min(shear, 1.2f) * G(s / (0.09f * w)) * (1f - MathF.Min(1f, MathF.Abs(conv)));
        }
        return (baseLevel, seam, uplift, nearest);
    }

    private static float G(float v) => MathF.Exp(-v * v);

    private void BuildPlates(WorldGenProgress? progress)
    {
        MakePlates();
        var touching = new HashSet<int>();
        Span<int> idx = stackalloc int[3];
        Span<float> dist = stackalloc float[3];
        for (int j = 0; j < GD; j += 2)
        for (int i = 0; i < GW; i += 2)
        {
            var (x, y) = Warp(i * Cell, j * Cell);
            Nearest(x, y, idx, dist);
            touching.Add(idx[0]);
        }
        PlateCount = touching.Count;
        int oceanic = touching.Count(k => !_plates[k].Continental);
        progress?.Step($"Drifting {PlateCount} plates", oceanic == 0 ? "Every one of them continental crust" :
            $"{PlateCount - oceanic} continental, {oceanic} oceanic, each drifting its own way", 0.00f, 0.06f);

        PlaceVolcanoes();
        int seed = _s.Seed;
        float h = _h;
        var raw = new float[GW * GD];
        Parallel.For(0, GD, j =>
        {
            for (int i = 0; i < GW; i++)
            {
                float tx = i * Cell, ty = j * Cell;
                var (baseLevel, seam, uplift, _) = Tectonics(tx, ty);
                float p = PlateSpacing;
                float continent = (Noise.Fbm2D(tx / (0.5f * p), ty / (0.5f * p), seed + 241, 3) - 0.5f) * 0.18f * h;
                var (wx, wy) = (tx + (Noise.Fbm2D(tx / 300f, ty / 300f, seed + 251, 2) - 0.5f) * 120f,
                                ty + (Noise.Fbm2D(tx / 300f, ty / 300f, seed + 252, 2) - 0.5f) * 120f);
                float ridges = (Terrain.Ridged(wx / 150f, wy / 150f, seed + 253, 5) - 0.32f) * 0.75f * uplift;
                float hills = (Noise.Fbm2D(tx / 260f, ty / 260f, seed + 254, 4) - 0.5f) * 0.10f * h * MathF.Max(0.5f, MathF.Min(Relief01, 1.1f));
                float cones = 0, volc = 0;
                foreach (var (vx, vy, vr, vh, cr, cd) in _cones)
                {
                    float dx = tx - vx, dy = ty - vy;
                    float r = MathF.Sqrt(dx * dx + dy * dy);
                    if (r >= vr * 1.6f) continue;
                    // Lava flows and gullies make the cone irregular: its radius wanders with direction and distance.
                    float rr = r / (0.75f + 0.5f * Noise.Fbm2D(tx / 45f, ty / 45f, seed + 255, 3));
                    volc = MathF.Max(volc, Math.Clamp(1f - rr / (0.55f * vr), 0f, 1f));
                    if (rr < vr) cones += vh * MathF.Pow(1f - rr / vr, 1.5f);
                    if (r < cr) cones -= cd * (1f - r * r / (cr * cr)); // the crater
                }
                int k = j * GW + i;
                raw[k] = BaseLevel + baseLevel + continent + seam + ridges + hills + cones;
                Uplift[k] = uplift + MathF.Max(0, cones);
                Volcanic[k] = volc;
            }
        });
        Blur(raw, 1);
        for (int k = 0; k < raw.Length; k++) Height[k] = Math.Clamp(raw[k], BaseLevel - 0.12f * h, 0.94f * h);
        MarkSea();
        // The sea floor stays a shelf (the player never sees the abyss; shallow seas cost little water).
        if (SeaLevel > 0)
            for (int k = 0; k < Height.Length; k++)
                if (Sea[k]) Height[k] = MathF.Max(Height[k], SeaLevel - 0.085f * h);
        progress?.Within(1f);
        progress?.Show(Picture(stage: 0), PictureW, PictureH);

        progress?.Step("Raising mountains", Volcanoes.Count > 0
            ? $"Ranges fold up where the plates collide; {Volcanoes.Count} volcano{(Volcanoes.Count == 1 ? "" : "es")} break through"
            : "Ranges fold up where the plates collide, rifts open where they part", 0.06f, 0.10f);
        progress?.Within(1f);
    }

    private readonly List<(float X, float Y, float R, float H, float CraterR, float CraterD)> _cones = new();

    /// <summary>
    /// Volcanoes: a lattice of candidate points (a third of a plate apart), each kept by chance where the plates make
    /// volcanoes (behind a subduction range, on an island arc) or, rarely, anywhere inside a plate (hotspots).
    /// </summary>
    private void PlaceVolcanoes()
    {
        float sp = 0.33f * PlateSpacing, w = _belt;
        int seed = _s.Seed;
        Span<int> idx = stackalloc int[3];
        Span<float> dist = stackalloc float[3];
        for (int j = -1; j * sp < _d + sp; j++)
        for (int i = -1; i * sp < _w + sp; i++)
        {
            float x = (i + 0.2f + 0.6f * Noise.Hash(i, j, 1, seed + 261)) * sp;
            float y = (j + 0.2f + 0.6f * Noise.Hash(i, j, 2, seed + 261)) * sp;
            if (x < -sp * 0.3f || y < -sp * 0.3f || x > _w + sp * 0.3f || y > _d + sp * 0.3f) continue;
            var (px, py) = Warp(x, y);
            Nearest(px, py, idx, dist);
            var a = _plates[idx[0]];
            var b = _plates[idx[1]];
            float lx = b.X - a.X, ly = b.Y - a.Y, len = MathF.Max(1e-3f, MathF.Sqrt(lx * lx + ly * ly));
            float s = (dist[1] * dist[1] - dist[0] * dist[0]) / (2f * len);
            float conv = ((a.Vx - b.Vx) * lx + (a.Vy - b.Vy) * ly) / len;
            float chance = 0.035f; // hotspot
            if (conv > 0.3f && a.Continental && !b.Continental && s > 0.55f * w && s < 1.15f * w) chance = 0.6f;
            else if (conv > 0.3f && !a.Continental && !b.Continental && s < 0.5f * w) chance = 0.45f;
            else if (s < 0.8f * w) chance = 0f;
            if (Noise.Hash(i, j, 3, seed + 261) >= chance) continue;
            float scale = MathF.Sqrt(PlateSpacing / 1024f);
            float r = (50f + 60f * Noise.Hash(i, j, 4, seed + 261)) * scale;
            float height = (0.10f + 0.12f * Noise.Hash(i, j, 5, seed + 261)) * _h * MathF.Sqrt(Relief01);
            float craterR = 0.17f * r, craterD = MathF.Min(0.05f * _h, 0.35f * height);
            _cones.Add((x, y, r, height, craterR, craterD));
            if (x >= 0 && y >= 0 && x < _w && y < _d) Volcanoes.Add((x, y, r));
        }
    }

    /// <summary>Marks the sea: nodes below sea level joined to the map edge (inland hollows stay land, for lakes).</summary>
    private void MarkSea()
    {
        Array.Clear(Sea);
        if (SeaLevel <= 0) return;
        var queue = new Queue<int>();
        for (int j = 0; j < GD; j++)
        for (int i = 0; i < GW; i++)
        {
            if (i != 0 && j != 0 && i != GW - 1 && j != GD - 1) continue;
            int k = j * GW + i;
            if (Height[k] < SeaLevel && !Sea[k]) { Sea[k] = true; queue.Enqueue(k); }
        }
        while (queue.TryDequeue(out int c))
        {
            int i = c % GW, j = c / GW;
            for (int d = 0; d < 4; d++)
            {
                int ni = i + (d == 0 ? 1 : d == 1 ? -1 : 0), nj = j + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if ((uint)ni >= (uint)GW || (uint)nj >= (uint)GD) continue;
                int m = nj * GW + ni;
                if (Sea[m] || Height[m] >= SeaLevel) continue;
                Sea[m] = true;
                queue.Enqueue(m);
            }
        }
    }

    // ---- Climate -----------------------------------------------------------------------------------------------

    /// <summary>Sea-level temperature of the world's middle row, by climate.</summary>
    private float MidTemperature => _s.Shape.Climate switch
    {
        Climate.Frozen => -10f, Climate.Cold => 2f, Climate.Warm => 19f, Climate.Tropical => 26f, _ => 12f,
    };

    /// <summary>How humid the incoming air is, by rainfall setting.</summary>
    private float BaseHumidity => _s.Shape.Rainfall switch
    {
        Rainfall.Arid => 0.2f, Rainfall.Dry => 0.4f, Rainfall.Wet => 1.05f, _ => 0.66f,
    };

    private void BuildClimate(WorldGenProgress? progress)
    {
        progress?.Step("Blowing winds", $"A prevailing wind from the {Compass(-WindX, -WindY)} carries moisture inland", 0.10f, 0.14f);
        int seed = _s.Seed;
        float span = MathF.Min(4f + Math.Max(_w, _d) / 256f, 16f), mid = MidTemperature;
        float p = PlateSpacing;
        Parallel.For(0, GD, j =>
        {
            for (int i = 0; i < GW; i++)
            {
                float tx = i * Cell, ty = j * Cell;
                float lat = ty / Math.Max(1, _d) - 0.5f; // north (top) is colder
                Temperature[j * GW + i] = mid + span * lat + (Noise.Fbm2D(tx / (0.6f * p), ty / (0.6f * p), seed + 271, 3) - 0.5f) * 6f;
            }
        });

        // Moisture rides the wind: nodes in order along it, each fed from a point 1.5 nodes upwind (already done).
        int n = GW * GD;
        var keys = new long[n];
        var order = new int[n];
        for (int k = 0; k < n; k++)
        {
            float proj = (k % GW) * WindX + (k / GW) * WindY;
            keys[k] = (long)MathF.Round(proj * 4096f) * n + k;
            order[k] = k;
        }
        Array.Sort(keys, order);
        var m = new float[n];
        float m0 = BaseHumidity;
        float seaSat = 0.6f + 0.5f * m0;
        const float step = 1.5f;
        for (int o = 0; o < n; o++)
        {
            int k = order[o], i = k % GW, j = k / GW;
            // Regions differ: some lie in the path of wet air, some are dry whatever the mountains do.
            float target = m0 * (0.45f + 1.1f * Smoothstep(0.28f, 0.72f, Noise.Fbm2D(i * Cell / (0.7f * p), j * Cell / (0.7f * p), seed + 281, 3)));
            float ux = i - WindX * step, uy = j - WindY * step;
            float inflow, upHeight;
            if (ux < 0 || uy < 0 || ux > GW - 1 || uy > GD - 1)
            {
                inflow = target;
                upHeight = Height[k];
            }
            else
            {
                inflow = Bilinear(m, ux, uy);
                upHeight = Bilinear(Height, ux, uy);
            }
            if (Sea[k])
            {
                m[k] = inflow + (seaSat - inflow) * 0.08f;
                Rain[k] = m[k];
                continue;
            }
            // Air climbing a slope cools and rains out (more the steeper it climbs); over the land it loses a little
            // and gains a little back (rain recycled by plants), settling at the region's humidity: a long range leaves
            // dry air behind it for a few hundred tiles (the rain shadow), a few hills don't.
            float slope = (Height[k] - upHeight) / (step * Cell); // levels per tile climbed along the wind
            float lift = Math.Clamp(slope / 0.6f, 0f, 2f);
            float f = 0.025f + 0.06f * lift;
            m[k] = MathF.Max(0f, inflow * (1f - f) + 0.025f * target);
            Rain[k] = MathF.Min(inflow * (1f + 2f * lift), 2.6f);
        }
        Blur(Rain, 2);
        progress?.Within(1f);
        progress?.Show(Picture(stage: 1), PictureW, PictureH);
    }

    private static string Compass(float x, float y)
    {
        // y grows southwards (north is the top of the map).
        int octant = (int)MathF.Round((MathF.Atan2(-y, x) / (MathF.PI * 2f) + 1f) * 8f) % 8;
        return octant switch { 0 => "east", 1 => "northeast", 2 => "north", 3 => "northwest", 4 => "west", 5 => "southwest", 6 => "south", _ => "southeast" };
    }

    // ---- Erosion -----------------------------------------------------------------------------------------------

    /// <summary>Erosion rounds.</summary>
    public const int ErosionRounds = 40;

    private void Erode(WorldGenProgress? progress)
    {
        progress?.Step("Eroding valleys", "Rain gathers into streams and wears the mountains down", 0.14f, 0.30f);
        int n = GW * GD;
        var start = (float[])Height.Clone();
        var area = new float[n];
        var next = new float[n];
        float talus = 1.25f * Cell, k = 0.0045f;
        for (int round = 0; round < ErosionRounds; round++)
        {
            if (round % 8 == 0) MarkSea();
            var (filled, receiver, order) = Flood();
            // Drainage area weighted by rain: downstream nodes gather what falls upstream.
            for (int c = 0; c < n; c++) area[c] = Sea[c] ? 0f : MathF.Max(0.05f, Rain[c]);
            for (int o = n - 1; o >= 0; o--)
            {
                int c = order[o];
                if (receiver[c] >= 0) area[receiver[c]] += area[c];
            }
            // Stream power, implicit (receivers first): each node falls towards its receiver by K·sqrt(A)/distance.
            for (int o = 0; o < n; o++)
            {
                int c = order[o], r = receiver[c];
                if (r < 0 || Sea[c]) continue;
                if (Height[c] < filled[c] - 0.01f) continue; // a basin: it keeps its hollow (a lake later)
                int dx = c % GW - r % GW, dy = c / GW - r / GW;
                float f = k * MathF.Sqrt(area[c]) / (dx != 0 && dy != 0 ? 1.4142f : 1f);
                float lowered = (Height[c] + f * Height[r]) / (1f + f);
                if (lowered < Height[c]) Height[c] = MathF.Max(Height[r], lowered);
            }
            // Slopes past the angle of repose slump, and soil creeps (mass-conserving, both from the same state).
            Parallel.For(0, GD, j =>
            {
                for (int i = 0; i < GW; i++)
                {
                    int c = j * GW + i;
                    float hc = Height[c], delta = 0, lap = 0;
                    int count = 0;
                    for (int d = 0; d < 4; d++)
                    {
                        int ni = i + (d == 0 ? 1 : d == 1 ? -1 : 0), nj = j + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if ((uint)ni >= (uint)GW || (uint)nj >= (uint)GD) continue;
                        float hn = Height[nj * GW + ni];
                        delta += 0.2f * (MathF.Max(0f, hn - hc - talus) - MathF.Max(0f, hc - hn - talus));
                        lap += hn - hc;
                        count++;
                    }
                    next[c] = Sea[c] ? hc : hc + delta + 0.02f * lap;
                }
            });
            Array.Copy(next, Height, n);
            progress?.Within((round + 1f) / ErosionRounds);
            if (round == ErosionRounds / 2) progress?.Show(Picture(stage: 2), PictureW, PictureH);
        }
        MarkSea();
        for (int c = 0; c < n; c++) Eroded[c] = MathF.Max(0f, start[c] - Height[c]);
        progress?.Show(Picture(stage: 2), PictureW, PictureH);
    }

    /// <summary>Priority flood from the sea and the map edge: filled heights, receivers (-1 at outlets), order (outlets first).</summary>
    private (float[] Filled, int[] Receiver, int[] Order) Flood()
    {
        int n = GW * GD;
        var filled = new float[n];
        var receiver = new int[n];
        var order = new int[n];
        var done = new bool[n];
        var open = new PriorityQueue<int, (float, int)>();
        for (int c = 0; c < n; c++)
        {
            int i = c % GW, j = c / GW;
            if (!Sea[c] && i != 0 && j != 0 && i != GW - 1 && j != GD - 1) continue;
            filled[c] = Sea[c] ? MathF.Max(Height[c], SeaLevel) : Height[c];
            receiver[c] = -1;
            done[c] = true;
            open.Enqueue(c, (filled[c], c));
        }
        int count = 0;
        while (open.TryDequeue(out int c, out _))
        {
            order[count++] = c;
            int i = c % GW, j = c / GW;
            for (int d = 0; d < 8; d++)
            {
                int ni = i + (d is 0 or 4 or 5 ? 1 : d is 1 or 6 or 7 ? -1 : 0);
                int nj = j + (d is 2 or 4 or 6 ? 1 : d is 3 or 5 or 7 ? -1 : 0);
                if ((uint)ni >= (uint)GW || (uint)nj >= (uint)GD) continue;
                int m = nj * GW + ni;
                if (done[m]) continue;
                done[m] = true;
                filled[m] = MathF.Max(Height[m], filled[c] + 1e-3f);
                receiver[m] = c;
                open.Enqueue(m, (filled[m], m));
            }
        }
        return (filled, receiver, order);
    }

    private void Finish()
    {
        float h = _h;
        for (int j = 0; j < GD; j++)
        for (int i = 0; i < GW; i++)
        {
            int c = j * GW + i;
            float slope = 0;
            if (i + 1 < GW) slope = MathF.Max(slope, MathF.Abs(Height[c + 1] - Height[c]));
            if (i > 0) slope = MathF.Max(slope, MathF.Abs(Height[c - 1] - Height[c]));
            if (j + 1 < GD) slope = MathF.Max(slope, MathF.Abs(Height[c + GW] - Height[c]));
            if (j > 0) slope = MathF.Max(slope, MathF.Abs(Height[c - GW] - Height[c]));
            slope /= Cell;
            Rugged[c] = Math.Clamp(Uplift[c] / (0.22f * h) + Volcanic[c] * 0.4f + slope / 1.4f, 0f, 1f);
        }
        Blur(Rugged, 1);
    }

    // ---- Sampling ----------------------------------------------------------------------------------------------

    /// <summary>A field at tile (x, y), interpolated linearly between nodes.</summary>
    public float At(float[] field, float x, float y) => Bilinear(field, x / Cell, y / Cell);

    /// <summary>Mean temperature (°C) of ground at level <paramref name="z"/> in column (x, y).</summary>
    public float TemperatureAt(int x, int y, int z) =>
        At(Temperature, x, y) - Lapse * MathF.Max(0, z - BaseLevel) / _h;

    /// <summary>Rain index at tile (x, y).</summary>
    public float RainAt(int x, int y) => At(Rain, x, y);

    /// <summary>
    /// How dry and hot a place is for desert landforms: 0 (none) .. 1 (sand sea), from rain and temperature.
    /// </summary>
    public float Desert(float rain, float temperature) =>
        Math.Clamp((0.3f - rain) / 0.15f, 0f, 1f) * Math.Clamp((temperature - 8f) / 8f, 0f, 1f);

    /// <summary>
    /// The fine surface height (levels, fractional) of tile (x, y): the eroded landform interpolated smoothly (Catmull-Rom)
    /// plus detail as rugged as the ground, dunes in sandy deserts, and stepped mesas in arid broken uplands.
    /// </summary>
    public float FineHeight(int x, int y)
    {
        int seed = _s.Seed;
        float gx = (float)x / Cell, gy = (float)y / Cell;
        float h = Bicubic(Height, gx, gy);
        float rug = Bilinear(Rugged, gx, gy);
        float detail = (Noise.Fbm2D(x / 56f, y / 56f, seed + 291, 3) - 0.5f) * (3f + 16f * rug)
                       + (Noise.Fbm2D(x / 13f, y / 13f, seed + 292, 2) - 0.5f) * (1f + 3f * rug);
        if (SeaLevel > 0 && h < SeaLevel + 3) detail *= Math.Clamp((h - SeaLevel + 6f) / 9f, 0.15f, 1f); // calm shores
        h += detail;

        float rain = Bilinear(Rain, gx, gy), temp = Bilinear(Temperature, gx, gy) - Lapse * MathF.Max(0, h - BaseLevel) / _h;
        float desert = Desert(rain, temp);
        if (desert > 0 && (SeaLevel <= 0 || h > SeaLevel + 2))
        {
            // Mesas: in broken arid ground the strata weather into flat-topped steps.
            float broken = Smoothstep(0.18f, 0.5f, rug) * desert;
            if (broken > 0)
            {
                const float tread = 7f;
                float f = h / tread, band = MathF.Floor(f);
                float stepped = (band + Smoothstep(0.6f, 1f, f - band)) * tread;
                h += (stepped - h) * broken * 0.9f;
            }
            // Dunes: crests across the wind, in open sandy ground.
            float sand = desert * (1f - Smoothstep(0.15f, 0.4f, rug));
            if (sand > 0)
            {
                float u = (x * WindX + y * WindY) / 34f + Noise.Fbm2D(x / 90f, y / 90f, seed + 293, 2) * 3f;
                float crest = 1f - MathF.Abs(2f * (u - MathF.Floor(u)) - 1f);
                h += MathF.Pow(crest, 1.6f) * 5f * sand * Noise.Fbm2D(x / 140f, y / 140f, seed + 294, 2) * 1.6f;
            }
        }
        return h;
    }

    private float Bilinear(float[] f, float gx, float gy)
    {
        gx = Math.Clamp(gx, 0f, GW - 1.001f);
        gy = Math.Clamp(gy, 0f, GD - 1.001f);
        int i = (int)gx, j = (int)gy;
        float fx = gx - i, fy = gy - j;
        int c = j * GW + i;
        float a = f[c] + (f[c + 1] - f[c]) * fx;
        float b = f[c + GW] + (f[c + GW + 1] - f[c + GW]) * fx;
        return a + (b - a) * fy;
    }

    private float Bicubic(float[] f, float gx, float gy)
    {
        gx = Math.Clamp(gx, 0f, GW - 1.001f);
        gy = Math.Clamp(gy, 0f, GD - 1.001f);
        int i = (int)gx, j = (int)gy;
        float fx = gx - i, fy = gy - j;
        Span<float> rows = stackalloc float[4];
        for (int r = 0; r < 4; r++)
        {
            int jj = Math.Clamp(j - 1 + r, 0, GD - 1) * GW;
            float p0 = f[jj + Math.Clamp(i - 1, 0, GW - 1)], p1 = f[jj + i], p2 = f[jj + Math.Min(i + 1, GW - 1)], p3 = f[jj + Math.Min(i + 2, GW - 1)];
            rows[r] = CatmullRom(p0, p1, p2, p3, fx);
        }
        return CatmullRom(rows[0], rows[1], rows[2], rows[3], fy);
    }

    private static float CatmullRom(float p0, float p1, float p2, float p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
    }

    private void Blur(float[] f, int passes)
    {
        var tmp = new float[f.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int j = 0; j < GD; j++)
            for (int i = 0; i < GW; i++)
            {
                int c = j * GW + i;
                tmp[c] = (2f * f[c] + f[j * GW + Math.Max(0, i - 1)] + f[j * GW + Math.Min(GW - 1, i + 1)]) * 0.25f;
            }
            for (int j = 0; j < GD; j++)
            for (int i = 0; i < GW; i++)
            {
                int c = j * GW + i;
                f[c] = (2f * tmp[c] + tmp[Math.Max(0, j - 1) * GW + i] + tmp[Math.Min(GD - 1, j + 1) * GW + i]) * 0.25f;
            }
        }
    }

    private static float Smoothstep(float a, float b, float v)
    {
        float t = Math.Clamp((v - a) / (b - a), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    // ---- Pictures ----------------------------------------------------------------------------------------------

    /// <summary>Width and height of <see cref="Picture"/> images (the grid, cropped to the map).</summary>
    public int PictureW => Math.Max(1, (_w + Cell - 1) / Cell);
    public int PictureH => Math.Max(1, (_d + Cell - 1) / Cell);

    /// <summary>
    /// A small shaded map of the landform for the loading screen (RGB, a pixel a node): 0 the plates' relief, 1 with the
    /// rain over it, 2 after erosion.
    /// </summary>
    public byte[] Picture(int stage)
    {
        int pw = PictureW, ph = PictureH;
        var rgb = new byte[pw * ph * 3];
        for (int j = 0; j < ph; j++)
        for (int i = 0; i < pw; i++)
        {
            int c = j * GW + i;
            float h = Height[c];
            // Light from the northwest.
            float hx = Height[j * GW + Math.Max(0, i - 1)] - Height[j * GW + Math.Min(GW - 1, i + 1)];
            float hy = Height[Math.Max(0, j - 1) * GW + i] - Height[Math.Min(GD - 1, j + 1) * GW + i];
            float shade = Math.Clamp(1f + (hx + hy) / (Cell * 2.5f), 0.55f, 1.35f);
            float r, g, b;
            if (Sea[c])
            {
                float depth = Math.Clamp((SeaLevel - h) / (0.085f * _h), 0f, 1f);
                (r, g, b) = (0.18f - 0.08f * depth, 0.38f - 0.12f * depth, 0.58f - 0.08f * depth);
                shade = 1f;
            }
            else
            {
                float t = Math.Clamp((h - BaseLevel) / (0.45f * _h), 0f, 1f);
                (r, g, b) = Ramp(t);
                if (stage == 1)
                {
                    float wet = Math.Clamp(Rain[c] / 1.6f, 0f, 1f);
                    (r, g, b) = (r * (1.25f - 0.5f * wet), g * (0.95f + 0.2f * wet), b * (0.8f + 0.5f * wet));
                }
                if (Volcanic[c] > 0.6f) (r, g, b) = (r * 0.7f + 0.12f, g * 0.6f, b * 0.6f);
            }
            int o = (j * pw + i) * 3;
            rgb[o] = (byte)Math.Clamp(r * shade * 255f, 0f, 255f);
            rgb[o + 1] = (byte)Math.Clamp(g * shade * 255f, 0f, 255f);
            rgb[o + 2] = (byte)Math.Clamp(b * shade * 255f, 0f, 255f);
        }
        return rgb;
    }

    /// <summary>Height colours: lowland green, upland tan, mountain grey, snowy white.</summary>
    private static (float, float, float) Ramp(float t)
    {
        if (t < 0.25f) return Mix((0.36f, 0.55f, 0.27f), (0.55f, 0.6f, 0.33f), t / 0.25f);
        if (t < 0.55f) return Mix((0.55f, 0.6f, 0.33f), (0.58f, 0.48f, 0.34f), (t - 0.25f) / 0.3f);
        if (t < 0.8f) return Mix((0.58f, 0.48f, 0.34f), (0.55f, 0.53f, 0.52f), (t - 0.55f) / 0.25f);
        return Mix((0.55f, 0.53f, 0.52f), (0.93f, 0.94f, 0.96f), (t - 0.8f) / 0.2f);
    }

    private static (float, float, float) Mix((float, float, float) a, (float, float, float) b, float t) =>
        (a.Item1 + (b.Item1 - a.Item1) * t, a.Item2 + (b.Item2 - a.Item2) * t, a.Item3 + (b.Item3 - a.Item3) * t);
}
