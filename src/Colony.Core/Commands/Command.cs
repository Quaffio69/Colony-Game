using Colony.Core.Agents;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Nature;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Commands;

/// <summary>
/// A player action on the simulation (dig here, paint a room, set a priority ...). Every change a player makes goes
/// through one, so a game is its starting world plus a log of (tick, command): replayable, and the basis of lockstep
/// multiplayer (every peer applies the same commands before the same tick). Commands name things by stable ids and
/// cells, never by object references. <see cref="Apply"/> returns a short message for the issuing player.
/// <see cref="Player"/> is who issued it (0 for now; factions build on it).
/// </summary>
public abstract record Command
{
    public int Player { get; init; }

    /// <summary>Carries the command out on the sim; the message says what happened (or why not).</summary>
    public abstract string Apply(ColonySim sim);

    // Shared helpers.
    protected static string Lower(string s) => s.ToLowerInvariant();
    protected static string Mat(ItemType t) => t.Name.ToLowerInvariant();
    /// <summary>The issuing colony's homes and housing policies.</summary>
    protected Housing Homes(ColonySim sim) => sim.Factions[Math.Clamp(Player, 0, sim.Factions.Count - 1)].Housing;
    /// <summary>Taken-down built parts leave their materials where they stood (blueprints leave what was delivered).</summary>
    protected static void Refund(ColonySim sim, List<Part> built)
    {
        foreach (var p in built) sim.Items.Drop(p.Cells[0], p.Cost.Material, p.Cost.Count);
    }
}

// ---- Orders and zones ------------------------------------------------------------------------------------

/// <summary>Dig or carve (one kind) every solid tile in a box.</summary>
public sealed record DesignateBox(TilePos A, TilePos B, DesignationKind Kind) : Command
{
    public override string Apply(ColonySim sim) =>
        $"{Kind}: designated {sim.Designations.DesignateBox(A, B, Kind, Player)} tiles{sim.Designations.LimitNote}";
}

/// <summary>Stairs through a box of levels: down on top, up/down between, up at the bottom.</summary>
public sealed record DesignateStairs(TilePos A, TilePos B) : Command
{
    public override string Apply(ColonySim sim) => $"Stairs: designated {sim.Designations.DesignateStairs(A, B, Player)} tiles{sim.Designations.LimitNote}";
}

/// <summary>Chop or gather every suitable plant in a 2D area, on the surface whatever the level.</summary>
public sealed record DesignatePlants(DesignationKind Kind, int X0, int Y0, int X1, int Y1) : Command
{
    public override string Apply(ColonySim sim)
    {
        sim.Designations.LimitReached = false;
        int n = sim.Designations.DesignatePlantArea(Kind, X0, Y0, X1, Y1, Player);
        return Kind == DesignationKind.Chop ? $"Chop trees: marked {n} trees{sim.Designations.LimitNote}" : $"Gather plants: marked {n} ripe plants{sim.Designations.LimitNote}";
    }
}

public sealed record CancelPlants(DesignationKind Kind, int X0, int Y0, int X1, int Y1) : Command
{
    public override string Apply(ColonySim sim) =>
        $"Cancelled {sim.Designations.CancelPlantArea(Kind, X0, Y0, X1, Y1, Player)} {(Kind == DesignationKind.Chop ? "chop" : "gather")} orders";
}

/// <summary>The general removal box: designations in it (and on the cells above its ground), stockpile and farm cells.</summary>
public sealed record CancelOrders(TilePos A, TilePos B) : Command
{
    public override string Apply(ColonySim sim)
    {
        int orders = sim.Designations.CancelBox(A with { Z = Math.Min(A.Z, B.Z) }, B with { Z = Math.Max(A.Z, B.Z) + 1 }, Player);
        int zones = sim.Stockpiles.RemoveBox(A, B, Player);
        int plots = sim.Farms.RemoveBox(A, B, Player);
        // Every plan in the box too: walls, doors, windows, floors, roofs, racks, waterworks and room parts not yet built.
        int plans = sim.Constructions.CancelBox(A, B, Player) + sim.Rooms.RemovePartsIn(A, B, Player, onlyPlanned: true);
        return $"Cancelled {orders} designations and {plans} plans, removed {zones} stockpile and {plots} farm cells";
    }
}

