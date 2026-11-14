using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Industry;

/// <summary>A placed room part (machine, auxiliary or crate) covering one or more cells of a room's floor.</summary>
public sealed class Part
{
    public required int Id { get; init; }
    public required PartType Type { get; init; }
    /// <summary>1..<see cref="PartInfo.Sizes"/>: bigger machines batch more, bigger tools cover more.</summary>
    public int Size { get; init; } = 1;
    public required TilePos Origin { get; init; }
    public required bool Rotated { get; init; }
    public required TilePos[] Cells { get; init; }
    public Room Room { get; internal set; } = null!;
    public PartRole Role => PartInfo.Role(Type);
    public string Name => PartInfo.Name(Type, Size);
    /// <summary>One batch at this machine: the room's recipe scaled by the machine's size.</summary>
    public Recipe Batch => PartInfo.MachineRecipe(Type, Size, Room.Recipe);

    /// <summary>Colonist operating this machine (-1 = free).</summary>
    public int ClaimedBy { get; internal set; } = -1;
    /// <summary>A worker is processing at this machine right now (for animation and status).</summary>
    public bool Working { get; internal set; }
    /// <summary>Set once the part is removed, so anyone holding a reference notices.</summary>
    public bool Removed { get; internal set; }

    /// <summary>False while it is a blueprint: it does nothing until its materials arrive and a builder puts it up.</summary>
    public bool Built { get; internal set; }
    /// <summary>Colonist building this blueprint (-1 = nobody).</summary>
    public int BuilderId { get; internal set; } = -1;
    public BuildCost Cost => PartInfo.Cost(Type, Size);
}

/// <summary>
/// A factory room: a contiguous set of floor cells (connected by moves a worker could make within the room,
/// so it can span several levels joined by stairs) plus the parts placed on it.
/// </summary>
public sealed class Room
{
    internal readonly HashSet<TilePos> CellSet = new();
    internal readonly List<Part> PartList = new();

    public required int Id { get; init; }
    public required RoomType Type { get; init; }
    public IReadOnlyCollection<TilePos> Cells => CellSet;
    public IReadOnlyList<Part> Parts => PartList;
    public Recipe Recipe => RoomInfo.Recipe(Type);
    public string Name => $"{RoomInfo.Name(Type)} #{Id}";

    /// <summary>Built parts with a role (blueprints don't work yet).</summary>
    public IEnumerable<Part> PartsWithRole(PartRole role) => PartList.Where(p => p.Built && p.Role == role);
    public int Count(PartRole role) => PartList.Count(p => p.Built && p.Role == role);
    /// <summary>Parts still waiting for materials or a builder.</summary>
    public IEnumerable<Part> Blueprints => PartList.Where(p => !p.Built);

    /// <summary>Work-speed multiplier from auxiliaries (see <see cref="Rooms.Efficiency"/>).</summary>
    public float Efficiency => Rooms.Efficiency(this);
}

/// <summary>
/// All factory rooms and their parts. Rooms are always contiguous: painting merges into the same-type room it
/// touches and splits disconnected pieces into separate rooms; erasing or losing floor (digging, cave-ins)
/// re-splits, and parts follow the piece they stand on (or are removed if cut in two). Deterministic.
/// Placed parts start as blueprints (see <see cref="Part.Built"/>): haulers bring their materials onto the
/// footprint and a builder turns them into working parts.
/// </summary>
public sealed class Rooms
{
    /// <summary>Sandbox/dev switch: parts are placed already built, at no cost.</summary>
    public bool InstantBuild { get; set; }

    private readonly VoxelWorld _world;
    private readonly List<Room> _rooms = new();
    private readonly Dictionary<TilePos, Room> _cellRoom = new();
    private readonly Dictionary<TilePos, Part> _cellPart = new();
    private readonly List<(TilePos, float)> _moves = new(12);
    private int _nextRoomId = 1, _nextPartId = 1;

    public Rooms(VoxelWorld world) => _world = world;

    /// <summary>Cells rooms may not claim (e.g. stockpile zones). Set by <see cref="ColonySim"/>.</summary>
    public Func<TilePos, bool>? IsBlocked { get; set; }

    public int Version { get; private set; }
    public IReadOnlyList<Room> All => _rooms;
    public Room? RoomAt(TilePos cell) => _cellRoom.GetValueOrDefault(cell);
    public Part? PartAt(TilePos cell) => _cellPart.GetValueOrDefault(cell);

