using Colony.Core.Commands;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>Deep mining: dig order (priority, top down), geology and depth, gems, fog of war.</summary>
public class MiningTests
{
    private static ColonySim FlatSim(int surface = 3, int size = 24)
    {
        var w = new VoxelWorld(size, size, 16);
        for (int z = 0; z <= surface; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            w.Set(x, y, z, TileType.Stone);
        return new ColonySim(w) { NeedsEnabled = false };
    }

    private static List<Job> CandidatesFor(ColonySim sim, TilePos from)
    {
        sim.Connectivity.Update();
        sim.JobBoard.Update(sim.World, sim.Designations, sim.Connectivity);
        return sim.JobBoard.Candidates(sim.Connectivity.ComponentOf(from), from, sim.Designations, 64);
    }

    [Fact]
    public void DigJobs_GoTopDown_BeforeNearer_LowerOnes()
    {
        var sim = FlatSim();
        var from = new TilePos(2, 2, 4);
        sim.Designations.Designate(new TilePos(3, 2, 2)); // right beside, but lower (reachable from the pit below)
        sim.Designations.Designate(new TilePos(3, 2, 3)); // right beside, on top
        sim.Designations.Designate(new TilePos(15, 15, 3)); // far, on top
        var jobs = CandidatesFor(sim, from);
        var order = jobs.Select(j => j.Target).ToList();
        Assert.Equal(new TilePos(3, 2, 3), order[0]);
        Assert.True(order.IndexOf(new TilePos(15, 15, 3)) < Math.Max(order.IndexOf(new TilePos(3, 2, 2)), order.Count),
            "every top-level order comes before a lower one");
    }

    [Fact]
    public void Priority_BeatsLevelAndDistance()
    {
        var sim = FlatSim();
        var from = new TilePos(2, 2, 4);
        sim.Designations.Designate(new TilePos(3, 2, 3));
        sim.Designations.Designate(new TilePos(15, 15, 3));
        sim.Commands.Execute(new SetOrderPriority(new TilePos(15, 15, 3), new TilePos(15, 15, 3), 1));
        Assert.Equal(1, sim.Designations.PriorityOf(new TilePos(15, 15, 3)));
        Assert.Equal(Designations.DefaultPriority, sim.Designations.PriorityOf(new TilePos(3, 2, 3)));
        Assert.Equal(new TilePos(15, 15, 3), CandidatesFor(sim, from)[0].Target);

        // Last: even the nearest order waits behind default ones.
        sim.Designations.SetPriorityBox(new TilePos(15, 15, 3), new TilePos(15, 15, 3), 5);
        Assert.Equal(new TilePos(3, 2, 3), CandidatesFor(sim, from)[0].Target);

        // A finished or cancelled order forgets its priority.
        sim.Designations.Cancel(new TilePos(15, 15, 3));
        sim.Designations.Designate(new TilePos(15, 15, 3));
        Assert.Equal(Designations.DefaultPriority, sim.Designations.PriorityOf(new TilePos(15, 15, 3)));
    }

    [Fact]
    public void Priority_OnlyTouchesTheColonysOwnOrders()
    {
        var sim = FlatSim();
        sim.Designations.Designate(new TilePos(5, 5, 3), faction: 1);
        Assert.Equal(0, sim.Designations.SetPriorityBox(new TilePos(0, 0, 0), new TilePos(20, 20, 5), 1, faction: 0));
        Assert.Equal(Designations.DefaultPriority, sim.Designations.PriorityOf(new TilePos(5, 5, 3)));
    }

    [Fact]
    public void ColonistsDig_TheUrgentOrdersFirst()
    {
        var sim = FlatSim();
        sim.AddColonist("Miner", new TilePos(2, 2, 4));
        sim.Commands.Execute(new DesignateBox(new TilePos(4, 2, 3), new TilePos(6, 2, 3), DesignationKind.Dig));
        sim.Commands.Execute(new DesignateBox(new TilePos(18, 18, 3), new TilePos(18, 18, 3), DesignationKind.Dig));
        sim.Commands.Execute(new SetOrderPriority(new TilePos(18, 18, 3), new TilePos(18, 18, 3), 1));
        for (int i = 0; i < 4000 && sim.World.Get(18, 18, 3) != TileType.Air; i++) sim.Step();
        Assert.Equal(TileType.Air, sim.World.Get(18, 18, 3));
        Assert.True(sim.Designations.Count >= 2, "the far urgent order was dug before most of the near ones");
    }

    // ---- Geology ----------------------------------------------------------------------------------------------

    private static readonly Lazy<GeneratedWorld> Deep = new(() => WorldGenerator.GenerateWithTerrain(new WorldGenSettings(5, 96, 96, 384)));

    [Fact]
    public void Strata_GetDeeperInOrder_OverAnUnevenBedrockFloor()
    {
        var (world, terrain) = (Deep.Value.World, Deep.Value.Terrain);
        var g = world.Geology!;
        Assert.InRange(g.ZeroLevel, 1, 380);
        Assert.Equal(terrain.Surface.Min(), g.ZeroLevel);
        Assert.True(g.BedrockMax > g.BedrockMin, $"bedrock {g.BedrockMin}..{g.BedrockMax} varies");
        Assert.True(g.BedrockMin >= 2);
        var seen = new HashSet<Rock>();
        for (int y = 0; y < 96; y += 7)
        for (int x = 0; x < 96; x += 7)
        {
            int last = (int)Rock.Bedrock;
            for (int z = 0; z < terrain.SurfaceAt(x, y) - 6; z++)
            {
                var r = g.RockAt(x, y, z);
                seen.Add(r);
                if (r is Rock.Bedrock or Rock.Basalt or Rock.Granite)
                {
                    Assert.True((int)r <= last, $"({x},{y},{z}): {r} above a softer layer");
                    last = (int)r;
                }
            }
            Assert.Equal(Rock.Bedrock, g.RockAt(x, y, g.BedrockTop(x, y)));
            Assert.NotEqual(Rock.Bedrock, g.RockAt(x, y, g.BedrockTop(x, y) + 1));
        }
        foreach (var r in Enum.GetValues<Rock>()) Assert.Contains(r, seen);
    }

    [Fact]
    public void Bedrock_CannotBeDug_AndDeepRockIsSlower()
    {
        var world = Deep.Value.World;
        var g = world.Geology!;
        var d = new Designations(world);
        var bed = new TilePos(40, 40, g.BedrockTop(40, 40));
        Assert.Equal(TileType.Stone, world.Get(bed));
        Assert.True(world.IsBedrock(bed));
        Assert.False(d.Designate(bed));
        Assert.Equal("Bedrock", world.TileName(bed));
        Assert.True(d.Designate(bed.Offset(0, 0, 1)) || world.Get(bed.Offset(0, 0, 1)) != TileType.Stone);

        int Ticks(Rock r, int below) => Designations.WorkTicks(DesignationKind.Dig, TileType.Stone, r, below);
        Assert.True(Ticks(Rock.Sandstone, 0) < Ticks(Rock.Slate, 0));
        Assert.True(Ticks(Rock.Slate, 0) < Ticks(Rock.Granite, 0));
        Assert.True(Ticks(Rock.Granite, 0) < Ticks(Rock.Basalt, 0));
        Assert.Equal(Ticks(Rock.Granite, 0) * 3 / 2, Ticks(Rock.Granite, 100)); // 100 levels below zero: half as long again
        Assert.Equal(Ticks(Rock.Granite, 0), Ticks(Rock.Granite, -50)); // above zero, no pressure
        Assert.True(Designations.WorkTicks(DesignationKind.Dig, TileType.Diamond) > Designations.WorkTicks(DesignationKind.Dig, TileType.GoldOre));
    }

    [Fact]
    public void GemsLieDeep_AndTheFinestOnlyDeepest()
    {
        Assert.DoesNotContain(Enumerable.Range(0, 200).Select(i => WorldGenerator.DepositMineral(0.8f, i / 200f)), TileInfo.IsGem);
        var deep = Enumerable.Range(0, 400).Select(i => WorldGenerator.DepositMineral(0.08f, i / 400f)).ToHashSet();
        Assert.Contains(TileType.Diamond, deep);
        Assert.Contains(TileType.Aetherite, deep);
        var middle = Enumerable.Range(0, 400).Select(i => WorldGenerator.DepositMineral(0.35f, i / 400f)).ToHashSet();
        Assert.Contains(TileType.Garnet, middle);
        Assert.DoesNotContain(TileType.Diamond, middle);

        // The generated world really has them, never in the bedrock.
        var world = Deep.Value.World;
        int gems = 0;
        for (int z = 1; z < 200; z++)
        for (int y = 0; y < 96; y++)
        for (int x = 0; x < 96; x++)
        {
            var t = world.Get(x, y, z);
            if (!TileInfo.IsMineral(t)) continue;
            Assert.True(z > world.Geology!.BedrockTop(x, y), $"{t} in the bedrock at ({x},{y},{z})");
            if (TileInfo.IsGem(t)) gems++;
        }
        Assert.True(gems > 0, "gem pockets in the deep");
    }

    [Fact]
    public void DiggingAGem_LeavesTheRoughGem_ThatCutsAndSets()
    {
        var sim = FlatSim();
        sim.World.Set(3, 3, 3, TileType.Ruby);
        Assert.Equal(ItemType.RoughGem(TileType.Ruby), ColonySim.MiningYield(new TilePos(3, 3, 3), TileType.Ruby));
        Assert.Equal("rough_ruby", ItemType.RoughGem(TileType.Ruby).Id);
        var cut = Industry.Catalog.Item("cut_diamond");
        Assert.True(cut.Def.Tier >= 1);
        Assert.True(Industry.Catalog.Item("regalia").Def.Tier > cut.Def.Tier);
    }

    [Fact]
    public void Levels_ReadFromZero()
    {
        var world = Deep.Value.World;
        Assert.Equal(world.Geology!.ZeroLevel, world.ZeroLevel);
        Assert.Equal(0, new VoxelWorld(8, 8, 8).ZeroLevel); // hand-built worlds: z itself
    }

    [Fact]
    public void Geology_IsTheSameEveryTime()
    {
        var a = new Geology(17, 200, 150, 384, 120);
        var b = new Geology(17, 200, 150, 384, 120);
        for (int i = 0; i < 2000; i++)
        {
            int x = i * 37 % 200, y = i * 53 % 150, z = i * 7 % 384;
            Assert.Equal(a.RockAt(x, y, z), b.RockAt(x, y, z));
        }
    }

    // ---- Fog of war -------------------------------------------------------------------------------------------

    [Fact]
    public void RockWithNoOpenSide_IsHidden_UntilDugTo()
    {
        var sim = FlatSim(surface: 6);
        var w = sim.World;
        Assert.False(w.IsHidden(5, 5, 6)); // the surface: open sky above
        Assert.True(w.IsHidden(5, 5, 4)); // buried
        Assert.False(w.IsHidden(5, 5, 7)); // air is never hidden
        w.Set(5, 5, 5, TileType.Air); // a pocket dug beside it
        Assert.False(w.IsHidden(5, 5, 4));
        Assert.False(w.IsHidden(6, 5, 5));
        Assert.True(w.IsHidden(7, 5, 5));
        Assert.True(w.IsHidden(0, 0, 0)); // the map's sides and bottom are no view
    }
}