/// <summary>
/// Takes down everything built in a box (on the cells above its ground), at once, leaving its materials: thin walls,
/// doors and windows on any edge touching the box, floors and roofs, storage racks, water wheels and pumps, and room
/// parts. Plans there are cancelled too. (Solid dam walls are tiles: dig them out.)
/// </summary>
public sealed record Demolish(TilePos A, TilePos B) : Command
{
    public override string Apply(ColonySim sim)
    {
        int x0 = Math.Min(A.X, B.X), x1 = Math.Max(A.X, B.X), y0 = Math.Min(A.Y, B.Y), y1 = Math.Max(A.Y, B.Y);
        int z0 = Math.Min(A.Z, B.Z) + 1, z1 = Math.Max(A.Z, B.Z) + 1;
        int plans = sim.Constructions.CancelBox(A, B, Player);
        int walls = 0, structures = 0;
        for (int z = z0; z <= z1; z++)
        for (int y = y0 - 1; y <= y1; y++)
        for (int x = x0 - 1; x <= x1; x++)
        {
            var c = new TilePos(x, y, z);
            if (!sim.World.InBounds(c)) continue;
            // Edges owned by this cell (east, north) that touch the box: inside it or on its outline.
            if (y >= y0 && x <= x1 && sim.Constructions.Demolish(c, EdgeInfo.East, sim.Items)) walls++;
            if (x >= x0 && y <= y1 && sim.Constructions.Demolish(c, EdgeInfo.North, sim.Items)) walls++;
            if (x < x0 || y < y0) continue;
            if (sim.Stockpiles.Racks.ContainsKey(c) && sim.Stockpiles.OwnerOf(c) != Player) continue; // another colony's rack
            if (sim.RemoveStructure(c)) structures++;
        }
        int covers = sim.Constructions.ClearCoverBox(StructureKind.Floor, A, B, sim.Items, Player)
                   + sim.Constructions.ClearCoverBox(StructureKind.Roof, A, B, sim.Items, Player);
        // Built parts leave their materials (blueprints leave whatever was delivered, as ever).
        var built = new List<Part>();
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            if (sim.Rooms.PartAt(new TilePos(x, y, z)) is { Built: true } p && p.Room.Faction == Player && !built.Contains(p)) built.Add(p);
        int parts = sim.Rooms.RemovePartsIn(A, B, Player);
        Refund(sim, built);
        return $"Demolished {walls} wall pieces, {covers} floors and roofs, {structures} structures, {parts} workshop parts; cancelled {plans} plans";
    }
}

public sealed record AddStockpile(TilePos A, TilePos B) : Command
{
    public override string Apply(ColonySim sim) => $"Stockpile: zoned {sim.Stockpiles.AddFloorBox(A, B, Player)} cells";
}

public sealed record AddFarm(PlantKind Crop, TilePos A, TilePos B) : Command
{
    public override string Apply(ColonySim sim)
    {
        int n = sim.Farms.AddFloorBox(Crop, A, B, Player);
        return n > 0 ? $"Farm plot ({Lower(PlantInfo.Name(Crop))}): zoned {n} plots"
            : Crop == PlantKind.Glowcap ? "No plots: glowcaps need a solid floor under a roof" : "No plots: crops need free grass or soil";
    }
}

/// <summary>Full stone wall cells (dams, canals), stacking on walls below.</summary>
public sealed record PlaceWalls(TilePos A, TilePos B) : Command
{
    public override string Apply(ColonySim sim)
    {
        int n = sim.Constructions.PlaceWalls(A, B, Player);
        return n > 0 ? $"Stone blocks: {n} planned (each needs 1 stone and a builder)" : "Stone blocks: no open cell with ground or a block below in that box";
    }
}

public sealed record CancelWalls(TilePos A, TilePos B) : Command
{
    public override string Apply(ColonySim sim) => $"Cancelled {sim.Constructions.CancelBox(A, B, Player)} wall blueprints";
}

/// <summary>Debug: completes every designation at once (top-down, as colonists would).</summary>
public sealed record DebugCompleteAll : Command
{
    public override string Apply(ColonySim sim)
    {
        var all = sim.Designations.All.Keys.Where(p => sim.Designations.OwnerOf(p) == Player).ToList();
        all.Sort((p, q) => q.Z != p.Z ? q.Z.CompareTo(p.Z) : q.Y != p.Y ? q.Y.CompareTo(p.Y) : q.X.CompareTo(p.X));
        foreach (var p in all) sim.CompleteDesignation(p);
        return $"Debug: completed {all.Count} designations";
    }
}

