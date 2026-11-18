// Headless simulation stress benchmark. Run in Release:
//   dotnet run -c Release --project tools/Colony.Bench -- [size=1024] [colonists=20] [ticks=6000]
// Generates a size x size x 384 world, designates a mix of unreachable work (buried under the map
// centre, scattered across the surface) and reachable work (a pit and a staircase by the colony), then steps
// the sim and reports per-phase tick costs. Watch "max" for hitches and "avg" for steady-state cost.
using System.Diagnostics;
using Colony.Core;
using Colony.Core.Navigation;
using Colony.Core.Procedural;
using Colony.Core.World;

if (args.Length > 0 && args[0] == "flood")
{
    // Flooding check: dotnet run -c Release --project tools/Colony.Bench -- flood [size] [ticks]. Counts water standing
    // on land columns (outside the river and lake beds the terrain carved) inside the simulated area, over time.
    int fsize = args.Length > 1 ? int.Parse(args[1]) : 1024, fticks = args.Length > 2 ? int.Parse(args[2]) : 12000;
    var fs = ColonySim.Generate(new WorldGenSettings(12345, fsize, fsize, fsize == 512 ? 160 : 384), Environment.GetEnvironmentVariable("FLOOD_ALL") is null ? 7 : 0);
    var t = fs.Terrain!;
    for (int tick = 1; tick <= fticks; tick++)
    {
        fs.Step();
        if (Environment.GetEnvironmentVariable("FLOOD_WATCH") is { } watch && tick % 500 == 0)
        {
            var wp = watch.Split(',');
            int wx = int.Parse(wp[0]), wy = int.Parse(wp[1]);
            var line = new System.Text.StringBuilder($"  t{tick}:");
            for (int x = wx - 30; x <= wx + 30; x += 3)
            {
                int i = wy * fsize + x, s = t.Surface[i], top = s;
                while (top < 383 && fs.Water.MassAt(x, wy, top + 1) >= Colony.Core.Fluids.Water.Full / 4) top++;
                line.Append($" {(t.Kind[i] == SurfaceKind.RiverBed ? 'R' : t.Kind[i] == SurfaceKind.LakeBed ? 'K' : '.')}{s % 100}+{top - s}");
            }
            Console.WriteLine(line);
        }
        if (tick % (fticks / 8) != 0 && tick != 1) continue;
        int flooded = 0, wet = 0, films = 0;
        for (int y = 0; y < fsize; y++)
        for (int x = 0; x < fsize; x++)
        {

            { int fi = y * fsize + x; if (t.Kind[fi] == SurfaceKind.Land && fs.Water.MassAt(x, y, t.Surface[fi] + 1) >= 20) films++; }
            int i = y * fsize + x, s = t.Surface[i];
            bool any = false;
            for (int z = s + 1; z <= Math.Min(383, s + 6); z++)
                if (fs.Water.MassAt(x, y, z) >= Colony.Core.Fluids.Water.Full / 4) { any = true; break; }
            if (!any) continue;
            wet++;
            if (t.Kind[i] == SurfaceKind.Land && t.Water[i] == 0) flooded++;
        }
        var fillHist = new int[11];
        for (int y = 0; y < fsize; y++)
        for (int x = 0; x < fsize; x++)
        {
            int i = y * fsize + x;
            if (t.Kind[i] != SurfaceKind.RiverBed) continue;
            fillHist[Math.Min(10, fs.Water.MassAt(x, y, t.Surface[i] + t.Water[i]) / 100)]++;
        }
        Console.WriteLine($"  river top cells by tenths full: {string.Join(" ", fillHist)}");
        Console.WriteLine($"tick {tick}: {wet} wet columns, {flooded} of them on land (flooded), {films} land columns with a film; drained {fs.Water.Drained / 1000} cells; total {fs.Water.Total() / 1000}, sourced {fs.Water.Sourced / 1000}, evaporated {fs.Water.Evaporated / 1000}, balance {(fs.Water.Total() + fs.Water.Drained + fs.Water.Evaporated - fs.Water.Sourced) / 1000}");
    }
    // Where: flooded columns per 32x32 block, the worst first, with the distance to the nearest carved water.
    var blocks = new Dictionary<(int, int), (int N, int MinDist, int X, int Y)>();
    for (int y = 0; y < fsize; y++)
    for (int x = 0; x < fsize; x++)
    {
        int i = y * fsize + x, s = t.Surface[i];
        if (t.Kind[i] != SurfaceKind.Land || t.Water[i] != 0) continue;
        if (fs.Water.MassAt(x, y, s + 1) < Colony.Core.Fluids.Water.Full / 4) continue;
        var k = (x >> 5, y >> 5);
        var b = blocks.TryGetValue(k, out var have) ? have : (N: 0, MinDist: 999, X: x, Y: y);
        blocks[k] = (b.N + 1, Math.Min(b.MinDist, t.WaterDistance[i]), b.X, b.Y);
    }
    foreach (var (k, b) in blocks.OrderByDescending(kv => kv.Value.N).Take(8))
        Console.WriteLine($"  block {k}: {b.N} flooded, nearest carved water {b.MinDist} tiles, e.g. ({b.X},{b.Y})");
    if (Environment.GetEnvironmentVariable("FLOOD_TRACE") is { } trace)
    {
        var tp = trace.Split(',');
        int sx0 = int.Parse(tp[0]), sy0 = int.Parse(tp[1]);
        // Nearest river tile, then BFS over river tiles to an outlet (map edge, lake or frozen area).
        int start = -1, best = int.MaxValue;
        for (int y = sy0 - 8; y <= sy0 + 8; y++) for (int x = sx0 - 8; x <= sx0 + 8; x++)
            if (t.Kind[y * fsize + x] == SurfaceKind.RiverBed && (x - sx0) * (x - sx0) + (y - sy0) * (y - sy0) < best) { best = (x - sx0) * (x - sx0) + (y - sy0) * (y - sy0); start = y * fsize + x; }
        var from = new Dictionary<int, int> { [start] = -1 };
        var q = new Queue<int>(); q.Enqueue(start); int end = -1;
        while (q.TryDequeue(out int c) && end < 0)
        {
            int qx = c % fsize, qy = c / fsize;
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                if ((dx != 0) == (dy != 0)) continue;
                int nx = qx + dx, ny = qy + dy, n = ny * fsize + nx;
                if (nx < 0 || ny < 0 || nx >= fsize || ny >= fsize || from.ContainsKey(n)) continue;
                if (t.Kind[n] != SurfaceKind.Land && (nx == 0 || ny == 0 || nx == fsize - 1 || ny == fsize - 1)) { from[n] = c; end = n; break; }
                if (t.Kind[n] == SurfaceKind.Land) continue;
                from[n] = c; q.Enqueue(n);
            }
        }
        var path = new List<int>();
        for (int p = end; p >= 0; p = from[p]) path.Add(p);
        path.Reverse();
        Console.WriteLine($"  path of {path.Count} tiles to {(end < 0 ? "nowhere" : t.Kind[end] == SurfaceKind.LakeBed ? "a lake" : "the edge")}:");
        var sb2 = new System.Text.StringBuilder();
        for (int k = 0; k < path.Count; k += Math.Max(1, path.Count / 40))
        {
            int p = path[k], x = p % fsize, y = p / fsize, s = t.Surface[p], top = s;
            while (top < 383 && fs.Water.MassAt(x, y, top + 1) >= Colony.Core.Fluids.Water.Full / 4) top++;
            sb2.Append($" {(t.Kind[p] == SurfaceKind.LakeBed ? 'K' : 'R')}{t.Surface[p] + t.Water[p]}/{top}");
        }
        Console.WriteLine($"  carved top / water top along it:{sb2}");
    }
    if (Environment.GetEnvironmentVariable("FLOOD_MAP") is { } fmap)
    {
        // Every 3rd column: kind letter and water level % 100 (land: height % 100, '*' if water stands on it).
        var mp2 = fmap.Split(',');
        int ax = int.Parse(mp2[0]), ay = int.Parse(mp2[1]);
        for (int y = ay - 45; y <= ay + 45; y += 3)
        {
            var line = new System.Text.StringBuilder();
            for (int x = ax - 75; x <= ax + 75; x += 3)
            {
                if (x < 0 || y < 0 || x >= fsize || y >= fsize) { line.Append("    "); continue; }
                int i = y * fsize + x, top = t.Surface[i] + t.Water[i];
                bool wetLand = t.Kind[i] == SurfaceKind.Land && fs.Water.MassAt(x, y, t.Surface[i] + 1) >= 100;
                char k = t.Kind[i] == SurfaceKind.RiverBed ? 'R' : t.Kind[i] == SurfaceKind.LakeBed ? 'K' : wetLand ? '*' : '.';
                line.Append($"{k}{top % 100,2} ");
            }
            Console.WriteLine(line);
        }
    }
    if (Environment.GetEnvironmentVariable("FLOOD_MASS") is { } fm)
    {
        // Per column: kind, water level (or land height) % 100, and the mass in the top water cell / the cell above it.
        var mp = fm.Split(',');
        int mx0 = int.Parse(mp[0]), my0 = int.Parse(mp[1]);
        for (int y = my0 - 6; y <= my0 + 6; y++)
        {
            var line = new System.Text.StringBuilder();
            for (int x = mx0 - 9; x <= mx0 + 9; x++)
            {
                int i = y * fsize + x, top = t.Surface[i] + t.Water[i];
                char kind = t.Kind[i] == SurfaceKind.RiverBed ? 'R' : t.Kind[i] == SurfaceKind.LakeBed ? 'K' : '.';
                int below = t.Kind[i] == SurfaceKind.Land ? fs.Water.MassAt(x, y, top + 1) : fs.Water.MassAt(x, y, top);
                line.Append($"{kind}{top % 100,2}:{below,4}/{fs.Water.MassAt(x, y, top + 1 + (t.Kind[i] == SurfaceKind.Land ? 1 : 0)),3} ");
            }
            Console.WriteLine(line);
        }
    }
    if (Environment.GetEnvironmentVariable("FLOOD_AT") is { } at)
    {
        var parts = at.Split(',');
        int cx0 = int.Parse(parts[0]), cy0 = int.Parse(parts[1]);
        for (int y = cy0 - 8; y <= cy0 + 8; y++)
        {
            var line = new System.Text.StringBuilder();
            for (int x = cx0 - 20; x <= cx0 + 20; x++)
            {
                int i = y * fsize + x, s = t.Surface[i];
                int top = s;
                while (top < 383 && fs.Water.MassAt(x, y, top + 1) >= Colony.Core.Fluids.Water.Full / 4) top++;
                char kind = t.Kind[i] == SurfaceKind.RiverBed ? 'R' : t.Kind[i] == SurfaceKind.LakeBed ? 'K' : '.';
                line.Append($"{kind}{s % 100,2}{(top > s ? (top - s).ToString() : " ")} ");
            }
            Console.WriteLine(line);
        }
    }
    var spawn = fs.Colonists[0].Position;
    Console.WriteLine($"  colony at ({spawn.X},{spawn.Y}); springs: {fs.Water.Springs.Count}");
    return;
}

