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
    /// <summary>What this machine makes (one of its station's recipes; null for other parts).</summary>
    public Recipe? Recipe { get; internal set; }
    /// <summary>Stock limit: the machine starts no batch while the colony holds this many of its product (0: no limit).
    /// Without one, stations ran forever (a playtest ended with 42 tableware nobody needed, and a writing desk ate the
    /// paper the lectern wanted).</summary>
    public int Limit { get; internal set; }
    /// <summary>One full batch at this machine: its recipe scaled by the machine's size.</summary>
    public Batch Batch => PartInfo.MachineBatchOf(Type, Size, Recipe ?? throw new InvalidOperationException($"{Name} is not a machine"));

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
    /// <summary>The colony that owns it: only its colonists work, live or store goods here.</summary>
    public int Faction { get; init; }
    public required RoomType Type { get; init; }
    public IReadOnlyCollection<TilePos> Cells => CellSet;
    public IReadOnlyList<Part> Parts => PartList;
    public string Name => $"{RoomInfo.Name(Type)} #{Id}";

    /// <summary>
    /// What the room's input crates should hold: for each input of its built machines' recipes (in machine
    /// then ingredient order), two full batches' worth, so the next batch is ready while one is worked.
    /// </summary>
    public List<(Items.ItemType Item, int Wanted)> InputNeeds()
    {
        var needs = new List<(Items.ItemType, int)>();
        foreach (var m in PartList)
        {
            if (!m.Built || m.Role != PartRole.Machine || m.Recipe is null) continue;
            var batch = m.Batch;
            for (int i = 0; i < m.Recipe.Inputs.Count; i++)
            {
                var item = m.Recipe.Inputs[i].Item;
                int want = 2 * batch.InCount(i), at = needs.FindIndex(n => n.Item1 == item);
                if (at < 0) needs.Add((item, want));
                else needs[at] = (item, needs[at].Item2 + want);
            }
        }
        return needs;
    }

    /// <summary>True if some built machine's recipe takes <paramref name="item"/>.</summary>
    public bool Needs(Items.ItemType item)
    {
        foreach (var m in PartList)
            if (m.Built && m.Recipe is { } r)
                foreach (var i in r.Inputs)
                    if (i.Item == item) return true;
        return false;
    }

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
    /// <summary>Cells no part may stand on (a conveyor belt runs there; set by <see cref="ColonySim"/>).</summary>
    public Func<TilePos, bool>? IsPartBlocked { get; set; }

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
    public int Paint(RoomType type, TilePos a, TilePos b, int faction = 0)
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
                if (_cellRoom.TryGetValue(q, out var r) && r.Type == type && r.Faction == faction && !touched.Contains(r)) touched.Add(r);
        }
        touched.Sort((x, y) => x.Id.CompareTo(y.Id));
        var target = touched.Count > 0 ? touched[0] : NewRoom(type, faction);
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
    public int Erase(TilePos a, TilePos b, int faction = Owners.Any)
    {
        var affected = new List<Room>();
        int n = 0;
        ForCells(a, b, c =>
        {
            if (!_cellRoom.TryGetValue(c, out var r) || (faction != Owners.Any && r.Faction != faction)) return;
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

    private Room NewRoom(RoomType type, int faction)
    {
        var r = new Room { Id = _nextRoomId++, Type = type, Faction = faction };
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
            var split = NewRoom(room.Type, room.Faction);
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
            if (r is null) return "must be placed inside a workshop";
            if (room is not null && r != room) return "must fit inside one room";
            room = r;
            if (_cellPart.ContainsKey(c)) return "space is occupied";
            if (IsPartBlocked?.Invoke(c) == true) return "a conveyor runs here";
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
            Recipe = type.Role == PartRole.Machine ? type.Recipes[0] : null,
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

    /// <summary>Sets what a machine makes (one of its station's recipes). Work in progress finishes as planned.</summary>
    public bool SetRecipe(Part machine, Recipe recipe)
    {
        if (machine.Removed || machine.Role != PartRole.Machine || recipe.Station != machine.Type || machine.Recipe == recipe) return false;
        machine.Recipe = recipe;
        Version++;
        return true;
    }

    /// <summary>Steps a machine to its station's next (or previous) recipe.</summary>
    public bool CycleRecipe(Part machine, bool back = false)
    {
        if (machine.Recipe is not { } r) return false;
        var list = machine.Type.Recipes;
        int i = list.IndexOf(r);
        return SetRecipe(machine, list[(i + (back ? list.Count - 1 : 1)) % list.Count]);
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
    /// <summary>Built parts with a cell in the box (the room cells of its levels), each once, in cell order.</summary>
    public List<Part> BuiltPartsIn(TilePos a, TilePos b, int faction = Owners.Any)
    {
        var parts = new List<Part>();
        ForCells(a, b, c =>
        {
            if (_cellPart.TryGetValue(c, out var p) && p.Built && (faction == Owners.Any || p.Room.Faction == faction) && !parts.Contains(p)) parts.Add(p);
        });
        return parts;
    }

    public int RemovePartsIn(TilePos a, TilePos b, int faction = Owners.Any, bool onlyPlanned = false)
    {
        var parts = new List<Part>();
        ForCells(a, b, c =>
        {
            if (_cellPart.TryGetValue(c, out var p) && !(onlyPlanned && p.Built) && (faction == Owners.Any || p.Room.Faction == faction) && !parts.Contains(p)) parts.Add(p);
        });
        foreach (var p in parts) RemovePart(p);
        return parts.Count;
    }

    // ---- Status ----------------------------------------------------------------------------------------

    /// <summary>What a room is doing or missing, most important first (for the builder's info panel).</summary>
    public static string Status(Room room, ColonySim sim)
    {
        var items = sim.Items;
        var machines = room.PartsWithRole(PartRole.Machine).ToList();
        if (machines.Count == 0) return Pending(room, PartRole.Machine) ? "machine under construction" : "needs a machine";
        int crates = room.Count(PartRole.Input);
        if (crates == 0) return Pending(room, PartRole.Input) ? "input crate under construction" : "needs an input crate";
        bool makesGoods = machines.Any(m => m.Recipe!.Kind == Items.ItemKind.Good);
        if (makesGoods && room.Count(PartRole.Output) == 0)
            return Pending(room, PartRole.Output) ? "output crate under construction" : "needs an output crate";
        int distinct = room.InputNeeds().Count;
        if (crates < distinct) return $"needs {distinct} input crates (one per input), has {crates}";
        // A crate holds one kind of item: machines making different products each need an output crate, or the
        // busiest one keeps the only crate full of its product and the others never start (found by a playtest).
        var products = machines.Where(m => m.Recipe!.Kind == Items.ItemKind.Good).Select(m => m.Recipe!.Output).Distinct().ToList();
        int outCrates = room.Count(PartRole.Output);
        if (outCrates < products.Count)
            return $"needs {products.Count} output crates (one per product: {string.Join(", ", products.Select(p => p.Name.ToLowerInvariant()))}), has {outCrates}";
        int manned = machines.Count(m => m.ClaimedBy >= 0);
        if (manned > 0) return $"working ({manned}/{machines.Count} machines manned)";
        if (machines.All(m => AtLimit(sim, m)))
            return $"stocked up: {string.Join(", ", machines.Select(m => $"{m.Recipe!.Name.ToLowerInvariant()} {StockOf(sim, m.Recipe.Output, room.Faction)}/{m.Limit}"))}";

        // Why nobody is working: the first idle machine's first problem.
        foreach (var m in machines)
        {
            var r = m.Recipe!;
            if (!sim.DevMode && !sim.Colonists.Any(c => c.Faction == room.Faction && c.Education >= r.Education))
                return $"{r.Name.ToLowerInvariant()} needs a {Catalog.EducationName(r.Education)} worker";
            if (r.Kind == Items.ItemKind.Education && sim.StudentsAvailable(r, room.Faction) == 0)
                return $"no colonists ready to become {r.Name}";
        }
        // Machines take partial batches when nothing more is coming, so one unit is the least they can start on.
        var missing = new List<string>();
        foreach (var m in machines)
            foreach (var ing in m.Recipe!.Inputs)
                if (Stock(room, PartRole.Input, ing.Item, items, available: true) < ing.Count && !missing.Contains(ing.Item.Name.ToLowerInvariant()))
                    missing.Add(ing.Item.Name.ToLowerInvariant());
        if (missing.Count > 0 && machines.All(m => m.Recipe!.Inputs.Any(i => Stock(room, PartRole.Input, i.Item, items, available: true) < i.Count)))
            return $"waiting for {string.Join(", ", missing)}";
        var goods = machines.Where(m => m.Recipe!.Kind == Items.ItemKind.Good).ToList();
        if (goods.Count > 0 && goods.All(m => room.PartsWithRole(PartRole.Output).All(c => items.RoomAt(c.Cells[0], m.Recipe!.Output) < m.Recipe!.OutCount)))
            return "output crates full";
        return "waiting for workers";
    }

    private static bool Pending(Room room, PartRole role) => room.Blueprints.Any(p => p.Role == role);

    /// <summary>Units of an item a colony holds anywhere it may use them (all of them when there is one colony).</summary>
    public static int StockOf(ColonySim sim, Items.ItemType type, int faction)
    {
        if (sim.Factions.Count == 1) return sim.Items.UnitsOf(type);
        int n = 0;
        foreach (var (p, pile) in sim.Items.All)
            if (pile.Type == type && Agents.Needs.Owned(sim, p, faction)) n += pile.Count;
        return n;
    }

    /// <summary>Whether a machine has met its stock limit (it starts no new batch until stock drops below it).</summary>
    public static bool AtLimit(ColonySim sim, Part machine) =>
        machine.Limit > 0 && machine.Recipe is { Kind: Items.ItemKind.Good } r && StockOf(sim, r.Output, machine.Room.Faction) >= machine.Limit;

    /// <summary>Sets a machine's stock limit (0: none).</summary>
    public void SetLimit(Part machine, int limit)
    {
        machine.Limit = Math.Max(0, limit);
        Version++;
    }

    /// <summary>
    /// The output crate a batch of <paramref name="product"/> goes to: one already holding it with room, else an empty
    /// one, but only while enough empty crates stay free for the room's other products that hold none yet (as input
    /// crates do). Without that, the busiest machine's product took every crate that came free and the others never
    /// ran (found by a playtest: planks in the crates meant for furniture).
    /// </summary>
    public static TilePos? OutputCrateFor(Room room, Items.ItemType product, int count, Items.ItemPiles items, Func<TilePos, bool> usable)
    {
        var crates = room.PartsWithRole(PartRole.Output).Select(c => c.Cells[0]).Where(usable).ToList();
        foreach (var c in crates)
            if (items.TryGet(c, out var pile) && pile.Type == product && items.RoomAt(c, product) >= count) return c;
        var empty = crates.Where(c => !items.TryGet(c, out _)).ToList();
        if (empty.Count == 0) return null;
        int othersWithout = room.PartsWithRole(PartRole.Machine)
            .Where(m => m.Recipe is { Kind: Items.ItemKind.Good } r && r.Output != product).Select(m => m.Recipe!.Output).Distinct()
            .Count(o => !room.PartsWithRole(PartRole.Output).Any(c => items.TryGet(c.Cells[0], out var p) && p.Type == o));
        return empty.Count > othersWithout && items.RoomAt(empty[0], product) >= count ? empty[0] : null;
    }

    /// <summary>Units of <paramref name="type"/> in the room's crates with the given role.</summary>
    public static int Stock(Room room, PartRole role, Items.ItemType type, Items.ItemPiles items, bool available = false)
    {
        int n = 0;
        foreach (var crate in room.PartsWithRole(role))
            if (items.TryGet(crate.Cells[0], out var pile) && pile.Type == type) n += available ? pile.Available : pile.Count;
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
        aux.Sort((a, b) => a.Type != b.Type ? a.Type.Index.CompareTo(b.Type.Index) : b.Bonus.CompareTo(a.Bonus));

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

    // ---- Snapshot -----------------------------------------------------------------------------------------

    /// <summary>Rebuilds the cell maps from the rooms (in room, then cell order), as a loaded copy builds them.</summary>
    internal void Settle()
    {
        foreach (var room in _rooms) Net.SnapIO.Compact(room.CellSet);
        Reindex();
    }

    private void Reindex()
    {
        _cellRoom.Clear();
        _cellPart.Clear();
        foreach (var room in _rooms)
        {
            foreach (var c in room.CellSet) _cellRoom[c] = room;
            foreach (var part in room.PartList)
                foreach (var c in part.Cells) _cellPart[c] = part;
        }
    }

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(_rooms.Count);
        foreach (var room in _rooms)
        {
            w.Write(room.Id);
            w.Write(room.Faction);
            w.Write(room.Type.Id);
            w.Write(room.CellSet.Count);
            foreach (var c in room.CellSet) Net.SnapIO.Write(w, c);
            w.Write(room.PartList.Count);
            foreach (var p in room.PartList)
            {
                w.Write(p.Id); w.Write(p.Type.Id); w.Write(p.Size); Net.SnapIO.Write(w, p.Origin); w.Write(p.Rotated);
                w.Write(p.Cells.Length);
                foreach (var c in p.Cells) Net.SnapIO.Write(w, c);
                w.Write(p.Recipe is { } recipe ? p.Type.Recipes.IndexOf(recipe) : -1);
                w.Write(p.ClaimedBy); w.Write(p.Working); w.Write(p.Removed); w.Write(p.Built); w.Write(p.BuilderId);
                w.Write(p.Limit);
            }
        }
        w.Write(_nextRoomId); w.Write(_nextPartId); w.Write(Version); w.Write(InstantBuild);
    }

    internal void SnapLoad(BinaryReader r)
    {
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            int id = r.ReadInt32(), faction = r.ReadInt32();
            string typeId = r.ReadString();
            var room = new Room { Id = id, Faction = faction, Type = typeId == Catalog.Home.Id ? Catalog.Home : Catalog.Workshop(typeId) };
            int cells = r.ReadInt32();
            for (int k = 0; k < cells; k++) room.CellSet.Add(Net.SnapIO.ReadPos(r));
            int parts = r.ReadInt32();
            for (int k = 0; k < parts; k++)
            {
                int pid = r.ReadInt32();
                var type = Catalog.Part(r.ReadString());
                int size = r.ReadInt32();
                var origin = Net.SnapIO.ReadPos(r);
                bool rotated = r.ReadBoolean();
                var pc = new TilePos[r.ReadInt32()];
                for (int c = 0; c < pc.Length; c++) pc[c] = Net.SnapIO.ReadPos(r);
                var part = new Part { Id = pid, Type = type, Size = size, Origin = origin, Rotated = rotated, Cells = pc, Room = room };
                int recipe = r.ReadInt32();
                part.Recipe = recipe >= 0 ? type.Recipes[recipe] : null;
                part.ClaimedBy = r.ReadInt32(); part.Working = r.ReadBoolean(); part.Removed = r.ReadBoolean();
                part.Built = r.ReadBoolean(); part.BuilderId = r.ReadInt32();
                if (!Net.SnapIO.Before(5)) part.Limit = r.ReadInt32();
                room.PartList.Add(part);
            }
            _rooms.Add(room);
        }
        _nextRoomId = r.ReadInt32(); _nextPartId = r.ReadInt32(); Version = r.ReadInt32(); InstantBuild = r.ReadBoolean();
        Reindex();
    }
}
