using Colony.Core.Agents;
using Colony.Core.Navigation;
using Colony.Core.Orders;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class ColonistTests
{
    private static ColonySim FlatSim(int surface = 3)
    {
        var w = new VoxelWorld(24, 24, 16);
        for (int z = 0; z <= surface; z++)
        for (int y = 0; y < 24; y++)
        for (int x = 0; x < 24; x++)
            w.Set(x, y, z, TileType.Stone);
        return new ColonySim(w);
    }

    private static void Run(ColonySim sim, int ticks)
    {
        for (int i = 0; i < ticks; i++) sim.Step();
    }

    [Fact]
    public void Colonist_WalksOverAndDigsDesignatedTile()
    {
        var sim = FlatSim();
        var c = sim.AddColonist("Test", new TilePos(2, 2, 4));
        var target = new TilePos(12, 9, 3);
        sim.Designations.Designate(target);

        Run(sim, 20);
        Assert.Equal(ColonistState.Moving, c.State);
        Assert.Equal(target, c.JobTarget);

        Run(sim, 600);
        Assert.Equal(TileType.Air, sim.World.Get(target));
        Assert.Equal(0, sim.Designations.Count);
        Assert.Equal(ColonistState.Idle, c.State);
        Assert.True(Colonist.InReach(c.Position, target));
    }

    [Fact]
    public void Colonists_ShareWork_AndFinishAPit()
    {
        var sim = FlatSim();
        for (int i = 0; i < 3; i++) sim.AddColonist($"C{i}", new TilePos(2 + i, 2, 4));
        sim.Designations.DesignateBox(new TilePos(8, 8, 3), new TilePos(12, 11, 3));

        int maxClaimedAtOnce = 0;
        for (int i = 0; i < 3000 && sim.Designations.Count > 0; i++)
        {
            sim.Step();
            maxClaimedAtOnce = Math.Max(maxClaimedAtOnce, sim.Designations.Count - sim.Designations.UnclaimedCount);
        }

        Assert.Equal(0, sim.Designations.Count);
        Assert.Equal(3, maxClaimedAtOnce);
        for (int y = 8; y <= 11; y++)
        for (int x = 8; x <= 12; x++)
            Assert.Equal(TileType.Air, sim.World.Get(x, y, 3));
        Assert.All(sim.Colonists, c => Assert.True(Movement.IsStandable(sim.World, c.Position)));
    }

    [Fact]
    public void Colonists_DigAStaircaseDown_AndCanUseIt()
    {
        const int surface = 10;
        var sim = FlatSim(surface);
        var c = sim.AddColonist("Digger", new TilePos(3, 3, surface + 1));
        sim.AddColonist("Helper", new TilePos(4, 3, surface + 1));
        sim.Designations.DesignateStairs(new TilePos(8, 8, surface), new TilePos(9, 9, surface), surface, depth: 6);

        for (int i = 0; i < 20000 && sim.Designations.Count > 0; i++) sim.Step();

        Assert.Equal(0, sim.Designations.Count);
        Assert.Equal(6, Enumerable.Range(surface - 5, 6).Count(z =>
            Enumerable.Range(8, 2).Any(x => Enumerable.Range(8, 2).Any(y => sim.World.Get(x, y, z) == TileType.Stairs))));

        // Now the bottom is reachable from the surface.
        var bottom = Pathfinder.FindNearest(sim.World, c.Position, p => p.Z == surface - 5, out _);
        Assert.NotNull(bottom);
    }

    [Fact]
    public void CancellingAJob_SendsColonistBackToIdle()
    {
        var sim = FlatSim();
        var c = sim.AddColonist("Test", new TilePos(2, 2, 4));
        var target = new TilePos(15, 15, 3);
        sim.Designations.Designate(target);
        Run(sim, 10);
        Assert.Equal(ColonistState.Moving, c.State);

        sim.Designations.Cancel(target);
        Run(sim, 1);
        Assert.Equal(ColonistState.Idle, c.State);
        Assert.Null(c.JobTarget);
    }

    [Fact]
    public void UnreachableJobs_AreLeftAlone()
    {
        var sim = FlatSim();
        var c = sim.AddColonist("Test", new TilePos(2, 2, 4));
        sim.Designations.Designate(new TilePos(12, 12, 0)); // buried deep under solid rock
        Run(sim, 200);
        Assert.Equal(ColonistState.Idle, c.State);
        Assert.Equal(1, sim.Designations.UnclaimedCount);
    }

    [Fact]
    public void UnreachableJobs_CostNoPathSearches()
    {
        var sim = FlatSim();
        for (int i = 0; i < 7; i++) sim.AddColonist($"C{i}", new TilePos(2 + i, 2, 4));
        // A sealed 3-high plateau with designations on top: workable spots exist but can't be reached.
        for (int z = 4; z <= 6; z++)
        for (int y = 14; y < 20; y++)
        for (int x = 14; x < 20; x++)
            sim.World.Set(x, y, z, TileType.Stone);
        sim.Designations.DesignateBox(new TilePos(14, 14, 6), new TilePos(19, 19, 6));

        Run(sim, 2000);
        Assert.Equal(0, sim.PathSearches);
        Assert.All(sim.Colonists, c => Assert.Equal(ColonistState.Idle, c.State));

        // A reachable job elsewhere still gets done promptly, and only it gets searched for.
        sim.Designations.Designate(new TilePos(5, 10, 3));
        Run(sim, 300);
        Assert.Equal(TileType.Air, sim.World.Get(5, 10, 3));
        Assert.True(sim.PathSearches <= 2, $"{sim.PathSearches} searches");
    }

    [Fact]
    public void ColonistStandingOnDugTile_FallsToNewFloor()
    {
        var sim = FlatSim();
        var c = sim.AddColonist("Faller", new TilePos(5, 5, 4));
        sim.World.Set(5, 5, 3, TileType.Air);
        sim.World.Set(5, 5, 2, TileType.Air);
        sim.Step();
        Assert.Equal(new TilePos(5, 5, 2), c.Position);
    }

    [Fact]
    public void Simulation_IsDeterministic()
    {
        string Snapshot()
        {
            var sim = ColonySim.Generate(new WorldGenSettings(Seed: 99, Width: 48, Depth: 48, Height: 32));
            var first = sim.Colonists[0].Position;
            sim.Designations.DesignateBox(first.Offset(-4, 3, -1), first.Offset(4, 8, -1));
            for (int i = 0; i < 1500; i++) sim.Step();
            return string.Join(";", sim.Colonists.Select(c => $"{c.Name}@{c.Position}:{c.State}")) +
                   $"|{sim.Designations.Count}";
        }

        Assert.Equal(Snapshot(), Snapshot());
    }

    [Fact]
    public void GeneratedColony_SpawnsStandingColonistsWithNames()
    {
        var sim = ColonySim.Generate(new WorldGenSettings(Seed: 1, Width: 64, Depth: 64, Height: 40));
        Assert.Equal(7, sim.Colonists.Count);
        Assert.All(sim.Colonists, c =>
        {
            Assert.True(Movement.IsStandable(sim.World, c.Position));
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
        });
        Assert.Equal(7, sim.Colonists.Select(c => c.Position).Distinct().Count());
    }
}
