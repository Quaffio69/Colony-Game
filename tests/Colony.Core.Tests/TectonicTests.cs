using Colony.Core.Net;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>The tectonic generator (WorldShape generator 2): landforms, climate, biomes, the sea, and its recipe in saves.</summary>
public class TectonicTests
{
    private static readonly Lazy<GeneratedWorld> Standard = new(() => WorldGenerator.GenerateWithTerrain(new WorldGenSettings(12345, 1024, 1024, 384)));

    private static int[] Census(Terrain t, int step = 4)
    {
        var count = new int[Enum.GetValues<Biome>().Length];
        for (int y = 0; y < t.Depth; y += step)
        for (int x = 0; x < t.Width; x += step)
            count[(int)Biomes.At(t, x, y).Biome]++;
        return count;
    }

    [Fact]
    public void AStandardWorld_HasSeaMountainsRiversAndManyBiomes_WithoutWalls()
    {
        var (w, t) = Standard.Value;
        Assert.NotNull(t.Landform);
        Assert.True(t.SeaLevel > 0);
        var count = Census(t);
        int total = count.Sum();
        Assert.True(count[(int)Biome.Sea] > total / 20, $"sea {count[(int)Biome.Sea]} of {total}");
        int land = Enum.GetValues<Biome>().Count(b => b is not (Biome.Sea or Biome.Lake or Biome.River) && count[(int)b] > total / 400);
        Assert.True(land >= 5, $"only {land} land biomes");
        Assert.True(t.RiverCells > 50, $"{t.RiverCells} river cells");
        Assert.True(t.Surface.Max() > t.SeaLevel + 80, $"highest ground {t.Surface.Max()} (sea {t.SeaLevel})");

        // No walls: neighbouring land never steps 10 levels (valley sides are graded, seams blended, slopes slump).
        int walls = 0;
        for (int y = 0; y < t.Depth; y++)
        for (int x = 0; x + 1 < t.Width; x++)
        {
            int i = y * t.Width + x;
            if (t.Kind[i] == SurfaceKind.Land && t.Kind[i + 1] == SurfaceKind.Land && Math.Abs(t.Surface[i] - t.Surface[i + 1]) >= 10) walls++;
        }
        Assert.Equal(0, walls);

        // The colony lands on livable, walkable ground.
        var home = Biomes.At(t, t.SpawnX, t.SpawnY);
        Assert.True(Biomes.Habitability(home.Biome) >= 0.5f, $"landed in {home.Biome}");
        Assert.True(Navigation.Movement.IsStandable(w, new TilePos(t.SpawnX, t.SpawnY, t.SurfaceAt(t.SpawnX, t.SpawnY) + 1)));
        // The sea floor is a shelf, and every sea column's water stands at sea level.
        for (int i = 0; i < t.Surface.Length; i++)
            if (t.IsSea(i)) Assert.InRange(t.Water[i], 1, Terrain.SeaDepth(t.Height));
    }

    [Fact]
    public void WorldOptions_ShapeTheWorld()
    {
        WorldGenSettings S(WorldShape shape) => new(777, 512, 512, 192) { Shape = shape };
        var dry = Terrain.Generate(S(WorldShape.Default with { Oceans = Oceans.None, Climate = Climate.Tropical, Rainfall = Rainfall.Arid }));
        Assert.Equal(0, dry.SeaLevel);
        var dc = Census(dry);
        Assert.Equal(0, dc[(int)Biome.Sea]);
        Assert.True(dc[(int)Biome.Desert] + dc[(int)Biome.Badlands] + dc[(int)Biome.Savanna] > dc.Sum() / 3, "an arid tropical world is mostly desert and savanna");

        var cold = Terrain.Generate(S(WorldShape.Default with { Climate = Climate.Frozen }));
        var cc = Census(cold);
        Assert.True(cc[(int)Biome.Snow] + cc[(int)Biome.Tundra] + cc[(int)Biome.Alpine] > (cc.Sum() - cc[(int)Biome.Sea]) / 2, string.Join(", ", Enum.GetValues<Biome>().Select(b => $"{b} {cc[(int)b]}")));

        var islands = Terrain.Generate(S(WorldShape.Default with { Oceans = Oceans.Islands }));
        var coasts = Terrain.Generate(S(WorldShape.Default with { Oceans = Oceans.Coasts }));
        Assert.True(Census(islands)[(int)Biome.Sea] > Census(coasts)[(int)Biome.Sea], "islands have more sea than coasts");

        var few = Terrain.Generate(S(WorldShape.Default with { Rivers = Rivers.Few }));
        var many = Terrain.Generate(S(WorldShape.Default with { Rivers = Rivers.Many }));
        Assert.True(many.RiverCells > few.RiverCells, $"many rivers {many.RiverCells}, few {few.RiverCells}");
    }

    [Fact]
    public void TheSea_HoldsItsLevelAtTheMapEdge()
    {
        // The map edge drains, except where the sea meets it: the sea there neither drains away nor rises.
        var sim = ColonySim.Generate(new WorldGenSettings(12345, 256, 256, 96) { Shape = WorldShape.Default with { Oceans = Oceans.Islands } }, colonists: 0);
        var t = sim.Terrain!;
        Assert.True(t.SeaLevel > 0);
        var edge = Enumerable.Range(0, t.Width).SelectMany(k => new[] { (X: k, Y: 0), (X: k, Y: t.Depth - 1), (X: 0, Y: k), (X: t.Width - 1, Y: k) })
            .FirstOrDefault(p => t.IsSea(p.Y * t.Width + p.X));
        Assert.True(t.IsSea(edge.Y * t.Width + edge.X), "no sea on the map edge");
        long before = sim.Water.Total();
        for (int i = 0; i < 400; i++) sim.Step();
        var top = new TilePos(edge.X, edge.Y, t.SeaLevel);
        Assert.True(sim.Water.MassAt(top) > 0, "the sea still stands at its level on the edge");
        Assert.True(sim.Water.MassAt(top.Offset(0, 0, 1)) < Fluids.Water.MinMass, "and no higher");
        Assert.InRange(sim.Water.Total(), before * 0.97, before * 1.03);
    }

    [Fact]
    public void TheRecipe_RoundTrips_AndOldRecordsReadAsTheFirstGenerator()
    {
        var settings = new GameSettings(9, 128, 128, 64, 3, 1) { Shape = new WorldShape(Oceans: Oceans.Islands, Relief: Relief.Alpine, Climate: Climate.Warm, Rainfall: Rainfall.Dry, Rivers: Rivers.Many) };
        var ms = new MemoryStream();
        settings.Write(new BinaryWriter(ms));
        ms.Position = 0;
        Assert.Equal(settings, GameSettings.Read(new BinaryReader(ms)));
        Assert.Equal(settings.Shape, GameSettings.Read(new BinaryReader(new MemoryStream(ms.ToArray()))).Shape);

        // A record from before world shapes: six ints only.
        var old = new MemoryStream();
        var ow = new BinaryWriter(old);
        foreach (int v in new[] { 9, 128, 128, 64, 3, 1 }) ow.Write(v);
        old.Position = 0;
        Assert.True(GameSettings.Read(new BinaryReader(old), withShape: false).Shape.IsLegacy);

        // A save made today carries its shape.
        var save = new SaveGame(settings, 0, [], "test", 0, 0);
        var file = new MemoryStream();
        save.Write(file);
        file.Position = 0;
        Assert.Equal(settings.Shape, SaveGame.Read(file, headerOnly: true).Settings.Shape);
    }
}
