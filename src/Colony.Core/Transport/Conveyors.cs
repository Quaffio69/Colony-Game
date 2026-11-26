using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Navigation;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Transport;

/// <summary>What one end of a belt is joined to.</summary>
public enum PortKind : byte
{
    /// <summary>Open ground. As a start: a hopper haulers fill with loose goods. As an end: goods are dropped there.</summary>
    Ground,
    /// <summary>A workshop crate: an output crate to empty at the start, an input crate to fill at the end.</summary>
    Crate,
    /// <summary>A stockpile: haulers load the hopper from it at the start; goods are stored in its cells at the end.</summary>
    Stockpile,
    /// <summary>Another belt: a splitter on it at the start (a branch), or a merge into it at the end.</summary>
    Belt,
}

/// <summary>
/// One end of a belt. <see cref="Cell"/> is the thing joined: the crate's cell, the stockpile cell clicked, the other
/// belt's cell (<see cref="Index"/> along it), or for open ground the belt's own end cell. <see cref="Id"/> is the
/// stockpile or belt id (-1 otherwise).
/// </summary>
public readonly record struct Port(PortKind Kind, TilePos Cell, int Id = -1, int Index = -1);

/// <summary>
/// Goods riding one belt tile: <see cref="Count"/> of <see cref="Type"/> (0 = an empty tile) addressed to the belt
/// <see cref="Dest"/> whose end delivers them, into <see cref="DestCell"/> (room there was reserved when they were loaded,
/// like a hauler reserves it). <see cref="Serial"/> tells packets apart for the view.
/// </summary>
public record struct Packet(ItemType Type, int Count, int Dest, TilePos DestCell, int Serial)
{
    public readonly bool Empty => Count == 0;
}

/// <summary>What a belt is doing, for the hover card and Inspect.</summary>
public enum BeltStatus : byte { Planned, Running, Idle, NoPower, Blocked, NoSource, Backed }

/// <summary>
/// A conveyor belt drawn in one stroke: a path of open cells (each a flat run, a lift straight up or down, or a ramp up
/// or down a level) from <see cref="From"/> to <see cref="To"/>. Built on site, tile by tile: a plank a tile, wooden gears
/// for its first tile (the drive, or the splitter it branches from). Goods ride it a tile per step, one packet a tile.
/// </summary>
public sealed class Belt
{
    public required int Id { get; init; }
    public required int Faction { get; init; }
    public required TilePos[] Cells { get; init; }
    /// <summary>Where each tile's materials are brought and its builder works from (the ground under it).</summary>
    public required TilePos[] Sites { get; init; }
    public Port From { get; internal set; }
    public Port To { get; internal set; }
    internal bool[] BuiltCells = [];
    internal Packet[] Slots = [];
    private HashSet<ushort>? _carries;
    internal int Rotor;
    internal bool MergeTurn;

    public int Length => Cells.Length;
    public int Unbuilt { get; internal set; }
    public bool Complete => Unbuilt == 0;
    public bool IsBuilt(int i) => BuiltCells[i];
    /// <summary>Index of a tile filled in (rock, a wall, stairs), or -1: the belt stands still until it is cleared.</summary>
    public int Blocked { get; internal set; } = -1;
    public bool Running => Complete && Blocked < 0;
    /// <summary>Its end delivers (to a crate, stockpile or the ground) rather than feeding another belt.</summary>
    public bool IsTerminal => To.Kind != PortKind.Belt;
    /// <summary>It starts at a hopper (open ground, or by a stockpile) that haulers fill.</summary>
    public bool HasHopper => From.Kind is PortKind.Ground or PortKind.Stockpile;
    public TilePos Start => Cells[0];
    public TilePos End => Cells[^1];
    public Packet PacketAt(int i) => Slots[i];
    public int Packets { get; internal set; }
    /// <summary>Units this belt has delivered off its end, ever (stats).</summary>
    public long Delivered { get; internal set; }
    public BeltStatus Status { get; internal set; }
    /// <summary>Item and amount the hopper asks haulers for (refreshed by <see cref="Conveyors.Tick"/>), or none.</summary>
    public ItemType? Wants { get; internal set; }
    internal int WantCount;

    /// <summary>Items it carries (null: everything).</summary>
    public IReadOnlyCollection<ushort>? Carries => _carries;
    public bool CarriesItem(ItemType t) => _carries is null || _carries.Contains(t.Index);

    internal void SetFilter(HashSet<ushort>? carries) => _carries = carries is { Count: 0 } ? new HashSet<ushort>() : carries;

    /// <summary>Order-independent fingerprint of the filter (for <see cref="Net.StateHash"/>).</summary>
    public long FilterPrint
    {
        get
        {
            if (_carries is null) return -1;
            long h = 17;
            foreach (var t in _carries.OrderBy(x => x)) h = h * 1_000_003 + t;
            return h;
        }
    }
}

/// <summary>
/// Conveyor belts (<see cref="Belt"/>): a colony's goods moved without hands, from where they are made or stored to where
/// they are wanted. A belt runs from a source (an output crate it empties, a stockpile whose goods haulers load into its
/// hopper, or a hopper on open ground haulers fill with loose goods) to a sink (an input crate it fills with what the
/// workshop needs, a stockpile it stores into, or the ground it drops onto). A belt may also start at a splitter on another
/// belt (a branch) or end by merging into one, so belts make networks: goods are addressed when loaded, to a sink that
/// wants them and has room (reserved there, like a hauler does: input crates first, then stockpiles, then the ground, in
/// turn among equals), and every splitter sends each packet down the branch that leads to its sink. Demand-driven: a belt
/// to a crate loads only what the workshop still needs, so it never floods; haulers see what is on its way (the reserved
/// room) and bring only the rest.
/// <para>Belts don't block movement (people step over them; raised ones run overhead) or water, but take their cells from
/// other things: parts, racks, stockpiles, farms, other belts. A tile filled in (a wall, a cave-in) stops the belt until
/// cleared. They run on their colony's power (<see cref="Fluids.Waterworks"/>): a step every
/// <see cref="Balance.ConveyorStepTicks"/> ticks at full power, slower at the share covered, none without it (sandboxes
/// without needs, and dev mode, run free). Deterministic: belts in id order, each step downstream belts first.</para>
/// </summary>
public sealed class Conveyors
{
    private readonly VoxelWorld _world;
    private readonly List<Belt> _belts = new();
    private readonly Dictionary<TilePos, (Belt Belt, int Index)> _cellOf = new();
    private readonly HashSet<TilePos> _hoppers = new();
    private readonly HashSet<int> _dirty = new();
    private int _nextId = 1, _serial = 1;
    private int[] _phase = [];

    public Conveyors(VoxelWorld world)
    {
        _world = world;
        _world.ChunkChanged += i => { if (_belts.Count > 0) _dirty.Add(i); };
    }