// ---- Rooms and parts -------------------------------------------------------------------------------------

/// <summary>Paints a room of a workshop type (or a home) over the floor in a box.</summary>
public sealed record PaintRoom(string RoomTypeId, TilePos A, TilePos B) : Command
{
    public override string Apply(ColonySim sim)
    {
        var type = RoomTypeId == Catalog.Home.Id ? Catalog.Home : Catalog.Workshops.First(w => w.Id == RoomTypeId);
        int n = sim.Rooms.Paint(type, A, B, Player);
        return n > 0 ? $"{type.Name}: marked out {n} floor cells" : $"{type.Name}: no free floor in that box";
    }
}

public sealed record EraseRoom(TilePos A, TilePos B) : Command
{
    public override string Apply(ColonySim sim)
    {
        var built = sim.Rooms.BuiltPartsIn(A, B, Player); // they come down with their cells, leaving their materials
        int cells = sim.Rooms.Erase(A, B, Player);
        Refund(sim, built);
        return built.Count == 0 ? $"Erased {cells} room cells" : $"Erased {cells} room cells and took down {built.Count} parts";
    }
}

/// <summary>Places a part (a blueprint unless instant build) with its origin cell, rotation and size.</summary>
public sealed record PlacePart(string PartId, TilePos Origin, bool Rotated, int Size) : Command
{
    public override string Apply(ColonySim sim)
    {
        var part = Catalog.Part(PartId);
        if (sim.Rooms.RoomAt(Origin) is { } room && room.Faction != Player) return "That room belongs to another colony";
        string name = Lower(PartInfo.Name(part, Size));
        var placed = sim.Rooms.Place(part, Origin, Rotated, Size);
        return placed is not null
            ? placed.Built ? $"Placed a {name} in {placed.Room.Name}"
                : $"Placed a {name} blueprint in {placed.Room.Name}: needs {placed.Cost.Count} {Mat(placed.Cost.Material)} and a builder"
            : $"Can't place {name}: {sim.Rooms.CanPlace(part, Origin, Rotated, Size)}";
    }
}

public sealed record RemoveParts(TilePos A, TilePos B) : Command
{
    public override string Apply(ColonySim sim)
    {
        var built = sim.Rooms.BuiltPartsIn(A, B, Player);
        int n = sim.Rooms.RemovePartsIn(A, B, Player);
        Refund(sim, built);
        return $"Removed {n} parts";
    }
}

/// <summary>Switches the machine covering this cell to its next (or previous) recipe.</summary>
public sealed record CycleRecipe(TilePos Cell, bool Back) : Command
{
    public override string Apply(ColonySim sim)
    {
        if (sim.Rooms.PartAt(Cell) is not { Role: PartRole.Machine } machine) return "No machine there";
        if (machine.Room.Faction != Player) return "That machine belongs to another colony";
        return sim.Rooms.CycleRecipe(machine, Back)
            ? $"{machine.Name} in {machine.Room.Name} now makes {Lower(machine.Recipe!.Name)}: {machine.Recipe.Describe()}"
            : $"{machine.Name} only makes {Lower(machine.Recipe!.Name)}";
    }
}

// ---- Structures, walls, floors and roofs --------------------------------------------------------------------

/// <summary>A water wheel or pump blueprint in a cell, facing 0..3 (east, south, west, north).</summary>
public sealed record PlaceStructure(StructureKind Kind, TilePos Cell, int Facing) : Command
{
    public override string Apply(ColonySim sim)
    {
        string name = Lower(Constructions.Name(Kind));
        var bp = sim.Constructions.Place(Kind, Cell, Facing, Player);
        return bp is not null
            ? $"Placed a {name} blueprint: needs {bp.Cost.Count} {Mat(bp.Cost.Material)} and a builder"
            : $"Can't place a {name}: {sim.Constructions.CanPlace(Kind, Cell)}";
    }
}

