using Colony.Core.Agents;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class HousingTests
{
    private static ColonySim FlatSim(int size = 32)
    {
        var w = new VoxelWorld(size, size, 16);
        for (int z = 0; z <= 3; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            w.Set(x, y, z, TileType.Stone);
        return new ColonySim(w);
    }

    private static void Run(ColonySim sim, int ticks)
    {
        for (int i = 0; i < ticks; i++) sim.Step();
    }

    private static Home PaintHome(ColonySim sim, int x0, int y0, int x1, int y1)
    {
        sim.Rooms.Paint(Catalog.Home, new TilePos(x0, y0, 4), new TilePos(x1, y1, 4));
        sim.Step(); // housing follows the rooms
        return sim.Housing.HomeOf(sim.Rooms.RoomAt(new TilePos(x0, y0, 4))!)!;
    }

    private static HousingPolicy Policy(ColonySim sim, string name) => Policy(sim.Housing, name);
    private static HousingPolicy Policy(Housing h, string name) => h.Policies.First(p => p.Name == name);

    private static int Count(ColonySim sim, ItemType t) => sim.Items.Totals()[t.Index];

    [Fact]
    public void TheSlots_AreTheCatalogsFixtureSlots()
    {
        Assert.Equal(Catalog.Fixtures.Keys.Order(), Housing.Slots.Order());
        Assert.False(RoomInfo.Allows(Catalog.Home, PartType.InputCrate)); // homes take no machines or crates
    }

    [Fact]
    public void ASizedHome_SleepsMoreAsADormitory_FewerAsAManor()
    {
        var h = new Housing();
        // Twelve cells keep three for walking: nine for beds (two cells) and furniture (one cell a piece).
        Assert.Equal(4, Housing.Plan(12, Policy(h, "Dormitory")).Residents); // a quarter piece of furniture each
        Assert.Equal(3, Housing.Plan(12, Policy(h, "Cottage")).Residents);   // a piece each
        Assert.Equal(2, Housing.Plan(12, Policy(h, "Manor")).Residents);     // two and a half each
        Assert.Equal(0, Housing.Plan(Housing.MinCells - 1, Policy(h, "Cottage")).Residents);
        var big = Housing.Plan(120, Policy(h, "Cottage"));
        Assert.Equal(30, big.Residents);
        Assert.Equal(30, big.Count[Housing.Furniture]);
        Assert.Equal(15, big.Count[Housing.Decor]);
        Assert.Equal(0, Housing.Plan(12, Policy(h, "Dormitory")).Count[Housing.Decor]); // a dormitory has no decor
    }

    private static void StockCottage(ColonySim sim, TilePos at, int n)
    {
        sim.Items.Drop(at, Catalog.Item("straw_pallet"), n);
        sim.Items.Drop(at.Offset(2, 0, 0), Catalog.Item("rough_furniture"), n);
        sim.Items.Drop(at.Offset(4, 0, 0), Catalog.Item("wooden_ware"), n);
        sim.Items.Drop(at.Offset(6, 0, 0), Catalog.Item("rushlights"), n);
        sim.Items.Drop(at.Offset(8, 0, 0), Catalog.Item("woven_rug"), n);
    }

    [Fact]
    public void Colonists_FurnishAHomeFromTheStores_ThenMoveIn()
    {
        var sim = FlatSim();
        var home = PaintHome(sim, 4, 4, 7, 6); // 12 cells: a cottage for three
        StockCottage(sim, new TilePos(4, 20, 4), 6);
        for (int i = 0; i < 4; i++) sim.AddColonist($"C{i}", new TilePos(10 + i, 10, 4));
        Run(sim, 3000);

        var plan = Housing.Plan(home);
        for (int s = 0; s < Housing.SlotCount; s++) Assert.Equal(plan.Count[s], home.Count(s));
        Assert.Equal(3, home.Beds);
        Assert.Equal(3, home.Residents.Count);
        Assert.Equal(1, sim.Colonists.Count(c => c.Home == null)); // three beds, four colonists
        Assert.Equal(6 - 3, Count(sim, Catalog.Item("straw_pallet"))); // the rest stay in store
        Assert.All(home.Residents, c => Assert.True(Housing.Score(home, c) > 0.8f, $"score {Housing.Score(home, c)}"));
    }

    [Fact]
    public void RaisingAPolicysTier_UpgradesEveryHomeOnIt_AndBreaksTheOldPiecesDown()
    {
        var sim = FlatSim();
        var a = PaintHome(sim, 2, 2, 5, 4);
        var b = PaintHome(sim, 2, 10, 5, 12);
        var cottage = Policy(sim, "Cottage");
        foreach (var home in new[] { a, b }) sim.Housing.Install(sim, home, Catalog.Item("straw_pallet"), 3);
        sim.AddColonist("Ann", new TilePos(15, 15, 4));
        sim.AddColonist("Ben", new TilePos(16, 15, 4));
        sim.Items.Drop(new TilePos(20, 20, 4), Catalog.Item("wooden_bed"), 6);

        sim.Housing.SetTier(cottage, Housing.Bed, 2);
        Run(sim, 4000);
        foreach (var home in new[] { a, b })
        {
            Assert.Equal(3, home.Beds);
            Assert.All(home.Fixtures.Where(f => f.Slot == Housing.Bed), f => Assert.Equal(2, f.Tier));
        }
        Assert.Equal(0, Count(sim, Catalog.Item("wooden_bed")));
        Assert.Equal(6, sim.Housing.Salvaged);
        Assert.Equal(6, Count(sim, ItemType.Fiber)); // each new pallet's 3 fiber, half back: one apiece
    }

    [Fact]
    public void WornOutFixtures_GoForGood_AndAreReplaced()
    {
        var sim = FlatSim();
        var home = PaintHome(sim, 4, 4, 7, 6);
        sim.Housing.Install(sim, home, Catalog.Item("straw_pallet"), 3);
        home.Fixtures[0].WearLeft = 10;
        Run(sim, Needs.Day);
        Assert.Equal(1, sim.Housing.WornOut);
        Assert.Equal(2, home.Beds);
        Assert.Equal(0, Count(sim, ItemType.Fiber)); // nothing to salvage from a worn-out piece
        Assert.Contains(sim.Housing.Demands(), d => d.Home == home && d.Slot == Housing.Bed && d.Missing == 1);
    }

    [Fact]
    public void ShrinkingAHome_TakesOutWhatNoLongerFits_AndErasingItBreaksEverythingDown()
    {
        var sim = FlatSim();
        var home = PaintHome(sim, 4, 4, 11, 6); // 24 cells: six in a cottage
        sim.Housing.Install(sim, home, Catalog.Item("straw_pallet"), 6);
        Assert.Equal(6, home.Beds);
        sim.Rooms.Erase(new TilePos(8, 4, 4), new TilePos(11, 6, 4)); // half of it
        sim.Step();
        home = sim.Housing.All.Single();
        Assert.Equal(Housing.Plan(home).Count[Housing.Bed], home.Beds);
        Assert.True(home.Beds < 6);
        sim.Rooms.Erase(new TilePos(0, 0, 4), new TilePos(31, 31, 4));
        sim.Step();
        Assert.Empty(sim.Housing.All);
        Assert.Equal(6, sim.Housing.Salvaged);
    }

    [Fact]
    public void ABetterHome_DrawsTheMostEducatedOutOfTheDormitory()
    {
        var sim = FlatSim();
        var dorm = PaintHome(sim, 2, 2, 8, 5);
        sim.Housing.Install(sim, dorm, Catalog.Item("straw_pallet"), 4);
        var hand = sim.AddColonist("Hand", new TilePos(15, 15, 4));
        var sage = sim.AddColonist("Sage", new TilePos(16, 15, 4), education: 2);
        sim.Step();
        Assert.Equal(dorm, hand.Home);
        Assert.Equal(dorm, sage.Home);

        var manor = PaintHome(sim, 2, 12, 6, 15);
        sim.Housing.SetPolicy(manor, Policy(sim, "Manor"));
        sim.Housing.Install(sim, manor, Catalog.Item("canopy_bed"), 1);
        sim.Step();
        Assert.Equal(manor, sage.Home); // the Scholar moves up
        Assert.Equal(dorm, hand.Home);
    }

    [Fact]
    public void ATiredColonist_SleepsInItsBed_AndRestsFasterThanOnTheFloor()
    {
        var sim = FlatSim();
        var home = PaintHome(sim, 4, 4, 7, 6);
        sim.Housing.Install(sim, home, Catalog.Item("wooden_bed"), 1);
        var housed = sim.AddColonist("Cot", new TilePos(15, 15, 4));
        var homeless = sim.AddColonist("Mat", new TilePos(20, 20, 4));
        sim.Step();
        Assert.Equal(home, housed.Home);
        Assert.Null(homeless.Home);
        housed.Rest = homeless.Rest = Needs.Tired - 1;
        Run(sim, 60);
        Assert.Equal(ColonistTask.Sleep, housed.Task);
        Assert.Equal(ColonistTask.Sleep, homeless.Task);
        Run(sim, 1300);
        Assert.Contains(housed.Position, home.Room.Cells);
        Assert.Equal(ColonistTask.None, housed.Task); // up again: a comfortable bed restores a day in ~1200 ticks
        Assert.Equal(ColonistTask.Sleep, homeless.Task); // the floor takes twice as long
    }

    [Fact]
    public void AGoodHome_MakesColonistsHappierAndQuicker()
    {
        var sim = FlatSim();
        var home = PaintHome(sim, 4, 4, 7, 6);
        foreach (var id in new[] { "straw_pallet", "rough_furniture", "wooden_ware", "rushlights", "woven_rug" })
            sim.Housing.Install(sim, home, Catalog.Item(id), id == "straw_pallet" ? 1 : 3); // one bed: one resident
        var housed = sim.AddColonist("In", new TilePos(15, 15, 4));
        var homeless = sim.AddColonist("Out", new TilePos(16, 15, 4));
        sim.Housing.SetPolicy(home, Policy(sim, "Cottage"));
        sim.Items.Drop(new TilePos(20, 20, 4), ItemType.Berries, 200);
        Run(sim, 2); // housing assigns the free bed
        Run(sim, Needs.Day);
        Assert.Equal(home, housed.Home);
        Assert.True(housed.Happiness > homeless.Happiness + 0.3f, $"{housed.Happiness} vs {homeless.Happiness}");
        Assert.True(housed.WorkSpeed > homeless.WorkSpeed);
        // A Literate expects better than humble pieces: the same home pleases them less.
        var scholar = new Colonist(99, "Lit", new TilePos(1, 1, 4), education: 1);
        Assert.True(Housing.Score(home, scholar) < Housing.Score(home, housed));
    }

    [Fact]
    public void Unhappiness_ThinsAColony_OneADay_ButNeverBelowAFoundingParty()
    {
        ColonySim Colony(int n)
        {
            var sim = FlatSim();
            for (int i = 0; i < n; i++) sim.AddColonist($"C{i}", new TilePos(2 + i, 2, 4));
            sim.Items.Drop(new TilePos(20, 20, 4), Catalog.Item("bread"), 40); // fed, but homeless and unclothed
            foreach (var c in sim.Colonists) c.UnhappyDays = Needs.UnhappyDaysToLeave;
            return sim;
        }
        var small = Colony(Needs.NeverBelow);
        for (int day = 0; day < 3; day++)
        {
            Run(small, Needs.Day);
            Assert.True(small.Colonists.Count >= Needs.NeverBelow, $"{small.Colonists.Count} left on day {day + 1}");
        }
        // (a settler may join and an unhappy colonist leave again: the colony never drops below a founding party)

        var big = Colony(Needs.NeverBelow + 3);
        for (int day = 1; day <= 5; day++)
        {
            int before = big.Society.Departed;
            Run(big, Needs.Day);
            Assert.InRange(big.Society.Departed - before, 0, 1); // one a day at most
            Assert.True(big.Colonists.Count >= Needs.NeverBelow);
        }
        Assert.True(big.Society.Departed >= 3, $"{big.Society.Departed} left"); // (settlers keep coming to a fed colony)
        Assert.Contains(big.Events, e => e.Text.Contains("unhappy"));
    }
}