    /// <summary>Bumps when belts are planned, built, removed or change their settings.</summary>
    public int Version { get; private set; }
    /// <summary>Bumps when belts change or a hopper starts asking for something else: idle haulers look again.</summary>
    public int WorkVersion => Version + _wantChanges;
    private int _wantChanges;
    /// <summary>Bumps every step that moved goods (for the view).</summary>
    public int Steps { get; private set; }
    public IReadOnlyList<Belt> All => _belts;
    public int Count => _belts.Count;
    public bool Contains(TilePos p) => _cellOf.ContainsKey(p);
    public (Belt Belt, int Index)? At(TilePos p) => _cellOf.TryGetValue(p, out var at) ? at : null;
    public Belt? ById(int id)
    {
        foreach (var b in _belts)
            if (b.Id == id) return b;
        return null;
    }
    /// <summary>The cell is the hopper at the start of a belt haulers fill.</summary>
    public bool IsHopper(TilePos p) => _hoppers.Contains(p);

    // ---- Geometry -------------------------------------------------------------------------------------------

    /// <summary>How one belt tile leads to the next.</summary>
    public enum Step : byte { None, Flat, Lift, RampUp, RampDown }

    /// <summary>
    /// The kind of step from <paramref name="a"/> to <paramref name="b"/> (shape only): a tile across (flat), straight up or
    /// down (a lift), or a tile across and a level up or down (a ramp, climbing in the lower column). None if not adjacent so.
    /// </summary>
    public static Step StepOf(TilePos a, TilePos b)
    {
        int dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        int h = Math.Abs(dx) + Math.Abs(dy);
        if (Math.Abs(dz) > 1 || h > 1 || h == 0 && dz == 0) return Step.None;
        if (h == 0) return Step.Lift;
        return dz == 0 ? Step.Flat : dz > 0 ? Step.RampUp : Step.RampDown;
    }

    /// <summary>
    /// Whether a belt can go from <paramref name="a"/> to <paramref name="b"/> here: adjacent as a step, and a ramp has the
    /// corner it climbs through open (up: the cell over the low end; down: the cell over the low end, beyond the edge).
    /// </summary>
    public static bool CanStep(ITileReader world, TilePos a, TilePos b)
    {
        var step = StepOf(a, b);
        if (step == Step.None) return false;
        if (step == Step.RampUp) return Open(world, a.Offset(0, 0, 1));
        if (step == Step.RampDown) return Open(world, b.Offset(0, 0, 1));
        return true;
    }

    private static bool Open(ITileReader world, TilePos p) => world.InBounds(p.X, p.Y, p.Z) && !TileInfo.IsSolid(world.Get(p.X, p.Y, p.Z));

    /// <summary>Why a belt can't run through this cell, or null if it can (checked when planning and placing).</summary>
    public static string? Free(ColonySim sim, TilePos c)
    {
        var w = sim.World;
        if (!w.InBounds(c) || c.Z <= 0) return "outside the world";
        if (w.IsUnseen(c.X, c.Y, c.Z)) return "unexplored";
        var tile = w.Get(c);
        if (TileInfo.IsSolid(tile)) return "solid rock";
        if (TileInfo.IsStair(tile)) return "stairs";
        if (sim.Conveyors.Contains(c)) return "another belt";
        if (sim.Stockpiles.Contains(c)) return "a stockpile";
        if (sim.Farms.Contains(c)) return "a farm plot";
        if (sim.Rooms.PartAt(c) is not null) return "a workshop part";
        if (sim.Constructions.At(c) is not null || sim.Waterworks.Occupied(c) || sim.Stockpiles.Racks.ContainsKey(c)) return "a structure";
        if (sim.Flora.TryGet(c, out var plant) && Nature.PlantInfo.IsTree(plant.Kind)) return "a tree";
        return null;
    }

    // ---- Ports ------------------------------------------------------------------------------------------------

    /// <summary>
    /// What a click on <paramref name="cell"/> joins a belt to (its start if <paramref name="source"/>, else its end): one of
    /// the colony's belts there (a splitter or a merge), a workshop crate (output to start from, input to end at), a
    /// stockpile, or the open ground. Null with a reason if nothing can be joined there.
    /// </summary>
    public static Port? PortAt(ColonySim sim, int faction, TilePos cell, bool source, out string? why)
    {
        why = null;
        if (sim.Conveyors.At(cell) is { } on)
        {
            if (on.Belt.Faction != faction) { why = "another colony's belt"; return null; }
            return new Port(PortKind.Belt, cell, on.Belt.Id, on.Index);
        }
        if (sim.Rooms.PartAt(cell) is { } part && part.Role is PartRole.Input or PartRole.Output)
        {
            if (part.Room.Faction != faction) { why = "another colony's workshop"; return null; }
            if (source && part.Role != PartRole.Output) { why = "a belt starts at an output crate (it fills input crates)"; return null; }
            if (!source && part.Role != PartRole.Input) { why = "a belt ends at an input crate (it empties output crates)"; return null; }
            return new Port(PortKind.Crate, cell);
        }
        if (sim.Stockpiles.Contains(cell) && !sim.Stockpiles.IsDepot(cell) && sim.Stockpiles.ZoneAt(cell) is { } zone)
        {
            if (zone.Faction != faction) { why = "another colony's stockpile"; return null; }
            return new Port(PortKind.Stockpile, cell, zone.Id);
        }
        if (Free(sim, cell) is { } bad) { why = bad; return null; }
        if (source && !Movement.IsStandable(sim.World, cell)) { why = "a hopper needs ground to stand on"; return null; }
        return new Port(PortKind.Ground, cell);
    }

    /// <summary>
    /// Cells a belt may begin in (<paramref name="source"/>) or finish in to join <paramref name="port"/>: beside the crate
    /// or the stockpile (by the cells round the one clicked), a step from the other belt's tile, or the ground cell itself.
    /// A hopper (start at ground or a stockpile) must be standable, so haulers can fill it.
    /// </summary>
    public static List<TilePos> EndCells(ColonySim sim, Port port, bool source)
    {
        var cells = new List<TilePos>();
        bool hopper = source && port.Kind is PortKind.Ground or PortKind.Stockpile;
        void Try(TilePos c)
        {
            if (cells.Contains(c) || Free(sim, c) is not null) return;
            if (hopper && !Movement.IsStandable(sim.World, c)) return;
            if (!Joins(sim, port, c, source)) return;
            cells.Add(c);
        }
        switch (port.Kind)
        {
            case PortKind.Ground:
                Try(port.Cell);
                break;
            case PortKind.Belt:
                for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    Try(port.Cell.Offset(dx, dy, dz));
                break;
            default:
                int r = port.Kind == PortKind.Stockpile ? 2 : 0;
                for (int oz = -r; oz <= r; oz++)
                for (int oy = -r; oy <= r; oy++)
                for (int ox = -r; ox <= r; ox++)
                {
                    var near = port.Cell.Offset(ox, oy, Math.Clamp(oz, -1, 1));
                    if (port.Kind == PortKind.Stockpile && sim.Stockpiles.ZoneAt(near)?.Id != port.Id) continue;
                    for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        Try(near.Offset(dx, dy, dz));
                }
                break;
        }
        // Beside a crate or a stockpile, ends level with it if any can (a belt hovering a level up looks wrong and costs trestles).
        if (port.Kind is PortKind.Crate or PortKind.Stockpile && cells.Any(c => c.Z == port.Cell.Z)) cells.RemoveAll(c => c.Z != port.Cell.Z);
        return cells;
    }

