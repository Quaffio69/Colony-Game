using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.World;
using Colony.Core.Navigation;

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
    /// <summary>A thin wall on a cell edge (see <see cref="EdgeInfo"/>): rooms keep their floor; walls, dams.</summary>
    EdgeWall,
    /// <summary>A door on a cell edge: walkers pass, water too.</summary>
    Door,
    /// <summary>A window on a cell edge: lets in light, stops walkers and water.</summary>
    Window,
    /// <summary>A floor laid over a cell's ground (plank, flagstone, tiles).</summary>
    Floor,
    /// <summary>A roof over a cell (thatch, shingle, slate, clay tile).</summary>
    Roof,
}

/// <summary>A structure waiting for its material and a builder.</summary>
public sealed class Blueprint
{
    public required TilePos Cell { get; init; }
    public required StructureKind Kind { get; init; }
    /// <summary>The colony that planned it: its haulers supply it, its builders build it.</summary>
    public int Faction { get; init; }
    /// <summary>0..3: +x, +y, -x, -y (which way a pump pushes, or a wheel's axle runs).</summary>
    public int Facing { get; init; }
    public BuildCost Cost => Constructions.Cost(Kind, Material);
    /// <summary>For edge pieces: the cell owning the edge and its side (<see cref="EdgeInfo.East"/>/<see cref="EdgeInfo.North"/>);
    /// <see cref="Cell"/> is then the site beside it where materials are brought. Side -1: a whole-cell structure.</summary>
    public TilePos EdgeCell { get; init; }
    public int Side { get; init; } = -1;
    /// <summary>Wall material (<see cref="WallMaterials"/>) of an edge piece: what a wall costs, how any piece looks.</summary>
    public int Material { get; init; }
    public bool IsEdge => Side >= 0;
    public bool IsCover => Kind is StructureKind.Floor or StructureKind.Roof;
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

    public static BuildCost Cost(StructureKind kind, int material = 0) => kind switch
    {
        StructureKind.Wall => new BuildCost(ItemType.Stone, 1),
        StructureKind.WaterWheel => new BuildCost(ItemType.Plank, 6),
        StructureKind.Pump => new BuildCost(ItemType.Plank, 8),
        StructureKind.EdgeWall => new BuildCost(Catalog.Item(WallMaterials.All[WallMaterials.Clamp(material)].Item), 1),
        StructureKind.Door => new BuildCost(ItemType.Plank, 2),
        StructureKind.Window => new BuildCost(Catalog.Item("glass"), 1),
        StructureKind.Floor => new BuildCost(Catalog.Item(FloorMaterials.All[Math.Clamp(material, 1, FloorMaterials.Count - 1)].Item), 1),
        _ => new BuildCost(Catalog.Item(RoofMaterials.All[Math.Clamp(material, 1, RoofMaterials.Count - 1)].Item), 1),
    };

    public static int BuildTicks(StructureKind kind) => kind switch
    {
        StructureKind.Wall => 50,
        StructureKind.EdgeWall => 40,
        StructureKind.Floor => 30,
        StructureKind.Roof => 45,
        StructureKind.Door or StructureKind.Window => 80,
        _ => 120,
    };

    public static string Name(StructureKind kind) => kind switch
    {
        StructureKind.Wall => "Wall",
        StructureKind.WaterWheel => "Water wheel",
        StructureKind.Pump => "Pump",
        StructureKind.EdgeWall => "Thin wall",
        StructureKind.Door => "Door",
        StructureKind.Floor => "Floor",
        StructureKind.Roof => "Roof",
        _ => "Window",
    };

    public static bool IsEdgeKind(StructureKind kind) => kind is StructureKind.EdgeWall or StructureKind.Door or StructureKind.Window;

    public static EdgeKind EdgeKindOf(StructureKind kind) => kind switch
    {
        StructureKind.EdgeWall => EdgeKind.Wall,
        StructureKind.Door => EdgeKind.Door,
        _ => EdgeKind.Window,
    };

    // ---- Edge pieces: thin walls, doors, windows -------------------------------------------------------

    private readonly Dictionary<(TilePos, int), Blueprint> _byEdge = new();

    public Blueprint? EdgeAt(TilePos cell, int side) => _byEdge.GetValueOrDefault((cell, side));

    /// <summary>The cell beside an edge where its materials go: whichever of its two cells has floor (its own first).</summary>
    public TilePos? SiteOf(TilePos cell, int side)
    {
        var other = side == EdgeInfo.East ? cell.Offset(1, 0, 0) : cell.Offset(0, 1, 0);
        if (Navigation.Movement.IsStandable(_world, cell)) return cell;
        if (_world.InBounds(other) && Navigation.Movement.IsStandable(_world, other)) return other;
        return null;
    }

