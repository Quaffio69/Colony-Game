using Colony.Core.Industry;
using Colony.Core.Navigation;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class EdgeTests
{
    private static readonly byte Wall = EdgeInfo.Make(EdgeKind.Wall, 2), Door = EdgeInfo.Make(EdgeKind.Door, 2), Window = EdgeInfo.Make(EdgeKind.Window, 2);

    private static VoxelWorld Flat(int size = 32, int height = 12)
    {
        var w = new VoxelWorld(size, size, height);
        for (int z = 0; z <= 3; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++) w.Set(x, y, z, TileType.Stone);
        return w;
    }

    private static bool CanMove(VoxelWorld w, TilePos a, TilePos b) => Movement.MoveCost(w, a, b) is not null;

    [Fact]
    public void Edges_AreStoredOnTheirCell_ReadFromEitherSide_AndNotifyBothChunks()
    {
        var w = Flat();
        var changed = new HashSet<int>();
        w.ChunkChanged += i => changed.Add(i);
        var a = new TilePos(15, 5, 4); // on a chunk's east boundary
        w.SetEdge(a, EdgeInfo.East, Wall);
        Assert.Equal(Wall, w.EdgeBetween(a, a.Offset(1, 0, 0)));
        Assert.Equal(Wall, w.EdgeBetween(a.Offset(1, 0, 0), a));
        Assert.Equal(0, w.EdgeBetween(a, a.Offset(0, 1, 0)));
        Assert.Contains(w.ChunkIndex(0, 0, 0), changed);
        Assert.Contains(w.ChunkIndex(1, 0, 0), changed);
        w.SetEdge(a, EdgeInfo.North, Window);
        Assert.Equal(2, w.AllEdges().Count());
        Assert.Equal(EdgeKind.Window, EdgeInfo.Kind(w.Edge(a, EdgeInfo.North)));
        Assert.Equal(2, EdgeInfo.Material(w.Edge(a, EdgeInfo.North)));
        w.SetEdge(a, EdgeInfo.East, 0);
        w.SetEdge(a, EdgeInfo.North, 0);
        Assert.Empty(w.AllEdges());
        Assert.False(w.HasEdges);
    }

    [Fact]
    public void WallsAndWindows_StopWalkers_DoorsLetThemThrough()
    {
        var w = Flat();
        var a = new TilePos(5, 5, 4);
        var east = a.Offset(1, 0, 0);
        Assert.True(CanMove(w, a, east));
        w.SetEdge(a, EdgeInfo.East, Wall);
        Assert.False(CanMove(w, a, east));
        Assert.False(CanMove(w, east, a));
        w.SetEdge(a, EdgeInfo.East, Window);
        Assert.False(CanMove(w, a, east));
        w.SetEdge(a, EdgeInfo.East, Door);
        Assert.True(CanMove(w, a, east));
        Assert.True(CanMove(w, east, a));
    }

    [Fact]
    public void NoSqueezingDiagonallyPastAWallsEnd_NorClimbingOverIt()
    {
        var w = Flat();
        var a = new TilePos(5, 5, 4);
        w.SetEdge(a, EdgeInfo.East, Wall); // between (5,5) and (6,5)
        Assert.False(CanMove(w, a, a.Offset(1, 1, 0)));  // past the wall's north end
        Assert.False(CanMove(w, a, a.Offset(1, -1, 0))); // and its south end
        Assert.True(CanMove(w, a, a.Offset(-1, 1, 0)));  // the other way is open

        // A one-level wall on top of a ledge can't be climbed over, nor dropped over from the other side.
        var v = Flat();
        v.Set(11, 5, 4, TileType.Stone);              // a one-level step east of (10,5,4)
        var low = new TilePos(10, 5, 4);
        var high = new TilePos(11, 5, 5);
        Assert.True(CanMove(v, low, high));
        v.SetEdge(new TilePos(10, 5, 5), EdgeInfo.East, Wall); // the wall stands at the upper level
        Assert.False(CanMove(v, low, high));
        Assert.False(CanMove(v, high, low));
    }

    [Fact]
    public void Movement_StaysSymmetric_WithEdgesEverywhere()
    {
        // Rough terrain with stairs, walls, doors and windows scattered: every legal move is legal in reverse.
        var w = new VoxelWorld(24, 24, 12);
        for (int y = 0; y < 24; y++)
        for (int x = 0; x < 24; x++)
        {
            int top = 3 + (int)(Noise.Hash(x, y, 0, 5) * 3);
            for (int z = 0; z <= top; z++) w.Set(x, y, z, TileType.Stone);
            if (Noise.Hash(x, y, 1, 5) < 0.05f) w.Set(x, y, top + 1, TileType.UpDownStair);
        }
        for (int y = 0; y < 24; y++)
        for (int x = 0; x < 24; x++)
        for (int z = 3; z < 10; z++)
            for (int side = 0; side < 2; side++)
            {
                float r = Noise.Hash(x * 2 + side, y, z, 9);
                if (r < 0.12f) w.SetEdge(new TilePos(x, y, z), side, r < 0.08f ? Wall : r < 0.1f ? Door : Window);
            }
        var moves = new List<(TilePos, float)>();
        var back = new List<(TilePos, float)>();
        for (int y = 0; y < 24; y++)
        for (int x = 0; x < 24; x++)
        for (int z = 1; z < 11; z++)
        {
            var p = new TilePos(x, y, z);
            if (!Movement.IsStandable(w, p)) continue;
            Movement.GetMoves(w, p, moves);
            foreach (var (q, _) in moves)
            {
                Movement.GetMoves(w, q, back);
                Assert.Contains(back, m => m.Item1 == p);
            }
        }
        // And a snapshot sees the same walls as the world.
        var box = TileBox.Capture(w, 0, 0, 0, 24, 24, 12);
        for (int y = 0; y < 24; y++)
        for (int x = 0; x < 24; x++)
            Assert.Equal(w.EdgesAt(x, y, 5), box.EdgesAt(x, y, 5));
    }

    [Fact]
    public void AWalledRing_IsCutOff_UntilItGetsADoor()
    {
        var sim = new ColonySim(Flat());
        // A ring of walls around the 4x4 cells (10..13, 10..13) on level 4.
        void Ring(byte doorAt)
        {
            for (int i = 10; i <= 13; i++)
            {
                sim.World.SetEdge(new TilePos(9, i, 4), EdgeInfo.East, Wall);
                sim.World.SetEdge(new TilePos(13, i, 4), EdgeInfo.East, Wall);
                sim.World.SetEdge(new TilePos(i, 9, 4), EdgeInfo.North, i == 11 ? doorAt : Wall);
                sim.World.SetEdge(new TilePos(i, 13, 4), EdgeInfo.North, Wall);
            }
        }
        Ring(Wall);
        sim.Connectivity.Update();
        Assert.NotEqual(sim.Connectivity.ComponentOf(new TilePos(11, 11, 4)), sim.Connectivity.ComponentOf(new TilePos(2, 2, 4)));
        Ring(Door);
        sim.Connectivity.Update();
        Assert.Equal(sim.Connectivity.ComponentOf(new TilePos(11, 11, 4)), sim.Connectivity.ComponentOf(new TilePos(2, 2, 4)));
        // Rooms painted across the wall line split along it: inside and outside are different rooms.
        sim.Rooms.Paint(Catalog.Home, new TilePos(8, 11, 3), new TilePos(15, 12, 3));
        Assert.NotEqual(sim.Rooms.RoomAt(new TilePos(11, 11, 4)), sim.Rooms.RoomAt(new TilePos(15, 11, 4)));
    }

    [Fact]
    public void Colonists_BuildAWalledOutline_CutADoorInIt_AndDemolishedWallsLeaveTheirMaterial()
    {
        var sim = new ColonySim(Flat()) { NeedsEnabled = false };
        sim.Items.Drop(new TilePos(25, 25, 4), Items.ItemType.Plank, 40);
        for (int i = 0; i < 3; i++) sim.AddColonist($"B{i}", new TilePos(2 + i, 2, 4));
        int planned = sim.Constructions.PlaceOutline(new TilePos(10, 10, 3), new TilePos(12, 12, 3), material: 2); // planks
        Assert.Equal(12, planned);
        Assert.All(sim.Constructions.All, bp => Assert.True(bp.IsEdge));
        for (int i = 0; i < 6000 && sim.Constructions.Count > 0; i++) sim.Step();
        Assert.Equal(0, sim.Constructions.Count);
        Assert.Equal(12, sim.World.AllEdges().Count(e => EdgeInfo.Kind(e.Info) == EdgeKind.Wall));
        Assert.Equal(40 - 12, sim.Items.Totals()[Items.ItemType.Plank.Index]);
        Assert.Contains(sim.Colonists, c => c.Position.X < 10 || c.Position.X > 12 || c.Position.Y < 10 || c.Position.Y > 12);

        // A door cut into the south wall: built from 2 planks, the wall's plank comes back.
        Assert.NotNull(sim.Constructions.PlaceEdge(Orders.StructureKind.Door, new TilePos(11, 9, 4), EdgeInfo.North));
        for (int i = 0; i < 4000 && sim.Constructions.Count > 0; i++) sim.Step();
        Assert.Equal(EdgeKind.Door, EdgeInfo.Kind(sim.World.Edge(new TilePos(11, 9, 4), EdgeInfo.North)));
        Assert.Equal(40 - 12 - 2 + 1, sim.Items.Totals()[Items.ItemType.Plank.Index]);
        sim.Connectivity.Update();
        Assert.Equal(sim.Connectivity.ComponentOf(new TilePos(11, 11, 4)), sim.Connectivity.ComponentOf(new TilePos(2, 2, 4)));

        Assert.True(sim.Constructions.Demolish(new TilePos(12, 10, 4), EdgeInfo.East, sim.Items));
        Assert.Equal(0, sim.World.Edge(new TilePos(12, 10, 4), EdgeInfo.East));
        Assert.Equal(40 - 12 - 2 + 1 + 1, sim.Items.Totals()[Items.ItemType.Plank.Index]);
    }

    [Fact]
    public void Colonists_LayFloorsAndRoofs_AndAFloorGoesWithItsGround()
    {
        var sim = new ColonySim(Flat()) { NeedsEnabled = false };
        sim.Items.Drop(new TilePos(25, 25, 4), Items.ItemType.Plank, 30);
        sim.Items.Drop(new TilePos(25, 20, 4), Items.ItemType.Fiber, 20);
        for (int i = 0; i < 3; i++) sim.AddColonist($"B{i}", new TilePos(2 + i, 2, 4));
        Assert.Equal(9, sim.Constructions.PlaceCoverBox(Orders.StructureKind.Floor, new TilePos(10, 10, 3), new TilePos(12, 12, 3), material: 1)); // planks
        Assert.Equal(9, sim.Constructions.PlaceCoverBox(Orders.StructureKind.Roof, new TilePos(10, 10, 3), new TilePos(12, 12, 3), material: 1));  // thatch
        Assert.Null(sim.Constructions.PlaceCover(Orders.StructureKind.Floor, new TilePos(11, 11, 4), 1)); // already planned
        for (int i = 0; i < 8000 && sim.Constructions.Count > 0; i++) sim.Step();
        Assert.Equal(0, sim.Constructions.Count);
        for (int y = 10; y <= 12; y++)
        for (int x = 10; x <= 12; x++)
        {
            Assert.Equal(1, sim.World.FloorAt(new TilePos(x, y, 4)));
            Assert.Equal(1, sim.World.RoofAt(new TilePos(x, y, 4)));
        }
        Assert.Equal(30 - 9, sim.Items.Totals()[Items.ItemType.Plank.Index]);
        Assert.Equal(20 - 9, sim.Items.Totals()[Items.ItemType.Fiber.Index]);

        sim.World.Set(11, 11, 3, TileType.Air); // the ground under one floor tile dug away
        Assert.Equal(0, sim.World.FloorAt(new TilePos(11, 11, 4)));
        Assert.Equal(1, sim.World.RoofAt(new TilePos(11, 11, 4)));

        Assert.Equal(8 + 9, sim.Constructions.ClearCoverBox(Orders.StructureKind.Floor, new TilePos(10, 10, 3), new TilePos(12, 12, 3), sim.Items)
                         + sim.Constructions.ClearCoverBox(Orders.StructureKind.Roof, new TilePos(10, 10, 3), new TilePos(12, 12, 3), sim.Items));
        Assert.Empty(sim.World.AllCovers());
    }

    [Fact]
    public void Colonists_WalkRoundWalls_ThroughTheDoor()
    {
        var sim = new ColonySim(Flat()) { NeedsEnabled = false };
        for (int y = 0; y < 32; y++)
            sim.World.SetEdge(new TilePos(15, y, 4), EdgeInfo.East, y == 28 ? Door : Wall); // a long wall with a door far south
        var c = sim.AddColonist("Walker", new TilePos(5, 5, 4));
        sim.Designations.Designate(new TilePos(25, 5, 3));
        for (int i = 0; i < 2000 && sim.Designations.Count > 0; i++) sim.Step();
        Assert.Equal(0, sim.Designations.Count);
        Assert.True(c.Position.X > 15);
    }
}
