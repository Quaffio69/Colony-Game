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
    private static float Height(Quad q) => MathF.Max(MathF.Max(q.A.Z, q.B.Z), MathF.Max(q.C.Z, q.D.Z)) - MathF.Min(MathF.Min(q.A.Z, q.B.Z), MathF.Min(q.C.Z, q.D.Z));
    private static bool IsSkirt(Quad q) => q.Dir != FaceDir.PosZ && MathF.Abs(Height(q) - FarMesher.Skirt) < 1e-4f;

    [Fact]
    public void FlatGround_IsOneTop_AndTheMapEdgesGetSides()
    {
        var w = Flat(32, 32, 4);
        var quads = Mesh(w, 0, 0, 8, 8, 4);
        var top = Assert.Single(quads, q => q.Dir == FaceDir.PosZ);
        Assert.Equal(TileType.Grass, top.Tile);
        Assert.All(new[] { top.A, top.B, top.C, top.D }, v => Assert.Equal(5f, v.Z)); // top of z = 4
        Assert.Equal(32f * 32f, Area(top), 0.01f);
        // The block is the whole map: each map edge is one side down below the world, plus the top's four skirts.
        Assert.Equal(4, quads.Count(IsSkirt));
        Assert.Equal(4, quads.Count(q => q.Dir != FaceDir.PosZ && !IsSkirt(q)));
    }

    [Fact]
    public void InnerBlock_HasNoSides_OnFlatGround()
    {
        var w = Flat(64, 32, 4);
        var quads = Mesh(w, 16, 16, 8, 8, 4);
        Assert.Single(quads, q => q.Dir == FaceDir.PosZ);
        Assert.All(quads.Where(q => q.Dir != FaceDir.PosZ), q => Assert.True(IsSkirt(q)));
    }

    [Fact]
    public void ACell_TakesItsHighestColumn_AndStandsAboveItsNeighbours()
    {
        var w = Flat(64, 32, 4);
        for (int z = 5; z <= 9; z++) w.Set(33, 34, z, TileType.Stone); // one tall column in the cell (32..35, 32..35)
        var quads = Mesh(w, 16, 16, 8, 8, 4);
        var raised = quads.Single(q => q.Dir == FaceDir.PosZ && q.A.Z == 10f);
        Assert.Equal(TileType.Stone, raised.Tile);
        Assert.Equal(16f, Area(raised), 0.01f);
        var sides = quads.Where(q => q.Dir != FaceDir.PosZ && !IsSkirt(q)).ToList();
        Assert.Equal(4, sides.Count);
        float wide = 4f + 2 * FarMesher.Seam;
        Assert.All(sides, q => Assert.Equal(wide * (5f + FarMesher.Skirt), Area(q), 0.01f)); // from a skirt below 5 up to 10
        // The ground round it covers the rest of the block exactly.
        Assert.Equal(32f * 32f, quads.Where(q => q.Dir == FaceDir.PosZ).Sum(Area), 0.01f);
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
    public void EveryTop_HangsAnOutwardSkirtFromEachEdge_SoNoCrackShowsTheSky()
    {
        // Greedy rectangles meet in T-junctions that rasterise with hairline cracks; under a crack there must be something.
        var w = Flat(64, 40, 4);
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        for (int z = 5; z < 5 + (x / 4 * 3 + y / 4 * 5) % 4; z++) w.Set(x, y, z, TileType.Soil);
        var quads = Mesh(w, 0, 0, 16, 16, 4);
        var skirts = quads.Where(IsSkirt).ToList();
        foreach (var top in quads.Where(q => q.Dir == FaceDir.PosZ))
        {
            float x0 = MathF.Min(top.A.X, top.C.X), x1 = MathF.Max(top.A.X, top.C.X), y0 = MathF.Min(top.A.Y, top.C.Y), y1 = MathF.Max(top.A.Y, top.C.Y);
            float h = top.A.Z;
            bool Has(FaceDir dir, Func<Quad, bool> on) => skirts.Any(s => s.Dir == dir && on(s) && MathF.Max(s.A.Z, s.C.Z) == h);
            Assert.True(Has(FaceDir.NegX, s => s.A.X == x0 && MathF.Min(s.A.Y, s.C.Y) == y0 && MathF.Max(s.A.Y, s.C.Y) == y1));
            Assert.True(Has(FaceDir.PosX, s => s.A.X == x1 && MathF.Min(s.A.Y, s.C.Y) == y0 && MathF.Max(s.A.Y, s.C.Y) == y1));
            Assert.True(Has(FaceDir.NegY, s => s.A.Y == y0 && MathF.Min(s.A.X, s.C.X) == x0 && MathF.Max(s.A.X, s.C.X) == x1));
            Assert.True(Has(FaceDir.PosY, s => s.A.Y == y1 && MathF.Min(s.A.X, s.C.X) == x0 && MathF.Max(s.A.X, s.C.X) == x1));
        }
    }

    [Fact]
    public void EmptyColumns_DrawNothing()
    {
        var w = new VoxelWorld(32, 32, 16);
        Assert.Empty(Mesh(w, 0, 0, 8, 8, 4));
    }
}
