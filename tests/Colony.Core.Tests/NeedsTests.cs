using Colony.Core.Agents;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class NeedsTests
{
    private static ColonySim FlatSim(int size = 24)
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

    private static int Count(ColonySim sim, ItemType t) => sim.Items.Totals()[t.Index];

    [Fact]
    public void TheCatalog_KnowsFoodsAndWhatEachGoodIsFor()
    {
        Assert.Equal(1, Needs.Nutrition(ItemType.Berries));
        Assert.Equal(4, Needs.Nutrition(Catalog.Item("bread")));
        Assert.False(Needs.IsFood(ItemType.Log));
        Assert.Equal("dress", Catalog.Item("clothing").Def.Need);
        Assert.Equal("dress", Catalog.Item("fine_clothing").Def.Need);
        Assert.Equal("household goods", Catalog.Item("ceramics").Def.Need);
        Assert.Equal("comfort", Catalog.Item("furniture").Def.Need);
        Assert.Equal("health", Catalog.Item("tinctures").Def.Need);
        // Every living-standard need can be met by something the colony can make.
        foreach (var (need, _) in Needs.Goods)
            Assert.Contains(Catalog.Items, i => i.Need == need);
    }

    [Fact]
    public void EveryHomeSlot_RunsHumbleToFine_AndHumbleFixturesNeedNoSchooling()
    {
        Assert.Equal(new[] { "bed", "decor", "furniture", "lighting", "tableware" }, Catalog.Fixtures.Keys.Order());
        foreach (var (slot, items) in Catalog.Fixtures)
        {
            Assert.Equal(new[] { 1, 2, 3 }, items.Select(i => i.Def.FixtureTier).Distinct());
            foreach (var humble in items.Where(i => i.Def.FixtureTier == 1))
            {
                Assert.Equal(0, Catalog.RecipeFor(humble)!.Education); // a new colony can furnish its first homes
                Assert.True(humble.Def.Tier <= 2, $"{humble.Def.Id} is tier {humble.Def.Tier} in the tree");
            }
            // Better fixtures last longer: upkeep per day falls as quality rises.
            for (int k = 1; k < items.Count; k++)
                if (items[k].Def.FixtureTier > items[k - 1].Def.FixtureTier) Assert.True(items[k].Def.Wear > items[k - 1].Def.Wear, slot);
        }
    }

    [Fact]
    public void AHungryColonist_WalksToTheNearestFood_EatsItsFill_AndGoesBackToWork()
    {
        var sim = FlatSim();
        var c = sim.AddColonist("Ada", new TilePos(2, 2, 4));
        c.Satiety = Needs.Hungry - 1;
        sim.Items.Drop(new TilePos(10, 10, 4), ItemType.Berries, 20);
        sim.Items.Drop(new TilePos(20, 20, 4), Catalog.Item("bread"), 5); // farther away
        Run(sim, 5);
        Assert.Equal(ColonistTask.Eat, c.Task);
        Run(sim, 300);
        // Hungry at a third of a day left: two berries (a third of a day each) fill it up.
        Assert.Equal(18, Count(sim, ItemType.Berries));
        Assert.Equal(5, Count(sim, Catalog.Item("bread")));
        Assert.True(c.Satiety > Needs.FullSatiety * 9 / 10, $"satiety {c.Satiety}"); // two meals: just short of full, none wasted
        Assert.NotEqual(ColonistTask.Eat, c.Task);
        Assert.Equal(0, sim.Items.All.Values.Sum(p => p.Reserved)); // nothing left promised
    }

    [Fact]
    public void AFamishedColonist_DropsItsWork_ToEat_ThenTheWorkGetsDone()
    {
        var sim = FlatSim();
        var c = sim.AddColonist("Bo", new TilePos(2, 2, 4));
        sim.Designations.Designate(new TilePos(20, 2, 3));
        Run(sim, 10);
        Assert.Equal(ColonistTask.Designation, c.Task);
        c.Satiety = Needs.Famished - 1;
        sim.Items.Drop(new TilePos(2, 12, 4), Catalog.Item("bread"), 3);
        Run(sim, 2);
        Assert.Equal(ColonistTask.Eat, c.Task);
        Run(sim, 1500);
        Assert.Equal(2, Count(sim, Catalog.Item("bread")));
        Assert.Equal(TileType.Air, sim.World.Get(new TilePos(20, 2, 3))); // and the dig was picked up again
    }

    [Fact]
    public void WithNoFood_AColonistKeepsWorking_ThenStarves_AndLeavesAfterADay()
    {
        var sim = FlatSim();
        var c = sim.AddColonist("Cy", new TilePos(2, 2, 4));
        for (int i = 0; i < Needs.NeverBelow; i++) sim.AddColonist($"Di{i}", new TilePos(3 + i, 2, 4)).Satiety = Needs.FullSatiety * 3; // well fed
        c.Satiety = 10;
        sim.Designations.Designate(new TilePos(20, 2, 3));
        Run(sim, 20);
        Assert.True(c.Starving);
        Run(sim, Needs.StarvingLimit);
        Assert.DoesNotContain(c, sim.Colonists);
        Assert.Equal(Needs.NeverBelow, sim.Colonists.Count);
        Assert.Equal(1, sim.Society.Departed);
        Assert.Contains(sim.Events, e => e.Text.Contains("Cy has left"));
        // Ids are never reused after someone leaves.
        var ed = sim.AddColonist("Ed", new TilePos(5, 5, 4));
        Assert.Single(sim.Colonists, k => k.Id == ed.Id);
        Assert.NotEqual(c.Id, ed.Id);
    }

    [Fact]
    public void Colonists_NeverEatAFactorysInputs()
    {
        var sim = FlatSim();
        sim.Rooms.InstantBuild = true;
        sim.Rooms.Paint(Catalog.Workshop("kitchen"), new TilePos(8, 8, 4), new TilePos(14, 14, 4));
        Assert.NotNull(sim.Rooms.Place(PartType.InputCrate, new TilePos(10, 10, 4), false));
        sim.Items.Drop(new TilePos(10, 10, 4), ItemType.Berries, 10);
        Assert.False(Needs.Usable(sim, new TilePos(10, 10, 4)));
        var c = sim.AddColonist("Fa", new TilePos(2, 2, 4));
        c.Satiety = Needs.Famished - 1;
        Run(sim, 200);
        Assert.Equal(10, Count(sim, ItemType.Berries));
        Assert.Equal(0f, Society.FoodDays(sim));
    }

    [Fact]
    public void Settlers_ComeToAColonyWithFoodToSpare_NotToOneWithout()
    {
        ColonySim Colony(int berries)
        {
            var sim = FlatSim(32);
            for (int i = 0; i < 4; i++) sim.AddColonist($"C{i}", new TilePos(4 + i, 4, 4));
            if (berries > 0) sim.Items.Drop(new TilePos(16, 16, 4), ItemType.Berries, berries);
            return sim;
        }
        var fed = Colony(4 * 3 * 4 + 60); // four colonists, over four days of food
        Run(fed, Needs.Day);
        Assert.Equal(5, fed.Colonists.Count);
        Assert.Equal(1, fed.Society.SettlersArrived);
        Assert.Contains(fed.Events, e => e.Text.Contains("settler"));

        var hungry = Colony(10);
        Run(hungry, Needs.Day);
        Assert.Equal(4, hungry.Colonists.Count);
    }

    [Fact]
    public void TheLivingStandard_ComesFromGoodsUsedUpEachDay()
    {
        var sim = FlatSim(32);
        for (int i = 0; i < 20; i++) sim.AddColonist($"C{i}", new TilePos(2 + i, 2, 4)).Satiety = Needs.FullSatiety;
        sim.NeedsEnabled = true;
        var clothing = Catalog.Item("clothing");
        sim.Items.Drop(new TilePos(16, 16, 4), clothing, 10);
        sim.Items.Drop(new TilePos(16, 22, 4), Catalog.Item("furniture"), 10); // furniture is for homes: never used up
        Run(sim, Needs.Day);
        // Twenty colonists use up two sets of clothes a day.
        Assert.Equal(8, Count(sim, clothing));
        Assert.Equal(10, Count(sim, Catalog.Item("furniture")));
        int dress = Array.FindIndex(Needs.Goods, g => g.Need == "dress"), health = Array.FindIndex(Needs.Goods, g => g.Need == "health");
        Assert.Equal(1f, sim.Society.Met[dress]);
        Assert.Equal(0f, sim.Society.Met[health]); // no tinctures
        Assert.InRange(sim.Society.Standard, 0.1f, 0.2f); // half the needs met, smoothed in over a day
    }
}
