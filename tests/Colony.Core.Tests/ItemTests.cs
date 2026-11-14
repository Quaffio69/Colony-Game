using Colony.Core.Agents;
using Colony.Core.Items;
using Colony.Core.Nature;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class ItemTests
{
    /// <summary>Stone up to z=2, grass at z=3: colonists walk at z=4.</summary>
    private static ColonySim Meadow(int size = 32, int height = 12)
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

    private static void AssertNoDanglingReservations(ColonySim sim)
    {
        foreach (var (p, pile) in sim.Items.All)
        {
            Assert.True(pile.Reserved == 0 && pile.Incoming == 0, $"{p}: {pile}");
            Assert.True(pile.Count > 0, $"{p}: empty pile left behind");
            Assert.True(pile.Count <= ItemInfo.StackLimit(pile.Type), $"{p}: over the stack limit");
            Assert.True(Navigation.Movement.IsStandable(sim.World, p), $"{p}: pile not on a floor");
        }
    }

    [Fact]
    public void Digging_LeavesStoneSometimes_AndOreAlways()
    {
        var sim = Meadow();
        for (int x = 2; x < 22; x++) sim.World.Set(x, 5, 2, TileType.Ore);
        sim.Designations.DesignateBox(new TilePos(2, 4, 2), new TilePos(21, 5, 3)); // 2 rows, grass + rock
        foreach (var p in sim.Designations.All.Keys.OrderByDescending(p => p.Z).ToList()) sim.CompleteDesignation(p);

        var totals = sim.Items.Totals();
        Assert.Equal(20, totals[(int)ItemType.Ore]);
        Assert.InRange(totals[(int)ItemType.Stone], 4, 16); // ~half of the 20 stone tiles
        AssertNoDanglingReservations(sim);
    }

    [Fact]
    public void ChopArea_MarksTreesInItsColumns_AtAnyLevel()
    {
        var sim = Meadow();
        // Raise a hill so trees stand at different levels.
        for (int y = 0; y < 6; y++)
        for (int x = 0; x < 6; x++)
        {
            sim.World.Set(x, y, 4, TileType.Soil);
            sim.World.Set(x, y, 5, TileType.Grass);
        }
        Assert.True(sim.Flora.Add(new Nature.Plant(new TilePos(2, 2, 6), Nature.PlantKind.Oak, 200, 0)));
        Assert.True(sim.Flora.Add(new Nature.Plant(new TilePos(8, 8, 4), Nature.PlantKind.Pine, 200, 0)));
        Assert.True(sim.Flora.Add(new Nature.Plant(new TilePos(9, 9, 4), Nature.PlantKind.Fern, 200, 0)));
        Assert.True(sim.Flora.Add(new Nature.Plant(new TilePos(20, 20, 4), Nature.PlantKind.Birch, 200, 0)));

        Assert.Equal(2, sim.Designations.DesignatePlantArea(DesignationKind.Chop, 10, 10, 0, 0)); // corners in any order; the fern isn't a tree
        Assert.True(sim.Designations.Contains(new TilePos(2, 2, 6)));
        Assert.True(sim.Designations.Contains(new TilePos(8, 8, 4)));
        Assert.Equal(1, sim.Designations.CancelPlantArea(DesignationKind.Chop, 0, 0, 3, 3));
        Assert.False(sim.Designations.Contains(new TilePos(2, 2, 6)));
    }

    [Fact]
    public void Gathering_TakesRipePlantsOnly_YieldsTheirCrop_AndCutsThemBack()
    {
        var sim = Meadow();
        var ripeBerries = new Plant(new TilePos(4, 4, 4), PlantKind.BerryBush, 255, 0);
        var reeds = new Plant(new TilePos(5, 4, 4), PlantKind.Reeds, 200, 0);
        sim.Flora.Add(ripeBerries);
        sim.Flora.Add(reeds);
        sim.Flora.Add(new Plant(new TilePos(6, 4, 4), PlantKind.BerryBush, 100, 0)); // unripe
        sim.Flora.Add(new Plant(new TilePos(7, 4, 4), PlantKind.Fern, 255, 0));      // nothing to gather
        sim.Flora.Add(new Plant(new TilePos(8, 4, 4), PlantKind.Oak, 255, 0));       // a tree: chop, not gather

        Assert.Equal(2, sim.Designations.DesignatePlantArea(DesignationKind.Gather, 0, 0, 20, 20));
        sim.AddColonist("Gatherer", new TilePos(10, 10, 4));
        for (int i = 0; i < 3000 && sim.Designations.Count > 0; i++) sim.Step();
        Assert.Equal(0, sim.Designations.Count);

        var totals = sim.Items.Totals();
        Assert.Equal(PlantInfo.HarvestYield(ripeBerries), totals[(int)ItemType.Berries]);
        Assert.Equal(PlantInfo.HarvestYield(reeds), totals[(int)ItemType.Fiber]);
        Assert.True(sim.Flora.TryGet(ripeBerries.Position, out var bush));
        Assert.Equal(PlantInfo.GatheredGrowth, bush.Growth);
        Assert.False(PlantInfo.IsGatherable(bush));
        Assert.Equal(0, sim.Designations.DesignatePlantArea(DesignationKind.Gather, 0, 0, 20, 20)); // nothing ripe left
    }

    [Fact]
    public void ChopBox_OnlyDesignatesTrees_AndFellingLeavesLogs()
    {
        var sim = Meadow();
        var oak = new Plant(new TilePos(5, 5, 4), PlantKind.Oak, 255, 0);
        sim.Flora.Add(oak);
        sim.Flora.Add(new Plant(new TilePos(6, 5, 4), PlantKind.Shrub, 255, 0));

        Assert.Equal(1, sim.Designations.DesignateBox(new TilePos(0, 0, 4), new TilePos(10, 10, 4), DesignationKind.Chop));
        Assert.True(sim.CompleteDesignation(oak.Position));

        Assert.False(sim.Flora.TryGet(oak.Position, out _));
        Assert.True(sim.Items.TryGet(oak.Position, out var logs));
        Assert.Equal(ItemType.Log, logs.Type);
        Assert.Equal(ColonySim.LogYield(oak), logs.Count);
        Assert.True(logs.Count >= 3);
    }

    [Fact]
    public void Drops_SettleOnTheFloor_AndSpillWhenFull()
    {
        var sim = Meadow();
        sim.Items.Drop(new TilePos(10, 10, 9), ItemType.Stone, 60); // dropped from mid-air, more than one stack
        Assert.True(sim.Items.TryGet(new TilePos(10, 10, 4), out var first));
        Assert.Equal(ItemInfo.StackLimit(ItemType.Stone), first.Count);
        Assert.Equal(60, sim.Items.Totals()[(int)ItemType.Stone]);
        Assert.Equal(3, sim.Items.Count);

        // Logs never merge into a stone pile.
        sim.Items.Drop(new TilePos(10, 10, 4), ItemType.Log, 2);
        Assert.Equal(2, sim.Items.Totals()[(int)ItemType.Log]);
        Assert.True(sim.Items.TryGet(new TilePos(10, 10, 4), out var still) && still.Type == ItemType.Stone);
        AssertNoDanglingReservations(sim);
    }

    [Fact]
    public void PilesFall_WhenTheirFloorIsDugOut()
    {
        var sim = Meadow();
        sim.Items.Drop(new TilePos(8, 8, 4), ItemType.Log, 3);
        sim.Designations.Designate(new TilePos(8, 8, 3));
        sim.CompleteDesignation(new TilePos(8, 8, 3)); // grass gone; the stone below is the new floor
        Assert.False(sim.Items.TryGet(new TilePos(8, 8, 4), out _));
        Assert.True(sim.Items.TryGet(new TilePos(8, 8, 3), out var fallen) && fallen.Count == 3);
    }

    [Fact]
    public void Colonists_FellAForest_AndHaulEveryLogToTheStockpile()
    {
        var sim = Meadow();
        for (int i = 0; i < 12; i++)
            sim.Flora.Add(new Plant(new TilePos(16 + (i % 4) * 3, 4 + (i / 4) * 3, 4), PlantKind.Pine, 255, (byte)i));
        sim.Stockpiles.AddFloorBox(new TilePos(2, 20, 3), new TilePos(5, 23, 3)); // 16 cells
        for (int i = 0; i < 6; i++) sim.AddColonist($"C{i}", new TilePos(3 + i, 12, 4));

        int expected = sim.Flora.All.Sum(ColonySim.LogYield);
        sim.Designations.DesignateBox(new TilePos(0, 0, 4), new TilePos(31, 31, 4), DesignationKind.Chop);

        for (int t = 0; t < 30_000 && !Done(); t++) sim.Step();
        Assert.True(Done(), $"designations left {sim.Designations.Count}, loose logs {Loose()}");

        var stored = sim.Items.Totals(sim.Stockpiles.Contains)[(int)ItemType.Log];
        Assert.Equal(expected, stored);
        Assert.All(sim.Colonists, c => Assert.Equal(0, c.CarryingCount));
        AssertNoDanglingReservations(sim);

        bool Done() => sim.Designations.Count == 0 && Loose() == 0 && sim.Colonists.All(c => c.Task == ColonistTask.None);
        int Loose() => sim.Items.Totals(p => !sim.Stockpiles.Contains(p)).Sum();
    }

    [Fact]
    public void Hauling_RespectsStockpileCapacity_AndSpillsNothingIntoOtherCells()
    {
        var sim = Meadow();
        sim.Stockpiles.Add(new TilePos(4, 4, 4)); // one cell: 25 stone fit
        sim.Items.Drop(new TilePos(20, 20, 4), ItemType.Stone, 25);
        sim.Items.Drop(new TilePos(20, 24, 4), ItemType.Stone, 15);
        for (int i = 0; i < 4; i++) sim.AddColonist($"C{i}", new TilePos(10 + i, 10, 4));

        for (int t = 0; t < 20_000; t++) sim.Step();

        Assert.True(sim.Items.TryGet(new TilePos(4, 4, 4), out var stored));
        Assert.Equal(25, stored.Count);
        Assert.Equal(40, sim.Items.Totals()[(int)ItemType.Stone]); // nothing lost
        Assert.All(sim.Colonists, c => Assert.Equal(ColonistTask.None, c.Task));
        AssertNoDanglingReservations(sim);
    }

    [Fact]
    public void WithoutStockpiles_IdleColonistsDoNotKeepSearching()
    {
        var sim = Meadow();
        sim.Items.Drop(new TilePos(20, 20, 4), ItemType.Stone, 10);
        for (int i = 0; i < 4; i++) sim.AddColonist($"C{i}", new TilePos(10 + i, 10, 4));
        for (int t = 0; t < 2000; t++) sim.Step();
        Assert.Equal(0, sim.PathSearches);
    }

    [Fact]
    public void ChopAndHaul_IsDeterministic()
    {
        string Run()
        {
            var sim = Meadow();
            for (int i = 0; i < 6; i++) sim.Flora.Add(new Plant(new TilePos(18 + i * 2, 18, 4), PlantKind.Oak, 220, (byte)i));
            sim.Stockpiles.AddFloorBox(new TilePos(2, 2, 3), new TilePos(4, 4, 3));
            for (int i = 0; i < 5; i++) sim.AddColonist($"C{i}", new TilePos(8 + i, 8, 4));
            sim.Designations.DesignateBox(new TilePos(0, 0, 4), new TilePos(31, 31, 4), DesignationKind.Chop);
            for (int t = 0; t < 4000; t++) sim.Step();
            return string.Join(";", sim.Items.All.Select(kv => $"{kv.Key}:{kv.Value}")) + "|" +
                   string.Join(";", sim.Colonists.Select(c => $"{c.Position}:{c.Task}:{c.CarryingCount}"));
        }
        Assert.Equal(Run(), Run());
    }
}
