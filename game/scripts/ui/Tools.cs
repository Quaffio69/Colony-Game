using System;
using System.Linq;
using Colony.Core;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.World;
using Godot;
using ToolMode = ColonyGame.DesignationTool.ToolMode;

namespace ColonyGame.UI;

/// <summary>
/// One tool as the player sees it: icon, colour, full name, short name (the orders ribbon), one line on what it does,
/// and the controls as key hints ("Drag" a box, "Shift" remove, "RMB" done).
/// </summary>
public sealed record ToolInfo(string Glyph, Color Color, string Name, string Short, string About, (string Key, string What)[] How);

/// <summary>
/// The single source for what every tool is called, how it looks and how it is used: the Build window, the orders
/// ribbon, the tool card and the help all read it, so a tool has one name everywhere. Also names built things with
/// their material first ("Plank wall", "Flagstone floor", "Thatch roof").
/// </summary>
public static class Tools
{
    private static Color Tok(string t) => GlyphTheme.Tok(t);
    public static Color Rgb(uint c) => new(((c >> 16) & 0xff) / 255f, ((c >> 8) & 0xff) / 255f, (c & 0xff) / 255f);

    // ---- Names of built things ----------------------------------------------------------------------------

    public static string WallName(int m) => $"{WallMaterials.All[WallMaterials.Clamp(m)].Name} wall";
    public static Color WallColor(int m) => Rgb(WallMaterials.All[WallMaterials.Clamp(m)].Color);

    public static string FloorName(int m)
    {
        string n = FloorMaterials.All[Math.Clamp(m, 0, FloorMaterials.Count - 1)].Name;
        return n.Contains("floor", StringComparison.OrdinalIgnoreCase) ? n : n + " floor";
    }

    public static string RoofName(int m) => RoofMaterials.All[Math.Clamp(m, 0, RoofMaterials.Count - 1)].Name + " roof";

    /// <summary>A planned structure's name with its material: "Plank wall", "Flagstone floor", "Stone block".</summary>
    public static string BuiltName(Blueprint bp) => Name(bp.Kind, bp.Material);

    public static string Name(StructureKind kind, int material) => kind switch
    {
        StructureKind.EdgeWall => WallName(material),
        StructureKind.Floor => FloorName(material),
        StructureKind.Roof => RoofName(material),
        StructureKind.Wall => "Stone block",
        StructureKind.Deck => FloorName(material) + " (upper storey)",
        StructureKind.Stair => "Stairs",
        _ => Constructions.Name(kind),
    };

    /// <summary>What stands on an edge, with its material ("Brick wall", "Door", "Window"); null for nothing.</summary>
    public static string? EdgeName(byte info) => EdgeInfo.Kind(info) switch
    {
        EdgeKind.Wall => WallName(EdgeInfo.Material(info)),
        EdgeKind.Door => "Door",
        EdgeKind.Window => "Window",
        _ => null,
    };

    // ---- Hotkeys (DF-style: lowercase = the key, uppercase = Shift+key) ---------------------------------------

    /// <summary>
    /// The orders' keys. DF: d mine, u/j/i stairs, t chop, p gather, x remove; d, j and i are taken here (pan right,
    /// Work, Stocks), so mine is n, down stair k, up/down stair o, and the removal box is X (Shift+X, or Del).
    /// Stockpile (DF s p) is P, farm F (again: the next crop), stone block B, demolish O (DF o: remove construction).
    /// </summary>
    public static readonly (ToolMode Mode, Key Key, bool Shift)[] Hotkeys =
    [
        (ToolMode.Dig, Key.N, false), (ToolMode.Stairs, Key.Y, false), (ToolMode.UpStair, Key.U, false), (ToolMode.DownStair, Key.K, false),
        (ToolMode.UpDownStair, Key.O, false), (ToolMode.Chop, Key.T, false), (ToolMode.Gather, Key.P, false), (ToolMode.Stockpile, Key.P, true),
        (ToolMode.FarmGrain, Key.F, true), (ToolMode.Block, Key.B, true), (ToolMode.Demolish, Key.O, true), (ToolMode.Cancel, Key.X, true),
    ];

