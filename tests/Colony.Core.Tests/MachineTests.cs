using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>Heavy machinery: the excavator and the power wagon, driven by colonists.</summary>
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

    /// <summary>Digs out a box on the top level; returns the ticks it took and whether anyone drove <paramref name="machine"/>.</summary>
    private static (int Ticks, bool Driven) Dig(ColonySim sim, TilePos a, TilePos b, Colonist? machine = null, int limit = 20000)
    {
        sim.Commands.Execute(new DesignateBox(a, b, DesignationKind.Dig));
        int t = 0;
        bool driven = false;
        while (sim.Designations.Count > 0 && t < limit)
        {
            sim.Step();
            t++;
            driven |= machine?.Driver != null;
        }
        Assert.Equal(0, sim.Designations.Count);
        return (t, driven);
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
    public void AMachine_DoesNothingByItself()
    {
        var sim = FlatSim();
        var ex = sim.AddMachine(MachineKind.Excavator, new TilePos(2, 2, 4));
        var wagon = sim.AddMachine(MachineKind.PowerWagon, new TilePos(3, 2, 4));
        Assert.True(ex.IsAutomaton && ex.IsMachine);
        Assert.Equal("Excavator 1", ex.Name);
        Assert.Equal("Power wagon 1", wagon.Name);
        ex.SetPriority(Work.Mining, 1);
        for (int w = 0; w < Colonist.WorkTypes; w++) Assert.Equal(0, ex.Priority((Work)w));
        Assert.Equal(Colonist.FuelCapacity, ex.Fuel);

        sim.Commands.Execute(new DesignateBox(new TilePos(6, 4, 3), new TilePos(9, 4, 3), DesignationKind.Dig));
        sim.Stockpiles.Add(new TilePos(20, 20, 4));
        sim.Items.Drop(new TilePos(5, 8, 4), ItemType.Stone, 10);
        for (int i = 0; i < 2000; i++) sim.Step();
        Assert.Equal(4, sim.Designations.Count);
        Assert.Equal(new TilePos(2, 2, 4), ex.Position);
        Assert.Equal(new TilePos(3, 2, 4), wagon.Position);
        Assert.Equal("parked", ex.Describe());
    }

    [Fact]
    public void AMiner_DrivesTheExcavator_AndDigsFaster()
    {
        var people = FlatSim();
        people.AddColonist("Miner", new TilePos(4, 4, 4));
        var (byHand, _) = Dig(people, new TilePos(6, 4, 3), new TilePos(13, 6, 3));

        var sim = FlatSim();
        var miner = sim.AddColonist("Miner", new TilePos(4, 4, 4));
        var ex = sim.AddMachine(MachineKind.Excavator, new TilePos(3, 4, 4));
        var (byMachine, driven) = Dig(sim, new TilePos(6, 4, 3), new TilePos(13, 6, 3), ex);
        Assert.True(driven, "the miner drove the excavator");
        Assert.True(byMachine * 2 < byHand, $"with the excavator {byMachine} ticks, by hand {byHand}");

        for (int i = 0; i < 20; i++) sim.Step();
        Assert.Null(ex.Driver); // the work ran out: parked
        Assert.Null(miner.Vehicle);
        Assert.True(Math.Abs(ex.Position.X - 9) <= 5, $"left at the dig ({ex.Position})");
    }

    [Fact]
    public void AFarExcavator_IsNotFetchedForOneSmallJob()
    {
        var sim = FlatSim(size: 64);
        var miner = sim.AddColonist("Miner", new TilePos(4, 4, 4));
        var ex = sim.AddMachine(MachineKind.Excavator, new TilePos(56, 56, 4));
        var (_, driven) = Dig(sim, new TilePos(6, 4, 3), new TilePos(7, 4, 3), ex);
        Assert.False(driven, "two tiles by hand beat a long walk for the machine");
        Assert.Equal(new TilePos(56, 56, 4), ex.Position);
        Assert.Null(miner.Vehicle);
    }

    [Fact]
    public void AFarExcavator_IsFetchedForABigDig()
    {
        var sim = FlatSim(size: 64);
        sim.AddColonist("Miner", new TilePos(4, 4, 4));
        var ex = sim.AddMachine(MachineKind.Excavator, new TilePos(40, 4, 4));
        var (_, driven) = Dig(sim, new TilePos(6, 8, 3), new TilePos(13, 12, 3), ex);
        Assert.True(driven, "forty tiles of stone are worth fetching it from across the yard");
        Assert.True(ex.Position.X < 20, $"it was driven over to the dig ({ex.Position})");
    }

    [Fact]
    public void TheCostModel_CountsTheWholeDig_NotOneJob()
    {
        int dig = Balance.DigStone;
        // A long way to the machine: not for one tile, but for a big excavation.
        Assert.True(Colonist.ExcavatorDigCost(60, 60, 1, dig, true) > Colonist.HandDigCost(5, 1, dig));
        Assert.True(Colonist.ExcavatorDigCost(60, 60, 40, dig, true) < Colonist.HandDigCost(5, 40, dig));
        // The wagon: a few stones walk; a yard of rubble to a far stockpile drives.
        int hand = ItemInfo.CarryLimit(ItemType.Stone), wagon = hand * Balance.WagonLoads;
        Assert.True(Colonist.WagonHaulCost(60, 60, 10, hand, wagon, true) > Colonist.HandHaulCost(5, 10, hand, hand));
        Assert.True(Colonist.WagonHaulCost(20, 20, 40, 3 * wagon, wagon, true) < Colonist.HandHaulCost(5, 40, 3 * wagon, hand));
    }

    [Fact]
    public void ADraftedDriver_ParksTheMachine()
    {
        var sim = FlatSim();
        var miner = sim.AddColonist("Miner", new TilePos(4, 4, 4));
        var ex = sim.AddMachine(MachineKind.Excavator, new TilePos(3, 4, 4));
        sim.Commands.Execute(new DesignateBox(new TilePos(6, 4, 3), new TilePos(13, 8, 3), DesignationKind.Dig));
        for (int i = 0; i < 3000 && miner.Vehicle == null; i++) sim.Step();
        Assert.Same(ex, miner.Vehicle);
        Assert.Contains("driving Excavator 1", miner.Describe());
        sim.Commands.Execute(new SetDrafted(miner.Id, true));
        sim.Step();
        sim.Step();
        Assert.Null(miner.Vehicle);
        Assert.Null(ex.Driver);
        var parked = ex.Position;
        for (int i = 0; i < 200; i++) sim.Step();
        Assert.Equal(parked, ex.Position); // it stays where it was left
    }

    [Fact]
    public void TheDriver_StokesTheExcavator_WithCharcoal()
    {
        var sim = FlatSim(needs: true);
        sim.AddColonist("Miner", new TilePos(4, 4, 4));
        var ex = sim.AddMachine(MachineKind.Excavator, new TilePos(3, 4, 4));
        ex.Fuel = Balance.ExcavatorFuelPerDig * 2; // low
        sim.Items.Drop(new TilePos(2, 10, 4), ItemType.Charcoal, 20);
        var (_, driven) = Dig(sim, new TilePos(6, 4, 3), new TilePos(13, 7, 3), ex);
        Assert.True(driven);
        Assert.True(sim.Items.TryGet(new TilePos(2, 10, 4), out var left) && left.Count < 20, "it took charcoal");
        Assert.True(ex.Fuel > Balance.ExcavatorFuelPerDig * 2);
    }

    [Fact]
    public void AnEmptyExcavator_WithNoCharcoal_IsLeftParked()
    {
        var sim = FlatSim(needs: true);
        var miner = sim.AddColonist("Miner", new TilePos(4, 4, 4));
        var ex = sim.AddMachine(MachineKind.Excavator, new TilePos(3, 4, 4));
        ex.Fuel = 0;
        var (_, driven) = Dig(sim, new TilePos(6, 4, 3), new TilePos(11, 6, 3), ex);
        Assert.False(driven, "nothing to burn: dug by hand");
        Assert.Contains("out of fuel", ex.Describe());
        Assert.Null(miner.Vehicle);
    }

    [Fact]
    public void AHauler_DrivesThePowerWagon_ManyLoadsATrip()
    {
        var sim = FlatSim();
        var hauler = sim.AddColonist("Hauler", new TilePos(3, 3, 4));
        var wagon = sim.AddMachine(MachineKind.PowerWagon, new TilePos(2, 2, 4));
        for (int x = 20; x <= 26; x++)
        for (int y = 20; y <= 26; y++)
            sim.Stockpiles.Add(new TilePos(x, y, 4));
        int carry = ItemInfo.CarryLimit(ItemType.Stone), stack = ItemInfo.StackLimit(ItemType.Stone);
        sim.Items.Drop(new TilePos(6, 6, 4), ItemType.Stone, stack); // one full pile...
        sim.Items.Drop(new TilePos(6, 7, 4), ItemType.Stone, stack); // ... and another
        int most = 0;
        bool driven = false;
        for (int i = 0; i < 4000; i++)
        {
            sim.Step();
            driven |= wagon.Driver == hauler;
            if (hauler.CarryingType == ItemType.Stone) most = Math.Max(most, hauler.CarryingCount);
        }
        Assert.True(driven, "the hauler drove the wagon");
        Assert.True(most > carry * 2, $"a trip carried {most} stone (a colonist carries {carry})");
        Assert.True(most > stack, $"more than one stockpile cell holds ({most} > {stack})");
        int stored = 0, cells = 0;
        foreach (var c in sim.Stockpiles.Cells)
            if (sim.Items.TryGet(c, out var p) && p.Type == ItemType.Stone) { stored += p.Count; cells++; }
        Assert.Equal(2 * stack, stored);
        Assert.True(cells >= 2);
        Assert.All(sim.Items.All, kv => Assert.Equal(0, kv.Value.Incoming)); // every reservation settled
        Assert.Null(wagon.Driver); // nothing left to haul: parked
    }

    [Fact]
    public void AFarWagon_IsNotFetchedForAFewStones()
    {
        var sim = FlatSim(size: 64);
        var hauler = sim.AddColonist("Hauler", new TilePos(4, 4, 4));
        var wagon = sim.AddMachine(MachineKind.PowerWagon, new TilePos(56, 56, 4));
        sim.Stockpiles.Add(new TilePos(10, 4, 4));
        sim.Items.Drop(new TilePos(6, 6, 4), ItemType.Stone, 3);
        bool driven = false;
        for (int i = 0; i < 1500; i++)
        {
            sim.Step();
            driven |= wagon.Driver != null;
        }
        Assert.False(driven);
        Assert.True(sim.Items.TryGet(new TilePos(10, 4, 4), out var stored) && stored.Count == 3, "carried by hand");
        Assert.Equal(new TilePos(56, 56, 4), wagon.Position);
        Assert.Null(hauler.Vehicle);
    }

    [Fact]
    public void ADrivenMachine_SurvivesASnapshot()
    {
        var settings = new Net.GameSettings(Seed: 4, Width: 64, Depth: 64, Height: 40, Colonists: 2, Colonies: 1);
        var sim = settings.Create();
        var at = sim.Colonists[0].Position;
        var ex = sim.AddMachine(MachineKind.Excavator, at);
        sim.AddMachine(MachineKind.PowerWagon, at);
        sim.Commands.Execute(new DesignateBox(at.Offset(2, -3, -1), at.Offset(7, 3, -2), DesignationKind.Dig));
        for (int i = 0; i < 3000 && ex.Driver == null; i++) sim.Step();
        Assert.NotNull(ex.Driver);
        for (int i = 0; i < 50; i++) sim.Step();
        var loaded = Net.Snapshot.Load(Net.Snapshot.Take(sim, settings));
        Assert.Equal(Net.StateHash.Compute(sim), Net.StateHash.Compute(loaded));
        var back = loaded.Colonists.Single(c => c.Machine == MachineKind.Excavator);
        Assert.Equal(ex.Fuel, back.Fuel);
        Assert.Equal(ex.Driver?.Id, back.Driver?.Id);
        for (int i = 0; i < 600; i++) { sim.Step(); loaded.Step(); }
        Assert.Equal(Net.StateHash.Compute(sim), Net.StateHash.Compute(loaded));
    }
}
