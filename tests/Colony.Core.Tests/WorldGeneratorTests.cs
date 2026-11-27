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
    public void EveryColumn_MatchesTheTerrain_WithSolidBottom_AndOpenSky()
    {
        var (w, t) = WorldGenerator.GenerateWithTerrain(Settings);

        for (int y = 0; y < w.Depth; y++)
        for (int x = 0; x < w.Width; x++)
        {
            int surface = w.SurfaceLevel(x, y);
            Assert.Equal(t.SurfaceAt(x, y), surface);
            Assert.InRange(surface, 1, w.Height - 2);
            var top = w.Get(x, y, surface);
            Assert.True(TileInfo.IsEarth(top) || top == TileType.Stone, $"{top} on top at ({x},{y})");
            Assert.True(w.IsSolid(x, y, 0));
            for (int z = 0; z <= surface; z++) Assert.True(w.IsSolid(x, y, z)); // no voids under the surface
            Assert.Equal(TileType.Air, w.Get(x, y, w.Height - 1));
        }
    }

    /// <summary>A mid-sized map: big enough for rivers to form.</summary>
    private static readonly WorldGenSettings Large = new(Seed: 12345, Width: 512, Depth: 512, Height: 192);

    [Fact]
    public void DeepRock_IsStoredAsUniformChunks()
    {
        var w = WorldGenerator.Generate(Large);
        int stored = w.Chunks.Count(c => !c.IsUniform);
        int deepStone = w.Chunks.Count(c => c.IsUniform && c.UniformType == TileType.Stone);
        Assert.True(stored < w.Chunks.Count / 3, $"{stored} of {w.Chunks.Count} chunks store tiles");
        Assert.True(deepStone > w.Chunks.Count / 4, $"only {deepStone} uniform stone chunks");

        // Ore exists, and deposits are clustered rather than speckled: most ore touches other ore.
        int ore = 0, clustered = 0;
        foreach (var c in w.Chunks.Where(c => !c.IsUniform))
        {
            var o = c.Origin;
            for (int z = 0; z < Chunk.Size; z++)
            for (int y = 0; y < Chunk.Size; y++)
            for (int x = 0; x < Chunk.Size; x++)
            {
                var m = c.Get(x, y, z);
                if (!TileInfo.IsMineral(m)) continue;
                ore++;
                int wx = o.X + x, wy = o.Y + y, wz = o.Z + z;
                if (w.Get(wx + 1, wy, wz) == m || w.Get(wx - 1, wy, wz) == m ||
                    w.Get(wx, wy + 1, wz) == m || w.Get(wx, wy - 1, wz) == m ||
                    w.Get(wx, wy, wz + 1) == m || w.Get(wx, wy, wz - 1) == m) clustered++;
            }
        }
        Assert.True(ore > 1000, $"only {ore} ore tiles");
        Assert.True(clustered > ore * 0.8, $"{clustered} of {ore} ore tiles have an ore neighbour");
    }

    [Fact]
    public void Terrain_IsMostlyGentle_WithRiversAndAWalkableSpawn()
    {
        var (w, t) = WorldGenerator.GenerateWithTerrain(Large);

        int gentle = 0, total = 0;
        for (int y = 0; y < w.Depth; y++)
        for (int x = 1; x < w.Width; x++, total++)
            if (Math.Abs(t.SurfaceAt(x, y) - t.SurfaceAt(x - 1, y)) <= 1) gentle++;
        Assert.True(gentle > total * 0.85, $"only {gentle * 100 / total}% of steps are walkable");

        Assert.True(t.RiverCells > 50, $"{t.RiverCells} river cells");
        for (int i = 0; i < t.Water.Length; i++)
            Assert.Equal(t.Kind[i] != SurfaceKind.Land, t.Water[i] > 0);

        var spawn = new TilePos(t.SpawnX, t.SpawnY, t.SurfaceAt(t.SpawnX, t.SpawnY) + 1);
        Assert.Equal(SurfaceKind.Land, t.Kind[t.SpawnY * w.Width + t.SpawnX]);
        Assert.True(Navigation.Movement.IsStandable(w, spawn));
    }

    [Fact]
    public void RiverWater_NeverFlowsUphill_ToTheMapEdge()
    {
        var t = Terrain.Generate(Large);
        // Water surfaces: from any river tile there is a path of river/lake tiles to the map edge
        // whose water surface never rises (checked with a flood from the edges, lowest first).
        int w = t.Width, d = t.Depth;
        int Top(int i) => t.Surface[i] + t.Water[i];
        var reached = new bool[w * d];
        var open = new PriorityQueue<int, int>();
        for (int i = 0; i < w * d; i++)
        {
            int x = i % w, y = i / w;
            if (t.Kind[i] != SurfaceKind.Land && (x == 0 || y == 0 || x == w - 1 || y == d - 1))
            {
                reached[i] = true;
                open.Enqueue(i, Top(i));
            }
        }
        while (open.TryDequeue(out int i, out _))
        {
            int x = i % w, y = i / w;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)d) continue;
                int j = ny * w + nx;
                if (reached[j] || t.Kind[j] == SurfaceKind.Land || Top(j) < Top(i) - 1) continue;
                reached[j] = true;
                open.Enqueue(j, Top(j));
            }
        }
        int rivers = 0, ok = 0;
        for (int i = 0; i < w * d; i++)
        {
            if (t.Kind[i] != SurfaceKind.RiverBed) continue;
            rivers++;
            if (reached[i]) ok++;
        }
        Assert.True(rivers > 0);
        Assert.True(ok > rivers * 0.9, $"{ok} of {rivers} river tiles drain downhill to the edge");
    }

    [Fact]
    public void OutOfBoundsReads_ReturnAir()
    {
        var w = new VoxelWorld(4, 4, 4);
        Assert.Equal(TileType.Air, w.Get(-1, 0, 0));
        Assert.Equal(TileType.Air, w.Get(0, 0, 99));
    }

    [Theory]
    [InlineData(12345)]
    [InlineData(7)]
    public void GeneratedWater_IsConnectedLevelAndContained(int seed)
    {
        var t = Terrain.Generate(new WorldGenSettings(seed, 768, 768, 384));
        int w = t.Width, d = t.Depth, n = w * d;
        int Level(int i) => t.Surface[i] + t.Water[i];
        bool Wet(int i) => t.Kind[i] != SurfaceKind.Land;

        // Every river and lake drains to the map edge through water (no orphaned lakes, no rivers to nowhere).
        var reach = new bool[n];
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            int x = i % w, y = i / w;
            if (Wet(i) && (x == 0 || y == 0 || x == w - 1 || y == d - 1)) { reach[i] = true; queue.Enqueue(i); }
        }
        while (queue.TryDequeue(out int c))
            foreach (int m in Neighbours(c))
                if (Wet(m) && !reach[m]) { reach[m] = true; queue.Enqueue(m); }
        int orphans = 0, spills = 0, ledges = 0;
        for (int i = 0; i < n; i++)
        {
            if (!Wet(i)) continue;
            if (!reach[i]) orphans++;
            foreach (int m in Neighbours(i))
            {
                if (!Wet(m)) { if (t.Surface[m] < Level(i)) spills++; continue; }
                // A river never stands beside lower river water unless that's where it flows.
                if (t.Kind[i] == SurfaceKind.RiverBed && t.Kind[m] == SurfaceKind.RiverBed && Level(m) < Level(i) &&
                    t.FlowDirection[i] != Direction(i, m) && t.FlowDirection[m] != Direction(m, i)) ledges++;
            }
        }
        Assert.Equal(0, orphans);
        Assert.Equal(0, spills);
        Assert.Equal(0, ledges);

        IEnumerable<int> Neighbours(int i)
        {
            int x = i % w, y = i / w;
            if (x + 1 < w) yield return i + 1;
            if (x > 0) yield return i - 1;
            if (y + 1 < d) yield return i + w;
            if (y > 0) yield return i - w;
        }
        int Direction(int from, int to) => to == from + 1 ? 0 : to == from + w ? 1 : to == from - 1 ? 2 : 3;
    }
}
