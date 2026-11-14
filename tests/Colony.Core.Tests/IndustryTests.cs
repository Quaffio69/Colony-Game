using Colony.Core.Agents;
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
        return new ColonySim(w);
    }

    private static TilePos G(int x, int y, int z = 3) => new(x, y, z); // a ground tile (click target)

    [Fact]
    public void Paint_TakesFloorCells_MergesTouchingRooms_AndKeepsPiecesSeparate()
    {
        var sim = Meadow();
        var rooms = sim.Rooms;
        Assert.Equal(25, rooms.Paint(RoomType.Sawmill, G(2, 2), G(6, 6)));
        Assert.Equal(9, rooms.Paint(RoomType.Sawmill, G(10, 2), G(12, 4)));
        Assert.Equal(2, rooms.All.Count);
        Assert.All(rooms.All.SelectMany(r => r.Cells), c => Assert.Equal(4, c.Z));

        // A strip joining them merges everything into one room.
        rooms.Paint(RoomType.Sawmill, G(7, 2), G(9, 2));
        Assert.Single(rooms.All);
        Assert.Equal(37, rooms.All[0].Cells.Count);

        // Stockpile cells are never taken, and stockpiles can't be zoned on a room.
        sim.Stockpiles.AddFloorBox(G(20, 20), G(21, 21));
        Assert.Equal(0, rooms.Paint(RoomType.Sawmill, G(20, 20), G(21, 21)));
        Assert.Equal(0, sim.Stockpiles.AddFloorBox(G(2, 2), G(3, 3)));
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

        sim.Rooms.Paint(RoomType.Sawmill, G(10, 10, 3), G(19, 14, 4));
        Assert.Single(sim.Rooms.All);
        Assert.Contains(sim.Rooms.All[0].Cells, c => c.Z == 5);
        Assert.Contains(sim.Rooms.All[0].Cells, c => c.Z == 4);

        sim.Rooms.Paint(RoomType.Sawmill, G(20, 10, 3), G(29, 14, 6));
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
        rooms.Paint(RoomType.Sawmill, G(2, 2), G(12, 6));
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
        rooms.Paint(RoomType.Sawmill, G(2, 2), G(4, 4));
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
        rooms.Paint(RoomType.Sawmill, G(2, 2), G(14, 8));
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
        var unit = RoomInfo.Recipe(RoomType.Sawmill);
        for (int s = 1; s <= PartInfo.MaxSize; s++)
        {
            // Machines: dearer per tile, less labour per unit.
            double costPerTile = PartInfo.Cost(PartType.Sawbench, s) / (double)PartInfo.Tiles(PartType.Sawbench, s);
            var batch = PartInfo.MachineRecipe(PartType.Sawbench, s, unit);
            double labour = batch.WorkTicks / (double)batch.InCount;
            Assert.True(costPerTile > prevCostPerTile && labour < prevLabour, $"saw size {s}");
            Assert.True(batch.OutCount <= Items.ItemInfo.StackLimit(batch.Output), "a batch must fit one output crate");
            Assert.True(batch.InCount <= Items.ItemInfo.StackLimit(batch.Input), "a batch must fit one input crate");

            // Tools: cost grows exponentially (at least x2.5 per size), effect per tile grows.
            double toolCost = PartInfo.Cost(PartType.ToolRack, s);
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
        rooms.Paint(RoomType.Sawmill, G(20, 20), G(27, 26));
        var mill = rooms.Place(PartType.Sawbench, new TilePos(21, 21, 4), false, size: 5)!;
        Assert.Equal(12, mill.Cells.Length);
        Assert.Equal(8, mill.Batch.InCount);
        rooms.Place(PartType.InputCrate, new TilePos(20, 20, 4), false);
        rooms.Place(PartType.OutputCrate, new TilePos(27, 26, 4), false);
        rooms.Place(PartType.ToolRack, new TilePos(21, 25, 4), false, size: 5);
        for (int i = 0; i < 4; i++) sim.AddColonist($"C{i}", new TilePos(10 + i, 12, 4));

        int t = 0;
        for (; t < 60_000 && sim.Items.Totals(sim.Stockpiles.Contains)[(int)ItemType.Plank] < logs * 4; t++) sim.Step();
        Assert.Equal(logs * 4, sim.Items.Totals(sim.Stockpiles.Contains)[(int)ItemType.Plank]);
        Assert.Equal(0, sim.Items.Totals()[(int)ItemType.Log]);
    }

    /// <summary>A sawmill fed from a log stockpile, with a separate stockpile for the planks.</summary>
    private static ColonySim SawmillScene(int logs, int colonists)
    {
        var sim = Meadow();
        sim.Stockpiles.AddFloorBox(G(2, 2), G(4, 4));
        for (int i = 0; i < logs; i += 10) sim.Items.Drop(new TilePos(3, 3, 4), ItemType.Log, Math.Min(10, logs - i));
        sim.Stockpiles.AddFloorBox(G(2, 30), G(6, 34));

        var rooms = sim.Rooms;
        rooms.Paint(RoomType.Sawmill, G(20, 20), G(27, 24));
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
        var recipe = RoomInfo.Recipe(RoomType.Sawmill);

        bool Done() => sim.Items.Totals()[(int)ItemType.Log] == 0 &&
                       sim.Items.Totals(p => !sim.Stockpiles.Contains(p))[(int)ItemType.Plank] == 0 &&
                       sim.Colonists.All(c => c.Task == ColonistTask.None);
        int t = 0;
        for (; t < 60_000 && !Done(); t++) sim.Step();
        Assert.True(Done(), $"after {t} ticks: {string.Join(", ", sim.Items.All.Select(kv => $"{kv.Key}={kv.Value}"))}");

        Assert.Equal(logs * recipe.OutCount / recipe.InCount, sim.Items.Totals(sim.Stockpiles.Contains)[(int)ItemType.Plank]);
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
        Assert.Equal(8, sim.Items.Totals(sim.Stockpiles.Contains)[(int)ItemType.Plank]);
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
        Assert.Equal(10, sim.Items.Totals()[(int)ItemType.Log] + (int)sim.ItemsProduced / 4); // nothing lost
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
