using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.World;

namespace Colony.Core.Orders;

/// <summary>Things built in a single world cell outside rooms.</summary>
public enum StructureKind : byte
{
    /// <summary>A solid stone wall: dams, canal sides, embankments.</summary>
    Wall,
    /// <summary>Turns water flowing through its cell into power.</summary>
    WaterWheel,
    /// <summary>Lifts water from its cell to the cell ahead and one level up, using power.</summary>
    Pump,
}

/// <summary>A structure waiting for its material and a builder.</summary>
public sealed class Blueprint
{
    public required TilePos Cell { get; init; }
    public required StructureKind Kind { get; init; }
    /// <summary>0..3: +x, +y, -x, -y (which way a pump pushes, or a wheel's axle runs).</summary>
    public int Facing { get; init; }
    public BuildCost Cost => Constructions.Cost(Kind);
    /// <summary>Colonist building it (-1 = nobody).</summary>
    public int BuilderId { get; internal set; } = -1;
    public bool Removed { get; internal set; }
}

/// <summary>
/// Single-cell structure blueprints (walls, water wheels, pumps). Like room parts: haulers deliver the cost
/// onto the cell as an ordinary pile, a builder (Designations labor) uses it up and the structure appears
/// (see <see cref="ColonySim.CompleteConstruction"/>). Walls stack: a wall can go on a wall blueprint below,
/// whose cell becomes a floor once built, so dams rise level by level. Deterministic order (placement order).
/// </summary>
public sealed class Constructions
{
    private readonly VoxelWorld _world;
    private readonly Dictionary<TilePos, Blueprint> _byCell = new();
    private readonly List<Blueprint> _order = new();

    public Constructions(VoxelWorld world) => _world = world;

    public int Version { get; private set; }
    public int Count => _order.Count;
    public IReadOnlyList<Blueprint> All => _order;
    public Blueprint? At(TilePos p) => _byCell.GetValueOrDefault(p);

    /// <summary>Cells already taken by a built structure (set by <see cref="ColonySim"/>).</summary>
    public Func<TilePos, bool>? IsOccupied { get; set; }

    public static BuildCost Cost(StructureKind kind) => kind switch
    {
        StructureKind.Wall => new BuildCost(ItemType.Stone, 1),
        StructureKind.WaterWheel => new BuildCost(ItemType.Plank, 6),
        _ => new BuildCost(ItemType.Plank, 8),
    };

    public static int BuildTicks(StructureKind kind) => kind == StructureKind.Wall ? 50 : 120;

    public static string Name(StructureKind kind) => kind switch
    {
        StructureKind.Wall => "Wall",
        StructureKind.WaterWheel => "Water wheel",
        _ => "Pump",
    };

    /// <summary>Why a structure can't go in this cell, or null if it can.</summary>
    public string? CanPlace(StructureKind kind, TilePos cell)
    {
        if (!_world.InBounds(cell) || cell.Z <= 0) return "outside the world";
        var tile = _world.Get(cell);
        if (TileInfo.IsSolid(tile)) return "the cell is solid";
        if (TileInfo.IsStair(tile)) return "there are stairs here";
        if (_byCell.ContainsKey(cell) || IsOccupied?.Invoke(cell) == true) return "something is already planned or built here";
        var below = cell.Offset(0, 0, -1);
        bool onFloor = TileInfo.IsSolid(_world.Get(below));
        if (kind == StructureKind.Wall)
            return onFloor || _byCell.TryGetValue(below, out var under) && under.Kind == StructureKind.Wall ? null : "a wall needs ground or a wall below";
        if (kind == StructureKind.WaterWheel)
        {
            // A wheel can hang on a wall beside a fall (where it makes the most power), or stand on the ground.
            bool mounted = onFloor || TileInfo.IsSolid(_world.Get(cell.Offset(1, 0, 0))) || TileInfo.IsSolid(_world.Get(cell.Offset(-1, 0, 0))) ||
                           TileInfo.IsSolid(_world.Get(cell.Offset(0, 1, 0))) || TileInfo.IsSolid(_world.Get(cell.Offset(0, -1, 0)));
            return mounted ? null : "needs ground below or a wall beside it";
        }
        return onFloor ? null : "needs ground below";
    }

    public Blueprint? Place(StructureKind kind, TilePos cell, int facing = 0)
    {
        if (CanPlace(kind, cell) is not null) return null;
        var bp = new Blueprint { Cell = cell, Kind = kind, Facing = facing & 3 };
        _byCell[cell] = bp;
        _order.Add(bp);
        Version++;
        return bp;
    }

    /// <summary>
    /// Walls in every open cell of the box between two ground tiles, from the level above the lower corner up
    /// to the level above the upper one (so a box spanning levels raises a dam that high). Returns how many.
    /// </summary>
    public int PlaceWalls(TilePos a, TilePos b)
    {
        int n = 0;
        for (int z = Math.Min(a.Z, b.Z) + 1; z <= Math.Max(a.Z, b.Z) + 1; z++)
        for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
        for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
            if (Place(StructureKind.Wall, new TilePos(x, y, z)) is not null) n++;
        return n;
    }

    /// <summary>Cancels blueprints in the box (and the level above it). Delivered materials stay as loose piles.</summary>
    public int CancelBox(TilePos a, TilePos b)
    {
        var hits = _order.Where(bp =>
            bp.Cell.X >= Math.Min(a.X, b.X) && bp.Cell.X <= Math.Max(a.X, b.X) &&
            bp.Cell.Y >= Math.Min(a.Y, b.Y) && bp.Cell.Y <= Math.Max(a.Y, b.Y) &&
            bp.Cell.Z >= Math.Min(a.Z, b.Z) && bp.Cell.Z <= Math.Max(a.Z, b.Z) + 1).ToList();
        foreach (var bp in hits) Remove(bp);
        return hits.Count;
    }

    internal void Remove(Blueprint bp)
    {
        if (bp.Removed) return;
        bp.Removed = true;
        bp.BuilderId = -1;
        _byCell.Remove(bp.Cell);
        _order.Remove(bp);
        Version++;
    }

    /// <summary>Units of the blueprint's material lying in its cell (<paramref name="available"/>: unreserved only).</summary>
    public static int MaterialsOnSite(Blueprint bp, ItemPiles items, bool available = false) =>
        items.TryGet(bp.Cell, out var pile) && pile.Type == bp.Cost.Material ? (available ? pile.Available : pile.Count) : 0;
}
