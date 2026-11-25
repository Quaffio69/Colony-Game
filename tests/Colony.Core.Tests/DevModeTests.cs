using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>Dev mode: build anything at once, for free, to try it.</summary>
public class DevModeTests
{
    private static ColonySim FlatSim(int surface = 3, int size = 32)
    {
        var w = new VoxelWorld(size, size, 16);
        for (int z = 0; z <= surface; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            w.Set(x, y, z, TileType.Stone);
        var sim = new ColonySim(w) { NeedsEnabled = false };
        sim.AddColonist("Tester", new TilePos(2, 2, 4));
        return sim;
    }

    [Fact]
    public void Blueprints_StandAtOnce_WithNoMaterials()
    {
        var sim = FlatSim();
        Assert.StartsWith("Dev mode", sim.Commands.Execute(new SetDevMode(true, false)));
        Assert.Equal("Stone blocks: 5 planned (each needs 1 stone and a builder)", sim.Commands.Execute(new PlaceWalls(new TilePos(10, 10, 3), new TilePos(14, 10, 3))));
        sim.Commands.Execute(new PaintRoom("carpentry", new TilePos(10, 14, 4), new TilePos(16, 20, 4)));
        var station = Catalog.Workshops.First(w => w.Id == "carpentry").Stations[0];
        Assert.StartsWith("Placed", sim.Commands.Execute(new PlacePart(station.Id, new TilePos(12, 16, 4), false, 3)));
        sim.Step();
        for (int x = 10; x <= 14; x++) Assert.Equal(TileType.Wall, sim.World.Get(new TilePos(x, 10, 4)));
        Assert.Equal(0, sim.Constructions.Count);
        Assert.All(sim.Rooms.All.SelectMany(r => r.Parts), p => Assert.True(p.Built));
        Assert.Empty(sim.Items.All); // nothing was hauled or used up
    }

    [Fact]
    public void AWall_NeverEntombsAColonist()
    {
        var sim = FlatSim();
        sim.Commands.Execute(new SetDevMode(true, false));
        sim.Commands.Execute(new PlaceWalls(new TilePos(2, 2, 3), new TilePos(2, 2, 3)));
        sim.Step();
        Assert.NotEqual(TileType.Wall, sim.World.Get(new TilePos(2, 2, 4)));
    }

    [Fact]
    public void InstantOrders_DigABoxAtOnce_TopDown()
    {
        var sim = FlatSim();
        sim.Commands.Execute(new SetDevMode(true, true));
        sim.Commands.Execute(new DesignateBox(new TilePos(10, 10, 1), new TilePos(14, 14, 3), DesignationKind.Dig));
        sim.Step();
        Assert.Equal(0, sim.Designations.Count);
        for (int z = 1; z <= 3; z++) Assert.Equal(TileType.Air, sim.World.Get(new TilePos(12, 12, z)));

        // Without instant orders, digs wait for a miner as usual.
        sim.Commands.Execute(new SetDevMode(true, false));
        sim.Commands.Execute(new DesignateBox(new TilePos(20, 20, 3), new TilePos(21, 20, 3), DesignationKind.Dig));
        sim.Step();
        Assert.Equal(2, sim.Designations.Count);
    }

    [Fact]
    public void Spawn_DropsItems_AndRollsOutMachines_OnlyInDevMode()
    {
        var sim = FlatSim();
        var at = new TilePos(8, 8, 4);
        Assert.Equal("Spawning needs dev mode", sim.Commands.Execute(new DevSpawn(at, "planks", 20)));
        sim.Commands.Execute(new SetDevMode(true, false));
        sim.Commands.Execute(new DevSpawn(at, "planks", 20));
        Assert.Equal(20, sim.Items.All.Where(kv => kv.Value.Type == ItemType.Plank).Sum(kv => kv.Value.Count));
        sim.Commands.Execute(new DevSpawn(at, "excavator", 1));
        sim.Commands.Execute(new DevSpawn(at, "colonist", 2));
        Assert.Single(sim.Colonists, c => c.Machine == MachineKind.Excavator);
        Assert.Equal(4, sim.Colonists.Count);
        Assert.StartsWith("Dev: no item", sim.Commands.Execute(new DevSpawn(at, "unobtainium", 1)));
    }

    [Fact]
    public void DevMode_IsSinglePlayerOnly_AndSurvivesASnapshot()
    {
        var two = new Net.GameSettings(Seed: 5, Width: 64, Depth: 64, Height: 40, Colonists: 1, Colonies: 2).Create();
        Assert.Equal("Dev mode is for single-player games", two.Commands.Execute(new SetDevMode(true, true)));
        Assert.False(two.DevMode);

        var settings = new Net.GameSettings(Seed: 5, Width: 64, Depth: 64, Height: 40, Colonists: 2, Colonies: 1);
        var sim = settings.Create();
        sim.Commands.Execute(new SetDevMode(true, true));
        var at = sim.Colonists[0].Position;
        sim.Commands.Execute(new DesignateBox(at.Offset(2, -1, -1), at.Offset(4, 1, -2), DesignationKind.Dig));
        for (int i = 0; i < 20; i++) sim.Step();
        var loaded = Net.Snapshot.Load(Net.Snapshot.Take(sim, settings));
        Assert.True(loaded.DevMode && loaded.DevInstantOrders && loaded.Rooms.InstantBuild);
        Assert.Equal(Net.StateHash.Compute(sim), Net.StateHash.Compute(loaded));
        for (int i = 0; i < 100; i++) { sim.Step(); loaded.Step(); }
        Assert.Equal(Net.StateHash.Compute(sim), Net.StateHash.Compute(loaded));
    }
}