/// <summary>Removes built waterworks in a box (and the cell above its ground) and cancels blueprints there.</summary>
public sealed record RemoveStructures(TilePos A, TilePos B) : Command
{
    public override string Apply(ColonySim sim)
    {
        int built = 0;
        for (int z = Math.Min(A.Z, B.Z); z <= Math.Max(A.Z, B.Z) + 1; z++)
        for (int y = Math.Min(A.Y, B.Y); y <= Math.Max(A.Y, B.Y); y++)
        for (int x = Math.Min(A.X, B.X); x <= Math.Max(A.X, B.X); x++)
        {
            var c = new TilePos(x, y, z);
            if (sim.Stockpiles.Racks.ContainsKey(c) && sim.Stockpiles.OwnerOf(c) != Player) continue; // another colony's rack
            if (sim.RemoveStructure(c)) built++;
        }
        return $"Took down {built} structures, cancelled {sim.Constructions.CancelBox(A, B, Player)} blueprints";
    }
}

/// <summary>Storage racks on every stockpile cell of this colony in a box (on the cells above its ground), fronts facing 0..3.</summary>
public sealed record PlaceRacks(TilePos A, TilePos B, int Facing) : Command
{
    public override string Apply(ColonySim sim)
    {
        int n = 0, planks = 0;
        string? why = null;
        for (int z = Math.Min(A.Z, B.Z); z <= Math.Max(A.Z, B.Z); z++)
        for (int y = Math.Min(A.Y, B.Y); y <= Math.Max(A.Y, B.Y); y++)
        for (int x = Math.Min(A.X, B.X); x <= Math.Max(A.X, B.X); x++)
        {
            var cell = new TilePos(x, y, z + 1);
            if (!sim.World.InBounds(cell) || !TileInfo.IsSolid(sim.World.Get(x, y, z))) continue;
            if (sim.Stockpiles.Contains(cell) && sim.Stockpiles.OwnerOf(cell) != Player) continue;
            if (sim.Constructions.Place(StructureKind.Rack, cell, Facing, Player) is { } bp) { n++; planks += bp.Cost.Count; }
            else why ??= sim.Constructions.CanPlace(StructureKind.Rack, cell);
        }
        return n > 0 ? $"Planned {n} storage racks: {planks} planks and builders" : $"No racks placed: {why ?? "no floor in the box"}";
    }
}

/// <summary>A thin wall, door or window on one cell edge.</summary>
public sealed record PlaceEdge(StructureKind Piece, TilePos Cell, int Side, int Material) : Command
{
    public override string Apply(ColonySim sim)
    {
        var bp = sim.Constructions.PlaceEdge(Piece, Cell, Side, Material, Player);
        string name = Lower(Constructions.Name(Piece));
        return bp is not null
            ? $"Planned a {name}: needs {bp.Cost.Count} {Mat(bp.Cost.Material)} and a builder"
            : $"Can't place a {name}: {sim.Constructions.CanPlaceEdge(Piece, Cell, Side)}";
    }
}

/// <summary>Cancels the planned piece on an edge, or takes the built one down (its material is left beside it).</summary>
public sealed record RemoveEdge(TilePos Cell, int Side) : Command
{
    public override string Apply(ColonySim sim) =>
        sim.Constructions.CancelEdge(Cell, Side, Player) ? "Cancelled the planned piece"
        : sim.Constructions.Demolish(Cell, Side, sim.Items) ? "Took the piece down (its material is left beside it)"
        : "Nothing on that edge";
}

/// <summary>
/// A straight line of edge pieces: from <see cref="Start"/> along its edge direction to coordinate <see cref="To"/>
/// (y for east edges, x for north edges). Remove takes pieces down instead.
/// </summary>
public sealed record EdgeLine(StructureKind Piece, TilePos Start, int Side, int To, int Material, bool Remove) : Command
{
    public IEnumerable<(TilePos Cell, int Side)> Edges()
    {
        if (Side == EdgeInfo.East)
            for (int y = Math.Min(Start.Y, To); y <= Math.Max(Start.Y, To); y++) yield return (Start with { Y = y }, Side);
        else
            for (int x = Math.Min(Start.X, To); x <= Math.Max(Start.X, To); x++) yield return (Start with { X = x }, Side);
    }

    public override string Apply(ColonySim sim)
    {
        int done = 0;
        foreach (var (cell, side) in Edges().ToList())
        {
            if (Remove)
            {
                if (sim.Constructions.CancelEdge(cell, side, Player) || sim.Constructions.Demolish(cell, side, sim.Items)) done++;
            }
            else if (sim.Constructions.PlaceEdge(Piece, cell, side, Material, Player) is not null) done++;
        }
        return Remove ? $"Removed {done} wall pieces along the line" : $"Planned {done} {Lower(Constructions.Name(Piece))}s along the line";
    }
}

