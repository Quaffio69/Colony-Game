// Colony playtest: play the real simulation a turn at a time through text, as an AI playtester would.
//
//   playtest new    DIR [seed] [size]   a fresh colony; writes DIR/reference.md (catalog, orders, map legend) and a report
//   playtest turn   DIR [ORDERS] [hours] applies the orders (a file, default DIR/orders.txt), plays (default 12 game
//                                       hours), saves, writes DIR/report-NNN.md and prints it
//   playtest report DIR                 prints the current report again (no time passes)
//   playtest probe  DIR X Y             a column top to bottom (what digging there finds; no time passes)
//   playtest export DIR [NAME]          copies the game into the game's saves (Load game in the main menu shows it)
//
// The game lives in DIR/game.colony (an ordinary save: a snapshot of the whole state). Each turn loads it, which
// settles the game as a save does (colonists put down what they carry and pick work again): like autosaving twice
// a day. DIR/log.md keeps every order with what the game answered.
using System.Text;
using Colony.Core;
using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Nature;
using Colony.Core.Net;
using Colony.Core.Orders;
using Colony.Core.World;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length < 2)
{
    Console.WriteLine("usage: playtest new|turn|report|probe|export DIR ...  (see the top of Program.cs)");
    return 1;
}
var dir = Path.GetFullPath(args[1]);
var game = Path.Combine(dir, "game.colony");
try
{
    switch (args[0])
    {
        case "new":
        {
            Directory.CreateDirectory(dir);
            int seed = args.Length > 2 ? int.Parse(args[2]) : 12345, size = args.Length > 3 ? int.Parse(args[3]) : 512;
            var settings = new GameSettings(seed, size, size, 384, 7, 1);
            var sim = settings.Create();
            var meta = new Meta { Turn = 0, LastTick = sim.Tick, CenterX = sim.Terrain!.SpawnX, CenterY = sim.Terrain.SpawnY };
            File.WriteAllText(Path.Combine(dir, "reference.md"), Reference.Text(sim));
            File.WriteAllText(Path.Combine(dir, "log.md"), $"# Playtest log (seed {seed}, {size}x{size})\n\n");
            if (!File.Exists(Path.Combine(dir, "orders.txt"))) File.WriteAllText(Path.Combine(dir, "orders.txt"), "# orders for the next turn, one per line (see reference.md)\n");
            Finish(sim, settings, meta, "The founders have landed. Nothing has happened yet.");
            break;
        }
        case "turn":
        {
            var (sim, settings, meta) = Load();
            string ordersFile = args.Length > 2 && args[2] != "-" ? args[2] : Path.Combine(dir, "orders.txt");
            double hours = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 12;
            var lines = File.Exists(ordersFile) ? File.ReadAllLines(ordersFile) : [];
            var answers = new StringBuilder();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                string answer;
                try { answer = Orders.Apply(sim, meta, line); }
                catch (Exception e) { answer = $"error: {e.Message}"; }
                answers.AppendLine($"> {line}\n  {answer.Replace("\n", "\n  ")}");
            }
            meta.LastTick = sim.Tick;
            int ticks = (int)(hours / 24 * Needs.Day);
            var time = new Dictionary<string, int>();
            for (int t = 0; t < ticks; t++)
            {
                sim.Step();
                if (t % 50 != 0) continue; // what everyone is doing, sampled
                foreach (var c in sim.Colonists)
                    if (!c.IsChild) time[Activity(c)] = time.GetValueOrDefault(Activity(c)) + 1;
            }
            Report.TimeSpent = time;
            meta.Turn++;
            File.AppendAllText(Path.Combine(dir, "log.md"),
                $"## Turn {meta.Turn} (day {sim.Tick / Needs.Day + 1}, played {hours:0.#} h)\n\n```\n{(answers.Length == 0 ? "(no orders)\n" : answers.ToString())}```\n\n");
            if (ordersFile == Path.Combine(dir, "orders.txt")) File.WriteAllText(ordersFile, "# orders for the next turn, one per line (see reference.md)\n");
            Finish(sim, settings, meta, answers.Length == 0 ? "(no orders)" : answers.ToString().TrimEnd());
            break;
        }
        case "report":
        {
            var (sim, _, meta) = Load();
            Console.WriteLine(Report.Text(sim, meta, "(report only: no time passed)"));
            break;
        }
        case "probe":
        {
            var (sim, _, _) = Load();
            Console.WriteLine(Report.Probe(sim, int.Parse(args[2]), int.Parse(args[3])));
            break;
        }
        case "export":
        {
            var name = args.Length > 2 ? args[2] : "Playtest " + Path.GetFileName(dir);
            var saves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Godot", "app_userdata", "Colony Game", "saves");
            Directory.CreateDirectory(saves);
            SaveGame save;
            using (var f = File.OpenRead(game)) save = SaveGame.Read(f);
            save = save with { Name = name };
            var target = Path.Combine(saves, string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_')) + ".colony");
            using (var f = File.Create(target)) save.Write(f);
            Console.WriteLine($"Saved as \"{name}\": {target} (main menu, Load game)");
            break;
        }
        default:
            Console.WriteLine($"unknown command {args[0]}");
            return 1;
    }
}
catch (Exception e)
{
    Console.WriteLine($"playtest error: {e.Message}");
    return 1;
}
return 0;

