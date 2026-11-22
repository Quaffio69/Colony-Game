using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>Two colonies in one world: each works, stores, eats and lives only within its own.</summary>
public class FactionTests
{
    private static ColonySim TwoColonies(int size = 40)
    {
        var w = new VoxelWorld(size, size, 16);
        for (int z = 0; z <= 3; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            w.Set(x, y, z, TileType.Stone);
        var sim = new ColonySim(w) { NeedsEnabled = false };
        sim.AddFaction("Ironvale");
        return sim;
    }

    private static void Run(ColonySim sim, int ticks)
    {
        for (int i = 0; i < ticks; i++) sim.Step();
    }

    [Fact]
    public void Colonists_OnlyTakeTheirOwnColonysOrders()
    {
        var sim = TwoColonies();
        var mine = sim.AddColonist("Mine", new TilePos(5, 5, 4));
        var theirs = sim.AddColonist("Theirs", new TilePos(6, 5, 4), faction: 1);
        var dig = new TilePos(20, 20, 3);
        sim.Commands.Execute(new DesignateBox(dig, dig, DesignationKind.Dig)); // player 0's order
        Run(sim, 30);
        Assert.Equal(dig, mine.JobTarget);
        Assert.Null(theirs.JobTarget);
        Assert.Equal(0, sim.Designations.OwnerOf(dig));
    }

    [Fact]
    public void APlayer_CantCancelOrRedesignateAnotherColonysOrders()
    {
        var sim = TwoColonies();
        var dig = new TilePos(20, 20, 3);
        sim.Commands.Execute(new DesignateBox(dig, dig, DesignationKind.Dig));
        Assert.Equal("Cancelled 0 designations and 0 plans, removed 0 stockpile and 0 farm cells", sim.Commands.Execute(new CancelOrders(dig, dig) { Player = 1 }));
        Assert.Equal("UpDownStair: designated 0 tiles", sim.Commands.Execute(new DesignateBox(dig, dig, DesignationKind.UpDownStair) { Player = 1 }));
        Assert.True(sim.Designations.TryGet(dig, out var kind) && kind == DesignationKind.Dig);
        Assert.StartsWith("Cancelled 1", sim.Commands.Execute(new CancelOrders(dig, dig)));
    }

    [Fact]
    public void Haulers_FillOnlyTheirOwnStockpiles_AndNeverEmptyAnothersStores()
    {
        var sim = TwoColonies();
        var a = sim.AddColonist("A", new TilePos(5, 5, 4));
        var b = sim.AddColonist("B", new TilePos(6, 5, 4), faction: 1);
        foreach (var c in new[] { a, b }) { c.SetPriority(Work.Hauling, 1); }
        sim.Commands.Execute(new AddStockpile(new TilePos(30, 30, 3), new TilePos(32, 32, 3)) { Player = 1 });
        sim.Items.Drop(new TilePos(10, 10, 4), ItemType.Log, 10);
        Run(sim, 4000);
        Assert.Equal(0, b.CarryingCount);
        int stored = sim.Stockpiles.Cells.Sum(c => sim.Items.TryGet(c, out var p) ? p.Count : 0);
        Assert.Equal(10, stored); // colony 1's hauler stored the loose logs in its own stockpile
        Assert.All(sim.Stockpiles.Cells, c => Assert.Equal(1, sim.Stockpiles.OwnerOf(c)));

        // Colony 0 makes a stockpile of its own: its hauler won't take colony 1's stored logs to it.
        sim.Commands.Execute(new AddStockpile(new TilePos(2, 30, 3), new TilePos(4, 32, 3)));
        Run(sim, 1200);
        int inMine = sim.Stockpiles.Cells.Where(c => sim.Stockpiles.OwnerOf(c) == 0).Sum(c => sim.Items.TryGet(c, out var p) ? p.Count : 0);
        Assert.Equal(0, inMine);
    }

    [Fact]
    public void Food_InAnotherColonysStockpile_IsNotTheirs()
    {
        var sim = TwoColonies();
        sim.Commands.Execute(new AddStockpile(new TilePos(30, 30, 3), new TilePos(31, 31, 3)) { Player = 1 });
        sim.Items.Drop(new TilePos(30, 30, 4), Catalog.Item("bread"), 10);
        Assert.Equal(0, Society.FoodInStore(sim, 0));
        Assert.Equal(40, Society.FoodInStore(sim, 1));
        sim.Items.Drop(new TilePos(10, 10, 4), ItemType.Berries, 5); // loose: anyone's
        Assert.Equal(5, Society.FoodInStore(sim, 0));
        Assert.Equal(45, Society.FoodInStore(sim, 1));
    }

    [Fact]
    public void Homes_HouseOnlyTheirOwnColony()
    {
        var sim = TwoColonies();
        sim.Commands.Execute(new PaintRoom(Catalog.Home.Id, new TilePos(4, 4, 3), new TilePos(7, 7, 3)) { Player = 1 });
        sim.Step();
        var room = sim.Rooms.All.Single();
        Assert.Equal(1, room.Faction);
        Assert.Empty(sim.Housing.All); // colony 0 has no homes
        var home = sim.Factions[1].Housing.HomeOf(room)!;
        sim.Factions[1].Housing.Install(sim, home, Catalog.Item("straw_pallet"), 2);
        var mine = sim.AddColonist("Mine", new TilePos(20, 20, 4));
        var theirs = sim.AddColonist("Theirs", new TilePos(21, 20, 4), faction: 1);
        Run(sim, 5);
        Assert.Null(mine.Home);
        Assert.Equal(home, theirs.Home);
    }

    [Fact]
    public void Commands_OnAnotherColonysThings_AreRefused()
    {
        var sim = TwoColonies();
        var theirs = sim.AddColonist("Theirs", new TilePos(6, 5, 4), faction: 1);
        int before = theirs.Priority(Work.Mining);
        sim.Rooms.InstantBuild = true;
        try
        {
            sim.Commands.Execute(new PaintRoom("carpentry", new TilePos(10, 10, 3), new TilePos(16, 16, 3)) { Player = 1 });
            Assert.Equal("That room belongs to another colony", sim.Commands.Execute(new PlacePart(Catalog.Workshops.First(w => w.Id == "carpentry").Stations[0].Id, new TilePos(12, 12, 4), false, 1)));
            Assert.Equal("Erased 0 room cells", sim.Commands.Execute(new EraseRoom(new TilePos(10, 10, 3), new TilePos(16, 16, 3))));
            Assert.Equal($"{theirs.Name} belongs to another colony", sim.Commands.Execute(new SetWorkPriority(theirs.Id, Work.Mining, 1)));
            Assert.Equal(before, theirs.Priority(Work.Mining)); // untouched
        }
        finally
        {
            sim.Rooms.InstantBuild = false;
        }
    }

    [Fact]
    public void AGeneratedWorld_LandsEachColonyApart_WithItsOwnFoundersAndFood()
    {
        var sim = ColonySim.Generate(new WorldGenSettings(Seed: 11, Width: 128, Depth: 128, Height: 48), colonists: 4, factions: 2);
        Assert.Equal(2, sim.Factions.Count);
        var a = sim.Colonists.Where(c => c.Faction == 0).ToList();
        var b = sim.Colonists.Where(c => c.Faction == 1).ToList();
        Assert.Equal(4, a.Count);
        Assert.Equal(4, b.Count);
        Assert.All(b, c => Assert.True(sim.Water.MassAt(c.Position) < Fluids.Water.MinMass, "lands on dry ground"));
        int dx = a[0].Position.X - b[0].Position.X, dy = a[0].Position.Y - b[0].Position.Y;
        Assert.True(dx * dx + dy * dy >= 20 * 20, $"apart: {a[0].Position} vs {b[0].Position}");
        Assert.True(Society.FoodInStore(sim, 0) > 0 && Society.FoodInStore(sim, 1) > 0);
        Assert.Equal(3, a.Count(c => c.Education >= 1)); // a Scholar and two Literates lead each party
        Assert.Equal(a.Select(c => c.Education).OrderBy(e => e), b.Select(c => c.Education).OrderBy(e => e));
    }

    [Fact]
    public void EachColony_GetsItsOwnSettlers()
    {
        var sim = ColonySim.Generate(new WorldGenSettings(Seed: 11, Width: 128, Depth: 128, Height: 48), colonists: 4, factions: 2);
        sim.NeedsEnabled = true;
        for (int i = 0; i < Needs.Day * Balance.FirstSettlerDay + 5; i++) sim.Step();
        Assert.True(sim.Factions[0].Society.SettlersArrived >= 1);
        Assert.True(sim.Factions[1].Society.SettlersArrived >= 1);
        Assert.Equal(4 + sim.Factions[1].Society.SettlersArrived, sim.Colonists.Count(c => c.Faction == 1));
        Assert.Contains(sim.Events, e => e.Faction == 1 && e.Text.Contains("settler"));
    }
}
