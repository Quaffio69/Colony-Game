using Colony.Core.Navigation;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class DesignationTests
{
    private static (VoxelWorld, Designations) Setup()
    {
        var w = new VoxelWorld(8, 8, 4);
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            w.Set(x, y, 0, TileType.Stone);
        return (w, new Designations(w));
    }

    /// <summary>Solid block of stone up to <paramref name="surface"/>.</summary>
    private static VoxelWorld Block(int size, int height, int surface)
    {
        var w = new VoxelWorld(size, size, height);
        for (int z = 0; z <= surface; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            w.Set(x, y, z, TileType.Stone);
        return w;
    }

    [Fact]
    public void OnlySolidInBoundsTiles_CanBeDesignated()
    {
        var (_, d) = Setup();
        Assert.True(d.Designate(new TilePos(1, 1, 0)));
        Assert.False(d.Designate(new TilePos(1, 1, 0)));  // duplicate
        Assert.False(d.Designate(new TilePos(1, 1, 1)));  // air
        Assert.False(d.Designate(new TilePos(-1, 1, 0))); // out of bounds
        Assert.Equal(1, d.Count);
    }

    [Fact]
    public void Box_DesignatesAndCancels_InAnyCornerOrder()
    {
        var (_, d) = Setup();
        Assert.Equal(12, d.DesignateBox(new TilePos(5, 4, 1), new TilePos(2, 2, 0))); // 4x3 solid at z=0
        Assert.Equal(4, d.CancelBox(new TilePos(2, 2, 0), new TilePos(5, 2, 0)));
        Assert.Equal(8, d.Count);
    }

    [Fact]
    public void Complete_DigsOrCarves_AndRemovesDesignation()
    {
        var (w, d) = Setup();
        var dig = new TilePos(3, 3, 0);
        var carve = new TilePos(4, 4, 0);
        d.Designate(dig);
        d.Designate(carve, DesignationKind.UpDownStair);

        Assert.True(d.Complete(dig));
        Assert.True(d.Complete(carve));
        Assert.Equal(TileType.Air, w.Get(dig));
        Assert.Equal(TileType.UpDownStair, w.Get(carve));
        Assert.Equal(0, d.Count);
        Assert.False(d.Complete(new TilePos(5, 5, 0))); // never designated
    }

    [Fact]
    public void Claims_AreExclusive_AndClearedOnCancel()
    {
        var (_, d) = Setup();
        var p = new TilePos(2, 2, 0);
        d.Designate(p);
        Assert.True(d.Claim(p, 1));
        Assert.False(d.Claim(p, 2));
        Assert.Equal(0, d.UnclaimedCount);
        d.Release(p, 2); // not the owner: ignored
        Assert.True(d.IsClaimedBy(p, 1));
        d.Cancel(p);
        Assert.False(d.IsClaimed(p));
    }

    [Fact]
    public void StairDesignations_CombineDirections_LikeDF()
    {
        Assert.Equal(TileType.UpDownStair, Designations.Result(DesignationKind.UpStair, TileType.DownStair));
        Assert.Equal(TileType.UpDownStair, Designations.Result(DesignationKind.DownStair, TileType.UpStair));
        Assert.Equal(TileType.UpStair, Designations.Result(DesignationKind.UpStair, TileType.Stone));
        Assert.Equal(TileType.Air, Designations.Result(DesignationKind.Dig, TileType.UpDownStair));

        var w = Block(8, 8, 4);
        var d = new Designations(w);
        w.Set(2, 2, 4, TileType.DownStair);
        Assert.False(d.Designate(new TilePos(2, 2, 4), DesignationKind.DownStair)); // already that
        Assert.True(d.Designate(new TilePos(2, 2, 4), DesignationKind.UpStair));
        Assert.True(d.Designate(new TilePos(3, 3, 4), DesignationKind.Dig));
    }

    [Fact]
    public void StairsBox_PutsDownOnTop_UpDownBetween_UpAtTheBottom()
    {
        var w = Block(8, 12, 9);
        var d = new Designations(w);
        // Corners in any order, spanning levels 9..5.
        Assert.Equal(2 * 5, d.DesignateStairs(new TilePos(2, 2, 5), new TilePos(3, 2, 9)));
        Assert.True(d.TryGet(new TilePos(2, 2, 9), out var top) && top == DesignationKind.DownStair);
        Assert.True(d.TryGet(new TilePos(3, 2, 7), out var mid) && mid == DesignationKind.UpDownStair);
        Assert.True(d.TryGet(new TilePos(3, 2, 5), out var bottom) && bottom == DesignationKind.UpStair);

        // A single level gets up/down stairs; the world's bottom layer is never designated.
        Assert.Equal(1, d.DesignateStairs(new TilePos(5, 5, 3), new TilePos(5, 5, 3)));
        Assert.True(d.TryGet(new TilePos(5, 5, 3), out var single) && single == DesignationKind.UpDownStair);
        d.DesignateStairs(new TilePos(6, 6, 0), new TilePos(6, 6, 2));
        Assert.False(d.Contains(new TilePos(6, 6, 0)));
    }

    [Theory]
    [InlineData(1, 1, 6)]
    [InlineData(2, 2, 8)]
    [InlineData(1, 3, 2)]
    public void CompletedStaircase_ConnectsSurfaceToBottom(int sizeX, int sizeY, int depth)
    {
        const int surface = 10;
        var w = Block(12, 16, surface);
        var d = new Designations(w);
        int bottomLevel = surface - depth + 1;
        var a = new TilePos(4, 4, surface);
        var b = new TilePos(4 + sizeX - 1, 4 + sizeY - 1, bottomLevel);

        Assert.Equal(sizeX * sizeY * depth, d.DesignateStairs(a, b));
        foreach (var p in d.All.Keys.ToList()) d.Complete(p);
        Assert.Equal(TileType.UpStair, w.Get(4, 4, bottomLevel));

        var top = new TilePos(1, 1, surface + 1);
        Assert.True(Movement.IsStandable(w, top));
        var down = Pathfinder.FindNearest(w, top, p => p.Z == bottomLevel, out var reached);
        Assert.NotNull(down);
        Assert.Equal(bottomLevel, reached.Z);
        Assert.NotNull(Pathfinder.FindPath(w, reached, top)); // and back up

        // Every level change on the way is a cheap stair move.
        var cost = Pathfinder.PathCost(w, top, down)!.Value;
        Assert.True(cost <= depth * Movement.StairCost + 6, $"cost {cost}");
    }
}
