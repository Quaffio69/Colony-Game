using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>Chunk tile storage: uniform, packed (1, 2, 4 bits against a palette) or a byte a tile, always reading back what was written.</summary>
public class ChunkStorageTests
{
    private static int Index(int lx, int ly, int lz) => (lz << 8) | (ly << 4) | lx;

    private static void AssertSame(Chunk c, TileType[] expected)
    {
        for (int i = 0; i < Chunk.Volume; i++)
            Assert.Equal(expected[i], c.Get(i & 15, (i >> 4) & 15, i >> 8));
    }

    [Fact]
    public void Writes_ReadBack_ThroughEveryWidening_AndStorageGrowsOnlyAsNeeded()
    {
        var c = new Chunk(new ChunkCoord(0, 0, 0));
        var expected = new TileType[Chunk.Volume];
        Assert.True(c.IsUniform);
        Assert.Equal(0, c.StorageBytes);
        var rng = new Random(7);
        int[] kindsAt = [2, 4, 16, TileInfo.Count];
        int widest = 0;
        foreach (int kinds in kindsAt)
        {
            for (int n = 0; n < 3000; n++)
            {
                int i = rng.Next(Chunk.Volume);
                var t = (TileType)rng.Next(kinds);
                c.Set(i & 15, (i >> 4) & 15, i >> 8, t);
                expected[i] = t;
            }
            AssertSame(c, expected);
            Assert.True(c.StorageBytes >= widest, "storage only widens");
            widest = c.StorageBytes;
            if (kinds <= 16) Assert.True(c.StorageBytes <= Chunk.Volume / 2 + 16, $"{kinds} kinds packed: {c.StorageBytes} bytes");
        }
        Assert.Equal(Chunk.Volume, c.StorageBytes); // more than 16 kinds: a byte a tile
    }

    [Fact]
    public void FewKinds_PackSmall_AndCompactNarrowsAgain()
    {
        var c = new Chunk(new ChunkCoord(0, 0, 0));
        c.Set(1, 2, 3, TileType.Stone);
        Assert.Equal(Chunk.Volume / 8 + 2, c.StorageBytes); // two kinds: a bit a tile
        for (int k = 0; k < 20; k++) c.Set(k % 16, 5, k / 16, (TileType)(k + 1)); // 21 kinds: bytes
        Assert.Equal(Chunk.Volume, c.StorageBytes);
        for (int k = 0; k < 20; k++) c.Set(k % 16, 5, k / 16, TileType.Air); // back to two kinds
        c.Compact();
        Assert.Equal(Chunk.Volume / 8 + 2, c.StorageBytes);
        Assert.Equal(TileType.Stone, c.Get(1, 2, 3));
        Assert.Equal(TileType.Air, c.Get(0, 0, 0));
        c.Set(1, 2, 3, TileType.Air);
        c.Compact();
        Assert.True(c.IsUniform);
    }

    [Fact]
    public void ASavedChunk_LoadsTheSameTiles_AndSavesTheSameBytes()
    {
        var w = new VoxelWorld(16, 16, 16);
        var rng = new Random(3);
        for (int n = 0; n < 500; n++) w.Set(rng.Next(16), rng.Next(16), rng.Next(16), (TileType)rng.Next(6));
        var c = w.GetChunk(0, 0, 0)!;
        var expected = new TileType[Chunk.Volume];
        for (int i = 0; i < Chunk.Volume; i++) expected[i] = c.Get(i & 15, (i >> 4) & 15, i >> 8);

        byte[] Save(Chunk chunk)
        {
            var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) chunk.SnapSave(bw);
            return ms.ToArray();
        }
        var bytes = Save(c);
        var copy = new Chunk(new ChunkCoord(0, 0, 0));
        using (var br = new BinaryReader(new MemoryStream(bytes))) copy.SnapLoad(br);
        AssertSame(copy, expected);
        Assert.Equal(c.NonAirCount, copy.NonAirCount);
        Assert.Equal(c.SolidCount, copy.SolidCount);
        Assert.Equal(bytes, Save(copy));
        Assert.True(copy.StorageBytes <= Chunk.Volume / 2 + 16); // six kinds: packed at 4 bits
    }
}