    // ---- Room cells ----------------------------------------------------------------------------------

    /// <summary>
    /// Paints a room of <paramref name="type"/> over every walkable cell in the box between two ground tiles
    /// (their levels and the level above, so a multi-level box takes every floor in it). Cells already in a
    /// room or blocked are skipped. Returns how many cells were added.
    /// </summary>
    public int Paint(RoomType type, TilePos a, TilePos b)
    {
        var candidates = new List<TilePos>();
        ForCells(a, b, c =>
        {
            if (_cellRoom.ContainsKey(c) || IsBlocked?.Invoke(c) == true) return;
            if (Movement.IsStandable(_world, c)) candidates.Add(c);
        });
        if (candidates.Count == 0) return 0;

        // Same-type rooms the new cells touch get merged with them.
        var touched = new List<Room>();
        foreach (var c in candidates)
        {
            Movement.GetMoves(_world, c, _moves);
            foreach (var (q, _) in _moves)
                if (_cellRoom.TryGetValue(q, out var r) && r.Type == type && !touched.Contains(r)) touched.Add(r);
        }
        touched.Sort((x, y) => x.Id.CompareTo(y.Id));
        var target = touched.Count > 0 ? touched[0] : NewRoom(type);
        for (int i = 1; i < touched.Count; i++) Merge(touched[i], target);
        foreach (var c in candidates)
        {
            target.CellSet.Add(c);
            _cellRoom[c] = target;
        }
        SplitIfDisconnected(target);
        Version++;
        return candidates.Count;
    }

    /// <summary>Removes room cells (and any parts on them) in the box. Returns how many cells were removed.</summary>
    public int Erase(TilePos a, TilePos b)
    {
        var affected = new List<Room>();
        int n = 0;
        ForCells(a, b, c =>
        {
            if (!_cellRoom.TryGetValue(c, out var r)) return;
            RemoveCell(c, r);
            if (!affected.Contains(r)) affected.Add(r);
            n++;
        });
        foreach (var r in affected) SplitIfDisconnected(r);
        if (n > 0) Version++;
        return n;
    }

    /// <summary>Drops a room cell whose floor is gone (dug out, caved in), re-splitting its room if needed.</summary>
    public void ValidateCell(TilePos c)
    {
        if (!_cellRoom.TryGetValue(c, out var r) || Movement.IsStandable(_world, c)) return;
        RemoveCell(c, r);
        SplitIfDisconnected(r);
        Version++;
    }

    private Room NewRoom(RoomType type)
    {
        var r = new Room { Id = _nextRoomId++, Type = type };
        _rooms.Add(r);
        return r;
    }

    private void Merge(Room from, Room into)
    {
        foreach (var c in from.CellSet)
        {
            into.CellSet.Add(c);
            _cellRoom[c] = into;
        }
        foreach (var p in from.PartList)
        {
            p.Room = into;
            into.PartList.Add(p);
        }
        _rooms.Remove(from);
    }

    private void RemoveCell(TilePos c, Room r)
    {
        if (_cellPart.TryGetValue(c, out var part)) RemovePart(part);
        r.CellSet.Remove(c);
        _cellRoom.Remove(c);
    }

    /// <summary>
    /// Keeps the invariant: a room is one connected piece. The largest piece keeps the room; the others become
    /// new rooms of the same type, taking the parts standing on them. Empty rooms are removed.
    /// </summary>
    private void SplitIfDisconnected(Room room)
    {
        if (room.CellSet.Count == 0)
        {
            _rooms.Remove(room);
            return;
        }
        var pieces = Components(room.CellSet);
        if (pieces.Count == 1) return;

        pieces.Sort((x, y) => y.Count != x.Count ? y.Count.CompareTo(x.Count) : Order(x[0]).CompareTo(Order(y[0])));
        for (int i = 1; i < pieces.Count; i++)
        {
            var piece = new HashSet<TilePos>(pieces[i]);
            var split = NewRoom(room.Type);
            foreach (var c in pieces[i])
            {
                room.CellSet.Remove(c);
                split.CellSet.Add(c);
                _cellRoom[c] = split;
            }
            foreach (var p in room.PartList.ToList())
            {
                int inside = p.Cells.Count(piece.Contains);
                if (inside == 0) continue;
                if (inside < p.Cells.Length)
                {
                    RemovePart(p); // cut in two: can't stand in either piece
                    continue;
                }
                room.PartList.Remove(p);
                split.PartList.Add(p);
                p.Room = split;
            }
        }
    }

