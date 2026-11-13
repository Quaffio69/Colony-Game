using Colony.Core.Navigation;
using Colony.Core.Orders;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class ConnectivityTests
{
    private static VoxelWorld Flat(int size = 40, int height = 20, int surface = 3)
    {
        var w = new VoxelWorld(size, size, height);
        for (int z = 0; z <= surface; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            w.Set(x, y, z, TileType.Stone);
        return w;
    }

    /// <summary>Ground truth: flood fill with the movement rules from <paramref name="start"/>.</summary>
    private static HashSet<TilePos> Reachable(VoxelWorld w, TilePos start)
    {
        var seen = new HashSet<TilePos> { start };
        var stack = new Stack<TilePos>([start]);
        var moves = new List<(TilePos, float)>();
        while (stack.TryPop(out var p))
        {
            Movement.GetMoves(w, p, moves);
            foreach (var (q, _) in moves)
                if (seen.Add(q)) stack.Push(q);
        }
        return seen;
    }

    private static List<TilePos> AllStandable(VoxelWorld w)
    {
        var list = new List<TilePos>();
        for (int z = 1; z < w.Height; z++)
        for (int y = 0; y < w.Depth; y++)
        for (int x = 0; x < w.Width; x++)
            if (Movement.IsStandable(w, new TilePos(x, y, z))) list.Add(new TilePos(x, y, z));
        return list;
    }

    /// <summary>Components must partition standable tiles exactly like the brute-force flood fill does.</summary>
    private static void AssertMatchesFloodFill(VoxelWorld w, Connectivity c)
    {
        var standable = AllStandable(w);
        var unvisited = new HashSet<TilePos>(standable);
        var componentsSeen = new HashSet<int>();
        while (unvisited.Count > 0)
        {
            var start = unvisited.First();
            var region = Reachable(w, start);
            int id = c.ComponentOf(start);
            Assert.True(id >= 0, $"{start} is standable but has no component");
            Assert.True(componentsSeen.Add(id), $"component {id} also covers another, disconnected region");
            foreach (var p in region)
            {
                Assert.Equal(id, c.ComponentOf(p));
                unvisited.Remove(p);
            }
        }
        Assert.Equal(componentsSeen.Count, c.ComponentCount);
    }

    [Fact]
    public void NonStandableTiles_HaveNoComponent()
    {
        var w = Flat();
        var c = new Connectivity(w);
        c.Update();
        Assert.Equal(-1, c.ComponentOf(new TilePos(5, 5, 2)));  // inside rock
        Assert.Equal(-1, c.ComponentOf(new TilePos(5, 5, 9)));  // mid-air
        Assert.Equal(-1, c.ComponentOf(new TilePos(-1, 5, 4))); // out of bounds
        Assert.True(c.ComponentOf(new TilePos(5, 5, 4)) >= 0);
        Assert.Equal(1, c.ComponentCount);
    }

    [Fact]
    public void Plateau_IsSeparateUntilAStaircaseConnectsIt()
    {
        var w = Flat(surface: 3);
        // A 3-level-high plateau: unreachable by single-level climbs.
        for (int z = 4; z <= 6; z++)
        for (int y = 20; y < 30; y++)
        for (int x = 20; x < 30; x++)
            w.Set(x, y, z, TileType.Stone);

        var c = new Connectivity(w);
        c.Update();
        var ground = new TilePos(5, 5, 4);
        var top = new TilePos(25, 25, 7);
        Assert.False(c.Connected(ground, top));

        // Cut a step into the plateau's side: 1-level climbs all the way up.
        w.Set(19, 22, 4, TileType.Stone);              // step 1 (ground is z=4 standing, step top -> stand at 5)
        w.Set(19, 23, 4, TileType.Stone); w.Set(19, 23, 5, TileType.Stone); // step 2 -> stand at 6
        c.Update();
        Assert.True(c.Connected(ground, top));
        AssertMatchesFloodFill(w, c);
    }

    [Fact]
    public void DiggingAMoat_SplitsTheComponent()
    {
        var w = Flat(size: 32, surface: 6);
        var c = new Connectivity(w);
        c.Update();
        var west = new TilePos(4, 10, 7);
        var east = new TilePos(28, 10, 7);
        Assert.True(c.Connected(west, east));

        // A 3-deep trench across the whole map: can't climb out of it or jump over it.
        for (int y = 0; y < 32; y++)
        for (int z = 4; z <= 6; z++)
        for (int x = 15; x <= 17; x++)
            w.Set(x, y, z, TileType.Air);
        c.Update();
        Assert.False(c.Connected(west, east));
        AssertMatchesFloodFill(w, c);
    }

    [Fact]
    public void MatchesFloodFill_OnGeneratedTerrain_BeforeAndAfterDigging()
    {
        var w = WorldGenerator.Generate(new WorldGenSettings(Seed: 11, Width: 48, Depth: 40, Height: 36));
        var c = new Connectivity(w);
        c.Update();
        AssertMatchesFloodFill(w, c);

        // Random digging, including on chunk borders, then incremental update.
        var rng = new Random(5);
        for (int i = 0; i < 400; i++)
        {
            int x = rng.Next(w.Width), y = rng.Next(w.Depth);
            int z = Math.Max(1, w.SurfaceLevel(x, y) - rng.Next(3));
            w.Set(x, y, z, TileType.Air);
        }
        c.Update();
        AssertMatchesFloodFill(w, c);
    }

    [Fact]
    public void CorridorRestrictedPaths_AlwaysExist_AndStayNearOptimal()
    {
        var w = WorldGenerator.Generate(new WorldGenSettings(Seed: 21, Width: 96, Depth: 96, Height: 40));
        var c = new Connectivity(w);
        c.Update();
        var rng = new Random(9);
        TilePos Surface(int x, int y) => new(x, y, w.SurfaceLevel(x, y) + 1);

        int tried = 0;
        for (int i = 0; i < 60; i++)
        {
            var a = Surface(rng.Next(96), rng.Next(96));
            var b = Surface(rng.Next(96), rng.Next(96));
            var goals = new HashSet<TilePos> { b };
            var corridor = c.Corridor(a, goals);
            Assert.Equal(c.Connected(a, b), corridor != null);
            if (corridor == null) continue;
            tried++;

            var restricted = Pathfinder.FindPathToAny(w, a, goals, b, int.MaxValue, p => corridor.Contains(c.RegionOf(p)));
            Assert.NotNull(restricted);
            var optimal = Pathfinder.FindPath(w, a, b, int.MaxValue)!;
            float rc = Pathfinder.PathCost(w, a, restricted)!.Value, oc = Pathfinder.PathCost(w, a, optimal)!.Value;
            Assert.True(rc <= oc * 1.6f + 4f, $"{a}->{b}: corridor path {rc:0.0} vs optimal {oc:0.0}");
        }
        Assert.True(tried > 20);
    }

    [Fact]
    public void Update_OnlyRelabelsChangedChunks()
    {
        var w = Flat(size: 64, height: 32);
        var c = new Connectivity(w);
        c.Update();
        int v = c.Version;

        c.Update();
        Assert.Equal(v, c.Version); // nothing changed: no work

        w.Set(40, 40, 3, TileType.Air); // interior of one chunk column at its z border region
        c.Update();
        Assert.True(c.LastRelabelCount <= 4, $"relabelled {c.LastRelabelCount} chunks");
    }

    [Fact]
    public void JobBoard_SkipsBuriedJobs_AndReportsComponents()
    {
        var w = Flat(surface: 6);
        var d = new Designations(w);
        var c = new Connectivity(w);
        d.Designate(new TilePos(10, 10, 6));  // surface tile: workable
        d.Designate(new TilePos(20, 20, 2));  // buried: no standable tile within reach
        c.Update();

        var board = new JobBoard();
        board.Update(w, d, c);
        Assert.Single(board.Jobs);
        Assert.Equal(1, board.InaccessibleCount);
        Assert.Equal(new TilePos(10, 10, 6), board.Jobs[0].Target);
        Assert.DoesNotContain(new TilePos(10, 10, 7), board.Jobs[0].WorkSpots); // can't dig your own floor
    }
}