static string Activity(Colonist c) => c.Downed ? "struck down" : c.Task switch
{
    ColonistTask.Sleep => "sleeping",
    ColonistTask.Eat => "eating",
    ColonistTask.Designation or ColonistTask.ScaffoldFetch => c.CurrentWork is { } w ? Colonist.WorkNames[(int)w].ToLowerInvariant() : "digging/felling/farming",
    ColonistTask.Build => "building",
    ColonistTask.CraftFetch or ColonistTask.CraftWork or ColonistTask.CraftStore => "factory work",
    ColonistTask.HaulPickup or ColonistTask.HaulDeliver => "hauling",
    ColonistTask.FurnishPickup or ColonistTask.FurnishDeliver => "furnishing homes",
    ColonistTask.TradePickup or ColonistTask.TradeDeliver => "trading",
    ColonistTask.Equip or ColonistTask.Fight or ColonistTask.Flee or ColonistTask.Rally => "fighting",
    _ => "idle",
};

(ColonySim, GameSettings, Meta) Load()
{
    if (!File.Exists(game)) throw new FileNotFoundException($"no game in {dir}: run 'playtest new {dir}' first");
    SaveGame save;
    using (var f = File.OpenRead(game)) save = SaveGame.Read(f);
    var sim = save.Load();
    return (sim, save.Settings, Meta.Read(Path.Combine(dir, "meta.txt")));
}

void Finish(ColonySim sim, GameSettings settings, Meta meta, string answers)
{
    var report = Report.Text(sim, meta, answers);
    var save = SaveGame.Of(sim, settings, "Playtest", 0); // settles: the next turn starts from exactly this state
    using (var f = File.Create(game)) save.Write(f);
    meta.Write(Path.Combine(dir, "meta.txt"));
    File.WriteAllText(Path.Combine(dir, $"report-{meta.Turn:000}.md"), report);
    Console.WriteLine(report);
}

/// <summary>What the playtest remembers between turns besides the game: the turn, where the map looks, since when events count.</summary>
sealed class Meta
{
    public int Turn;
    public long LastTick;
    public int CenterX, CenterY;
    /// <summary>The level the map shows (-1: the surface, top-down).</summary>
    public int ViewZ = -1;

    public static Meta Read(string path)
    {
        var m = new Meta();
        foreach (var line in File.ReadAllLines(path))
        {
            var kv = line.Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0])
            {
                case "turn": m.Turn = int.Parse(kv[1]); break;
                case "last": m.LastTick = long.Parse(kv[1]); break;
                case "cx": m.CenterX = int.Parse(kv[1]); break;
                case "cy": m.CenterY = int.Parse(kv[1]); break;
                case "vz": m.ViewZ = int.Parse(kv[1]); break;
            }
        }
        return m;
    }

    public void Write(string path) => File.WriteAllText(path, $"turn={Turn}\nlast={LastTick}\ncx={CenterX}\ncy={CenterY}\nvz={ViewZ}\n");
}

