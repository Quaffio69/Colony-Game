using Colony.Core.Agents;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class FamilyTests
{
    private static ColonySim FlatSim(int size = 40)
    {
        var w = new VoxelWorld(size, size, 16);
        for (int z = 0; z <= 3; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            w.Set(x, y, z, TileType.Stone);
        var sim = new ColonySim(w);
        sim.Items.Drop(new TilePos(35, 35, 4), Catalog.Item("bread"), 400); // well fed: food is not what these test
        sim.Society.SettlersCome = false; // only home-grown colonists here (settlers would take the free beds first)
        return sim;
    }

    private static Home Cottage(ColonySim sim, int x0, int y0, int beds)
    {
        sim.Rooms.Paint(Catalog.Home, new TilePos(x0, y0, 3), new TilePos(x0 + 3, y0 + 3, 3)); // 16 cells
        sim.Step();
        var home = sim.Housing.HomeOf(sim.Rooms.RoomAt(new TilePos(x0, y0, 4))!)!;
        sim.Housing.Install(sim, home, Catalog.Item("straw_pallet"), beds);
        return home;
    }

    private static void Days(ColonySim sim, int days)
    {
        for (int i = 0; i < days * Needs.Day; i++) sim.Step();
    }

    private static int RunUntil(ColonySim sim, Func<bool> done, int maxDays)
    {
        for (int d = 1; d <= maxDays; d++)
        {
            Days(sim, 1);
            if (done()) return d;
        }
        return -1;
    }

    [Fact]
    public void TwoSingles_Marry_WhenAHomeOfTheirOwnStandsReady_AndMoveInTogether()
    {
        var sim = FlatSim();
        var home = Cottage(sim, 4, 4, beds: 3);
        var a = sim.AddColonist("Ann", new TilePos(20, 20, 4));
        var b = sim.AddColonist("Bo", new TilePos(21, 20, 4));
        Assert.True(RunUntil(sim, () => a.Spouse != null, 15) > 0, "they marry");
        Assert.Equal(b, a.Spouse);
        Assert.Equal(a, b.Spouse);
        Assert.Equal(a.Household, b.Household);
        Assert.True(a.InFamily && b.InFamily);
        Assert.Equal(a.Household, home.Family);
        Assert.Equal(home, a.Home);
        Assert.Equal(home, b.Home);
        Assert.Contains(sim.Events, e => e.Text.Contains("marry"));
    }

    [Fact]
    public void NoWedding_WhileSettlersFillEveryHome()
    {
        var sim = FlatSim();
        var home = Cottage(sim, 4, 4, beds: 3);
        for (int i = 0; i < 3; i++) sim.AddColonist($"S{i}", new TilePos(20 + i, 20, 4));
        Days(sim, 8);
        Assert.All(sim.Colonists, c => Assert.Null(c.Spouse));
        Assert.Equal(0, home.Family);
        Assert.Equal(3, home.Residents.Count); // three settlers lodge there; the home is taken
    }

    [Fact]
    public void Settlers_TakeAFreeHomeBeforeACoupleCanMarryIntoIt()
    {
        var sim = FlatSim();
        sim.Society.SettlersCome = true;
        var home = Cottage(sim, 4, 4, beds: 3);
        var a = sim.AddColonist("Ann", new TilePos(20, 20, 4));
        sim.AddColonist("Bo", new TilePos(21, 20, 4));
        Days(sim, 1);
        Assert.Equal(3, home.Residents.Count); // a settler came on the first morning and took the spare bed
        Days(sim, 6);
        Assert.Null(a.Spouse);
        Assert.Equal(0, home.Family);
        Assert.True(sim.Society.SettlersArrived >= 1);
    }

    [Fact]
    public void ACouple_HasChildrenUntilTheirHomeIsFull_ChildrenDontWork_AndGrowUpAtHome()
    {
        var sim = FlatSim();
        var home = Cottage(sim, 4, 4, beds: 4);
        var a = sim.AddColonist("Ann", new TilePos(20, 20, 4));
        var b = sim.AddColonist("Bo", new TilePos(21, 20, 4));
        Assert.True(RunUntil(sim, () => a.Spouse != null, 15) > 0);
        Assert.True(RunUntil(sim, () => sim.Colonists.Count(c => c.IsChild) == 2, 20) > 0, "two children: four beds");
        var kids = sim.Colonists.Where(c => c.IsChild).ToList();
        Assert.All(kids, k => Assert.Equal(a.Household, k.Household));
        Assert.All(kids, k => Assert.Equal(home, k.Home));

        // Children don't work: a dig waits for the grown-ups (who are kept busy elsewhere: there are none).
        sim.Designations.Designate(new TilePos(30, 30, 3));
        a.Spouse!.Satiety = a.Satiety = Needs.FullSatiety * 3;
        Days(sim, 1);
        Assert.All(kids, k => Assert.NotEqual(ColonistTask.Designation, k.Task));

        Days(sim, 4);
        Assert.Equal(2, sim.Colonists.Count(c => c.IsChild)); // the home is full: no more
        Assert.Equal(4, home.Residents.Count);

        Assert.True(RunUntil(sim, () => kids.All(k => !k.IsChild), Needs.ChildDays + 2) > 0, "they grow up");
        Assert.All(kids, k => Assert.Equal(home, k.Home)); // and still live at home
        Assert.Contains(sim.Events, e => e.Text.Contains("grown up"));
    }

    [Fact]
    public void GrownSiblings_NeverMarryEachOther_ButMarryOutWhenAHomeIsFree()
    {
        var sim = FlatSim();
        var home = Cottage(sim, 4, 4, beds: 4);
        var a = sim.AddColonist("Ann", new TilePos(20, 20, 4));
        sim.AddColonist("Bo", new TilePos(21, 20, 4));
        Assert.True(RunUntil(sim, () => sim.Colonists.Count(c => c.IsChild) == 2, 30) > 0);
        var kids = sim.Colonists.Where(c => c.IsChild).ToList();
        Assert.True(RunUntil(sim, () => kids.All(k => !k.IsChild), Needs.ChildDays + 2) > 0);

        var second = Cottage(sim, 20, 4, beds: 2);
        Days(sim, 6);
        Assert.All(kids, k => Assert.NotEqual(kids.First(o => o != k), k.Spouse)); // siblings: never
        Assert.Equal(0, second.Family);

        // An outsider arrives: one of the grown children marries them and they take the new home.
        var outsider = sim.AddColonist("Out", new TilePos(25, 25, 4));
        Assert.True(RunUntil(sim, () => outsider.Spouse != null, 15) > 0);
        Assert.Contains(outsider.Spouse!, kids);
        Assert.Equal(outsider.Household, second.Family);
        Assert.Equal(second, outsider.Spouse!.Home);
        Assert.NotEqual(a.Household, outsider.Spouse!.Household); // a new family
    }

    [Fact]
    public void Families_StayTogether_OnlySinglesEverLeave()
    {
        var sim = FlatSim();
        Cottage(sim, 4, 4, beds: 2);
        var a = sim.AddColonist("Ann", new TilePos(20, 20, 4));
        var b = sim.AddColonist("Bo", new TilePos(21, 20, 4));
        Assert.True(RunUntil(sim, () => a.Spouse != null, 15) > 0);
        for (int i = 0; i < Needs.NeverBelow + 2; i++) sim.AddColonist($"S{i}", new TilePos(5 + i, 30, 4));
        foreach (var c in sim.Colonists) c.UnhappyDays = Needs.UnhappyDaysToLeave + 1;
        Days(sim, 3);
        Assert.Contains(a, sim.Colonists);
        Assert.Contains(b, sim.Colonists);
        Assert.True(sim.Society.Departed > 0, "unhappy singles did leave");
    }
}
