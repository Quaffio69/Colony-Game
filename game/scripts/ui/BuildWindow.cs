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
/// Build (B): every tool that changes the world, in four tabs. Orders (dig, stairs, plants, cancel and demolish), Zones
/// (stockpiles, racks, the trade depot, farms, homes), Structures (walls by material, doors and windows, floors,
/// roofs, stone blocks, waterworks) and Workshops (mark out the room, then place its stations and fittings). The left
/// column picks a category, the right an item: icon, name, hotkey and cost (red when short); hovering one explains
/// it. The footer names the tool in hand with its controls as key hints (the same as the tool card) and offers size,
/// rotation and Done. Names and icons come from <see cref="Tools"/>. Closing the window drops build tools.
/// </summary>
public partial class BuildWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private DesignationTool Tool => _ui.Tool;
    private int _tab, _cat;
    private string _drawn = "";
    private GlyphChip _footChip = null!;
    private Label _footName = null!, _footInfo = null!;
    private KeyHints _footKeys = null!;
    private HBoxContainer _footActions = null!;

    private sealed record Item(string Glyph, Color Color, string Name, string? Key, (ItemType Material, int Count)? Cost, string About, Func<bool> On, Action Pick);
    private sealed record Category(string Name, Func<List<Item>> Items);

    public static readonly string[] Tabs = ["Orders", "Zones", "Structures", "Workshops"];
    private const int WorkshopsTab = 3;

    public BuildWindow(GameUi ui) : base("build", "Build", "#", "warn", "B")
    {
        _ui = ui;
        MinCols = 60;
        MinRows = 14;
        Default = new WindowState { X = 2, Y = 18, W = 78, H = 20 };
        Closed += _ => Tool.LeaveBuild();
    }
    public BuildWindow() : this(null!) { }

    /// <summary>Opens on a workshop (--build-menu=ID).</summary>
    public void ShowWorkshop(string id)
    {
        _tab = WorkshopsTab;
        var cats = Categories();
        var ws = Catalog.Workshops.FirstOrDefault(w => w.Id == id);
        _cat = Math.Max(0, cats.FindIndex(c => c.Name == ws?.Name));
        if (ws != null) Tool.SelectWorkshop(ws);
        Rebuild();
    }

    protected override void Build()
    {
        Header.AddChild(new GlyphSegmented(Tabs, _tab, i => { _tab = i; _cat = 0; _drawn = ""; Tick(); })
        {
            Tips = ["Orders: dig, stairs, chop, gather, cancel, demolish", "Zones: stockpiles, racks, trade depot, farms, homes",
                "Structures: whole houses, walls, doors, windows, floors, roofs, stone blocks, waterworks, conveyors", "Workshops: mark out a workshop, then place its stations"],
        });
        Header.AddChild(G.Gap(0.25f));
        Footer.AddChild(G.Gap(0.25f));
        Footer.AddChild(new GlyphSection(""));
        var top = new HBoxContainer();
        _footChip = new GlyphChip("·", GlyphTheme.Tok("dim"));
        _footName = G.Text("", "Hi", bold: true);
        _footInfo = G.Text("", "Dim", expand: true);
        _footInfo.ClipText = true;
        top.AddChild(_footChip);
        top.AddChild(_footName);
        top.AddChild(G.Text("", cols: 1));
        top.AddChild(_footInfo);
        Footer.AddChild(top);
        var bottom = new HBoxContainer();
        _footKeys = new KeyHints { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _footActions = new HBoxContainer();
        bottom.AddChild(_footKeys);
        bottom.AddChild(_footActions);
        Footer.AddChild(bottom);
        _drawn = "";
    }

    private string StateKey() =>
        $"{_tab}/{_cat}/{Tool.Mode}/{Tool.Workshop.Id}/{Tool.Part.Index}/{Tool.Structure}/{Tool.EdgePiece}/{Tool.WallMaterial}/{Tool.FloorMaterial}/{Tool.RoofMaterial}/" +
        $"{(Tool.PartFor(Tool.Mode) is { } p ? Tool.SizeFor(p) : 0)}/{Tool.Rotated}/{Tool.Selecting}/{Tool.Removing}/{TotalsHash()}";

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
                var catRow = new GlyphRow { Clickable = true, Selected = i == _cat, CustomMinimumSize = new Vector2(24 * GlyphTheme.Cw, GlyphTheme.Ch) };
                catRow.AddChild(G.Text(cats[i].Name, i == _cat ? "Hi" : "Dim", bold: i == _cat, cols: 23));
                catRow.Pressed += () => { _cat = ci; _drawn = ""; Tick(); };
                row.AddChild(catRow);
            }
            else row.AddChild(G.Text("", cols: 24));
            row.AddChild(new VRule());
            row.AddChild(G.Text("", cols: 1));
            if (i < items.Count) row.AddChild(ItemRow(items[i]));
            Content.AddChild(row);
        }
    }

    private Control ItemRow(Item it)
    {
        bool on = it.On();
        var row = new GlyphRow { Clickable = true, Selected = on, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(new GlyphChip(it.Glyph, it.Color));
        row.AddChild(G.Text("", cols: 1));
        row.AddChild(G.Text(it.Name, on ? "Accent" : "Text", bold: on, expand: true));
        row.AddChild(G.Text(it.Key ?? "", on ? "Accent" : "Faint", bold: true, cols: 3, align: HorizontalAlignment.Center));
        if (it.Cost is { } cost)
        {
            bool enough = _ui.Count(cost.Material) >= cost.Count;
            row.AddChild(new GlyphChip(ItemLook.Glyph(cost.Material), ItemLook.Color(cost.Material)));
            row.AddChild(G.Text(cost.Count.ToString(), enough ? "Text" : "Bad", cols: 4, align: HorizontalAlignment.Right));
        }
        else row.AddChild(G.Text("", cols: 6));
        row.Pressed += () => { it.Pick(); _drawn = ""; Tick(); };
        string key = it.Key is { } k ? $"  ({(char.IsUpper(k[0]) ? "Shift+" + k : k.ToUpperInvariant())})" : "";
        string cost2 = it.Cost is { } c ? $"\nCosts {c.Count} {c.Material.Name.ToLowerInvariant()} · you have {_ui.Count(c.Material)}" : "";
        row.WithTip($"{it.Name}{key}\n{it.About}{cost2}");
        return row;
    }

    private void DrawFooter()
    {
        var mode = Tool.Mode;
        G.Clear(_footActions);
        if (mode == ToolMode.None && !Tool.Selecting)
        {
            _footChip.Set("·", GlyphTheme.Tok("faint"));
            _footName.Text = "Pick a tool";
            _footInfo.Text = "Hover one to read what it does; orders and zones also have hotkeys";
            _footKeys.Set([("Tab", "cycle orders"), ("B", "close")]);
            return;
        }
        var info = Tools.Current(Tool);
        _footChip.Set(info.Glyph, info.Color);
        _footName.Text = Tool.Removing ? "Remove: " + info.Name : info.Name;
        G.Retone(_footName, Tool.Removing ? "Bad" : "Hi", bold: true);
        _footInfo.Text = info.About;
        _footKeys.Set(Tool.IsArea && Tool.Selecting ? [("Click", "confirm"), ("RMB", "abort")] : info.How);
        if (Tool.PartFor(mode) is { } part && PartInfo.Sizes(part) > 1)
        {
            _footActions.AddChild(G.Text("Size ", "Dim"));
            _footActions.AddChild(G.Button("−", () => Tool.ChangeSize(-1), "Smaller ([)"));
            _footActions.AddChild(G.Text($"{Tool.SizeFor(part)}/{PartInfo.Sizes(part)}", "Hi", bold: true, cols: 4, align: HorizontalAlignment.Center));
            _footActions.AddChild(G.Button("+", () => Tool.ChangeSize(1), "Bigger (])"));
            _footActions.AddChild(G.Text("", cols: 1));
        }
        if (Tool.PartFor(mode) is not null || mode is ToolMode.Structure or ToolMode.Rack)
            _footActions.AddChild(G.Button(" Turn ", Tool.Rotate, "Turn it (R)"));
        _footActions.AddChild(G.Text("", cols: 1));
        _footActions.AddChild(G.Button(" Done ", () => Tool.CancelPlacement(), "Put the tool down (right click or Esc)"));
    }

    // ---- What each tab offers ---------------------------------------------------------------------------

    private static Color Tk(string token) => GlyphTheme.Tok(token);

    /// <summary>An order or zone tool, as <see cref="Tools"/> describes it.</summary>
    private Item Mode(ToolMode mode, Action? pick = null)
    {
        var info = Tools.Of(Tool, mode);
        return new Item(info.Glyph, info.Color, info.Name, Tools.KeyOf(mode), null, info.About, () => Tool.Mode == mode, pick ?? (() => Tool.SelectOrder(mode)));
    }

    private List<Category> Categories() => _tab switch
    {
        0 =>
        [
            new("Digging", () => [Mode(ToolMode.Dig), Mode(ToolMode.Stairs), Mode(ToolMode.UpStair), Mode(ToolMode.DownStair), Mode(ToolMode.UpDownStair)]),
            new("Plants", () => [Mode(ToolMode.Chop), Mode(ToolMode.Gather)]),
            new("Remove", () => [Mode(ToolMode.Cancel), Mode(ToolMode.Demolish)]),
        ],
        1 =>
        [
            new("Storage", () =>
            [
                Mode(ToolMode.Stockpile),
                Mode(ToolMode.Rack, Tool.SelectRack) with { Cost = (ItemType.Plank, Constructions.Cost(StructureKind.Rack, Stockpiles.MaxRackLevels).Count) },
                Mode(ToolMode.TradeDepot, Tool.SelectTradeDepot),
            ]),
            new("Farms", () => [Mode(ToolMode.FarmGrain), Mode(ToolMode.FarmHerbs) with { Key = null }, Mode(ToolMode.FarmFlax) with { Key = null }, Mode(ToolMode.FarmGlowcaps) with { Key = null }]),
            new("Homes", () =>
            [
                new Item("⌂", Tk("furn"), "Home", null, null,
                    $"{Catalog.Home.About} Mark out any shape, at least {Colony.Core.Agents.Housing.MinCells} cells; Housing (H) sets who lives where",
                    () => Tool.Mode == ToolMode.Room && Tool.Workshop.IsHome, () => Tool.SelectWorkshop(Catalog.Home)),
            ]),
        ],
        2 =>
        [
            new("Buildings", () => [Mode(ToolMode.House, Tool.SelectHouse)]),
            new("Walls", () => Enumerable.Range(0, WallMaterials.Count).Select(m =>
            {
                var cost = Constructions.Cost(StructureKind.EdgeWall, m);
                return new Item("#", Tools.WallColor(m), Tools.WallName(m), null, (cost.Material, cost.Count),
                    $"Stands on the edge between cells. Click for one, drag along an edge for a line, drag away from it to wall round a box · " +
                    $"{Catalog.FixtureTiers[Math.Clamp(WallMaterials.All[m].Quality + 1, 0, Catalog.FixtureTiers.Length - 1)].ToLowerInvariant()} for homes",
                    () => Tool.Mode == ToolMode.Edge && Tool.EdgePiece == StructureKind.EdgeWall && Tool.WallMaterial == m,
                    () => Tool.SelectWall(StructureKind.EdgeWall, m));
            }).ToList()),
            new("Doors & windows", () => new[] { StructureKind.Door, StructureKind.Window }.Select(k =>
            {
                var cost = Constructions.Cost(k);
                return new Item(k == StructureKind.Door ? "◘" : "°", Tk(k == StructureKind.Door ? "wood" : "water2"), Constructions.Name(k), null, (cost.Material, cost.Count),
                    k == StructureKind.Door ? "People and water pass through. Click an edge, or drag along one for a row; it can be cut into a built wall"
                        : "Lets light in, nothing through. Click an edge, or drag along one for a row; it can be cut into a built wall",
                    () => Tool.Mode == ToolMode.Edge && Tool.EdgePiece == k, () => Tool.SelectWall(k, Tool.WallMaterial));
            }).ToList()),
            new("Floors", () => Enumerable.Range(1, FloorMaterials.Count - 1).Select(m =>
            {
                var cost = Constructions.Cost(StructureKind.Floor, m);
                return new Item("_", Tools.Rgb(FloorMaterials.All[m].Color), Tools.FloorName(m), null, (cost.Material, cost.Count),
                    $"Floor over a box of ground, one {cost.Material.Name.ToLowerInvariant().TrimEnd('s')} a cell · {Catalog.FixtureTiers[FloorMaterials.All[m].Tier].ToLowerInvariant()} for homes",
                    () => Tool.Mode == ToolMode.Floor && Tool.FloorMaterial == m, () => Tool.SelectFloor(m));
            }).ToList()),
            new("Roofs", () => Enumerable.Range(1, RoofMaterials.Count - 1).Select(m =>
            {
                var cost = Constructions.Cost(StructureKind.Roof, m);
                return new Item("▲", Tools.Rgb(RoofMaterials.All[m].Color), Tools.RoofName(m), null, (cost.Material, cost.Count),
                    $"Roof over a box of floor, per cell · {Catalog.FixtureTiers[RoofMaterials.All[m].Tier].ToLowerInvariant()} for homes; glowcaps grow under any roof",
                    () => Tool.Mode == ToolMode.Roof && Tool.RoofMaterial == m, () => Tool.SelectRoof(m));
            }).ToList()),
            new("Stone blocks", () => [Mode(ToolMode.Block) with { Cost = (ItemType.Stone, 1) }]),
            new("Conveyors", () =>
            [
                Mode(ToolMode.Conveyor, () => Tool.SelectConveyor(false)) with { Cost = (ItemType.Plank, 1) },
                Mode(ToolMode.Splitter, () => Tool.SelectConveyor(true)) with { Cost = (Catalog.Item("wooden_gears"), 1) },
            ]),
            new("Waterworks", () => new[] { StructureKind.WaterWheel, StructureKind.Pump }.Select(k =>
            {
                var cost = Constructions.Cost(k);
                bool wheel = k == StructureKind.WaterWheel;
                return new Item(wheel ? "☼" : "↑", Tk("water"), Constructions.Name(k), null, (cost.Material, cost.Count),
                    wheel ? "Makes power from water flowing through its cell; hang it on a wall in a waterfall. R turns it"
                        : "Lifts water one level, using power from water wheels. R turns it",
                    () => Tool.Mode == ToolMode.Structure && Tool.Structure == k, () => Tool.SelectStructure(k));
            }).ToList()),
        ],
        _ => Catalog.Workshops.Select(w => new Category(w.Name, () => WorkshopItems(w))).ToList(),
    };

    private List<Item> WorkshopItems(RoomType w)
    {
        var list = new List<Item>
        {
            new("□", RoomView.RoomColor(w) with { A = 1f }, $"Mark out the {w.Name}", null, null,
                $"{w.About} Drag a box over floor for the workshop, then place its stations below. Makes {string.Join(", ", w.Recipes.Select(r => r.Name.ToLowerInvariant()))}",
                () => Tool.Mode == ToolMode.Room && Tool.Workshop == w, () => Tool.SelectWorkshop(w)),
        };
        foreach (var part in w.Stations.Concat(Catalog.GenericParts))
        {
            var pt = part;
            int size = Tool.SizeFor(part);
            var (material, count) = PartInfo.Cost(part, size);
            var role = PartInfo.Role(part);
            string about = part.Recipes.Count > 0
                ? $"Made of {material.Name.ToLowerInvariant()}. " + string.Join("; ", part.Recipes.Select(r => $"{r.Describe()} ({Catalog.EducationName(r.Education)})"))
                : $"Made of {material.Name.ToLowerInvariant()}. " + role switch
                {
                    PartRole.Auxiliary => $"Speeds up machines nearby (+{PartInfo.Bonus(part, size):P0})",
                    PartRole.Input => "Holds one of the workshop's inputs",
                    _ => "Holds the workshop's products",
                };
            list.Add(new Item(Tools.PartGlyph(part), role == PartRole.Machine ? Tk("warn") : Tk("dim"),
                PartInfo.Sizes(part) > 1 ? $"{PartInfo.Name(part, size)} ({size}/{PartInfo.Sizes(part)})" : part.Name, null, (material, count), about,
                () => Tool.Mode == ToolMode.Part && Tool.Part == pt, () => Tool.SelectPart(pt)));
        }
        return list;
    }
}