/// <summary>Walls of one material on every edge round a box of floor.</summary>
public sealed record PlaceOutline(TilePos A, TilePos B, int Material) : Command
{
    public override string Apply(ColonySim sim)
    {
        int n = sim.Constructions.PlaceOutline(A, B, Material, StructureKind.EdgeWall, Player);
        var (mat, _) = Constructions.Cost(StructureKind.EdgeWall, Material);
        return n > 0 ? $"Walls: {n} planned round the box ({n} {Mat(mat)}); add doors with the Door tool"
            : "Walls: none fit (already walled, or no floor beside the outline)";
    }
}

/// <summary>
/// A whole house over a box between ground tiles (<see cref="Houses"/>): walls round each of its storeys, a door, floors,
/// stairs between storeys and a roof unless rock roofs it. Nothing is planned if anything is in the way.
/// </summary>
public sealed record PlaceHouse(TilePos A, TilePos B, int Storeys, int Wall, int Floor, int Roof) : Command
{
    public override string Apply(ColonySim sim)
    {
        var plan = Houses.Survey(sim, A, B, Storeys);
        if (plan.Problem is { } why) return $"Can't plan the house: {why}";
        int floor = Math.Clamp(Floor, 1, FloorMaterials.Count - 1), roof = Math.Clamp(Roof, 1, RoofMaterials.Count - 1);
        var p = Houses.Place(sim, plan, WallMaterials.Clamp(Wall), floor, roof, Player);
        string storeys = plan.Storeys == 1 ? "one storey" : $"{plan.Storeys} storeys";
        string needs = string.Join(", ", p.Cost.Select(kv => $"{kv.Value} {Mat(kv.Key)}"));
        string roofed = p.Roofs == 0 ? "rock overhead for a roof" : p.Roofs < plan.Width * plan.Depth ? $"a roof of {p.Roofs} cells (rock covers the rest)" : "a roof";
        return $"House planned, {plan.Width}x{plan.Depth}, {storeys}: {p.Walls} wall pieces, {(p.Door ? "a door, " : "")}" +
               $"{p.Floors + p.Decks} floor cells{(p.Stairs > 0 ? $", {p.Stairs} stairs" : "")}, {roofed}. Needs {needs}";
    }
}

/// <summary>Cancels planned and takes down built walls, doors and windows on every edge in or round a box.</summary>
public sealed record ClearWalls(TilePos A, TilePos B) : Command
{
    public override string Apply(ColonySim sim)
    {
        int x0 = Math.Min(A.X, B.X), x1 = Math.Max(A.X, B.X), y0 = Math.Min(A.Y, B.Y), y1 = Math.Max(A.Y, B.Y);
        int planned = 0, built = 0;
        for (int z = Math.Min(A.Z, B.Z) + 1; z <= Math.Max(A.Z, B.Z) + 1; z++)
        for (int y = y0 - 1; y <= y1; y++)
        for (int x = x0 - 1; x <= x1; x++)
        {
            var c = new TilePos(x, y, z);
            if (!sim.World.InBounds(c)) continue;
            foreach (int side in new[] { EdgeInfo.East, EdgeInfo.North })
            {
                bool onBox = side == EdgeInfo.East ? y >= y0 : x >= x0; // the edge touches the box
                if (!onBox) continue;
                if (sim.Constructions.CancelEdge(c, side, Player)) planned++;
                else if (sim.Constructions.Demolish(c, side, sim.Items)) built++;
            }
        }
        return $"Cancelled {planned} planned and took down {built} built wall pieces";
    }
}

/// <summary>Floors or roofs (<see cref="StructureKind.Floor"/> / <see cref="StructureKind.Roof"/>) over a box; Remove takes them up.</summary>
public sealed record Covers(StructureKind Kind, TilePos A, TilePos B, int Material, bool Remove) : Command
{
    public override string Apply(ColonySim sim)
    {
        bool floor = Kind == StructureKind.Floor;
        if (Remove) return $"Took up {sim.Constructions.ClearCoverBox(Kind, A, B, sim.Items, Player)} {(floor ? "floor" : "roof")} cells";
        string what = floor ? Lower(FloorMaterials.All[Material].Name) : Lower(RoofMaterials.All[Material].Name) + " roof";
        var cost = Constructions.Cost(Kind, Material);
        return $"Planned {sim.Constructions.PlaceCoverBox(Kind, A, B, Material, Player)} cells of {what} ({cost.Count} {Mat(cost.Material)} each)";
    }
}