/// <summary>
/// The order language: one order a line, coordinates in tiles. A "level" z is the open cell a colonist stands in
/// (the ground under it is z-1); the report gives the landing level and a height map. Every order becomes the same
/// command a player's click would issue, and its answer is the game's own message.
/// </summary>
static class Orders
{
    public static string Apply(ColonySim sim, Meta meta, string line)
    {
        var w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var opts = w.Where(t => t.Contains('=')).ToDictionary(t => t.Split('=')[0].ToLowerInvariant(), t => t.Split('=', 2)[1]);
        var a = w.Where(t => !t.Contains('=')).ToArray();
        int I(int i) => int.Parse(a[i]);
        int? Opt(string k) => opts.TryGetValue(k, out var v) ? int.Parse(v) : null;
        string Run(Command c) => sim.Commands.Execute(c);
        switch (a[0].ToLowerInvariant())
        {
            case "wait": return "ok";
            case "view":
                meta.CenterX = I(1);
                meta.CenterY = I(2);
                meta.ViewZ = a.Length > 3 ? I(3) : -1;
                return meta.ViewZ < 0 ? $"map centred on {meta.CenterX},{meta.CenterY} (surface)" : $"map centred on {meta.CenterX},{meta.CenterY}, showing level {meta.ViewZ}";
            case "dig" or "updown" or "upstair" or "downstair":
            {
                var kind = a[0] switch { "updown" => DesignationKind.UpDownStair, "upstair" => DesignationKind.UpStair, "downstair" => DesignationKind.DownStair, _ => DesignationKind.Dig };
                int z1 = I(5), z2 = a.Length > 6 ? I(6) : z1;
                return Run(new DesignateBox(new TilePos(I(1), I(2), z1), new TilePos(I(3), I(4), z2), kind));
            }
            case "stairs": // stairs X Y TOPLEVEL BOTTOMLEVEL: down at the top, up/down between, up at the bottom
                return Run(new DesignateStairs(new TilePos(I(1), I(2), Math.Min(I(3), I(4))), new TilePos(I(1), I(2), Math.Max(I(3), I(4)))));
            case "chop": return Run(new DesignatePlants(DesignationKind.Chop, I(1), I(2), I(3), I(4)));
            case "gather": return Run(new DesignatePlants(DesignationKind.Gather, I(1), I(2), I(3), I(4)));
            case "stockpile": { var (p, q) = Ground(sim, a, opts); return Run(new AddStockpile(p, q)); }
            case "farm":
            {
                var crop = a[1] switch { "wheat" or "grain" => PlantKind.Wheat, "herbs" => PlantKind.Herbs, "flax" => PlantKind.Flax, "glowcaps" or "glowcap" => PlantKind.Glowcap, _ => throw new ArgumentException("crop is wheat, herbs, flax or glowcaps") };
                var (p, q) = Ground(sim, a[1..], opts);
                return Run(new AddFarm(crop, p, q));
            }
            case "room":
            {
                var id = a[1];
                if (id != Catalog.Home.Id && !Catalog.Workshops.Any(x => x.Id == id)) throw new ArgumentException($"no room type '{id}' (see reference.md)");
                var (p, q) = Ground(sim, a[1..], opts);
                return Run(new PaintRoom(id, p, q));
            }
            case "home": { var (p, q) = Ground(sim, a, opts); return Run(new PaintRoom(Catalog.Home.Id, p, q)); }
            case "part":
            {
                int x = I(2), y = I(3);
                var at = new TilePos(x, y, Opt("z") ?? Level(sim, x, y));
                return Run(new PlacePart(a[1], at, opts.ContainsKey("rot") || a.Contains("rot"), Opt("size") ?? 1));
            }
            case "recipe": // recipe X Y OUTPUT [z=]
            {
                int x = I(1), y = I(2);
                var at = new TilePos(x, y, Opt("z") ?? Level(sim, x, y));
                if (sim.Rooms.PartAt(at) is not { Role: PartRole.Machine } m) return "No machine there";
                string want = a[3];
                for (int k = 0; k < 40 && m.Recipe?.Output.Id != want; k++) Run(new CycleRecipe(at, false));
                return m.Recipe?.Output.Id == want ? $"{m.Name} now makes {m.Recipe.Name.ToLowerInvariant()}: {m.Recipe.Describe()}"
                    : $"{m.Name} can't make {want}: it makes {string.Join(", ", m.Type.Recipes.Select(r => r.Output.Id))}";
            }
            case "limit": // limit X Y N [z=]: the machine there stops once the colony holds N of its product (0: no limit)
                return Run(new SetMachineLimit(new TilePos(I(1), I(2), Opt("z") ?? Level(sim, I(1), I(2))), I(3)));
            case "walls":
            {
                int mat = Material(opts.GetValueOrDefault("material", "log"), WallMaterials.All.Select(m => m.Name));
                var (p, q) = Ground(sim, a, opts);
                return Run(new PlaceOutline(p, q, mat));
            }
            case "floor" or "roof":
            {
                var names = a[0] == "floor" ? FloorMaterials.All.Select(m => m.Name) : RoofMaterials.All.Select(m => m.Name);
                int mat = Math.Max(1, Material(opts.GetValueOrDefault("material", ""), names));
                var (p, q) = Ground(sim, a, opts);
                return Run(new Covers(a[0] == "floor" ? StructureKind.Floor : StructureKind.Roof, p, q, mat, false));
            }
            case "door" or "window": // door X Y east|west|north|south [z=LEVEL]: on that edge of the cell
            {
                int x = I(1), y = I(2), z = Opt("z") ?? Level(sim, I(1), I(2));
                var (cell, side) = a[3].ToLowerInvariant() switch
                {
                    "east" or "e" => (new TilePos(x, y, z), EdgeInfo.East),
                    "north" or "n" => (new TilePos(x, y, z), EdgeInfo.North),
                    "west" or "w" => (new TilePos(x - 1, y, z), EdgeInfo.East),
                    "south" or "s" => (new TilePos(x, y - 1, z), EdgeInfo.North),
                    _ => throw new ArgumentException("side is east, west, north or south"),
                };
                int mat = Material(opts.GetValueOrDefault("material", ""), WallMaterials.All.Select(m => m.Name));
                return Run(new PlaceEdge(a[0] == "door" ? StructureKind.Door : StructureKind.Window, cell, side, mat));
            }
            case "erase": { var (p, q) = Ground(sim, a, opts); return Run(new EraseRoom(p, q)); }
            case "cancel": { var (p, q) = Ground(sim, a, opts, spread: 3); return Run(new CancelOrders(p, q)); }
            case "demolish": { var (p, q) = Ground(sim, a, opts); return Run(new Demolish(p, q)); }
            case "priority": // priority NAME|all WORK N
            {
                if (!Enum.TryParse<Work>(a[2], true, out var work)) throw new ArgumentException($"work is one of {string.Join(", ", Colonist.WorkNames)}");
                var who = sim.Colonists.Where(c => a[1] == "all" || c.Name.Equals(a[1], StringComparison.OrdinalIgnoreCase)).ToList();
                if (who.Count == 0) return $"nobody called {a[1]}";
                foreach (var c in who) Run(new SetWorkPriority(c.Id, work, I(3)));
                return $"{work} priority {I(3)} for {(a[1] == "all" ? $"all {who.Count}" : who[0].Name)}";
            }
            case "policy": // policy X Y INDEX: a home's furnishing policy
                return Run(new SetHomePolicy(new TilePos(I(1), I(2), Opt("z") ?? Level(sim, I(1), I(2))), I(3)));
            case "draft": return Run(new SetDrafted(-1, a.Length < 2 || a[1] != "off"));
            case "attack": return Run(new SquadAttack(I(1)));
            case "march": return Run(new SquadMove(new TilePos(I(1), I(2), Opt("z") ?? Level(sim, I(1), I(2)))));
            default: throw new ArgumentException($"unknown order '{a[0]}' (see reference.md)");
        }
    }

    /// <summary>The ground level (top solid tile) of a column, and the level a colonist stands on there.</summary>
    public static int Ground(ColonySim sim, int x, int y) => sim.World.SurfaceLevel(Math.Clamp(x, 0, sim.World.Width - 1), Math.Clamp(y, 0, sim.World.Depth - 1));
    public static int Level(ColonySim sim, int x, int y) => Ground(sim, x, y) + 1;

    /// <summary>
    /// A box of ground tiles for zones, rooms, walls and covers: X1 Y1 X2 Y2, on the ground levels the box spans
    /// (z=LEVEL: one level only, e.g. a dug hall's floor).
    /// </summary>
    private static (TilePos, TilePos) Ground(ColonySim sim, string[] a, Dictionary<string, string> opts, int spread = 0)
    {
        int x1 = int.Parse(a[1]), y1 = int.Parse(a[2]), x2 = int.Parse(a[3]), y2 = int.Parse(a[4]);
        if (opts.TryGetValue("z", out var zs))
        {
            int g = int.Parse(zs) - 1;
            return (new TilePos(x1, y1, g - spread), new TilePos(x2, y2, g + spread));
        }
        int lo = int.MaxValue, hi = int.MinValue;
        for (int y = Math.Min(y1, y2); y <= Math.Max(y1, y2); y++)
        for (int x = Math.Min(x1, x2); x <= Math.Max(x1, x2); x++)
        {
            int g = Ground(sim, x, y);
            lo = Math.Min(lo, g);
            hi = Math.Max(hi, g);
        }
        return (new TilePos(x1, y1, lo - spread), new TilePos(x2, y2, hi + spread));
    }