if (args.Length > 0 && args[0] == "terrain")
{
    // Hydrology consistency: dotnet run -c Release --project tools/Colony.Bench -- terrain [size]
    int tsize = args.Length > 1 ? int.Parse(args[1]) : 1024;
    var tt = Terrain.Generate(new WorldGenSettings(12345, tsize, tsize, 384));
    int spills = 0, steps = 0, rivers = 0, lakes = 0, drops = 0, ex = -1, lakeSide = 0, intoLake = 0;
    for (int y = 0; y < tsize; y++)
    for (int x = 0; x < tsize; x++)
    {
        int i = y * tsize + x;
        if (tt.Kind[i] == SurfaceKind.Land) continue;
        if (tt.Kind[i] == SurfaceKind.RiverBed) rivers++; else lakes++;
        int lvl = tt.Surface[i] + tt.Water[i];
        for (int k = 0; k < 4; k++)
        {
            int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
            if (nx < 0 || ny < 0 || nx >= tsize || ny >= tsize) continue;
            int j = ny * tsize + nx;
            if (tt.Kind[j] == SurfaceKind.Land)
            {
                if (tt.Surface[j] < lvl) { spills++; if (ex < 0) ex = i; }
                continue;
            }
            int other = tt.Surface[j] + tt.Water[j];
            if (other == lvl) continue;
            if (other < lvl) { drops++; if (tt.FlowDirection[i] != (k == 0 ? 0 : k == 1 ? 2 : k == 2 ? 1 : 3)) { steps++; if (tt.Kind[i] == SurfaceKind.LakeBed) lakeSide++; else if (tt.Kind[j] == SurfaceKind.LakeBed) intoLake++; } }
        }
    }
    Console.WriteLine($"{rivers} river, {lakes} lake tiles; {spills} water-to-lower-land contacts (e.g. {ex % tsize},{ex / tsize}); " +
                      $"{drops} drops, {steps} of them not downstream (side steps: {lakeSide} from a lake, {intoLake} river beside a lower lake)");

    // Water systems (4-connected) that never reach the map edge, river tiles with no way out, big drops, heads.
    var comp = new int[tsize * tsize];
    int orphans = 0, orphanTiles = 0, orphanLakes = 0, noFlow = 0, bigDrops = 0, heads = 0;
    var oq = new Stack<int>();
    for (int s0 = 0; s0 < comp.Length; s0++)
    {
        if (tt.Kind[s0] == SurfaceKind.Land || comp[s0] != 0) continue;
        comp[s0] = 1;
        oq.Push(s0);
        bool edge = false, lake = false;
        int csize = 0;
        while (oq.TryPop(out int c))
        {
            csize++;
            int ocx = c % tsize, ocy = c / tsize;
            edge |= ocx == 0 || ocy == 0 || ocx == tsize - 1 || ocy == tsize - 1;
            lake |= tt.Kind[c] == SurfaceKind.LakeBed;
            for (int k = 0; k < 4; k++)
            {
                int nx = ocx + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = ocy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= tsize || ny >= tsize) continue;
                int j = ny * tsize + nx;
                if (tt.Kind[j] == SurfaceKind.Land || comp[j] != 0) continue;
                comp[j] = 1;
                oq.Push(j);
            }
        }
        if (!edge) { orphans++; orphanTiles += csize; if (lake) orphanLakes++; }
    }
    for (int i = 0; i < comp.Length; i++)
    {
        if (tt.Kind[i] != SurfaceKind.RiverBed) continue;
        if (tt.FlowDirection[i] == Terrain.NoFlow) noFlow++;
        int x = i % tsize, y = i / tsize, lvl = tt.Surface[i] + tt.Water[i];
        bool hasUp = false;
        for (int k = 0; k < 4; k++)
        {
            int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
            if (nx < 0 || ny < 0 || nx >= tsize || ny >= tsize) continue;
            int j = ny * tsize + nx;
            if (tt.Kind[j] == SurfaceKind.Land) continue;
            if (lvl - (tt.Surface[j] + tt.Water[j]) >= 3) bigDrops++;
            if (tt.Kind[j] == SurfaceKind.RiverBed && tt.FlowDirection[j] == (k == 0 ? 2 : k == 1 ? 0 : k == 2 ? 3 : 1)) hasUp = true;
        }
        if (!hasUp) heads++;
    }
    Console.WriteLine($"{orphans} water systems not reaching the edge ({orphanTiles} tiles, {orphanLakes} with a lake); " +
                      $"{noFlow} river tiles with no way out; {bigDrops} drops of 3+ levels; {heads} river tiles nothing flows into");

    // Traps: water tiles that can't reach the edge without flowing uphill (walking upstream from the edge only
    // ever to water at the same level or higher).
    var drainsOut = new bool[tsize * tsize];
    var dq = new Queue<int>();
    for (int i = 0; i < drainsOut.Length; i++)
    {
        int x = i % tsize, y = i / tsize;
        if (tt.Kind[i] != SurfaceKind.Land && (x == 0 || y == 0 || x == tsize - 1 || y == tsize - 1)) { drainsOut[i] = true; dq.Enqueue(i); }
    }
    while (dq.TryDequeue(out int c))
    {
        int cx2 = c % tsize, cy2 = c / tsize, lc = tt.Surface[c] + tt.Water[c];
        for (int k = 0; k < 4; k++)
        {
            int nx = cx2 + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = cy2 + (k == 2 ? 1 : k == 3 ? -1 : 0);
            if (nx < 0 || ny < 0 || nx >= tsize || ny >= tsize) continue;
            int j = ny * tsize + nx;
            if (drainsOut[j] || tt.Kind[j] == SurfaceKind.Land || tt.Surface[j] + tt.Water[j] < lc) continue;
            drainsOut[j] = true;
            dq.Enqueue(j);
        }
    }
    int traps = 0, trapEx = -1;
    for (int i = 0; i < drainsOut.Length; i++)
        if (tt.Kind[i] != SurfaceKind.Land && !drainsOut[i])
        {
            traps++;
            int bx2 = i % tsize, by2 = i / tsize;
            foreach (int j in new[] { i + 1, i - 1, i + tsize, i - tsize })
                if (j >= 0 && j < drainsOut.Length && drainsOut[j] && trapEx < 0) { trapEx = i; Console.WriteLine($"  trap edge at ({bx2},{by2}) {tt.Kind[i]} level {tt.Surface[i] + tt.Water[i]} next to {tt.Kind[j]} level {tt.Surface[j] + tt.Water[j]} at ({j % tsize},{j / tsize}), flow {tt.FlowDirection[i]}"); }
        }
    Console.WriteLine($"{traps} water tiles with no downhill way out (e.g. {trapEx % tsize},{trapEx / tsize})");

    if (args.Length > 3)
    {
        // Map around (x, y): kind, flow arrow, water level % 100 (land: '.' and height % 100).
        int mx = int.Parse(args[2]), my = int.Parse(args[3]), st = args.Length > 4 ? int.Parse(args[4]) : 1;
        for (int y = my - 12 * st; y <= my + 12 * st; y += st)
        {
            var sb = new System.Text.StringBuilder();
            for (int x = mx - 18 * st; x <= mx + 18 * st; x += st)
            {
                if (x < 0 || y < 0 || x >= tsize || y >= tsize) { sb.Append("    "); continue; }
                int i = y * tsize + x, lvl = tt.Surface[i] + tt.Water[i];
                char a = tt.Kind[i] == SurfaceKind.Land ? '.' : tt.Kind[i] == SurfaceKind.LakeBed ? 'K' :
                    tt.FlowDirection[i] switch { 0 => '>', 1 => 'v', 2 => '<', 3 => '^', _ => '?' };
                sb.Append($"{a}{lvl % 100,2} ");
            }
            Console.WriteLine(sb);
        }
    }
    return;
}

