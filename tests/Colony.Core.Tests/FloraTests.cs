using Colony.Core.Nature;
using Colony.Core.Orders;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class FloraTests
{
    private static readonly WorldGenSettings Large = new(Seed: 12345, Width: 512, Depth: 512, Height: 192);

    [Fact]
    public void Generation_IsDeterministic_AndOnlyOnFertileDryLand()
    {
        var (w, t) = WorldGenerator.GenerateWithTerrain(Large);
        var a = FloraGenerator.Generate(Large, t, w);
        var b = FloraGenerator.Generate(Large, t, w);
        Assert.Equal(a.All.ToList(), b.All.ToList());

        int trees = 0, conifers = 0, reeds = 0;
        foreach (var p in a.All)
        {
            if (p.Kind == PlantKind.Glowcap && w.IsUnseen(p.Position))
            {
                Assert.True(PlantInfo.CanGrowAt(w, p.Position, PlantKind.Glowcap), $"{p}: cave glowcap with no ground or roof");
                continue; // wild glowcaps on cavern floors (CavernTests)
            }
            Assert.True(PlantInfo.CanGrowAt(w, p.Position), $"{p} has no ground");
            int i = p.Position.Y * w.Width + p.Position.X;
            Assert.Equal(SurfaceKind.Land, t.Kind[i]);
            Assert.Equal(TileType.Grass, w.Get(p.Position.X, p.Position.Y, p.Position.Z - 1));
            if (PlantInfo.IsTree(p.Kind))
            {
                trees++;
                Assert.True(p.Position.Z - 1 <= FloraGenerator.TreeLine(Large), $"{p} above the treeline");
                if (p.Kind is PlantKind.Pine or PlantKind.Spruce) conifers++;
            }
            if (p.Kind == PlantKind.Reeds)
            {
                reeds++;
                Assert.True(t.WaterDistance[i] <= 2, $"{p} reeds far from water");
            }
        }
        Assert.True(trees > 2000, $"only {trees} trees");
        Assert.True(a.Count - trees > 2000, $"only {a.Count - trees} plants");
        Assert.True(conifers > 0 && conifers < trees);
        Assert.True(reeds > 0);
    }

    [Fact]
    public void AddRemoveAndLookup_KeepRegionsConsistent()
    {
        var w = new VoxelWorld(300, 300, 8);
        for (int y = 0; y < 300; y++)
        for (int x = 0; x < 300; x++)
            w.Set(x, y, 0, TileType.Grass);
        var f = new Flora(w);
        var a = new Plant(new TilePos(5, 5, 1), PlantKind.Oak, 255, 0);
        var b = new Plant(new TilePos(6, 5, 1), PlantKind.Fern, 100, 1);
        var c = new Plant(new TilePos(200, 150, 1), PlantKind.Pine, 255, 2);
        Assert.True(f.Add(a) && f.Add(b) && f.Add(c));
        Assert.False(f.Add(a with { Kind = PlantKind.Birch })); // occupied
        Assert.False(f.Add(a with { Position = new TilePos(9, 9, 3) })); // floating: no ground
        int v = f.RegionVersion(0, 0);

        Assert.True(f.Remove(a.Position, out var removed));
        Assert.Equal(a, removed);
        Assert.True(f.TryGet(b.Position, out var stillThere) && stillThere == b); // swap-remove kept the index right
        Assert.NotEqual(v, f.RegionVersion(0, 0));
        Assert.Equal(1, f.InRegion(1, 1).Count);
        Assert.Equal(2, f.Count);
    }

    [Fact]
    public void DiggingTheGround_RemovesThePlantOnIt()
    {
        var w = new VoxelWorld(20, 20, 8);
        for (int z = 0; z <= 2; z++)
        for (int y = 0; y < 20; y++)
        for (int x = 0; x < 20; x++)
            w.Set(x, y, z, z == 2 ? TileType.Grass : TileType.Stone);
        var sim = new ColonySim(w);
        sim.Flora.Add(new Plant(new TilePos(5, 5, 3), PlantKind.Oak, 255, 0));
        sim.Flora.Add(new Plant(new TilePos(8, 8, 3), PlantKind.Shrub, 255, 0));

        sim.Designations.Designate(new TilePos(5, 5, 2));
        sim.CompleteDesignation(new TilePos(5, 5, 2));
        sim.Designations.Designate(new TilePos(8, 8, 2), DesignationKind.DownStair);
        sim.CompleteDesignation(new TilePos(8, 8, 2));

        Assert.Equal(0, sim.Flora.Count);
    }

    [Fact]
    public void CaveIns_CarryPlantsDown_AndCrushThoseBelow()
    {
        var w = new VoxelWorld(24, 24, 16);
        for (int y = 0; y < 24; y++)
        for (int x = 0; x < 24; x++)
        {
            for (int z = 0; z <= 2; z++) w.Set(x, y, z, TileType.Stone);
            w.Set(x, y, 3, TileType.Grass);
        }
        // A grassy slab two levels up, held by a corner post.
        for (int z = 4; z <= 5; z++) w.Set(12, 12, z, TileType.Stone);
        for (int y = 8; y <= 12; y++)
        for (int x = 8; x <= 12; x++)
            w.Set(x, y, 6, TileType.Grass);
        var sim = new ColonySim(w);
        var rider = new Plant(new TilePos(9, 9, 7), PlantKind.Birch, 255, 3);
        Assert.True(sim.Flora.Add(rider));

        w.Set(12, 12, 4, TileType.Air); // cut the post at its base...
        var victim = new Plant(new TilePos(12, 12, 4), PlantKind.Flowers, 255, 4);
        Assert.True(sim.Flora.Add(victim)); // ...where flowers now grow, right under the falling post
        sim.CheckSupport(new TilePos(12, 12, 4));

        int drop = sim.RecentCaveIns[0].Drop;
        Assert.Equal(1, drop);
        Assert.True(sim.Flora.TryGet(rider.Position.Offset(0, 0, -drop), out var moved));
        Assert.Equal(PlantKind.Birch, moved.Kind);
        Assert.False(sim.Flora.TryGet(victim.Position, out _));
    }
}
