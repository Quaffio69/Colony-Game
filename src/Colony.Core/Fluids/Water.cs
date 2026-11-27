using Colony.Core.World;

namespace Colony.Core.Fluids;

/// <summary>
/// Water as shallow-water flow over the terrain, column by column (the approach of city builders' water), extended
/// to stacked open spaces so caves and tunnels hold water too.
/// <para>
/// Every map column is a list of <em>spans</em>: vertical runs of open cells resting on solid ground (the open
/// sky above the surface, a tunnel, a cave). A span holds a continuous depth of water sitting on its floor, so a
/// water surface is a smooth height, not whole cells. Water moves sideways between spans of neighbouring columns
/// whose open ranges overlap, through "virtual pipes" driven by the difference in water surface height
/// (clipped to the shared window, so water pours over a lip and down into a lower neighbour: waterfalls, filling
/// a pit, flooding a tunnel). Each pipe's flow keeps most of last step's (momentum, so rivers carry real volume)
/// and is scaled down if a span would give more than it holds. A pipe's pull grows with the depth of water at
/// the window, so deep channels carry more than thin films. Water in flooded regions (spans full to their ceilings)
/// is under pressure and moves as one pipe between its open ends (see <see cref="Pressurize"/>).
/// </para>
/// <para>
/// Steps are two passes over 16x16-column blocks (chunk columns): every span's outflows from the previous state,
/// then every span's new volume from its outflows and its neighbours' outflows towards it. Each pass writes only
/// its own block, so blocks run in parallel and the result is deterministic. Only awake blocks step: a block
/// sleeps once its water has been still for a while (still lakes cost nothing) and wakes when water flows in, its
/// terrain changes, or something adds water. Water reaching a map-border column leaves the world; springs keep
/// adding until their span's surface reaches their level.
/// </para>
/// Public amounts are in thousandths of a cell (<see cref="Full"/> = one full cell), as callers think in cells.
/// </summary>
public sealed class Water
{
    /// <summary>One full cell, in the units of every public amount.</summary>
    public const int Full = 1000;
    /// <summary>A cell holding less than this counts as dry.</summary>
    public const int MinMass = 4;
    /// <summary>A placed spring keeps its span's surface up to this many levels above its cell's floor...</summary>
    public const int SpringHead = 1;
    /// <summary>...adding at most this much per step.</summary>
    public const int SpringRate = 100;
    /// <summary>How deep generated rivers start (fraction of their channel); the settle finds their flowing depth.</summary>
    public const float RiverFill = 0.8f;

    /// <summary>One level of water in one column, internally (fixed point, so steps are exact and deterministic).</summary>
    internal const int U = 1 << 16;
    /// <summary>Less than this in a span that nothing flows into dries up.</summary>
    private const int Dry = U / 256;
    // A pipe's flow keeps FrictionKeep/FrictionDiv of last step's and gains 1/Accel of the head difference (times
    // the depth at the window, up to a level). Accel must stay above 4 (sides): at 4 a span can give its whole
    // excess away in one step and alternate columns ping-pong.
    private const int FrictionKeep = 31, FrictionDiv = 32, Accel = 5;
    /// <summary>Flows smaller than this stop (still water settles exactly and sleeps).</summary>
    private const int MinFlow = U / 4096;
    /// <summary>A block whose spans change by less than this per step, with nothing flowing, is still...</summary>
    private const int StillDelta = U / 2048;
    /// <summary>...and no span sends more than this a step (a lake a river crosses barely moves; it sleeps and wakes as water arrives).</summary>
    private const int SleepFlow = U / 256;
    /// <summary>...and sleeps after this many still steps.</summary>
    private const int QuietSteps = 20;
    /// <summary>Neighbouring spans a span keeps a pipe to per side (a cliff face beside a tunnel and the sky).</summary>
    private const int Links = 2;
    private const int Slots = 4 * Links;

    private sealed class Block
    {
        public readonly int Index, X0, Y0;
        public readonly bool Border;
        public int[] Start = new int[Chunk.Size * Chunk.Size + 1]; // spans of local column c: Start[c]..Start[c+1]
        public short[] Floor = [], Ceil = [];
        public int[] Vol = [];
        public int[]? Out;              // per span and slot (side * Links + k): outflow this step
        public int[] Link = [];         // per span and slot: the span the pipe leads to (see BuildLinks), or -1
        public ushort[] Sill = [];      // per span and slot: the level water must reach to pass (thin walls on the edge; see SillOf)
        public byte[] Back = [];        // per span and slot: which of that span's pipes leads back
        public bool LinksValid;
        public bool[] Confined = [];    // per span: part of a flooded region this step (see Pressurize)
        public int[] Press = [];        // per span: that region's head (units), while Confined
        public bool HasFull;            // some span is full to its ceiling (a flooded region may start here)
        public int[] FlowX = [], FlowY = []; // per span: net flow last step (for drawing, wheels)
        public int[] NewVol = [];
        public int[] Shown = [];        // per span: the volume views last drew (see Apply)
        public bool Awake, Prepared, NeedsSpans = true, Changed, Received;
        public int Quiet, Version, Wet;
        public long Drained, Evaporated;
        public int Visited;

        public Block(int index, int x0, int y0, bool border)
        {
            Index = index;
            (X0, Y0) = (x0, y0);
            Border = border;
        }

        public int Spans => Floor.Length;
    }

    private readonly VoxelWorld _world;
    private readonly Block?[] _blocks;
    private readonly int _bw, _bd;
    private readonly SortedSet<int> _wet = new();
    private readonly HashSet<int> _dirty = new();
    private readonly SortedSet<int> _fullBlocks = new(); // blocks with a span full to its ceiling (see Pressurize)
    private readonly List<(Block B, int S)> _confined = new();
    private List<Block> _awake = new();
    private readonly List<Block> _prepared = new();
    private readonly Dictionary<TilePos, (int Rate, int Limit)> _springs = new();
    private long _drained, _sourced, _evaporated;
    /// <summary>Water that seeped away so far (thousandths of a cell), see <see cref="Seepage"/>.</summary>
    public long Evaporated => _evaporated * Full / U;
    /// <summary>
    /// Water standing above its column's natural level (<see cref="FillFromTerrain"/>: just under a lake's shore or a
    /// river's bank, or the ground on land) loses this much a step, about a level per six minutes of game time.
    /// Rivers feed lakes faster than some outlets can pass; without this a lake would rise forever and flood the land
    /// around it. It's nothing to a fed reservoir behind a dam, and water in a dug-out pit (below the old ground) is
    /// never touched.
    /// </summary>
    private const int Seepage = U / 2048;
    private int[]? _natural; // per column: the natural water surface (units), see FillFromTerrain
    /// <summary>Water springs have added so far (thousandths of a cell): with <see cref="Total"/> and <see cref="Drained"/>, the books balance.</summary>
    public long Sourced => _sourced * Full / U;

    public Water(VoxelWorld world)
    {
        _world = world;
        _bw = world.ChunksX;
        _bd = world.ChunksY;
        _blocks = new Block?[_bw * _bd];
        world.ChunkChanged += ci =>
        {
            Join(); // a step in flight finishes on the old shape (as if the change came just after it)
            int col = ci % (_bw * _bd);
            if (_blocks[col] is not null) _dirty.Add(col);
        };
    }

