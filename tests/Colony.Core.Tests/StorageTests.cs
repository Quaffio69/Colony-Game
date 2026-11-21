using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>Storage racks, grass growing back over dug dirt, and scaffolds for work out of reach.</summary>
public class StorageTests
{
    /// <summary>Stone up to z=1, soil at z=2, grass at z=3: colonists walk at z=4.</summary>
    private static ColonySim Meadow(int size = 32, int height = 12)
    {
        var w = new VoxelWorld(size, size, height);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            for (int z = 0; z <= 1; z++) w.Set(x, y, z, TileType.Stone);
            w.Set(x, y, 2, TileType.Soil);
            w.Set(x, y, 3, TileType.Grass);
        }
        return new ColonySim(w) { NeedsEnabled = false };
    }

    private static int Units(ColonySim sim, ItemType type) => sim.Items.All.Values.Where(p => p.Type == type).Sum(p => p.Count);

    // ---- Racks ------------------------------------------------------------------------------------------

    [Fact]
    public void Racks_GoOnlyOnStockpileCells_AndRiseIntoTheOpenSpace()
    {
        var sim = Meadow();
        var cell = new TilePos(10, 10, 4);
        Assert.Equal("racks go on stockpile cells", sim.Constructions.CanPlace(StructureKind.Rack, cell));
        sim.Stockpiles.Add(cell);
        Assert.Null(sim.Constructions.CanPlace(StructureKind.Rack, cell));
        sim.World.Set(10, 10, 6, TileType.Stone); // a low ceiling: two levels of room
        var bp = sim.Constructions.Place(StructureKind.Rack, cell, 1);
        Assert.NotNull(bp);
        Assert.Equal(2, bp!.Material);
        Assert.Equal(2 * Constructions.RackPlanksPerLevel, bp.Cost.Count);
        Assert.NotNull(sim.Constructions.CanPlace(StructureKind.Rack, cell)); // one per cell
    }

    [Fact]
    public void ABuiltRack_HoldsSeveralStacks_AndComesDownForItsPlanks()
    {
        var sim = Meadow();
        var cell = new TilePos(10, 10, 4);
        sim.Stockpiles.AddFloorBox(cell.Offset(0, 0, -1), cell.Offset(0, 0, -1));
        Assert.StartsWith("Planned 1", sim.Commands.Execute(new PlaceRacks(cell.Offset(0, 0, -1), cell.Offset(0, 0, -1), 0)));
        sim.Items.Drop(new TilePos(4, 4, 4), ItemType.Plank, 9);
        for (int i = 0; i < 3; i++) sim.AddColonist($"C{i}", new TilePos(6 + i, 6, 4));
        for (int t = 0; t < 4000 && !sim.Stockpiles.Racks.ContainsKey(cell); t++) sim.Step();
        Assert.True(sim.Stockpiles.Racks.TryGetValue(cell, out var rack), "the rack was never built");
        Assert.Equal(Stockpiles.MaxRackLevels, rack.Levels);
        int limit = ItemInfo.StackLimit(ItemType.Stone);
        Assert.Equal(limit * Stockpiles.Capacity(rack.Levels), sim.Items.RoomAt(cell, ItemType.Stone));

        // Three stacks of stone: all of it ends up on the one racked cell.
        sim.Items.Drop(new TilePos(20, 20, 4), ItemType.Stone, 3 * limit);
        for (int t = 0; t < 20_000 && !(sim.Items.TryGet(cell, out var p) && p.Count == 3 * limit); t++) sim.Step();
        Assert.True(sim.Items.TryGet(cell, out var stored));
        Assert.Equal(3 * limit, stored.Count);

        // Taken down, it leaves its planks.
        int before = Units(sim, ItemType.Plank);
        Assert.StartsWith("Took down 1", sim.Commands.Execute(new RemoveStructures(cell.Offset(0, 0, -1), cell.Offset(0, 0, -1))));
        Assert.False(sim.Stockpiles.Racks.ContainsKey(cell));
        Assert.Equal(before + Stockpiles.MaxRackLevels * Constructions.RackPlanksPerLevel, Units(sim, ItemType.Plank));
        Assert.Equal(3 * limit, Units(sim, ItemType.Stone)); // nothing lost
        foreach (var t in new[] { ItemType.Stone, ItemType.Plank }) Assert.Equal(Units(sim, t), sim.Items.UnitsOf(t)); // the running tally
    }

    [Fact]
    public void RemovingTheZone_TakesItsRackDown()
    {
        var sim = Meadow();
        var cell = new TilePos(10, 10, 4);
        sim.Stockpiles.Add(cell);
        Assert.True(sim.Stockpiles.AddRack(cell, 2, 0));
        Assert.Equal(Stockpiles.Capacity(2), sim.Items.CapacityAt!(cell));
        sim.Stockpiles.Remove(cell);
        Assert.Empty(sim.Stockpiles.Racks);
        Assert.Equal(2 * Constructions.RackPlanksPerLevel, Units(sim, ItemType.Plank));
    }

    // ---- Grass growing back -------------------------------------------------------------------------------

    [Fact]
    public void Grass_CreepsBackOverDugDirt_ButNotUnderAnOverhang()
    {
        var sim = Meadow();
        sim.World.Set(20, 5, 6, TileType.Stone); // a floating overhang above one dig
        sim.Designations.DesignateBox(new TilePos(5, 5, 3), new TilePos(8, 5, 3));
        sim.Designations.DesignateBox(new TilePos(20, 5, 3), new TilePos(20, 5, 3));
        foreach (var p in sim.Designations.All.Keys.ToList()) sim.CompleteDesignation(p);
        Assert.Equal(5, sim.Regrowth.Count);
        for (int x = 5; x <= 8; x++) Assert.Equal(TileType.Soil, sim.World.Get(x, 5, 2));

        for (int t = 0; t < Needs.Day * 2; t++) sim.Step();
        for (int x = 5; x <= 8; x++) Assert.Equal(TileType.Grass, sim.World.Get(x, 5, 2));
        Assert.Equal(TileType.Soil, sim.World.Get(20, 5, 2)); // no sky there
        Assert.Equal(0, sim.Regrowth.Count);
        Assert.Equal(4, sim.Regrowth.Greened);
    }

    // ---- Scaffolds ----------------------------------------------------------------------------------------

    /// <summary>A stone ceiling at z=6 (two levels over the floor) held up by a pillar; one ceiling tile to dig.</summary>
    private static (ColonySim Sim, TilePos Target) Ceiling()
    {
        var sim = Meadow();
        for (int y = 8; y <= 12; y++)
        for (int x = 8; x <= 12; x++) sim.World.Set(x, y, 6, TileType.Stone);
        for (int z = 4; z <= 6; z++) sim.World.Set(13, 10, z, TileType.Stone);
        var target = new TilePos(10, 10, 6);
        sim.Designations.DesignateBox(target, target);
        sim.AddColonist("Miner", new TilePos(4, 4, 4));
        return (sim, target);
    }

    [Fact]
    public void WorkOutOfReach_WaitsForAScaffold()
    {
        var (sim, target) = Ceiling();
        for (int t = 0; t < 600; t++) sim.Step();
        Assert.True(sim.Designations.Contains(target)); // nothing to stand on
        Assert.Equal(ColonistTask.None, sim.Colonists[0].Task);
        sim.Connectivity.Update();
        sim.JobBoard.Update(sim.World, sim.Designations, sim.Connectivity);
        int home = sim.Connectivity.ComponentOf(sim.Colonists[0].Position);
        var job = Assert.Single(sim.JobBoard.Jobs, j => j.Target == target);
        Assert.DoesNotContain(home, job.Components); // only the top of the ceiling, out of reach
        Assert.Contains(home, job.HighComponents);
    }

    [Fact]
    public void AScaffold_IsFetched_SetUp_WorkedFrom_AndLeftBehind()
    {
        var (sim, target) = Ceiling();
        var scaffold = Colonist.ScaffoldItem;
        sim.Items.Drop(new TilePos(3, 3, 4), scaffold, 1);
        bool stoodOnIt = false, fetched = false;
        for (int t = 0; t < 4000 && (sim.Designations.Contains(target) || sim.Colonists[0].Task != ColonistTask.None); t++)
        {
            sim.Step();
            var c = sim.Colonists[0];
            fetched |= c.CarryingType == scaffold && c.CarryingCount == 1;
            stoodOnIt |= c.OnScaffold;
        }
        Assert.False(sim.Designations.Contains(target));
        Assert.Equal(TileType.Air, sim.World.Get(target));
        Assert.True(fetched, "never carried the scaffold");
        Assert.True(stoodOnIt, "never worked from the scaffold");
        Assert.False(sim.Colonists[0].OnScaffold);
        Assert.Equal(1, Units(sim, scaffold)); // taken down and left as a pile
        Assert.Equal(1, sim.Items.UnitsOf(scaffold));
        Assert.All(sim.Items.All.Values, p => Assert.True(p.Reserved == 0 && p.Incoming == 0));
    }

    [Fact]
    public void CancellingTheWork_LeavesTheScaffoldWhereItStood()
    {
        var (sim, target) = Ceiling();
        var scaffold = Colonist.ScaffoldItem;
        sim.Items.Drop(new TilePos(3, 3, 4), scaffold, 1);
        for (int t = 0; t < 4000 && !sim.Colonists[0].OnScaffold; t++) sim.Step();
        Assert.True(sim.Colonists[0].OnScaffold);
        sim.Designations.Cancel(target);
        sim.Step();
        Assert.False(sim.Colonists[0].OnScaffold);
        Assert.Equal(1, Units(sim, scaffold));
        Assert.True(sim.Items.TryGet(sim.Colonists[0].Position, out var pile) && pile.Type == scaffold);
    }
}

