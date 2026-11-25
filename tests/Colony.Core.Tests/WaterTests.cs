using Colony.Core.Fluids;
using Colony.Core.Items;
using Colony.Core.Nature;
using Colony.Core.Orders;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class WaterTests
{
    private const int F = Water.Full;

    /// <summary>Stone up to z=2; open air above (floor cells at z=3). Walls are drawn in with <see cref="Fill"/>.</summary>
    private static ColonySim Flat(int size = 32, int height = 16)
    {
        var w = new VoxelWorld(size, size, height);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        for (int z = 0; z <= 2; z++) w.Set(x, y, z, TileType.Stone);
        return new ColonySim(w);
    }

    private static void Fill(ColonySim sim, int x0, int y0, int z0, int x1, int y1, int z1, TileType t = TileType.Stone)
    {
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++) sim.World.Set(x, y, z, t);
    }

    /// <summary>A closed basin: walls around [x0..x1]x[y0..y1] up to z=top.</summary>
    private static void Basin(ColonySim sim, int x0, int y0, int x1, int y1, int top)
    {
        Fill(sim, x0 - 1, y0 - 1, 3, x1 + 1, y0 - 1, top);
        Fill(sim, x0 - 1, y1 + 1, 3, x1 + 1, y1 + 1, top);
        Fill(sim, x0 - 1, y0, 3, x0 - 1, y1, top);
        Fill(sim, x1 + 1, y0, 3, x1 + 1, y1, top);
    }

    private static void Steps(ColonySim sim, int n)
    {
        for (int i = 0; i < n; i++) sim.Water.Step();
    }

    [Fact]
    public void PouredWater_LevelsOutFlat_ConservesItself_AndGoesToSleep()
    {
        var sim = Flat();
        Basin(sim, 10, 10, 15, 15, 8);
        for (int i = 0; i < 6; i++) sim.Water.Add(new TilePos(10 + i, 10, 3), F * 3); // 18 cells' worth along one wall
        long total = sim.Water.Total();
        Steps(sim, 1500);

        Assert.InRange(sim.Water.Total(), total - total / 100, total);
        // 36 columns hold 18 cells of water: half a level everywhere, flat (no sloshing left).
        for (int y = 10; y <= 15; y++)
        for (int x = 10; x <= 15; x++)
            Assert.InRange(sim.Water.SurfaceAt(x, y, 3) ?? 0, 3.45f, 3.55f);
        Assert.Equal(0, sim.Water.AwakeChunks); // still water costs nothing
    }

    [Fact]
    public void UBend_LevelsOutThroughAFloodedTunnel()
    {
        // Two shafts (x=10 and x=14) in a block of rock, joined by a tunnel along the floor at z=3.
        var sim = Flat(height: 20);
        Fill(sim, 8, 8, 3, 16, 12, 18);
        Fill(sim, 10, 10, 4, 10, 10, 18, TileType.Air);
        Fill(sim, 14, 10, 4, 14, 10, 18, TileType.Air);
        Fill(sim, 10, 10, 3, 14, 10, 3, TileType.Air);
        sim.Water.Add(new TilePos(10, 10, 4), F * 10); // ten levels poured into the west shaft
        long total = sim.Water.Total();
        Steps(sim, 3000);

        float west = sim.Water.SurfaceAt(10, 10, 4) ?? 0, east = sim.Water.SurfaceAt(14, 10, 4) ?? 0;
        Assert.True(east > 6f, $"the east shaft fills up through the tunnel ({east}, west {west})");
        Assert.InRange(west - east, -0.2f, 0.2f); // and the two stand level
        Assert.Equal(F, sim.Water.MassAt(12, 10, 3)); // the tunnel is full (under pressure)
        Assert.InRange(sim.Water.Total(), total - total / 200, total);
        Assert.True(sim.Water.Pressure(new TilePos(12, 10, 3)) > 2f, "the tunnel's water is pressed by the shafts");
        Assert.Equal(0f, sim.Water.Pressure(new TilePos(10, 10, 4)));
    }

    [Fact]
    public void AShaftDugUpFromATunnelUnderALake_FillsToTheLakesLevel()
    {
        // A lake (floor z=8, water to z=12) on a plateau of rock; a tunnel at z=3 runs from under it to a shaft
        // 12 tiles away, joined to the lake by a hole through its floor.
        var sim = Flat(size: 48, height: 20);
        Fill(sim, 0, 0, 3, 47, 47, 7);
        for (int x = 5; x <= 14; x++)
        for (int y = 20; y <= 28; y++) sim.Water.Add(new TilePos(x, y, 8), 4 * F);
        Basin(sim, 5, 20, 14, 28, 14);
        Fill(sim, 10, 24, 3, 30, 24, 3, TileType.Air); // the tunnel
        Fill(sim, 10, 24, 4, 10, 24, 7, TileType.Air); // the hole up into the lake
        Fill(sim, 30, 24, 4, 30, 24, 19, TileType.Air); // the shaft at its far end, up into the open
        Fill(sim, 29, 23, 8, 31, 25, 16); // walls around the shaft's top so it can stand higher than the plateau
        Fill(sim, 30, 24, 8, 30, 24, 16, TileType.Air);
        Steps(sim, 4000);
        float lake = sim.Water.SurfaceAt(8, 22, 9) ?? 0, shaft = sim.Water.SurfaceAt(30, 24, 10) ?? 0;
        Assert.True(shaft > 10f, $"water rises up the shaft past the plateau ({shaft})");
        Assert.InRange(shaft - lake, -0.3f, 0.3f);
    }

    [Fact]
    public void ALongFloodedTunnel_PassesTheLakesFullPressure_AndTheFarShaftFillsToTheLakesLevel()
    {
        // Rock up to z=44 with a lake on it (floor z=45, three levels deep); a shaft from the lake bed down to a
        // tunnel at z=6 running 30 tiles east, and a shaft at its end back up to the open air.
        var w = new VoxelWorld(64, 48, 60);
        for (int y = 0; y < 48; y++)
        for (int x = 0; x < 64; x++)
        for (int z = 0; z <= 44; z++) w.Set(x, y, z, TileType.Stone);
        var sim = new ColonySim(w);
        Basin(sim, 2, 2, 25, 25, 50);
        Fill(sim, 1, 1, 3, 26, 26, 44); // (Basin's walls start at z=3; the rock below is solid anyway)
        for (int y = 2; y <= 25; y++)
        for (int x = 2; x <= 25; x++) sim.Water.Add(new TilePos(x, y, 45), 3 * F);
        Fill(sim, 10, 20, 6, 10, 20, 44, TileType.Air); // the shaft down from the lake bed
        Fill(sim, 10, 20, 6, 40, 20, 6, TileType.Air);  // the tunnel
        Fill(sim, 40, 20, 6, 40, 20, 59, TileType.Air); // the shaft up at its end...
        Fill(sim, 39, 19, 45, 41, 21, 55);               // ...walled up above the lake (or it gushes out on the plateau)
        Fill(sim, 40, 20, 45, 40, 20, 55, TileType.Air);
        long total = sim.Water.Total();
        Steps(sim, 1500);

        float lake = sim.Water.SurfaceAt(15, 15, 46) ?? 0, far = sim.Water.SurfaceAt(40, 20, 10) ?? 0;
        float near = sim.Water.Pressure(new TilePos(12, 20, 6)), end = sim.Water.Pressure(new TilePos(39, 20, 6));
        Assert.True(Math.Abs(far - lake) < 0.3f, $"communicating vessels: lake {lake}, far shaft {far}, pressure near {near} end {end}, tunnel mass {sim.Water.MassAt(25, 20, 6)}, total {sim.Water.Total()} of {total}");
        Assert.InRange(near - end, -0.5f, 0.5f); // a still flooded tunnel is pressed alike all along
        Assert.True(near > 35f, $"the tunnel carries the lake's head ({near})");
        Assert.Equal(F, sim.Water.MassAt(25, 20, 6));
        Assert.InRange(sim.Water.Total(), total - total / 500, total); // nothing hidden, nothing lost
    }

    [Fact]
    public void AShaftEndingBelowTheLakesLevel_GushesLikeAnArtesianWell()
    {
        // As above, but the far shaft opens on the plateau, a level below the lake's surface: it overflows.
        var w = new VoxelWorld(64, 48, 60);
        for (int y = 0; y < 48; y++)
        for (int x = 0; x < 64; x++)
        for (int z = 0; z <= 44; z++) w.Set(x, y, z, TileType.Stone);
        var sim = new ColonySim(w);
        Basin(sim, 2, 2, 25, 25, 50);
        for (int y = 2; y <= 25; y++)
        for (int x = 2; x <= 25; x++) sim.Water.Add(new TilePos(x, y, 45), 3 * F);
        Fill(sim, 10, 20, 6, 10, 20, 44, TileType.Air);
        Fill(sim, 10, 20, 6, 40, 20, 6, TileType.Air);
        Fill(sim, 40, 20, 6, 40, 20, 44, TileType.Air);
        Steps(sim, 600);
        Assert.True(sim.Water.MassAt(42, 20, 45) > 0, "water spills out over the plateau around the shaft");
    }

    [Fact]
    public void WaterLeavesTheWorld_OverItsBorder()
    {
        var sim = Flat();
        Fill(sim, 0, 15, 3, 10, 15, 6);
        Fill(sim, 0, 17, 3, 10, 17, 6);
        Fill(sim, 10, 16, 3, 10, 16, 6);
        sim.Water.Add(new TilePos(8, 16, 3), F * 4);
        Steps(sim, 600);
        Assert.True(sim.Water.Drained > F * 3, $"drained {sim.Water.Drained}");
        Assert.True(sim.Water.Total() < F, $"left {sim.Water.Total()}");
    }

    [Fact]
    public void WaterPoursUnderAnOverhang_IntoATunnel()
    {
        // A hill (solid up to z=8) east of x=15, with a tunnel at z=3..4 running into it; a pool west of it.
        var sim = Flat();
        Fill(sim, 15, 0, 3, 31, 31, 8);
        Fill(sim, 15, 16, 3, 25, 16, 4, TileType.Air);
        Basin(sim, 10, 14, 14, 18, 6);
        Fill(sim, 15, 15, 3, 15, 17, 4, TileType.Air); // the basin opens into the tunnel mouth
        Fill(sim, 15, 15, 3, 15, 15, 4);
        Fill(sim, 15, 17, 3, 15, 17, 4);
        for (int y = 14; y <= 18; y++) sim.Water.Add(new TilePos(12, y, 3), F * 2);
        Steps(sim, 800);
        Assert.True(sim.Water.MassAt(24, 16, 3) > F / 4, $"water runs into the tunnel ({sim.Water.MassAt(24, 16, 3)})");
        Assert.Equal(0, sim.Water.MassAt(24, 16, 9)); // and not onto the hill above it
    }

    [Fact]
    public void ARiverRunsDownASteppedBed_WithoutSloshing()
    {
        // A channel along x falling a level every 8 tiles towards the border at x=0, fed by a spring at the top.
        var sim = Flat(size: 64, height: 20);
        for (int x = 0; x < 64; x++)
        {
            int bed = 3 + x / 8; // floor rises upstream
            Fill(sim, x, 0, 3, x, 63, bed - 1);
            Fill(sim, x, 0, bed, x, 14, bed + 3);
            Fill(sim, x, 18, bed, x, 63, bed + 3);
        }
        Fill(sim, 63, 15, 0, 63, 17, 19);
        sim.Water.AddSpring(new TilePos(62, 16, 3 + 62 / 8), 200, head: 1);
        Steps(sim, 3000);
        Assert.True(sim.Water.MassAt(4, 16, 3) > F / 50, "the river reaches the bottom");
        // Steady flow: from step to step the surface barely moves (no checkerboard ping-pong).
        var before = new float[64];
        for (int x = 0; x < 64; x++) before[x] = sim.Water.SurfaceAt(x, 16, 3 + x / 8) ?? 0;
        Steps(sim, 1);
        for (int x = 1; x < 63; x++)
            Assert.InRange((sim.Water.SurfaceAt(x, 16, 3 + x / 8) ?? 0) - before[x], -0.05f, 0.05f);
    }

    /// <summary>A channel along x at y=16 (walls at y=15 and 17 up to z=10), closed at x=25, open to the border at x=0.</summary>
    private static ColonySim Channel()
    {
        var sim = Flat(height: 13);
        Fill(sim, 0, 15, 3, 25, 15, 10);
        Fill(sim, 0, 17, 3, 25, 17, 10);
        Fill(sim, 25, 16, 3, 25, 16, 10);
        return sim;
    }

    [Fact]
    public void Springs_KeepFlowing_DigsReleaseWater_AndWallsDamIt()
    {
        var sim = Channel();
        sim.Water.AddSpring(new TilePos(24, 16, 3), head: 5);
        Steps(sim, 600);
        Assert.True(sim.Water.MassAt(5, 16, 3) > F / 50, "the channel carries the spring's water");
        Assert.True(sim.Water.AwakeChunks > 0, "a fed river never sleeps");

        // Dam it at x=12, as high as the channel: upstream fills up, downstream runs dry.
        Fill(sim, 12, 16, 3, 12, 16, 10, TileType.Wall);
        Steps(sim, 3000);
        Assert.True(sim.Water.MassAt(18, 16, 5) > F / 2, $"the reservoir rises behind the dam ({sim.Water.MassAt(18, 16, 5)})");
        Assert.True(sim.Water.MassAt(5, 16, 3) < F / 50, $"below the dam it runs dry ({sim.Water.MassAt(5, 16, 3)})");

        // Dig through the dam's foot: the water floods back down the channel.
        sim.World.Set(12, 16, 3, TileType.Air);
        Steps(sim, 400);
        Assert.True(sim.Water.MassAt(5, 16, 3) > F / 50);
    }

    [Fact]
    public void AThinWallDam_HoldsAReservoir_SpillsOverItsCrest_AndADoorLetsWaterThrough()
    {
        // The channel's floor is z=3; a thin wall on the edge between x=12 and x=13, three levels high (z 3..5).
        var sim = Channel();
        sim.Water.AddSpring(new TilePos(24, 16, 3), head: 5);
        byte wall = EdgeInfo.Make(EdgeKind.Wall, 3);
        for (int z = 3; z <= 5; z++) sim.World.SetEdge(new TilePos(12, 16, z), EdgeInfo.East, wall);
        Steps(sim, 2500);
        Assert.True(sim.Water.MassAt(18, 16, 5) > F / 2, "the reservoir rises to the crest");
        Assert.True(sim.Water.MassAt(5, 16, 3) > F / 100, "and spills over it down the channel");
        Assert.True(sim.Water.MassAt(18, 16, 8) < F / 10, "but doesn't stack up past the crest");

        // Raised to the channel's full height, nothing gets past.
        var shut = Channel();
        shut.Water.AddSpring(new TilePos(24, 16, 3), head: 5);
        for (int z = 3; z <= 12; z++) shut.World.SetEdge(new TilePos(12, 16, z), EdgeInfo.East, wall);
        Steps(shut, 2500);
        Assert.True(shut.Water.MassAt(5, 16, 3) < F / 100, $"below a full wall it stays dry ({shut.Water.MassAt(5, 16, 3)})");

        // A door at the bottom is no dam.
        shut.World.SetEdge(new TilePos(12, 16, 3), EdgeInfo.East, EdgeInfo.Make(EdgeKind.Door, 2));
        Steps(shut, 600);
        Assert.True(shut.Water.MassAt(5, 16, 3) > F / 100, "water runs through the doorway");
    }

    [Fact]
    public void ALowDam_FillsToItsCrest_ThenSpillsOver()
    {
        var sim = Channel();
        sim.Water.AddSpring(new TilePos(24, 16, 3), head: 5);
        Fill(sim, 12, 16, 3, 12, 16, 5, TileType.Wall); // 3 levels high
        Steps(sim, 2500);
        Assert.True(sim.Water.MassAt(18, 16, 5) > F / 2, "the reservoir reaches the crest");
        Assert.True(sim.Water.MassAt(5, 16, 3) > F / 100, "and the overflow keeps the channel below wet");
        Assert.True(sim.Water.MassAt(18, 16, 8) < F / 10, "but doesn't stack up past the crest");
    }

    [Fact]
    public void WaterWheel_UnderAFall_PowersAPump_ThatLiftsWaterUpABank()
    {
        // A raised bed (floor at z=6) for x >= 13 dropping to the channel floor (z=3) below: a waterfall at x=12.
        var sim = Channel();
        Fill(sim, 13, 16, 3, 24, 16, 5);
        sim.Water.AddSpring(new TilePos(24, 16, 6), 300, head: 1);
        var wheel = sim.Waterworks.AddWheel(sim.Water, new TilePos(12, 16, 3), 0); // in the pool under the fall
        // A pump in a spring-fed well elsewhere lifts water over the well's wall: intake (5,5,3), outlet (5,4,4).
        Basin(sim, 5, 5, 5, 5, 3);
        var pump = sim.Waterworks.AddPump(new TilePos(5, 5, 3), 3);
        sim.Water.AddSpring(new TilePos(5, 5, 3));
        for (int i = 0; i < 4000; i++) sim.Step();
        Assert.True(wheel.Power > 0.5f, $"wheel power {wheel.Power}");
        Assert.True(sim.Waterworks.Supply > 0);
        Assert.True(sim.Water.MassAt(pump.Outlet) > 0 || pump.Pumped > 0, "the pump lifts water");

        // No flow, no power: a still pool.
        var still = Flat();
        Basin(still, 10, 10, 12, 12, 5);
        for (int x = 10; x <= 12; x++) still.Water.Add(new TilePos(x, 11, 3), F);
        var idle = still.Waterworks.AddWheel(still.Water, new TilePos(11, 11, 3), 0);
        for (int i = 0; i < 1200; i++) still.Step();
        Assert.True(idle.Power < 0.2f, $"a still pool turns no wheel ({idle.Power})");
        Assert.Equal(0, still.Waterworks.Supply);
    }

    [Fact]
    public void WaterWheels_HangOnWallsBesideAFall_PumpsNeedGround()
    {
        var sim = Flat();
        Fill(sim, 10, 10, 3, 10, 10, 8); // a pillar
        Assert.Null(sim.Constructions.CanPlace(StructureKind.WaterWheel, new TilePos(11, 10, 6)));
        Assert.NotNull(sim.Constructions.CanPlace(StructureKind.Pump, new TilePos(11, 10, 6)));
        Assert.NotNull(sim.Constructions.CanPlace(StructureKind.WaterWheel, new TilePos(20, 20, 6)));
    }

    [Fact]
    public void DiggingOutALakesFloor_DrainsTheLake()
    {
        var w = new VoxelWorld(160, 32, 12);
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 160; x++)
        for (int z = 0; z <= 4; z++) w.Set(x, y, z, TileType.Stone);
        var sim = new ColonySim(w);
        // A lake basin dug into the rock (floor at z=3, water two levels deep), left to settle.
        Fill(sim, 120, 10, 3, 130, 20, 4, TileType.Air);
        for (int y = 10; y <= 20; y++)
        for (int x = 120; x <= 130; x++) sim.Water.Add(new TilePos(x, y, 3), 2 * F);
        Steps(sim, 300);
        Assert.Equal(0, sim.Water.AwakeChunks);
        long lake = sim.Water.MassAt(125, 15, 4);

        // Dig a pit under part of the lake (two levels down through its floor).
        Fill(sim, 120, 10, 1, 126, 20, 2, TileType.Air);
        Steps(sim, 600);
        Assert.True(sim.Water.MassAt(122, 15, 1) > F / 2, "water pours into the pit");
        Assert.True(sim.Water.MassAt(125, 15, 4) < lake / 2, "and the lake's surface drops");
    }

    [Fact]
    public void GeneratedRivers_StayInTheirChannels()
    {
        var sim = ColonySim.Generate(new WorldGenSettings(12345, 512, 512, 160), colonists: 3);
        var t = sim.Terrain!;
        int Flooded()
        {
            int n = 0;
            for (int y = 0; y < t.Depth; y++)
            for (int x = 0; x < t.Width; x++)
            {
                int i = y * t.Width + x;
                if (t.Kind[i] == SurfaceKind.Land && sim.Water.MassAt(x, y, t.Surface[i] + 1) >= F / 4) n++;
            }
            return n;
        }
        int early = Flooded();
        long start = sim.Water.Total() + sim.Water.Drained + sim.Water.Evaporated - sim.Water.Sourced;
        // Long enough for slow imbalances to show (rivers feeding a lake faster than its outlet passes, say).
        for (int i = 0; i < 40000; i++) sim.Step();
        int late = Flooded();
        Assert.True(late <= early + early / 5 + 20, $"rivers keep spilling over their banks: {early} flooded land columns, then {late}");
        Assert.True(late < t.Width * t.Depth / 2000, $"{late} flooded land columns");
        long books = sim.Water.Total() + sim.Water.Drained + sim.Water.Evaporated - sim.Water.Sourced;
        Assert.InRange(books, start - start / 1000, start + start / 1000); // nothing made or lost unaccounted
    }

    [Fact]
    public void ACanalDugOffAGeneratedRiver_Fills_WithoutFloodingTheLand()
    {
        var sim = ColonySim.Generate(new WorldGenSettings(12345, 512, 512, 160), colonists: 3);
        var t = sim.Terrain!;
        int w = t.Width;
        // A river tile with water in it and ten tiles of land beside it standing at most three levels above it.
        (int X, int Y, int Dir, int Level) site = (-1, -1, -1, -1);
        for (int i = 0; i < w * t.Depth && site.X < 0; i++)
        {
            if (t.Kind[i] != SurfaceKind.RiverBed || t.FlowDirection[i] == Terrain.NoFlow) continue;
            int x = i % w, y = i / w, lvl = t.Surface[i] + t.Water[i];
            if (sim.Water.MassAt(x, y, lvl) < F / 2) continue;
            for (int k = 0; k < 4 && site.X < 0; k++)
            {
                int dx = k == 0 ? 1 : k == 2 ? -1 : 0, dy = k == 1 ? 1 : k == 3 ? -1 : 0;
                bool ok = true;
                for (int s = 1; s <= 10 && ok; s++)
                {
                    int nx = x + dx * s, ny = y + dy * s;
                    ok = nx > 0 && ny > 0 && nx < w - 1 && ny < t.Depth - 1 && t.Kind[ny * w + nx] == SurfaceKind.Land &&
                         t.Surface[ny * w + nx] >= lvl && t.Surface[ny * w + nx] <= lvl + 3;
                }
                if (ok) site = (x, y, k, lvl);
            }
        }
        Assert.True(site.X >= 0, "no river with flat land beside it");
        var (rx, ry, dir, level) = site;
        int ddx = dir == 0 ? 1 : dir == 2 ? -1 : 0, ddy = dir == 1 ? 1 : dir == 3 ? -1 : 0;

        // Dig the canal: open every cell from the river's top water cell up, leaving the floor below.
        for (int s = 1; s <= 10; s++)
        {
            int x = rx + ddx * s, y = ry + ddy * s;
            for (int z = level; z <= t.Surface[y * w + x]; z++) sim.World.Set(x, y, z, TileType.Air);
        }
        for (int i = 0; i < 3000; i++) sim.Step();

        Assert.True(sim.Water.MassAt(rx + ddx * 10, ry + ddy * 10, level) > F / 4,
            $"the canal fills from the river ({sim.Water.MassAt(rx + ddx * 10, ry + ddy * 10, level)})");
        int flooded = 0;
        for (int y = ry - 24; y <= ry + 24; y++)
        for (int x = rx - 24; x <= rx + 24; x++)
        {
            if (x < 0 || y < 0 || x >= w || y >= t.Depth) continue;
            int i = y * w + x;
            bool canal = (x - rx) * ddy == (y - ry) * ddx && (x - rx) * ddx + (y - ry) * ddy is >= 1 and <= 10;
            if (t.Kind[i] == SurfaceKind.Land && !canal && sim.Water.MassAt(x, y, t.Surface[i] + 1) >= F / 4) flooded++;
        }
        Assert.Equal(0, flooded);
    }

    [Fact]
    public void Colonists_BuildWallsFromStone_AndTheWallDisplacesWater()
    {
        var sim = Flat();
        sim.Stockpiles.AddFloorBox(new TilePos(2, 2, 2), new TilePos(4, 4, 2));
        sim.Items.Drop(new TilePos(3, 3, 3), ItemType.Stone, 10);
        Assert.Equal(3, sim.Constructions.PlaceWalls(new TilePos(10, 10, 2), new TilePos(12, 10, 2)));
        Assert.Equal(1, sim.Constructions.PlaceWalls(new TilePos(11, 10, 3), new TilePos(11, 10, 3))); // stacked on a blueprint
        sim.Water.Add(new TilePos(11, 10, 3), F);
        sim.AddColonist("Mason", new TilePos(6, 6, 3));
        sim.AddColonist("Hauler", new TilePos(7, 6, 3));
        for (int i = 0; i < 20_000 && sim.Constructions.Count > 0; i++) sim.Step();
        Assert.Equal(0, sim.Constructions.Count);
        Assert.Equal(TileType.Wall, sim.World.Get(11, 10, 3));
        Assert.Equal(TileType.Wall, sim.World.Get(11, 10, 4));
        Assert.Equal(6, sim.Items.Totals()[ItemType.Stone.Index]);
        Assert.Equal(0, sim.Water.MassAt(11, 10, 3));
        Assert.All(sim.Colonists, c => Assert.False(TileInfo.IsSolid(sim.World.Get(c.Position))));
    }

    [Fact]
    public void AsyncWater_GivesExactlyTheSameWater_EvenWithDigsWhileAStepIsInFlight()
    {
        ColonySim Build(bool async)
        {
            var sim = Channel();
            sim.AsyncWater = async;
            sim.Water.AddSpring(new TilePos(24, 16, 3), head: 5);
            Fill(sim, 12, 16, 3, 12, 16, 6, TileType.Wall); // a dam, dug away bit by bit below
            return sim;
        }
        var sync = Build(false);
        var async = Build(true);
        for (int tick = 1; tick <= 4000; tick++)
        {
            sync.Step();
            async.Step(); // with async, a water step may still be computing now...
            if (tick % 500 == 0)
            {
                int z = 6 - tick / 1000; // ...when the world changes under it (as the player's debug digs can)
                sync.World.Set(12, 16, z, TileType.Air);
                async.World.Set(12, 16, z, TileType.Air);
            }
        }
        async.Water.Join();
        Assert.Equal(sync.Water.Total(), async.Water.Total());
        Assert.Equal(sync.Water.Drained, async.Water.Drained);
        for (int x = 0; x < 26; x++)
        for (int z = 3; z <= 10; z++)
            Assert.Equal(sync.Water.MassAt(x, 16, z), async.Water.MassAt(x, 16, z));
        Assert.True(sync.Water.MassAt(5, 16, 3) > 0, "the dug-through dam lets the water down the channel");
    }

    [Fact]
    public void IrrigatedFarms_GrowTwiceAsFast()
    {
        var sim = Flat();
        sim.Farms.AddFloorBox(PlantKind.Wheat, new TilePos(10, 10, 2), new TilePos(10, 10, 2));
        sim.Farms.AddFloorBox(PlantKind.Wheat, new TilePos(20, 20, 2), new TilePos(20, 20, 2));
        sim.Flora.Add(new Plant(new TilePos(10, 10, 3), PlantKind.Wheat, 0, 0));
        sim.Flora.Add(new Plant(new TilePos(20, 20, 3), PlantKind.Wheat, 0, 0));
        Basin(sim, 12, 10, 12, 10, 4); // a one-cell ditch two tiles away, kept full
        sim.Water.AddSpring(new TilePos(12, 10, 3));
        for (int i = 0; i < 1000; i++) sim.Step();
        sim.Flora.TryGet(new TilePos(10, 10, 3), out var wet);
        sim.Flora.TryGet(new TilePos(20, 20, 3), out var dry);
        Assert.True(Farms.Irrigated(sim.Water, new TilePos(10, 10, 3)));
        Assert.Equal(dry.Growth * 2, wet.Growth);
    }

    [Fact]
    public void GeneratedWorld_HasFlowingRiversFedBySprings_AndIsDeterministic()
    {
        ColonySim Make() => ColonySim.Generate(new WorldGenSettings(3, 256, 256, 96), colonists: 0);
        var a = Make();
        var b = Make();
        Assert.True(a.Water.Springs.Count > 0, "rivers get springs");
        Assert.True(a.Water.WetChunks > 0);
        for (int i = 0; i < 300; i++)
        {
            a.Step();
            b.Step();
        }
        Assert.True(a.Water.Total() > 0);
        Assert.Equal(a.Water.Total(), b.Water.Total());
        Assert.Equal(a.Water.Drained, b.Water.Drained);
    }

    [Fact]
    public void WaterPouringOffALedge_IsReportedAsAFall()
    {
        var sim = Flat();
        Fill(sim, 0, 0, 3, 11, 31, 7); // a shelf: its top at z=7, water on it at z=8; the floor below at z=3
        sim.Water.AddSpring(new TilePos(4, 16, 8), 200, head: 1);
        Steps(sim, 300);
        var falls = new List<Fall>();
        sim.Water.Falls(0, 0, 31, 31, falls);
        Assert.Contains(falls, f => f.X == 11 && f.Side == 0 && f.Top >= 8 && f.Bottom <= 4 && f.Rate > 0);
        Assert.DoesNotContain(falls, f => f.X < 11); // the shelf itself is level: no falls on it
    }
}

public class WaterLevelTests
{
    [Fact]
    public void AThinFilm_ReadsOneSeventh_NotZero()
    {
        var w = new VoxelWorld(32, 32, 16);
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) for (int z = 0; z <= 2; z++) w.Set(x, y, z, TileType.Stone);
        var sim = new ColonySim(w);
        var cell = new TilePos(10, 10, 3);
        Assert.Equal(0, sim.Water.Level(cell)); // dry
        sim.Water.Add(cell, Fluids.Water.Full / 20); // a twentieth of a level: well under half a seventh
        Assert.True(sim.Water.IsWet(cell));
        Assert.Equal(1, sim.Water.Level(cell));
        sim.Water.Add(cell, Fluids.Water.Full);
        Assert.Equal(7, sim.Water.Level(cell));
    }
}