    /// <summary>"n", "P" (Shift+P), or null when the tool has no key of its own (farm crops share F).</summary>
    public static string? KeyOf(ToolMode m)
    {
        if (m is ToolMode.FarmHerbs or ToolMode.FarmFlax or ToolMode.FarmGlowcaps) m = ToolMode.FarmGrain;
        foreach (var (mode, key, shift) in Hotkeys)
            if (mode == m) return shift ? key.ToString().ToUpperInvariant() : key.ToString().ToLowerInvariant();
        return null;
    }

    /// <summary>"Shift+P" for the hint text.</summary>
    public static string? KeySpoken(ToolMode m) => KeyOf(m) is { } k ? char.IsUpper(k[0]) ? $"Shift+{k}" : k.ToUpperInvariant() : null;

    // ---- Controls -----------------------------------------------------------------------------------------

    private static readonly (string, string)[] BoxKeys = [("Drag", "a box"), ("Shift", "remove"), ("RMB", "done")];
    private static readonly (string, string)[] AreaKeys = [("Drag", "an area"), ("Shift", "remove"), ("RMB", "done")];
    private static readonly (string, string)[] RemoveKeys = [("Drag", "a box"), ("RMB", "done")];

    // ---- What a mode is -----------------------------------------------------------------------------------

    /// <summary>The tool in hand, with its current pick (workshop, part, material, size) and the controls for its state.</summary>
    public static ToolInfo Current(DesignationTool t)
    {
        var info = Of(t, t.Mode);
        if (t.Selecting && t.Mode == ToolMode.House)
            info = info with { How = [("Click", "confirm"), ("[ ] or Z/X", "storeys"), ("RMB", "abort")] };
        else if (t.Selecting && t.Mode != ToolMode.Edge)
            info = info with { How = [("Click", "confirm"), ("RMB", "abort")] };
        else if (t.Selecting)
            info = info with { How = [("Release", "confirm"), ("RMB", "abort")] };
        return info;
    }

    /// <summary>A mode as the tool would be with its current picks.</summary>
    public static ToolInfo Of(DesignationTool t, ToolMode m)
    {
        var info = Base(t, m);
        if (!DesignationTool.IsDigTool(m)) return info;
        // Dig and stair orders carry a priority: 1 is dug first, 5 last.
        string pri = t.DigPriority == Designations.DefaultPriority ? "" : $", priority {t.DigPriority}";
        return info with
        {
            Name = info.Name + pri,
            About = info.About + $". Priority {t.DigPriority} ([ more urgent, ] less): miners take the most urgent orders first, then the highest",
            How = [("Drag", "a box"), ("[ ]", "priority"), ("Shift", "remove"), ("RMB", "done")],
        };
    }

