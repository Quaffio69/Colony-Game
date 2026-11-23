using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class IndustryTests
{
    /// <summary>Stone up to z=2, grass at z=3: the floor cells are at z=4.</summary>
    private static ColonySim Meadow(int size = 40, int height = 14)
    {
        var w = new VoxelWorld(size, size, height);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            for (int z = 0; z <= 2; z++) w.Set(x, y, z, TileType.Stone);
            w.Set(x, y, 3, TileType.Grass);
        }
        var sim = new ColonySim(w);
        sim.Rooms.InstantBuild = true; // most tests are about running factories; construction has its own tests
        return sim;
    }

    private static TilePos G(int x, int y, int z = 3) => new(x, y, z); // a ground tile (click target)

    [Fact]
    public void Paint_TakesFloorCells_MergesTouchingRooms_AndKeepsPiecesSeparate()
    {
        var sim = Meadow();
        var rooms = sim.Rooms;
        Assert.Equal(25, rooms.Paint(RoomType.Carpentry, G(2, 2), G(6, 6)));
        Assert.Equal(9, rooms.Paint(RoomType.Carpentry, G(10, 2), G(12, 4)));
        Assert.Equal(2, rooms.All.Count);
        Assert.All(rooms.All.SelectMany(r => r.Cells), c => Assert.Equal(4, c.Z));

        // A strip joining them merges everything into one room.
        rooms.Paint(RoomType.Carpentry, G(7, 2), G(9, 2));
        Assert.Single(rooms.All);
        Assert.Equal(37, rooms.All[0].Cells.Count);

        // Stockpile cells are never taken, and stockpiles can't be zoned on a room.
        sim.Stockpiles.AddFloorBox(G(20, 20), G(21, 21));
        Assert.Equal(0, rooms.Paint(RoomType.Carpentry, G(20, 20), G(21, 21)));
        Assert.Equal(0, sim.Stockpiles.AddFloorBox(G(2, 2), G(3, 3)));
    }

    [Fact]
    public void ErasingCells_ShrinksAWorkshop_AndPartsThereLeaveTheirMaterials()
    {
        var sim = Meadow();
        sim.Commands.Execute(new PaintRoom("carpentry", G(2, 2), G(9, 5)));
        var saw = sim.Rooms.Place(RoomType.Carpentry.Stations[0], new TilePos(8, 3, 4), false)!;
        Assert.True(saw.Built);
        int before = sim.Items.UnitsOf(saw.Cost.Material);

        // Erase the east end, where the saw stands: the room shrinks, the saw comes down and leaves its materials.
        string message = sim.Commands.Execute(new EraseRoom(G(8, 2), G(9, 5)));
        Assert.Equal("Erased 8 room cells and took down 1 parts", message);
        var room = Assert.Single(sim.Rooms.All);
        Assert.Equal(24, room.Cells.Count);
        Assert.Empty(room.Parts);
        Assert.Equal(before + saw.Cost.Count, sim.Items.UnitsOf(saw.Cost.Material));

        // Erasing a strip through the middle splits it in two.
        sim.Commands.Execute(new EraseRoom(G(5, 2), G(5, 5)));
        Assert.Equal(2, sim.Rooms.All.Count);
    }

    [Fact]
    public void Rooms_SpanLevels_OnlyWhereWorkersCanWalkBetweenThem()
    {
        var sim = Meadow();
        // A raised floor one level up (a step workers can climb) and a separate one three levels up.
        for (int y = 10; y <= 14; y++)
        for (int x = 15; x <= 19; x++)
            sim.World.Set(x, y, 4, TileType.Stone);
        for (int y = 10; y <= 14; y++)
        for (int x = 25; x <= 29; x++)
        for (int z = 4; z <= 6; z++)
            sim.World.Set(x, y, z, TileType.Stone);

        sim.Rooms.Paint(RoomType.Carpentry, G(10, 10, 3), G(19, 14, 4));
        Assert.Single(sim.Rooms.All);
        Assert.Contains(sim.Rooms.All[0].Cells, c => c.Z == 5);
        Assert.Contains(sim.Rooms.All[0].Cells, c => c.Z == 4);

        sim.Rooms.Paint(RoomType.Carpentry, G(20, 10, 3), G(29, 14, 6));
        // The low strip x=20..24 joins the first room; the tall block can't be walked onto: its own room.
        Assert.Equal(2, sim.Rooms.All.Count);
        var tall = sim.Rooms.All.Single(r => r.Cells.Any(c => c.Z == 7));
        Assert.All(tall.Cells, c => Assert.Equal(7, c.Z));
    }

    [Fact]
    public void Erasing_SplitsTheRoom_PartsFollowTheirPiece_AndCutPartsAreRemoved()
    {
        var sim = Meadow();
        var rooms = sim.Rooms;
        rooms.Paint(RoomType.Carpentry, G(2, 2), G(12, 6));
        var left = rooms.Place(PartType.InputCrate, new TilePos(3, 3, 4), false)!;
        var right = rooms.Place(PartType.OutputCrate, new TilePos(11, 3, 4), false)!;
        var cut = rooms.Place(PartType.Sawbench, new TilePos(6, 5, 4), false)!; // covers x=6..7

        rooms.Erase(G(7, 2), G(7, 6));
        Assert.Equal(2, rooms.All.Count);
        Assert.NotSame(left.Room, right.Room);
        Assert.True(cut.Removed);
        Assert.Null(rooms.PartAt(new TilePos(6, 5, 4)));
        Assert.Equal(2, rooms.All.Sum(r => r.Parts.Count));
    }

    [Fact]
    public void Parts_NeedRoomSpace_AndRotateTheirFootprint()
    {
        var sim = Meadow();
        var rooms = sim.Rooms;
        rooms.Paint(RoomType.Carpentry, G(2, 2), G(4, 4));
        Assert.NotNull(rooms.CanPlace(PartType.Lantern, new TilePos(10, 10, 4), false));          // outside
        Assert.NotNull(rooms.CanPlace(PartType.Sawbench, new TilePos(4, 2, 4), false));           // sticks out
        Assert.Null(rooms.CanPlace(PartType.Sawbench, new TilePos(4, 2, 4), rotated: true));      // fits as 1x2
        var bench = rooms.Place(PartType.Sawbench, new TilePos(4, 2, 4), rotated: true)!;
        Assert.Equal([new TilePos(4, 2, 4), new TilePos(4, 3, 4)], bench.Cells);
        Assert.NotNull(rooms.CanPlace(PartType.ToolRack, new TilePos(4, 3, 4), false));          // occupied
    }

    [Fact]
    public void Efficiency_ScalesWithCoverage_NotRawCount()
    {
        var sim = Meadow();
        var rooms = sim.Rooms;
        rooms.Paint(RoomType.Carpentry, G(2, 2), G(14, 8));
        var room = rooms.All[0];
        Assert.Equal(1f, room.Efficiency);
        for (int i = 0; i < 4; i++) rooms.Place(PartType.Sawbench, new TilePos(2 + i * 3, 2, 4), false); // 8 machine tiles
        rooms.Place(PartType.ToolRack, new TilePos(2, 5, 4), false);
        Assert.Equal(1.10f, room.Efficiency, 0.001f); // tool pegs: +20% on 4 of 8 machine tiles
        rooms.Place(PartType.ToolRack, new TilePos(3, 5, 4), false, size: 2);
        Assert.Equal(1.30f, room.Efficiency, 0.001f); // the better rack covers all 8; the pegs add nothing
        rooms.Place(PartType.ToolRack, new TilePos(6, 5, 4), false);
        Assert.Equal(1.30f, room.Efficiency, 0.001f); // nor do more pegs
        rooms.Place(PartType.Lantern, new TilePos(8, 5, 4), false);
        Assert.Equal(1.40f, room.Efficiency, 0.001f); // light is a separate family
    }

    [Fact]
    public void Sizes_TradeCostForLabourAndSpace()
    {
        double prevCostPerTile = 0, prevLabour = double.MaxValue, prevToolCost = 0, prevEffectPerTile = 0;
        var unit = Catalog.RecipeFor(ItemType.Plank)!;
        for (int s = 1; s <= PartInfo.MaxSize; s++)
        {
            // Machines: dearer per tile, less labour per unit.
            double costPerTile = PartInfo.Cost(PartType.Sawbench, s).Count / (double)PartInfo.Tiles(PartType.Sawbench, s);
            var batch = PartInfo.MachineBatchOf(PartType.Sawbench, s, unit);
            double labour = batch.WorkTicks / (double)batch.InCount(0);
            Assert.True(costPerTile > prevCostPerTile && labour < prevLabour, $"saw size {s}");
            Assert.True(batch.OutCount <= Items.ItemInfo.StackLimit(unit.Output), "a batch must fit one output crate");
            Assert.True(batch.InCount(0) <= Items.ItemInfo.StackLimit(unit.Inputs[0].Item), "a batch must fit one input crate");

            // Tools: cost grows exponentially (at least x2.5 per size), effect per tile grows.
            double toolCost = PartInfo.Cost(PartType.ToolRack, s).Count;
            double effect = PartInfo.Bonus(PartType.ToolRack, s) * PartInfo.Coverage(PartType.ToolRack, s) / (double)PartInfo.Tiles(PartType.ToolRack, s);
            Assert.True(toolCost >= prevToolCost * 2.5 && effect > prevEffectPerTile, $"tool size {s}");
            (prevCostPerTile, prevLabour, prevToolCost, prevEffectPerTile) = (costPerTile, labour, toolCost, effect);
        }
        Assert.Equal(1, PartInfo.Sizes(PartType.Lantern));
    }

    [Fact]
    public void BigMachine_ProcessesBatches_AndFinishesTheRemainderAsAPartialBatch()
    {
        const int logs = 21; // two full batches of 8 and a remainder of 5
        var sim = Meadow();
        sim.Stockpiles.AddFloorBox(G(2, 2), G(4, 4));
        for (int i = 0; i < logs; i += 10) sim.Items.Drop(new TilePos(3, 3, 4), ItemType.Log, Math.Min(10, logs - i));
        sim.Stockpiles.AddFloorBox(G(2, 30), G(6, 34));
        var rooms = sim.Rooms;
        rooms.Paint(RoomType.Carpentry, G(20, 20), G(27, 26));
        var mill = rooms.Place(PartType.Sawbench, new TilePos(21, 21, 4), false, size: 5)!;
        Assert.Equal(12, mill.Cells.Length);
        Assert.Equal(8, mill.Batch.InCount(0));
        rooms.Place(PartType.InputCrate, new TilePos(20, 20, 4), false);
        rooms.Place(PartType.OutputCrate, new TilePos(27, 26, 4), false);
        rooms.Place(PartType.ToolRack, new TilePos(21, 25, 4), false, size: 5);
        for (int i = 0; i < 4; i++) sim.AddColonist($"C{i}", new TilePos(10 + i, 12, 4));

        int t = 0;
        for (; t < 60_000 && sim.Items.Totals(sim.Stockpiles.Contains)[ItemType.Plank.Index] < logs * 4; t++) sim.Step();
        Assert.Equal(logs * 4, sim.Items.Totals(sim.Stockpiles.Contains)[ItemType.Plank.Index]);
        Assert.Equal(0, sim.Items.Totals()[ItemType.Log.Index]);
    }

    /// <summary>A sawmill fed from a log stockpile, with a separate stockpile for the planks.</summary>
    private static ColonySim SawmillScene(int logs, int colonists)
    {
        var sim = Meadow();
        sim.Stockpiles.AddFloorBox(G(2, 2), G(4, 4));
        for (int i = 0; i < logs; i += 10) sim.Items.Drop(new TilePos(3, 3, 4), ItemType.Log, Math.Min(10, logs - i));
        sim.Stockpiles.AddFloorBox(G(2, 30), G(6, 34));

        var rooms = sim.Rooms;
        rooms.Paint(RoomType.Carpentry, G(20, 20), G(27, 24));
        rooms.Place(PartType.Sawbench, new TilePos(21, 21, 4), false);
        rooms.Place(PartType.Sawbench, new TilePos(25, 21, 4), rotated: true);
        rooms.Place(PartType.InputCrate, new TilePos(20, 20, 4), false);
        rooms.Place(PartType.OutputCrate, new TilePos(27, 24, 4), false);
        rooms.Place(PartType.ToolRack, new TilePos(22, 23, 4), false);
        for (int i = 0; i < colonists; i++) sim.AddColonist($"C{i}", new TilePos(10 + i, 12, 4));
        return sim;
    }

    [Fact]
    public void Sawmill_TurnsEveryLogIntoPlanks_AndTheyEndUpStored()
    {
        const int logs = 24;
        var sim = SawmillScene(logs, colonists: 6);
        var recipe = Catalog.RecipeFor(ItemType.Plank)!;

        bool Done() => sim.Items.Totals()[ItemType.Log.Index] == 0 &&
                       sim.Items.Totals(p => !sim.Stockpiles.Contains(p))[ItemType.Plank.Index] == 0 &&
                       sim.Colonists.All(c => c.Task == ColonistTask.None);
        int t = 0;
        for (; t < 60_000 && !Done(); t++) sim.Step();
        Assert.True(Done(), $"after {t} ticks: {string.Join(", ", sim.Items.All.Select(kv => $"{kv.Key}={kv.Value}"))}");

        Assert.Equal(logs * recipe.OutCount / recipe.Inputs[0].Count, sim.Items.Totals(sim.Stockpiles.Contains)[ItemType.Plank.Index]);
        Assert.Equal(logs * recipe.OutCount, sim.ItemsProduced);
        Assert.All(sim.Items.All, kv => Assert.True(kv.Value is { Reserved: 0, Incoming: 0 }, $"{kv.Key}: {kv.Value}"));
        Assert.All(sim.Rooms.All[0].PartsWithRole(PartRole.Machine), m => Assert.Equal(-1, m.ClaimedBy));
    }

    [Fact]
    public void LosingTheFloor_RemovesCellsAndParts_AndCrateContentsGetHauledAway()
    {
        var sim = SawmillScene(logs: 0, colonists: 0);
        var crate = sim.Rooms.PartAt(new TilePos(27, 24, 4))!;
        sim.Items.Drop(new TilePos(27, 24, 4), ItemType.Plank, 8);

        sim.Designations.Designate(new TilePos(27, 24, 3));
        sim.CompleteDesignation(new TilePos(27, 24, 3)); // dig out the crate's floor

        Assert.True(crate.Removed);
        Assert.Null(sim.Rooms.RoomAt(new TilePos(27, 24, 4)));
        // The planks fell into the hole as a loose pile; a hauler takes them to the stockpile.
        sim.AddColonist("Hauler", new TilePos(15, 15, 4));
        for (int i = 0; i < 6000; i++) sim.Step();
        Assert.Equal(8, sim.Items.Totals(sim.Stockpiles.Contains)[ItemType.Plank.Index]);
    }

    [Fact]
    public void AMachineWithAStockLimit_StopsThere_AndResumesWhenStockIsUsed()
    {
        var sim = SawmillScene(logs: 20, colonists: 3);
        var saws = sim.Rooms.All[0].PartsWithRole(PartRole.Machine).ToList(); // two saw benches
        var saw = saws[0];
        Assert.Equal("No machine there", sim.Commands.Execute(new SetMachineLimit(new TilePos(0, 0, 4), 8)));
        foreach (var s in saws) sim.Commands.Execute(new SetMachineLimit(s.Cells[0], 8));
        Assert.Equal(8, saw.Limit);
        for (int i = 0; i < 12_000; i++) sim.Step();
        int planks = sim.Items.UnitsOf(ItemType.Plank);
        Assert.InRange(planks, 8, 8 + saws.Sum(s => s.Batch.OutCount)); // stops at the limit (batches in hand finish)
        Assert.True(sim.Items.UnitsOf(ItemType.Log) > 0, "logs left over");
        Assert.Contains("stocked up", Rooms.Status(sim.Rooms.All[0], sim));
        // Use some planks: it starts again.
        foreach (var (p, pile) in sim.Items.All.Where(e => e.Value.Type == ItemType.Plank).ToList()) sim.Items.Consume(p, pile.Available);
        for (int i = 0; i < 6000; i++) sim.Step();
        Assert.True(sim.Items.UnitsOf(ItemType.Plank) >= 8, "made more once stock fell");
    }

    [Fact]
    public void RemovingAMachineMidCycle_ReleasesEverything()
    {
        var sim = SawmillScene(logs: 10, colonists: 4);
        for (int i = 0; i < 20_000 && !sim.Colonists.Any(c => c.Task == ColonistTask.CraftWork); i++) sim.Step();
        Assert.Contains(sim.Colonists, c => c.Task == ColonistTask.CraftWork);

        foreach (var m in sim.Rooms.All[0].PartsWithRole(PartRole.Machine).ToList()) sim.Rooms.RemovePart(m);
        for (int i = 0; i < 6000; i++) sim.Step();

        Assert.All(sim.Colonists, c => Assert.Equal(0, c.CarryingCount));
        Assert.Equal(10, sim.Items.Totals()[ItemType.Log.Index] + (int)sim.ItemsProduced / 4); // nothing lost
        Assert.All(sim.Items.All, kv => Assert.True(kv.Value is { Reserved: 0, Incoming: 0 }, $"{kv.Key}: {kv.Value}"));
    }

    [Fact]
    public void ARoomMakingTwoProducts_SaysItNeedsAnOutputCrateForEach()
    {
        // The first agent playtest: a kitchen with a millstone and an oven but one output crate kept it full of flour,
        // and the oven never baked for 8 days with nothing saying why.
        var sim = Meadow();
        var kitchen = Catalog.Workshop("kitchen");
        sim.Rooms.Paint(kitchen, G(10, 10), G(17, 15));
        sim.Rooms.Place(Catalog.Part("kitchen.millstone"), new TilePos(10, 10, 4), false);
        sim.Rooms.Place(Catalog.Part("kitchen.oven"), new TilePos(10, 13, 4), false);
        for (int i = 0; i < 3; i++) sim.Rooms.Place(PartType.InputCrate, new TilePos(15 + i % 2, 10 + i, 4), false);
        sim.Rooms.Place(PartType.OutputCrate, new TilePos(17, 15, 4), false);
        var room = sim.Rooms.All[0];
        Assert.Contains("needs 2 output crates", Rooms.Status(room, sim));
        sim.Rooms.Place(PartType.OutputCrate, new TilePos(16, 15, 4), false);
        Assert.DoesNotContain("output crate", Rooms.Status(room, sim));
    }

    [Fact]
    public void ABlueprintOnGoodsLyingAbout_IsClearedAndBuilt()
    {
        // The second agent playtest: crates planned on dig rubble waited for days ("needs 2 logs and a builder").
        var sim = Meadow();
        sim.Rooms.InstantBuild = false;
        sim.Stockpiles.AddFloorBox(G(2, 2), G(6, 6));
        sim.Items.Drop(new TilePos(3, 3, 4), ItemType.Log, 10);
        // A backlog of loose stone between the stockpile and the room: always nearer the haulers than the rubble.
        for (int i = 0; i < 60; i++) sim.Items.Drop(new TilePos(8 + i % 10, 2 + i / 10, 4), ItemType.Stone, 2);
        sim.Rooms.Paint(RoomType.Carpentry, G(30, 30), G(34, 34));
        sim.Items.Drop(new TilePos(32, 32, 4), ItemType.Stone, 3); // rubble where the crate goes
        var crate = sim.Rooms.Place(PartType.InputCrate, new TilePos(32, 32, 4), false)!;
        for (int i = 0; i < 2; i++)
        {
            var c = sim.AddColonist($"C{i}", new TilePos(7 + i, 4, 4));
            foreach (var w in Enum.GetValues<Work>()) c.SetPriority(w, w is Work.Hauling or Work.Construction ? 1 : 0);
        }
        int t = 0;
        for (; t < 2500 && !crate.Built; t++) sim.Step();
        Assert.True(crate.Built, $"not built after {t} ticks");
    }

    [Fact]
    public void AnOutputCrate_IsKeptForEachProduct_NotTakenByTheBusiestOne()
    {
        var sim = Meadow();
        sim.Rooms.Paint(RoomType.Carpentry, G(10, 10), G(17, 15));
        var room = sim.Rooms.All[0];
        var saw = sim.Rooms.Place(PartType.Sawbench, new TilePos(10, 10, 4), false)!; // planks
        var joinery = sim.Rooms.Place(Catalog.Part("carpentry.joinery_bench"), new TilePos(10, 13, 4), false)!;
        sim.Rooms.SetRecipe(joinery, Catalog.RecipeFor(Catalog.Item("rough_furniture"))!);
        var a = sim.Rooms.Place(PartType.OutputCrate, new TilePos(17, 15, 4), false)!.Cells[0];
        var b = sim.Rooms.Place(PartType.OutputCrate, new TilePos(16, 15, 4), false)!.Cells[0];
        sim.Items.Drop(a, ItemType.Plank, 40); // the planks' crate is full
        Assert.Null(Rooms.OutputCrateFor(room, ItemType.Plank, 4, sim.Items, _ => true)); // the empty one is kept for furniture
        Assert.Equal(b, Rooms.OutputCrateFor(room, Catalog.Item("rough_furniture"), 1, sim.Items, _ => true));
    }

    [Fact]
    public void Construction_BootstrapsFromLogs_ThenPlanksBuildTheBiggerMachines()
    {
        var sim = Meadow();
        sim.Rooms.InstantBuild = false;
        sim.Stockpiles.AddFloorBox(G(2, 2), G(6, 6));
        for (int i = 0; i < 4; i++) sim.Items.Drop(new TilePos(3 + i, 3, 4), ItemType.Log, 10);
        var rooms = sim.Rooms;
        rooms.Paint(RoomType.Carpentry, G(20, 20), G(28, 26));
        var horse = rooms.Place(PartType.Sawbench, new TilePos(21, 21, 4), false)!;
        var input = rooms.Place(PartType.InputCrate, new TilePos(20, 20, 4), false)!;
        var output = rooms.Place(PartType.OutputCrate, new TilePos(28, 26, 4), false)!;
        Assert.False(horse.Built);
        Assert.Equal(new BuildCost(ItemType.Log, 2), horse.Cost);
        Assert.Equal("machine under construction", Rooms.Status(rooms.All[0], sim));
        for (int i = 0; i < 4; i++) sim.AddColonist($"C{i}", new TilePos(10 + i, 12, 4));

        int t = 0;
        for (; t < 20_000 && !(horse.Built && input.Built && output.Built); t++) sim.Step();
        Assert.True(horse.Built && input.Built && output.Built, $"not built after {t} ticks");
        // Materials were used up, not left lying on the footprints.
        foreach (var part in new[] { horse, input, output })
            Assert.Equal(0, Rooms.MaterialsOnSite(part, sim.Items) - (part.Role == PartRole.Input ? Rooms.Stock(rooms.All[0], PartRole.Input, ItemType.Log, sim.Items) : 0));

        // The working sawmill's planks pay for a saw table.
        var table = rooms.Place(PartType.Sawbench, new TilePos(24, 21, 4), false, size: 2)!;
        Assert.Equal(ItemType.Plank, table.Cost.Material);
        for (t = 0; t < 60_000 && !table.Built; t++) sim.Step();
        Assert.True(table.Built, $"saw table not built after {t} ticks");
        Assert.All(sim.Items.All, kv => Assert.True(kv.Value.Reserved >= 0 && kv.Value.Incoming >= 0));
    }

    [Fact]
    public void CancellingABlueprint_LeavesItsMaterialsToBeHauledAway()
    {
        var sim = Meadow();
        sim.Rooms.InstantBuild = false;
        sim.Stockpiles.AddFloorBox(G(2, 2), G(4, 4));
        sim.Rooms.Paint(RoomType.Carpentry, G(20, 20), G(24, 24));
        var crate = sim.Rooms.Place(PartType.InputCrate, new TilePos(22, 22, 4), false)!;
        sim.Items.Drop(new TilePos(22, 22, 4), ItemType.Log, 1); // half delivered
        sim.Rooms.RemovePart(crate);

        sim.AddColonist("Hauler", new TilePos(10, 10, 4));
        for (int i = 0; i < 4000; i++) sim.Step();
        Assert.Equal(1, sim.Items.Totals(sim.Stockpiles.Contains)[ItemType.Log.Index]);
        Assert.All(sim.Items.All, kv => Assert.True(kv.Value is { Reserved: 0, Incoming: 0 }, $"{kv.Key}: {kv.Value}"));
    }

    [Fact]
    public void Industry_IsDeterministic()
    {
        string Run()
        {
            var sim = SawmillScene(logs: 12, colonists: 5);
            for (int t = 0; t < 5000; t++) sim.Step();
            return string.Join(";", sim.Items.All.Select(kv => $"{kv.Key}:{kv.Value}")) + "|" +
                   string.Join(";", sim.Colonists.Select(c => $"{c.Position}:{c.Task}:{c.CarryingCount}"));
        }
        Assert.Equal(Run(), Run());
    }
}
