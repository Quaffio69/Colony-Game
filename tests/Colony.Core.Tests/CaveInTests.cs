using Colony.Core.Orders;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class CaveInTests
{
    private static VoxelWorld Flat(int size = 24, int height = 16, int surface = 3)
    {
        var w = new VoxelWorld(size, size, height);
        for (int z = 0; z <= surface; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            w.Set(x, y, z, TileType.Stone);
        return w;
    }

    private static void Fill(VoxelWorld w, int x0, int y0, int z0, int x1, int y1, int z1, TileType t = TileType.Stone)
    {
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            w.Set(x, y, z, t);
    }

    private static int CountSolid(VoxelWorld w)
    {
        int n = 0;
        for (int z = 0; z < w.Height; z++)
        for (int y = 0; y < w.Depth; y++)
        for (int x = 0; x < w.Width; x++)
            if (w.IsSolid(x, y, z)) n++;
        return n;
    }

    /// <summary>Ground truth: every solid tile reaches z=0 through face-adjacent solid tiles.</summary>
    private static void AssertAllSupported(VoxelWorld w)
    {
        var seen = new HashSet<TilePos>();
        var stack = new Stack<TilePos>();
        for (int y = 0; y < w.Depth; y++)
        for (int x = 0; x < w.Width; x++)
            if (w.IsSolid(x, y, 0) && seen.Add(new TilePos(x, y, 0))) stack.Push(new TilePos(x, y, 0));
        while (stack.TryPop(out var p))
        {
            foreach (var q in new[] { p.Offset(1, 0, 0), p.Offset(-1, 0, 0), p.Offset(0, 1, 0), p.Offset(0, -1, 0), p.Offset(0, 0, 1), p.Offset(0, 0, -1) })
                if (w.InBounds(q) && w.IsSolid(q.X, q.Y, q.Z) && seen.Add(q)) stack.Push(q);
        }
        for (int z = 0; z < w.Height; z++)
        for (int y = 0; y < w.Depth; y++)
        for (int x = 0; x < w.Width; x++)
            if (w.IsSolid(x, y, z)) Assert.True(seen.Contains(new TilePos(x, y, z)), $"({x},{y},{z}) is floating");
    }

    /// <summary>A pillar standing on flat ground at (10,10), z=4..8, with a 5x5 slab on top at z=9.</summary>
    private static VoxelWorld Mushroom()
    {
        var w = Flat();
        Fill(w, 10, 10, 4, 10, 10, 8);
        Fill(w, 8, 8, 9, 12, 12, 9);
        return w;
    }

    [Fact]
    public void CuttingAPillar_DropsEverythingAboveAsOnePiece()
    {
        var w = Mushroom();
        var sim = new ColonySim(w);
        int before = CountSolid(w);
        sim.Designations.Designate(new TilePos(10, 10, 6));
        Assert.True(sim.CompleteDesignation(new TilePos(10, 10, 6)));

        Assert.Equal(1, sim.CaveInCount);
        var c = sim.RecentCaveIns[0];
        Assert.Equal(27, c.Tiles); // 2 pillar tiles + 25 slab tiles
        Assert.Equal(1, c.Drop);   // the upper pillar lands on the stump
        Assert.Equal(before - 1, CountSolid(w));
        Assert.True(w.IsSolid(10, 10, 6) && w.IsSolid(10, 10, 7));
        Assert.True(w.IsSolid(8, 8, 8) && w.IsSolid(12, 12, 8));
        Assert.False(w.IsSolid(8, 8, 9));
        AssertAllSupported(w);
    }

    [Fact]
    public void SupportedStructures_StayPut()
    {
        var w = Flat();
        // An arch: two legs joined by a bridge. Cutting one leg leaves it hanging off the other.
        Fill(w, 5, 5, 4, 5, 5, 7);
        Fill(w, 9, 5, 4, 9, 5, 7);
        Fill(w, 5, 5, 8, 9, 5, 8);
        var sim = new ColonySim(w);
        w.Set(5, 5, 5, TileType.Air);
        sim.CheckSupport(new TilePos(5, 5, 5));
        Assert.Equal(0, sim.CaveInCount);
        Assert.True(w.IsSolid(5, 5, 7));

        // Digging a pit in solid ground never caves anything in.
        w.Set(15, 15, 3, TileType.Air);
        sim.CheckSupport(new TilePos(15, 15, 3));
        Assert.Equal(0, sim.CaveInCount);
    }

    [Fact]
    public void CuttingTheLastSupport_SplitsIntoIndependentFalls()
    {
        var w = Flat();
        // A T: two arms hang from the tile at (10,10,8), which sits on a pillar.
        Fill(w, 10, 10, 4, 10, 10, 8);
        Fill(w, 7, 10, 8, 13, 10, 8);
        var sim = new ColonySim(w);
        w.Set(10, 10, 8, TileType.Air);
        sim.CheckSupport(new TilePos(10, 10, 8));
        Assert.Equal(2, sim.CaveInCount);
        Assert.All(sim.RecentCaveIns, c => Assert.Equal((3, 4), (c.Tiles, c.Drop)));
        Assert.True(w.IsSolid(7, 10, 4) && w.IsSolid(13, 10, 4));
        AssertAllSupported(w);
    }

    [Fact]
    public void DesignationsOnFallenRock_AreCancelled()
    {
        var w = Mushroom();
        var sim = new ColonySim(w);
        sim.Designations.Designate(new TilePos(8, 8, 9));
        sim.Designations.Designate(new TilePos(10, 10, 6));
        sim.CompleteDesignation(new TilePos(10, 10, 6));
        Assert.False(sim.Designations.Contains(new TilePos(8, 8, 9)));
        Assert.Equal(0, sim.Designations.Count);
    }

    [Fact]
    public void ColonistRidingTheMass_FallsWithIt()
    {
        var w = Mushroom();
        var sim = new ColonySim(w);
        var rider = sim.AddColonist("Rider", new TilePos(8, 8, 10));
        sim.Designations.Designate(new TilePos(10, 10, 6));
        sim.CompleteDesignation(new TilePos(10, 10, 6));
        sim.Step();
        Assert.Equal(new TilePos(8, 8, 9), rider.Position);
    }

    [Fact]
    public void ColonistUnderneath_EndsUpOnTopInsteadOfInsideTheRock()
    {
        var w = Flat();
        // A slab one level above the ground, held by a single corner post; the colonist stands under it.
        Fill(w, 8, 8, 5, 12, 12, 5);
        w.Set(12, 12, 4, TileType.Stone);
        var sim = new ColonySim(w);
        var victim = sim.AddColonist("Victim", new TilePos(8, 8, 4));
        w.Set(12, 12, 4, TileType.Air); // knock out the post: the slab drops onto the colonist
        sim.CheckSupport(new TilePos(12, 12, 4));
        Assert.Equal(1, sim.RecentCaveIns[0].Drop);
        Assert.True(w.IsSolid(8, 8, 4));
        sim.Step();
        Assert.False(w.IsSolid(victim.Position.X, victim.Position.Y, victim.Position.Z));
        Assert.Equal(new TilePos(8, 8, 5), victim.Position);
    }

    [Fact]
    public void RandomDigging_OnGeneratedTerrain_NeverLeavesFloatingRock()
    {
        var w = WorldGenerator.Generate(new WorldGenSettings(Seed: 3, Width: 48, Depth: 40, Height: 36));
        var sim = new ColonySim(w);
        AssertAllSupported(w);
        var rng = new Random(17);
        for (int i = 0; i < 1500; i++)
        {
            int x = rng.Next(w.Width), y = rng.Next(w.Depth);
            int z = Math.Max(1, w.SurfaceLevel(x, y) - rng.Next(6));
            if (!w.IsSolid(x, y, z)) continue;
            w.Set(x, y, z, TileType.Air);
            sim.CheckSupport(new TilePos(x, y, z));
        }
        AssertAllSupported(w);
    }
}