if (args.Length > 0 && args[0] == "far")
{
    // Long trips: dotnet run -c Release --project tools/Colony.Bench -- far [size] [ticks]. Digs the top off the
    // highest peak and follows the colonists walking there.
    int psize = args.Length > 1 ? int.Parse(args[1]) : 2048, pticks = args.Length > 2 ? int.Parse(args[2]) : 20000;
    var ps = ColonySim.Generate(new WorldGenSettings(12345, psize, psize, 384), 7);
    int bx = 0, by = 0, bz = 0;
    for (int y = 8; y < psize - 8; y += 2)
    for (int x = 8; x < psize - 8; x += 2)
        if (ps.World.SurfaceLevel(x, y) > bz) (bx, by, bz) = (x, y, ps.World.SurfaceLevel(x, y));
    int n = ps.Designations.DesignateBox(new(bx - 4, by - 4, bz - 3), new(bx + 4, by + 4, bz));
    var start = ps.Colonists[0].Position;
    Console.WriteLine($"peak ({bx},{by},{bz}), {n} digs; colony at {start}, {Math.Abs(bx - start.X) + Math.Abs(by - start.Y)} tiles away");
    var psw = new Stopwatch();
    double pworst = 0, ptotal = 0;
    for (int t = 1; t <= pticks; t++)
    {
        long nodes0 = Pathfinder.NodesExpanded, s0 = ps.PathSearches;
        psw.Restart();
        ps.Step();
        double ms = psw.Elapsed.TotalMilliseconds;
        ptotal += ms;
        if (ms > pworst || ms > 20)
        {
            pworst = Math.Max(pworst, ms);
            Console.WriteLine($"  tick {t}: {ms:0.0} ms, {ps.PathSearches - s0} searches, {Pathfinder.NodesExpanded - nodes0} nodes");
        }
        if (t % 1000 == 0)
        {
            var c = ps.Colonists;
            Console.WriteLine($"tick {t}: avg {ptotal / t:0.00} ms; digs left {ps.Designations.Count}; colonists {string.Join(" ", c.Select(k => $"{k.State}:{Math.Abs(bx - k.Position.X) + Math.Abs(by - k.Position.Y)}"))}");
        }
    }
    return;
}