    private static ToolInfo Base(DesignationTool t, ToolMode m) => m switch
    {
        ToolMode.None => new("·", Tok("dim"), "Look", "Look", "Click anything to inspect it", [("Click", "inspect"), ("Drag", "levels"), ("RMB drag", "pan")]),
        ToolMode.Dig => new("▼", Tok("ore"), "Dig", "Dig", "Dig out rock and soil (stone half the time; clay, sand, ores and gems always). Deeper rock is harder", BoxKeys),
        ToolMode.Stairs => new("≡", Tok("stair"), "Stairway", "Stairs", "Stairs through a box of levels: down at the top, up/down between, up at the bottom", BoxKeys),
        ToolMode.UpStair => new("▲", Tok("stair"), "Up stair", "Up", "Carve stairs that lead up", BoxKeys),
        ToolMode.DownStair => new("▼", Tok("stair"), "Down stair", "Down", "Carve stairs that lead down", BoxKeys),
        ToolMode.UpDownStair => new("÷", Tok("stair"), "Up/down stair", "Up/down", "Carve stairs that lead both ways", BoxKeys),
        ToolMode.Chop => new("♣", Tok("pine"), "Chop trees", "Chop", "Fell every tree in an area for logs (bigger trees give more)", AreaKeys),
        ToolMode.Gather => new("♦", Tok("good"), "Gather plants", "Gather", "Pick ripe berries, fiber and herbs in an area", AreaKeys),
        ToolMode.Stockpile => new("≡", Tok("item"), "Stockpile", "Stockpile", "Zone floor where haulers store loose goods", BoxKeys),
        ToolMode.FarmGrain => new("τ", Tok("farm"), "Grain farm", "Grain", "Wheat for grain, on grass or soil; faster within 2 tiles of water", BoxKeys),
        ToolMode.FarmHerbs => new("♣", Tok("good"), "Herb farm", "Herbs", "Herbs, on grass or soil; faster within 2 tiles of water", BoxKeys),
        ToolMode.FarmFlax => new("‖", Tok("info"), "Flax farm", "Flax", "Flax for fiber (beds, thread, cloth, rope, paper), on grass or soil", BoxKeys),
        ToolMode.FarmGlowcaps => new("°", Tok("gem"), "Glowcap farm", "Glowcaps", "Glowcaps grow on any floor under a roof, even deep underground", BoxKeys),
        ToolMode.Block => new("■", Tok("wall"), "Stone block", "Block", "A solid stone cell for dams and canals (1 stone each; stack them to raise)", BoxKeys),
        ToolMode.Demolish => new("▓", Tok("warn"), "Demolish", "Demolish", "Take down what is built in a box: walls, floors, roofs, racks, waterworks, workshop parts. Materials are left", RemoveKeys),
        ToolMode.Cancel => new("×", Tok("bad"), "Cancel orders", "Cancel", "Cancel orders and unbuilt plans in a box, and unzone its cells", RemoveKeys),
        ToolMode.Room when t.Workshop.IsHome => new("⌂", Tok("furn"), "Home", "Home",
            $"Mark out a home on floor: any shape, any levels, at least {Colony.Core.Agents.Housing.MinCells} cells", [("Drag", "mark out"), ("Shift", "shrink"), ("RMB", "done")]),
        ToolMode.Room => new("□", RoomView.RoomColor(t.Workshop) with { A = 1 }, $"{t.Workshop.Name} workshop", t.Workshop.Name,
            $"Mark out the workshop on floor, then place its stations. Touching {t.Workshop.Name} workshops join up", [("Drag", "mark out"), ("Shift", "shrink"), ("RMB", "done")]),
        ToolMode.Part => PartInfoOf(t),
        ToolMode.Structure => new(t.Structure == StructureKind.WaterWheel ? "☼" : "↑", Tok("water"), Constructions.Name(t.Structure), Constructions.Name(t.Structure),
            t.Structure == StructureKind.WaterWheel ? "Makes power from water flowing through its cell; hang it on a wall in a waterfall"
                : "Lifts water from its cell to the next cell and one level up, using power",
            [("Click", "place"), ("R", "turn"), ("Shift+drag", "remove"), ("RMB", "done")]),
        ToolMode.Edge when t.EdgePiece == StructureKind.EdgeWall => new("#", WallColor(t.WallMaterial), WallName(t.WallMaterial), "Wall",
            "A wall on the edges between cells, so the floor stays. Drag along an edge for a line, or away from it to wall round a box",
            [("Click", "one"), ("Drag", "a line or a box"), ("Shift", "remove"), ("RMB", "done")]),
        ToolMode.Edge => new(t.EdgePiece == StructureKind.Door ? "◘" : "°", Tok(t.EdgePiece == StructureKind.Door ? "wood" : "water2"), Constructions.Name(t.EdgePiece), Constructions.Name(t.EdgePiece),
            t.EdgePiece == StructureKind.Door ? "People and water pass through; can be cut into a built wall" : "Lets light in, nothing through; can be cut into a built wall",
            [("Click", "one"), ("Drag", "a line"), ("Shift", "remove"), ("RMB", "done")]),
        ToolMode.Floor => new("_", Rgb(FloorMaterials.All[t.FloorMaterial].Color), FloorName(t.FloorMaterial), "Floor",
            $"Lay floor over a box ({Catalog.FixtureTiers[FloorMaterials.All[t.FloorMaterial].Tier].ToLowerInvariant()} for homes)", BoxKeys),
        ToolMode.Roof => new("▲", Rgb(RoofMaterials.All[t.RoofMaterial].Color), RoofName(t.RoofMaterial), "Roof",
            $"Roof over a box of floor ({Catalog.FixtureTiers[RoofMaterials.All[t.RoofMaterial].Tier].ToLowerInvariant()} for homes)", BoxKeys),
        ToolMode.Rack => new("╥", Tok("wood"), "Storage rack", "Rack",
            $"Shelving over stockpile cells, up to {Stockpiles.MaxRackLevels} levels: {Stockpiles.Capacity(Stockpiles.MaxRackLevels)} stacks a cell ({Constructions.RackPlanksPerLevel} planks a level)",
            [("Drag", "a box"), ("R", "turn"), ("Shift", "remove"), ("RMB", "done")]),
        ToolMode.House => new("Π", WallColor(t.WallMaterial), t.Storeys == 1 ? "House" : $"House, {t.Storeys} storeys", "House",
            $"A whole building over a box: {WallName(t.WallMaterial).ToLowerInvariant()}s and a door, {FloorName(t.FloorMaterial).ToLowerInvariant()}" +
            $"{(t.Storeys > 1 ? ", stairs up through the storeys" : "")}, a {RoofName(t.RoofMaterial).ToLowerInvariant()} unless rock is overhead. " +
            "Materials: the last wall, floor and roof picked. Nothing is planned if anything is in the way (shown red)",
            [("Drag", "a box"), ("[ ]", "storeys"), ("Shift", "remove"), ("RMB", "done")]),
        ToolMode.TradeDepot => new("$", Tok("accent"), "Trade depot", "Depot", "Where other colonies' traders deliver what you buy; haulers carry it on to your stockpiles", BoxKeys),
        _ => new("?", Tok("dim"), m.ToString(), m.ToString(), "", BoxKeys),
    };

