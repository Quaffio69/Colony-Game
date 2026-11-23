using Colony.Core.Items;
using Colony.Core.Navigation;
using Colony.Core.Nature;
using Colony.Core.World;

namespace Colony.Core.Orders;

/// <summary>
/// A whole building planned in one go (the House tool): walls round every storey with a door on the ground floor,
/// a floor on each storey (laid on the ground, decks above), stairs up through the storeys in one corner, and a roof
/// over the top storey except where rock already roofs it. Planned all or nothing: <see cref="Survey"/> lists every
/// cell that is in the way (rock or earth, a tree, a station, a structure, other plans, no ground to stand on), and
/// nothing is placed while anything is. Existing walls on the outline are kept.
/// </summary>
public static class Houses
{
    public const int MaxStoreys = 6;
    /// <summary>Longest side (a house is a house: bigger boxes are probably a slip of the mouse).</summary>
    public const int MaxSide = 40;

    /// <summary>A house's cells and what is in its way.</summary>
    public sealed record Plan(int X0, int Y0, int X1, int Y1, int Z0, int Storeys, List<(TilePos Cell, string Why)> Conflicts,
        TilePos? Stair, (TilePos Cell, int Side)? Door)
    {
        public int Width => X1 - X0 + 1;
        public int Depth => Y1 - Y0 + 1;
        public int Top => Z0 + Storeys - 1;
        public string? Problem => Conflicts.Count == 0 ? null
            : Conflicts.Count == 1 ? $"{Conflicts[0].Why} at {Conflicts[0].Cell}" : $"{Conflicts[0].Why} ({Conflicts.Count} cells in the way)";
    }

    /// <summary>
    /// The house over a box between two ground tiles (its ground floor is the level above the lower corner), with
    /// <paramref name="storeys"/> storeys. The stairs go in the corner away from the door.
    /// </summary>
    public static Plan Survey(ColonySim sim, TilePos a, TilePos b, int storeys)
    {
        var world = sim.World;
        int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
        int z0 = Math.Min(a.Z, b.Z) + 1;
        storeys = Math.Clamp(storeys, 1, MaxStoreys);
        var conflicts = new List<(TilePos, string)>();
        if (x1 - x0 + 1 > MaxSide || y1 - y0 + 1 > MaxSide)
        {
            conflicts.Add((new TilePos(x0, y0, z0), $"too big: at most {MaxSide} cells a side"));
            return new Plan(x0, y0, x1, y1, z0, storeys, conflicts, null, null);
        }
        if (!world.InBounds(new TilePos(x0, y0, z0)) || !world.InBounds(new TilePos(x1, y1, z0 + storeys - 1)) || z0 <= 0)
        {
            conflicts.Add((new TilePos(x0, y0, z0), "outside the world"));
            return new Plan(x0, y0, x1, y1, z0, storeys, conflicts, null, null);
        }

        var door = DoorEdge(sim, x0, y0, x1, y1, z0);
        // Stairs in the corner farthest from the door (a one-storey house has none).
        TilePos? stair = null;
        if (storeys > 1)
        {
            var dc = door is { } d ? d.Cell : new TilePos(x0, y0 - 1, z0);
            int sx = Math.Abs(dc.X - x0) > Math.Abs(dc.X - x1) ? x0 : x1, sy = Math.Abs(dc.Y - y0) > Math.Abs(dc.Y - y1) ? y0 : y1;
            stair = new TilePos(sx, sy, z0);
        }

        for (int i = 0; i < storeys; i++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var c = new TilePos(x, y, z0 + i);
            if (Conflict(sim, c, ground: i == 0) is { } why) conflicts.Add((c, why));
        }
        return new Plan(x0, y0, x1, y1, z0, storeys, conflicts, stair, door);
    }

    /// <summary>What in a house's cell is in the way, or null.</summary>
    private static string? Conflict(ColonySim sim, TilePos c, bool ground)
    {
        var world = sim.World;
        var t = world.Get(c);
        if (TileInfo.IsSolid(t)) return ground ? "uneven ground (rock or earth in the way)" : "rock or earth in the way";
        if (TileInfo.IsStair(t)) return "stairs in the way";
        if (t == TileType.Deck) return "a built floor in the way";
        if (ground && !Movement.IsStandable(world, c)) return "no ground to build on";
        if (sim.Flora.TryGet(c, out var plant) && PlantInfo.IsTree(plant.Kind)) return "a tree in the way (chop it first)";
        if (sim.Rooms.PartAt(c) != null) return "a workshop station in the way";
        if (sim.Constructions.At(c) != null || sim.Constructions.IsOccupied?.Invoke(c) == true) return "a structure or plan in the way";
        if (!ground && (sim.Constructions.CoverAt(c, false) != null)) return "a plan in the way";
        return null;
    }