if (args.Length > 0 && args[0] == "colony")
{
    // Population over game days with no player: the founders' food, settlers, hunger, departures.
    // dotnet run -c Release --project tools/Colony.Bench -- colony [size] [days]
    int csize = args.Length > 1 ? int.Parse(args[1]) : 512, days = args.Length > 2 ? int.Parse(args[2]) : 12;
    var cs = ColonySim.Generate(new WorldGenSettings(12345, csize, csize, 384), 7);
    var clock = Stopwatch.StartNew();
    for (int d = 1; d <= days; d++)
    {
        for (int t = 0; t < Colony.Core.Agents.Needs.Day; t++) cs.Step();
        var s = cs.Society;
        Console.WriteLine($"day {d}: {cs.Colonists.Count} colonists ({cs.Colonists.Count(c => c.Hungry)} hungry, {cs.Colonists.Count(c => c.Starving)} starving), " +
                          $"food {Colony.Core.Agents.Society.FoodInStore(cs)} meals = {Colony.Core.Agents.Society.FoodDays(cs):0.0} days, " +
                          $"standard {s.Standard:P0}, settlers {s.SettlersArrived}, left {s.Departed}");
    }
    Console.WriteLine($"{days} days in {clock.ElapsedMilliseconds} ms; events: {string.Join(" | ", cs.Events.Select(e => e.Text))}");
    return;
}