    /// <summary>Whether a belt tile at <paramref name="c"/> (its first if <paramref name="source"/>, else its last) joins the port.</summary>
    internal static bool Joins(ColonySim sim, Port port, TilePos c, bool source)
    {
        static bool Near(TilePos a, TilePos b) => a != b && Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1 && Math.Abs(a.Z - b.Z) <= 1;
        switch (port.Kind)
        {
            case PortKind.Ground: return c == port.Cell;
            case PortKind.Crate: return Near(c, port.Cell);
            case PortKind.Stockpile:
                for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if ((dx | dy | dz) != 0 && sim.Stockpiles.ZoneAt(c.Offset(dx, dy, dz))?.Id == port.Id) return true;
                return false;
            default:
                return source ? CanStep(sim.World, port.Cell, c) : CanStep(sim.World, c, port.Cell);
        }
    }

    /// <summary>The port of a kind at a cell as it stands now (a command names ports by kind and cell), or null.</summary>
    public static Port? Resolve(ColonySim sim, PortKind kind, TilePos cell) => kind switch
    {
        PortKind.Ground => new Port(PortKind.Ground, cell),
        PortKind.Crate => sim.Rooms.PartAt(cell) is { Role: PartRole.Input or PartRole.Output } ? new Port(PortKind.Crate, cell) : null,
        PortKind.Stockpile => sim.Stockpiles.ZoneAt(cell) is { } z ? new Port(PortKind.Stockpile, cell, z.Id) : null,
        PortKind.Belt => sim.Conveyors.At(cell) is { } at ? new Port(PortKind.Belt, cell, at.Belt.Id, at.Index) : null,
        _ => null,
    };

    /// <summary>"the output crate of Carpentry #2", "Stockpile 1", "belt 4", "the ground".</summary>
    public static string Describe(ColonySim sim, Port p, bool source) => p.Kind switch
    {
        PortKind.Crate => sim.Rooms.PartAt(p.Cell) is { } part ? $"{(source ? "from" : "into")} the {part.Name.ToLowerInvariant()} of {part.Room.Name}" : "a crate",
        PortKind.Stockpile => $"{(source ? "from" : "into")} {sim.Stockpiles.ZoneById(p.Id)?.Name ?? "a stockpile"}",
        PortKind.Belt => source ? $"split off belt {p.Id}" : $"onto belt {p.Id}",
        _ => source ? "from a hopper" : "onto the ground",
    };

    // Route letters: a step across in direction 0..3 (+x, +y, -x, -y) a level down, level or up, or a lift up or down.
    private const string Down = "abcd", Level = "ENWS", Up = "fghi";
    private static readonly (int X, int Y)[] Dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    /// <summary>A route as letters, one a step after the first cell (for commands: compact and compared by value).</summary>
    public static string Encode(IReadOnlyList<TilePos> cells)
    {
        var s = new System.Text.StringBuilder(cells.Count);
        for (int i = 1; i < cells.Count; i++)
        {
            var (a, b) = (cells[i - 1], cells[i]);
            int dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            if (dx == 0 && dy == 0) { s.Append(dz > 0 ? 'U' : 'D'); continue; }
            int d = Array.IndexOf(Dirs, (dx, dy));
            s.Append((dz < 0 ? Down : dz > 0 ? Up : Level)[Math.Max(0, d)]);
        }
        return s.ToString();
    }

    /// <summary>The cells of an encoded route, or null if it is garbled or too long.</summary>
    public static List<TilePos>? Decode(TilePos start, string route)
    {
        if (route.Length >= Balance.ConveyorMaxTiles) return null;
        var cells = new List<TilePos>(route.Length + 1) { start };
        var at = start;
        foreach (char ch in route)
        {
            if (ch == 'U') at = at.Offset(0, 0, 1);
            else if (ch == 'D') at = at.Offset(0, 0, -1);
            else
            {
                int dz = Level.IndexOf(ch) >= 0 ? 0 : Up.IndexOf(ch) >= 0 ? 1 : Down.IndexOf(ch) >= 0 ? -1 : 9;
                if (dz == 9) return null;
                int d = (dz == 0 ? Level : dz > 0 ? Up : Down).IndexOf(ch);
                at = at.Offset(Dirs[d].X, Dirs[d].Y, dz);
            }
            cells.Add(at);
        }
        return cells;
    }

    // ---- Planning -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Why a belt along <paramref name="cells"/> from <paramref name="from"/> to <paramref name="to"/> can't be planned for
    /// the colony, or null if it can. Checks every tile is free and steps to the next, the ends join their ports, and a
    /// merge never loops back upstream of itself.
    /// </summary>
    public static string? CanPlace(ColonySim sim, int faction, IReadOnlyList<TilePos> cells, Port from, Port to)
    {
        if (cells.Count == 0) return "no route";
        if (cells.Count > Balance.ConveyorMaxTiles) return $"longer than {Balance.ConveyorMaxTiles} tiles: draw it in pieces";
        var seen = new HashSet<TilePos>();
        for (int i = 0; i < cells.Count; i++)
        {
            if (!seen.Add(cells[i])) return "the route crosses itself";
            if (Free(sim, cells[i]) is { } why) return $"{why} in the way";
            if (i > 0 && !CanStep(sim.World, cells[i - 1], cells[i])) return "the route has a gap";
        }
        if (Check(from, true) is { } a) return a;
        if (Check(to, false) is { } b) return b;
        if (!Joins(sim, from, cells[0], true)) return "its start doesn't reach what it starts from";
        if (!Joins(sim, to, cells[^1], false)) return "its end doesn't reach what it ends at";
        if (from.Kind is PortKind.Ground or PortKind.Stockpile && !Movement.IsStandable(sim.World, cells[0])) return "a hopper needs ground to stand on";
        if (from.Kind is PortKind.Ground or PortKind.Stockpile && to.Kind == PortKind.Ground)
            return "goods dropped on open ground would be hauled straight back: end it at a crate, a stockpile or another belt";
        if (from.Kind == PortKind.Belt && to.Kind == PortKind.Belt && sim.Conveyors.Reaches(to.Id, to.Index, from.Id, from.Index))
            return "that would loop back into itself";
        return null;

        string? Check(Port p, bool source)
        {
            switch (p.Kind)
            {
                case PortKind.Belt:
                    var other = sim.Conveyors.ById(p.Id);
                    if (other is null || p.Index < 0 || p.Index >= other.Length || other.Cells[p.Index] != p.Cell) return "that belt is gone";
                    if (other.Faction != faction) return "another colony's belt";
                    if (source && sim.Conveyors.BranchAt(other, p.Index) is not null) return "there is a splitter there already";
                    if (!source && sim.Conveyors.MergeAt(other, p.Index) is not null) return "another belt joins there already";
                    return null;
                case PortKind.Crate:
                    var part = sim.Rooms.PartAt(p.Cell);
                    if (part is null || part.Room.Faction != faction) return "no crate there";
                    if (part.Role != (source ? PartRole.Output : PartRole.Input)) return source ? "a belt starts at an output crate" : "a belt ends at an input crate";
                    return null;
                case PortKind.Stockpile:
                    var zone = sim.Stockpiles.ZoneById(p.Id);
                    if (zone is null || zone.Faction != faction || sim.Stockpiles.ZoneAt(p.Cell) != zone) return "no stockpile there";
                    return null;
                default:
                    return null;
            }
        }
    }

