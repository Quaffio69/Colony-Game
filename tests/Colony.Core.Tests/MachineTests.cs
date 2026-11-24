using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>Heavy machinery: the excavator and the power wagon.</summary>
public class MachineTests
{
    private static ColonySim FlatSim(int surface = 3, int size = 32, bool needs = false)
    {
        var w = new VoxelWorld(size, size, 16);
        for (int z = 0; z <= surface; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            w.Set(x, y, z, TileType.Stone);
        return new ColonySim(w) { NeedsEnabled = needs };
    }

    private static int TicksToDig(ColonySim sim, int tiles)
    {
        sim.Commands.Execute(new DesignateBox(new TilePos(6, 4, 3), new TilePos(6 + tiles - 1, 4, 3), DesignationKind.Dig));
        int t = 0;
        while (sim.Designations.Count > 0 && t < 20000) { sim.Step(); t++; }
        Assert.Equal(0, sim.Designations.Count);
        return t;
    }

    [Fact]
    public void Machines_ComeFromTheCatalog_AsMachines()
    {
        foreach (var id in new[] { "excavator", "power_wagon" })
        {
            var item = Catalog.Item(id);
            Assert.Equal(ItemKind.Machine, item.Def.Kind);
            Assert.NotEqual(MachineKind.None, MachineInfo.Of(id));
            var recipe = Catalog.RecipeFor(item);
            Assert.NotNull(recipe);
            Assert.Equal("artificer", recipe!.Workshop.Id);
        }
        Assert.Contains(Catalog.RecipeFor(Catalog.Item("excavator"))!.Inputs, i => i.Item.Id == "steam_engine");
    }

    [Fact]
    public void AMachine_KnowsOnlyItsTrade()
    {
        var sim = FlatSim();
        var ex = sim.AddMachine(MachineKind.Excavator, new TilePos(2, 2, 4));
        var wagon = sim.AddMachine(MachineKind.PowerWagon, new TilePos(3, 2, 4));
        Assert.True(ex.IsAutomaton && ex.IsMachine);
        Assert.Equal("Excavator 1", ex.Name);
        Assert.Equal("Power wagon 1", wagon.Name);
        Assert.Equal(1, ex.Priority(Work.Mining));
        Assert.Equal(0, ex.Priority(Work.Hauling));
        Assert.Equal(1, wagon.Priority(Work.Hauling));
        Assert.Equal(0, wagon.Priority(Work.Mining));
        Assert.Equal(Colonist.FuelCapacity, ex.Fuel);
    }

    [Fact]
    public void TheExcavator_DigsFasterThanAMiner()
    {
        var people = FlatSim();
        people.AddColonist("Miner", new TilePos(4, 4, 4));
        int byHand = TicksToDig(people, 8);
        var machines = FlatSim();
        machines.AddMachine(MachineKind.Excavator, new TilePos(4, 4, 4));
        int byMachine = TicksToDig(machines, 8);
        Assert.True(byMachine * 2 < byHand, $"excavator {byMachine} ticks, miner {byHand}");
    }

    [Fact]
    public void TheExcavator_BurnsFuel_StopsWhenEmpty_AndRefuelsWithCharcoal()
    {
        var sim = FlatSim(needs: true);
        var ex = sim.AddMachine(MachineKind.Excavator, new TilePos(4, 4, 4));
        ex.Fuel = Balance.ExcavatorFuelPerDig; // one dig left in the firebox
        sim.Commands.Execute(new DesignateBox(new TilePos(6, 4, 3), new TilePos(9, 4, 3), DesignationKind.Dig));
        for (int i = 0; i < 1500; i++) sim.Step();
        Assert.Equal(0, ex.Fuel);
        Assert.Equal(3, sim.Designations.Count); // dug one, then stood
        Assert.Contains("fuel", ex.Describe());

        sim.Items.Drop(new TilePos(2, 8, 4), ItemType.Charcoal, 20);
        for (int i = 0; i < 3000 && sim.Designations.Count > 0; i++) sim.Step();
        Assert.Equal(0, sim.Designations.Count);
        Assert.True(sim.Items.TryGet(new TilePos(2, 8, 4), out var left) && left.Count < 20, "it took charcoal");
        Assert.True(ex.Fuel > 0);
    }

    [Fact]
    public void ThePowerWagon_HaulsManyLoadsATrip_IntoTheCellsRoundItsStop()
    {
        var sim = FlatSim();
        var wagon = sim.AddMachine(MachineKind.PowerWagon, new TilePos(2, 2, 4));
        for (int x = 20; x <= 26; x++)
        for (int y = 20; y <= 26; y++)
            sim.Stockpiles.Add(new TilePos(x, y, 4));
        int carry = ItemInfo.CarryLimit(ItemType.Stone), stack = ItemInfo.StackLimit(ItemType.Stone);
        int total = carry * Balance.WagonLoads;
        sim.Items.Drop(new TilePos(6, 6, 4), ItemType.Stone, stack); // one full pile...
        sim.Items.Drop(new TilePos(6, 7, 4), ItemType.Stone, stack); // ... and another
        int most = 0;
        for (int i = 0; i < 4000; i++)
        {
            sim.Step();
            if (wagon.CarryingType == ItemType.Stone) most = Math.Max(most, wagon.CarryingCount);
        }
        Assert.True(most > carry * 2, $"a trip carried {most} stone (a colonist carries {carry})");
        Assert.True(most > stack, $"more than one stockpile cell holds ({most} > {stack})");
        int stored = 0, cells = 0;
        foreach (var c in sim.Stockpiles.Cells)
            if (sim.Items.TryGet(c, out var p) && p.Type == ItemType.Stone) { stored += p.Count; cells++; }
        Assert.Equal(2 * stack, stored);
        Assert.True(cells >= 2);
        Assert.True(total >= stack);
        Assert.All(sim.Items.All, kv => Assert.Equal(0, kv.Value.Incoming)); // every reservation settled
    }

    [Fact]
    public void Machines_SurviveASnapshot()
    {
        var settings = new Net.GameSettings(Seed: 4, Width: 64, Depth: 64, Height: 40, Colonists: 2, Colonies: 1);
        var sim = settings.Create();
        var at = sim.Colonists[0].Position;
        var ex = sim.AddMachine(MachineKind.Excavator, at);
        sim.AddMachine(MachineKind.PowerWagon, at);
        sim.Commands.Execute(new DesignateBox(at.Offset(2, -2, -1), at.Offset(5, 2, -2), DesignationKind.Dig));
        for (int i = 0; i < 300; i++) sim.Step();
        var loaded = Net.Snapshot.Load(Net.Snapshot.Take(sim, settings));
        Assert.Equal(Net.StateHash.Compute(sim), Net.StateHash.Compute(loaded));
        var back = loaded.Colonists.Single(c => c.Machine == MachineKind.Excavator);
        Assert.Equal(ex.Fuel, back.Fuel);
        for (int i = 0; i < 600; i++) { sim.Step(); loaded.Step(); }
        Assert.Equal(Net.StateHash.Compute(sim), Net.StateHash.Compute(loaded));
    }
}