/// <summary>Taking things away: the cancel box drops every plan, Demolish takes down what is built; founders can build a workshop.</summary>
public class DemolishTests
{
    private static ColonySim Flat()
    {
        var w = new VoxelWorld(32, 32, 12);
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            for (int z = 0; z <= 2; z++) w.Set(x, y, z, TileType.Stone);
            w.Set(x, y, 3, TileType.Grass);
        }
        return new ColonySim(w) { NeedsEnabled = false };
    }

    private static int WallsBuilt(ColonySim sim)
    {
        int n = 0;
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            if (sim.World.Edge(new TilePos(x, y, 4), EdgeInfo.East) != 0) n++;
            if (sim.World.Edge(new TilePos(x, y, 4), EdgeInfo.North) != 0) n++;
        }
        return n;
    }

    [Fact]
    public void TheCancelBox_DropsPlannedWalls()
    {
        var sim = Flat();
        sim.Commands.Execute(new PlaceOutline(new TilePos(10, 10, 3), new TilePos(12, 12, 3), 2));
        Assert.Equal(12, sim.Constructions.Count);
        Assert.Contains("12 plans", sim.Commands.Execute(new CancelOrders(new TilePos(9, 9, 3), new TilePos(13, 13, 3))));
        Assert.Equal(0, sim.Constructions.Count);
    }

    [Fact]
    public void Demolish_TakesDownBuiltWalls_FloorsAndRacks_LeavingTheirMaterials()
    {
        var sim = Flat();
        sim.Commands.Execute(new PlaceOutline(new TilePos(10, 10, 3), new TilePos(12, 12, 3), 2)); // plank walls
        foreach (var bp in sim.Constructions.All.ToList()) sim.CompleteConstruction(bp);
        Assert.Equal(12, WallsBuilt(sim));
        sim.World.SetFloor(new TilePos(11, 11, 4), 1);
        var rackCell = new TilePos(20, 20, 4);
        sim.Stockpiles.Add(rackCell);
        sim.Stockpiles.AddRack(rackCell, 2, 0);

        // A box over the room's floor takes down its walls all round (the outline touches the box).
        sim.Commands.Execute(new Demolish(new TilePos(10, 10, 3), new TilePos(12, 12, 3)));
        Assert.Equal(0, WallsBuilt(sim));
        Assert.Equal(0, sim.World.FloorAt(new TilePos(11, 11, 4)));
        Assert.Equal(12 + 1, sim.Items.UnitsOf(ItemType.Plank)); // a plank a wall, and the plank floor
        Assert.Contains("1 structures", sim.Commands.Execute(new Demolish(rackCell.Offset(0, 0, -1), rackCell.Offset(0, 0, -1))));
        Assert.Empty(sim.Stockpiles.Racks);
    }

    [Fact]
    public void Founders_BuildTheirFirstCarpentry_FromTheTimberTheyBring()
    {
        var sim = ColonySim.Generate(new Procedural.WorldGenSettings(Seed: 7, Width: 64, Depth: 64, Height: 40), colonists: 7);
        Assert.Equal(ColonySim.StartingLogs, sim.Items.UnitsOf(ItemType.Log));
        var c = sim.Colonists[0].Position;
        var a = new TilePos(c.X + 2, c.Y + 2, c.Z - 1);
        sim.Commands.Execute(new PaintRoom("carpentry", a, a.Offset(5, 4, 0)));
        var room = Assert.Single(sim.Rooms.All);
        foreach (var station in room.Type.Stations)
            Assert.True(room.Cells.ToList().Exists(cell => sim.Commands.Execute(new PlacePart(station.Id, cell, false, 1)).StartsWith("Placed")), station.Name);
        for (int t = 0; t < 8000 && !room.Parts.All(p => p.Built); t++) sim.Step();
        Assert.All(room.Parts, p => Assert.True(p.Built, p.Name));
    }
}

public class WheelMountTests
{
    [Fact]
    public void AWaterWheel_HangsOnAWallFace_OrAThinWall_ButNotInOpenAir()
    {
        var w = new VoxelWorld(16, 16, 12);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
        for (int z = 0; z <= 2; z++) w.Set(x, y, z, TileType.Stone);
        for (int z = 3; z <= 8; z++) w.Set(5, 5, z, TileType.Stone); // a pillar
        var sim = new ColonySim(w);
        Assert.Null(sim.Constructions.CanPlace(StructureKind.WaterWheel, new TilePos(6, 5, 7)));      // against the pillar's face, mid-air
        Assert.NotNull(sim.Constructions.CanPlace(StructureKind.WaterWheel, new TilePos(10, 10, 7))); // in open air
        w.SetEdge(new TilePos(10, 10, 7), EdgeInfo.East, EdgeInfo.Make(EdgeKind.Wall, 2));
        Assert.Null(sim.Constructions.CanPlace(StructureKind.WaterWheel, new TilePos(10, 10, 7)));    // on a thin wall
        Assert.Null(sim.Constructions.CanPlace(StructureKind.WaterWheel, new TilePos(11, 10, 7)));    // the wall's other side
    }
}