    /// <summary>Plans a belt (after <see cref="CanPlace"/>): blueprints for every tile, built from the ground below.</summary>
    internal Belt Place(ColonySim sim, int faction, IReadOnlyList<TilePos> cells, Port from, Port to)
    {
        var path = cells.ToArray();
        if (to.Kind == PortKind.Ground) to = to with { Cell = path[^1] };
        if (@from.Kind == PortKind.Ground) @from = @from with { Cell = path[0] };
        var sites = new TilePos[path.Length];
        var first = SiteFor(sim, path[0], path[0], null);
        sites[0] = HasHopperKind(from) ? path[0] : first ?? path[0];
        TilePos? last = null;
        for (int i = 1; i < path.Length; i++)
        {
            var s = SiteFor(sim, path[i], path[0], sites[0]);
            if (s is { } found) last = found;
            sites[i] = s ?? last ?? sites[0];
        }
        // Tiles with no ground of their own under them (a span over a chasm) are built from the next tile's ground back.
        for (int i = path.Length - 2; i >= 1; i--)
            if (sites[i] == sites[0] && sites[i + 1] != sites[0]) sites[i] = sites[i + 1];
        var belt = new Belt
        {
            Id = _nextId++, Faction = faction, Cells = path, Sites = sites, From = from, To = to,
            BuiltCells = new bool[path.Length], Slots = new Packet[path.Length], Unbuilt = path.Length,
        };
        _belts.Add(belt);
        Index(belt);
        for (int i = 0; i < path.Length; i++)
            sim.Constructions.PlaceConveyor(path[i], sites[i], i == 0 ? Constructions.ConveyorDrive : Constructions.ConveyorTile, faction);
        Version++;
        return belt;
    }

    private static bool HasHopperKind(Port p) => p.Kind is PortKind.Ground or PortKind.Stockpile;

    /// <summary>
    /// Where a belt tile's materials go and its builder stands: the first floor straight below it (its own cell on the
    /// ground) that holds nothing of its own (not a stockpile, farm, workshop part or another belt's hopper), within 24
    /// levels. Null if there is none (the tile is built from its neighbours' ground).
    /// </summary>
    private static TilePos? SiteFor(ColonySim sim, TilePos cell, TilePos start, TilePos? startSite)
    {
        for (int z = cell.Z; z >= Math.Max(1, cell.Z - 24); z--)
        {
            var c = cell with { Z = z };
            if (TileInfo.IsSolid(sim.World.Get(c))) return null;
            if (!Movement.IsStandable(sim.World, c)) continue;
            if (sim.Stockpiles.Contains(c) || sim.Farms.Contains(c) || sim.Rooms.PartAt(c) is not null || sim.Conveyors.IsHopper(c)) return null;
            if (startSite is { } s && c == s && cell != start) return null; // the drive's gears have that cell to themselves
            return c;
        }
        return null;
    }

    private void Index(Belt b)
    {
        for (int i = 0; i < b.Length; i++) _cellOf[b.Cells[i]] = (b, i);
        if (b.HasHopper) _hoppers.Add(b.Cells[0]);
        _netStale = true;
    }

    /// <summary>A builder finished a tile.</summary>
    internal void TileBuilt(TilePos cell)
    {
        if (At(cell) is not { } at || at.Belt.BuiltCells[at.Index]) return;
        at.Belt.BuiltCells[at.Index] = true;
        at.Belt.Unbuilt--;
        if (at.Belt.Complete) Validate(at.Belt);
        _netStale = true;
        Version++;
    }

    // ---- Removal ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Takes a belt away: built tiles leave their materials at their sites, plans are cancelled (what was delivered stays,
    /// loose), goods riding it fall where they are (their reserved room let go), and belts joined to it carry on alone (a
    /// branch from it starts at a hopper, one merging into it drops at its end).
    /// </summary>
    public void Remove(ColonySim sim, Belt b)
    {
        if (!_belts.Contains(b)) return;
        for (int i = 0; i < b.Length; i++) Spill(sim, b, i); // (while it is still listed: its packets' reserved room is found by id)
        // Goods elsewhere on the network bound for its end: their room is let go; they end wherever their belt takes them.
        foreach (var other in _belts)
            for (int i = 0; i < other.Length; i++)
                if (other.Slots[i] is { Empty: false } q && q.Dest == b.Id)
                {
                    Release(sim, q);
                    other.Slots[i] = q with { Dest = 0 };
                }
        _belts.Remove(b);
        for (int i = 0; i < b.Length; i++)
        {
            _cellOf.Remove(b.Cells[i]);
            if (b.BuiltCells[i])
            {
                var (mat, n) = Constructions.Cost(StructureKind.Conveyor, i == 0 ? Constructions.ConveyorDrive : Constructions.ConveyorTile);
                sim.Items.Drop(b.Sites[i], mat, n);
            }
            else if (sim.Constructions.At(b.Cells[i]) is { Kind: StructureKind.Conveyor } bp) sim.Constructions.Remove(bp);
        }
        _hoppers.Remove(b.Cells[0]);
        foreach (var other in _belts)
        {
            if (other.From.Kind == PortKind.Belt && other.From.Id == b.Id) { other.From = new Port(PortKind.Ground, other.Cells[0]); if (other.HasHopper) _hoppers.Add(other.Cells[0]); }
            if (other.To.Kind == PortKind.Belt && other.To.Id == b.Id) other.To = new Port(PortKind.Ground, other.Cells[^1]);
        }
        _netStale = true;
        Version++;
    }

