using Colony.Core.Nature;
using Colony.Core.Net;
using Colony.Core.Orders;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>Generated caverns: where they lie, unseen until breached, revealed whole, kept in snapshots.</summary>
public class CavernTests
{
    private static readonly GameSettings Deep = new(Seed: 5, Width: 160, Depth: 160, Height: 384, Colonists: 2, Colonies: 1);
    private static readonly Lazy<ColonySim> Shared = new(() => Deep.Create());

    /// <summary>Every unseen cell in the world.</summary>
    private static List<TilePos> UnseenCells(VoxelWorld w)
    {
        var cells = new List<TilePos>();
        for (int z = 0; z < w.Height; z++)
        for (int y = 0; y < w.Depth; y++)
        for (int x = 0; x < w.Width; x++)
            if (w.IsUnseen(x, y, z)) cells.Add(new TilePos(x, y, z));
        return cells;
    }

    [Fact]
    public void Caverns_LieBelowZero_UnderRockAndOverTheBedrock_AllUnseen()
    {
        var sim = Shared.Value;
        var w = sim.World;
        var g = w.Geology!;
        Assert.True(g.CavernLayers.Length >= 2, $"{g.CavernLayers.Length} cavern layers");
        var cells = UnseenCells(w);
        Assert.Equal(w.UnseenCells, cells.Count);
        Assert.True(cells.Count > 2000, $"{cells.Count} cavern cells");
        foreach (var p in cells)
        {
            Assert.Equal(TileType.Air, w.Get(p));
            Assert.True(p.Z < g.ZeroLevel, $"{p} above level zero");
            Assert.True(p.Z > g.BedrockTop(p.X, p.Y) + 1, $"{p} in the bedrock");
            Assert.True(p.Z <= sim.Terrain!.SurfaceAt(p.X, p.Y) - WorldGenerator.CavernRoof, $"{p} too near the surface");
            Assert.True(w.IsHidden(p.X, p.Y, p.Z));
        }
        // Rock round an unseen cavern stays hidden: its floor too.
        var floor = cells.First(p => TileInfo.IsSolid(w.Get(p.X, p.Y, p.Z - 1)) && !w.IsUnseen(p.X, p.Y, p.Z - 1));
        Assert.True(w.IsHidden(floor.X, floor.Y, floor.Z - 1));
    }

    [Fact]
    public void BreakingIn_RevealsTheWholeCavern_AndOnlyIt()
    {
        var sim = Deep.Create();
        var w = sim.World;
        int before = w.UnseenCells;
        var cells = UnseenCells(w);
        // A cavern floor cell: dig the rock under it from below (as a tunnel would arrive).
        var p = cells.First(c => TileInfo.IsSolid(w.Get(c.X, c.Y, c.Z - 1)));
        long revealed = w.RevealRevision;
        var opened = new List<int>();
        w.ChunkRevealed += opened.Add;
        w.Set(p.X, p.Y, p.Z - 1, TileType.Air);
        Assert.False(w.IsUnseen(p));
        Assert.True(w.RevealRevision > revealed);
        Assert.NotEmpty(opened);
        int shown = before - w.UnseenCells;
        Assert.True(shown > 50, $"the cavern revealed: {shown} cells");
        Assert.True(w.UnseenCells > 0, "other caverns stay unseen");
        // Revealed cells are exactly the ones connected to the breach.
        var seen = new HashSet<TilePos>(cells.Where(c => !w.IsUnseen(c)));
        Assert.Equal(shown, seen.Count);
        foreach (var c in seen)
        foreach (var n in new[] { c.Offset(1, 0, 0), c.Offset(-1, 0, 0), c.Offset(0, 1, 0), c.Offset(0, -1, 0), c.Offset(0, 0, 1), c.Offset(0, 0, -1) })
            Assert.False(w.IsUnseen(n), $"{n} touches the revealed cavern but is still unseen");
        Assert.False(w.IsHidden(p.X, p.Y, p.Z - 2)); // the rock under the tunnel shows now
    }

    [Fact]
    public void RockFallingIntoAnUnseenCell_FillsIt()
    {
        var sim = Deep.Create();
        var w = sim.World;
        var p = UnseenCells(w)[0];
        int before = w.UnseenCells;
        w.Set(p, TileType.Stone);
        Assert.False(w.IsUnseen(p));
        Assert.Equal(before - 1, w.UnseenCells);
    }

    [Fact]
    public void Glowcaps_GrowInCaverns_ButCantBeOrderedUntilFound()
    {
        var sim = Deep.Create();
        var w = sim.World;
        var caps = sim.Flora.All.Where(pl => pl.Kind == PlantKind.Glowcap && w.IsUnseen(pl.Position)).ToList();
        Assert.NotEmpty(caps);
        Assert.All(caps, c => Assert.True(PlantInfo.IsGatherable(c)));
        var cap = caps[0].Position;
        Assert.Equal(0, sim.Designations.DesignatePlantArea(DesignationKind.Gather, cap.X, cap.Y, cap.X, cap.Y));
        w.Set(cap.X, cap.Y, cap.Z - 1, TileType.Air); // break in under it
        w.Set(cap.X, cap.Y, cap.Z - 1, TileType.Stone); // and put the floor back
        Assert.False(w.IsUnseen(cap));
        Assert.Equal(1, sim.Designations.DesignatePlantArea(DesignationKind.Gather, cap.X, cap.Y, cap.X, cap.Y));
    }

    [Fact]
    public void PicksAndDrawing_TreatUnseenAirAsRock()
    {
        var w = Shared.Value.World;
        var p = UnseenCells(w).First(c => TileInfo.IsSolid(w.Get(c.X, c.Y, c.Z - 1)));
        // A ray straight down through the cavern stops at its (unseen) top, never on the floor.
        var hit = VoxelRaycast.Cast(w, new System.Numerics.Vector3(p.X + 0.5f, p.Y + 0.5f, p.Z + 0.9f), new System.Numerics.Vector3(0, 0, -1), 5f);
        Assert.NotNull(hit);
        Assert.Equal(p, hit!.Value.Tile);
        var box = TileBox.CaptureSeen(w, p.X - 1, p.Y - 1, p.Z - 1, 3, 3, 3);
        Assert.Equal(TileType.Stone, box.Get(p.X, p.Y, p.Z));
        Assert.Equal(TileType.Air, TileBox.Capture(w, p.X - 1, p.Y - 1, p.Z - 1, 3, 3, 3).Get(p.X, p.Y, p.Z));
    }

    [Fact]
    public void ARevealedCavern_StaysRevealed_ThroughASnapshot()
    {
        var sim = Deep.Create();
        var w = sim.World;
        var p = UnseenCells(w).First(c => TileInfo.IsSolid(w.Get(c.X, c.Y, c.Z - 1)));
        w.Set(p.X, p.Y, p.Z - 1, TileType.Air);
        var loaded = Snapshot.Load(Snapshot.Take(sim, Deep));
        Assert.Equal(w.UnseenCells, loaded.World.UnseenCells);
        Assert.False(loaded.World.IsUnseen(p));
        Assert.Equal(StateHash.Compute(sim), StateHash.Compute(loaded));
    }
}