    private static ToolInfo PartInfoOf(DesignationTool t)
    {
        var part = t.Part;
        int size = t.SizeFor(part);
        var (fw, fd) = PartInfo.Footprint(part, size);
        if (t.Rotated) (fw, fd) = (fd, fw);
        var (material, cost) = PartInfo.Cost(part, size);
        string role = PartInfo.Role(part) switch
        {
            PartRole.Machine => MachineInfo(part, size),
            PartRole.Auxiliary => $"+{PartInfo.Bonus(part, size):P0} speed for up to {PartInfo.Coverage(part, size)} machine tiles nearby",
            PartRole.Input => "holds one of the workshop's inputs",
            _ => "holds the workshop's products",
        };
        var keys = PartInfo.Sizes(part) > 1
            ? new[] { ("Click", "place"), ("R", "turn"), ("[ ]", "size"), ("Shift+drag", "remove"), ("RMB", "done") }
            : new[] { ("Click", "place"), ("R", "turn"), ("Shift+drag", "remove"), ("RMB", "done") };
        return new(PartGlyph(part), PartInfo.Role(part) == PartRole.Machine ? Tok("warn") : Tok("dim"), PartInfo.Name(part, size), part.Name,
            $"{fw}×{fd}, {cost} {material.Name.ToLowerInvariant()}: {role}", keys);
    }

    public static string PartGlyph(PartType part) => PartInfo.Role(part) switch { PartRole.Machine => "÷", PartRole.Auxiliary => "☼", PartRole.Input => "↓", _ => "↑" };

    /// <summary>"makes 16 planks from 4 logs a batch, 0.6 s a unit; also: ..." for a station.</summary>
    public static string MachineInfo(PartType part, int size)
    {
        var first = part.Recipes[0];
        var batch = PartInfo.MachineBatchOf(part, size, first);
        double labour = batch.WorkTicks / (double)batch.Units / ColonySim.TicksPerSecond;
        string others = part.Recipes.Count > 1 ? $"; also {string.Join(", ", part.Recipes.Skip(1).Select(r => r.Name.ToLowerInvariant()))}" : "";
        return $"{first.Describe(batch.Units)} a batch, {labour:0.0} s a unit{others}";
    }
}