    /// <summary>
    /// The ground floor's door: the middle of the first side (south, north, west, east) with ground to step out onto;
    /// none if a side already has a door, or nowhere has ground.
    /// </summary>
    private static (TilePos Cell, int Side)? DoorEdge(ColonySim sim, int x0, int y0, int x1, int y1, int z)
    {
        var world = sim.World;
        int mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
        // (outside cell, the edge's owning cell and side)
        var options = new (TilePos Outside, TilePos Cell, int Side)[]
        {
            (new TilePos(mx, y0 - 1, z), new TilePos(mx, y0 - 1, z), EdgeInfo.North), // south side
            (new TilePos(mx, y1 + 1, z), new TilePos(mx, y1, z), EdgeInfo.North),     // north side
            (new TilePos(x0 - 1, my, z), new TilePos(x0 - 1, my, z), EdgeInfo.East),  // west side
            (new TilePos(x1 + 1, my, z), new TilePos(x1, my, z), EdgeInfo.East),      // east side
        };
        // A door already on the outline will do.
        for (int x = x0; x <= x1; x++)
            if (IsDoor(world, new TilePos(x, y0 - 1, z), EdgeInfo.North) || IsDoor(world, new TilePos(x, y1, z), EdgeInfo.North)) return null;
        for (int y = y0; y <= y1; y++)
            if (IsDoor(world, new TilePos(x0 - 1, y, z), EdgeInfo.East) || IsDoor(world, new TilePos(x1, y, z), EdgeInfo.East)) return null;
        foreach (var (outside, cell, side) in options)
            if (world.InBounds(outside) && world.InBounds(cell) && Movement.IsStandable(world, outside)) return (cell, side);
        return null;
    }

    private static bool IsDoor(VoxelWorld world, TilePos cell, int side) =>
        world.InBounds(cell) && EdgeInfo.Kind(world.Edge(cell, side)) == EdgeKind.Door;

    /// <summary>What a house plans, counted by piece.</summary>
    public readonly record struct Placed(int Walls, int Floors, int Decks, int Stairs, int Roofs, bool Door, Dictionary<ItemType, int> Cost);

    /// <summary>Plans the whole house (call only when <see cref="Plan.Conflicts"/> is empty).</summary>
    public static Placed Place(ColonySim sim, Plan plan, int wall, int floor, int roof, int faction)
    {
        var bps = sim.Constructions;
        int walls = 0, floors = 0, decks = 0, stairs = 0, roofs = 0;
        var cost = new Dictionary<ItemType, int>();
        void Count(Blueprint? bp)
        {
            if (bp is null) return;
            var (m, n) = bp.Cost;
            cost[m] = cost.GetValueOrDefault(m) + n;
        }

        // Bottom up, so each piece finds the one it is built from already planned.
        for (int i = 0; i < plan.Storeys; i++)
        {
            int z = plan.Z0 + i;
            if (plan.Stair is { } st)
            {
                var type = i == 0 ? TileType.UpStair : i == plan.Storeys - 1 ? TileType.DownStair : TileType.UpDownStair;
                var bp = bps.PlaceStair(st with { Z = z }, type, faction);
                if (bp != null) { stairs++; Count(bp); }
            }
            for (int y = plan.Y0; y <= plan.Y1; y++)
            for (int x = plan.X0; x <= plan.X1; x++)
            {
                var c = new TilePos(x, y, z);
                if (plan.Stair is { } s && s.X == x && s.Y == y) continue;
                if (i == 0)
                {
                    var bp = bps.PlaceCover(StructureKind.Floor, c, floor, faction);
                    if (bp != null) { floors++; Count(bp); }
                }
                else
                {
                    var bp = bps.PlaceDeck(c, floor, faction);
                    if (bp != null) { decks++; Count(bp); }
                }
            }
            // The door first, so the walls leave its edge.
            if (i == 0 && plan.Door is { } door && bps.PlaceEdge(StructureKind.Door, door.Cell, door.Side, wall, faction) is { } d) Count(d);
            int before = bps.Count;
            walls += bps.PlaceOutline(new TilePos(plan.X0, plan.Y0, z - 1), new TilePos(plan.X1, plan.Y1, z - 1), wall, StructureKind.EdgeWall, faction);
            for (int k = before; k < bps.Count; k++) Count(bps.All[k]);
        }
        // The roof over the top storey, where no rock does the job.
        for (int y = plan.Y0; y <= plan.Y1; y++)
        for (int x = plan.X0; x <= plan.X1; x++)
        {
            var bp = bps.PlaceCover(StructureKind.Roof, new TilePos(x, y, plan.Top), roof, faction);
            if (bp != null) { roofs++; Count(bp); }
        }
        return new Placed(walls, floors, decks, stairs, roofs, plan.Door != null, cost);
    }
}