    /// <summary>Increments whenever any water surface visibly changes (for views).</summary>
    public int Version { get; private set; }
    public long Steps { get; private set; }
    public string Timings { get; private set; } = "";

    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static double Sw() => Clock.Elapsed.TotalMilliseconds;
    /// <summary>Spans stepped by the last step (diagnostics).</summary>
    public int LastStepCells { get; private set; }
    public int AwakeChunks => _awake.Count;
    public int WetChunks => _wet.Count;
    /// <summary>Water that left the world over its border so far (thousandths of a cell).</summary>
    public long Drained => _drained * Full / U;
    public IReadOnlyCollection<TilePos> Springs => _springs.Keys;

    // ---- Queries --------------------------------------------------------------------------------------

    /// <summary>How full a cell is (0..<see cref="Full"/>): the part of it below its span's water surface.</summary>
    public int MassAt(int x, int y, int z)
    {
        if (!Find(x, y, z, out var b, out int s)) return 0;
        long surface = (long)b.Floor[s] * U + b.Vol[s];
        return (int)(Math.Clamp(surface - (long)z * U, 0, U) * Full / U);
    }

    public int MassAt(TilePos p) => MassAt(p.X, p.Y, p.Z);
    public bool IsWet(TilePos p) => MassAt(p) >= MinMass;
    /// <summary>A cell's water in sevenths (DF-style 1..7, 0 = dry): any wet cell (<see cref="MinMass"/>+) reads at least 1, so a
    /// thin film (a sheet spreading over flat ground towards the map's draining border) doesn't read as "0/7".</summary>
    public int Level(TilePos p) => MassAt(p) is var m and >= MinMass ? Math.Clamp((m * 7 + Full / 2) / Full, 1, 7) : 0;

    /// <summary>Height of the water surface (in levels) in the open space containing the cell, or null if it's dry.</summary>
    public float? SurfaceAt(int x, int y, int z)
    {
        if (!Find(x, y, z, out var b, out int s) || b.Vol[s] < Dry) return null;
        return b.Floor[s] + Math.Min(b.Vol[s], (b.Ceil[s] - b.Floor[s]) * U) / (float)U;
    }

    /// <summary>
    /// How hard the water in a cell's space is pressed, in levels of head above its ceiling: 0 for open water or a
    /// space not yet full; a flooded tunnel under a lake ten levels above its roof is pressed by about ten.
    /// </summary>
    public float Pressure(TilePos p)
    {
        if (!Find(p.X, p.Y, p.Z, out var b, out int s)) return 0;
        if (!b.Confined[s]) return 0;
        long over = b.Press[s] - (long)b.Ceil[s] * U;
        return over > 0 ? over / (float)U : 0f;
    }

    /// <summary>Open spans of a column (floor, ceiling, water surface or null), lowest first, for drawing.</summary>
    public int SpanCount(int x, int y) =>
        _blocks[(y >> Chunk.Shift) * _bw + (x >> Chunk.Shift)] is { NeedsSpans: false } b ? b.Start[Col(x, y) + 1] - b.Start[Col(x, y)] : 0;

    public (int Floor, int Ceil, float? Surface) Span(int x, int y, int k)
    {
        var b = _blocks[(y >> Chunk.Shift) * _bw + (x >> Chunk.Shift)]!;
        int s = b.Start[Col(x, y)] + k;
        return (b.Floor[s], b.Ceil[s], b.Vol[s] < Dry ? null : b.Floor[s] + Math.Min(b.Vol[s], (b.Ceil[s] - b.Floor[s]) * U) / (float)U);
    }

