using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core;
using Colony.Core.Agents;
using Godot;

namespace ColonyGame;

/// <summary>
/// Work priorities (J): a grid of colonists by work type. Each cell is off or 1 (done first) to 4 (last); left click
/// cycles 1, 2, 3, 4, off, right click goes the other way. A column header sets that work for everyone to the value
/// the first colonist's cell would cycle to. The last column shows what each is doing now.
/// </summary>
public partial class WorkMenu : PanelContainer
{
    private ColonySim _sim = null!;
    private GridContainer _grid = null!;
    private readonly List<(Colonist Colonist, Button[] Cells, Label Doing)> _rows = new();
    private int _drawnCount = -1;
    private double _refresh;

    private static readonly Color[] PriorityColors =
    [
        new(0.35f, 0.35f, 0.38f), // off
        new(0.95f, 0.45f, 0.35f),
        new(0.95f, 0.75f, 0.35f),
        new(0.75f, 0.85f, 0.55f),
        new(0.6f, 0.7f, 0.8f),
    ];

    public void Initialize(ColonySim sim)
    {
        _sim = sim;
        Visible = false;
        OffsetLeft = 12;
        OffsetTop = 120;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.11f, 0.985f),
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        });
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        var title = new Label { Text = "Work priorities  (J or Esc to close)" };
        title.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(title);
        box.AddChild(new Label
        {
            Text = "1 = done first ... 4 = last, - = never. Left click cycles 1-4-off, right click back. Click a heading to set the whole column.\n" +
                   "Same number: the nearest job wins among digging, felling and farming. Children don't work.",
        });
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 160), SizeFlagsVertical = SizeFlags.ExpandFill };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        box.AddChild(scroll);
        _grid = new GridContainer { Columns = Colonist.WorkTypes + 3 };
        _grid.AddThemeConstantOverride("h_separation", 4);
        _grid.AddThemeConstantOverride("v_separation", 2);
        scroll.AddChild(_grid);
    }

    public void Toggle()
    {
        Visible = !Visible;
        _drawnCount = -1;
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        if (_drawnCount != _sim.Colonists.Count || _rows.Exists(r => !_sim.Colonists.Contains(r.Colonist)))
        {
            Rebuild();
            _refresh = 0;
        }
        if ((_refresh -= delta) > 0) return;
        _refresh = 0.25;
        foreach (var row in _rows) Refresh(row);
    }

    private void Rebuild()
    {
        _drawnCount = _sim.Colonists.Count;
        foreach (var child in _grid.GetChildren()) child.QueueFree();
        _rows.Clear();
        _grid.AddChild(new Label { Text = "Colonist", CustomMinimumSize = new Vector2(150, 0) });
        _grid.AddChild(new Label { Text = "Schooling", CustomMinimumSize = new Vector2(90, 0) });
        for (int w = 0; w < Colonist.WorkTypes; w++)
        {
            var work = (Work)w;
            var head = new Button { Text = Colonist.WorkNames[w], Flat = true, CustomMinimumSize = new Vector2(96, 0) };
            head.Pressed += () => SetColumn(work);
            _grid.AddChild(head);
        }
        _grid.AddChild(new Label { Text = "Doing", CustomMinimumSize = new Vector2(150, 0) });

        // Scroll height follows the roster, up to most of the screen.
        float rowHeight = 32;
        var scroll = (ScrollContainer)_grid.GetParent();
        scroll.CustomMinimumSize = new Vector2(0, Math.Min(GetViewportRect().Size.Y - 300, rowHeight * (_sim.Colonists.Count + 1)));

        foreach (var c in _sim.Colonists)
        {
            var colonist = c;
            _grid.AddChild(new Label { Text = c.Name, ClipText = true, CustomMinimumSize = new Vector2(150, 0) });
            _grid.AddChild(new Label { Text = c.IsChild ? "Child" : c.EducationName });
            var cells = new Button[Colonist.WorkTypes];
            for (int w = 0; w < Colonist.WorkTypes; w++)
            {
                var work = (Work)w;
                var cell = new Button { CustomMinimumSize = new Vector2(96, 28), Disabled = c.IsChild };
                cell.GuiInput += e =>
                {
                    if (e is not InputEventMouseButton { Pressed: true } mb) return;
                    if (mb.ButtonIndex == MouseButton.Left) colonist.SetPriority(work, Next(colonist.Priority(work), +1));
                    else if (mb.ButtonIndex == MouseButton.Right) colonist.SetPriority(work, Next(colonist.Priority(work), -1));
                    else return;
                    cell.AcceptEvent();
                    Refresh(_rows.Find(r => r.Colonist == colonist));
                };
                cells[w] = cell;
                _grid.AddChild(cell);
            }
            var doing = new Label { CustomMinimumSize = new Vector2(150, 0), ClipText = true };
            _grid.AddChild(doing);
            var row = (c, cells, doing);
            _rows.Add(row);
            Refresh(row);
        }
    }

    /// <summary>1 -> 2 -> 3 -> 4 -> off -> 1 (step +1), or back.</summary>
    private static int Next(int priority, int step)
    {
        int slot = priority == 0 ? Colonist.LowestPriority : priority - 1; // 0..3 = 1..4, 4 = off
        slot = ((slot + step) % (Colonist.LowestPriority + 1) + Colonist.LowestPriority + 1) % (Colonist.LowestPriority + 1);
        return slot == Colonist.LowestPriority ? 0 : slot + 1;
    }

    private void SetColumn(Work work)
    {
        Colonist? first = _sim.Colonists.FirstOrDefault(c => !c.IsChild);
        if (first == null) return;
        int value = Next(first.Priority(work), +1);
        foreach (var c in _sim.Colonists) c.SetPriority(work, value);
        foreach (var row in _rows) Refresh(row);
    }

    private static void Refresh((Colonist Colonist, Button[] Cells, Label Doing) row)
    {
        var c = row.Colonist;
        for (int w = 0; w < Colonist.WorkTypes; w++)
        {
            int p = c.Priority((Work)w);
            var cell = row.Cells[w];
            cell.Text = c.IsChild ? "" : p == 0 ? "-" : p.ToString();
            cell.AddThemeColorOverride("font_color", PriorityColors[p]);
            cell.AddThemeColorOverride("font_hover_color", PriorityColors[p].Lightened(0.2f));
        }
        row.Doing.Text = c.CurrentWork is { } now ? Colonist.WorkNames[(int)now]
            : c.Task == ColonistTask.Sleep ? "Sleeping"
            : c.Task == ColonistTask.Eat ? "Eating"
            : "Idle";
    }
}
