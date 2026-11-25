using System.Linq;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Items;
using Godot;
using ToolMode = ColonyGame.DesignationTool.ToolMode;

namespace ColonyGame.UI;

/// <summary>
/// Dev mode (F7, or the main menu): switches it on and off (single-player games), whether orders finish at once, and
/// a spawn list of everything in the catalog (people and machines first): pick one and a count, then click the map
/// to drop it there. While it is on, everything built stands at once, for free, and anyone runs any recipe.
/// </summary>
public partial class DevWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private string _drawn = "";
    private int _count = 1;
    private static readonly int[] Counts = [1, 10, 50, 200];

    public DevWindow(GameUi ui) : base("dev", "Dev mode", "!", "warn", "F7")
    {
        _ui = ui;
        MinCols = 36;
        Default = new WindowState { X = -2, Y = 3, W = 44, H = 34 };
    }
    public DevWindow() : this(null!) { }

    protected override void Build() => _drawn = "";

    private bool Allowed => _ui.Sim.Factions.Count == 1 && !_ui.Main.Online;

    public override void Tick()
    {
        var sim = _ui.Sim;
        var tool = _ui.Tool;
        string key = $"{sim.DevMode}|{sim.DevInstantOrders}|{_count}|{(tool.Mode == ToolMode.Spawn ? tool.SpawnItem : "")}|{Allowed}";
        if (key == _drawn) return;
        _drawn = key;
        G.Clear(Content);

        Content.AddChild(new GlyphSection("Dev mode"));
        if (!Allowed)
        {
            Content.AddChild(G.Para("Dev mode is for single-player games.", "Faint"));
            return;
        }
        Content.AddChild(new GlyphSwitch("Dev mode", sim.DevMode, on => Set(on, sim.DevInstantOrders || !sim.DevMode)));
        if (!sim.DevMode)
        {
            Content.AddChild(G.Para("Build anything to try it: whatever you place stands at once, with no materials, and " +
                                    "anyone can run any recipe. Spawn items, machines and colonists too.", "Faint"));
            return;
        }
        Content.AddChild(new GlyphSwitch("Orders done at once", sim.DevInstantOrders, on => Set(true, on)));
        Content.AddChild(G.Para("Blueprints stand the moment they are placed, for free; recipes need no schooling." +
                                (sim.DevInstantOrders ? " Digging, stairs, chopping and gathering finish at once (top level first)." : ""), "Faint"));

        Content.AddChild(G.Gap());
        Content.AddChild(new GlyphSection("Spawn"));
        var row = new HBoxContainer();
        row.AddChild(G.Text("How many", "Dim", expand: true));
        row.AddChild(new GlyphSegmented(Counts.Select(c => c.ToString()).ToArray(), System.Array.IndexOf(Counts, _count), i =>
        {
            _count = Counts[i];
            if (_ui.Tool.Mode == ToolMode.Spawn) _ui.Tool.SelectSpawn(_ui.Tool.SpawnItem, _count);
            _drawn = ""; // redrawn on the next tick, not inside this click
        }));
        Content.AddChild(row);
        Content.AddChild(G.Para("Pick one, then click the floor to drop it there (right click: done).", "Faint"));

        Content.AddChild(G.Gap(0.4f));
        Content.AddChild(new GlyphSection("People and machines"));
        Spawnable("colonist", "☺", GlyphTheme.Tok("accent"), "Colonist", "A new settler");
        foreach (var t in Catalog.Items.Where(d => d.Kind is ItemKind.Worker or ItemKind.Machine).Select(d => d.Type))
            Spawnable(t.Def.Id, ItemLook.Glyph(t), ItemLook.Color(t), t.Name, t.Def.Kind == ItemKind.Machine ? "Rolls out fuelled" : "An automaton worker");

        foreach (var group in Catalog.Items.Where(d => d.Kind == ItemKind.Good).GroupBy(d => d.Category))
        {
            Content.AddChild(G.Gap(0.4f));
            Content.AddChild(new GlyphSection(char.ToUpperInvariant(group.Key[0]) + group.Key[1..]));
            foreach (var d in group)
                Spawnable(d.Id, ItemLook.Glyph(d.Type), ItemLook.Color(d.Type), d.Name, $"{d.Weight:0.#} kg each, {d.CarryLimit} a load");
        }
    }

    private void Set(bool on, bool instantOrders)
    {
        _ui.Tool.Issue(new SetDevMode(on, instantOrders));
        if (!on && _ui.Tool.Mode == ToolMode.Spawn) _ui.Tool.SelectOrder(ToolMode.None);
    }

    private void Spawnable(string id, string glyph, Color colour, string name, string tip)
    {
        var r = new GlyphRow { Clickable = true, Selected = _ui.Tool.Mode == ToolMode.Spawn && _ui.Tool.SpawnItem == id };
        r.AddChild(new GlyphChip(glyph, colour));
        r.AddChild(G.Text(name, expand: true));
        r.Pressed += () => _ui.Tool.SelectSpawn(id, _count);
        r.MouseEntered += () => GlyphStatus.Tip = $"{name}: {tip} · click, then click the floor";
        r.MouseExited += () => GlyphStatus.Tip = null;
        Content.AddChild(r);
    }
}