// ---- People and homes ------------------------------------------------------------------------------------

public sealed record SetWorkPriority(int ColonistId, Work Work, int Priority) : Command
{
    public override string Apply(ColonySim sim)
    {
        if (sim.Colonists.FirstOrDefault(c => c.Id == ColonistId) is not { } c) return "No such colonist";
        if (c.Faction != Player) return $"{c.Name} belongs to another colony";
        c.SetPriority(Work, Priority);
        return $"{c.Name}: {Lower(Colonist.WorkNames[(int)Work])} {(Priority == 0 ? "off" : $"priority {Priority}")}";
    }
}

/// <summary>Sets the home whose room covers this cell to a housing policy (by index).</summary>
public sealed record SetHomePolicy(TilePos Cell, int Policy) : Command
{
    public override string Apply(ColonySim sim)
    {
        if (Player >= sim.Factions.Count) return "No such colony";
        var housing = sim.Factions[Player].Housing;
        var policies = housing.Policies;
        if (sim.Rooms.RoomAt(Cell) is not { Type.IsHome: true } room || housing.HomeOf(room) is not { } home) return "No home there";
        if (Policy < 0 || Policy >= policies.Count) return "No such policy";
        housing.SetPolicy(home, policies[Policy]);
        return $"{home.Name} is now a {Lower(policies[Policy].Name)}: room for {Housing.Plan(home).Residents}";
    }
}

public sealed record SetPolicyTier(int Policy, int Slot, int Tier) : Command
{
    public override string Apply(ColonySim sim)
    {
        if (Policy < 0 || Policy >= Homes(sim).Policies.Count) return "No such policy";
        Homes(sim).SetTier(Homes(sim).Policies[Policy], Slot, Tier);
        return $"{Homes(sim).Policies[Policy].Name}: {Housing.Slots[Slot]} {Catalog.FixtureTiers[Homes(sim).Policies[Policy].Tier[Slot]]}";
    }
}

public sealed record SetPolicyPerResident(int Policy, int Slot, float PerResident) : Command
{
    public override string Apply(ColonySim sim)
    {
        if (Policy < 0 || Policy >= Homes(sim).Policies.Count) return "No such policy";
        Homes(sim).SetPerResident(Homes(sim).Policies[Policy], Slot, PerResident);
        return $"{Homes(sim).Policies[Policy].Name}: {Homes(sim).Policies[Policy].PerResident[Slot]:0.##} {Housing.Slots[Slot]} each";
    }
}

public sealed record SetPolicyShared(int Policy, bool Shared) : Command
{
    public override string Apply(ColonySim sim)
    {
        if (Policy < 0 || Policy >= Homes(sim).Policies.Count) return "No such policy";
        Homes(sim).SetShared(Homes(sim).Policies[Policy], Shared);
        return $"{Homes(sim).Policies[Policy].Name}: {(Shared ? "shared lodging" : "family homes")}";
    }
}

/// <summary>A new housing policy, a copy of the last one.</summary>
public sealed record AddPolicy : Command
{
    public override string Apply(ColonySim sim)
    {
        if (Player >= sim.Factions.Count) return "No such colony";
        var housing = sim.Factions[Player].Housing;
        var policies = housing.Policies;
        var last = policies[^1];
        policies.Add(last.Clone($"{last.Name} {policies.Count + 1}"));
        housing.Touch();
        return $"New policy: {policies[^1].Name}";
    }
}

/// <summary>
/// Settles the game for a snapshot (<see cref="ColonySim.Settle"/>): a command, so in an online game every peer
/// settles before the same tick and the host's snapshot matches everyone's world.
/// </summary>
public sealed record SettleGame : Command
{
    public override string Apply(ColonySim sim)
    {
        sim.Settle();
        return "Saved the game's state";
    }
}

// ---- Military --------------------------------------------------------------------------------------------