if (args.Length > 0 && args[0] == "upipe")
{
    // A U-pipe under the deepest lake near the centre, dug a cell every 2 steps like colonists would: a shaft from the
    // lake bed down 30 levels, a 30-tile tunnel, a shaft back up to the land. dotnet run ... -- upipe [size] [steps]
    int usize = args.Length > 1 ? int.Parse(args[1]) : 1024, steps = args.Length > 2 ? int.Parse(args[2]) : 1200;
    var us = ColonySim.Generate(new WorldGenSettings(12345, usize, usize, 384), 0);
    var t = us.Terrain!;
    int best = -1;
    double bestScore = double.MinValue;
    for (int i = 0; i < t.Kind.Length; i++)
    {
        if (t.Kind[i] != Colony.Core.Procedural.SurfaceKind.LakeBed || t.Water[i] < 3) continue;
        int x = i % usize, y = i / usize;
        if (x + 40 >= usize) continue;
        double score = t.Water[i] * 4 - Math.Abs(x - usize / 2) / 50.0 - Math.Abs(y - usize / 2) / 50.0;
        if (score > bestScore) (best, bestScore) = (i, score);
    }
    int lx = best % usize, ly = best / usize, bed = us.World.SurfaceLevel(lx, ly), bottom = bed - 30;
    Console.WriteLine($"world {usize}: {us.StartupReport}\nlake at ({lx},{ly}) bed {bed}, water {t.Water[best]} deep; tunnel at z={bottom}");
    var cells = new List<TilePos>();
    for (int z = bed; z >= bottom; z--) cells.Add(new(lx, ly, z));
    for (int x = lx + 1; x <= lx + 30; x++) cells.Add(new(x, ly, bottom));
    for (int z = bottom + 1; z <= us.World.SurfaceLevel(lx + 30, ly); z++) cells.Add(new(lx + 30, ly, z));
    cells.Reverse(); // dig from the far end, the lake's bed last (as a careful player would)
    var clock = new Stopwatch();
    double sum = 0, worst = 0;
    var water = us.Water;
    for (int s = 1; s <= steps; s++)
    {
        if (s % 2 == 0 && cells.Count > 0)
        {
            us.World.Set(cells[^1].X, cells[^1].Y, cells[^1].Z, TileType.Air);
            cells.RemoveAt(cells.Count - 1);
        }
        clock.Restart();
        water.Step();
        double ms = clock.Elapsed.TotalMilliseconds;
        sum += ms;
        worst = Math.Max(worst, ms);
        if (s % 50 == 0)
            Console.WriteLine($"  step {s}: {ms:0.00} ms [{water.Timings}], far shaft surface {water.SurfaceAt(lx + 30, ly, bottom + 1):0.0}, {water.AwakeChunks} awake");
    }
    Console.WriteLine($"upipe: avg {sum / steps:0.00} ms/step, worst {worst:0.00} ms");
    return;
}

