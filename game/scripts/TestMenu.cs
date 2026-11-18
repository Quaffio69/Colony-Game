using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;

namespace ColonyGame;

/// <summary>
/// The test menu (T): every unit test of <c>tests/Colony.Core.Tests</c>, grouped by file, with a filter.
/// "Run" runs one and opens the world it built at the end; "Watch" opens that world as the test set it up, paused,
/// so you can see it happen (Space); "Run all shown" runs the listed tests and marks each pass/fail. Also launches
/// the dev demos into the current colony, and leads back to the colony from a test's world.
/// Runs happen on a background thread; state is static so a run survives the scene reload that opens a world.
/// </summary>
public partial class TestMenu : PanelContainer
{
    private static readonly Dictionary<string, TestResult> Results = new();
    private static readonly Queue<TestCase> Queue = new();
    private static Task<TestResult>? _running;
    private static bool _openWhenDone;
    private static int _batchTotal;
    private static string _filter = "";
    private static string? _selected;

    private Main _main = null!;
    private Tree _tree = null!;
    private LineEdit _filterBox = null!;
    private Label _status = null!;
    private Button _run = null!, _watch = null!, _runAll = null!, _home = null!;
    private HBoxContainer _demos = null!;
    private IReadOnlyList<TestCase> _cases = Array.Empty<TestCase>();
    private string _loadError = "";

    private static readonly (string Label, string Id)[] Demos =
    [
        ("Water", "water"), ("Factory", "factory"), ("Hauling", "haul"), ("Parts", "parts"),
        ("Homes", "homes"), ("Showroom", "showroom"), ("Stairs", "stairs"), ("Dig", "dig"),
    ];