/// <summary>Drafts a colonist (or, with id -1, every grown colonist of the colony) to fight, or stands them down.</summary>
public sealed record SetDrafted(int ColonistId, bool On) : Command
{
    public override string Apply(ColonySim sim)
    {
        var people = ColonistId < 0
            ? sim.Colonists.Where(c => c.Faction == Player && !c.IsChild && !c.IsMachine).ToList() // machines are driven, they don't fight
            : sim.Colonists.Where(c => c.Id == ColonistId).ToList();
        if (people.Count == 0) return ColonistId < 0 ? "Nobody to draft" : "No such colonist";
        if (ColonistId >= 0 && sim.CanCommand(people[0], Player) is { } why) return why;
        if (ColonistId >= 0 && people[0].IsChild && On) return $"{people[0].Name} is a child";
        if (ColonistId >= 0 && people[0].IsMachine && On) return $"{people[0].Name} is a machine: draft its driver";
        foreach (var c in people) c.SetDrafted(sim, On);
        return ColonistId < 0
            ? On ? $"Drafted {people.Count} colonists: send them with the squad tool (G)" : $"{people.Count} colonists stood down"
            : On ? $"{people[0].Name} drafted" : $"{people[0].Name} stood down";
    }
}

/// <summary>Sends the colony's drafted colonists to a spot (they fight whatever comes at them there).</summary>
public sealed record SquadMove(TilePos Target) : Command
{
    public override string Apply(ColonySim sim)
    {
        // A loose formation round the spot: the first on it, the rest on standable cells spiralling out.
        var spots = new List<TilePos>();
        for (int r = 0; r <= 4 && spots.Count < 64; r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            var p = Target.Offset(dx, dy, 0);
            if (Navigation.Movement.IsStandable(sim.World, p) && (r == 0 || sim.Connectivity.Connected(p, Target))) spots.Add(p);
        }
        int n = 0;
        foreach (var c in sim.Colonists)
            if (c.Faction == Player && c.Drafted)
            {
                c.OrderMove(spots.Count > 0 ? spots[n % spots.Count] : Target);
                n++;
            }
        return n == 0 ? "Nobody is drafted (G drafts everyone)" : $"Squad of {n} moving out";
    }
}

/// <summary>Sends the colony's drafted colonists to destroy a nest.</summary>
public sealed record SquadAttack(int NestId) : Command
{
    public override string Apply(ColonySim sim)
    {
        if (sim.Wildlife.NestById(NestId) is not { } nest) return "That nest is gone";
        int n = 0;
        foreach (var c in sim.Colonists)
            if (c.Faction == Player && c.Drafted)
            {
                c.OrderAttack(NestId);
                n++;
            }
        return n == 0 ? "Nobody is drafted (G drafts everyone)" : $"Squad of {n} sent to destroy the nest of {nest.Kind.Plural}";
    }
}

/// <summary>Debug: a nest of giant ants at a cell (angry: at war with the issuing colony from the start).</summary>
public sealed record DebugSpawnNest(TilePos At, int Population, bool Angry) : Command
{
    public override string Apply(ColonySim sim)
    {
        if (!Navigation.Movement.IsStandable(sim.World, At)) return "No footing for a nest there";
        var nest = sim.Wildlife.AddNest(sim, Combat.CreatureKinds.GiantAnt, At, Population);
        if (Angry) sim.Wildlife.Provoke(sim, nest, Player);
        return $"Debug: a nest of {Population} giant ants{(Angry ? ", angry" : "")}";
    }
}

// ---- Trade -------------------------------------------------------------------------------------------------

/// <summary>Zones a trade depot over the floor of a box: where other colonies' traders deliver what they sold us.</summary>
public sealed record AddTradeDepot(TilePos A, TilePos B) : Command
{
    public override string Apply(ColonySim sim) =>
        $"Trade depot: zoned {sim.Stockpiles.AddFloorBox(A, B, Player, depot: true)} cells";
}

/// <summary>Offers another colony a deal: what each side gives.</summary>
public sealed record ProposeTrade(int To, Trade.TradeTerms Terms) : Command
{
    public override string Apply(ColonySim sim) => sim.Trades.Propose(sim, Player, To, Terms);
}

/// <summary>Answers an offer with new terms (the other colony's turn next).</summary>
public sealed record CounterTrade(int Deal, Trade.TradeTerms Terms) : Command
{
    public override string Apply(ColonySim sim) => sim.Trades.Counter(sim, Deal, Player, Terms);
}

/// <summary>Accepts the offer on the table: binding, the traders set out.</summary>
public sealed record AcceptTrade(int Deal) : Command
{
    public override string Apply(ColonySim sim) => sim.Trades.Accept(sim, Deal, Player);
}