    private static int Material(string name, IEnumerable<string> names)
    {
        var list = names.ToList();
        if (name.Length == 0) return 1;
        int i = list.FindIndex(n => n.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) throw new ArgumentException($"material is one of {string.Join(", ", list)}");
        return i;
    }
}

/// <summary>What the playtester sees after a turn: the colony in numbers and words, and the map.</summary>
static class Report
{
    private const int MapW = 64, MapH = 30;
    /// <summary>What the grown colonists did during the turn (samples per activity), set by the turn.</summary>
    public static Dictionary<string, int>? TimeSpent;

    public static string Text(ColonySim sim, Meta meta, string answers)
    {
        var s = new StringBuilder();
        long t = sim.Tick;
        int day = (int)(t / Needs.Day) + 1, hour = (int)(t % Needs.Day * 24 / Needs.Day);
        var land = sim.Terrain is { } tr ? (tr.SpawnX, tr.SpawnY) : (meta.CenterX, meta.CenterY);
        int landLevel = Orders.Level(sim, land.Item1, land.Item2);
        s.AppendLine($"# Turn {meta.Turn}: day {day}, {hour:00}:00");
        s.AppendLine();
        s.AppendLine("## Your orders this turn");
        s.AppendLine("```");
        s.AppendLine(answers);
        s.AppendLine("```");

        var people = sim.Colonists.ToList();
        var society = sim.Society;
        s.AppendLine();
        s.AppendLine("## Colony");
        s.AppendLine($"- People: {people.Count} ({people.Count(c => c.IsChild)} children), mood {society.Happiness:P0}, " +
                     $"homeless {people.Count(c => c.Home == null && !c.IsAutomaton)}, hungry {people.Count(c => c.Hungry)}, starving {people.Count(c => c.Starving)}, tired {people.Count(c => c.Tired)}");
        s.AppendLine($"- Food: {Society.FoodInStore(sim)} meals = {Society.FoodDays(sim):0.0} days for everyone (a colonist eats 3 meals a day)");
        s.AppendLine($"- Arrived {society.SettlersArrived} settlers, {society.Births} births, {society.Departed} left, {sim.Deaths} died; living standard {society.Standard:P0}");
        // Why the living standard is what it is: each need's share met yesterday, and what meets it.
        var needs = Needs.Goods.Select((g, k) =>
        {
            var goods = Catalog.Items.Where(i => i.Need == g.Need).Select(i => i.Id).ToList();
            return $"{g.Need} {society.Met[k]:P0} met ({g.PerDay:0.##} a head a day of {(goods.Count == 0 ? "nothing yet" : string.Join("/", goods))})";
        });
        s.AppendLine($"- Living standard needs (used up daily from stores): {string.Join("; ", needs)}");
        int waveDay = Balance.FirstSettlerDay;
        while (waveDay <= sim.Society.Days) waveDay += Balance.SettlerWaveDays;
        s.AppendLine($"- Settlers: next wave on day {waveDay + 1}, if there are 3+ days of food and under a tenth starving; " +
                     $"free beds for settlers {sim.Housing.FreeBeds} (family homes keep their spare beds for children)");
        s.AppendLine($"- Landing at {land.Item1},{land.Item2}, level {landLevel}");
        var totals = sim.Items.Totals();
        var stocks = Enumerable.Range(0, totals.Length).Where(i => totals[i] > 0).Select(i => (Type: new ItemType((ushort)i), N: totals[i])).OrderByDescending(e => e.N).ToList();
        s.AppendLine($"- Goods: {(stocks.Count == 0 ? "none" : string.Join(", ", stocks.Select(e => $"{e.Type.Id} {e.N}")))}");
        int loose = sim.Items.All.Where(e => !sim.Stockpiles.Contains(e.Key) && sim.Rooms.PartAt(e.Key) == null).Sum(e => e.Value.Count);
        s.AppendLine($"- Stockpile cells {sim.Stockpiles.Count}; lying loose (not in a stockpile or crate) {loose}");

        if (TimeSpent is { Count: > 0 } time)
        {
            int all = time.Values.Sum();
            s.AppendLine($"- Time spent this turn (grown colonists): {string.Join(", ", time.OrderByDescending(e => e.Value).Select(e => $"{e.Key} {100.0 * e.Value / all:0}%"))}");
        }

        s.AppendLine();
        s.AppendLine("## People");
        foreach (var c in people)
        {
            var flags = new List<string>();
            if (c.IsChild) flags.Add("child");
            if (c.Starving) flags.Add("STARVING");
            else if (c.Hungry) flags.Add("hungry");
            if (c.Tired) flags.Add("tired");
            if (c.Downed) flags.Add("DOWN");
            if (c.Drafted) flags.Add("drafted");
            if (c.Home == null) flags.Add("homeless");
            s.AppendLine($"- {c.Name} ({Catalog.EducationName(c.Education)}, mood {c.Happiness:P0}{(flags.Count > 0 ? ", " + string.Join(", ", flags) : "")}) at {c.Position.X},{c.Position.Y},{c.Position.Z}: {c.Describe()}");
        }

        s.AppendLine();
        s.AppendLine("## Orders, rooms and buildings");
        var orderCounts = sim.Designations.All.GroupBy(e => e.Value).Select(g => $"{g.Key} {g.Count()}").ToList();
        s.AppendLine($"- Open orders: {(orderCounts.Count == 0 ? "none" : string.Join(", ", orderCounts))}");
        // Orders nobody can get to: no standable cell beside them in any colonist's connected area.
        var reach = sim.Colonists.Select(c => sim.Connectivity.ComponentOf(c.Position)).Where(k => k >= 0).ToHashSet();
        var stuck = sim.Designations.All.Keys.Where(p =>
        {
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if ((dx | dy | dz) != 0 && reach.Contains(sim.Connectivity.ComponentOf(p.Offset(dx, dy, dz)))) return false;
            return true;
        }).ToList();
        if (stuck.Count > 0)
            s.AppendLine($"- UNREACHABLE: {stuck.Count} orders have no reachable cell beside them (e.g. at {string.Join("; ", stuck.Take(3).Select(p => $"{p.X},{p.Y},{p.Z}"))}): " +
                         "colonists can't get there (stairs connect only straight up/down and orthogonally, not diagonally)");
        foreach (var room in sim.Rooms.All)
        {
            var home = sim.Housing.HomeOf(room);
            var box = Bounds(room.Cells);
            string where = $"{box.x1},{box.y1}..{box.x2},{box.y2} level {box.z}";
            if (home != null)
            {
                string family = home.Family != 0 ? $", family home of {string.Join(" & ", home.Residents.Where(r => r.Household == home.Family && !r.IsChild).Select(r => r.Name))} (spare beds kept for children)" : "";
                s.AppendLine($"- {home.Name} ({home.Policy.Name}) at {where}: {room.Cells.Count} cells, beds {home.Beds}, residents {home.Residents.Count}, fixtures {home.Fixtures.Count}{family}");
                // What it still wants, and whether the stores have it (the policy's tier exactly).
                var wants = sim.Housing.Demands().Where(d => d.Home == home).Select(d =>
                {
                    var fits = Catalog.Fixtures[Housing.Slots[d.Slot]].Where(i => i.Def.FixtureTier == d.Tier).ToList();
                    int inStore = fits.Sum(i => totals[i.Index]);
                    return $"{d.Missing} {Housing.Slots[d.Slot]} of tier {d.Tier} ({string.Join("/", fits.Select(i => i.Id))}: {inStore} in stock)";
                }).ToList();
                if (wants.Count > 0) s.AppendLine($"  - wants: {string.Join("; ", wants)}");
                continue;
            }
            s.AppendLine($"- {room.Name} at {where}: {room.Cells.Count} cells, efficiency {room.Efficiency:P0}; status: {Rooms.Status(room, sim)}");
            foreach (var p in room.Parts)
            {
                string state = p.Built ? "built" : $"BLUEPRINT (needs {p.Cost.Count} {p.Cost.Material.Id} and a builder)";
                string recipe = p.Role == PartRole.Machine && p.Recipe is { } r ? $", makes {r.Output.Id}: {r.Describe()}{(p.Limit > 0 ? $", limit {p.Limit} (stock {Rooms.StockOf(sim, r.Output, 0)})" : "")}{(p.ClaimedBy >= 0 ? " (being worked)" : "")}" : "";
                string holds = p.Built && p.Role is PartRole.Input or PartRole.Output
                    ? sim.Items.TryGet(p.Cells[0], out var pile) ? $", holds {pile.Count} {pile.Type.Id}" : ", empty" : "";
                s.AppendLine($"  - {p.Name} [{p.Type.Id}] at {p.Origin.X},{p.Origin.Y}: {state}{recipe}{holds}");
            }
        }
        var structures = sim.Constructions.All.GroupBy(b => b.Kind).Select(g => $"{g.Key} {g.Count()}").ToList();
        if (structures.Count > 0) s.AppendLine($"- Structures waiting to be built: {string.Join(", ", structures)}");
        var farms = sim.Farms.Cells.Count();
        if (farms > 0)
        {
            int sown = sim.Farms.Cells.Count(c => sim.Flora.TryGet(c, out _));
            int ripe = sim.Farms.Cells.Count(c => sim.Flora.TryGet(c, out var p) && p.Growth >= 255);
            s.AppendLine($"- Farm plots {farms}: {sown} sown, {ripe} ripe");
        }

        var nests = sim.Wildlife.Nests.Select(n => (Nest: n, D: Math.Max(Math.Abs(n.Entrance.X - land.Item1), Math.Abs(n.Entrance.Y - land.Item2))))
            .Where(e => e.D <= 160).OrderBy(e => e.D).ToList();
        s.AppendLine();
        s.AppendLine("## Threats");
        if (nests.Count == 0) s.AppendLine("- No ant nests within 160 tiles.");
        foreach (var (n, d) in nests)
            s.AppendLine($"- Ant nest #{n.Id} at {n.Entrance.X},{n.Entrance.Y} ({d} tiles away): {n.Total} ants, " +
                         $"{(((n.Crowded >> 0) & 1) != 0 ? "RAIDING (your land meets theirs)" : n.HostileTo(0, sim.Tick) ? "ANGRY" : "calm")}");
        int ants = sim.Wildlife.Creatures.Count(c => c.Fighting);
        if (ants > 0) s.AppendLine($"- {ants} ants are fighting right now.");

        s.AppendLine();
        s.AppendLine("## What happened");
        var events = sim.Events.Where(e => e.Tick >= meta.LastTick && e.Faction is -1 or 0).Select(e => e.Text).ToList();
        if (events.Count == 0) s.AppendLine("- Nothing worth noting.");
        foreach (var e in events.TakeLast(25)) s.AppendLine($"- {e}");

        s.AppendLine();
        s.Append(Map(sim, meta, landLevel));
        return s.ToString();
    }

