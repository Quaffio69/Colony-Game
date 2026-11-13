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
        d.Designate(carve, DesignationKind.CarveStairs);

        Assert.True(d.Complete(dig));
        Assert.True(d.Complete(carve));
        Assert.Equal(TileType.Air, w.Get(dig));
        Assert.Equal(TileType.Stairs, w.Get(carve));
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
    public void PerimeterRing_IsAClosedOrthogonalLoop()
    {
        foreach (var (w, h) in new[] { (2, 2), (3, 2), (4, 5) })
        {
            var ring = Designations.PerimeterRing(0, 0, w - 1, h - 1);
            Assert.Equal(2 * (w + h) - 4, ring.Count);
            Assert.Equal(ring.Count, ring.Distinct().Count());
            for (int i = 0; i < ring.Count; i++)
            {
                var (ax, ay) = ring[i];
                var (bx, by) = ring[(i + 1) % ring.Count];
                Assert.Equal(1, Math.Abs(ax - bx) + Math.Abs(ay - by));
            }
        }
    }

    [Fact]
    public void Stairs_RequireTwoByTwo()
    {
        var w = Block(8, 12, 9);
        var d = new Designations(w);
        Assert.Throws<ArgumentException>(() => d.DesignateStairs(new TilePos(2, 2, 9), new TilePos(2, 5, 9), 9, 4));
    }

    [Theory]
    [InlineData(2, 2, 6)]
    [InlineData(3, 3, 8)]
    [InlineData(2, 4, 5)]
    public void CompletedStaircase_ConnectsSurfaceToBottom(int sizeX, int sizeY, int depth)
    {
        const int surface = 10;
        var w = Block(12, 16, surface);
        var d = new Designations(w);
        var a = new TilePos(4, 4, surface);
        var b = new TilePos(4 + sizeX - 1, 4 + sizeY - 1, surface);

        int n = d.DesignateStairs(a, b, surface, depth);
        Assert.Equal(sizeX * sizeY * depth, n);
        Assert.Equal(depth, d.All.Count(kv => kv.Value == DesignationKind.CarveStairs));

        foreach (var p in d.All.Keys.ToList()) d.Complete(p);

        var top = new TilePos(1, 1, surface + 1);
        int bottomLevel = surface - depth + 1; // standing on the undug level below the shaft
        var bottom = new TilePos(4, 4, bottomLevel);
        Assert.True(Movement.IsStandable(w, top));

        var down = Pathfinder.FindNearest(w, top, p => p.Z == bottomLevel, out var reached);
        Assert.NotNull(down);
        Assert.Equal(bottomLevel, reached.Z);

        // And back up again.
        Assert.NotNull(Pathfinder.FindPath(w, reached, top));

        // The descent should use stair-cost climbs, not raw climbs.
        var cost = Pathfinder.PathCost(w, top, down)!.Value;
        Assert.True(cost < depth * Movement.ClimbDownCost + 10, $"cost {cost}");
        _ = bottom;
    }
}