if (args.Length > 0 && args[0] == "water")
{
    // Water only: dotnet run -c Release --project tools/Colony.Bench -- water [size] [steps]
    int wsize = args.Length > 1 ? int.Parse(args[1]) : 1024, steps = args.Length > 2 ? int.Parse(args[2]) : 600;
    var ws = ColonySim.Generate(new WorldGenSettings(12345, wsize, wsize, 384), 0);
    Console.WriteLine($"world {wsize}: {ws.StartupReport}");
    var clock = new Stopwatch();
    double worst = 0, sum = 0;
    for (int s = 1; s <= steps; s++)
    {
        clock.Restart();
        ws.Water.Step();
        double ms = clock.Elapsed.TotalMilliseconds;
        sum += ms;
        worst = Math.Max(worst, ms);
        if (s % 50 == 0 || s <= 3)
            Console.WriteLine($"  step {s}: {ms:0.00} ms [{ws.Water.Timings}], {ws.Water.LastStepCells} wet cells visited, {ws.Water.AwakeChunks} awake / {ws.Water.WetChunks} wet chunks, drained {ws.Water.Drained / Colony.Core.Fluids.Water.Full} cells");
    }
    Console.WriteLine($"water: avg {sum / steps:0.00} ms/step, worst {worst:0.00} ms");
    return;
}