    private static (int x1, int y1, int x2, int y2, int z) Bounds(IEnumerable<TilePos> cells)
    {
        var l = cells.ToList();
        return (l.Min(c => c.X), l.Min(c => c.Y), l.Max(c => c.X), l.Max(c => c.Y), l.GroupBy(c => c.Z).OrderByDescending(g => g.Count()).First().Key);
    }

    /// <summary>The map round the view centre: top-down surface (or one level, DF-style), with a height map for the surface.</summary>
    private static string Map(ColonySim sim, Meta meta, int landLevel)
    {
        var w = sim.World;
        int x0 = Math.Clamp(meta.CenterX - MapW / 2, 0, w.Width - MapW), y0 = Math.Clamp(meta.CenterY - MapH / 2, 0, w.Depth - MapH);
        var people = sim.Colonists.Where(c => !c.Dead).GroupBy(c => (c.Position.X, c.Position.Y)).ToDictionary(g => g.Key, g => g.Select(c => c.Position.Z).ToList());
        var ants = sim.Wildlife.Creatures.Where(c => !c.Dead).GroupBy(c => (c.Position.X, c.Position.Y)).ToDictionary(g => g.Key, g => g.Select(c => c.Position.Z).ToList());
        var nests = sim.Wildlife.Nests.ToDictionary(n => (n.Entrance.X, n.Entrance.Y));
        var orders = new Dictionary<(int, int), List<(int Z, DesignationKind K)>>();
        foreach (var (p, k) in sim.Designations.All)
        {
            if (!orders.TryGetValue((p.X, p.Y), out var list)) orders[(p.X, p.Y)] = list = new();
            list.Add((p.Z, k));
        }
        var sites = sim.Constructions.All.GroupBy(b => (b.Cell.X, b.Cell.Y)).ToDictionary(g => g.Key, g => g.Select(b => b.Cell.Z).ToList());
        bool surface = meta.ViewZ < 0;

        char Cell(int x, int y)
        {
            int level = surface ? Orders.Level(sim, x, y) : meta.ViewZ;
            bool Near(int z) => surface ? Math.Abs(z - level) <= 1 : z == level;
            var at = new TilePos(x, y, level);
            if (people.TryGetValue((x, y), out var pz) && pz.Any(Near)) return '@';
            if (nests.TryGetValue((x, y), out var nest) && Near(nest.Entrance.Z)) return 'N';
            if (ants.TryGetValue((x, y), out var az) && az.Any(Near)) return 'a';
            if (orders.TryGetValue((x, y), out var ol))
                foreach (var (z, k) in ol)
                    if (Near(z) || surface && k is DesignationKind.Dig && z >= level - 2)
                        return k switch { DesignationKind.Dig => 'x', DesignationKind.Chop => 'c', DesignationKind.Gather => 'g', DesignationKind.Sow => 'f', _ => '>' };
            if (sites.TryGetValue((x, y), out var sz) && sz.Any(Near)) return 'b';
            if (sim.Rooms.PartAt(at) is { } part)
                return !part.Built ? 'b' : part.Role switch { PartRole.Machine => 'M', PartRole.Input => 'i', PartRole.Output => 'o', _ => 'k' };
            if (sim.Items.TryGet(at, out _)) return '*';
            if (sim.Rooms.RoomAt(at) is { } room) return sim.Housing.HomeOf(room) != null ? 'h' : 'w';
            if (sim.Stockpiles.Contains(at)) return 's';
            if (sim.Farms.Contains(at)) return 'f';
            if (sim.Water.MassAt(at) >= Colony.Core.Fluids.Water.Full / 4) return '~';
            var tile = w.Get(x, y, level);
            if (!surface)
            {
                if (TileInfo.IsStair(tile)) return '>';
                if (TileInfo.IsSolid(tile)) return TileInfo.IsMineral(tile) ? 'o' : tile is TileType.Clay ? 'C' : tile is TileType.Sand ? '_' : '#';
                if (level > 0 && !TileInfo.IsSolid(w.Get(x, y, level - 1)) && !TileInfo.IsStair(w.Get(x, y, level - 1))) return ' ';
            }
            if (sim.Flora.TryGet(at, out var plant))
                return PlantInfo.IsTree(plant.Kind) ? 'T' : PlantInfo.IsGatherable(plant) ? '"' : ',';
            var ground = w.Get(x, y, level - 1);
            if (!surface && TileInfo.IsSolid(ground) && ground is not (TileType.Grass or TileType.Soil)) return '+'; // floor (dug or natural rock), not a wall
            return ground switch
            {
                TileType.Grass => '.',
                TileType.Soil => ':',
                TileType.Sand => '_',
                TileType.Clay => 'C',
                _ when TileInfo.IsStair(ground) => '>',
                _ when TileInfo.IsMineral(ground) => 'o',
                _ => '#',
            };
        }

        char Height(int x, int y)
        {
            if (sim.Water.MassAt(new TilePos(x, y, Orders.Level(sim, x, y))) >= Colony.Core.Fluids.Water.Full / 4) return '~';
            int d = Orders.Level(sim, x, y) - landLevel;
            if (d == 0) return '0';
            if (d > 0) return d <= 9 ? (char)('0' + d) : d <= 35 ? (char)('A' + d - 10) : '+';
            return -d <= 26 ? (char)('a' - d - 1) : '-';
        }

        var s = new StringBuilder();
        string Ruler()
        {
            var r = new StringBuilder("      ");
            for (int x = x0; x < x0 + MapW; x++) r.Append(x % 10 == 0 ? '|' : ' ');
            r.Append("\n      ");
            for (int x = x0; x < x0 + MapW; x++)
            {
                if (x % 10 == 0) { var label = x.ToString(); r.Append(label); x += label.Length - 1; }
                else r.Append(' ');
            }
            return r.ToString();
        }
        void Grid(Func<int, int, char> f)
        {
            s.AppendLine("```");
            s.AppendLine(Ruler());
            for (int y = y0 + MapH - 1; y >= y0; y--) // north (+y) at the top
            {
                s.Append($"{y,5} ");
                for (int x = x0; x < x0 + MapW; x++) s.Append(f(x, y));
                s.AppendLine();
            }
            s.AppendLine("```");
        }
        s.AppendLine(surface
            ? $"## Map (surface, x {x0}..{x0 + MapW - 1}, y {y0}..{y0 + MapH - 1}; north up)"
            : $"## Map (level {meta.ViewZ}, x {x0}..{x0 + MapW - 1}, y {y0}..{y0 + MapH - 1}; north up)");
        s.AppendLine("Legend: @ colonist  a ant  N nest  x dig order  c chop  g gather  > stair  b blueprint  M machine  i/o input/output crate  k other part  " +
                     "* goods  w workshop  h home  s stockpile  f farm  ~ water  T tree  \" ripe plant  , plant  . grass  : soil  _ sand  C clay  o ore  # rock" +
                     (surface ? "" : "  + rock floor (walkable)  (blank: open air, no floor)"));
        Grid(Cell);
        if (surface)
        {
            s.AppendLine();
            s.AppendLine($"## Heights (standing level minus the landing level {landLevel}: 0 = same, 1-9 and A-Z = higher, a-z = lower, ~ water)");
            Grid(Height);
        }
        return s.ToString();
    }

