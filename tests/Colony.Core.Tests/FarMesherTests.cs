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
        // The block is the whole map: each map edge is one side down below the world; no skirts (no equal top beside it).
        Assert.Equal(0, quads.Count(IsSkirt));
        Assert.Equal(4, quads.Count(q => q.Dir != FaceDir.PosZ));
    }

    [Fact]
    public void InnerBlock_HasNoSides_OnFlatGround()
    {
        var w = Flat(64, 32, 4);
        var quads = Mesh(w, 16, 16, 8, 8, 4);
        Assert.Single(quads, q => q.Dir == FaceDir.PosZ);
        // The neighbouring blocks' ground is level with it: one skirt along each border (their tops meet it there).
        Assert.Equal(4, quads.Count(IsSkirt));
        Assert.Equal(5, quads.Count);
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
    public void EveryEdgeBetweenLevelTops_HasASkirt_SoNoCrackShowsTheSky()
    {
        // Greedy rectangles meet in T-junctions that rasterise with hairline cracks; under each such edge there must be a
        // skirt (edges to lower or higher cells have sides).
        var w = Flat(64, 40, 4);
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        if (x < 40 && y < 40 && !(x >= 20 && y >= 20)) w.Set(x, y, 5, x < 16 ? TileType.Soil : TileType.Clay); // an L of two kinds: level rectangles meet
        var quads = Mesh(w, 0, 0, 16, 16, 4);
        var tops = quads.Where(q => q.Dir == FaceDir.PosZ).ToList();
        var skirts = quads.Where(IsSkirt).ToList();
        int Rect(float px, float py) => tops.FindIndex(q => MathF.Min(q.A.X, q.C.X) < px && px < MathF.Max(q.A.X, q.C.X) && MathF.Min(q.A.Y, q.C.Y) < py && py < MathF.Max(q.A.Y, q.C.Y));
        int checkedEdges = 0;
        for (int cy = 0; cy < 16; cy++)
        for (int cx = 0; cx < 16; cx++)
        foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
        {
            if (cx + dx >= 16 || cy + dy >= 16) continue;
            int a = Rect(cx * 4 + 2, cy * 4 + 2), b = Rect((cx + dx) * 4 + 2, (cy + dy) * 4 + 2);
            if (a == b || tops[a].A.Z != tops[b].A.Z) continue;
            checkedEdges++;
            float ex = (cx + dx) * 4, ey = (cy + dy) * 4, mid = dx != 0 ? cy * 4 + 2 : cx * 4 + 2;
            Assert.Contains(skirts, s => dx != 0
                ? s.A.X == ex && MathF.Min(s.A.Y, s.C.Y) < mid && mid < MathF.Max(s.A.Y, s.C.Y)
                : s.A.Y == ey && MathF.Min(s.A.X, s.C.X) < mid && mid < MathF.Max(s.A.X, s.C.X));
        }
        Assert.True(checkedEdges > 10, $"only {checkedEdges} level edges between rectangles");
    }

    [Fact]
    public void EmptyColumns_DrawNothing()
    {
        var w = new VoxelWorld(32, 32, 16);
        Assert.Empty(Mesh(w, 0, 0, 8, 8, 4));
    }
}