int size = args.Length > 0 ? int.Parse(args[0]) : 1024;
int colonists = args.Length > 1 ? int.Parse(args[1]) : 20;
int ticks = args.Length > 2 ? int.Parse(args[2]) : 6000;

var sim = ColonySim.Generate(new WorldGenSettings(12345, size, size, 384), colonists);
var w = sim.World;
Console.WriteLine($"world {size}x{size}x{w.Height}, {colonists} colonists: {sim.StartupReport}");

var home = sim.Colonists[0].Position;
int cx = size / 2, cy = size / 2;
int buried = w.SurfaceLevel(cx, cy) - 10;
sim.Designations.DesignateBox(new(cx - 30, cy - 30, buried), new(cx + 29, cy + 29, buried));
for (int i = 0; i < 40; i++)
{
    int x = cx + i * 3 - 60, y = cy + 20;
    sim.Designations.Designate(new(x, y, w.SurfaceLevel(x, y)));
}
int pit = w.SurfaceLevel(home.X + 10, home.Y + 10);
sim.Designations.DesignateBox(new(home.X + 5, home.Y + 5, pit), new(home.X + 34, home.Y + 24, pit));
int stairTop = w.SurfaceLevel(home.X - 8, home.Y + 5);
sim.Designations.DesignateStairs(new(home.X - 8, home.Y + 5, stairTop), new(home.X - 7, home.Y + 6, stairTop - 7));
// Industry load: fell the woods around the colony and haul everything (logs, dug stone) to a stockpile.
int chops = sim.Designations.DesignateBox(new(home.X - 40, home.Y - 40, home.Z - 12), new(home.X + 40, home.Y + 40, home.Z + 12),
    Colony.Core.Orders.DesignationKind.Chop);