    /// <summary>Why a wall, door or window can't go on this edge, or null if it can.</summary>
    public string? CanPlaceEdge(StructureKind kind, TilePos cell, int side)
    {
        if (!IsEdgeKind(kind) || side is not (EdgeInfo.East or EdgeInfo.North)) return "not an edge piece";
        if (!_world.InBounds(cell) || cell.Z <= 0) return "outside the world";
        var other = side == EdgeInfo.East ? cell.Offset(1, 0, 0) : cell.Offset(0, 1, 0);
        if (!_world.InBounds(other)) return "at the edge of the world";
        if (TileInfo.IsSolid(_world.Get(cell)) && TileInfo.IsSolid(_world.Get(other))) return "solid on both sides";
        if (SiteOf(cell, side) is null) return "needs floor on one side";
        if (_byEdge.ContainsKey((cell, side))) return "something is already planned here";
        var now = EdgeInfo.Kind(_world.Edge(cell, side));
        if (now == EdgeKindOf(kind)) return $"there is already a {Name(kind).ToLowerInvariant()} here";
        if (kind == StructureKind.EdgeWall && now != EdgeKind.None) return "something is already built here";
        return null;
    }

    /// <summary>Plans a thin wall, door or window (a door or window may replace a built wall: it is cut in when built).</summary>
    public Blueprint? PlaceEdge(StructureKind kind, TilePos cell, int side, int material = 0, int faction = 0)
    {
        if (CanPlaceEdge(kind, cell, side) is not null) return null;
        var bp = new Blueprint { Cell = SiteOf(cell, side)!.Value, Kind = kind, EdgeCell = cell, Side = side, Material = WallMaterials.Clamp(material), Faction = faction };
        _byEdge[(cell, side)] = bp;
        AddSite(bp);
        _order.Add(bp);
        Version++;
        return bp;
    }

