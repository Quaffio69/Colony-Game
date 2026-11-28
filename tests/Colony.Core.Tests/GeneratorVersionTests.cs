using Colony.Core.Nature;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class GeneratorVersionTests
{
    /// <summary>FNV-1a over everything generation decides: terrain, every tile, rock kinds, flora.</summary>
    internal static ulong Fingerprint(WorldGenSettings s)
    {
        var (w, t) = WorldGenerator.GenerateWithTerrain(s);
        var flora = FloraGenerator.Generate(s, t, w);
        ulong h = 14695981039346656037UL;
        void Mix(long v) { unchecked { h = (h ^ (ulong)v) * 1099511628211UL; } }
        for (int i = 0; i < t.Surface.Length; i++) { Mix(t.Surface[i]); Mix(t.Water[i]); Mix((long)t.Kind[i]); Mix(t.FlowDirection[i]); }
        Mix(t.SpawnX); Mix(t.SpawnY);
        foreach (var (x, y, share) in t.RiverSources) { Mix(x); Mix(y); Mix(BitConverter.SingleToInt32Bits(share)); }
        for (int z = 0; z < w.Height; z++)
        for (int y = 0; y < w.Depth; y++)
        for (int x = 0; x < w.Width; x++)
        {
            Mix((long)w.Get(x, y, z));
            if ((x + y + z) % 7 == 0) Mix((long)w.Geology!.RockAt(x, y, z));
        }
        foreach (var p in flora.All.OrderBy(p => p.Position.Z).ThenBy(p => p.Position.Y).ThenBy(p => p.Position.X))
        {
            Mix(p.Position.X); Mix(p.Position.Y); Mix(p.Position.Z); Mix((long)p.Kind); Mix(p.Growth); Mix(p.Variant);
        }
        return h;
    }

    [Fact]
    public void TheFirstGenerator_StillMakesTheSameWorld()
    {
        // Saves rebuild their world from its recipe: worlds made before the tectonic generator must come out exactly as
        // they did, or their saved changes land on different ground.
        var s = new WorldGenSettings(Seed: 4242, Width: 256, Depth: 256, Height: 96) { Shape = WorldShape.Legacy };
        Assert.Equal(6748849766862576698UL, Fingerprint(s));
        Assert.Equal(13647918698330532034UL, Fingerprint(new WorldGenSettings(Seed: 12345, Width: 512, Depth: 512, Height: 160) { Shape = WorldShape.Legacy }));
    }

    [Fact]
    public void TheFirstTectonicGenerator_StillMakesTheSameWorld()
    {
        // Generator 2 (its banks reshaped the land; lakes stopped LakeShoreReach tiles out) is kept for its saves.
        var s = new WorldGenSettings(Seed: 777, Width: 512, Depth: 512, Height: 256) { Shape = new WorldShape(Generator: 2) };
        Assert.Equal(2518458387926943134UL, Fingerprint(s));
    }
}
