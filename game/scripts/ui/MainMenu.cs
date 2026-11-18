using System;
using Godot;

namespace ColonyGame.UI;

/// <summary>
/// The main menu: a solid backdrop (placeholder until there is title art) with Continue, New colony (seed and map
/// size), Multiplayer (not yet), Appearance, Tests & demos and Quit. Esc opens it when nothing else is left to close;
/// the game doesn't start on it yet (that would slow testing). The sim waits while it is up.
/// </summary>
public partial class MainMenu : Control
{
    private readonly GameUi _ui;
    private readonly ColorRect _backdrop = new();
    private readonly GlyphPanel _panel = new() { Opaque = true };
    private readonly VBoxContainer _box = new();
    private bool _newColony;
    private int _size = 1;
    private LineEdit? _seed;
    private static readonly int[] Sizes = [512, 1024, 2048];

    public MainMenu(GameUi ui)
    {
        _ui = ui;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.FullRect);
        _backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        _backdrop.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_backdrop);
        AddChild(_panel);
        _box.MouseFilter = MouseFilterEnum.Ignore;
        _panel.AddChild(_box);
        GlyphTheme.Changed += Restyle;
    }
    public MainMenu() : this(null!) { }

    public override void _ExitTree() => GlyphTheme.Changed -= Restyle;

    private void Restyle()
    {
        if (Visible) Build();
    }

    public void Show(bool show)
    {
        Visible = show;
        _newColony = false;
        if (show) Build();
    }

    private void Build()
    {
        _backdrop.Color = GlyphTheme.Tok("bg");
        G.Clear(_box);
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;

        var title = new Label { Text = "COLONY", HorizontalAlignment = HorizontalAlignment.Center, ThemeTypeVariation = "AccentBold" };
        title.AddThemeFontSizeOverride("font_size", GlyphTheme.FontSize * 4);
        _box.AddChild(title);
        var sub = G.Text("dig deep · build big · grow a people", "Dim", align: HorizontalAlignment.Center);
        _box.AddChild(sub);
        _box.AddChild(G.Gap(1.5f));

        if (_newColony) BuildNewColony();
        else
        {
            Item("Continue", "Primary", () => Show(false), "Back to the colony (Esc)");
            Item("New colony", "", () => { _newColony = true; Build(); }, "A fresh world: pick a seed and a map size");
            var mp = Item("Multiplayer", "", () => { }, "Coming: several players' colonies in one world, to trade, ally or fight");
            mp.Disabled = true;
            _box.AddChild(G.Text("Coming: several colonies in one world", "Faint", align: HorizontalAlignment.Center));
            _box.AddChild(G.Gap(0.5f));
            Item("Appearance", "", () => { Show(false); _ui.OpenWindow("appearance"); }, "Themes, fonts and effects (F2)");
            Item("Tests & demos", "", () => { Show(false); _ui.OpenWindow("tests"); }, "Developer: run tests, build demos (F1)");
            Item("Quit", "", () => GetTree().Quit(), "Leave the game (nothing is saved yet)");
        }

        // Centre the panel; its height follows its content.
        _box.Position = new Vector2(3 * cw, 1.5f * ch);
        _box.Size = new Vector2(38 * cw, 0);
        Callable.From(Layout).CallDeferred();
    }

    private void Layout()
    {
        float cw = GlyphTheme.Cw, ch = GlyphTheme.Ch;
        var inner = _box.GetCombinedMinimumSize();
        var size = new Vector2(44 * cw, inner.Y + 3 * ch);
        _panel.Size = size;
        _panel.Position = ((GetViewportRect().Size - size) / 2).Floor();
        _box.Size = new Vector2(38 * cw, inner.Y);
    }

    private Button Item(string text, string variation, Action pressed, string tip)
    {
        var b = G.Button(text, pressed, tip, variation);
        b.CustomMinimumSize = new Vector2(30 * GlyphTheme.Cw, GlyphTheme.Ch * 1.2f);
        b.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _box.AddChild(b);
        _box.AddChild(G.Gap(0.35f));
        return b;
    }

    private void BuildNewColony()
    {
        _box.AddChild(new GlyphSection("New colony"));
        var seedRow = new HBoxContainer();
        seedRow.AddChild(G.Text("Seed", "Dim", cols: 6));
        _seed = new LineEdit { Text = new Random().Next(1, 999_999).ToString(), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        seedRow.AddChild(_seed);
        seedRow.AddChild(G.Button(" Random ", () => _seed.Text = new Random().Next(1, 999_999).ToString(), "Roll a new seed"));
        _box.AddChild(seedRow);
        _box.AddChild(G.Gap(0.35f));
        var sizeRow = new HBoxContainer();
        sizeRow.AddChild(G.Text("Map", "Dim", cols: 6));
        sizeRow.AddChild(new GlyphSegmented(Array.ConvertAll(Sizes, s => $"{s}²"), _size, i => _size = i)
        {
            Tips = ["Small: quick to make, good for trying things", "Medium", "Large: the full world (takes a few seconds)"],
        });
        _box.AddChild(sizeRow);
        _box.AddChild(G.Gap(1));
        Item("Start", "Primary", () =>
        {
            int seed = int.TryParse(_seed!.Text.Trim(), out int s) ? s : _seed.Text.GetHashCode();
            _ui.Main.NewColony(seed, Sizes[_size]);
        }, "Make the world and found the colony (the current one is not saved)");
        Item("Back", "", () => { _newColony = false; Build(); }, "Back to the menu");
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton) AcceptEvent();
    }
}