    /// <summary>Connected pieces of a cell set under the movement rules, each sorted, in a deterministic order.</summary>
    private List<List<TilePos>> Components(HashSet<TilePos> cells)
    {
        var ordered = cells.OrderBy(Order).ToList();
        var seen = new HashSet<TilePos>();
        var result = new List<List<TilePos>>();
        var stack = new Stack<TilePos>();
        foreach (var start in ordered)
        {
            if (!seen.Add(start)) continue;
            var piece = new List<TilePos>();
            stack.Push(start);
            while (stack.TryPop(out var c))
            {
                piece.Add(c);
                Movement.GetMoves(_world, c, _moves);
                foreach (var (q, _) in _moves)
                    if (cells.Contains(q) && seen.Add(q)) stack.Push(q);
            }
            piece.Sort((x, y) => Order(x).CompareTo(Order(y)));
            result.Add(piece);
        }
        return result;
    }

    private static (int, int, int) Order(TilePos p) => (p.Z, p.Y, p.X);

    private void ForCells(TilePos a, TilePos b, Action<TilePos> action)
    {
        for (int z = Math.Min(a.Z, b.Z); z <= Math.Max(a.Z, b.Z) + 1; z++)
        for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
        for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
        {
            var c = new TilePos(x, y, z);
            if (_world.InBounds(c)) action(c);
        }
    }

    // ---- Parts -----------------------------------------------------------------------------------------

    /// <summary>Cells a part would cover with its origin cell at <paramref name="origin"/> (same level).</summary>
    public static TilePos[] Footprint(PartType type, TilePos origin, bool rotated, int size = 1)
    {
        var (w, d) = PartInfo.Footprint(type, size);
        if (rotated) (w, d) = (d, w);
        var cells = new TilePos[w * d];
        for (int j = 0; j < d; j++)
        for (int i = 0; i < w; i++)
            cells[j * w + i] = origin.Offset(i, j, 0);
        return cells;
    }

    /// <summary>Why the part can't go here, or null if it can.</summary>
    public string? CanPlace(PartType type, TilePos origin, bool rotated, int size = 1)
    {
        Room? room = null;
        foreach (var c in Footprint(type, origin, rotated, PartInfo.ClampSize(type, size)))
        {
            var r = RoomAt(c);
            if (r is null) return "must be placed inside a room";
            if (room is not null && r != room) return "must fit inside one room";
            room = r;
            if (_cellPart.ContainsKey(c)) return "space is occupied";
        }
        if (!RoomInfo.Allows(room!.Type, type)) return $"doesn't belong in a {RoomInfo.Name(room.Type)}";
        return null;
    }

    public Part? Place(PartType type, TilePos origin, bool rotated, int size = 1)
    {
        size = PartInfo.ClampSize(type, size);
        if (CanPlace(type, origin, rotated, size) is not null) return null;
        var cells = Footprint(type, origin, rotated, size);
        var room = RoomAt(cells[0])!;
        var part = new Part
        {
            Id = _nextPartId++, Type = type, Size = size, Origin = origin, Rotated = rotated, Cells = cells, Room = room,
            Built = InstantBuild,
        };
        room.PartList.Add(part);
        foreach (var c in cells) _cellPart[c] = part;
        Version++;
        return part;
    }

    public void RemovePart(Part part)
    {
        if (part.Removed) return;
        foreach (var c in part.Cells) _cellPart.Remove(c);
        part.Room.PartList.Remove(part);
        part.Removed = true;
        part.ClaimedBy = -1;
        part.Working = false;
        Version++;
    }

    /// <summary>A builder finished the blueprint (its materials are used up by the caller).</summary>
    internal void CompleteConstruction(Part part)
    {
        if (part.Removed || part.Built) return;
        part.Built = true;
        part.BuilderId = -1;
        Version++;
    }

    /// <summary>Units of the blueprint's material lying on its footprint (<paramref name="available"/>: unreserved only).</summary>
    public static int MaterialsOnSite(Part part, Items.ItemPiles items, bool available = false)
    {
        var material = part.Cost.Material;
        int n = 0;
        foreach (var c in part.Cells)
            if (items.TryGet(c, out var pile) && pile.Type == material) n += available ? pile.Available : pile.Count;
        return n;
    }

