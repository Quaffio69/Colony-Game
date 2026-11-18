using Colony.Core.Agents;
using Colony.Core.Items;
using Colony.Core.Nature;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class WorkPriorityTests
{
    private static ColonySim Meadow(int size = 32)
    {
        var w = new VoxelWorld(size, size, 12);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            for (int z = 0; z <= 2; z++) w.Set(x, y, z, TileType.Stone);
            w.Set(x, y, 3, TileType.Grass);
        }
        return new ColonySim(w) { NeedsEnabled = false };
    }

    private static void Run(ColonySim sim, int ticks)
    {
        for (int i = 0; i < ticks; i++) sim.Step();
    }

    private static void Only(Colonist c, params Work[] works)
    {
        for (int w = 0; w < Colonist.WorkTypes; w++) c.SetPriority((Work)w, 0);
        foreach (var w in works) c.SetPriority(w, 1);
    }

    [Fact]
    public void Defaults_PreferredWorkFirst_EverythingElseOn()
    {
        var sim = Meadow();
        for (int i = 0; i < 3; i++) sim.AddColonist($"C{i}", new TilePos(4 + i, 4, 4));
        foreach (var c in sim.Colonists)
        for (int w = 0; w < Colonist.WorkTypes; w++)
        {
            int expected = Colonist.LaborOf((Work)w) == c.PreferredWork ? 2 : 3;
            Assert.Equal(expected, c.Priority((Work)w));
        }
    }

    [Fact]
    public void DisabledWork_IsNeverTaken_EvenWhenItIsTheOnlyWork()
    {
        var sim = Meadow();
        var c = sim.AddColonist("Miner", new TilePos(4, 4, 4));
        Only(c, Work.Mining);
        sim.Flora.Add(new Plant(new TilePos(6, 6, 4), PlantKind.Oak, 255, 0));
        sim.Designations.DesignateBox(new TilePos(0, 0, 4), new TilePos(10, 10, 4), DesignationKind.Chop);
        Run(sim, 400);
        Assert.Equal(ColonistState.Idle, c.State);
        Assert.True(sim.Flora.TryGet(new TilePos(6, 6, 4), out _), "the tree still stands: felling is off");

        c.SetPriority(Work.Woodcutting, 3); // turned on: it goes and fells it
        Run(sim, 20);
        Assert.Equal(Work.Woodcutting, c.CurrentWork);
        Run(sim, 1500);
        Assert.False(sim.Flora.TryGet(new TilePos(6, 6, 4), out _));
    }

    [Fact]
    public void HigherPriority_ComesFirst_EvenWhenFarther()
    {
        var sim = Meadow();
        var c = sim.AddColonist("C", new TilePos(4, 4, 4));
        sim.Flora.Add(new Plant(new TilePos(5, 4, 4), PlantKind.Oak, 255, 0)); // right beside it
        sim.Designations.DesignateBox(new TilePos(0, 0, 4), new TilePos(10, 10, 4), DesignationKind.Chop);
        var dig = new TilePos(25, 25, 3);
        sim.Designations.Designate(dig); // far away
        Only(c, Work.Mining, Work.Woodcutting);
        c.SetPriority(Work.Woodcutting, 2);
        Run(sim, 5);
        Assert.Equal(Work.Mining, c.CurrentWork);
        Assert.Equal(dig, c.JobTarget);

        // Same priority: designation kinds are searched together, so the nearer job wins.
        var sim2 = Meadow();
        var d = sim2.AddColonist("D", new TilePos(4, 4, 4));
        sim2.Flora.Add(new Plant(new TilePos(5, 4, 4), PlantKind.Oak, 255, 0));
        sim2.Designations.DesignateBox(new TilePos(0, 0, 4), new TilePos(10, 10, 4), DesignationKind.Chop);
        sim2.Designations.Designate(dig);
        Only(d, Work.Mining, Work.Woodcutting);
        Run(sim2, 5);
        Assert.Equal(Work.Woodcutting, d.CurrentWork);
    }

    [Fact]
    public void Hauling_OnlyByThoseSetToHaul()
    {
        var sim = Meadow();
        var hauler = sim.AddColonist("Hauler", new TilePos(4, 4, 4));
        var miner = sim.AddColonist("Miner", new TilePos(5, 4, 4));
        Only(hauler, Work.Hauling);
        Only(miner, Work.Mining);
        sim.Stockpiles.AddFloorBox(new TilePos(20, 20, 3), new TilePos(22, 22, 3));
        sim.Items.Drop(new TilePos(8, 8, 4), ItemType.Log, 30);
        Run(sim, 20);
        Assert.Equal(Work.Hauling, hauler.CurrentWork);
        Assert.Null(miner.CurrentWork);
    }

    [Fact]
    public void LoweringAPriority_TakesEffectAtTheNextSearch()
    {
        var sim = Meadow();
        var c = sim.AddColonist("C", new TilePos(4, 4, 4));
        Only(c, Work.Mining);
        for (int x = 10; x < 14; x++) sim.Designations.Designate(new TilePos(x, 10, 3));
        Run(sim, 20);
        Assert.Equal(Work.Mining, c.CurrentWork);
        c.SetPriority(Work.Mining, 0);
        Run(sim, 2000); // finishes the dig in hand, then takes no more
        Assert.Equal(ColonistState.Idle, c.State);
        Assert.True(sim.Designations.Count >= 3);
    }
}
