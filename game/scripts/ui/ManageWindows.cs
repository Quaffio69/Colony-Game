using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Godot;

namespace ColonyGame.UI;

// ---- Housing (H) --------------------------------------------------------------------------------------------

/// <summary>
/// Housing policies: per policy and fixture slot, the tier wanted and pieces per resident. Every home follows a
/// policy (F over a home switches it), so editing a policy refurnishes all its homes.
/// </summary>
public partial class HousingWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private int _version = -1, _shownFor = -1;

    public HousingWindow(GameUi ui) : base("housing", "Housing", "⌂", "good", "H")
    {
        _ui = ui;
        MinCols = 56;
        Default = new WindowState { X = -2, Y = 3, W = 76, H = 32 };
    }
    public HousingWindow() : this(null!) { }

    protected override void Build()
    {
        Header.AddChild(G.Para("Every home follows a policy (F over a home switches it): edit one and colonists refurnish every home on it. " +
                               "Each: pieces per resident (more per head, fewer beds fit).", "Dim"));
        Header.AddChild(G.Gap(0.25f));
        var add = new HBoxContainer();
        add.AddChild(G.Button(" New policy ", () =>
        {
            _ui.Tool.Issue(new AddPolicy());
            _version = -1;
        }, "A copy of the last policy"));
        Footer.AddChild(G.Gap(0.25f));
        Footer.AddChild(add);
        _version = -1;
    }

    public override void Tick()
    {
        var housing = _ui.MyFaction.Housing;
        if (housing.Version == _version && _shownFor == _ui.Me) return;
        _shownFor = _ui.Me;
        _version = housing.Version;
        G.Clear(Content);
        foreach (var policy in housing.Policies)
        {
            var p = policy;
            int homes = housing.All.Count(h => h.Policy == p);
            int pi = housing.Policies.IndexOf(p);
            var plan = Housing.Plan(12, p);
            var head = new HBoxContainer();
            head.AddChild(new GlyphSection(p.Name));
            Content.AddChild(head);
            Content.AddChild(new GlyphKV(null, default, $"{G.Plural(homes, "home")} on it · a 12-cell home sleeps", plan.Residents.ToString()));
            Content.AddChild(new GlyphSwitch("Shared lodging (singles board here; never a family's home)", p.Shared, on => _ui.Tool.Issue(new SetPolicyShared(pi, on))));
            for (int s = 0; s < Housing.SlotCount; s++)
            {
                int slot = s;
                var row = new HBoxContainer();
                row.AddChild(G.Text(Housing.Slots[s], cols: 11));
                row.AddChild(new GlyphSegmented(Catalog.FixtureTiers.Select(t => t).ToArray(), p.Tier[s], i => _ui.Tool.Issue(new SetPolicyTier(pi, slot, i)))
                {
                    Tips = Catalog.FixtureTiers.Select(t => $"{Housing.Slots[slot]}: {t} ({Housing.SlotEffects[slot]})").ToArray(),
                });
                row.AddChild(G.Spacer());
                if (s == Housing.Bed) row.AddChild(G.Text("one each", "Faint", cols: 12, align: HorizontalAlignment.Right));
                else
                {
                    row.AddChild(G.Button("−", () => _ui.Tool.Issue(new SetPolicyPerResident(pi, slot, p.PerResident[slot] - 0.25f)), "Fewer per resident"));
                    row.AddChild(G.Text($"{p.PerResident[s]:0.##}", "Hi", bold: true, cols: 4, align: HorizontalAlignment.Right));
                    row.AddChild(G.Button("+", () => _ui.Tool.Issue(new SetPolicyPerResident(pi, slot, p.PerResident[slot] + 0.25f)), "More per resident"));
                }
                Content.AddChild(row);
            }
            Content.AddChild(G.Gap());
        }
    }
}

// ---- Work priorities (J) --------------------------------------------------------------------------------------