/// <summary>Declines an offer made to us, or withdraws our own.</summary>
public sealed record RejectTrade(int Deal) : Command
{
    public override string Apply(ColonySim sim) => sim.Trades.Reject(sim, Deal, Player);
}

// ---- Factories (continued) -----------------------------------------------------------------------------------

/// <summary>Sets the machine covering a cell to stop starting batches once the colony holds <see cref="Limit"/> of its product (0: no limit).</summary>
public sealed record SetMachineLimit(TilePos Cell, int Limit) : Command
{
    public override string Apply(ColonySim sim)
    {
        if (sim.Rooms.PartAt(Cell) is not { Role: PartRole.Machine } machine) return "No machine there";
        if (machine.Room.Faction != Player) return "That machine belongs to another colony";
        sim.Rooms.SetLimit(machine, Limit);
        string what = machine.Recipe?.Name.ToLowerInvariant() ?? "its product";
        return machine.Limit == 0 ? $"{machine.Name} makes {what} without limit" : $"{machine.Name} keeps {machine.Limit} {what} in store";
    }
}

/// <summary>
/// Sets the priority (1 first ... 5 last) of this colony's orders in a box. The dig tools send it right after each box, so
/// it says nothing (the box's own message stands).
/// </summary>
public sealed record SetOrderPriority(TilePos A, TilePos B, int Priority) : Command
{
    public override string Apply(ColonySim sim)
    {
        sim.Designations.SetPriorityBox(A, B, Priority, Player);
        return "";
    }
}

// ---- Stockpiles ----------------------------------------------------------------------------------------------

/// <summary>
/// Changes one setting of a stockpile (by id): its name, priority, reach, cap, reserve, workshop access and list, or
/// which items it takes (see <see cref="StockpileOption"/> for what <see cref="Value"/>, <see cref="Arg"/> and
/// <see cref="Text"/> mean for each).
/// </summary>
public sealed record ConfigureStockpile(int Stockpile, StockpileOption Option, int Value = 0, int Arg = 0, string Text = "") : Command
{
    public override string Apply(ColonySim sim)
    {
        if (sim.Stockpiles.ZoneById(Stockpile) is not { } zone) return "No such stockpile";
        if (zone.Faction != Player) return "That stockpile belongs to another colony";
        if (Option == StockpileOption.Workshop && Arg != 0 && sim.Rooms.All.FirstOrDefault(r => r.Id == Value) is null) return "No such workshop";
        if (!sim.Stockpiles.Configure(zone, Option, Value, Arg, Text)) return "";
        return Option switch
        {
            StockpileOption.Name => $"Stockpile renamed {zone.Name}",
            StockpileOption.Priority => $"{zone.Name}: priority {zone.Priority}",
            StockpileOption.Reach => zone.Reach == 0 ? $"{zone.Name} takes goods from any distance" : $"{zone.Name} takes goods from within {zone.Reach} tiles",
            StockpileOption.Cap => zone.Cap == 0 ? $"{zone.Name} keeps any amount of each item" : $"{zone.Name} keeps at most {zone.Cap} of each item",
            StockpileOption.Reserve => zone.Reserve ? $"{zone.Name} is a reserve: kept for workshops and trade" : $"{zone.Name} is open to everyone",
            _ => "",
        };
    }
}

// ---- Dev mode ----------------------------------------------------------------------------------------------

/// <summary>Turns dev mode on or off (single-player games only): instant, free construction, no schooling needed, and
/// with <see cref="InstantOrders"/> work orders done at once (see <see cref="ColonySim.DevMode"/>).</summary>
public sealed record SetDevMode(bool On, bool InstantOrders) : Command
{
    public override string Apply(ColonySim sim)
    {
        if (On && sim.Factions.Count > 1) return "Dev mode is for single-player games";
        sim.SetDevMode(On, InstantOrders);
        return !On ? "Dev mode off" : InstantOrders ? "Dev mode: everything builds and digs at once, for free" : "Dev mode: everything builds at once, for free";
    }
}

/// <summary>Dev mode: drops <see cref="Count"/> of a catalog item (by id) at a cell; machines and automatons roll out
/// there, and "colonist" brings settlers.</summary>
public sealed record DevSpawn(TilePos At, string ItemId, int Count) : Command
{
    public override string Apply(ColonySim sim) =>
        !sim.DevMode ? "Spawning needs dev mode" : sim.DevSpawn(At, ItemId, Count, Player);
}
