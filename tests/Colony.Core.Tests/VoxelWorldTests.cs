using Colony.Core.World;

namespace Colony.Core.Tests;

public class VoxelWorldTests
{
    [Fact]
    public void NonMultipleDimensions_RoundTripEveryTile()
    {
        var w = new VoxelWorld(20, 17, 33);
        Assert.Equal((2, 2, 3), (w.ChunksX, w.ChunksY, w.ChunksZ));

        for (int z = 0; z < w.Height; z++)
        for (int y = 0; y < w.Depth; y++)
        for (int x = 0; x < w.Width; x++)
            w.Set(x, y, z, (TileType)(1 + (x + y + z) % 4));

        for (int z = 0; z < w.Height; z++)
        for (int y = 0; y < w.Depth; y++)
        for (int x = 0; x < w.Width; x++)
            Assert.Equal((TileType)(1 + (x + y + z) % 4), w.Get(x, y, z));
    }

    [Fact]
    public void SetOutOfBounds_Throws()
    {
        var w = new VoxelWorld(4, 4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Set(4, 0, 0, TileType.Stone));
    }

    [Fact]
    public void SolidCount_TracksChanges()
    {
        var w = new VoxelWorld(16, 16, 16);
        var c = w.GetChunk(0, 0, 0)!;
        w.Set(1, 1, 1, TileType.Stone);
        w.Set(2, 1, 1, TileType.Soil);
        w.Set(2, 1, 1, TileType.Ore); // solid -> solid: no change in count
        Assert.Equal(2, c.SolidCount);
        w.Set(1, 1, 1, TileType.Air);
        Assert.Equal(1, c.SolidCount);
    }

    [Fact]
    public void InteriorSet_BumpsOnlyOwnChunkVersion()
    {
        var w = new VoxelWorld(48, 48, 48);
        var before = w.Chunks.Select(c => c.Version).ToArray();

        w.Set(20, 20, 20, TileType.Stone); // interior of chunk (1,1,1)

        var changed = w.Chunks.Where((c, i) => c.Version != before[i]).Select(c => c.Coord).ToList();
        Assert.Equal([new ChunkCoord(1, 1, 1)], changed);
    }

    [Fact]
    public void BorderSet_AlsoBumpsTouchingNeighbours()
    {
        var w = new VoxelWorld(48, 48, 48);
        var before = w.Chunks.Select(c => c.Version).ToArray();

        w.Set(16, 31, 20, TileType.Stone); // chunk (1,1,1): on its -x and +y faces

        var changed = w.Chunks.Where((c, i) => c.Version != before[i]).Select(c => c.Coord).ToHashSet();
        // Face neighbours and the diagonal one between them (diagonal moves can cross that corner).
        Assert.Equal(new HashSet<ChunkCoord> { new(1, 1, 1), new(0, 1, 1), new(1, 2, 1), new(0, 2, 1) }, changed);
    }

    [Fact]
    public void SettingSameValue_DoesNotBumpVersion()
    {
        var w = new VoxelWorld(16, 16, 16);
        w.Set(3, 3, 3, TileType.Stone);
        int v = w.GetChunk(0, 0, 0)!.Version;
        w.Set(3, 3, 3, TileType.Stone);
        w.Set(4, 4, 4, TileType.Air);
        Assert.Equal(v, w.GetChunk(0, 0, 0)!.Version);
    }
}
