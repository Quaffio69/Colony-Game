using System.Numerics;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class VoxelRaycastTests
{
    private static VoxelWorld FlatWorld(int surface = 3)
    {
        var w = new VoxelWorld(20, 20, 10);
        for (int z = 0; z <= surface; z++)
        for (int y = 0; y < 20; y++)
        for (int x = 0; x < 20; x++)
            w.Set(x, y, z, TileType.Stone);
        return w;
    }

    [Fact]
    public void StraightDown_FromAboveWorld_HitsSurfaceTop()
    {
        var w = FlatWorld();
        var hit = VoxelRaycast.Cast(w, new Vector3(5.5f, 6.5f, 50f), -Vector3.UnitZ, 200f);
        Assert.NotNull(hit);
        Assert.Equal(new TilePos(5, 6, 3), hit.Value.Tile);
        Assert.Equal(new TilePos(0, 0, 1), hit.Value.Normal);
        Assert.Equal(46f, hit.Value.Distance, 0.001f);
    }

    [Fact]
    public void Diagonal_FromInsideAir_HitsSideFace()
    {
        var w = new VoxelWorld(20, 20, 10);
        w.Set(10, 5, 2, TileType.Ore);
        var hit = VoxelRaycast.Cast(w, new Vector3(2.5f, 5.5f, 2.5f), Vector3.UnitX, 100f);
        Assert.NotNull(hit);
        Assert.Equal(new TilePos(10, 5, 2), hit.Value.Tile);
        Assert.Equal(new TilePos(-1, 0, 0), hit.Value.Normal);
    }

    [Fact]
    public void MaxLevel_IgnoresTilesAboveSlice()
    {
        var w = FlatWorld(surface: 6);
        var hit = VoxelRaycast.Cast(w, new Vector3(5.5f, 5.5f, 50f), new Vector3(0.3f, 0.2f, -1f), 200f, maxLevel: 2);
        Assert.NotNull(hit);
        Assert.Equal(2, hit.Value.Tile.Z);
        Assert.Equal(new TilePos(0, 0, 1), hit.Value.Normal);
    }

    [Fact]
    public void Miss_ReturnsNull()
    {
        var w = FlatWorld();
        Assert.Null(VoxelRaycast.Cast(w, new Vector3(5, 5, 50), Vector3.UnitZ, 200f));     // pointing away
        Assert.Null(VoxelRaycast.Cast(w, new Vector3(-5, 5, 50), -Vector3.UnitX, 200f));  // beside the world
        Assert.Null(VoxelRaycast.Cast(w, new Vector3(5.5f, 5.5f, 50f), -Vector3.UnitZ, 10f)); // too short
    }

    [Fact]
    public void AngledRay_MatchesFineStepReference()
    {
        var w = Procedural.WorldGenerator.Generate(new Procedural.WorldGenSettings(3, 40, 40, 30));
        var rng = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            var o = new Vector3(rng.NextSingle() * 40, rng.NextSingle() * 40, 40 + rng.NextSingle() * 20);
            var d = Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, -1f));

            TilePos? expected = null;
            for (float t = 0; t < 120; t += 0.001f)
            {
                var p = o + d * t;
                int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
                if (w.IsSolid(x, y, z)) { expected = new TilePos(x, y, z); break; }
            }

            var hit = VoxelRaycast.Cast(w, o, d, 120f);
            Assert.Equal(expected, hit?.Tile);
        }
    }

    [Fact]
    public void IntersectLevelPlane_FindsPoint()
    {
        var p = VoxelRaycast.IntersectLevelPlane(new Vector3(0, 0, 10), new Vector3(1, 0, -1), 4f);
        Assert.NotNull(p);
        Assert.Equal(6f, p.Value.X, 0.0001f);
        Assert.Equal(4f, p.Value.Z, 0.0001f);
    }
}
