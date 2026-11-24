using Colony.Core.Agents;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Nature;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>The data-driven production tree: catalog, multi-input recipes, education, automatons, farms, minerals.</summary>
public class ProductionTests
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
        var sim = new ColonySim(w);
        sim.Rooms.InstantBuild = true;
        return sim;
    }

    private static TilePos G(int x, int y, int z = 3) => new(x, y, z);
    private static TilePos F(int x, int y) => new(x, y, 4);

    private static void Stock(ColonySim sim, params (ItemType Item, int Count)[] goods)
    {
        sim.Stockpiles.AddFloorBox(G(2, 2), G(8, 8));
        int x = 2;
        foreach (var (item, count) in goods)
        {
            for (int left = count; left > 0; left -= ItemInfo.StackLimit(item))
                sim.Items.Drop(F(x, 2), item, Math.Min(left, ItemInfo.StackLimit(item)));
            x++;
        }
    }

    private static int Total(ColonySim sim, ItemType t) => sim.Items.Totals()[t.Index];

    private static Part Machine(ColonySim sim, string station, TilePos at, string? makes = null)
    {
        var part = sim.Rooms.Place(Catalog.Part(station), at, false)!;
        Assert.NotNull(part);
        if (makes is not null) Assert.True(sim.Rooms.SetRecipe(part, Catalog.RecipeFor(Catalog.Item(makes))!) || part.Recipe!.Output.Id == makes);
        return part;
    }

    private static void Crates(ColonySim sim, int inputs, int outputs, int x0 = 20, int y0 = 20)
    {
        for (int i = 0; i < inputs; i++) Assert.NotNull(sim.Rooms.Place(PartType.InputCrate, F(x0, y0 + i), false));
        for (int i = 0; i < outputs; i++) Assert.NotNull(sim.Rooms.Place(PartType.OutputCrate, F(x0 + 7, y0 + i), false));
    }

    // ---- Catalog ------------------------------------------------------------------------------------

    [Fact]
    public void Catalog_LoadsTheWholeTree()
    {
        Assert.Equal(109, Catalog.Items.Count);
        Assert.Equal(12, Catalog.Workshops.Count);
        Assert.Equal(86, Catalog.Recipes.Count);
        Assert.Equal(32, Catalog.Parts.Count(p => p.Role == PartRole.Machine));
        Assert.All(Catalog.Items, i => Assert.True(i.IsRaw || Catalog.RecipeFor(i.Type) is not null, i.Id));
        Assert.Equal(23, Catalog.Items.Count(i => i.IsRaw));
        Assert.Equal(7, Catalog.Item("automaton").Def.Tier);
        Assert.Equal(ItemKind.Worker, Catalog.Item("automaton").Def.Kind);
        Assert.Equal("Saw horse", PartInfo.Name(PartType.Sawbench, 1));
        Assert.Equal("Large kiln", PartInfo.Name(Catalog.Part("kiln.kiln"), 3));
    }

    [Fact]
    public void EveryBatch_FitsOneCratePerIngredient()
    {
        foreach (var r in Catalog.Recipes)
        {
            var batch = PartInfo.MachineBatchOf(r.Station, PartInfo.MaxSize, r);
            for (int i = 0; i < r.Inputs.Count; i++)
                Assert.True(batch.InCount(i) <= ItemInfo.StackLimit(r.Inputs[i].Item), $"{r}: {r.Inputs[i].Item}");
            if (r.Kind == ItemKind.Good) Assert.True(batch.OutCount <= ItemInfo.StackLimit(r.Output), r.ToString());
        }
    }

    /// <summary>
    /// The tree can be climbed from nothing: the smallest version of every station is buildable from goods the
    /// colony can already make, every item becomes makeable, and Magister recipes only open once the Academy has
    /// the attunement circle that draws the first Magister.
    /// </summary>
    [Fact]
    public void TheTree_BootstrapsFromRawResources()
    {
        var available = Catalog.Items.Where(i => i.IsRaw).Select(i => i.Type).ToHashSet();
        var built = new HashSet<PartType>();
        int magister = Catalog.EducationLevels.Count - 1;
        bool changed = true;
        while (changed)
        {
            changed = false;
            bool hasMagister = built.Any(p => p.Recipes.Any(r => r.Kind == ItemKind.Education && ColonySim.Teaches(r) == magister));
            foreach (var p in Catalog.Parts.Where(p => p.Role == PartRole.Machine))
                if (!built.Contains(p) && available.Contains(PartInfo.Cost(p, 1).Material)) changed |= built.Add(p);
            foreach (var r in Catalog.Recipes)
                if (built.Contains(r.Station) && (r.Education < magister || hasMagister) && r.Inputs.All(i => available.Contains(i.Item)))
                    changed |= available.Add(r.Output);
        }
        Assert.All(Catalog.Parts.Where(p => p.Role == PartRole.Machine), p => Assert.Contains(p, built));
        Assert.All(Catalog.Items, i => Assert.Contains(i.Type, available));
    }

    // ---- Multi-input chains ------------------------------------------------------------------------------

    /// <summary>
    /// One Kilnworks: the clamp turns logs into charcoal, which haulers move from its output crate to an input
    /// crate where the kiln fires it with clay into bricks. Three inputs, three crates, nothing lost.
    /// </summary>
    [Fact]
    public void Kilnworks_ChainsCharcoalIntoBricks_WithinOneRoom()
    {
        var sim = Meadow();
        Stock(sim, (ItemType.Log, 4), (ItemType.Clay, 8));
        sim.Stockpiles.AddFloorBox(G(2, 30), G(8, 34));
        sim.Rooms.Paint(Catalog.Workshop("kiln"), G(20, 20), G(27, 26));
        Machine(sim, "kiln.charcoal_clamp", F(22, 21));
        Machine(sim, "kiln.kiln", F(22, 24), makes: "bricks");
        Crates(sim, inputs: 3, outputs: 2);
        for (int i = 0; i < 5; i++) sim.AddColonist($"C{i}", F(12 + i, 12));

        var bricks = Catalog.Item("bricks");
        int t = 0;
        for (; t < 60_000 && Total(sim, bricks) < 12; t++) sim.Step();
        Assert.Equal(12, Total(sim, bricks)); // 8 clay = 4 batches of 3 bricks
        Assert.Equal(0, Total(sim, ItemType.Clay));
        Assert.Equal(4 * 2 - 4, Total(sim, Catalog.Item("charcoal"))); // 4 logs -> 8 charcoal, 4 fired
    }

    [Fact]
    public void ChangingAMachinesRecipe_ClearsTheOldInputOutOfTheCrates()
    {
        var sim = Meadow();
        Stock(sim, (Catalog.Item("copper_ore"), 6), (Catalog.Item("charcoal"), 10));
        sim.Rooms.Paint(Catalog.Workshop("smelter"), G(20, 20), G(27, 26));
        var bloomery = Machine(sim, "smelter.bloomery", F(22, 22), makes: "copper_ingot");
        Crates(sim, inputs: 2, outputs: 1);
        sim.AddColonist("Hauler", F(12, 12));
        sim.Rooms.RemovePart(sim.Rooms.All[0].PartsWithRole(PartRole.Output).First()); // keep the ore in the crates
        for (int i = 0; i < 4000; i++) sim.Step();
        Assert.True(Rooms.Stock(sim.Rooms.All[0], PartRole.Input, ItemType.CopperOre, sim.Items) > 0);

        Assert.True(sim.Rooms.SetRecipe(bloomery, Catalog.RecipeFor(Catalog.Item("iron_ingot"))!));
        for (int i = 0; i < 8000; i++) sim.Step();
        Assert.Equal(0, Rooms.Stock(sim.Rooms.All[0], PartRole.Input, ItemType.CopperOre, sim.Items));
        Assert.Equal(6, sim.Items.Totals(sim.Stockpiles.Contains)[ItemType.CopperOre.Index]);
    }

    [Fact]
    public void Recipes_NeedAnEducatedWorker()
    {
        var sim = Meadow();
        Stock(sim, (Catalog.Item("bronze"), 4));
        sim.Stockpiles.AddFloorBox(G(2, 30), G(8, 34));
        sim.Rooms.Paint(Catalog.Workshop("artificer"), G(20, 20), G(27, 26));
        Machine(sim, "artificer.tinker_bench", F(22, 22), makes: "cogs");
        Crates(sim, inputs: 1, outputs: 1);
        var labourer = sim.AddColonist("Labourer", F(12, 12));
        for (int i = 0; i < 4000; i++) sim.Step();
        Assert.Equal(0, Total(sim, Catalog.Item("cogs")));
        Assert.Contains("Literate", Rooms.Status(sim.Rooms.All[0], sim));

        sim.AddColonist("Clerk", F(13, 12), education: 1);
        for (int i = 0; i < 8000 && Total(sim, Catalog.Item("cogs")) < 12; i++) sim.Step();
        Assert.Equal(12, Total(sim, Catalog.Item("cogs")));
        Assert.Equal(0, labourer.Education);
    }

    // ---- Education and workers -----------------------------------------------------------------------

    [Fact]
    public void Academy_TeachesTheNextLevel_UsingUpItsBooks()
    {
        var sim = Meadow();
        Stock(sim, (Catalog.Item("books"), 2), (Catalog.Item("paper"), 4));
        sim.Rooms.Paint(Catalog.Workshop("academy"), G(20, 20), G(27, 26));
        Machine(sim, "academy.lectern", F(22, 22));
        Crates(sim, inputs: 2, outputs: 0);
        var teacher = sim.AddColonist("Teacher", F(12, 12), education: 1);
        var a = sim.AddColonist("A", F(13, 12));
        var b = sim.AddColonist("B", F(14, 12));

        for (int i = 0; i < 20_000 && (a.Education == 0 || b.Education == 0); i++) sim.Step();
        Assert.Equal(1, a.Education);
        Assert.Equal(1, b.Education);
        Assert.Equal(1, teacher.Education);
        Assert.Equal(0, Total(sim, Catalog.Item("books")));
        Assert.Contains(sim.Events, e => e.Text.Contains("graduated"));
        for (int i = 0; i < 2000; i++) sim.Step();
        Assert.Contains("no colonists ready", Rooms.Status(sim.Rooms.All[0], sim)); // nobody left to teach
    }

    [Fact]
    public void TheFirstMagister_ArrivesWhenTheAcademyGetsAnAttunementCircle()
    {
        var sim = Meadow();
        sim.Rooms.Paint(Catalog.Workshop("academy"), G(20, 20), G(27, 26));
        sim.AddColonist("Scholar", F(12, 12), education: 2);
        sim.Step();
        Assert.Single(sim.Colonists);
        Machine(sim, "academy.attunement_circle", F(22, 22));
        sim.Step();
        sim.Step();
        Assert.Equal(2, sim.Colonists.Count);
        Assert.Equal(3, sim.Colonists[1].Education);
        for (int i = 0; i < 100; i++) sim.Step();
        Assert.Equal(2, sim.Colonists.Count); // only the first
    }

    [Fact]
    public void Automatons_JoinTheColonyAsUnskilledWorkers()
    {
        var sim = Meadow();
        Stock(sim, (Catalog.Item("automaton_core"), 1), (Catalog.Item("steel_plate"), 3), (Catalog.Item("clockwork"), 2));
        sim.Rooms.Paint(Catalog.Workshop("artificer"), G(20, 20), G(27, 26));
        Machine(sim, "artificer.assembly_frame", F(22, 22), makes: "automaton");
        Crates(sim, inputs: 3, outputs: 0);
        sim.AddColonist("Magister", F(12, 12), education: 3);
        for (int i = 0; i < 20_000 && sim.Colonists.Count < 2; i++) sim.Step();
        Assert.Equal(2, sim.Colonists.Count);
        Assert.True(sim.Colonists[1].IsAutomaton);
        Assert.Equal(0, sim.Colonists[1].Education);
        Assert.Equal(0, Total(sim, Catalog.Item("automaton_core")));
    }

    // ---- Farms -------------------------------------------------------------------------------------------

    [Fact]
    public void WheatFarm_SowsGrowsAndHarvests_ThenSowsAgain()
    {
        var sim = Meadow();
        sim.Stockpiles.AddFloorBox(G(2, 2), G(6, 6));
        Assert.Equal(9, sim.Farms.AddFloorBox(PlantKind.Wheat, G(20, 20), G(22, 22)));
        Assert.Equal(0, sim.Farms.AddFloorBox(PlantKind.Wheat, G(2, 2), G(3, 3))); // stockpile cells are taken
        sim.AddColonist("Farmer", F(12, 12));
        sim.AddColonist("Hauler", F(13, 12));

        int t = 0;
        for (; t < Balance.WheatGrowTicks + 8000 && sim.Items.Totals(sim.Stockpiles.Contains)[ItemType.Grain.Index] < 36; t++) sim.Step();
        Assert.Equal(36, sim.Items.Totals(sim.Stockpiles.Contains)[ItemType.Grain.Index]); // 9 plots x 4 grain
        for (int i = 0; i < 400; i++) sim.Step();
        Assert.All(sim.Farms.Cells, c => Assert.True(sim.Flora.TryGet(c, out var p) && p.Kind == PlantKind.Wheat, $"{c} not resown"));
    }

    [Fact]
    public void Glowcaps_GrowOnlyUnderARoof()
    {
        var sim = Meadow();
        Assert.Equal(0, sim.Farms.AddFloorBox(PlantKind.Glowcap, G(10, 10), G(12, 12)));
        for (int y = 10; y <= 12; y++)
        for (int x = 10; x <= 12; x++) sim.World.Set(x, y, 7, TileType.Stone);
        Assert.Equal(9, sim.Farms.AddFloorBox(PlantKind.Glowcap, G(10, 10), G(12, 12)));
        Assert.Equal(9, sim.Farms.AddFloorBox(PlantKind.Herbs, G(20, 10), G(22, 12)));
    }

    // ---- Raw resources -----------------------------------------------------------------------------------

    [Fact]
    public void Minerals_FollowTheirStrata()
    {
        var deep = new[] { TileType.SilverOre, TileType.GoldOre, TileType.Sulfur, TileType.Aetherite };
        for (int i = 0; i < 200; i++)
        {
            float pick = i / 200f;
            Assert.DoesNotContain(WorldGenerator.DepositMineral(0.9f, pick), deep);
            Assert.NotEqual(TileType.Aetherite, WorldGenerator.DepositMineral(0.3f, pick));
        }
        Assert.Contains(Enumerable.Range(0, 200).Select(i => WorldGenerator.DepositMineral(0.1f, i / 200f)), t => t == TileType.Aetherite);
    }

    [Fact]
    public void WorldGen_LaysDownClayAndVariedMinerals_AndDiggingYieldsThem()
    {
        var w = WorldGenerator.Generate(new WorldGenSettings(7, 128, 128, 96));
        var counts = new Dictionary<TileType, int>();
        for (int z = 0; z < w.Height; z++)
        for (int y = 0; y < w.Depth; y++)
        for (int x = 0; x < w.Width; x++)
        {
            var t = w.Get(x, y, z);
            counts[t] = counts.GetValueOrDefault(t) + 1;
        }
        Assert.True(counts.GetValueOrDefault(TileType.Clay) > 50, "clay");
        Assert.True(Enum.GetValues<TileType>().Count(t => TileInfo.IsMineral(t) && counts.GetValueOrDefault(t) > 0) >= 5, "mineral variety");
        Assert.Equal(ItemType.Clay, ColonySim.MiningYield(new TilePos(0, 0, 0), TileType.Clay));
        Assert.Equal(ItemType.Aetherite, ColonySim.MiningYield(new TilePos(0, 0, 0), TileType.Aetherite));
        Assert.Equal(ItemType.Sand, ColonySim.MiningYield(new TilePos(0, 0, 0), TileType.Sand));
    }
}
