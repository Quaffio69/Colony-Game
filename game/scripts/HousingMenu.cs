using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core;
using Colony.Core.Agents;
using Colony.Core.Industry;
using Godot;

namespace ColonyGame;

/// <summary>
/// Housing policies (H): per policy and fixture slot, the tier wanted and how many pieces each resident gets.
/// Every home follows a policy (F over a home switches it), so editing a policy re-furnishes all its homes:
/// colonists bring the new pieces and the old ones are broken down. The preview shows what a 12-cell home holds.
/// </summary>
public partial class HousingMenu : PanelContainer
{
    private ColonySim _sim = null!;
    private VBoxContainer _rows = null!;
    private int _drawnVersion = -1;

    public void Initialize(ColonySim sim)
    {
        _sim = sim;
        Visible = false;
        AnchorLeft = AnchorRight = 1;
        OffsetLeft = -820;
        OffsetRight = -12;
        OffsetTop = 12;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.11f, 0.94f),
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        });
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        var title = new Label { Text = "Housing policies  (H or Esc to close)" };
        title.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(title);
        box.AddChild(new Label
        {
            Text = "Each home follows a policy (F over a home switches it). Edit a policy and every home on it is refurnished.\n" +
                   "Tier: humble / comfortable / fine.  Each: pieces per resident (more furniture per head = fewer beds fit).",
        });
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 10);
        box.AddChild(_rows);
        var add = new Button { Text = "New policy (copy of the last)" };
        add.Pressed += () =>
        {
            var last = _sim.Housing.Policies[^1];
            _sim.Housing.Policies.Add(last.Clone($"{last.Name} {_sim.Housing.Policies.Count + 1}"));
            _drawnVersion = -1;
        };
        box.AddChild(add);
    }

    public void Toggle()
    {
        Visible = !Visible;
        _drawnVersion = -1;
    }

    public override void _Process(double delta)
    {
        if (!Visible || _drawnVersion == _sim.Housing.Version) return;
        _drawnVersion = _sim.Housing.Version;
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (var child in _rows.GetChildren()) child.QueueFree();
        foreach (var policy in _sim.Housing.Policies)
        {
            var p = policy;
            int homes = _sim.Housing.All.Count(h => h.Policy == p);
            var plan = Housing.Plan(12, p);
            var block = new VBoxContainer();
            var head = new Label { Text = $"{p.Name}   ({homes} home{(homes == 1 ? "" : "s")}; a 12-cell home: {plan.Residents} residents)" };
            head.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.55f));
            block.AddChild(head);
            var grid = new GridContainer { Columns = 7 };
            for (int s = 0; s < Housing.SlotCount; s++)
            {
                int slot = s;
                grid.AddChild(new Label { Text = Housing.Slots[s], CustomMinimumSize = new Vector2(90, 0) });
                grid.AddChild(Small("-", () => _sim.Housing.SetTier(p, slot, p.Tier[slot] - 1)));
                grid.AddChild(new Label { Text = Catalog.FixtureTiers[p.Tier[s]], CustomMinimumSize = new Vector2(110, 0), HorizontalAlignment = HorizontalAlignment.Center });
                grid.AddChild(Small("+", () => _sim.Housing.SetTier(p, slot, p.Tier[slot] + 1)));
                if (s == Housing.Bed)
                {
                    grid.AddChild(new Label());
                    grid.AddChild(new Label { Text = "one each", CustomMinimumSize = new Vector2(120, 0), HorizontalAlignment = HorizontalAlignment.Center });
                    grid.AddChild(new Label());
                    continue;
                }
                grid.AddChild(Small("-", () => _sim.Housing.SetPerResident(p, slot, p.PerResident[slot] - 0.25f)));
                grid.AddChild(new Label { Text = $"{p.PerResident[s]:0.##} each", CustomMinimumSize = new Vector2(120, 0), HorizontalAlignment = HorizontalAlignment.Center });
                grid.AddChild(Small("+", () => _sim.Housing.SetPerResident(p, slot, p.PerResident[slot] + 0.25f)));
            }
            block.AddChild(grid);
            _rows.AddChild(block);
        }
    }

    private static Button Small(string text, Action pressed)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(28, 0) };
        b.Pressed += pressed;
        return b;
    }
}
