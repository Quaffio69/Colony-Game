using Colony.Core.Fluids;
using Colony.Core.Net;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class MistTests
{
    /// <summary>Stone up to z=2, open floor at z=3; a shelf (x 0..11, top z=7) with a spring on it pours off its +x edge.</summary>
    private static ColonySim Falls(int size = 48, int shelfTop = 7, bool twoSprings = false)
    {
        var w = new VoxelWorld(size, size, 16);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        for (int z = 0; z <= 2; z++) w.Set(x, y, z, TileType.Stone);
        var sim = new ColonySim(w) { NeedsEnabled = false };
        Fill(sim, 0, 0, 3, 11, size - 1, shelfTop);
        sim.Water.AddSpring(new TilePos(4, 16, shelfTop + 1), 200, head: 1);
        if (twoSprings) sim.Water.AddSpring(new TilePos(4, 30, shelfTop + 1), 200, head: 1);
        return sim;
    }

    private static void Fill(ColonySim sim, int x0, int y0, int z0, int x1, int y1, int z1, TileType t = TileType.Stone)
    {
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++) sim.World.Set(x, y, z, t);
    }

    private static void Steps(ColonySim sim, int n)
    {
        for (int i = 0; i < n; i++) sim.Step();
    }

    [Fact]
    public void ATallFall_RaisesMistAtItsFoot_ThatSpreadsOut()
    {
        var sim = Falls();
        Steps(sim, 1200);
        var (foot, level) = sim.Mist.CellAt(12 / Mist.Cell, 16 / Mist.Cell);
        Assert.True(foot >= Mist.Felt, $"foot {foot}");
        Assert.Equal(3, level); // hangs over the pool at the cliff's foot
        Assert.True(sim.Mist.ActiveCells >= 6, $"cells {sim.Mist.ActiveCells}");
        Assert.True(sim.Mist.CellAt(5, 4).Density > 0, "spread out from the foot");
        Assert.Equal(0, sim.Mist.CellAt(1, 4).Density); // never up into the rock of the shelf
        Assert.True(sim.Mist.At(new TilePos(13, 16, 3)) > 0);
        Assert.Equal(0, sim.Mist.At(new TilePos(13, 16, 12))); // far above it: clear air
    }

    [Fact]
    public void AShortDrop_RaisesNoMist()
    {
        var sim = Falls(shelfTop: 3); // water at z=4 falls barely a level onto the floor
        Steps(sim, 1200);
        Assert.Equal(0, sim.Mist.ActiveCells);
    }

    [Fact]
    public void MoreFalls_MakeMoreMist()
    {
        var one = Falls();
        var two = Falls(twoSprings: true);
        Steps(one, 1200);
        Steps(two, 1200);
        Assert.True(two.Mist.Total > one.Mist.Total * 3 / 2, $"one {one.Mist.Total}, two {two.Mist.Total}");
    }

    [Fact]
    public void Mist_FadesAway_WhenTheFallsStop()
    {
        var sim = Falls();
        Steps(sim, 1200);
        Assert.True(sim.Mist.Total > 0);
        sim.Water.RemoveSpring(new TilePos(4, 16, 8));
        Steps(sim, 20 * 180); // three minutes: the shelf drains, the mist thins away
        Assert.Equal(0, sim.Mist.ActiveCells);
    }

    [Fact]
    public void Mist_DoesNotPassThroughAWall()
    {
        var sim = Falls();
        Fill(sim, 24, 0, 3, 27, 47, 15); // a wall from floor to sky, a whole mist cell thick
        Steps(sim, 2400);
        Assert.True(sim.Mist.CellAt(5, 4).Density > 0);
        for (int cy = 0; cy < sim.Mist.CellsDeep; cy++)
        for (int cx = 6; cx < sim.Mist.CellsWide; cx++)
            Assert.Equal(0, sim.Mist.CellAt(cx, cy).Density);
    }

    [Fact]
    public void ColonistsInTheMist_AreLifted_OthersAreNot()
    {
        var sim = Falls();
        Fill(sim, 24, 0, 3, 27, 47, 15);
        var near = sim.AddColonist("Near", new TilePos(14, 17, 3));
        var far = sim.AddColonist("Far", new TilePos(40, 16, 3));
        Steps(sim, 2400);
        Assert.True(near.MistBonus > 0, $"near {near.MistBonus}");
        Assert.True(near.MistBonus <= Balance.MistMood);
        Assert.Equal(0, far.MistBonus);
    }

    [Fact]
    public void TheWind_VeersSlowly_AndIsTheSameForEveryone()
    {
        Assert.Equal(Mist.Wind(0), Mist.Wind(ColonySim.TicksPerSecond * 100)); // steady for a while
        var headings = Enumerable.Range(0, 40).Select(k => Mist.Wind(k * ColonySim.TicksPerSecond * 150L)).Distinct().Count();
        Assert.True(headings >= 4, $"headings {headings}");
    }

    [Fact]
    public void ASnapshot_WithMist_PlaysOnIdentically()
    {
        var settings = new GameSettings(Seed: 8, Width: 96, Depth: 96, Height: 48, Colonists: 4, Colonies: 1);
        var sim = settings.Create();
        var a = sim.Colonists[0].Position;
        // A stone tower beside the colony with a spring on top: water pours off its sides.
        int x0 = a.X + 4, y0 = a.Y - 2, x1 = a.X + 6, y1 = a.Y + 2, top = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++) top = Math.Max(top, sim.World.SurfaceLevel(x, y) + 5);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        for (int z = sim.World.SurfaceLevel(x, y); z <= top; z++) sim.World.Set(x, y, z, TileType.Stone);
        sim.Water.AddSpring(new TilePos(a.X + 5, a.Y, top + 1), 300, head: 1);
        for (int i = 0; i < 900; i++) sim.Step();
        Assert.True(sim.Mist.ActiveCells > 0, "mist");
        var loaded = Snapshot.Load(Snapshot.Take(sim, settings));
        Assert.Equal(StateHash.Compute(sim), StateHash.Compute(loaded));
        Assert.Equal(sim.Mist.Total, loaded.Mist.Total);
        for (int i = 1; i <= 1500; i++)
        {
            sim.Step();
            loaded.Step();
            if (i % 100 == 0) Assert.True(StateHash.Compute(sim) == StateHash.Compute(loaded), $"diverged by tick {sim.Tick}");
        }
        Assert.Equal(sim.Mist.Total, loaded.Mist.Total);
    }
}