    /// <summary>
    /// Water pouring over a drop, for drawing: every pipe in the box (columns x0..x1, y0..y1) sending water last step to a
    /// neighbouring span whose surface (or floor, if dry) lies at least <paramref name="minDrop"/> levels below the
    /// sender's surface. The simulation moves that water between the two columns directly, so without this nothing shows
    /// in the air between the lip and where it lands. Read-only; may be read while an async step runs (a frame's tear).
    /// </summary>
    public void Falls(int x0, int y0, int x1, int y1, List<Fall> into, float minDrop = 0.75f, int max = 2000)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(_world.Width - 1, x1); y1 = Math.Min(_world.Depth - 1, y1);
        int minFlow = SleepFlow; // less trickles (and a sleeping block's last flows) show nothing
        for (int by = y0 >> Chunk.Shift; by <= y1 >> Chunk.Shift; by++)
        for (int bx = x0 >> Chunk.Shift; bx <= x1 >> Chunk.Shift; bx++)
        {
            var b = _blocks[by * _bw + bx];
            if (b is null || !b.Awake || b.NeedsSpans || !b.LinksValid || b.Out is not { } o || b.Wet == 0) continue;
            for (int c = 0; c < Chunk.Size * Chunk.Size; c++)
            {
                int x = b.X0 + (c & Chunk.Mask), y = b.Y0 + (c >> Chunk.Shift);
                if (x < x0 || x > x1 || y < y0 || y > y1) continue;
                for (int s = b.Start[c]; s < b.Start[c + 1]; s++)
                {
                    if (b.Vol[s] < Dry) continue;
                    float top = b.Floor[s] + Math.Min(b.Vol[s], (b.Ceil[s] - b.Floor[s]) * U) / (float)U;
                    for (int slot = 0; slot < Slots; slot++)
                    {
                        int f = o[s * Slots + slot], l = b.Link[s * Slots + slot];
                        if (f < minFlow || l < 0) continue;
                        var nb = _blocks[l >> SpanBits]!;
                        int t = l & SpanMask;
                        if (t >= nb.Floor.Length) continue;
                        float bottom = nb.Vol[t] < Dry ? nb.Floor[t] : nb.Floor[t] + Math.Min(nb.Vol[t], (nb.Ceil[t] - nb.Floor[t]) * U) / (float)U;
                        if (top - bottom < minDrop) continue;
                        into.Add(new Fall(x, y, slot / Links, top, bottom, f / (float)U));
                        if (into.Count >= max) return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Which way and how fast the water in a cell's span moves, for drawing (scaled to about 0..1.5). (0, 0) for still
    /// or no water. Wheels use <see cref="ThroughFlow"/>.
    /// </summary>
    public (float X, float Y) FlowAt(int x, int y, int z)
    {
        if (!Find(x, y, z, out var b, out int s) || b.Vol[s] < Dry) return (0, 0);
        float depth = Math.Max(b.Vol[s], U / 4) / (float)U;
        // Exaggerated for the eye: a real current moves a few hundredths of a tile a step.
        const float visual = 10f;
        return (Math.Clamp(b.FlowX[s] * visual / U / depth, -1.5f, 1.5f), Math.Clamp(b.FlowY[s] * visual / U / depth, -1.5f, 1.5f));
    }

    /// <summary>Water passing through a cell's span per step (thousandths of a cell), whichever way: what turns a wheel.</summary>
    public int ThroughFlow(TilePos p)
    {
        if (!Find(p.X, p.Y, p.Z, out var b, out int s)) return 0;
        return (int)(Math.Sqrt((double)b.FlowX[s] * b.FlowX[s] + (double)b.FlowY[s] * b.FlowY[s]) * Full / U);
    }

    /// <summary>All water in the world (thousandths of a cell).</summary>
    public long Total()
    {
        long sum = 0;
        foreach (var b in _blocks)
            if (b is not null)
                foreach (int v in b.Vol) sum += v;
        return sum * Full / U;
    }

    /// <summary>Blocks (chunk columns, <c>cy * ChunksX + cx</c>) holding water, for views.</summary>
    public IEnumerable<int> WetBlockIndices() => _wet;

    /// <summary>Whether a block (a 16x16 chunk column, index by * ChunksX + bx) holds any water (views ask about the blocks near the camera only).</summary>
    public bool IsWetBlock(int block) => _wet.Contains(block);

    /// <summary>Increments whenever a block joins or leaves the wet set (views cache which nearby blocks are wet). Not sim state.</summary>
    public int WetSetVersion { get; private set; }
    /// <summary>Bumped when a block's water surfaces visibly change (or its spans were rebuilt).</summary>
    public int BlockVersion(int block) => _blocks[block]?.Version ?? 0;

    // ---- Changes --------------------------------------------------------------------------------------

    /// <summary>Adds water to the open space containing a cell (negative removes). Returns the amount actually changed.</summary>
    public int Add(TilePos p, int amount)
    {
        Join();
        if (!_world.InBounds(p)) return 0;
        var b = Ensure(p.X >> Chunk.Shift, p.Y >> Chunk.Shift);
        if (!FindIn(b, p.X, p.Y, p.Z, out int s)) return 0;
        long cap = (long)(b.Ceil[s] - b.Floor[s]) * U;
        long now = b.Vol[s];
        long next = Math.Clamp(now + (long)amount * U / Full, 0, cap);
        b.Vol[s] = (int)next;
        Touch(b, visible: true);
        return (int)((next - now) * Full / U);
    }

    /// <summary>
    /// A pump: moves up to <paramref name="amount"/> from the water in <paramref name="from"/>'s space to
    /// <paramref name="to"/>'s, filling that only up to the top of the <paramref name="to"/> cell. Returns what moved.
    /// </summary>
    public int Move(TilePos from, TilePos to, int amount)
    {
        Join();
        if (amount <= 0 || !_world.InBounds(from) || !_world.InBounds(to)) return 0;
        var fb = Ensure(from.X >> Chunk.Shift, from.Y >> Chunk.Shift);
        var tb = Ensure(to.X >> Chunk.Shift, to.Y >> Chunk.Shift);
        if (!FindIn(fb, from.X, from.Y, from.Z, out int fs) || !FindIn(tb, to.X, to.Y, to.Z, out int ts)) return 0;
        long room = (long)(to.Z + 1) * U - ((long)tb.Floor[ts] * U + tb.Vol[ts]);
        long n = Math.Min(Math.Min((long)amount * U / Full, fb.Vol[fs]), room);
        if (n <= 0) return 0;
        fb.Vol[fs] -= (int)n;
        tb.Vol[ts] += (int)n;
        Touch(fb, visible: true);
        Touch(tb, visible: true);
        return (int)(n * Full / U);
    }

    /// <summary>
    /// A spring in a cell: adds up to <paramref name="rate"/> a step while its space's water surface is below
    /// <paramref name="head"/> levels above the cell's top (or, given <paramref name="mass"/>, below that much of
    /// the cell itself).
    /// </summary>
    public void AddSpring(TilePos p, int rate = SpringRate, int head = SpringHead, int mass = 0)
    {
        Join();
        if (!_world.InBounds(p) || _springs.ContainsKey(p)) return;
        long limit = mass > 0 ? (long)p.Z * U + (long)mass * U / Full : (long)(p.Z + 1 + head) * U;
        _springs[p] = ((int)((long)Math.Max(1, rate) * U / Full), (int)Math.Min(int.MaxValue, limit));
        Touch(Ensure(p.X >> Chunk.Shift, p.Y >> Chunk.Shift));
    }

    public void RemoveSpring(TilePos p)
    {
        Join();
        _springs.Remove(p);
    }

    /// <summary>
    /// Fills generated rivers and lakes (<see cref="Procedural.Terrain.Water"/>: rivers <see cref="RiverFill"/> of
    /// their channel, lakes to the brim) and puts a spring at each place the terrain says a river gains water
    /// (<see cref="Procedural.Terrain.RiverSources"/>), discharging in proportion to that gain and never raising
    /// its river above the bank. Then <see cref="Settle"/> lets the rivers find their flowing depth.
    /// </summary>
    /// <summary>Natural water surfaces per column (see <see cref="Seepage"/>), from the terrain.</summary>
    private void SetNatural(Procedural.Terrain t)
    {
        int w = t.Width, d = t.Depth;
        _natural = new int[w * d];
        for (int i = 0; i < w * d; i++)
        {
            int lvl = t.Surface[i] + t.Water[i];
            _natural[i] = t.Kind[i] switch
            {
                Procedural.SurfaceKind.LakeBed => (lvl + 1) * U - U / 10,
                Procedural.SurfaceKind.RiverBed => (lvl + 2) * U - U / 10,
                _ => (t.Surface[i] + 1) * U + U / 32,
            };
        }
    }

    public void FillFromTerrain(Procedural.Terrain t, int headRadius = 12, int sourceRate = 30)
    {
        int w = t.Width, d = t.Depth;
        // Natural water surfaces: just under a lake's shore or a river's bank (lakes sit at their sill, rivers a
        // level below the bank; see Terrain), or the ground on land. Water above them seeps away (Seepage).
        SetNatural(t);
        for (int y = 0; y < d; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x, depth = t.Water[i];
            if (depth == 0) continue;
            var b = Ensure(x >> Chunk.Shift, y >> Chunk.Shift);
            if (!FindIn(b, x, y, t.Surface[i] + 1, out int s)) continue;
            // Rivers part of their channel; lakes up to their sill (a level below the shore, see Terrain).
            float fill = t.Kind[i] == Procedural.SurfaceKind.RiverBed ? depth * RiverFill : depth - 0.9f;
            b.Vol[s] = Math.Min((int)(fill * U), (b.Ceil[s] - b.Floor[s]) * U);
            Touch(b, visible: true);
        }
        foreach (var (hx, hy, share) in t.RiverSources)
        {
            int best = -1, bestDist = int.MaxValue;
            for (int dy = -headRadius; dy <= headRadius; dy++)
            for (int dx = -headRadius; dx <= headRadius; dx++)
            {
                int x = hx + dx, y = hy + dy;
                if (x < 0 || y < 0 || x >= w || y >= d) continue;
                int i = y * w + x;
                if (t.Kind[i] != Procedural.SurfaceKind.RiverBed) continue;
                int dist = dx * dx + dy * dy;
                if (dist < bestDist) (best, bestDist) = (i, dist);
            }
            if (best < 0) continue;
            var cell = new TilePos(best % w, best / w, t.Surface[best] + 1);
            int rate = (int)Math.Clamp(sourceRate * share, 2, 4 * SpringRate);
            int brim = t.Water[best] * Full * 9 / 10; // never quite up to the bank
            if (_springs.TryGetValue(cell, out var have)) _springs[cell] = have with { Rate = have.Rate + (int)((long)rate * U / Full) };
            else AddSpring(cell, rate, mass: brim);
        }
    }

    /// <summary>Steps the water <paramref name="steps"/> times (generation: let rivers and lakes find their rest).</summary>
    public void Settle(int steps)
    {
        for (int i = 0; i < steps; i++) Step();
    }

    // ---- Stepping -------------------------------------------------------------------------------------

    /// <summary>Advances the water one step. Cost follows the awake blocks.</summary>
    public void Step() => BeginStep(async: false);

    /// <summary>A step started by <see cref="BeginStep"/> with async is still computing (see <see cref="Join"/>).</summary>
    public bool StepInFlight => _stepping != null;

    private Task? _stepping;
    private double _tr, _tp, _tq, _t0, _t1, _t2, _t3;
    private int _rebuilt;

    /// <summary>
    /// Starts a step. With <paramref name="async"/>, its heavy part (the pipes and volumes of every awake block)
    /// runs on worker threads while the caller goes on: until <see cref="Join"/>, readers see the water as it was
    /// before the step (consistent: the step writes only next volumes and flows), and anything that changes water
    /// (<see cref="Add"/>, <see cref="Move"/>, springs, the world) joins first. The result is exactly the same as
    /// a synchronous step.
    /// </summary>
    public void BeginStep(bool async)
    {
        Join();
        DevHooks.Stepping?.Invoke();
        Steps++;
        _tr = Sw();
        _rebuilt = _dirty.Count;
        if (_dirty.Count > 0)
        {
            foreach (int bi in _dirty)
            {
                var b = _blocks[bi]!;
                Rebuild(b);
                Touch(b, visible: true);
                for (int k = 0; k < 4; k++) if (Neighbour(b, k) is { } n) Touch(n);
            }
            _dirty.Clear();
        }
        _tp = Sw();
        if (_fullBlocks.Count > 0 || _confined.Count > 0) Pressurize();
        _tq = Sw();
        if (_awake.Count == 0)
        {
            LastStepCells = 0;
            AddSprings();
            return;
        }

        _t0 = Sw();
        _awake.Sort((a, b) => a.Index.CompareTo(b.Index));
        _prepared.Clear();
        foreach (var b in _awake) Prepare(b);
        foreach (var b in _awake)
        {
            int bx = b.Index % _bw, by = b.Index / _bw;
            if (bx + 1 < _bw) Prepare(Ensure(bx + 1, by));
            if (bx > 0) Prepare(Ensure(bx - 1, by));
            if (by + 1 < _bd) Prepare(Ensure(bx, by + 1));
            if (by > 0) Prepare(Ensure(bx, by - 1));
        }
        foreach (var b in _awake)
            if (b.Out is null || b.Out.Length < b.Spans * Slots) b.Out = new int[Math.Max(b.Spans, 1) * Slots];
        foreach (var b in _prepared)
            if (!b.LinksValid) BuildLinks(b);
        _t1 = Sw();
        if (async) _stepping = Task.Run(Compute);
        else
        {
            Compute();
            Finish();
        }
    }

    /// <summary>Waits for a step started with async (if any) and applies it. Cheap when none is running.</summary>
    public void Join()
    {
        if (_stepping is not { } running) return;
        _stepping = null;
        running.GetAwaiter().GetResult();
        Finish();
    }

    /// <summary>The parallel part of a step: writes only each block's next volumes, outflows, flows and flags.</summary>
    private void Compute()
    {
        const int parallelThreshold = 8;
        if (_awake.Count < parallelThreshold) foreach (var b in _awake) Outflows(b);
        else Parallel.For(0, _awake.Count, k => Outflows(_awake[k]));
        _t2 = Sw();
        if (_prepared.Count < parallelThreshold) foreach (var b in _prepared) Apply(b);
        else Parallel.For(0, _prepared.Count, k => Apply(_prepared[k]));
        _t3 = Sw();
    }

    /// <summary>The new volumes take over; wet/awake/full sets and versions update; springs add their water.</summary>
    private void Finish()
    {
        double tf = Sw();
        int visited = 0;
        var next = new List<Block>(_awake.Count);
        foreach (var b in _prepared)
        {
            b.Prepared = false;
            visited += b.Visited;
            _drained += b.Drained;
            _evaporated += b.Evaporated;
            b.Drained = 0;
            (b.Vol, b.NewVol) = (b.NewVol, b.Vol);
            if (b.Wet > 0 ? _wet.Add(b.Index) : _wet.Remove(b.Index)) WetSetVersion++;
            if (b.HasFull) _fullBlocks.Add(b.Index); else _fullBlocks.Remove(b.Index);
            if (b.Changed)
            {
                b.Version++;
                Version++;
            }
            bool moving = b.Received || b.Quiet < QuietSteps;
            if (moving && !b.Awake && b.Wet > 0) { b.Awake = true; b.Quiet = 0; }
            if (b.Awake && (!moving || b.Wet == 0))
            {
                b.Awake = false;
                b.Out = null; // still water has no momentum
                Array.Clear(b.FlowX);
                Array.Clear(b.FlowY);
            }
            if (b.Awake) next.Add(b);
        }
        _awake = next;
        LastStepCells = visited;
        Timings = $"rebuild {_tp - _tr:0.00} ({_rebuilt}) pressure {_tq - _tp:0.00} ({_fullBlocks.Count} full blocks, {_confined.Count} confined) " +
                  $"prep {_t1 - _t0:0.00} out {_t2 - _t1:0.00} apply {_t3 - _t2:0.00} finish {Sw() - tf:0.00} ms ({_prepared.Count} prepared)";
        AddSprings();
    }

    private void Prepare(Block b)
    {
        if (b.Prepared) return;
        b.Prepared = true;
        if (b.NewVol.Length != b.Vol.Length) b.NewVol = new int[b.Vol.Length];
        _prepared.Add(b);
    }

    private void AddSprings()
    {
        foreach (var (p, (rate, limit)) in _springs)
        {
            var b = Ensure(p.X >> Chunk.Shift, p.Y >> Chunk.Shift);
            if (!FindIn(b, p.X, p.Y, p.Z, out int s)) continue; // buried: dormant
            long surface = (long)b.Floor[s] * U + b.Vol[s];
            long add = Math.Min(rate, limit - surface);
            add = Math.Min(add, (long)(b.Ceil[s] - b.Floor[s]) * U - b.Vol[s]);
            if (add <= 0) continue;
            b.Vol[s] += (int)add;
            _sourced += add;
            Touch(b);
        }
    }

    /// <summary>Pass 1: each span's outflow through each pipe, from the previous state. Writes only this block.</summary>
    private void Outflows(Block b)
    {
        var o = b.Out!;
        var link = b.Link;
        for (int s = 0; s < b.Spans; s++)
        {
            int ob = s * Slots;
            int vol = b.Vol[s];
            if (vol <= 0)
            {
                Array.Clear(o, ob, Slots);
                continue;
            }
            long floor = (long)b.Floor[s] * U, ceil = (long)b.Ceil[s] * U;
            if (b.Confined[s])
            {
                Array.Clear(o, ob, Slots); // flooded: its region moves its water (see Pressurize)
                continue;
            }
            long head = floor + vol;
            long sum = 0;
            for (int slot = 0; slot < Slots; slot++)
            {
                int l = link[ob + slot];
                if (l < 0)
                {
                    o[ob + slot] = 0;
                    continue;
                }
                var nb = _blocks[l >> SpanBits]!;
                int t = l & SpanMask;
                long nf = (long)nb.Floor[t] * U, nc = (long)nb.Ceil[t] * U;
                long lo = Math.Max(Math.Max(floor, nf), (long)b.Sill[ob + slot] * U), hi = Math.Min(ceil, nc);
                if (lo >= hi)
                {
                    o[ob + slot] = 0; // walled off
                    continue;
                }
                if (nb.Confined[t])
                {
                    o[ob + slot] = 0;
                    continue;
                }
                long other = nf + nb.Vol[t];
                long ha = Math.Max(head, lo), hb = Math.Max(other, lo);
                long window = Math.Clamp(Math.Min(Math.Max(ha, hb), hi) - lo, U / 8, U);
                long f = (long)o[ob + slot] * FrictionKeep / FrictionDiv + (ha - hb) * window / ((long)U * Accel);
                if (f < MinFlow) f = 0;
                f = Math.Min(f, Math.Max(0, head - lo)); // only water above the window passes
                o[ob + slot] = (int)f;
                sum += f;
            }
            if (sum > vol)
                for (int i = 0; i < Slots; i++) o[ob + i] = (int)((long)o[ob + i] * vol / sum);
        }
    }

    /// <summary>Pass 2: each span's new volume (its outflows out, its neighbours' outflows towards it in). Writes only this block.</summary>
    private void Apply(Block b)
    {
        var o = b.Out;
        var link = b.Link;
        var back = b.Back;
        int wet = 0, visited = 0;
        bool changed = false, received = false, still = true, full = false;
        long drained = 0, evaporated = 0;
        for (int c = 0; c < Chunk.Size * Chunk.Size; c++)
        {
            int x = b.X0 + (c & Chunk.Mask), y = b.Y0 + (c >> Chunk.Shift);
            bool edge = x == 0 || y == 0 || x == _world.Width - 1 || y == _world.Depth - 1;
            // A sleeping block has no flows of its own: only spans on its rim can receive anything.
            bool rim = (c & Chunk.Mask) == 0 || (c & Chunk.Mask) == Chunk.Mask || c < Chunk.Size || c >= Chunk.Size * Chunk.Mask;
            if (o is null && !rim)
            {
                for (int s = b.Start[c]; s < b.Start[c + 1]; s++)
                {
                    b.NewVol[s] = b.Vol[s];
                    full |= b.Vol[s] >= (long)(b.Ceil[s] - b.Floor[s]) * U;
                    if (b.Vol[s] > 0) wet++;
                }
                continue;
            }
            for (int s = b.Start[c]; s < b.Start[c + 1]; s++)
            {
                long vol = b.Vol[s], fx = 0, fy = 0, inflow = 0, outflow = 0;
                int ob = s * Slots;
                for (int slot = 0; slot < Slots; slot++)
                {
                    int l = link[ob + slot];
                    if (l < 0) continue;
                    int side = slot / Links;
                    long f = o is null ? 0 : o[ob + slot];
                    var nb = _blocks[l >> SpanBits]!;
                    long g = nb.Out is { } no ? no[(l & SpanMask) * Slots + (side ^ 1) * Links + back[ob + slot]] : 0;
                    outflow += f;
                    inflow += g;
                    // Out through the +x side moves water towards +x; in through it, towards -x; and so on.
                    long net = f - g;
                    if (side == 0) fx += net; else if (side == 1) fx -= net; else if (side == 2) fy += net; else fy -= net;
                }
                long n = vol - outflow + inflow;
                // Water standing above its column's natural level seeps away (see Seepage): an overfilled lake or a
                // flood over land drains back down, slowly; natural water and dug-out water below the ground stay.
                if (n > 0 && _natural is { } nat)
                {
                    long over = (long)b.Floor[s] * U + n - nat[(b.Y0 + (c >> Chunk.Shift)) * _world.Width + b.X0 + (c & Chunk.Mask)];
                    if (over > 0) { long e = Math.Min(over, Seepage); n -= e; evaporated += e; }
                }
                if (n < Dry && inflow == 0) { evaporated += n; n = 0; }
                if (edge && n > 0)
                {
                    drained += n;
                    n = 0;
                }
                b.NewVol[s] = (int)n;
                full |= n >= (long)(b.Ceil[s] - b.Floor[s]) * U;
                b.FlowX[s] = (int)(fx / 2);
                b.FlowY[s] = (int)(fy / 2);
                if (n > 0) wet++;
                if (vol > 0 || n > 0) visited++;
                long delta = Math.Abs(n - vol);
                if (delta > StillDelta || outflow > SleepFlow) still = false;
                if (inflow > StillDelta && !b.Awake) received = true;
                // Visible change, with hysteresis: a sixteenth of a level from what views last drew (or wet/dry).
                if (Math.Abs(n - b.Shown[s]) > U / 16 || (b.Shown[s] > 0) != (n > 0))
                {
                    b.Shown[s] = (int)n;
                    changed = true;
                }
            }
        }
        b.Wet = wet;
        b.Visited = visited;
        b.Drained = drained;
        b.Evaporated = evaporated;
        b.HasFull = full;
        b.Changed = changed;
        b.Received = received;
        b.Quiet = still ? b.Quiet + 1 : 0;
    }

    /// <summary>
    /// Pressure. Water in a flooded region (connected spans full to their ceilings: a drowned tunnel, a flooded
    /// mine) can't rise or fall, so it acts as one pipe between the free water at its ends (the lake above a shaft,
    /// an open shaft at the far end, a cave half full). All its ends share one pressure: their water heads averaged,
    /// weighted by the size of their openings. Ends standing higher than that push water in, lower ones receive
    /// it, the same amount both ways, so the region stays exactly full and hides nothing: a shaft dug down from a
    /// lake, along a tunnel and up again fills to the lake's level, and a U-bend levels out. Each opening passes
    /// up to about a level of water a step (a real flooded tunnel fills a shaft in seconds). If the ends stand lower
    /// than the region's roof, it isn't under pressure: ordinary pipes drain it. Regions are found each step from
    /// the blocks that have full spans; inside them pipes carry nothing (they're marked Confined). Serial and in
    /// block/span order: deterministic.
    /// </summary>
    private void Pressurize()
    {
        foreach (var (cb, cs) in _confined)
            if (cs < cb.Confined.Length) cb.Confined[cs] = false;
        _confined.Clear();
        if (_fullBlocks.Count == 0) return;

        var seen = new HashSet<long>();
        var members = new List<(Block B, int S)>();
        var ends = new List<(Block B, int S, long Lo, long Window, long Head)>();
        var queue = new Queue<(Block B, int S)>();
        foreach (int bi in _fullBlocks.ToArray())
        {
            var b0 = _blocks[bi]!;
            if (b0.NeedsSpans) continue;
            if (!b0.LinksValid) BuildLinks(b0);
            for (int s0 = 0; s0 < b0.Spans; s0++)
            {
                if (!IsFull(b0, s0) || !seen.Add((long)b0.Index << SpanBits | s0)) continue;
                members.Clear();
                ends.Clear();
                queue.Enqueue((b0, s0));
                long top = 0;
                while (queue.TryDequeue(out var m))
                {
                    members.Add(m);
                    var (b, s) = m;
                    top = Math.Max(top, (long)b.Ceil[s] * U);
                    if (!b.LinksValid) BuildLinks(b);
                    for (int slot = 0; slot < Slots; slot++)
                    {
                        int l = b.Link[s * Slots + slot];
                        if (l < 0) continue;
                        var nb = _blocks[l >> SpanBits]!;
                        int t = l & SpanMask;
                        if (b.Sill[s * Slots + slot] >= Math.Min(b.Ceil[s], nb.Ceil[t])) continue; // a wall between: not one region
                        if (IsFull(nb, t))
                        {
                            if (seen.Add((long)nb.Index << SpanBits | t)) queue.Enqueue((nb, t));
                            continue;
                        }
                        long lo = Math.Max((long)Math.Max(b.Floor[s], nb.Floor[t]), b.Sill[s * Slots + slot]) * U, hi = (long)Math.Min(b.Ceil[s], nb.Ceil[t]) * U;
                        if (lo >= hi) continue; // walled off
                        long head = Math.Max((long)nb.Floor[t] * U + nb.Vol[t], lo); // dry below the opening: water pours in
                        ends.Add((nb, t, lo, Math.Min(hi - lo, U), head));
                    }
                }
                // The region's pressure: its ends' heads, averaged by opening.
                long weight = 0;
                double sum = 0;
                foreach (var e in ends)
                {
                    weight += e.Window;
                    sum += (double)e.Window * e.Head;
                }
                long press = weight > 0 ? (long)(sum / weight) : top;
                if (press < top - U / 16) continue; // not pressed: ordinary pipes drain or fill it
                long extra = 0;
                foreach (var (b, s) in members)
                {
                    long cap = (long)(b.Ceil[s] - b.Floor[s]) * U;
                    extra += Math.Max(0, b.Vol[s] - cap); // water squeezed in earlier: let it out
                    b.Vol[s] = (int)Math.Min(b.Vol[s], cap);
                    b.Confined[s] = true;
                    b.Press[s] = (int)Math.Min(int.MaxValue, press);
                    _confined.Add((b, s));
                }
                if (ends.Count == 0)
                {
                    members[0].B.Vol[members[0].S] += (int)extra; // sealed: nowhere to go
                    continue;
                }
                // Ends above the pressure give, ends below it take, half the difference a step, at most a level per
                // opening; scaled so that what goes in comes out (plus any extra that was squeezed in).
                long give = 0, take = 0;
                var flow = new long[ends.Count];
                for (int k = 0; k < ends.Count; k++)
                {
                    var e = ends[k];
                    long q = (e.Head - press) * e.Window / (2L * U);
                    q = Math.Clamp(q, -e.Window, e.Window);
                    if (q > 0) q = Math.Min(q, Math.Max(0, (long)e.B.Floor[e.S] * U + e.B.Vol[e.S] - e.Lo)); // only water above the opening
                    flow[k] = q;
                    if (q > 0) give += q; else take -= q;
                }
                long moved = Math.Min(give + extra, take);
                if (moved <= 0)
                {
                    members[0].B.Vol[members[0].S] += (int)extra;
                    continue;
                }
                long fromExtra = Math.Min(extra, moved), fromEnds = moved - fromExtra;
                long given = fromExtra, received = 0;
                int lastTaker = -1;
                for (int k = 0; k < ends.Count; k++)
                {
                    var e = ends[k];
                    if (flow[k] > 0)
                    {
                        long q = give > 0 ? flow[k] * fromEnds / give : 0;
                        e.B.Vol[e.S] -= (int)q;
                        given += q;
                    }
                }
                for (int k = 0; k < ends.Count; k++)
                {
                    if (flow[k] >= 0) continue;
                    var e = ends[k];
                    long q = -flow[k] * given / take;
                    e.B.Vol[e.S] += (int)q;
                    received += q;
                    lastTaker = k;
                }
                if (lastTaker >= 0) ends[lastTaker].B.Vol[ends[lastTaker].S] += (int)(given - received); // rounding
                if (extra > fromExtra) members[0].B.Vol[members[0].S] += (int)(extra - fromExtra);
                foreach (var e in ends) Touch(e.B, visible: true);
            }
        }
    }

    private static bool IsFull(Block b, int s) => b.Vol[s] >= (long)(b.Ceil[s] - b.Floor[s]) * U;

    private const int SpanBits = 13, SpanMask = (1 << SpanBits) - 1;

    /// <summary>
    /// Each span's pipes: for each side, up to <see cref="Links"/> spans of the neighbouring column whose open
    /// range overlaps it (lowest first), as (block, span) packed in an int, and which of that span's pipes leads
    /// back. A pipe exists only if it exists both ways, so every flow out of one span flows into another.
    /// </summary>
    /// <summary>
    /// The lowest level water can cross from column (x, y) to (nx, ny) within the shared window [lo, hi): above the
    /// run of watertight thin walls standing on that edge from the window's bottom (a wall line is a dam; water spills
    /// over its top). <paramref name="hi"/> or more means the window is walled shut. Walls higher up are ignored.
    /// </summary>
    private int SillOf(int x, int y, int nx, int ny, int lo, int hi)
    {
        if (!_world.HasEdges) return lo;
        int z = lo;
        while (z < hi && EdgeInfo.HoldsWater(_world.EdgeBetween(new TilePos(x, y, z), new TilePos(nx, ny, z)))) z++;
        return z;
    }

    private void BuildLinks(Block b)
    {
        b.LinksValid = true;
        int n = b.Spans * Slots;
        if (b.Link.Length != n)
        {
            b.Link = new int[n];
            b.Back = new byte[n];
            b.Sill = new ushort[n];
        }
        Array.Fill(b.Link, -1);
        Array.Clear(b.Sill);
        for (int c = 0; c < Chunk.Size * Chunk.Size; c++)
        {
            int x = b.X0 + (c & Chunk.Mask), y = b.Y0 + (c >> Chunk.Shift);
            if (x >= _world.Width || y >= _world.Depth) continue;
            for (int side = 0; side < 4; side++)
            {
                int nx = x + (side == 0 ? 1 : side == 1 ? -1 : 0), ny = y + (side == 2 ? 1 : side == 3 ? -1 : 0);
                if ((uint)nx >= (uint)_world.Width || (uint)ny >= (uint)_world.Depth) continue;
                var nb = _blocks[(ny >> Chunk.Shift) * _bw + (nx >> Chunk.Shift)];
                if (nb is null || nb.NeedsSpans) continue;
                int nc = Col(nx, ny);
                for (int s = b.Start[c]; s < b.Start[c + 1]; s++)
                {
                    int k = 0;
                    for (int t = nb.Start[nc]; t < nb.Start[nc + 1] && k < Links; t++)
                    {
                        if (Math.Max(b.Floor[s], nb.Floor[t]) >= Math.Min(b.Ceil[s], nb.Ceil[t])) continue;
                        // Which of t's pipes towards us is s (t's overlaps with our column, lowest first)?
                        int kb = 0;
                        for (int u = b.Start[c]; u < s; u++)
                            if (Math.Max(b.Floor[u], nb.Floor[t]) < Math.Min(b.Ceil[u], nb.Ceil[t])) kb++;
                        if (kb < Links)
                        {
                            b.Link[s * Slots + side * Links + k] = nb.Index << SpanBits | t;
                            b.Sill[s * Slots + side * Links + k] = (ushort)SillOf(x, y, nx, ny, Math.Max(b.Floor[s], nb.Floor[t]), Math.Min(b.Ceil[s], nb.Ceil[t]));
                            b.Back[s * Slots + side * Links + k] = (byte)kb;
                        }
                        k++;
                    }
                }
            }
        }
    }


    // ---- Spans ----------------------------------------------------------------------------------------

    private static int Col(int x, int y) => (y & Chunk.Mask) * Chunk.Size + (x & Chunk.Mask);

    private Block? Neighbour(Block b, int side)
    {
        int bx = b.Index % _bw + (side == 0 ? 1 : side == 1 ? -1 : 0), by = b.Index / _bw + (side == 2 ? 1 : side == 3 ? -1 : 0);
        return bx < 0 || by < 0 || bx >= _bw || by >= _bd ? null : Ensure(bx, by);
    }

    private bool Find(int x, int y, int z, out Block b, out int s)
    {
        s = -1;
        b = null!;
        if (!_world.InBounds(x, y, z) || _blocks[(y >> Chunk.Shift) * _bw + (x >> Chunk.Shift)] is not { NeedsSpans: false } found) return false;
        b = found;
        return FindIn(b, x, y, z, out s);
    }

    private static bool FindIn(Block b, int x, int y, int z, out int s)
    {
        int c = Col(x, y);
        for (s = b.Start[c]; s < b.Start[c + 1]; s++)
            if (z >= b.Floor[s] && z < b.Ceil[s]) return true;
        s = -1;
        return false;
    }

    /// <summary>The block for a chunk column, created (spans read from the world) if needed.</summary>
    private Block Ensure(int bx, int by)
    {
        int bi = by * _bw + bx;
        if (_blocks[bi] is not { } b)
        {
            int x0 = bx << Chunk.Shift, y0 = by << Chunk.Shift;
            b = new Block(bi, x0, y0, x0 == 0 || y0 == 0 || x0 + Chunk.Size >= _world.Width || y0 + Chunk.Size >= _world.Depth);
            _blocks[bi] = b;
        }
        if (b.NeedsSpans) Rebuild(b);
        return b;
    }

    /// <summary>Wakes a block (water was added, moved or its ground changed); <paramref name="visible"/> also bumps its version.</summary>
    private void Touch(Block b, bool visible = false)
    {
        b.Quiet = 0;
        if (visible)
        {
            b.Version++;
            Version++;
            if (_wet.Add(b.Index)) WetSetVersion++;
        }
        if (b.Awake) return;
        b.Awake = true;
        _awake.Add(b);
    }

    /// <summary>
    /// Reads a block's spans from the world (open runs of cells per column, each resting on solid ground or the
    /// world's floor), keeping its water: every span's water is laid back into the column cell by cell, and what
    /// now lies in solid cells (a wall built in a river, a cave-in) goes to the space above it, or else below.
    /// </summary>
    private void Rebuild(Block b)
    {
        bool hadSpans = !b.NeedsSpans;
        b.NeedsSpans = false;
        int h = _world.Height;
        var floors = new List<short>();
        var ceils = new List<short>();
        var start = new int[Chunk.Size * Chunk.Size + 1];
        var open = new bool[h];
        var oldWater = new List<(long Bottom, long Top, long Pressed)>();
        var newVol = new List<int>();
        for (int c = 0; c < Chunk.Size * Chunk.Size; c++)
        {
            int x = b.X0 + (c & Chunk.Mask), y = b.Y0 + (c >> Chunk.Shift);
            start[c] = floors.Count;
            if (x >= _world.Width || y >= _world.Depth) continue;
            ReadColumn(x, y, open);
            int first = floors.Count;
            for (int z = 0; z < h; z++)
            {
                if (!open[z] || z > 0 && open[z - 1]) continue;
                int top = z;
                while (top < h && open[top]) top++;
                floors.Add((short)z);
                ceils.Add((short)top);
                newVol.Add(0);
            }
            if (!hadSpans) continue;
            // Lay the old water back in; a pressed span's extra (past its ceiling) stays with the space its floor is in.
            oldWater.Clear();
            for (int s = b.Start[c]; s < b.Start[c + 1]; s++)
                if (b.Vol[s] > 0)
                {
                    long bottom = (long)b.Floor[s] * U, cap = (long)(b.Ceil[s] - b.Floor[s]) * U;
                    oldWater.Add((bottom, bottom + Math.Min(b.Vol[s], cap), Math.Max(0, b.Vol[s] - cap)));
                }
            foreach (var (bottom, topW, pressed) in oldWater)
            {
                long left = topW - bottom + pressed;
                int home = -1;
                for (int s = first; s < floors.Count; s++)
                {
                    if (bottom >= (long)floors[s] * U && bottom < (long)ceils[s] * U) home = s;
                    long lo = Math.Max(bottom, (long)floors[s] * U), hi = Math.Min(topW, (long)ceils[s] * U);
                    if (hi <= lo) continue;
                    newVol[s] += (int)(hi - lo);
                    left -= hi - lo;
                }
                if (left <= 0) continue;
                // Displaced (and pressure): to the space the water stood in, else the lowest space above it, else
                // the highest one below. Past its ceiling it presses, and flows on.
                int to = home;
                for (int s = first; s < floors.Count && to < 0; s++)
                    if ((long)floors[s] * U >= bottom) to = s;
                if (to < 0) to = floors.Count - 1;
                if (to >= first) newVol[to] += (int)left;
            }
        }
        start[Chunk.Size * Chunk.Size] = floors.Count;
        b.Start = start;
        b.Floor = floors.ToArray();
        b.Ceil = ceils.ToArray();
        b.Vol = hadSpans ? newVol.ToArray() : new int[floors.Count];
        b.NewVol = new int[floors.Count];
        b.Shown = (int[])b.Vol.Clone();
        b.FlowX = new int[floors.Count];
        b.FlowY = new int[floors.Count];
        b.Out = null;
        b.Confined = new bool[floors.Count];
        b.Press = new int[floors.Count];
        b.HasFull = true; // let the next step look for flooded regions here
        _fullBlocks.Add(b.Index);
        b.LinksValid = false;
        for (int side = 0; side < 4; side++)
        {
            int nbx = b.Index % _bw + (side == 0 ? 1 : side == 1 ? -1 : 0), nby = b.Index / _bw + (side == 2 ? 1 : side == 3 ? -1 : 0);
            if (nbx >= 0 && nby >= 0 && nbx < _bw && nby < _bd && _blocks[nby * _bw + nbx] is { } nb) nb.LinksValid = false;
        }
        b.Version++;
        Version++;
    }

    /// <summary>Which cells of a column are open (not solid), reading whole chunks at once where they're uniform.</summary>
    private void ReadColumn(int x, int y, bool[] open)
    {
        int cx = x >> Chunk.Shift, cy = y >> Chunk.Shift, lx = x & Chunk.Mask, ly = y & Chunk.Mask;
        for (int cz = 0; cz < _world.ChunksZ; cz++)
        {
            var chunk = _world.Chunks[_world.ChunkIndex(cx, cy, cz)];
            int z0 = cz << Chunk.Shift;
            if (chunk.IsUniform)
            {
                bool o = !TileInfo.IsSolid(chunk.UniformType);
                for (int lz = 0; lz < Chunk.Size && z0 + lz < open.Length; lz++) open[z0 + lz] = o;
                continue;
            }
            var raw = chunk.Raw!;
            for (int lz = 0; lz < Chunk.Size && z0 + lz < open.Length; lz++) open[z0 + lz] = !TileInfo.IsSolid(raw[Chunk.Index(lx, ly, lz)]);
        }
    }

    // ---- Snapshot (see Net.Snapshot) ----------------------------------------------------------------------

    /// <summary>Finishes any step in flight and drops the pipes (rebuilt from the spans; a loaded copy does the same).</summary>
    internal void Settle()
    {
        Join();
        foreach (var b in _blocks) if (b != null) b.LinksValid = false;
        Net.SnapIO.Compact(_springs);
        Net.SnapIO.Compact(_dirty);
    }

    internal void SnapSave(BinaryWriter w)
    {
        Join();
        w.Write(_blocks.Count(b => b != null));
        foreach (var b in _blocks)
        {
            if (b == null) continue;
            w.Write(b.Index);
            Net.SnapIO.WriteInts(w, b.Start);
            Net.SnapIO.WriteShorts(w, b.Floor);
            Net.SnapIO.WriteShorts(w, b.Ceil);
            Net.SnapIO.WriteInts(w, b.Vol);
            w.Write(b.Out != null);
            if (b.Out != null) Net.SnapIO.WriteInts(w, b.Out);
            Net.SnapIO.WriteInts(w, b.FlowX);
            Net.SnapIO.WriteInts(w, b.FlowY);
            Net.SnapIO.WriteInts(w, b.Shown);
            Net.SnapIO.WriteBools(w, b.Confined);
            Net.SnapIO.WriteInts(w, b.Press);
            w.Write(b.HasFull); w.Write(b.Awake); w.Write(b.Prepared); w.Write(b.NeedsSpans); w.Write(b.Changed); w.Write(b.Received);
            w.Write(b.Quiet); w.Write(b.Version); w.Write(b.Wet); w.Write(b.Drained); w.Write(b.Evaporated); w.Write(b.Visited);
        }
        void Set(IReadOnlyCollection<int> s) { w.Write(s.Count); foreach (int i in s) w.Write(i); }
        Set(_wet);
        Set(_dirty);
        Set(_fullBlocks);
        w.Write(_confined.Count);
        foreach (var (b, s) in _confined) { w.Write(b.Index); w.Write(s); }
        w.Write(_awake.Count);
        foreach (var b in _awake) w.Write(b.Index);
        w.Write(_prepared.Count);
        foreach (var b in _prepared) w.Write(b.Index);
        w.Write(_springs.Count);
        foreach (var (p, (rate, limit)) in _springs) { Net.SnapIO.Write(w, p); w.Write(rate); w.Write(limit); }
        w.Write(_drained); w.Write(_sourced); w.Write(_evaporated);
        w.Write(Version); w.Write(Steps); w.Write(LastStepCells);
    }

    internal void SnapLoad(BinaryReader r, Procedural.Terrain? terrain)
    {
        if (terrain != null) SetNatural(terrain);
        int count = r.ReadInt32();
        for (int k = 0; k < count; k++)
        {
            int bi = r.ReadInt32();
            int x0 = (bi % _bw) << Chunk.Shift, y0 = (bi / _bw) << Chunk.Shift;
            var b = new Block(bi, x0, y0, x0 == 0 || y0 == 0 || x0 + Chunk.Size >= _world.Width || y0 + Chunk.Size >= _world.Depth);
            _blocks[bi] = b;
            b.Start = Net.SnapIO.ReadInts(r);
            b.Floor = Net.SnapIO.ReadShorts(r);
            b.Ceil = Net.SnapIO.ReadShorts(r);
            b.Vol = Net.SnapIO.ReadInts(r);
            b.Out = r.ReadBoolean() ? Net.SnapIO.ReadInts(r) : null;
            b.FlowX = Net.SnapIO.ReadInts(r);
            b.FlowY = Net.SnapIO.ReadInts(r);
            b.Shown = Net.SnapIO.ReadInts(r);
            b.Confined = Net.SnapIO.ReadBools(r);
            b.Press = Net.SnapIO.ReadInts(r);
            b.HasFull = r.ReadBoolean(); b.Awake = r.ReadBoolean(); b.Prepared = r.ReadBoolean(); b.NeedsSpans = r.ReadBoolean();
            b.Changed = r.ReadBoolean(); b.Received = r.ReadBoolean();
            b.Quiet = r.ReadInt32(); b.Version = r.ReadInt32(); b.Wet = r.ReadInt32();
            b.Drained = r.ReadInt64(); b.Evaporated = r.ReadInt64(); b.Visited = r.ReadInt32();
            b.NewVol = new int[b.Floor.Length];
            b.LinksValid = false;
        }
        void Set(ICollection<int> s) { s.Clear(); int n = r.ReadInt32(); for (int i = 0; i < n; i++) s.Add(r.ReadInt32()); }
        Set(_wet);
        WetSetVersion++;
        Set(_dirty);
        Set(_fullBlocks);
        _confined.Clear();
        int nc = r.ReadInt32();
        for (int i = 0; i < nc; i++) _confined.Add((_blocks[r.ReadInt32()]!, r.ReadInt32()));
        _awake = new List<Block>();
        int na = r.ReadInt32();
        for (int i = 0; i < na; i++) _awake.Add(_blocks[r.ReadInt32()]!);
        _prepared.Clear();
        int np = r.ReadInt32();
        for (int i = 0; i < np; i++) _prepared.Add(_blocks[r.ReadInt32()]!);
        _springs.Clear();
        int ns = r.ReadInt32();
        for (int i = 0; i < ns; i++) _springs[Net.SnapIO.ReadPos(r)] = (r.ReadInt32(), r.ReadInt32());
        _drained = r.ReadInt64(); _sourced = r.ReadInt64(); _evaporated = r.ReadInt64();
        Version = r.ReadInt32(); Steps = r.ReadInt64(); LastStepCells = r.ReadInt32();
    }
}

/// <summary>
/// Water pouring from column (X, Y) over its <see cref="Side"/> edge (0..3: +x, -x, +y, -y) from the surface at
/// <see cref="Top"/> down to <see cref="Bottom"/> (levels), <see cref="Rate"/> levels of water a step.
/// </summary>
public readonly record struct Fall(int X, int Y, int Side, float Top, float Bottom, float Rate);
