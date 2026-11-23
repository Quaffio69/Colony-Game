using Colony.Core.Commands;
using Colony.Core.Items;
using Colony.Core.Navigation;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class HouseTests
{
    // Ground: stone up to z=3, so floors stand at z=4.
    private static VoxelWorld Flat(int size = 32, int height = 14)
    {
        var w = new VoxelWorld(size, size, height);
        for (int z = 0; z <= 3; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++) w.Set(x, y, z, TileType.Stone);
        return w;
    }

    [Fact]
    public void ADeck_IsStoodOn_KeepsItsFloor_AndCanBeReachedByStairs()
    {
        var w = Flat();
        var deck = new TilePos(10, 10, 5);
        Assert.False(Movement.IsStandable(w, deck));
        w.Set(deck.X, deck.Y, deck.Z, TileType.Deck);
        w.SetFloor(deck, 1);
        Assert.True(Movement.IsStandable(w, deck));
        Assert.False(TileInfo.IsSolid(TileType.Deck));
        Assert.True(Movement.IsStandable(w, new TilePos(10, 10, 4))); // headroom under it: still the ground floor

        // A stair up beside it: up from the ground, down at the deck's level, then a step across.
        w.Set(11, 10, 4, TileType.UpStair);
        w.Set(11, 10, 5, TileType.DownStair);
        Assert.NotNull(Pathfinder.FindPath(w, new TilePos(14, 14, 4), deck));
        // Its edge is a ledge to climb onto from the ground beside it, until an upper storey's wall stands there.
        Assert.NotNull(Movement.MoveCost(w, new TilePos(10, 11, 4), new TilePos(10, 10, 5)));
        w.SetEdge(new TilePos(10, 10, 5), EdgeInfo.North, EdgeInfo.Make(EdgeKind.Wall, 2));
        Assert.Null(Movement.MoveCost(w, new TilePos(10, 11, 4), new TilePos(10, 10, 5)));

        // Digging the ground away below doesn't take a deck's floor (it is its own floor); taking the deck down does.
        w.Set(10, 10, 3, TileType.Air);
        Assert.Equal(1, w.FloorAt(deck));
        w.Set(deck.X, deck.Y, deck.Z, TileType.Air);
        Assert.Equal(0, w.FloorAt(deck));
    }

    [Fact]
    public void AHouse_PlansWallsDoorFloorsStairsAndRoof_ForEachStorey()
    {
        var sim = new ColonySim(Flat());
        var plan = Houses.Survey(sim, new TilePos(10, 10, 3), new TilePos(13, 13, 3), 2);
        Assert.Empty(plan.Conflicts);
        Assert.NotNull(plan.Stair);
        Assert.NotNull(plan.Door);
        var p = Houses.Place(sim, plan, wall: 2, floor: 1, roof: 1, faction: 0);
        Assert.Equal(16 + 16 - 1, p.Walls);           // the outline of each storey, less the door
        Assert.True(p.Door);
        Assert.Equal(15, p.Floors);                   // ground floor, less the stair cell
        Assert.Equal(15, p.Decks);                    // upper floor, less the stair cell
        Assert.Equal(2, p.Stairs);                    // up, then down
        Assert.Equal(16, p.Roofs);
        var bps = sim.Constructions;
        Assert.Equal(StructureKind.Stair, bps.At(plan.Stair!.Value)!.Kind);
        var upper = bps.At(plan.Stair.Value with { Z = 5 })!;
        Assert.Equal(TileType.DownStair, (TileType)upper.Material);
        Assert.Equal(plan.Stair.Value, upper.Cell);   // built from the stair below
        var deck = bps.At(new TilePos(11, 11, 5))!;
        Assert.Equal(StructureKind.Deck, deck.Kind);
        Assert.Equal(new TilePos(11, 11, 4), deck.Cell); // its boards go to the floor below
        Assert.Equal(new TilePos(11, 11, 5), deck.Target);
    }

    [Fact]
    public void AnythingInTheWay_StopsTheWholeHouse()
    {
        var sim = new ColonySim(Flat());
        sim.World.Set(12, 12, 5, TileType.Stone); // a rock in the upper storey
        string msg = sim.Commands.Execute(new PlaceHouse(new TilePos(10, 10, 3), new TilePos(13, 13, 3), 2, 2, 1, 1));
        Assert.StartsWith("Can't plan the house", msg);
        Assert.Equal(0, sim.Constructions.Count);
        // One storey fits under it, and the rock is not the roof: it isn't over the storey.
        sim.World.Set(12, 12, 5, TileType.Air);
        sim.World.Set(12, 12, 4, TileType.Stone); // uneven ground
        Assert.StartsWith("Can't plan the house", sim.Commands.Execute(new PlaceHouse(new TilePos(10, 10, 3), new TilePos(13, 13, 3), 1, 2, 1, 1)));
        Assert.Equal(0, sim.Constructions.Count);
    }

    [Fact]
    public void RockOverhead_IsTheRoof_WhereItIs()
    {
        var sim = new ColonySim(Flat());
        for (int x = 10; x <= 11; x++) sim.World.Set(x, 10, 5, TileType.Stone); // an overhang over two cells
        var plan = Houses.Survey(sim, new TilePos(10, 10, 3), new TilePos(12, 12, 3), 1);
        Assert.Empty(plan.Conflicts);
        var p = Houses.Place(sim, plan, 2, 1, 1, 0);
        Assert.Equal(9 - 2, p.Roofs);
        Assert.Null(plan.Stair);
    }

    [Fact]
    public void AHalfBuiltHouse_SnapshotsAndPlaysOnIdentically()
    {
        var settings = new Net.GameSettings(Seed: 8, Width: 96, Depth: 96, Height: 48, Colonists: 5, Colonies: 1);
        var sim = settings.Create();
        var home = sim.Colonists[0].Position;
        // The nearest flat 3x3 with room for two storeys.
        Houses.Plan? spot = null;
        for (int r = 2; r < 30 && spot is null; r++)
        for (int dy = -r; dy <= r && spot is null; dy++)
        for (int dx = -r; dx <= r && spot is null; dx++)
        {
            int x = home.X + dx, y = home.Y + dy;
            if (x < 2 || y < 2 || x + 2 >= sim.World.Width - 2 || y + 2 >= sim.World.Depth - 2) continue;
            int g = sim.World.SurfaceLevel(x, y);
            var plan = Houses.Survey(sim, new TilePos(x, y, g), new TilePos(x + 2, y + 2, g), 2);
            if (plan.Conflicts.Count == 0 && plan.Door is not null) spot = plan;
        }
        Assert.NotNull(spot);
        var s = spot!;
        sim.Items.Drop(home, ItemType.Plank, 60);
        sim.Items.Drop(home.Offset(1, 0, 0), ItemType.Fiber, 20);
        Assert.StartsWith("House planned", sim.Commands.Execute(new PlaceHouse(new TilePos(s.X0, s.Y0, s.Z0 - 1), new TilePos(s.X1, s.Y1, s.Z0 - 1), 2, 2, 1, 1)));
        int planned = sim.Constructions.Count;
        for (int i = 0; i < 3000 && sim.Constructions.Count > planned / 2; i++) sim.Step();
        Assert.True(sim.Constructions.Count < planned, "something built before the snapshot");
        Assert.True(sim.Constructions.Count > 0, "still building at the snapshot");

        var loaded = Net.Snapshot.Load(Net.Snapshot.Take(sim, settings));
        Assert.Equal(Net.StateHash.Compute(sim), Net.StateHash.Compute(loaded));
        for (int i = 1; i <= 3000; i++)
        {
            sim.Step();
            loaded.Step();
            if (i % 250 == 0) Assert.True(Net.StateHash.Compute(sim) == Net.StateHash.Compute(loaded), $"diverged by tick {sim.Tick}");
        }
    }

    [Fact]
    public void Colonists_BuildATwoStoreyHouse_FromTheGroundUp_AndCanClimbIt()
    {
        var sim = new ColonySim(Flat()) { NeedsEnabled = false };
        sim.Items.Drop(new TilePos(25, 25, 4), ItemType.Plank, 50);
        sim.Items.Drop(new TilePos(25, 22, 4), ItemType.Plank, 50);
        sim.Items.Drop(new TilePos(22, 25, 4), ItemType.Fiber, 20);
        for (int i = 0; i < 4; i++) sim.AddColonist($"B{i}", new TilePos(2 + i, 2, 4));
        string msg = sim.Commands.Execute(new PlaceHouse(new TilePos(10, 10, 3), new TilePos(12, 12, 3), 2, 2, 1, 1));
        Assert.StartsWith("House planned", msg);
        int planned = sim.Constructions.Count;
        for (int i = 0; i < 40000 && sim.Constructions.Count > 0; i++) sim.Step();
        Assert.True(sim.Constructions.Count == 0, $"{sim.Constructions.Count} of {planned} pieces left: " +
            string.Join(", ", sim.Constructions.All.Take(5).Select(b => $"{b.Kind}@{b.Target}")));

        var stair = new TilePos(12, 12, 4); // the corner away from the south door
        Assert.Equal(TileType.UpStair, sim.World.Get(stair));
        Assert.Equal(TileType.DownStair, sim.World.Get(stair with { Z = 5 }));
        Assert.Equal(TileType.Deck, sim.World.Get(new TilePos(10, 10, 5)));
        Assert.Equal(1, sim.World.FloorAt(new TilePos(10, 10, 5)));
        Assert.Equal(1, sim.World.RoofAt(new TilePos(11, 11, 5)));
        Assert.Equal(EdgeKind.Door, EdgeInfo.Kind(sim.World.Edge(new TilePos(11, 9, 4), EdgeInfo.North)));
        // Upper walls stand on the upper storey.
        Assert.Equal(EdgeKind.Wall, EdgeInfo.Kind(sim.World.Edge(new TilePos(11, 9, 5), EdgeInfo.North)));
        // From outside, through the door and up the stairs, onto the upper floor.
        sim.Connectivity.Update();
        Assert.Equal(sim.Connectivity.ComponentOf(new TilePos(2, 2, 4)), sim.Connectivity.ComponentOf(new TilePos(10, 10, 5)));
        Assert.Contains(sim.Colonists, c => c.Position.Z == 4); // nobody walled in up there
    }
}
