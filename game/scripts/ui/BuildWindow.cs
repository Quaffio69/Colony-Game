using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Orders;
using Colony.Core.World;
using Godot;
using ToolMode = ColonyGame.DesignationTool.ToolMode;

namespace ColonyGame.UI;

/// <summary>
/// Build (B): every tool that changes the world. Tabs: Orders (dig, stairs, plants, dams), Zones (stockpiles,
/// farms), Rooms (homes and workshops: paint the room, then place its stations and fittings), Walls (by material),
/// Floors and roofs, Water. Left column picks a category, right column an item with its cost (red when short);
/// the footer describes the pick and, while placing, offers size, rotation and Cancel. Closing it drops build tools.
/// </summary>
public partial class BuildWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private DesignationTool Tool => _ui.Tool;
    private int _tab, _cat;
    private string _drawn = "";
    private Label _footName = null!, _footInfo = null!, _footPrompt = null!;
    private HBoxContainer _footActions = null!;

    private sealed record Item(string Glyph, Color Color, string Name, (ItemType Material, int Count)? Cost, string Tip, Func<bool> On, Action Pick);
    private sealed record Category(string Name, Func<List<Item>> Items);

    public static readonly string[] Tabs = ["Orders", "Zones", "Rooms", "Walls", "Floors", "Water"];

    public BuildWindow(GameUi ui) : base("build", "Build", "#", "warn", "B")
    {
        _ui = ui;
        MinCols = 56;
        MinRows = 12;
        Default = new WindowState { X = 2, Y = 20, W = 76, H = 18 };
        Closed += _ => Tool.LeaveBuild();
    }
    public BuildWindow() : this(null!) { }

    /// <summary>Opens on a workshop's room tab (--build-menu=ID).</summary>
    public void ShowWorkshop(string id)
    {
        _tab = 2;
        var cats = Categories();
        int i = cats.FindIndex(c => c.Name.Equals(Catalog.Workshops.FirstOrDefault(w => w.Id == id)?.Name ?? "", StringComparison.OrdinalIgnoreCase));
        _cat = Math.Max(0, i);
        if (Catalog.Workshops.FirstOrDefault(w => w.Id == id) is { } ws) Tool.SelectWorkshop(ws);
        Rebuild();
    }

    protected override void Build()
    {
        Header.AddChild(new GlyphSegmented(Tabs, _tab, i => { _tab = i; _cat = 0; _drawn = ""; Tick(); }));
        Header.AddChild(G.Gap(0.25f));
        Footer.AddChild(G.Gap(0.25f));
        var rule = new GlyphSection("");
        Footer.AddChild(rule);
        var top = new HBoxContainer();
        _footName = G.Text("", "Hi", bold: true);
        _footInfo = G.Text("", "Dim", expand: true);
        _footInfo.ClipText = true;
        top.AddChild(_footName);
        top.AddChild(_footInfo);
        Footer.AddChild(top);
        var bottom = new HBoxContainer();
        _footPrompt = G.Text("", "Faint", expand: true);
        _footPrompt.ClipText = true;
        _footActions = new HBoxContainer();
        bottom.AddChild(_footPrompt);
        bottom.AddChild(_footActions);
        Footer.AddChild(bottom);
        _drawn = "";
    }

    private string StateKey() =>
        $"{_tab}/{_cat}/{Tool.Mode}/{Tool.Workshop.Id}/{Tool.Part.Index}/{Tool.Structure}/{Tool.EdgePiece}/{Tool.WallMaterial}/{Tool.FloorMaterial}/{Tool.RoofMaterial}/" +
        $"{(Tool.PartFor(Tool.Mode) is { } p ? Tool.SizeFor(p) : 0)}/{Tool.Rotated}/{Tool.Selecting}/{TotalsHash()}";

    /// <summary>Changes when any stock total does (costs turn red or back).</summary>
    private long TotalsHash()
    {
        long h = 17;
        foreach (int t in _ui.Totals) h = h * 31 + t;
        return h;
    }

    public override void Tick()
    {
        string key = StateKey();
        if (key == _drawn) return;
        _drawn = key;
        DrawList();
        DrawFooter();
    }

    // ---- The list -------------------------------------------------------------------------------------

    private void DrawList()
    {
        var cats = Categories();
        _cat = Math.Clamp(_cat, 0, Math.Max(0, cats.Count - 1));
        var items = cats.Count > 0 ? cats[_cat].Items() : new List<Item>();
        G.Clear(Content);
        int n = Math.Max(cats.Count, items.Count);
        for (int i = 0; i < n; i++)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 0);
            if (i < cats.Count)
            {
                int ci = i;
                var catRow = new GlyphRow { Clickable = true, Selected = i == _cat, CustomMinimumSize = new Vector2(19 * GlyphTheme.Cw, GlyphTheme.Ch) };
                catRow.AddChild(G.Text(cats[i].Name, i == _cat ? "Hi" : "Dim", bold: i == _cat, cols: 18));
                catRow.Pressed += () => { _cat = ci; _drawn = ""; Tick(); };
                row.AddChild(catRow);
            }
            else row.AddChild(G.Text("", cols: 19));
            row.AddChild(new VRule());
            if (i < items.Count) row.AddChild(ItemRow(items[i]));
            Content.AddChild(row);
        }
    }

    private Control ItemRow(Item it)
    {
        bool on = it.On();
        var row = new GlyphRow { Clickable = true, Selected = on, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(new GlyphChip(it.Glyph, it.Color));
        row.AddChild(G.Text(it.Name, on ? "Accent" : "Text", bold: on, expand: true));
        if (it.Cost is { } cost)
        {
            bool enough = _ui.Count(cost.Material) >= cost.Count;
            row.AddChild(new GlyphChip(ItemLook.Glyph(cost.Material), ItemLook.Color(cost.Material)));
            row.AddChild(G.Text(cost.Count.ToString(), enough ? "Text" : "Bad", cols: 3));
        }
        else row.AddChild(G.Text("labour", "Faint", cols: 7));
        row.Pressed += () => { it.Pick(); _drawn = ""; Tick(); };
        string tip = it.Tip + (it.Cost is { } c ? $" · costs {c.Count} {c.Material.Name.ToLowerInvariant()} (have {_ui.Count(c.Material)})" : "");
        row.MouseEntered += () => GlyphStatus.Tip = tip;
        row.MouseExited += () => GlyphStatus.Tip = null;
        return row;
    }

    private void DrawFooter()
    {
        var mode = Tool.Mode;
        string name = Tool.ModeName(mode);
        int colon = name.IndexOf(": ", StringComparison.Ordinal);
        _footName.Text = colon > 0 && name.StartsWith("Build") ? name[(colon + 2)..].Split(" (")[0] : name.Split(" (")[0];
        _footInfo.Text = "  " + (Tool.SelectionInfo is { } s ? s : Describe(mode));
        G.Clear(_footActions);
        bool placing = mode != ToolMode.None || Tool.Selecting;
        _footPrompt.Text = Tool.Selecting ? "Boxing · click again to confirm, Z/X for levels"
            : Tool.PartFor(mode) is not null || mode == ToolMode.Structure || mode == ToolMode.Edge ? "Placing · click the map"
            : "Drag a box on the map: click a corner, then the other";
        G.Retone(_footPrompt, "Warn");
        if (Tool.PartFor(mode) is { } part && PartInfo.Sizes(part) > 1)
        {
            _footActions.AddChild(G.Text("Size", "Dim"));
            _footActions.AddChild(G.Button("−", () => Tool.ChangeSize(-1), "Smaller ([)"));
            _footActions.AddChild(G.Text($"{Tool.SizeFor(part)}/{PartInfo.Sizes(part)}", "Hi", bold: true, cols: 3, align: HorizontalAlignment.Center));
            _footActions.AddChild(G.Button("+", () => Tool.ChangeSize(1), "Bigger (])"));
        }
        if (Tool.PartFor(mode) is not null || mode == ToolMode.Structure)
            _footActions.AddChild(G.Button(" Rotate ", Tool.Rotate, "R"));
        if (placing) _footActions.AddChild(G.Button(" Cancel ", () => Tool.CancelPlacement(), "Esc"));
    }

    private string Describe(ToolMode mode) => mode switch
    {
        ToolMode.Dig => "Dig out rock and soil (stone 50%, clay, sand and ores always)",
        ToolMode.Stairs => "Stairs through a box of levels: down on top, up/down between, up at the bottom",
        ToolMode.Chop => "Fell every tree in an area, on the surface whatever level you view",
        ToolMode.Gather => "Gather ripe berries, fiber and herbs in an area",
        ToolMode.Stockpile => "Haulers bring loose goods here",
        ToolMode.FarmGrain or ToolMode.FarmHerbs or ToolMode.FarmFlax => "Sown and harvested by farmers; half again as fast within 2 tiles of water",
        ToolMode.FarmGlowcaps => "Glowcaps grow on any floor under a roof",
        ToolMode.Wall => "A full stone wall cell: dams and canals (stack them to raise it)",
        ToolMode.Room => Tool.Workshop.IsHome ? $"Paint a home: any size and levels, at least {Colony.Core.Agents.Housing.MinCells} cells" : $"Paint the {Tool.Workshop.Name.ToLowerInvariant()} room over floor, then place its parts",
        ToolMode.Outline => "Walls all round a box of floor",
        ToolMode.WallLine => "A straight line of pieces along cell edges",
        ToolMode.Floor => "Floor over a box (homes count it)",
        ToolMode.Roof => "Roof over a box of floor (homes count it)",
        ToolMode.Rack => $"Shelving over stockpile cells: up to {Stockpiles.Capacity(Stockpiles.MaxRackLevels)} stacks a cell (Shift-drag takes racks down)",
        ToolMode.TradeDepot => "Other colonies' traders deliver here; your haulers move it on to your stockpiles",
        ToolMode.Cancel => "Cancel designations and plans, remove zone cells in a box",
        ToolMode.Demolish => "Take down what is built in a box; walls on its outline too",
        ToolMode.None => "Pick something to build or order",
        _ => "",
    };

    // ---- What each tab offers ---------------------------------------------------------------------------

    private static Color Tone(string token) => GlyphTheme.Tok(token);
    private static Color Rgb(uint c) => new(((c >> 16) & 0xff) / 255f, ((c >> 8) & 0xff) / 255f, (c & 0xff) / 255f);

    private Item Order(string glyph, string tone, string name, ToolMode mode, string tip) =>
        new(glyph, Tone(tone), name, null, tip, () => Tool.Mode == mode, () => Tool.SelectOrder(mode));

    private List<Category> Categories() => _tab switch
    {
        0 =>
        [
            new("Mining", () =>
            [
                Order("▼", "ore", "Dig", ToolMode.Dig, "Dig out a box of rock and soil"),
                Order("≡", "stair", "Stairs (auto)", ToolMode.Stairs, "Down on top, up/down between, up at the bottom"),
                Order("▲", "stair", "Up stair", ToolMode.UpStair, "Carve an up stair"),
                Order("▼", "stair", "Down stair", ToolMode.DownStair, "Carve a down stair"),
                Order("÷", "stair", "Up/down stair", ToolMode.UpDownStair, "Carve an up/down stair"),
            ]),
            new("Plants", () =>
            [
                Order("♣", "pine", "Chop trees", ToolMode.Chop, "Fell trees in an area (logs, by tree size)"),
                Order("♦", "good", "Gather plants", ToolMode.Gather, "Gather ripe plants in an area (berries, fiber, herbs)"),
            ]),
            new("Dams", () => [Order("#", "wall", "Stone wall", ToolMode.Wall, "Full wall cells for dams and canals (1 stone each)")]),
            new("Remove", () =>
            [
                Order("×", "bad", "Cancel orders", ToolMode.Cancel, "Cancel designations and plans (walls, floors, parts not yet built) and remove zone cells in a box"),
                Order("▓", "warn", "Demolish", ToolMode.Demolish, "Take down everything built in a box (walls all round it, doors, windows, floors, roofs, racks, waterworks, room parts); materials are left"),
            ]),
        ],
        1 =>
        [
            new("Storage", () =>
            [
                Order("≡", "item", "Stockpile", ToolMode.Stockpile, "Zone floor where haulers store goods"),
                new Item("╥", Tone("wood"), "Storage rack", (Colony.Core.Items.ItemType.Plank, Constructions.Cost(StructureKind.Rack, Stockpiles.MaxRackLevels).Count),
                    $"Shelving on stockpile cells, up to {Stockpiles.MaxRackLevels} levels tall with a sliding ladder: {Stockpiles.Capacity(Stockpiles.MaxRackLevels)} stacks a cell " +
                    $"({Constructions.RackPlanksPerLevel} planks a level); drag a box over the zone, R turns the fronts",
                    () => Tool.Mode == ToolMode.Rack, () => Tool.SelectRack()),
                new Item("$", Tone("accent"), "Trade depot", null,
                    "Where other colonies' traders deliver what you buy from them (Trade, F6); your haulers carry it on to your stockpiles",
                    () => Tool.Mode == ToolMode.TradeDepot, () => Tool.SelectTradeDepot()),
            ]),
            new("Farms", () =>
            [
                Order("τ", "farm", "Grain plot", ToolMode.FarmGrain, "Wheat: grain and herbs, on grass or soil"),
                Order("♣", "good", "Herb plot", ToolMode.FarmHerbs, "Herbs, on grass or soil"),
                Order("‖", "info", "Flax plot", ToolMode.FarmFlax, "Flax: fiber (beds, thread, cloth, rope, paper), on grass or soil"),
                Order("°", "gem", "Glowcap plot", ToolMode.FarmGlowcaps, "Glowcaps on any floor under a roof"),
            ]),
        ],
        2 => new[] { Catalog.Home }.Concat(Catalog.Workshops).Select(w => new Category(w.Name, () => RoomItems(w))).ToList(),
        3 => Enumerable.Range(0, WallMaterials.Count).Select(m => new Category(WallMaterials.All[m].Name, () => WallItems(m))).ToList(),
        4 =>
        [
            new("Floors", () => Enumerable.Range(1, FloorMaterials.Count - 1).Select(m =>
            {
                var (name, _, tier, color) = FloorMaterials.All[m];
                var cost = Constructions.Cost(StructureKind.Floor, m);
                return new Item("_", Rgb(color), name, (cost.Material, cost.Count), $"{name}: {Catalog.FixtureTiers[tier]}, per cell",
                    () => Tool.Mode == ToolMode.Floor && Tool.FloorMaterial == m, () => Tool.SelectFloor(m));
            }).ToList()),
            new("Roofs", () => Enumerable.Range(1, RoofMaterials.Count - 1).Select(m =>
            {
                var (name, _, tier, color) = RoofMaterials.All[m];
                var cost = Constructions.Cost(StructureKind.Roof, m);
                return new Item("▲", Rgb(color), name + " roof", (cost.Material, cost.Count), $"{name} roof: {Catalog.FixtureTiers[tier]}, per cell",
                    () => Tool.Mode == ToolMode.Roof && Tool.RoofMaterial == m, () => Tool.SelectRoof(m));
            }).ToList()),
        ],
        _ =>
        [
            new("Waterworks", () => new[] { StructureKind.WaterWheel, StructureKind.Pump }.Select(k =>
            {
                var cost = Constructions.Cost(k);
                return new Item(k == StructureKind.WaterWheel ? "☼" : "↑", Tone("water"), Constructions.Name(k), (cost.Material, cost.Count),
                    k == StructureKind.WaterWheel ? "Makes power from water flowing through its cell (hang it in a fall)" : "Lifts water one level, using power",
                    () => Tool.Mode == ToolMode.Structure && Tool.Structure == k, () =>
                    {
                        Tool.SelectStructure(k);
                    });
            }).ToList()),
        ],
    };

    private List<Item> RoomItems(RoomType w)
    {
        var color = RoomView.RoomColor(w) with { A = 1f };
        var list = new List<Item>
        {
            new(w.IsHome ? "⌂" : "□", color, w.IsHome ? "Paint a home" : $"Paint {w.Name.ToLowerInvariant()} room", null,
                w.IsHome ? Catalog.Home.About : $"{w.About} · makes {string.Join(", ", w.Recipes.Select(r => r.Name.ToLowerInvariant()))}",
                () => Tool.Mode == ToolMode.Room && Tool.Workshop == w, () => Tool.SelectWorkshop(w)),
        };
        if (w.IsHome) return list;
        foreach (var part in w.Stations.Concat(Catalog.GenericParts))
        {
            var pt = part;
            int size = Tool.SizeFor(part);
            var (material, count) = PartInfo.Cost(part, size);
            string glyph = PartInfo.Role(part) switch { PartRole.Machine => "÷", PartRole.Auxiliary => "☼", PartRole.Input => "↓", _ => "↑" };
            string tip = part.Recipes.Count > 0 ? string.Join("; ", part.Recipes.Select(r => $"{r.Describe()} ({Catalog.EducationName(r.Education)})")) : PartInfo.Role(part).ToString();
            list.Add(new Item(glyph, PartInfo.Role(part) == PartRole.Machine ? Tone("warn") : Tone("dim"),
                PartInfo.Sizes(part) > 1 ? $"{part.Name} ({size})" : part.Name, (material, count), tip,
                () => Tool.Mode == ToolMode.Part && Tool.Part == pt, () => Tool.SelectPart(pt)));
        }
        return list;
    }

    private List<Item> WallItems(int m)
    {
        var (matName, item, _, quality, color) = WallMaterials.All[m];
        var c = Rgb(color);
        Item Piece(string glyph, string name, ToolMode mode, StructureKind piece, string tip)
        {
            var cost = Constructions.Cost(piece, m);
            return new Item(glyph, piece == StructureKind.EdgeWall ? c : Tone(piece == StructureKind.Door ? "wood" : "water2"), name, (cost.Material, cost.Count), tip,
                () => Tool.Mode == mode && (mode == ToolMode.Outline || Tool.EdgePiece == piece) && (piece != StructureKind.EdgeWall || Tool.WallMaterial == m),
                () => { Tool.SelectWallMaterial(m); Tool.SelectEdgeTool(mode, piece); });
        }
        return
        [
            Piece("□", $"{matName} walls round a box", ToolMode.Outline, StructureKind.EdgeWall, "Drag over the floor: walls on every edge round it (1 a wall)"),
            Piece("#", $"{matName} wall", ToolMode.Edge, StructureKind.EdgeWall, "Click the cell edge nearest the cursor; RMB removes"),
            Piece("≡", $"Line of {matName.ToLowerInvariant()} walls", ToolMode.WallLine, StructureKind.EdgeWall, "Click an edge, drag along it, click"),
            Piece("◘", "Door", ToolMode.Edge, StructureKind.Door, "Walkers and water pass; can be cut into a wall"),
            Piece("≡", "Line of doors", ToolMode.WallLine, StructureKind.Door, "Click an edge, drag along it, click"),
            Piece("°", "Window", ToolMode.Edge, StructureKind.Window, "Light, no way through; can be cut into a wall"),
            Piece("≡", "Line of windows", ToolMode.WallLine, StructureKind.Window, "Click an edge, drag along it, click"),
        ];
    }
}