int zone = sim.Stockpiles.AddFloorBox(new(home.X - 12, home.Y - 12, home.Z - 8), new(home.X - 3, home.Y - 3, home.Z));
Console.WriteLine($"designations: {sim.Designations.Count} ({chops} trees to chop), stockpile cells: {zone}");

var sw = new Stopwatch();
double max = 0, total = 0, connTotal = 0, boardTotal = 0, stepTotal = 0;
int slowTicks = 0;
for (int t = 0; t < ticks; t++)
{
    // Run the lazily-triggered phases explicitly first so their cost can be attributed.
    sw.Restart();
    sim.Connectivity.Update();
    double conn = sw.Elapsed.TotalMilliseconds;
    sw.Restart();
    sim.JobBoard.Update(w, sim.Designations, sim.Connectivity);
    double board = sw.Elapsed.TotalMilliseconds;
    long searches = sim.PathSearches, nodes = Pathfinder.NodesExpanded;
    int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
    sw.Restart();
    sim.Step();
    double step = sw.Elapsed.TotalMilliseconds;

    double ms = conn + board + step;
    total += ms;
    connTotal += conn; boardTotal += board; stepTotal += step;
    if (ms > 5 && ++slowTicks <= 4 && conn > 2) Console.WriteLine($"  slow connectivity at tick {t}: {conn:0.00} ms [{sim.Connectivity.LastTimings}]");
    if (ms > max)
    {
        max = ms;
        Console.WriteLine($"  new max {ms:0.00} ms at tick {t}: connectivity {conn:0.00}, job board {board:0.00}, " +
                          $"colonists {step:0.00} ({sim.PathSearches - searches} searches, {Pathfinder.NodesExpanded - nodes} nodes)" +
                          (conn > 0.5 ? $" [{sim.Connectivity.LastTimings}]" : "") +
                          $" gc0+{GC.CollectionCount(0) - gc0} gc2+{GC.CollectionCount(2) - gc2}");
    }
}

Console.WriteLine($"{ticks} ticks: avg {total / ticks:0.000} ms, max {max:0.00} ms; {sim.PathSearches} path searches, " +
                  $"{Pathfinder.NodesExpanded} nodes; {sim.Designations.Count} designations left " +
                  $"({sim.JobBoard.InaccessibleCount} inaccessible)");
var stored = sim.Items.Totals(sim.Stockpiles.Contains).Sum();
var loose = sim.Items.Totals(p => !sim.Stockpiles.Contains(p)).Sum();
Console.WriteLine($"items: {stored} stored, {loose} loose in {sim.Items.Count} piles");
Console.WriteLine($"phase totals: connectivity {connTotal:0} ms, job board {boardTotal:0} ms, colonists {stepTotal:0} ms; " +
                  $"{slowTicks} ticks over 5 ms; GCs gen0 {GC.CollectionCount(0)}, gen2 {GC.CollectionCount(2)}");
