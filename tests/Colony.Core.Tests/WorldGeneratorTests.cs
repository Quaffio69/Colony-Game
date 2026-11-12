using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class WorldGeneratorTests
{
    private static readonly WorldGenSettings Settings = new(Seed: 42, Width: 32, Depth: 32, Height: 24);

    [Fact]
    public void SameSeed_ProducesIdenticalWorld()
    {
        var a = WorldGenerator.Generate(Settings);
        var b = WorldGenerator.Generate(Settings);

        for (int z = 0; z < a.Height; z++)
        for (int y = 0; y < a.Depth; y++)
        for (int x = 0; x < a.Width; x++)
            Assert.Equal(a.Get(x, y, z), b.Get(x, y, z));
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentWorlds()
    {
        var a = WorldGenerator.Generate(Settings);
        var b = WorldGenerator.Generate(Settings with { Seed = 43 });

        bool anyDifference = false;
        for (int y = 0; y < a.Depth && !anyDifference; y++)
        for (int x = 0; x < a.Width && !anyDifference; x++)
            anyDifference = a.SurfaceLevel(x, y) != b.SurfaceLevel(x, y);

        Assert.True(anyDifference);
    }

    [Fact]
    public void EveryColumn_HasGrassOnTop_SolidBottom_AndOpenSky()
    {
        var w = WorldGenerator.Generate(Settings);

        for (int y = 0; y < w.Depth; y++)
        for (int x = 0; x < w.Width; x++)
        {
            int surface = w.SurfaceLevel(x, y);
            Assert.InRange(surface, 1, w.Height - 2);
            Assert.Equal(TileType.Grass, w.Get(x, y, surface));
            Assert.True(w.IsSolid(x, y, 0));
            Assert.Equal(TileType.Air, w.Get(x, y, w.Height - 1));
        }
    }

    [Fact]
    public void CentreIsHigherThanEdges()
    {
        var w = WorldGenerator.Generate(Settings);
        int centre = w.SurfaceLevel(w.Width / 2, w.Depth / 2);
        int corner = w.SurfaceLevel(0, 0);
        Assert.True(centre > corner, $"Expected mountain at centre: centre={centre}, corner={corner}");
    }

    [Fact]
    public void OutOfBoundsReads_ReturnAir()
    {
        var w = new VoxelWorld(4, 4, 4);
        Assert.Equal(TileType.Air, w.Get(-1, 0, 0));
        Assert.Equal(TileType.Air, w.Get(0, 0, 99));
    }
}
