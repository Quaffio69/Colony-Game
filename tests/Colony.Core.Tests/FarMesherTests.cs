using System.Numerics;
using Colony.Core.Meshing;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class FarMesherTests
{
    private static VoxelWorld Flat(int size, int height, int groundTop)
    {
        var w = new VoxelWorld(size, size, height);
        for (int z = 0; z <= groundTop; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            w.Set(x, y, z, z == groundTop ? TileType.Grass : TileType.Stone);
        return w;
    }

    private static List<Quad> Mesh(VoxelWorld w, int x0, int y0, int cellsX, int cellsY, int cell)
    {
        int n = (cellsX + 2) * (cellsY + 2);
        var heights = new float[n];
        var types = new TileType[n];
        FarMesher.Sample(w, null, x0, y0, cellsX, cellsY, cell, heights, types);
        var quads = new List<Quad>();
        FarMesher.Build(heights, types, cellsX, cellsY, x0, y0, cell, quads);
        return quads;
    }

    private static float Area(Quad q) => Vector3.Cross(q.B - q.A, q.D - q.A).Length();

    [Fact]
    public void FlatGround_IsOneTop_AndTheMapEdgesGetSides()
    {
        var w = Flat(32, 32, 4);
        var quads = Mesh(w, 0, 0, 8, 8, 4);
        var tops = quads.Where(q => q.Dir == FaceDir.PosZ).ToList();
        var top = Assert.Single(tops);
        Assert.Equal(TileType.Grass, top.Tile);
        Assert.All(new[] { top.A, top.B, top.C, top.D }, v => Assert.Equal(5f, v.Z)); // top of z = 4
        float s = 32f + 2 * FarMesher.Seam;
        Assert.Equal(s * s, Area(top), 0.01f);
        // The block is the whole map: each map edge is one side down to the ground under the world.
        Assert.Equal(4, quads.Count(q => q.Dir != FaceDir.PosZ));
    }

    [Fact]
    public void InnerBlock_HasNoSides_OnFlatGround()
    {
        var w = Flat(64, 32, 4);
        var quads = Mesh(w, 16, 16, 8, 8, 4);
        Assert.Single(quads);
        Assert.Equal(FaceDir.PosZ, quads[0].Dir);
    }

    [Fact]
    public void ACell_TakesItsHighestColumn_AndStandsAboveItsNeighbours()
    {
        var w = Flat(64, 32, 4);
        for (int z = 5; z <= 9; z++) w.Set(33, 34, z, TileType.Stone); // one tall column in the cell (32..35, 32..35)
        var quads = Mesh(w, 16, 16, 8, 8, 4);
        var raised = quads.Single(q => q.Dir == FaceDir.PosZ && q.A.Z == 10f);
        Assert.Equal(TileType.Stone, raised.Tile);
        float c = 4f + 2 * FarMesher.Seam;
        Assert.Equal(c * c, Area(raised), 0.01f);
        var sides = quads.Where(q => q.Dir != FaceDir.PosZ).ToList();
        Assert.Equal(4, sides.Count);
        Assert.All(sides, q => Assert.Equal(c * 5.5f, Area(q), 0.01f)); // a cell wide (plus seams), from half below 5 up to 10
        // The ground round it still covers the rest of the block.
        Assert.True(quads.Where(q => q.Dir == FaceDir.PosZ).Sum(Area) >= 32f * 32f);
    }

    [Fact]
    public void Quads_FaceTheirOwnDirection()
    {
        var w = Flat(64, 40, 4);
        for (int y = 20; y < 44; y++)
        for (int x = 8; x < 30; x++)
        for (int z = 5; z < 5 + (x + y) % 7; z++) w.Set(x, y, z, TileType.Soil);
        var quads = Mesh(w, 0, 0, 16, 16, 4);
        Assert.NotEmpty(quads);
        foreach (var q in quads)
        {
            var (nx, ny, nz) = SectionMesher.Normal(q.Dir);
            Assert.True(Vector3.Dot(Vector3.Cross(q.B - q.A, q.C - q.A), new Vector3(nx, ny, nz)) > 0, $"{q}");
        }
    }

    [Fact]
    public void EveryCellCorner_IsInsideATop_SoNoCrackShowsTheSky()
    {
        // Greedy rectangles meet in T-junctions; without overlap the corners lie on edges and rasterise with cracks.
        var w = Flat(64, 40, 4);
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        for (int z = 5; z < 5 + (x / 4 * 3 + y / 4 * 5) % 4; z++) w.Set(x, y, z, TileType.Soil);
        var tops = Mesh(w, 0, 0, 16, 16, 4).Where(q => q.Dir == FaceDir.PosZ).ToList();
        for (int cy = 0; cy <= 16; cy++)
        for (int cx = 0; cx <= 16; cx++)
        {
            float px = cx * 4, py = cy * 4;
            Assert.Contains(tops, q => Math.Min(q.A.X, q.C.X) < px && px < Math.Max(q.A.X, q.C.X) &&
                                       Math.Min(q.A.Y, q.C.Y) < py && py < Math.Max(q.A.Y, q.C.Y));
        }
    }

    [Fact]
    public void EmptyColumns_DrawNothing()
    {
        var w = new VoxelWorld(32, 32, 16);
        Assert.Empty(Mesh(w, 0, 0, 8, 8, 4));
    }
}
