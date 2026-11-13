using System.Numerics;
using Colony.Core.Meshing;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class SectionMesherTests
{
    /// <summary>Every visible unit face of every full cube in the box, computed the slow obvious way.</summary>
    private static HashSet<(int X, int Y, int Z, FaceDir Dir, TileType T)> NaiveFaces(VoxelWorld w, int x0, int y0, int z0, int sx, int sy, int sz)
    {
        var faces = new HashSet<(int, int, int, FaceDir, TileType)>();
        for (int z = z0; z < z0 + sz; z++)
        for (int y = y0; y < y0 + sy; y++)
        for (int x = x0; x < x0 + sx; x++)
        {
            var t = w.Get(x, y, z);
            if (!TileInfo.Occludes(t)) continue;
            foreach (var d in Enum.GetValues<FaceDir>())
            {
                if (d == FaceDir.NegZ && z == 0) continue;
                var (nx, ny, nz) = SectionMesher.Normal(d);
                if (!TileInfo.Occludes(w.Get(x + nx, y + ny, z + nz))) faces.Add((x, y, z, d, t));
            }
        }
        return faces;
    }

    /// <summary>Splits greedy quads back into the unit faces they cover.</summary>
    private static List<(int X, int Y, int Z, FaceDir Dir, TileType T)> Rasterize(IEnumerable<Quad> quads)
    {
        var faces = new List<(int, int, int, FaceDir, TileType)>();
        foreach (var q in quads.Where(q => q.Tile != TileType.Stairs))
        {
            var min = Vector3.Min(Vector3.Min(q.A, q.B), Vector3.Min(q.C, q.D));
            var max = Vector3.Max(Vector3.Max(q.A, q.B), Vector3.Max(q.C, q.D));
            var (nx, ny, nz) = SectionMesher.Normal(q.Dir);
            for (int z = (int)min.Z; z < Math.Max((int)min.Z + 1, (int)max.Z); z++)
            for (int y = (int)min.Y; y < Math.Max((int)min.Y + 1, (int)max.Y); y++)
            for (int x = (int)min.X; x < Math.Max((int)min.X + 1, (int)max.X); x++)
            {
                // A face on the positive side sits on the tile's far plane: step back into the tile.
                int tx = x - (nx > 0 ? 1 : 0), ty = y - (ny > 0 ? 1 : 0), tz = z - (nz > 0 ? 1 : 0);
                faces.Add((tx, ty, tz, q.Dir, q.Tile));
            }
        }
        return faces;
    }

    [Fact]
    public void GreedyQuads_CoverExactlyTheVisibleFaces_OnGeneratedTerrain()
    {
        var w = WorldGenerator.Generate(new WorldGenSettings(Seed: 7, Width: 40, Depth: 36, Height: 30));
        // A box that straddles chunk borders and the world edge.
        var quads = new List<Quad>();
        SectionMesher.Build(w, 10, 4, 0, 30, 32, 30, quads);

        var expected = NaiveFaces(w, 10, 4, 0, 30, 32, 30);
        var actual = Rasterize(quads);
        Assert.Equal(actual.Count, actual.Distinct().Count()); // no overlaps
        Assert.True(expected.SetEquals(actual), $"expected {expected.Count} faces, got {actual.Count}");
        Assert.True(quads.Count < expected.Count / 2, $"greedy meshing should merge: {quads.Count} quads for {expected.Count} faces");
    }

    [Fact]
    public void Quads_AreWoundCounterClockwiseFromOutside()
    {
        var w = WorldGenerator.Generate(new WorldGenSettings(Seed: 3, Width: 24, Depth: 24, Height: 20));
        w.Set(5, 5, 10, TileType.Stairs);
        var quads = new List<Quad>();
        SectionMesher.Build(w, 0, 0, 0, 24, 24, 20, quads);

        Assert.All(quads, q =>
        {
            var (nx, ny, nz) = SectionMesher.Normal(q.Dir);
            var n = new Vector3(nx, ny, nz);
            Assert.True(Vector3.Dot(Vector3.Cross(q.B - q.A, q.C - q.A), n) > 0);
            Assert.True(Vector3.Dot(Vector3.Cross(q.C - q.A, q.D - q.A), n) > 0);
        });
    }

    [Fact]
    public void EmptyBox_ProducesNothing()
    {
        var w = new VoxelWorld(16, 16, 16);
        var quads = new List<Quad>();
        SectionMesher.Build(w, 0, 0, 0, 16, 16, 16, quads);
        Assert.Empty(quads);
    }

    [Fact]
    public void FullyBuriedBox_ProducesNothing()
    {
        var w = new VoxelWorld(48, 48, 48);
        for (int z = 0; z < 48; z++)
        for (int y = 0; y < 48; y++)
        for (int x = 0; x < 48; x++)
            w.Set(x, y, z, TileType.Stone);
        var quads = new List<Quad>();
        SectionMesher.Build(w, 16, 16, 16, 16, 16, 16, quads);
        Assert.Empty(quads);
    }

    [Fact]
    public void Stair_IsAStepRisingTowardsTheNextStepUp()
    {
        var w = new VoxelWorld(8, 8, 8);
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            w.Set(x, y, 0, TileType.Stone);
        w.Set(3, 3, 1, TileType.Stairs);
        w.Set(3, 4, 2, TileType.Stairs); // next step up is in +y

        Assert.Equal((0, 1), SectionMesher.StairFacing(w, 3, 3, 1));

        var quads = new List<Quad>();
        SectionMesher.Build(w, 0, 0, 0, 8, 8, 8, quads);
        var tops = quads.Where(q => q.Tile == TileType.Stairs && q.Dir == FaceDir.PosZ && q.A.X >= 3 && q.A.X <= 4 && q.A.Y < 4).ToList();
        // Two tread surfaces: low half at z+0.5 on the -y side, high half at z+1 on the +y side.
        Assert.Contains(tops, q => q.A.Z == 1.5f && Math.Max(Math.Max(q.A.Y, q.B.Y), q.C.Y) <= 3.5f + 1e-4f);
        Assert.Contains(tops, q => q.A.Z == 2f && Math.Min(Math.Min(q.A.Y, q.B.Y), q.C.Y) >= 3.5f - 1e-4f);
    }

    [Fact]
    public void StairsDoNotHideNeighbourFaces()
    {
        var w = new VoxelWorld(8, 8, 8);
        w.Set(3, 3, 3, TileType.Stone);
        w.Set(4, 3, 3, TileType.Stairs);
        var quads = new List<Quad>();
        SectionMesher.Build(w, 0, 0, 0, 8, 8, 8, quads);
        Assert.Contains(quads, q => q.Tile == TileType.Stone && q.Dir == FaceDir.PosX);
    }
}
