using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class NavigationTests
{
    /// <summary>16x16 world with solid floor at z=0; agents walk at z=1.</summary>
    private static VoxelWorld Flat()
    {
        var w = new VoxelWorld(16, 16, 8);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
            w.Set(x, y, 0, TileType.Stone);
        return w;
    }

    private static List<(TilePos To, float Cost)> Moves(VoxelWorld w, TilePos p)
    {
        var list = new List<(TilePos, float)>();
        Movement.GetMoves(w, p, list);
        return list;
    }

    [Fact]
    public void OpenFloor_HasEightNeighbours()
    {
        var moves = Moves(Flat(), new TilePos(5, 5, 1));
        Assert.Equal(8, moves.Count);
        Assert.Equal(4, moves.Count(m => m.Cost == Movement.FlatCost));
        Assert.Equal(4, moves.Count(m => m.Cost == Movement.DiagonalCost));
    }

    [Fact]
    public void CanClimbOneLevel_ButNotTwo()
    {
        var w = Flat();
        w.Set(6, 5, 1, TileType.Stone); // 1-high step east
        w.Set(4, 5, 1, TileType.Stone); // 2-high wall west
        w.Set(4, 5, 2, TileType.Stone);

        var moves = Moves(w, new TilePos(5, 5, 1)).Select(m => m.To).ToList();
        Assert.Contains(new TilePos(6, 5, 2), moves);
        Assert.DoesNotContain(moves, p => p.X == 4 && p.Y == 5);
    }

    [Fact]
    public void ClimbNeedsHeadroom()
    {
        var w = Flat();
        w.Set(6, 5, 1, TileType.Stone);
        w.Set(5, 5, 2, TileType.Stone); // ceiling right above the agent
        Assert.Null(Movement.MoveCost(w, new TilePos(5, 5, 1), new TilePos(6, 5, 2)));
    }

    [Fact]
    public void CanStepDownOneLevel_ButNotTwo()
    {
        var w = new VoxelWorld(8, 8, 8);
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        for (int z = 0; z <= (x < 4 ? 3 : x == 4 ? 2 : 0); z++)
            w.Set(x, y, z, TileType.Stone);
        // Surface: x<4 stands at z=4, x=4 at z=3, x>4 at z=1.

        Assert.Equal(Movement.ClimbDownCost, Movement.MoveCost(w, new TilePos(3, 3, 4), new TilePos(4, 3, 3)));
        Assert.Null(Movement.MoveCost(w, new TilePos(4, 3, 3), new TilePos(5, 3, 1)));
    }

    [Fact]
    public void Diagonals_DoNotCutCorners()
    {
        var w = Flat();
        w.Set(6, 5, 1, TileType.Stone);
        w.Set(6, 5, 2, TileType.Stone); // pillar east
        Assert.Null(Movement.MoveCost(w, new TilePos(5, 5, 1), new TilePos(6, 6, 1)));
        Assert.NotNull(Movement.MoveCost(w, new TilePos(5, 5, 1), new TilePos(4, 6, 1)));
    }

    [Fact]
    public void StairFloors_MakeClimbingCheaper()
    {
        var w = Flat();
        w.Set(6, 5, 1, TileType.Stone);
        w.Set(6, 6, 1, TileType.Stairs);
        Assert.Equal(Movement.ClimbUpCost, Movement.MoveCost(w, new TilePos(5, 5, 1), new TilePos(6, 5, 2)));
        Assert.Equal(Movement.StairCost, Movement.MoveCost(w, new TilePos(5, 6, 1), new TilePos(6, 6, 2)));
    }

    [Fact]
    public void FindPath_GoesAroundWall()
    {
        var w = Flat();
        for (int y = 0; y < 12; y++)
        {
            w.Set(8, y, 1, TileType.Stone);
            w.Set(8, y, 2, TileType.Stone);
        }

        var start = new TilePos(2, 2, 1);
        var goal = new TilePos(13, 2, 1);
        var path = Pathfinder.FindPath(w, start, goal);

        Assert.NotNull(path);
        Assert.Equal(goal, path[^1]);
        Assert.Contains(path, p => p.Y >= 12); // had to go round the end of the wall
        Assert.NotNull(Pathfinder.PathCost(w, start, path)); // every step is legal
    }

    [Fact]
    public void FindPath_ReturnsNullWhenEnclosed()
    {
        var w = Flat();
        for (int i = 3; i <= 7; i++)
        foreach (var (x, y) in new[] { (i, 3), (i, 7), (3, i), (7, i) })
        {
            w.Set(x, y, 1, TileType.Stone);
            w.Set(x, y, 2, TileType.Stone);
        }
        Assert.Null(Pathfinder.FindPath(w, new TilePos(5, 5, 1), new TilePos(12, 12, 1)));
    }

    [Fact]
    public void FindPath_IsOptimal_ComparedWithDijkstra()
    {
        var w = Procedural.WorldGenerator.Generate(new Procedural.WorldGenSettings(5, 40, 40, 24));
        TilePos Surface(int x, int y) => new(x, y, w.SurfaceLevel(x, y) + 1);
        var start = Surface(3, 3);

        foreach (var goal in new[] { Surface(36, 30), Surface(20, 20), Surface(5, 35) })
        {
            var astar = Pathfinder.FindPath(w, start, goal);
            var dijkstra = Pathfinder.FindNearest(w, start, p => p == goal, out _);
            Assert.Equal(dijkstra is null, astar is null);
            if (astar is null) continue;
            Assert.Equal(Pathfinder.PathCost(w, start, dijkstra!)!.Value, Pathfinder.PathCost(w, start, astar)!.Value, 0.001f);
        }
    }
}