/// <summary>
/// Work priorities: colonists by work type, each off or 1 (first) to 4 (last). Left click cycles 1-2-3-4-off, right
/// click back; a heading sets the column for everyone. The last column shows what each is doing.
/// </summary>
public partial class WorkWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private string _roster = "";
    private readonly List<(Colonist C, PriorityCell[] Cells, Label Doing)> _rows = new();
    private static readonly int[] Widths = Colonist.WorkNames.Select(n => Math.Max(6, n.Length + 2)).ToArray();

    public WorkWindow(GameUi ui) : base("work", "Work", "τ", "warn", "J")
    {
        _ui = ui;
        MinCols = 60;
        Default = new WindowState { X = 2, Y = 3, W = 100, H = 16 };
    }
    public WorkWindow() : this(null!) { }

    protected override void Build()
    {
        Header.AddChild(G.Para("1 = done first … 4 = last, – = never. Left click cycles, right click goes back; a heading sets the whole column. " +
                               "At one number the nearest of digging, felling and farming wins. Children don't work.", "Dim"));
        Header.AddChild(G.Gap(0.25f));
        var heads = new HBoxContainer();
        heads.AddThemeConstantOverride("separation", 0);
        heads.AddChild(G.Text("Colonist", "Faint", cols: 15));
        heads.AddChild(G.Text("Schooling", "Faint", cols: 11));
        for (int w = 0; w < Colonist.WorkTypes; w++)
        {
            var work = (Work)w;
            var b = G.Button(Colonist.WorkNames[w], () => SetColumn(work), $"Set {Colonist.WorkNames[w].ToLowerInvariant()} for everyone", "Ghost");
            b.CustomMinimumSize = new Vector2(Widths[w] * GlyphTheme.Cw, 0);
            heads.AddChild(b);
        }
        heads.AddChild(G.Text("  Doing", "Faint", expand: true));
        Header.AddChild(heads);
        _roster = "";
    }

    public override void Tick()
    {
        string roster = string.Join(",", _ui.Mine.Select(c => c.Id + (c.IsChild ? "c" : "")));
        if (roster != _roster)
        {
            _roster = roster;
            G.Clear(Content);
            _rows.Clear();
            foreach (var c in _ui.Mine)
            {
                var row = new GlyphRow();
                row.AddThemeConstantOverride("separation", 0);
                row.AddChild(new GlyphChip(c.IsAutomaton ? "◘" : "☺", GlyphTheme.Person(c.Id)));
                row.AddChild(G.Text(" " + c.Name, cols: 14));
                row.AddChild(G.Text(c.IsChild ? "Child" : c.EducationName, "Dim", cols: 11));
                var cells = new PriorityCell[Colonist.WorkTypes];
                for (int w = 0; w < Colonist.WorkTypes; w++)
                {
                    cells[w] = new PriorityCell(c, (Work)w, Widths[w], _ui.Tool.Issue);
                    row.AddChild(cells[w]);
                }
                var doing = G.Text("", "Dim", expand: true);
                row.AddChild(doing);
                Content.AddChild(row);
                _rows.Add((c, cells, doing));
            }
        }
        foreach (var (c, cells, doing) in _rows)
        {
            foreach (var cell in cells) cell.QueueRedraw();
            doing.Text = "  " + ColonistsWindow.Doing(c);
        }
    }

    private void SetColumn(Work work)
    {
        var first = _ui.Mine.FirstOrDefault(c => !c.IsChild);
        if (first == null) return;
        int value = PriorityCell.Next(first.Priority(work), +1);
        foreach (var c in _ui.Mine) _ui.Tool.Issue(new SetWorkPriority(c.Id, work, value));
        foreach (var r in _rows) foreach (var cell in r.Cells) cell.QueueRedraw();
    }
}

/// <summary>One priority in the grid: the number in its colour (1 hot ... 4 cool, – off), hover fill, click to cycle.</summary>
public partial class PriorityCell : GlyphControl
{
    private readonly Colonist _c;
    private readonly Work _work;
    private readonly int _cols;
    private bool _hover;
    private static readonly string[] Tones = ["faint", "bad", "warn", "good", "info"];

    private readonly Action<Command> _issue;

    public PriorityCell(Colonist c, Work work, int cols, Action<Command> issue)
    {
        _c = c; _work = work; _cols = cols; _issue = issue;
        MouseDefaultCursorShape = c.IsChild ? CursorShape.Arrow : CursorShape.PointingHand;
        MouseEntered += () =>
        {
            _hover = true;
            QueueRedraw();
            GlyphStatus.Tip = _c.IsChild ? $"{_c.Name} is a child: no work yet" : $"{_c.Name}: {Colonist.WorkNames[(int)_work].ToLowerInvariant()} · left click: next, right click: back";
        };
        MouseExited += () => { _hover = false; GlyphStatus.Tip = null; QueueRedraw(); };
    }
    public PriorityCell() : this(null!, Work.Mining, 6, _ => { }) { }

    public static int Next(int priority, int step)
    {
        int n = Colonist.LowestPriority + 1;
        int slot = priority == 0 ? Colonist.LowestPriority : priority - 1; // 0..3 = 1..4, 4 = off
        slot = ((slot + step) % n + n) % n;
        return slot == Colonist.LowestPriority ? 0 : slot + 1;
    }

    public override Vector2 _GetMinimumSize() => new(_cols * Cw, Ch);

    public override void _GuiInput(InputEvent e)
    {
        if (_c.IsChild || e is not InputEventMouseButton { Pressed: true } b) return;
        int step = b.ButtonIndex switch { MouseButton.Left => 1, MouseButton.Right => -1, _ => 0 };
        if (step == 0) return;
        AcceptEvent();
        _issue(new SetWorkPriority(_c.Id, _work, Next(_c.Priority(_work), step)));
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_hover && !_c.IsChild) GlyphDraw.Round(this, new Rect2(Cw * 0.5f, Ch * 0.1f, Size.X - Cw, Ch * 0.8f), GlyphTheme.Tok("text", 0.08f), 4);
        if (_c.IsChild) return;
        int p = _c.Priority(_work);
        GlyphDraw.Text(this, p == 0 ? "–" : p.ToString(), Vector2.Zero, Size.X, Tok(Tones[p]), bold: p is 1 or 2, align: HorizontalAlignment.Center);
    }
}

// ---- Tests (F1) ----------------------------------------------------------------------------------------------

/// <summary>The dev test menu (every xUnit test, run or watched in the game) and the demos, in a Glyph window.</summary>
public partial class TestsWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private TestMenu? _menu;

    public TestsWindow(GameUi ui) : base("tests", "Tests & demos", "♦", "info", "F1")
    {
        _ui = ui;
        MinCols = 50;
        MinRows = 20;
        Default = new WindowState { X = -2, Y = 3, W = 84, H = 34 };
    }
    public TestsWindow() : this(null!) { }

    protected override void Build()
    {
        _menu = new TestMenu { Name = "TestMenu" };
        _menu.Initialize(_ui.Main);
        _menu.DemoStarted = Close;
        Content.AddChild(_menu);
        _menu.Opened();
    }
}