    /// <summary>Drops the packet on tile <paramref name="i"/> where it is, letting go of the room it had reserved.</summary>
    private void Spill(ColonySim sim, Belt b, int i)
    {
        var p = b.Slots[i];
        if (p.Empty) return;
        Release(sim, p);
        sim.Items.Drop(b.Cells[i], p.Type, p.Count);
        b.Slots[i] = default;
        b.Packets--;
    }

    private void Release(ColonySim sim, Packet p)
    {
        if (ById(p.Dest) is { } d && d.To.Kind is PortKind.Crate or PortKind.Stockpile) sim.Items.ReleaseIncoming(p.DestCell, p.Count);
    }

    /// <summary>Removes the colony's belts with a tile in the box (only those not finished, if <paramref name="onlyPlanned"/>). Returns how many.</summary>
    public int RemoveIn(ColonySim sim, TilePos a, TilePos b, int faction, bool onlyPlanned)
    {
        int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
        int z0 = Math.Min(a.Z, b.Z), z1 = Math.Max(a.Z, b.Z) + 1;
        var hits = _belts.Where(belt => (faction == Owners.Any || belt.Faction == faction) && (!onlyPlanned || !belt.Complete) &&
            belt.Cells.Any(c => c.X >= x0 && c.X <= x1 && c.Y >= y0 && c.Y <= y1 && c.Z >= z0 && c.Z <= z1)).ToList();
        foreach (var belt in hits) Remove(sim, belt);
        return hits.Count;
    }

    /// <summary>Sets what a belt carries: <paramref name="mode"/> 0 everything, 1 also this item, 2 not this item, 3 only it.</summary>
    public bool Configure(Belt b, ItemType? item, int mode)
    {
        HashSet<ushort>? set = b.Carries is null ? null : new HashSet<ushort>(b.Carries);
        switch (mode)
        {
            case 0: set = null; break;
            case 1 when item is { } t: if (set is not null) set.Add(t.Index); break;
            case 2 when item is { } t:
                set ??= new HashSet<ushort>(Enumerable.Range(0, ItemInfo.TypeCount).Select(i => (ushort)i));
                set.Remove(t.Index);
                break;
            case 3 when item is { } t: set = [t.Index]; break;
            default: return false;
        }
        b.SetFilter(set);
        Version++;
        return true;
    }

    // ---- The network ------------------------------------------------------------------------------------------

    private bool _netStale = true;
    private readonly List<Belt> _order = new();
    private readonly Dictionary<(int Belt, int Index), Belt> _branch = new(), _merge = new();
    private readonly Dictionary<(int Belt, int Index), int[]> _terminals = new();
    private readonly Dictionary<int, HashSet<int>> _leads = new();
    private float[] _demand = [];

    public Belt? BranchAt(Belt b, int index) { Network(); return _branch.GetValueOrDefault((b.Id, index)); }
    public Belt? MergeAt(Belt b, int index) { Network(); return _merge.GetValueOrDefault((b.Id, index)); }

    /// <summary>Power the colony's finished belts draw.</summary>
    public float PowerDemand(int faction)
    {
        Network();
        return faction >= 0 && faction < _demand.Length ? _demand[faction] : 0;
    }

    private void Network()
    {
        if (!_netStale) return;
        _branch.Clear();
        _merge.Clear();
        _terminals.Clear();
        _leads.Clear();
        _order.Clear();
        var byId = new Dictionary<int, Belt>();
        foreach (var b in _belts) byId[b.Id] = b;
        foreach (var b in _belts)
        {
            if (b.From.Kind == PortKind.Belt && byId.ContainsKey(b.From.Id)) _branch[(b.From.Id, b.From.Index)] = b;
            if (b.To.Kind == PortKind.Belt && byId.ContainsKey(b.To.Id)) _merge[(b.To.Id, b.To.Index)] = b;
        }
        // Downstream first: a belt after the belts it feeds (its branches and the belt it merges into).
        var state = new Dictionary<int, int>();
        void Visit(Belt b)
        {
            if (state.GetValueOrDefault(b.Id) != 0) return;
            state[b.Id] = 1;
            for (int i = 0; i < b.Length; i++)
                if (_branch.TryGetValue((b.Id, i), out var br)) Visit(br);
            if (b.To.Kind == PortKind.Belt && byId.TryGetValue(b.To.Id, out var into)) Visit(into);
            state[b.Id] = 2;
            _order.Add(b);
        }
        foreach (var b in _belts) Visit(b);
        int factions = _belts.Count == 0 ? 0 : _belts.Max(b => b.Faction) + 1;
        _demand = new float[factions];
        foreach (var b in _belts)
            if (b.Complete) _demand[b.Faction] += Balance.ConveyorDrivePower + Balance.ConveyorPowerPerTile * b.Length;
        _netStale = false;
    }

    /// <summary>Terminal belts (ids, ascending) goods on <paramref name="b"/> at tile <paramref name="index"/> can still reach.</summary>
    public IReadOnlyList<int> Terminals(Belt b, int index)
    {
        Network();
        if (_terminals.TryGetValue((b.Id, index), out var known)) return known;
        var found = new SortedSet<int>();
        var guard = new HashSet<(int, int)>();
        void Walk(Belt x, int from)
        {
            if (!guard.Add((x.Id, from))) return;
            for (int i = from; i < x.Length; i++)
                if (_branch.TryGetValue((x.Id, i), out var br)) Walk(br, 0);
            if (x.IsTerminal) found.Add(x.Id);
            else if (ById(x.To.Id) is { } into) Walk(into, x.To.Index);
        }
        Walk(b, index);
        var list = found.ToArray();
        _terminals[(b.Id, index)] = list;
        return list;
    }

    /// <summary>Whether goods entering belt <paramref name="belt"/> at <paramref name="index"/> can arrive at <paramref name="target"/> at or before <paramref name="targetIndex"/> (a loop).</summary>
    internal bool Reaches(int belt, int index, int target, int targetIndex)
    {
        Network();
        var guard = new HashSet<(int, int)>();
        bool Walk(int id, int from)
        {
            if (!guard.Add((id, from)) || ById(id) is not { } x) return false;
            if (id == target && from <= targetIndex) return true;
            for (int i = from; i < x.Length; i++)
                if (_branch.TryGetValue((id, i), out var br) && Walk(br.Id, 0)) return true;
            return x.To.Kind == PortKind.Belt && Walk(x.To.Id, x.To.Index);
        }
        return Walk(belt, index);
    }

    private bool Leads(Belt branch, int dest)
    {
        if (!_leads.TryGetValue(branch.Id, out var set)) _leads[branch.Id] = set = new HashSet<int>(Terminals(branch, 0));
        return set.Contains(dest);
    }

    // ---- Running ------------------------------------------------------------------------------------------------

