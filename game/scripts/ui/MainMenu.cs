using System;
using System.Linq;
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
    private string _page = "";
    private string? _problem;
    private int _hostColonies = 2;
    private LineEdit? _name, _address, _port;
    private int _size = 1, _colonies = 1;
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

    public void Show(bool show, string page = "")
    {
        Visible = show;
        _page = page;
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
        _box.AddChild(G.Text($"v{GameInfo.Version}", "Faint", align: HorizontalAlignment.Center));
        _box.AddChild(G.Gap(1.5f));

        if (_page == "loading" || _ui.Main.Loading) BuildLoading();
        else if (_page == "save") BuildSave();
        else if (_page == "load") BuildLoad();
        else if (_page == "multiplayer") BuildMultiplayer();
        else if (_page == "new") BuildNewColony();
        else
        {
            Item("Continue", "Primary", () => Show(false), "Back to the colony (Esc)");
            Item("New colony", "", () => Go("new"), "A fresh world: pick a seed and a map size");
            var save = Item("Save game", "", () => Go("save"), _ui.Main.Shareable ? "Save this game (F5 quicksaves)" : _ui.Main.Client != null ? "Only the host saves an online game" : "A test's world can't be saved");
            save.Disabled = !_ui.Main.Shareable || _ui.Main.Client != null;
            Item("Load game", "", () => Go("load"), "Pick up a saved game (F9 loads the newest)");
            Item(_ui.Main.Online ? "Multiplayer (online)" : "Multiplayer", "", () => Go("multiplayer"), "Several players' colonies in one world: host a game or join one");
            _box.AddChild(G.Gap(0.5f));
            Item("Appearance", "", () => { Show(false); _ui.OpenWindow("appearance"); }, "Themes, fonts and effects (F2)");
            if (GameInfo.DevBuild) Item("Tests & demos", "", () => { Show(false); _ui.OpenWindow("tests"); }, "Developer: run tests, build demos (F1)");
            Item("Quit", "", () => GetTree().Quit(), "Leave the game (nothing is saved yet)");
        }

        // Centre the panel; its height follows its content.
        _box.Position = new Vector2(3 * cw, 1.5f * ch);
        _box.Size = new Vector2(38 * cw, 0);
        Callable.From(Layout).CallDeferred();
    }

    private void Layout()
    {
        if (!IsInsideTree()) return; // the scene reloaded (a game joined) before this deferred call ran
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

    /// <summary>
    /// Multiplayer: your name; host this world (when others can rebuild it and it has free colonies) or a new one for
    /// 2-4 colonies; join a host by address. Online: who is in, and Leave.
    /// </summary>
    private void Go(string page)
    {
        _page = page;
        _problem = null;
        Build();
    }

    private static string Ago(long unixSeconds)
    {
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        return age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago" : $"{(int)age.TotalDays} d ago";
    }

    private static string Describe(Colony.Core.Net.SaveGame s) =>
        $"day {s.Tick / Colony.Core.Agents.Needs.Day + 1} · {s.Settings.Width}² · {G.Plural(s.Settings.Colonies, "colony", "colonies")}";

    /// <summary>Save: a name (blank: the colony and day), Save; the saves there are, to overwrite one by name.</summary>
    private void BuildSave()
    {
        var main = _ui.Main;
        _box.AddChild(new GlyphSection("Save game"));
        var row = new HBoxContainer();
        row.AddChild(G.Text("Name", "Dim", cols: 9));
        var name = new LineEdit { PlaceholderText = $"{_ui.MyFaction.Name}, day {_ui.Sim.Tick / Colony.Core.Agents.Needs.Day + 1}", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(name);
        _box.AddChild(row);
        _box.AddChild(G.Gap(0.5f));
        Item("Save", "Primary", () =>
        {
            _problem = main.SaveAs(name.Text);
            if (_problem == null)
            {
                GlyphStatus.Say("Saved");
                Show(false);
            }
            else Build();
        }, "Saves the world's recipe and every order given: small, and exact when loaded");
        var saves = main.ListSaves();
        if (saves.Count > 0)
        {
            _box.AddChild(G.Gap(0.5f));
            _box.AddChild(new GlyphSection("Overwrite"));
            foreach (var (_, header) in saves.Take(6))
            {
                var h = header;
                var r = new GlyphRow { Clickable = true };
                r.AddChild(G.Text(h.Name, cols: 18));
                r.AddChild(G.Text(Ago(h.SavedAt), "Faint", expand: true, align: HorizontalAlignment.Right));
                r.Pressed += () => name.Text = h.Name;
                r.MouseEntered += () => GlyphStatus.Tip = $"{h.Name}: {Describe(h)} · click to save over it";
                r.MouseExited += () => GlyphStatus.Tip = null;
                _box.AddChild(r);
            }
        }
        if (_problem != null) _box.AddChild(G.Para(_problem, "Bad"));
        _box.AddChild(G.Gap(0.5f));
        Item("Back", "", () => Go(""), "Back to the menu");
    }

    /// <summary>Load: every save, newest first (name, day, map, when), Load or delete (×).</summary>
    private void BuildLoad()
    {
        var main = _ui.Main;
        _box.AddChild(new GlyphSection("Load game"));
        var saves = main.ListSaves();
        if (saves.Count == 0) _box.AddChild(G.Para("No saves yet. Save from this menu, or press F5 in a game.", "Faint"));
        foreach (var (file, header) in saves.Take(12))
        {
            var (f, h) = (file, header);
            var r = new GlyphRow { Clickable = true };
            r.AddChild(new GlyphChip("≡", h.Name is "Autosave" or "Quicksave" ? "dim" : "accent"));
            var text = new VBoxContainer();
            text.AddChild(G.Text(h.Name, "Text", cols: 22));
            r.AddChild(text);
            r.AddChild(G.Text(Ago(h.SavedAt), "Faint", expand: true, align: HorizontalAlignment.Right));
            var del = G.Button("×", () => { main.DeleteSave(f); Build(); }, "Delete this save", "Ghost");
            r.AddChild(del);
            r.Pressed += () =>
            {
                _problem = main.LoadSave(f);
                Go(_problem == null ? "loading" : "load");
            };
            r.MouseEntered += () => GlyphStatus.Tip = $"{h.Name}: {Describe(h)} · click to load";
            r.MouseExited += () => GlyphStatus.Tip = null;
            _box.AddChild(r);
            _box.AddChild(G.Text("   " + Describe(h), "Faint"));
        }
        if (_problem != null) _box.AddChild(G.Para(_problem, "Bad"));
        _box.AddChild(G.Gap(0.5f));
        Item("Back", "", () => Go(""), "Back to the menu");
    }

    private GlyphBar? _loadBar;

    /// <summary>Loading: the save's name and a progress bar (the world is rebuilt in the background).</summary>
    private void BuildLoading()
    {
        _box.AddChild(new GlyphSection("Loading"));
        _box.AddChild(G.Text(_ui.Main.LoadingName ?? "", "Hi", bold: true, align: HorizontalAlignment.Center));
        _box.AddChild(G.Gap(0.5f));
        _loadBar = new GlyphBar(_ui.Main.LoadProgress, 0, "accent", "  0%");
        _box.AddChild(_loadBar);
        _box.AddChild(G.Para("Making the world and replaying every order given. Long games take a little while.", "Faint"));
    }

    public override void _Process(double delta)
    {
        if (!Visible || _loadBar == null || !IsInstanceValid(_loadBar) || !_loadBar.IsInsideTree()) return;
        float p = _ui.Main.LoadProgress;
        _loadBar.Set(p, $"{p * 100,3:0}%", "accent");
    }

    private void BuildMultiplayer()
    {
        var main = _ui.Main;
        _box.AddChild(new GlyphSection("Multiplayer"));
        if (main.Online)
        {
            _box.AddChild(G.Text(main.NetStatus, "Accent", align: HorizontalAlignment.Center));
            var players = main.Host?.Players ?? main.Client?.Players ?? [];
            foreach (var p in players)
                _box.AddChild(new GlyphKV("☺", ColonistView.FactionColor(p.Faction), p.Name, _ui.Sim.Factions[p.Faction].Name));
            _box.AddChild(G.Gap(0.5f));
            Item("Leave", "", () => { main.LeaveOnline(); Build(); }, "Stop hosting, or leave the game you joined (this world stays open, offline)");
            Item("Back", "", () => Go(""), "Back to the menu");
            return;
        }

        LineEdit Field(string label, string text, string tip)
        {
            var row = new HBoxContainer();
            row.AddChild(G.Text(label, "Dim", cols: 9));
            var field = new LineEdit { Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            field.WithTip(tip);
            row.AddChild(field);
            _box.AddChild(row);
            _box.AddChild(G.Gap(0.25f));
            return field;
        }

        _name = Field("Name", main.PlayerName, "What other players see");
        _port = Field("Port", Main.DefaultPort.ToString(), "The TCP port the host listens on (forward it on your router to play over the internet)");
        int Port() => int.TryParse(_port!.Text.Trim(), out int p) && p is > 0 and < 65536 ? p : Main.DefaultPort;
        void TakeName() { if (_name!.Text.Trim() is { Length: > 0 } n) main.PlayerName = n; }

        _box.AddChild(G.Gap(0.5f));
        _box.AddChild(new GlyphSection("Host"));
        var hostHere = Item("Host this world", "Primary", () =>
        {
            TakeName();
            _problem = main.HostGame(Port());
            Build();
        }, main.Shareable && _ui.Sim.Factions.Count > 1
            ? $"Others join this world as its other colonies ({_ui.Sim.Factions.Count - 1} free)"
            : "Only a world made by New colony (or Host a new world) with room for guests can be shared");
        hostHere.Disabled = !main.Shareable || _ui.Sim.Factions.Count < 2;
        var coloniesRow = new HBoxContainer();
        coloniesRow.AddChild(G.Text("Players", "Dim", cols: 9));
        coloniesRow.AddChild(new GlyphSegmented(["2", "3", "4"], _hostColonies - 2, i => _hostColonies = i + 2) { Tips = ["You and one guest", "You and two guests", "You and three guests"] });
        _box.AddChild(coloniesRow);
        _box.AddChild(G.Gap(0.35f));
        Item("Host a new world", "", () =>
        {
            TakeName();
            main.HostNewGame(new Random().Next(1, 999_999), Sizes[_size], _hostColonies, Port());
        }, $"A fresh {Sizes[_size]}² world with a colony for each player (map size as in New colony)");

        _box.AddChild(G.Gap(0.5f));
        _box.AddChild(new GlyphSection("Join"));
        _address = Field("Address", "127.0.0.1", "The host's address (their IP, or 127.0.0.1 for a game on this computer)");
        Item("Join", "Primary", () =>
        {
            TakeName();
            _problem = main.JoinGame(_address!.Text.Trim(), Port());
            if (_problem != null) Build();
        }, "Connect, receive the game so far and take a free colony (building the world takes a moment)");
        if (_problem != null) _box.AddChild(G.Para(_problem, "Bad"));
        _box.AddChild(G.Gap(0.5f));
        Item("Back", "", () => Go(""), "Back to the menu");
    }

    private void BuildNewColony()
    {
        _box.AddChild(new GlyphSection("New colony"));
        var seedRow = new HBoxContainer();
        seedRow.AddChild(G.Text("Seed", "Dim", cols: 9));
        _seed = new LineEdit { Text = new Random().Next(1, 999_999).ToString(), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        seedRow.AddChild(_seed);
        seedRow.AddChild(G.Button(" Random ", () => _seed.Text = new Random().Next(1, 999_999).ToString(), "Roll a new seed"));
        _box.AddChild(seedRow);
        _box.AddChild(G.Gap(0.35f));
        var sizeRow = new HBoxContainer();
        sizeRow.AddChild(G.Text("Map", "Dim", cols: 9));
        sizeRow.AddChild(new GlyphSegmented(Array.ConvertAll(Sizes, s => $"{s}²"), _size, i => _size = i)
        {
            Tips = ["Small: quick to make, good for trying things", "Medium", "Large: the full world (takes a few seconds)"],
        });
        _box.AddChild(sizeRow);
        _box.AddChild(G.Gap(0.35f));
        var coloniesRow = new HBoxContainer();
        coloniesRow.AddChild(G.Text("Colonies", "Dim", cols: 9));
        coloniesRow.AddChild(new GlyphSegmented(["1", "2", "3", "4"], _colonies - 1, i => _colonies = i + 1)
        {
            Tips = ["Just yours", "Two colonies in one world (F4 switches which you play, until online play)", "Three colonies", "Four colonies"],
        });
        _box.AddChild(coloniesRow);
        _box.AddChild(G.Gap(1));
        Item("Start", "Primary", () =>
        {
            int seed = int.TryParse(_seed!.Text.Trim(), out int s) ? s : _seed.Text.GetHashCode();
            _ui.Main.NewColony(seed, Sizes[_size], _colonies);
        }, "Make the world and found the colony (the current one is not saved)");
        Item("Back", "", () => Go(""), "Back to the menu");
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton) AcceptEvent();
    }
}
