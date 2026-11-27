using System;
using System.Linq;
using Colony.Core.Procedural;
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
    private static readonly int[] Sizes = [512, 1024, 2048, 4096, 8192];

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
        if (!show && _ui.Main.Generating) return; // the loading screen stays until the new world opens
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

        if (_ui.Main.Generating) BuildGenerating();
        else if (_page == "loading" || _ui.Main.Loading) BuildLoading();
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
            var sim = _ui.Sim;
            bool solo = sim.Factions.Count == 1 && !_ui.Main.Online;
            var dev = Item(sim.DevMode ? "Dev mode (on)" : "Dev mode", "", () =>
            {
                if (!sim.DevMode) _ui.Tool.Issue(new Colony.Core.Commands.SetDevMode(true, true));
                Show(false);
                _ui.OpenWindow("dev");
            }, solo ? "Build anything to test it: instant, free construction, orders done at once, spawn items and machines (F7)" : "Dev mode is for single-player games");
            dev.Disabled = !solo;
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
        float width = Mathf.Max(38 * cw, Mathf.Ceil(inner.X / cw) * cw); // at least 38 columns, wider when a row needs it
        var size = new Vector2(width + 6 * cw, inner.Y + 3 * ch);
        _panel.Size = size;
        _panel.Position = ((GetViewportRect().Size - size) / 2).Floor();
        _box.Size = new Vector2(width, inner.Y);
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
        if (Visible && _ui.Main.Generating) UpdateGenerating();
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
            // Every colony: who plays it, and whether they're here. A colony held for an absent player waits for them
            // (their own again when they rejoin, even from a save); the host can free it for a newcomer.
            foreach (var colony in _ui.Sim.Factions)
            {
                var f = colony;
                bool here = players.Any(p => p.Faction == f.Id);
                var row = new HBoxContainer();
                row.AddChild(new GlyphKV("☺", ColonistView.FactionColor(f.Id), f.Name,
                    here ? $"{players.First(p => p.Faction == f.Id).Name} · here" : f.Claimed ? $"{f.Player} · away" : "free")
                { SizeFlagsHorizontal = SizeFlags.ExpandFill });
                if (main.Host is { } host && !here && f.Claimed && f.Id > 0)
                    row.AddChild(G.Button(" Free ", () => { host.Free(f.Id); Build(); }, $"Stop holding {f.Name} for {f.Player}: the next player to join may take it"));
                _box.AddChild(row);
            }
            _box.AddChild(G.Para("A player who leaves gets their own colony back when they rejoin, even after the game is saved and hosted again.", "Faint"));
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

        var prefs = MultiplayerPrefs.Current;
        _name = Field("Name", main.PlayerName, "What other players see (hosts know you by this computer's key, so changing it keeps your colony)");
        _port = Field("Port", (prefs.LastPort > 0 ? prefs.LastPort : Main.DefaultPort).ToString(), "The TCP port the host listens on (forward it on your router to play over the internet)");
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
            ? $"Others join this world as its other colonies ({_ui.Sim.Factions.Skip(1).Count(c => !c.Claimed)} free" + (_ui.Sim.Factions.Skip(1).Any(c => c.Claimed) ? $"; held for {string.Join(", ", _ui.Sim.Factions.Skip(1).Where(c => c.Claimed).Select(c => c.Player))}, theirs again when they rejoin)" : ")")
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
        if (prefs.LastAddress.Length > 0)
        {
            Item($"Rejoin {prefs.LastAddress}:{prefs.LastPort}", "Primary", () =>
            {
                TakeName();
                _problem = main.JoinGame(prefs.LastAddress, prefs.LastPort);
                if (_problem != null) Build();
            }, "Back to the last game you joined: you get your own colony again");
            _box.AddChild(G.Gap(0.35f));
        }
        _address = Field("Address", prefs.LastAddress.Length > 0 ? prefs.LastAddress : "127.0.0.1", "The host's address (their IP, or 127.0.0.1 for a game on this computer)");
        Item("Join", "Primary", () =>
        {
            TakeName();
            _problem = main.JoinGame(_address!.Text.Trim(), Port());
            if (_problem != null) Build();
        }, "Connect, receive the game so far and take your colony (the one you played before, else a free one; building the world takes a moment)");
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
            Tips = ["Small: quick to make, good for trying things", "Medium", "Large (takes a few seconds)",
                "Huge: four times Large (about 10 s to make, 2.5 GB of memory; far water rests until someone comes near)",
                "Vast: sixteen times Large (about 2 minutes to make, 10 GB of memory: for strong machines)"],
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
        _box.AddChild(G.Gap(0.5f));
        BuildWorldOptions();
        _box.AddChild(G.Gap(1));
        Item("Start", "Primary", () =>
        {
            int seed = int.TryParse(_seed!.Text.Trim(), out int s) ? s : _seed.Text.GetHashCode();
            _ui.Main.NewColony(seed, Sizes[_size], _colonies);
            Build();
        }, "Make the world and found the colony (the current one is not saved)");
        Item("Back", "", () => Go(""), "Back to the menu");
    }

    /// <summary>
    /// The world options (Main.WorldOptions, kept for the session): climate, rain, seas, mountains and rivers. Each is
    /// one segmented row; the tips say what the generator does with it.
    /// </summary>
    private void BuildWorldOptions()
    {
        _box.AddChild(new GlyphSection("World"));
        var shape = Main.WorldOptions;
        void Row<T>(string label, T current, string[] names, string[] tips, Func<WorldShape, T, WorldShape> set) where T : struct, Enum
        {
            var row = new HBoxContainer();
            row.AddChild(G.Text(label, "Dim", cols: 9));
            var values = Enum.GetValues<T>();
            row.AddChild(new GlyphSegmented(names, Array.IndexOf(values, current), i => Main.WorldOptions = set(Main.WorldOptions, values[i])) { Tips = tips });
            _box.AddChild(row);
            _box.AddChild(G.Gap(0.35f));
        }
        Row("Climate", shape.Climate, ["Frozen", "Cold", "Temperate", "Warm", "Tropical"],
            ["Ice and tundra, snow down to the shore", "Taiga and tundra, snowy mountains",
             "Forests, grassland and taiga from south to north", "Grassland, savanna and woods", "Jungle, savanna and desert"],
            (s, v) => s with { Climate = v });
        Row("Rain", shape.Rainfall, ["Arid", "Dry", "Normal", "Wet"],
            ["Deserts and badlands, rivers only from the mountains", "Steppe and savanna, deserts in the rain shadows",
             "Wet windward slopes, drier lee sides", "Lush everywhere: forests, jungle and swamps"],
            (s, v) => s with { Rainfall = v });
        Row("Seas", shape.Oceans, ["None", "Coasts", "Seas", "Islands"],
            ["All land: rivers run off the edge of the map", "Mostly land, with a coast or two",
             "Continents and seas", "An archipelago: island arcs and volcanoes in open sea"],
            (s, v) => s with { Oceans = v });
        Row("Peaks", shape.Relief, ["Gentle", "Rolling", "Mountainous", "Alpine"],
            ["Low hills where the plates meet", "Hills and modest ranges", "Real ranges along the plate seams",
             "Towering, snowy ranges: lots of rock to dig into"],
            (s, v) => s with { Relief = v });
        Row("Rivers", shape.Rivers, ["Few", "Normal", "Many"],
            ["Only the big rivers", "Rivers where the rain gathers", "Streams and rivers everywhere"],
            (s, v) => s with { Rivers = v });
    }

    // ---- Making the world ------------------------------------------------------------------------------------

    private Label? _genTitle, _genDetail;
    private GlyphBar? _genBar;
    private TextureRect? _genMap;
    private VBoxContainer? _genDone;
    private int _genVersion = -1;
    private object? _genMapShown;
    private const int DoneRows = 10;

    /// <summary>
    /// The loading screen while a new world is made: a map of the world as it forms (plates, rain, erosion, biomes), the
    /// step under way with a line about it, the progress bar, and the steps finished with their times.
    /// </summary>
    private void BuildGenerating()
    {
        var main = _ui.Main;
        _box.AddChild(new GlyphSection("Making the world"));
        if (main.GenSettings is { } gs)
            _box.AddChild(G.Text($"seed {gs.Seed} · {gs.Width}² · {gs.Shape.Describe()}", "Faint", align: HorizontalAlignment.Center));
        _box.AddChild(G.Gap(0.5f));
        // The map on the left, the steps beside it (fits a 900-pixel-high window).
        var split = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        split.AddThemeConstantOverride("separation", (int)(GlyphTheme.Cw * 2));
        _box.AddChild(split);
        float side = 17 * GlyphTheme.Ch;
        var frame = new GlyphPanel { CustomMinimumSize = new Vector2(side + GlyphTheme.Cw * 2, side + GlyphTheme.Ch), SizeFlagsVertical = SizeFlags.ShrinkBegin };
        _genMap = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = TextureFilterEnum.Nearest,
            Position = new Vector2(GlyphTheme.Cw, GlyphTheme.Ch * 0.5f),
            Size = new Vector2(side, side),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        frame.AddChild(_genMap);
        split.AddChild(frame);
        var steps = new VBoxContainer { CustomMinimumSize = new Vector2(38 * GlyphTheme.Cw, 0), MouseFilter = MouseFilterEnum.Ignore };
        split.AddChild(steps);
        _genTitle = G.Text("", "Accent", bold: true);
        steps.AddChild(_genTitle);
        _genDetail = G.Text("", "Dim");
        _genDetail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _genDetail.CustomMinimumSize = new Vector2(38 * GlyphTheme.Cw, 2 * GlyphTheme.Ch);
        steps.AddChild(_genDetail);
        steps.AddChild(G.Gap(0.35f));
        _genBar = new GlyphBar(0, 0, "accent", "  0%");
        steps.AddChild(_genBar);
        steps.AddChild(G.Gap(0.5f));
        _genDone = new VBoxContainer { CustomMinimumSize = new Vector2(0, DoneRows * GlyphTheme.Ch), MouseFilter = MouseFilterEnum.Ignore };
        steps.AddChild(_genDone);
        _genVersion = -1;
        _genMapShown = null;
        _genDoneCount = -1;
        UpdateGenerating();
    }

    private void UpdateGenerating()
    {
        if (_ui.Main.GenProgress is not { } p || _genTitle == null || !IsInstanceValid(_genTitle)) return;
        if (p.Version == _genVersion) return;
        _genVersion = p.Version;
        _genTitle.Text = p.Title.Length > 0 ? p.Title + "…" : "Ready";
        _genDetail!.Text = p.Detail;
        _genBar!.Set(p.Fraction, $"{p.Fraction * 100,3:0}%", "accent");
        if (p.Map is { } map && !ReferenceEquals(map.Rgb, _genMapShown))
        {
            _genMapShown = map.Rgb;
            var image = Image.CreateFromData(map.Width, map.Height, false, Image.Format.Rgb8, map.Rgb);
            _genMap!.Texture = ImageTexture.CreateFromImage(image);
        }
        var done = p.Finished;
        if (done.Length == _genDoneCount) return;
        _genDoneCount = done.Length;
        G.Clear(_genDone!);
        for (int k = Math.Max(0, done.Length - DoneRows); k < done.Length; k++) // the last few, newest brightest
        {
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            row.AddChild(G.Text("✓ " + done[k].Title, k == done.Length - 1 ? "Text" : "Faint", expand: true));
            row.AddChild(G.Text($"{done[k].Milliseconds / 1000.0:0.0} s", "Faint", align: HorizontalAlignment.Right));
            _genDone!.AddChild(row);
        }
    }

    private int _genDoneCount = -1;

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton) AcceptEvent();
    }
}