    /// <summary>Once a tick: notices tiles filled in; every <see cref="Balance.ConveyorStepTicks"/>, moves and loads goods.</summary>
    internal void Tick(ColonySim sim)
    {
        if (_belts.Count == 0) return;
        if (_dirty.Count > 0)
        {
            foreach (var b in _belts)
                if (Touches(b)) Validate(b);
            _dirty.Clear();
        }
        if (sim.Tick % Balance.ConveyorStepTicks != 0) return;
        Network();
        bool free = !sim.NeedsEnabled || sim.DevMode;
        int factions = sim.Factions.Count;
        if (_phase.Length < factions) Array.Resize(ref _phase, factions);
        if (sim.Tick % (Balance.ConveyorStepTicks * 4) == 0)
            foreach (var b in _belts)
                if (b.HasHopper && b.Running) RefreshWant(sim, b);
        for (int f = 0; f < factions; f++)
        {
            int supply = free ? 1000 : (int)(sim.Waterworks.SupplyOf(f) * 1000);
            _phase[f] += supply;
            bool step = _phase[f] >= 1000;
            if (step) _phase[f] -= 1000;
            foreach (var b in _order)
            {
                if (b.Faction != f) continue;
                b.Status = !b.Complete ? BeltStatus.Planned : b.Blocked >= 0 ? BeltStatus.Blocked : supply == 0 ? BeltStatus.NoPower : b.Packets > 0 ? BeltStatus.Running : BeltStatus.Idle;
                if (step && b.Running) Advance(sim, b);
            }
            if (!step) continue;
            foreach (var b in _belts)
                if (b.Faction == f && b.Running && b.From.Kind != PortKind.Belt && b.Slots[0].Empty) Load(sim, b);
        }
        Steps++;
    }

    private bool Touches(Belt b)
    {
        foreach (var c in b.Cells)
            if (_dirty.Contains(_world.ChunkIndex(c.X >> Chunk.Shift, c.Y >> Chunk.Shift, c.Z >> Chunk.Shift))) return true;
        return false;
    }

    /// <summary>Finds the first tile filled in (solid, or stairs carved into it).</summary>
    private void Validate(Belt b)
    {
        b.Blocked = -1;
        for (int i = 0; i < b.Length; i++)
        {
            var t = _world.Get(b.Cells[i]);
            if (TileInfo.IsSolid(t) || TileInfo.IsStair(t)) { b.Blocked = i; return; }
        }
    }

    /// <summary>One step of a belt: from its end back, each packet moves on a tile (or down a branch, or off the end).</summary>
    private void Advance(ColonySim sim, Belt b)
    {
        int n = b.Length;
        for (int i = n - 1; i >= 0; i--)
        {
            ref var p = ref b.Slots[i];
            if (!p.Empty)
            {
                if (_branch.TryGetValue((b.Id, i), out var br) && Leads(br, p.Dest))
                {
                    if (br.Running && br.Slots[0].Empty) { br.Slots[0] = p; br.Packets++; p = default; b.Packets--; }
                }
                else if (i == n - 1)
                {
                    if (b.IsTerminal) { Deliver(sim, b, p); p = default; b.Packets--; }
                    // A belt merging into another waits to be let in (the belt it joins pulls, taking turns).
                }
                else if (b.Slots[i + 1].Empty) { b.Slots[i + 1] = p; p = default; }
            }
            if (!b.Slots[i].Empty || !_merge.TryGetValue((b.Id, i), out var m) || !m.Running || m.Slots[^1].Empty) continue;
            bool behind = i > 0 && !b.Slots[i - 1].Empty;
            if (behind && !b.MergeTurn) { b.MergeTurn = true; continue; } // the belt's own goods go first this time
            b.MergeTurn = false;
            b.Slots[i] = m.Slots[^1];
            m.Slots[^1] = default;
            m.Packets--;
            b.Packets++;
        }
    }

    /// <summary>Goods off the end: into the crate or stockpile cell reserved for them, or onto the ground.</summary>
    private void Deliver(ColonySim sim, Belt b, Packet p)
    {
        b.Delivered += p.Count;
        if (p.Dest == b.Id && b.To.Kind is PortKind.Crate or PortKind.Stockpile)
        {
            int added = sim.Items.Deliver(p.DestCell, p.Type, p.Count);
            if (added < p.Count) sim.Items.Drop(b.End, p.Type, p.Count - added);
            return;
        }
        if (p.Dest != b.Id) Release(sim, p); // the network changed under it: it ends up here instead
        sim.Items.Drop(b.End, p.Type, p.Count);
    }

    /// <summary>Units of an item one packet carries (<see cref="Balance.ConveyorPacketKg"/> over its weight).</summary>
    public static int PacketUnits(ItemType t) => Math.Clamp((int)(Balance.ConveyorPacketKg / t.Def.Weight + 0.001f), 1, ItemInfo.StackLimit(t));

    /// <summary>The pile the belt loads from: the output crate it starts at, or its hopper.</summary>
    private static TilePos? SourceCell(ColonySim sim, Belt b)
    {
        if (b.From.Kind == PortKind.Crate)
            return sim.Rooms.PartAt(b.From.Cell) is { Role: PartRole.Output, Built: true } crate && crate.Room.Faction == b.Faction ? b.From.Cell : null;
        return b.HasHopper ? b.Start : null;
    }

    /// <summary>Puts a packet on the belt's first tile: what its source holds that one of its sinks wants, addressed there.</summary>
    private void Load(ColonySim sim, Belt b)
    {
        if (SourceCell(sim, b) is not { } src) { b.Status = BeltStatus.NoSource; return; }
        if (!sim.Items.TryGet(src, out var pile) || pile.Available <= 0 || !b.CarriesItem(pile.Type)) return;
        var type = pile.Type;
        var sinks = Terminals(b, 0);
        if (sinks.Count == 0) return;
        for (int cls = 0; cls < 3; cls++)
        {
            int count = sinks.Count;
            for (int k = 0; k < count; k++)
            {
                var t = ById(sinks[(b.Rotor + k) % count]);
                if (t is null || ClassOf(t) != cls) continue;
                if (Accepts(sim, b, t, type) is not { } room) continue;
                int n = Math.Min(Math.Min(pile.Available, PacketUnits(type)), room.Space);
                if (n <= 0) continue;
                if (t.To.Kind != PortKind.Ground && !sim.Items.ReserveIncoming(room.Cell, type, n)) continue;
                int taken = sim.Items.Consume(src, n);
                if (taken < n && t.To.Kind != PortKind.Ground) sim.Items.ReleaseIncoming(room.Cell, n - taken);
                if (taken <= 0) return;
                b.Slots[0] = new Packet(type, taken, t.Id, room.Cell, _serial++);
                b.Packets++;
                b.Rotor++;
                b.Status = BeltStatus.Running;
                return;
            }
        }
        b.Status = BeltStatus.Backed;
    }

