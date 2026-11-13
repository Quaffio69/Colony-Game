// Headless simulation stress benchmark. Run in Release:
//   dotnet run -c Release --project tools/Colony.Bench -- [size=1024] [colonists=20] [ticks=6000]
// Generates a size x size x 384 world, designates a mix of unreachable work (buried under the map
// centre, scattered across the surface) and reachable work (a pit and a staircase by the colony), then steps
// the sim and reports per-phase tick costs. Watch "max" for hitches and "avg" for steady-state cost.
using System.Diagnostics;
using Colony.Core;
using Colony.Core.Navigation;
using Colony.Core.Procedural;

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