    /// <summary>Removes every part with a cell in the box. Returns how many were removed.</summary>
    public int RemovePartsIn(TilePos a, TilePos b)
    {
        var parts = new List<Part>();
        ForCells(a, b, c =>
        {
            if (_cellPart.TryGetValue(c, out var p) && !parts.Contains(p)) parts.Add(p);
        });
        foreach (var p in parts) RemovePart(p);
        return parts.Count;
    }

    // ---- Status ----------------------------------------------------------------------------------------

    /// <summary>What a room is doing or missing, most important first (for the builder's info panel).</summary>
    public static string Status(Room room, Items.ItemPiles items)
    {
        var recipe = room.Recipe;
        int machines = room.Count(PartRole.Machine);
        if (machines == 0) return Pending(room, PartRole.Machine) ? "machine under construction" : "needs a machine";
        if (room.Count(PartRole.Input) == 0) return Pending(room, PartRole.Input) ? "input crate under construction" : "needs an input crate";
        if (room.Count(PartRole.Output) == 0) return Pending(room, PartRole.Output) ? "output crate under construction" : "needs an output crate";
        int manned = room.PartsWithRole(PartRole.Machine).Count(m => m.ClaimedBy >= 0);
        if (manned > 0) return $"working ({manned}/{machines} machines manned)";
        // Machines take partial batches when nothing more is coming, so one unit is the least they can start on.
        if (room.PartsWithRole(PartRole.Input).All(c => !items.TryGet(c.Cells[0], out var p) || p.Type != recipe.Input || p.Available < recipe.InCount))
            return $"waiting for {Items.ItemInfo.Name(recipe.Input).ToLowerInvariant()}";
        int smallestOut = room.PartsWithRole(PartRole.Machine).Min(m => m.Batch.OutCount);
        if (room.PartsWithRole(PartRole.Output).All(c => items.RoomAt(c.Cells[0], recipe.Output) < smallestOut))
            return "output crates full";
        return "waiting for workers";
    }

    private static bool Pending(Room room, PartRole role) => room.Blueprints.Any(p => p.Role == role);

    /// <summary>Units of <paramref name="type"/> in the room's crates with the given role.</summary>
    public static int Stock(Room room, PartRole role, Items.ItemType type, Items.ItemPiles items)
    {
        int n = 0;
        foreach (var crate in room.PartsWithRole(role))
            if (items.TryGet(crate.Cells[0], out var pile) && pile.Type == type) n += pile.Count;
        return n;
    }

    // ---- Efficiency ------------------------------------------------------------------------------------

    /// <summary>
    /// 1 + for each auxiliary family (tools, light): the average bonus over the room's machine tiles. Each
    /// auxiliary covers <see cref="PartInfo.Coverage"/> machine tiles with its <see cref="PartInfo.Bonus"/>;
    /// the best ones are assigned first, so extra or lesser auxiliaries never hurt, and coverage beyond the
    /// machine area adds nothing. Auxiliaries scale with the factory instead of stacking.
    /// </summary>
    public static float Efficiency(Room room)
    {
        int machineTiles = 0;
        foreach (var p in room.PartList)
            if (p.Built && p.Role == PartRole.Machine) machineTiles += p.Cells.Length;
        if (machineTiles == 0) return 1f;

        var aux = new List<(PartType Type, float Bonus, int Coverage)>();
        foreach (var p in room.PartList)
            if (p.Built && p.Role == PartRole.Auxiliary) aux.Add((p.Type, PartInfo.Bonus(p.Type, p.Size), PartInfo.Coverage(p.Type, p.Size)));
        aux.Sort((a, b) => a.Type != b.Type ? a.Type.CompareTo(b.Type) : b.Bonus.CompareTo(a.Bonus));

        float e = 1f, sum = 0f;
        int remaining = 0;
        PartType? family = null;
        foreach (var (type, bonus, coverage) in aux)
        {
            if (type != family)
            {
                e += sum / machineTiles;
                (family, sum, remaining) = (type, 0f, machineTiles);
            }
            int covered = Math.Min(coverage, remaining);
            sum += bonus * covered;
            remaining -= covered;
        }
        return e + sum / machineTiles;
    }
}