    /// <summary>A column from just above the ground down: what digging finds there.</summary>
    public static string Probe(ColonySim sim, int x, int y)
    {
        var w = sim.World;
        int top = Orders.Ground(sim, x, y);
        var s = new StringBuilder($"Column {x},{y}: ground (top solid tile) at z {top}, standing level {top + 1}\n");
        for (int z = top + 3; z >= Math.Max(0, top - 30); z--)
        {
            var t = w.Get(x, y, z);
            s.Append($"  z {z,4}: {t}");
            if (sim.Water.MassAt(new TilePos(x, y, z)) > 0) s.Append(" (water)");
            if (sim.Flora.TryGet(new TilePos(x, y, z), out var p)) s.Append($" ({PlantInfo.Name(p.Kind)})");
            s.AppendLine();
        }
        return s.ToString();
    }
}

/// <summary>The playtester's reference: the order language, the map, and what can be built and made (from the catalog).</summary>
static class Reference
{
    public static string Text(ColonySim sim)
    {
        var s = new StringBuilder();
        s.AppendLine("# Colony playtest reference");
        s.AppendLine();
        s.AppendLine("""
            ## Orders (one per line in orders.txt; `#` starts a comment)

            Coordinates are tiles. x grows east, y grows north. A *level* z is the open cell a colonist stands in (its ground
            is z-1). The report gives the landing level; the height map shows every column's level relative to it, and
            `probe X Y` lists a column's tiles. Boxes are inclusive.

            | Order | Does |
            |---|---|
            | `dig X1 Y1 X2 Y2 Z1 [Z2]` | dig out every solid tile in the box on levels Z1..Z2 (tunnels, halls: dig at the level you want to walk) |
            | `stairs X Y TOP BOTTOM` | a staircase down through levels (down stair on top, up/down between, up at the bottom) |
            | `updown/upstair/downstair X1 Y1 X2 Y2 Z1 [Z2]` | carve that stair kind in a box |
            | `chop X1 Y1 X2 Y2` | fell every tree in the area (logs) |
            | `gather X1 Y1 X2 Y2` | gather ripe plants in the area (berries = food, fiber, herbs) |
            | `stockpile X1 Y1 X2 Y2 [z=LEVEL]` | zone a stockpile on the floor (haulers store goods there) |
            | `farm wheat|herbs|flax|glowcaps X1 Y1 X2 Y2 [z=LEVEL]` | farm plots (wheat, herbs, flax = fiber, on grass or soil; glowcaps on any floor under a roof) |
            | `room ROOMTYPE X1 Y1 X2 Y2 [z=LEVEL]` | paint a workshop room over the floor (room types below) |
            | `home X1 Y1 X2 Y2 [z=LEVEL]` | paint a home (4+ cells; beds and furniture are brought by haulers per its policy) |
            | `part PARTID X Y [size=1..5] [rot] [z=LEVEL]` | place a station/crate/tool in a room (a blueprint: builders build it from delivered materials) |
            | `recipe X Y OUTPUTID [z=LEVEL]` | set the machine covering X Y to a recipe |
            | `limit X Y N [z=LEVEL]` | the machine stops starting batches once the colony holds N of its product (0: no limit) |
            | `walls X1 Y1 X2 Y2 [material=log|rubble|plank|stone|brick]` | thin walls all round a box (homes like walls, floor and roof) |
            | `floor X1 Y1 X2 Y2 [material=plank|flagstone|tile]`, `roof ... [material=thatch|shingle|slate|clay]` | lay floors / roofs |
            | `policy X Y INDEX` | a home's furnishing policy (0 dormitory, 1 cottage, 2 house, 3 manor) |
            | `priority NAME|all WORK N` | work priority 0 (never) or 1 (first) .. 4 (last); WORK: mining, woodcutting, farming, construction, hauling, industry, trading |
            | `door X Y east|west|north|south [z=LEVEL]`, `window ...` | a door or window on that edge of the cell (cut into a wall, or on its own) |
            | `erase X1 Y1 X2 Y2 [z=LEVEL]` | remove room or home cells in the box (its parts go) |
            | `cancel X1 Y1 X2 Y2 [z=LEVEL]` | cancel designations and unbuilt plans, and remove stockpile and farm cells in the box (not rooms: use `erase`) |
            | `demolish X1 Y1 X2 Y2 [z=LEVEL]` | take down what is built there (materials are left) |
            | `draft` / `draft off` | draft every grown colonist to fight / stand them down |
            | `attack NESTID`, `march X Y` | send the drafted against a nest / to a spot |
            | `view X Y [LEVEL]` | centre the map (and show one level, DF-style, instead of the surface) |
            | `wait` | nothing (let time pass) |

            Without `z=`, zones, rooms, walls and covers take the ground levels the box spans on the surface. Inside a dug
            hall, pass `z=` with the hall's level. Parts: X Y is the part's origin cell; its footprint (W x D, rotated by
            `rot`) must lie inside one room.

            ## Tools (run from the repo root)

            - `dotnet run --project tools/Colony.Playtest -- turn DIR` applies DIR/orders.txt, plays 12 game hours, prints the report
            - `... -- turn DIR ORDERSFILE HOURS` other orders file / turn length
            - `... -- probe DIR X Y` a column's tiles (no time passes) · `... -- report DIR` the report again

            ## How the game works, briefly

            - A day is 24 game hours. Colonists eat about 3 meals a day (berries 1 meal, bread 4), sleep about a fifth of the
              day (in a bed if they have a home), and do work by their priorities: designations (dig, chop, gather, sow),
              building blueprints, hauling (stockpiles, crates, construction materials, home furnishing), factory work.
            - Work: each colonist does work types by priority (1 first .. 4 last); among work of equal priority the nearest
              job wins, so a big chop or gather order can keep everyone busy outside while a dig waits: raise what is urgent
              to priority 1 (`priority all mining 1`). New colonists prefer one kind of work (2) over the rest (3).
            - Settlers arrive in waves (day 3, then every 3 days) while there are 3+ days of food and nobody starves; free
              beds draw more. Unhappy colonists (no home, no goods) leave after 5 days.
            - Food: gather wild berries early; farm wheat (4 days), mill it to flour and bake bread (kitchen: millstone,
              oven; ovens need charcoal) or farm glowcaps under a roof (3 days, eaten raw).
            - Industry: a workshop room holds stations (machines) set to recipes, input crates (one input item each) and an
              output crate per product (a crate holds one kind of item). Haulers fill input crates; workers run the
              machines. Each room reports a status saying what it is missing. Bigger station sizes do more per worker.
              Size-1 stations are built from logs (or stone); bigger ones need planks etc. Tools/lanterns add efficiency.
            - Homes: a home room gets beds (and furniture) installed by haulers from stock: make straw pallets (fiber) or
              wooden beds (planks, cloth). Homeless colonists sleep on the floor and get unhappy.
            - Ants: nests are calm until struck or until your rooms, stockpiles or farms reach their territory; then they
              raid every few days. Draft and arm colonists (spears, axes, armour) to fight.

            ## Map legend

            @ colonist · a ant · N nest · x dig order · c chop order · g gather order · > stair · b blueprint (unbuilt part
            or structure) · M machine · i input crate · o output crate · k other part · * goods lying · w workshop floor ·
            h home floor · s stockpile · f farm plot · ~ water · T tree · " ripe plant · , unripe plant · . grass · : soil ·
            _ sand · C clay · o ore · # rock. In a level view (`view X Y LEVEL`), # o C _ are solid tiles at that level and a
            blank is open air with no floor, and + is walkable rock floor (dug out).
            """);

        s.AppendLine();
        s.AppendLine("## Rooms, stations and recipes");
        s.AppendLine();
        s.AppendLine($"Home room type id: `{Catalog.Home.Id}` (use the `home` order).");
        foreach (var ws in Catalog.Workshops)
        {
            s.AppendLine();
            s.AppendLine($"### `{ws.Id}`: {ws.Name}");
            foreach (var st in ws.Stations)
            {
                var sizes = Enumerable.Range(1, PartInfo.Sizes(st)).Select(z =>
                {
                    var (fw, fd) = PartInfo.Footprint(st, z);
                    var c = PartInfo.Cost(st, z);
                    return $"{z}: {fw}x{fd}, {c.Count} {c.Material.Id}";
                });
                s.AppendLine($"- station `{st.Id}` ({st.Name}), sizes {string.Join(" · ", sizes)}");
                foreach (var r in st.Recipes)
                    s.AppendLine($"  - `{r.Output.Id}`: {r.Describe()} ({Catalog.EducationName(r.Education)}, {r.WorkTicks} ticks of work)");
            }
        }
        s.AppendLine();
        s.AppendLine("### Parts any workshop takes");
        foreach (var p in Catalog.GenericParts)
        {
            var sizes = Enumerable.Range(1, PartInfo.Sizes(p)).Select(z =>
            {
                var (fw, fd) = PartInfo.Footprint(p, z);
                var c = PartInfo.Cost(p, z);
                return $"{z}: {fw}x{fd}, {c.Count} {c.Material.Id}";
            });
            s.AppendLine($"- `{p.Id}` ({p.Name}, {p.Role}): {string.Join(" · ", sizes)}");
        }
        s.AppendLine();
        s.AppendLine("## Items");
        s.AppendLine();
        foreach (var g in Catalog.Items.GroupBy(i => i.Category))
            s.AppendLine($"- {g.Key}: {string.Join(", ", g.Select(i => i.Nutrition > 0 ? $"`{i.Id}` ({i.Nutrition} meals)" : $"`{i.Id}`"))}");
        s.AppendLine();
        s.AppendLine($"Education levels: {string.Join(", ", Catalog.EducationLevels)}. Founders: 1 Scholar, 2 Literates, the rest unskilled.");
        return s.ToString();
    }
}
