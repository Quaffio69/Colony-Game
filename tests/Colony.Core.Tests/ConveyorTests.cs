using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.Transport;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>Conveyor belts: routing across obstacles, building on site, demand-driven loading, splitters, merges, hoppers,
/// power, blocking, removal and snapshots.</summary>
public class ConveyorTests
{
    /// <summary>Stone up to z=3: the floor cells are at z=4.</summary>
    private static ColonySim Flat(int size = 48, bool needs = false)
    {
        var w = new VoxelWorld(size, size, 14);
        for (int z = 0; z <= 3; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            w.Set(x, y, z, TileType.Stone);
        var sim = new ColonySim(w) { NeedsEnabled = needs };
        sim.Rooms.InstantBuild = true;
        return sim;
    }

    private static TilePos G(int x, int y) => new(x, y, 3);
    private static TilePos F(int x, int y, int z = 4) => new(x, y, z);

    private static void Run(ColonySim sim, int ticks, Func<bool>? until = null)
    {
        for (int i = 0; i < ticks && until?.Invoke() != true; i++) sim.Step();
    }

    /// <summary>Routes and plans a belt between two clicked cells, as the game's tool does; fails the test if it can't.</summary>
    private static Belt Lay(ColonySim sim, TilePos from, TilePos to, int faction = 0)
    {
        var a = Conveyors.PortAt(sim, faction, from, true, out var whyA);
        Assert.True(a is not null, whyA);
        var b = Conveyors.PortAt(sim, faction, to, false, out var whyB);
        Assert.True(b is not null, whyB);
        var route = ConveyorRouter.Route(sim, Conveyors.EndCells(sim, a!.Value, true), Conveyors.EndCells(sim, b!.Value, false), out _);
        Assert.NotNull(route);
        var message = sim.Commands.Execute(new PlaceConveyor(route![0], Conveyors.Encode(route), a.Value.Kind, from, b.Value.Kind, to) { Player = faction });
        Assert.StartsWith("Planned", message);
        return sim.Conveyors.All[^1];
    }

    /// <summary>Every conveyor tile planned is finished at once (as if built).</summary>
    private static void BuildAll(ColonySim sim)
    {
        foreach (var bp in sim.Constructions.All.Where(b => b.Kind == StructureKind.Conveyor).ToList()) sim.CompleteConstruction(bp);
    }

    private static int CountAt(ColonySim sim, TilePos p, ItemType t) => sim.Items.TryGet(p, out var pile) && pile.Type == t ? pile.Count : 0;

    /// <summary>A carpentry with an output crate at (8, 6) holding planks, and one far off with a joinery bench making
    /// wooden gears (it needs planks) and an input crate at (36, 6).</summary>
    private static (Part Out, Part In) TwoShops(ColonySim sim, int planks = 30)
    {
        sim.Rooms.Paint(RoomType.Carpentry, G(4, 4), G(8, 8));
        sim.Rooms.Paint(RoomType.Carpentry, G(36, 4), G(42, 8));
        var outCrate = sim.Rooms.Place(PartType.OutputCrate, F(8, 6), false)!;
        var inCrate = sim.Rooms.Place(PartType.InputCrate, F(36, 6), false)!;
        var bench = sim.Rooms.Place(Catalog.Part("carpentry.joinery_bench"), F(38, 6), false)!;
        Assert.True(sim.Rooms.SetRecipe(bench, Catalog.RecipeFor(Catalog.Item("wooden_gears"))!));
        sim.Items.Drop(F(8, 6), ItemType.Plank, planks);
        return (outCrate, inCrate);
    }

    // ---- Routing ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheRoute_GoesRoundRock_OverHills_UpCliffsAndAcrossGaps()
    {
        var sim = Flat();
        // A rock wall across the way (with a gap far off), a hill two levels up, and a trench.
        for (int y = 0; y < 40; y++)
        for (int z = 4; z <= 6; z++)
            sim.World.Set(20, y, z, TileType.Stone);
        for (int y = 20; y < 30; y++)
        for (int x = 8; x < 14; x++)
        {
            sim.World.Set(x, y, 4, TileType.Stone);
            sim.World.Set(x, y, 5, TileType.Stone);
        }
        var route = ConveyorRouter.Route(sim, [F(4, 10)], [F(30, 10)], out _);
        Assert.NotNull(route);
        Assert.All(route!, c => Assert.Null(Conveyors.Free(sim, c)));
        for (int i = 1; i < route!.Count; i++) Assert.True(Conveyors.CanStep(sim.World, route[i - 1], route[i]));
        Assert.Contains(route, c => c.Z > 4 || c.Y >= 40); // over the wall or round its end

        // Up onto the hill: ramps, not lifts, where the ground rises a level at a time.
        var up = ConveyorRouter.Route(sim, [F(4, 25)], [F(10, 25, 6)], out _);
        Assert.NotNull(up);
        Assert.Equal(F(10, 25, 6), up![^1]);

        // A sheer cliff five levels high: a lift.
        for (int y = 0; y < 48; y++)
        for (int z = 4; z <= 8; z++)
            sim.World.Set(44, y, z, TileType.Stone);
        var cliff = ConveyorRouter.Route(sim, [F(40, 44)], [F(46, 44, 9)], out _);
        Assert.NotNull(cliff);
        Assert.Contains(Enumerable.Range(1, cliff!.Count - 1), i => Conveyors.StepOf(cliff[i - 1], cliff[i]) == Conveyors.Step.Lift);
    }

    [Fact]
    public void PlacingChecksEveryTile_AndTheEnds()
    {
        var sim = Flat();
        var (outCrate, inCrate) = TwoShops(sim);
        var from = new Port(PortKind.Crate, outCrate.Cells[0]);
        var to = new Port(PortKind.Crate, inCrate.Cells[0]);
        var straight = Enumerable.Range(9, 27).Select(x => F(x, 6)).ToList();
        Assert.Null(Conveyors.CanPlace(sim, 0, straight, from, to));
        sim.World.Set(F(20, 6), TileType.Stone);
        Assert.Contains("rock", Conveyors.CanPlace(sim, 0, straight, from, to));
        sim.World.Set(F(20, 6), TileType.Air);
        Assert.Contains("gap", Conveyors.CanPlace(sim, 0, straight.Where(c => c.X != 15).ToList(), from, to));
        Assert.Contains("start", Conveyors.CanPlace(sim, 0, straight.Skip(2).ToList(), from, to));
        Assert.NotNull(Conveyors.CanPlace(sim, 1, straight, from, to)); // another colony can't use these crates
        // Starting at an input crate is refused when clicked.
        Assert.Null(Conveyors.PortAt(sim, 0, inCrate.Cells[0], true, out var why));
        Assert.Contains("output crate", why);
    }

    // ---- Moving goods -----------------------------------------------------------------------------------------

    [Fact]
    public void ABelt_FeedsAnInputCrate_OnlyWhatItsWorkshopNeeds()
    {
        var sim = Flat();
        var (outCrate, inCrate) = TwoShops(sim);
        var belt = Lay(sim, outCrate.Cells[0], inCrate.Cells[0]);
        Assert.False(belt.Running);
        Run(sim, 200);
        Assert.Equal(30, CountAt(sim, outCrate.Cells[0], ItemType.Plank)); // nothing moves until it is built
        BuildAll(sim);
        Assert.True(belt.Running);
        Run(sim, 1500);
        // Two batches of the bench's recipe (2 planks each), no more: the belt is demand-driven.
        Assert.Equal(4, CountAt(sim, inCrate.Cells[0], ItemType.Plank));
        Assert.Equal(26, CountAt(sim, outCrate.Cells[0], ItemType.Plank));
        Assert.Equal(0, belt.Packets);
        Assert.Equal(0, sim.Items.TryGet(inCrate.Cells[0], out var p) ? p.Incoming : 0);
        Assert.Equal(4, belt.Delivered);
    }


    [Fact]
    public void Haulers_LeaveWhatABeltCarries_ToTheBelt()
    {
        var sim = Flat();
        var (outCrate, inCrate) = TwoShops(sim, planks: 20); // half full: the belt empties it, not haulers
        var belt = Lay(sim, outCrate.Cells[0], inCrate.Cells[0]);
        BuildAll(sim);
        for (int i = 0; i < 3; i++) sim.AddColonist($"Hauler {i}", F(20, 10 + i));
        Run(sim, 2000);
        Assert.Equal(4, CountAt(sim, inCrate.Cells[0], ItemType.Plank));
        Assert.Equal(4, belt.Delivered); // all of it by belt
        Assert.Equal(16, CountAt(sim, outCrate.Cells[0], ItemType.Plank)); // and nobody carried the rest off
    }
    [Fact]
    public void ASplitter_SendsEachPacketWhereItIsWanted()
    {
        var sim = Flat();
        var (outCrate, inCrate) = TwoShops(sim);
        var main = Lay(sim, outCrate.Cells[0], F(24, 30)); // the workshop's planks, dumped on open ground
        // Branch off a tile of the main belt to the far workshop.
        int i = Array.FindIndex(main.Cells, c => c.X >= 14);
        var branch = Lay(sim, main.Cells[i], inCrate.Cells[0]);
        Assert.Equal(PortKind.Belt, branch.From.Kind);
        Assert.Same(branch, sim.Conveyors.BranchAt(main, i));
        BuildAll(sim);
        Run(sim, 3000);
        Assert.Equal(4, CountAt(sim, inCrate.Cells[0], ItemType.Plank)); // the crate gets what it needs first
        Assert.Equal(0, CountAt(sim, outCrate.Cells[0], ItemType.Plank)); // the rest goes on to the ground
        int onGround = sim.Items.All.Where(kv => kv.Value.Type == ItemType.Plank && kv.Key != inCrate.Cells[0]).Sum(kv => kv.Value.Count);
        Assert.Equal(26, onGround);
        Assert.Equal(26, main.Delivered);
        Assert.Equal(4, branch.Delivered);
    }

    [Fact]
    public void AFeeder_MergesIntoABelt_AndBothReachTheStockpile()
    {
        var sim = Flat();
        sim.Rooms.Paint(RoomType.Carpentry, G(4, 4), G(8, 10));
        var planks = sim.Rooms.Place(PartType.OutputCrate, F(8, 5), false)!;
        var logs = sim.Rooms.Place(PartType.OutputCrate, F(8, 10), false)!;
        sim.Items.Drop(planks.Cells[0], ItemType.Plank, 20);
        sim.Items.Drop(logs.Cells[0], ItemType.Log, 10);
        sim.Stockpiles.AddFloorBox(G(36, 4), G(40, 8));
        var store = sim.Stockpiles.ZoneAt(F(36, 4))!;
        var trunk = Lay(sim, planks.Cells[0], F(36, 5));
        Assert.Equal(PortKind.Stockpile, trunk.To.Kind);
        int k = Array.FindIndex(trunk.Cells, c => c.X >= 20);
        var feeder = Lay(sim, logs.Cells[0], trunk.Cells[k]);
        Assert.Equal(PortKind.Belt, feeder.To.Kind);
        // A merge that loops back upstream of where the feeder starts is refused.
        var loop = Conveyors.CanPlace(sim, 0, [trunk.Cells[2].Offset(0, 1, 0)], new Port(PortKind.Belt, trunk.Cells[3], trunk.Id, 3), new Port(PortKind.Belt, trunk.Cells[2], trunk.Id, 2));
        Assert.NotNull(loop);
        BuildAll(sim);
        Run(sim, 4000);
        int Stored(ItemType t) => store.Cells.Sum(c => CountAt(sim, c, t));
        Assert.Equal(20, Stored(ItemType.Plank));
        Assert.Equal(10, Stored(ItemType.Log));
        Assert.Equal(0, trunk.Packets + feeder.Packets);
    }

    [Fact]
    public void AStockpileBelt_IsLoadedByHaulers_FromThatStockpile()
    {
        var sim = Flat();
        sim.Stockpiles.AddFloorBox(G(4, 4), G(8, 8));
        var source = sim.Stockpiles.ZoneAt(F(4, 4))!;
        sim.Stockpiles.AddFloorBox(G(36, 4), G(40, 8));
        var sink = sim.Stockpiles.ZoneAt(F(36, 4))!;
        Assert.Equal(source.Priority, sink.Priority); // equal priority: haulers alone would leave the planks be
        for (int x = 4; x <= 6; x++) sim.Items.Drop(F(x, 4), ItemType.Plank, 40);
        var belt = Lay(sim, F(8, 6), F(36, 6));
        Assert.Equal(PortKind.Stockpile, belt.From.Kind);
        Assert.True(sim.Conveyors.IsHopper(belt.Start));
        BuildAll(sim);
        sim.AddColonist("Hauler", F(10, 10));
        Run(sim, 6000, () => sink.Cells.Sum(c => CountAt(sim, c, ItemType.Plank)) >= 40);
        Assert.True(belt.Delivered >= 40, $"delivered {belt.Delivered}, status {belt.Status}, wants {belt.Wants}x{belt.WantCount}, hopper {(sim.Items.TryGet(belt.Start, out var hp) ? hp.ToString() : "-")}, colonist {sim.Colonists[0].Task} at {sim.Colonists[0].Position}, start {belt.Start}, from {belt.From}, sink {sink.Cells.Sum(c => CountAt(sim, c, ItemType.Plank))}");
        Assert.True(sink.Cells.Sum(c => CountAt(sim, c, ItemType.Plank)) >= 40);
    }

    [Fact]
    public void AHopperOnOpenGround_TakesLooseGoodsRoundIt()
    {
        var sim = Flat();
        sim.Stockpiles.AddFloorBox(G(36, 4), G(40, 8));
        var sink = sim.Stockpiles.ZoneAt(F(36, 4))!;
        var belt = Lay(sim, F(6, 20), F(36, 6));
        Assert.Equal(PortKind.Ground, belt.From.Kind);
        BuildAll(sim);
        for (int i = 0; i < 4; i++) sim.Items.Drop(F(3 + 2 * i, 24), ItemType.Stone, 3);
        sim.AddColonist("Hauler", F(6, 22));
        Run(sim, 6000, () => sink.Cells.Sum(c => CountAt(sim, c, ItemType.Stone)) >= 12);
        Assert.True(belt.Delivered > 0, "the belt carried some of it");
        Assert.Equal(12, sink.Cells.Sum(c => CountAt(sim, c, ItemType.Stone)));
    }

    // ---- Power, blocking, building, removal ---------------------------------------------------------------

    [Fact]
    public void WithoutPower_BeltsStandStill()
    {
        var sim = Flat(needs: true);
        var (outCrate, inCrate) = TwoShops(sim);
        var belt = Lay(sim, outCrate.Cells[0], inCrate.Cells[0]);
        BuildAll(sim);
        Run(sim, 600);
        Assert.Equal(BeltStatus.NoPower, belt.Status);
        Assert.Equal(30, CountAt(sim, outCrate.Cells[0], ItemType.Plank));
        Assert.True(sim.Conveyors.PowerDemand(0) > 0);
        Assert.Equal(sim.Conveyors.PowerDemand(0), sim.Waterworks.DemandOf(0), 0.001f);
    }

    [Fact]
    public void ATileFilledIn_StopsTheBelt_UntilCleared()
    {
        var sim = Flat();
        var (outCrate, inCrate) = TwoShops(sim);
        var belt = Lay(sim, outCrate.Cells[0], inCrate.Cells[0]);
        BuildAll(sim);
        var mid = belt.Cells[belt.Length / 2];
        sim.World.Set(mid, TileType.Stone);
        Run(sim, 800);
        Assert.Equal(BeltStatus.Blocked, belt.Status);
        Assert.Equal(0, CountAt(sim, inCrate.Cells[0], ItemType.Plank));
        sim.World.Set(mid, TileType.Air);
        Run(sim, 1500);
        Assert.Equal(4, CountAt(sim, inCrate.Cells[0], ItemType.Plank));
    }

    [Fact]
    public void Colonists_BuildABelt_FromPlanksAndGears()
    {
        var sim = Flat();
        var (outCrate, inCrate) = TwoShops(sim);
        sim.Stockpiles.AddFloorBox(G(20, 20), G(24, 24));
        sim.Items.Drop(F(20, 20), ItemType.Plank, 40);
        sim.Items.Drop(F(21, 20), Catalog.Item("wooden_gears"), 2);
        // A raised stretch: the belt must cross a trench a level deep, two wide.
        for (int y = 0; y < 48; y++)
            for (int x = 20; x <= 21; x++)
                if (y < 18 || y > 26) sim.World.Set(x, y, 3, TileType.Air);
        var belt = Lay(sim, outCrate.Cells[0], inCrate.Cells[0]);
        Assert.Equal(belt.Length, sim.Constructions.All.Count(b => b.Kind == StructureKind.Conveyor));
        for (int i = 0; i < 2; i++) sim.AddColonist($"Builder {i}", F(22, 22));
        Run(sim, 20_000, () => belt.Complete);
        Assert.True(belt.Complete, $"{belt.Unbuilt} of {belt.Length} tiles left");
        Run(sim, 1500, () => CountAt(sim, inCrate.Cells[0], ItemType.Plank) >= 4);
        Assert.True(CountAt(sim, inCrate.Cells[0], ItemType.Plank) >= 4);
    }

    [Fact]
    public void TakingABeltDown_LeavesItsMaterials_AndDropsItsGoods()
    {
        var sim = Flat();
        var (outCrate, inCrate) = TwoShops(sim, planks: 40);
        var belt = Lay(sim, outCrate.Cells[0], inCrate.Cells[0]);
        BuildAll(sim);
        Run(sim, 60, () => belt.Packets > 0);
        Assert.True(belt.Packets > 0);
        int before = sim.Items.Totals()[ItemType.Plank.Index] + sim.Conveyors.UnitsOn(0, ItemType.Plank);
        Assert.StartsWith("Took the conveyor down", sim.Commands.Execute(new RemoveConveyor(belt.Cells[3])));
        Assert.Equal(0, sim.Conveyors.Count);
        Assert.Empty(sim.Constructions.All);
        // Every plank tile back, the drive's gears, the goods that rode it, and the crate's reserved room let go.
        Assert.Equal(before + belt.Length - 1, sim.Items.Totals()[ItemType.Plank.Index]);
        Assert.Equal(1, sim.Items.Totals()[Catalog.Item("wooden_gears").Index]);
        Assert.Equal(0, sim.Items.TryGet(inCrate.Cells[0], out var p) ? p.Incoming : 0);
    }

    [Fact]
    public void AFilter_LimitsWhatTheBeltCarries()
    {
        var sim = Flat();
        sim.Rooms.Paint(RoomType.Carpentry, G(4, 4), G(8, 8));
        var crate = sim.Rooms.Place(PartType.OutputCrate, F(8, 6), false)!;
        sim.Items.Drop(crate.Cells[0], ItemType.Plank, 10);
        var belt = Lay(sim, crate.Cells[0], F(30, 6));
        BuildAll(sim);
        sim.Commands.Execute(new FilterConveyor(belt.Cells[1], "logs", 3)); // only logs: the planks stay
        Run(sim, 800);
        Assert.Equal(10, CountAt(sim, crate.Cells[0], ItemType.Plank));
        sim.Commands.Execute(new FilterConveyor(belt.Cells[1], "", 0));
        Run(sim, 800);
        Assert.Equal(0, CountAt(sim, crate.Cells[0], ItemType.Plank));
    }

    [Fact]
    public void ASnapshot_WithGoodsOnTheBelts_PlaysOnIdentically()
    {
        var settings = new Net.GameSettings(Seed: 8, Width: 96, Depth: 96, Height: 48, Colonists: 4, Colonies: 1);
        var sim = settings.Create();
        sim.NeedsEnabled = false;
        var home = sim.Colonists[0].Position;
        // A hopper by the landing (its logs lie round it) to a stockpile a little way off.
        sim.Commands.Execute(new AddStockpile(home.Offset(-1, -1, -1), home.Offset(1, 1, -1)));
        var store = sim.Stockpiles.ZoneAt(home)!;
        TilePos? hopper = null;
        for (int r = 6; r < 20 && hopper is null; r++)
        for (int dx = -r; dx <= r && hopper is null; dx++)
        {
            int x = home.X + dx, y = home.Y + r;
            var c = new TilePos(x, y, sim.World.SurfaceLevel(x, y) + 1);
            if (Conveyors.PortAt(sim, 0, c, true, out _) is { Kind: PortKind.Ground }) hopper = c;
        }
        Assert.NotNull(hopper);
        var a = Conveyors.PortAt(sim, 0, hopper!.Value, true, out _)!.Value;
        var b = Conveyors.PortAt(sim, 0, home, false, out _)!.Value;
        var route = ConveyorRouter.Route(sim, Conveyors.EndCells(sim, a, true), Conveyors.EndCells(sim, b, false), out _);
        Assert.NotNull(route);
        Assert.StartsWith("Planned", sim.Commands.Execute(new PlaceConveyor(route![0], Conveyors.Encode(route), a.Kind, hopper.Value, b.Kind, home)));
        Assert.NotNull(store);
        sim.Items.Drop(home, Catalog.Item("wooden_gears"), 2);
        sim.Items.Drop(home, ItemType.Plank, 40);
        for (int i = 0; i < 6000 && !sim.Conveyors.All[0].Complete; i++) sim.Step();
        Assert.True(sim.Conveyors.All[0].Complete);
        for (int i = 0; i < 6; i++) sim.Items.Drop(hopper.Value.Offset(i - 3, 2, 0), ItemType.Log, 2); // goods to carry, now it runs
        for (int i = 0; i < 3000 && sim.Conveyors.All[0].Packets == 0; i++) sim.Step();
        var bb = sim.Conveyors.All[0];
        Assert.True(bb.Packets > 0, $"goods on the belt at the snapshot: {bb.Status} wants {bb.Wants} delivered {bb.Delivered} len {bb.Length} tick {sim.Tick} hopper {(sim.Items.TryGet(bb.Start, out var hp) ? hp : default)} tasks {string.Join(",", sim.Colonists.Select(c => c.Task))}");

        var loaded = Net.Snapshot.Load(Net.Snapshot.Take(sim, settings));
        Assert.Equal(Net.StateHash.Compute(sim), Net.StateHash.Compute(loaded));
        for (int i = 1; i <= 2000; i++)
        {
            sim.Step();
            loaded.Step();
            if (i % 200 == 0) Assert.True(Net.StateHash.Compute(sim) == Net.StateHash.Compute(loaded), $"diverged by tick {sim.Tick}");
        }
    }

    [Fact]
    public void ARoute_SurvivesItsEncoding()
    {
        var cells = new List<TilePos> { F(5, 5), F(6, 5), F(6, 6), F(6, 6, 5), F(7, 6, 6), F(7, 7, 6), F(6, 7, 5), F(6, 6, 5).Offset(0, 0, 0) with { Y = 8 }, };
        cells = cells.Take(7).ToList();
        Assert.Equal(cells, Conveyors.Decode(cells[0], Conveyors.Encode(cells)));
        Assert.Null(Conveyors.Decode(F(1, 1), "EEQ"));
    }
}