    /// <summary>
    /// Walls all round a box of floor cells (the edges on its outline, on the level above each corner's ground: the
    /// box's floor level). Returns how many were planned.
    /// </summary>
    public int PlaceOutline(TilePos a, TilePos b, int material, StructureKind kind = StructureKind.EdgeWall, int faction = 0)
    {
        int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y), z = Math.Min(a.Z, b.Z) + 1;
        int n = 0;
        for (int x = x0; x <= x1; x++)
        {
            if (PlaceEdge(kind, new TilePos(x, y0 - 1, z), EdgeInfo.North, material, faction) is not null) n++; // south side
            if (PlaceEdge(kind, new TilePos(x, y1, z), EdgeInfo.North, material, faction) is not null) n++;     // north side
        }
        for (int y = y0; y <= y1; y++)
        {
            if (PlaceEdge(kind, new TilePos(x0 - 1, y, z), EdgeInfo.East, material, faction) is not null) n++;  // west side
            if (PlaceEdge(kind, new TilePos(x1, y, z), EdgeInfo.East, material, faction) is not null) n++;      // east side
        }
        return n;
    }

    // ---- Floors and roofs ------------------------------------------------------------------------------

    private readonly Dictionary<(TilePos, bool Roof), Blueprint> _byCover = new();
    /// <summary>Edge and cover pieces by the cell their materials are brought to (several may share one).</summary>
    private readonly Dictionary<TilePos, List<Blueprint>> _bySite = new();

    private void AddSite(Blueprint bp)
    {
        if (!_bySite.TryGetValue(bp.Cell, out var list)) _bySite[bp.Cell] = list = new List<Blueprint>();
        list.Add(bp);
    }

    public Blueprint? CoverAt(TilePos cell, bool roof) => _byCover.GetValueOrDefault((cell, roof));

    /// <summary>Why a floor or roof of this material can't go on this cell, or null if it can.</summary>
    public string? CanPlaceCover(StructureKind kind, TilePos cell, int material)
    {
        bool roof = kind == StructureKind.Roof;
        if (kind is not (StructureKind.Floor or StructureKind.Roof)) return "not a floor or roof";
        if (material < 1 || material >= (roof ? RoofMaterials.Count : FloorMaterials.Count)) return "no such material";
        if (!_world.InBounds(cell) || cell.Z <= 0) return "outside the world";
        if (!Movement.IsStandable(_world, cell)) return "needs floor to stand on";
        if (!roof && TileInfo.IsStair(_world.Get(cell))) return "there are stairs here";
        if (roof && (!_world.InBounds(cell.Offset(0, 0, 1)) || TileInfo.IsSolid(_world.Get(cell.Offset(0, 0, 1))))) return "already under rock";
        if (_byCover.ContainsKey((cell, roof))) return "already planned";
        if ((roof ? _world.RoofAt(cell) : _world.FloorAt(cell)) == material) return roof ? "already roofed so" : "already floored so";
        return null;
    }

    public Blueprint? PlaceCover(StructureKind kind, TilePos cell, int material, int faction = 0)
    {
        if (CanPlaceCover(kind, cell, material) is not null) return null;
        var bp = new Blueprint { Cell = cell, Kind = kind, Material = material, Faction = faction };
        _byCover[(cell, kind == StructureKind.Roof)] = bp;
        AddSite(bp);
        _order.Add(bp);
        Version++;
        return bp;
    }

    /// <summary>Floors (or roofs) over every floor cell in a box between ground tiles, like painting a room. Returns how many.</summary>
    public int PlaceCoverBox(StructureKind kind, TilePos a, TilePos b, int material, int faction = 0)
    {
        int n = 0;
        for (int z = Math.Min(a.Z, b.Z) + 1; z <= Math.Max(a.Z, b.Z) + 1; z++)
        for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
        for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
            if (PlaceCover(kind, new TilePos(x, y, z), material, faction) is not null) n++;
        return n;
    }

    /// <summary>Takes up floors or roofs (planned or built) on the floor cells of a box, refunding built ones. Returns how many.</summary>
    public int ClearCoverBox(StructureKind kind, TilePos a, TilePos b, ItemPiles items, int faction = Owners.Any)
    {
        bool roof = kind == StructureKind.Roof;
        int n = 0;
        for (int z = Math.Min(a.Z, b.Z) + 1; z <= Math.Max(a.Z, b.Z) + 1; z++)
        for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
        for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
        {
            var c = new TilePos(x, y, z);
            if (CoverAt(c, roof) is { } bp && (faction == Owners.Any || bp.Faction == faction))
            {
                Remove(bp);
                n++;
            }
            int built = roof ? _world.RoofAt(c) : _world.FloorAt(c);
            if (built == 0) continue;
            if (roof) _world.SetRoof(c, 0);
            else _world.SetFloor(c, 0);
            var (mat, count) = Cost(kind, built);
            items.Drop(c, mat, count);
            n++;
        }
        if (n > 0) Version++;
        return n;
    }

    /// <summary>Cancels the blueprint on an edge (delivered materials stay as a loose pile). False if there is none.</summary>
    public bool CancelEdge(TilePos cell, int side, int faction = Owners.Any)
    {
        if (EdgeAt(cell, side) is not { } bp || (faction != Owners.Any && bp.Faction != faction)) return false;
        Remove(bp);
        return true;
    }

    /// <summary>Takes down a built thin wall, door or window at once, leaving its materials at its site.</summary>
    public bool Demolish(TilePos cell, int side, ItemPiles items)
    {
        byte info = _world.Edge(cell, side);
        if (info == 0) return false;
        var kind = EdgeInfo.Kind(info) switch { EdgeKind.Door => StructureKind.Door, EdgeKind.Window => StructureKind.Window, _ => StructureKind.EdgeWall };
        var site = SiteOf(cell, side);
        _world.SetEdge(cell, side, 0);
        var (mat, n) = Cost(kind, EdgeInfo.Material(info));
        if (site is { } s) items.Drop(s, mat, n);
        Version++;
        return true;
    }

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

    public Blueprint? Place(StructureKind kind, TilePos cell, int facing = 0, int faction = 0)
    {
        if (CanPlace(kind, cell) is not null) return null;
        var bp = new Blueprint { Cell = cell, Kind = kind, Facing = facing & 3, Faction = faction };
        _byCell[cell] = bp;
        _order.Add(bp);
        Version++;
        return bp;
    }

    /// <summary>
    /// Walls in every open cell of the box between two ground tiles, from the level above the lower corner up
    /// to the level above the upper one (so a box spanning levels raises a dam that high). Returns how many.
    /// </summary>
    public int PlaceWalls(TilePos a, TilePos b, int faction = 0)
    {
        int n = 0;
        for (int z = Math.Min(a.Z, b.Z) + 1; z <= Math.Max(a.Z, b.Z) + 1; z++)
        for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
        for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
            if (Place(StructureKind.Wall, new TilePos(x, y, z), 0, faction) is not null) n++;
        return n;
    }

    /// <summary>Cancels blueprints in the box (and the level above it). Delivered materials stay as loose piles.</summary>
    public int CancelBox(TilePos a, TilePos b, int faction = Owners.Any)
    {
        var hits = _order.Where(bp => (faction == Owners.Any || bp.Faction == faction) &&
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
        if (bp.IsEdge) _byEdge.Remove((bp.EdgeCell, bp.Side));
        else if (bp.IsCover) _byCover.Remove((bp.Cell, bp.Kind == StructureKind.Roof));
        else _byCell.Remove(bp.Cell);
        if (_bySite.TryGetValue(bp.Cell, out var site))
        {
            site.Remove(bp);
            if (site.Count == 0) _bySite.Remove(bp.Cell);
        }
        _order.Remove(bp);
        Version++;
    }

    /// <summary>Units of the blueprint's material lying in its cell (<paramref name="available"/>: unreserved only).</summary>
    /// <summary>Whether a pile of <paramref name="type"/> here is materials for a blueprint (a structure, or an edge piece sited here).</summary>
    public bool IsSite(TilePos p, ItemType type) =>
        (At(p) is { } site && site.Cost.Material == type) || (_bySite.TryGetValue(p, out var here) && here.Exists(bp => bp.Cost.Material == type));

    public static int MaterialsOnSite(Blueprint bp, ItemPiles items, bool available = false) =>
        items.TryGet(bp.Cell, out var pile) && pile.Type == bp.Cost.Material ? (available ? pile.Available : pile.Count) : 0;
}