    private static int ClassOf(Belt t) => t.To.Kind switch { PortKind.Crate => 0, PortKind.Stockpile => 1, _ => 2 };

    /// <summary>Why a belt isn't loading what its source holds: per sink, whether it takes it (for Inspect and the demos).</summary>
    public string Explain(ColonySim sim, Belt b)
    {
        if (!b.Complete) return "not built yet";
        if (b.Blocked >= 0) return $"tile {b.Blocked} is filled in";
        if (b.From.Kind == PortKind.Belt) return "a branch: it carries what its splitter sends";
        if (SourceCell(sim, b) is not { } src) return "its source is gone";
        if (!sim.Items.TryGet(src, out var pile) || pile.Available <= 0) return "nothing at its source";
        if (!b.CarriesItem(pile.Type)) return $"it doesn't carry {pile.Type.Name.ToLowerInvariant()}";
        var parts = new List<string>();
        foreach (var id in Terminals(b, 0))
        {
            if (ById(id) is not { } t) continue;
            var room = Accepts(sim, b, t, pile.Type);
            parts.Add($"belt {t.Id} ({t.To.Kind}): {(room is { } r ? $"takes {Math.Min(r.Space, 999)} at {r.Cell}" : "takes none")}");
        }
        return parts.Count == 0 ? "it leads nowhere" : string.Join("; ", parts);
    }