    public void Initialize(Main main)
    {
        _main = main;
        Visible = false;
        AnchorLeft = AnchorRight = 1;
        AnchorTop = 0;
        AnchorBottom = 1;
        OffsetLeft = -760;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.11f, 0.94f),
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        });

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);

        var title = new Label { Text = "Tests  (T or Esc to close)" };
        title.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(title);

        var top = new HBoxContainer();
        _filterBox = new LineEdit { PlaceholderText = "Filter (e.g. water, tunnel, pressure)", Text = _filter, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _filterBox.TextChanged += text =>
        {
            _filter = text;
            Populate();
        };
        top.AddChild(_filterBox);
        var refresh = new Button { Text = "Reload tests", TooltipText = "Picks up a fresh build of the tests (dotnet build ColonyGame.sln)" };
        refresh.Pressed += () => Load();
        top.AddChild(refresh);
        box.AddChild(top);

        _tree = new Tree { HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill, SelectMode = Tree.SelectModeEnum.Single };
        _tree.ItemSelected += () =>
        {
            _selected = SelectedCase()?.FullName;
            UpdateStatus();
        };
        _tree.ItemActivated += () => Start(watch: false); // double-click or Enter
        box.AddChild(_tree);

        var buttons = new HBoxContainer();
        _run = new Button { Text = "Run, then open its world", TooltipText = "Runs the selected test to the end and shows the world it built, still running" };
        _run.Pressed += () => Start(watch: false);
        _watch = new Button { Text = "Watch from the start", TooltipText = "Opens the selected test's world as it was set up, paused: press Space to watch it run" };
        _watch.Pressed += () => Start(watch: true);
        _runAll = new Button { Text = "Run all shown", TooltipText = "Runs every listed test and marks it pass/fail (no worlds opened)" };
        _runAll.Pressed += RunAll;
        buttons.AddChild(_run);
        buttons.AddChild(_watch);
        buttons.AddChild(_runAll);
        box.AddChild(buttons);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 90) };
        box.AddChild(_status);

        box.AddChild(new HSeparator());
        var demoRow = new HBoxContainer();
        demoRow.AddChild(new Label { Text = "Demos (built next to the colony):" });
        _demos = new HBoxContainer();
        foreach (var (label, id) in Demos)
        {
            var b = new Button { Text = label };
            b.Pressed += () =>
            {
                _main.RunDemo(id);
                Visible = false;
            };
            _demos.AddChild(b);
        }
        demoRow.AddChild(_demos);
        box.AddChild(demoRow);

        _home = new Button { Text = "Back to the colony" };
        _home.Pressed += _main.ReturnHome;
        box.AddChild(_home);
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (!Visible)
        {
            _filterBox.ReleaseFocus();
            return;
        }
        Load();
        foreach (var child in _demos.GetChildren()) ((Button)child).Disabled = !_main.CanRunDemos;
        _home.Visible = _main.InTestWorld;
        _filterBox.GrabFocus();
    }

    private void Load()
    {
        try
        {
            _cases = TestRunner.Discover();
            _loadError = "";
        }
        catch (Exception e)
        {
            _cases = Array.Empty<TestCase>();
            _loadError = e.Message;
        }
        Populate();
    }

    private IEnumerable<TestCase> Shown()
    {
        var words = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _cases.Where(c => words.All(w =>
            c.FullName.Contains(w, StringComparison.OrdinalIgnoreCase) || Humanize(c.Name).Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    private void Populate()
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        bool filtered = _filter.Trim().Length > 0;
        foreach (var group in Shown().GroupBy(c => c.Class))
        {
            var cls = _tree.CreateItem(root);
            int passed = group.Count(c => Results.TryGetValue(c.FullName, out var r) && r.Passed);
            int failed = group.Count(c => Results.TryGetValue(c.FullName, out var r) && !r.Passed);
            cls.SetText(0, $"{group.Key}  ({group.Count()}" + (passed + failed > 0 ? $": {passed} passed, {failed} failed" : "") + ")");
            cls.SetSelectable(0, false);
            if (failed > 0) cls.SetCustomColor(0, new Color(1f, 0.45f, 0.4f));
            cls.Collapsed = !filtered && !group.Any(c => c.FullName == _selected);
            foreach (var test in group)
            {
                var item = _tree.CreateItem(cls);
                item.SetMetadata(0, test.FullName);
                item.SetTooltipText(0, test.FullName);
                string mark = "";
                if (Results.TryGetValue(test.FullName, out var r))
                {
                    mark = r.Stopped ? "  [watched]" : r.Passed ? $"  [pass, {r.Ms:0} ms]" : "  [FAIL]";
                    item.SetCustomColor(0, r.Stopped ? new Color(0.9f, 0.85f, 0.5f) : r.Passed ? new Color(0.55f, 0.9f, 0.55f) : new Color(1f, 0.45f, 0.4f));
                }
                item.SetText(0, Humanize(test.Name) + mark);
                if (test.FullName == _selected) item.Select(0);
            }
        }
        UpdateStatus();
    }

    private TestCase? SelectedCase() =>
        _tree.GetSelected() is { } item && item.GetMetadata(0).AsString() is { Length: > 0 } name ? _cases.FirstOrDefault(c => c.FullName == name) : null;

    private void Start(bool watch)
    {
        if (_running != null || SelectedCase() is not { } test) return;
        _openWhenDone = true;
        _batchTotal = 0;
        _running = TestRunner.Run(test, watch);
        UpdateStatus();
    }

    private void RunAll()
    {
        if (_running != null) return;
        Queue.Clear();
        foreach (var c in Shown()) Queue.Enqueue(c);
        _batchTotal = Queue.Count;
        _openWhenDone = false;
        if (Queue.Count > 0) _running = TestRunner.Run(Queue.Dequeue(), watch: false);
        UpdateStatus();
    }

    public override void _Process(double delta)
    {
        if (_running is not { IsCompleted: true } done) return;
        _running = null;
        var result = done.Result;
        Results[result.Case.FullName] = result with { Sim = null }; // only an opened world is kept
        if (_openWhenDone)
        {
            _selected = result.Case.FullName;
            if (result.Sim != null)
            {
                _main.ShowTestWorld(result); // reloads the scene: this menu goes with it
                return;
            }
        }
        else if (Queue.Count > 0) _running = TestRunner.Run(Queue.Dequeue(), watch: false);
        if (Visible) Populate();
    }

    private void UpdateStatus()
    {
        bool busy = _running != null;
        _run.Disabled = _watch.Disabled = _runAll.Disabled = busy || _cases.Count == 0;
        var sb = new StringBuilder();
        if (_loadError.Length > 0) sb.AppendLine(_loadError);
        else if (busy && _batchTotal > 0) sb.AppendLine($"Running {_batchTotal - Queue.Count} of {_batchTotal}...");
        else if (busy) sb.AppendLine("Running... (the world opens when it's done)");
        else
        {
            int passed = Results.Values.Count(r => r.Passed && !r.Stopped), failed = Results.Values.Count(r => !r.Passed);
            sb.AppendLine($"{_cases.Count} tests" + (passed + failed > 0 ? $"; this session: {passed} passed, {failed} failed" : "") +
                          ". Double-click a test to run it and open its world.");
        }
        if (SelectedCase() is { } sel)
        {
            sb.AppendLine(sel.FullName);
            if (Results.TryGetValue(sel.FullName, out var r))
                sb.AppendLine(r.Message);
        }
        _status.Text = sb.ToString().Replace("\r", "").TrimEnd();
    }

    /// <summary>"ALongFloodedTunnel_PassesTheLakesFullPressure" -> "A long flooded tunnel, passes the lakes full pressure".</summary>
    public static string Humanize(string name)
    {
        int paren = name.IndexOf('(');
        string args = paren >= 0 ? " " + name[paren..] : "";
        if (paren >= 0) name = name[..paren];
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '_')
            {
                sb.Append(',');
                continue;
            }
            bool wordStart = i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])))
                             || i > 0 && char.IsDigit(c) && !char.IsDigit(name[i - 1]);
            if (wordStart || (i > 0 && name[i - 1] == '_')) sb.Append(' ');
            // Keep acronyms (2+ capitals) as they are; lower-case ordinary words after the first.
            bool acronym = char.IsUpper(c) && i + 1 < name.Length && char.IsUpper(name[i + 1]);
            sb.Append(sb.Length > 0 && !acronym ? char.ToLowerInvariant(c) : c);
        }
        return sb.ToString() + args;
    }
}