    /// <summary>Whether the sink at the end of terminal belt <paramref name="t"/> takes <paramref name="type"/> loaded on
    /// <paramref name="from"/>: where it goes and how much room there is.</summary>
    private static (TilePos Cell, int Space)? Accepts(ColonySim sim, Belt from, Belt t, ItemType type)
    {
        if (!t.CarriesItem(type) || !t.Running) return null;
        switch (t.To.Kind)
        {
            case PortKind.Crate:
            {
                if (sim.Rooms.PartAt(t.To.Cell) is not { Role: PartRole.Input, Built: true } crate || crate.Room.Faction != t.Faction) return null;
                int space = CrateRoom(sim, crate.Room, t.To.Cell, type);
                return space > 0 ? (t.To.Cell, space) : null;
            }
            case PortKind.Stockpile:
            {
                if (sim.Stockpiles.ZoneById(t.To.Id) is not { } zone || zone.Faction != t.Faction || !zone.Accepts(type)) return null;
                // From a stockpile, goods only go to one at least as good (haulers would carry them straight back).
                if (from.From.Kind == PortKind.Stockpile && sim.Stockpiles.ZoneById(from.From.Id) is { } home && zone.Priority > home.Priority) return null;
                if (zone.Cap > 0)
                {
                    int held = 0;
                    foreach (var c in zone.Cells)
                        if (sim.Items.TryGet(c, out var have) && have.Type == type) held += have.Count + have.Incoming;
                    if (held >= zone.Cap) return null;
                }
                // The stockpile cells round the end, ring by ring out to UnloadReach (a chute spreads its load): in the nearest
                // ring with room, a part-filled stack of the item first, else an empty cell.
                var end = t.End;
                for (int r = 1; r <= UnloadReach; r++)
                {
                    (TilePos Cell, int Space)? empty = null;
                    for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        var c = end.Offset(dx, dy, dz);
                        if (sim.Stockpiles.ZoneAt(c) != zone || sim.Constructions.At(c) is not null) continue;
                        int room = sim.Items.RoomAt(c, type);
                        if (room <= 0) continue;
                        if (sim.Items.TryGet(c, out _)) return (c, room);
                        empty ??= (c, room);
                    }
                    if (empty is not null) return empty;
                }
                return null;
            }
            default:
                // Only a workshop's products are dropped on open ground (goods from a stockpile or a hopper would just be hauled back).
                return from.From.Kind == PortKind.Crate ? (t.End, int.MaxValue) : null;
        }
    }

    /// <summary>Room an input crate has for an item its workshop needs (towards two batches, as haulers fill it; an empty
    /// crate is kept for each needed input that has none yet).</summary>
    internal static int CrateRoom(ColonySim sim, Room room, TilePos crate, ItemType type)
    {
        var needs = room.InputNeeds();
        int wanted = 0;
        foreach (var (item, w) in needs)
            if (item == type) wanted = w;
        if (wanted <= 0) return 0;
        int have = 0, empty = 0;
        bool hasCrate = false;
        var crates = room.PartsWithRole(PartRole.Input).Select(c => c.Cells[0]).ToList();
        foreach (var c in crates)
        {
            if (!sim.Items.TryGet(c, out var p)) { empty++; continue; }
            if (p.Type != type) continue;
            have += p.Count + p.Incoming;
            hasCrate = true;
        }
        if (have >= wanted) return 0;
        if (sim.Items.TryGet(crate, out var here))
        {
            if (here.Type != type) return 0;
        }
        else if (hasCrate)
        {
            int crateless = needs.Count(n => !crates.Any(c => sim.Items.TryGet(c, out var p) && p.Type == n.Item));
            if (empty <= crateless) return 0;
        }
        return Math.Min(sim.Items.RoomAt(crate, type), wanted - have);
    }

    /// <summary>What a hopper asks haulers to bring: from its stockpile (or loose goods nearby), an item a sink wants.</summary>
    private void RefreshWant(ColonySim sim, Belt b)
    {
        var (was, any) = (b.Wants, b.WantCount > 0);
        FindWant(sim, b);
        if (b.Wants != was || b.WantCount > 0 != any) _wantChanges++;
    }

    private void FindWant(ColonySim sim, Belt b)
    {
        b.Wants = null;
        b.WantCount = 0;
        var h = b.Start;
        int room;
        if (sim.Items.TryGet(h, out var held))
        {
            room = sim.Items.RoomAt(h, held.Type);
            if (room > 0 && Wanted(held.Type)) (b.Wants, b.WantCount) = (held.Type, room);
            return;
        }
        if (b.From.Kind == PortKind.Stockpile)
        {
            if (sim.Stockpiles.ZoneById(b.From.Id) is not { } zone) return;
            int looked = 0;
            foreach (var c in zone.Cells)
            {
                if (++looked > 4000) break;
                if (sim.Items.TryGet(c, out var p) && p.Available > 0 && Wanted(p.Type)) { Set(p.Type); return; }
            }
            return;
        }
        for (int r = 0; r <= HopperReach; r++)
        for (int dz = -2; dz <= 2; dz++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            var c = h.Offset(dx, dy, dz);
            if (c == h || !sim.Items.TryGet(c, out var p) || p.Available <= 0) continue;
            if (Hauling.RoleOf(c, p.Type, sim.Stockpiles, sim.Rooms, sim.Constructions) != StorageRole.Loose || !Wanted(p.Type)) continue;
            Set(p.Type);
            return;
        }

        void Set(ItemType t) => (b.Wants, b.WantCount) = (t, sim.Items.RoomAt(h, t));
        bool Wanted(ItemType t)
        {
            if (!b.CarriesItem(t)) return false;
            foreach (var id in Terminals(b, 0))
                if (ById(id) is { } s && Accepts(sim, b, s, t) is not null) return true;
            return false;
        }
    }

    /// <summary>Tiles from a belt's end it stores goods into a stockpile (nearest first).</summary>
    public const int UnloadReach = 3;

    /// <summary>Tiles round a hopper on open ground haulers bring loose goods from.</summary>
    public const int HopperReach = 16;

    /// <summary>Whether haulers leave this output crate to the belt emptying it (while it isn't nearly full).</summary>
    public bool Drains(ColonySim sim, TilePos crate, ItemPile pile)
    {
        foreach (var b in _belts)
            if (b.From.Kind == PortKind.Crate && b.From.Cell == crate && b.Running && (sim.NeedsEnabled ? sim.Waterworks.SupplyOf(b.Faction) > 0 || sim.DevMode : true))
                return pile.Count * 1000L < (long)sim.Items.LimitAt(crate, pile.Type) * Balance.ConveyorCrateOverflow;
        return false;
    }

    /// <summary>Whether a running belt from the output crate at <paramref name="crate"/> reaches the input crate at
    /// <paramref name="input"/> (haulers then leave that run to the belt).</summary>
    public bool Serves(TilePos crate, TilePos input)
    {
        foreach (var b in _belts)
        {
            if (b.From.Kind != PortKind.Crate || b.From.Cell != crate || !b.Running) continue;
            foreach (var id in Terminals(b, 0))
                if (ById(id) is { Running: true, To: { Kind: PortKind.Crate } to } && to.Cell == input) return true;
        }
        return false;
    }

    /// <summary>Hoppers waiting for goods: (hopper cell, item, how many, the stockpile it loads from or -1 for loose goods).</summary>
    public IEnumerable<(Belt Belt, TilePos Cell, ItemType Type, int Space, int Stockpile)> HopperDemands(int faction)
    {
        foreach (var b in _belts)
            if (b.HasHopper && b.Running && (faction == Owners.Any || b.Faction == faction) && b.Wants is { } t && b.WantCount > 0)
                yield return (b, b.Start, t, b.WantCount, b.From.Kind == PortKind.Stockpile ? b.From.Id : -1);
    }

    /// <summary>Units of each item riding the colony's belts.</summary>
    public int UnitsOn(int faction, ItemType type)
    {
        int n = 0;
        foreach (var b in _belts)
            if ((faction == Owners.Any || b.Faction == faction) && b.Packets > 0)
                foreach (var p in b.Slots)
                    if (!p.Empty && p.Type == type) n += p.Count;
        return n;
    }

    // ---- Snapshot -----------------------------------------------------------------------------------------------

    /// <summary>Brings derived state up to date (as a loaded copy computes it).</summary>
    internal void Settle()
    {
        foreach (var b in _belts) Validate(b);
        _dirty.Clear();
        _netStale = true;
    }

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(_nextId); w.Write(_serial); w.Write(Version); w.Write(_wantChanges);
        w.Write(_phase.Length);
        foreach (int p in _phase) w.Write(p);
        w.Write(_belts.Count);
        foreach (var b in _belts)
        {
            w.Write(b.Id); w.Write(b.Faction); w.Write(b.Length);
            for (int i = 0; i < b.Length; i++)
            {
                Net.SnapIO.Write(w, b.Cells[i]); Net.SnapIO.Write(w, b.Sites[i]); w.Write(b.BuiltCells[i]);
                var p = b.Slots[i];
                w.Write(p.Count);
                if (p.Empty) continue;
                w.Write(p.Type.Index); w.Write(p.Dest); Net.SnapIO.Write(w, p.DestCell); w.Write(p.Serial);
            }
            WritePort(w, b.From); WritePort(w, b.To);
            w.Write(b.Rotor); w.Write(b.MergeTurn); w.Write(b.Delivered);
            w.Write(b.Wants?.Index ?? -1); w.Write(b.WantCount); w.Write((byte)b.Status);
            if (b.Carries is null) w.Write(-1);
            else
            {
                var list = b.Carries.OrderBy(x => x).ToList();
                w.Write(list.Count);
                foreach (var t in list) w.Write(t);
            }
        }
    }

    private static void WritePort(BinaryWriter w, Port p)
    {
        w.Write((byte)p.Kind); Net.SnapIO.Write(w, p.Cell); w.Write(p.Id); w.Write(p.Index);
    }

    private static Port ReadPort(BinaryReader r) => new((PortKind)r.ReadByte(), Net.SnapIO.ReadPos(r), r.ReadInt32(), r.ReadInt32());

    internal void SnapLoad(BinaryReader r)
    {
        _nextId = r.ReadInt32(); _serial = r.ReadInt32(); Version = r.ReadInt32(); _wantChanges = r.ReadInt32();
        _phase = new int[r.ReadInt32()];
        for (int i = 0; i < _phase.Length; i++) _phase[i] = r.ReadInt32();
        int n = r.ReadInt32();
        for (int k = 0; k < n; k++)
        {
            int id = r.ReadInt32(), faction = r.ReadInt32(), len = r.ReadInt32();
            var cells = new TilePos[len];
            var sites = new TilePos[len];
            var built = new bool[len];
            var slots = new Packet[len];
            int packets = 0, unbuilt = 0;
            for (int i = 0; i < len; i++)
            {
                cells[i] = Net.SnapIO.ReadPos(r); sites[i] = Net.SnapIO.ReadPos(r); built[i] = r.ReadBoolean();
                if (!built[i]) unbuilt++;
                int count = r.ReadInt32();
                if (count == 0) continue;
                slots[i] = new Packet(new ItemType(r.ReadUInt16()), count, r.ReadInt32(), Net.SnapIO.ReadPos(r), r.ReadInt32());
                packets++;
            }
            var b = new Belt
            {
                Id = id, Faction = faction, Cells = cells, Sites = sites, From = ReadPort(r), To = ReadPort(r),
                BuiltCells = built, Slots = slots, Unbuilt = unbuilt, Packets = packets,
            };
            b.Rotor = r.ReadInt32(); b.MergeTurn = r.ReadBoolean(); b.Delivered = r.ReadInt64();
            int want = r.ReadInt32();
            b.Wants = want < 0 ? null : new ItemType((ushort)want);
            b.WantCount = r.ReadInt32();
            b.Status = (BeltStatus)r.ReadByte();
            int filter = r.ReadInt32();
            if (filter >= 0)
            {
                var set = new HashSet<ushort>();
                for (int i = 0; i < filter; i++) set.Add(r.ReadUInt16());
                b.SetFilter(set);
            }
            _belts.Add(b);
            Index(b);
        }
        Settle();
    }
}
