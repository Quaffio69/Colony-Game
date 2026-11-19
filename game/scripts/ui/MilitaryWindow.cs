using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core.Agents;
using Colony.Core.Combat;
using Colony.Core.Commands;
using Colony.Core.Items;
using Godot;

namespace ColonyGame.UI;

/// <summary>
/// M: the colony's fighters and the threats around it. Each colonist with health, gear and a draft switch; Draft all
/// (G) / Stand down; the squad tool (G again: click ground to march there, a nest to destroy it); nests within reach of
/// the colony with their size and mood, a Look and an Attack button.
/// </summary>
public partial class MilitaryWindow : GlyphWindow
{
    private readonly GameUi _ui;
    private string _roster = "", _threatKey = "";
    private readonly List<(Colonist C, GlyphBar Health, Label Gear, Label Doing, GlyphSwitch Draft)> _rows = new();
    private VBoxContainer _people = null!, _threats = null!;
    private Label _summary = null!;
    /// <summary>Nests this far from the colony's people show up as threats.</summary>
    public const int ThreatRange = 120;

    public MilitaryWindow(GameUi ui) : base("military", "Military", "♠", "bad", "M")
    {
        _ui = ui;
        MinCols = 64;
        Default = new WindowState { X = -94, Y = 2, W = 92, H = 22 };
    }
    public MilitaryWindow() : this(null!) { }

    protected override void Build()
    {
        var bar = new HBoxContainer();
        bar.AddChild(G.Button(" Draft all ", () => _ui.Tool.Issue(new SetDrafted(-1, true)), "Draft every grown colonist: they drop their work and fetch the best arms lying about (G)", "Primary"));
        bar.AddChild(G.Button(" Stand down ", () => { _ui.Tool.Issue(new SetDrafted(-1, false)); _ui.Tool.SquadOrders = false; }, "Everyone back to work, keeping their gear (Shift+G)"));
        bar.AddChild(G.Button(" Squad orders ", () => _ui.Tool.SquadOrders = true, "Click the ground: the drafted march there · click a nest: they go to destroy it · RMB: done (G)"));
        bar.AddChild(G.Spacer());
        _summary = G.Text("", "Dim");
        bar.AddChild(_summary);
        Header.AddChild(bar);
        Header.AddChild(G.Gap(0.25f));
        _people = new VBoxContainer();
        _threats = new VBoxContainer();
        Content.AddChild(new GlyphSection("Fighters"));
        Content.AddChild(_people);
        Content.AddChild(G.Gap(0.5f));
        Content.AddChild(new GlyphSection("Threats nearby"));
        Content.AddChild(_threats);
        Footer.AddChild(G.Para("Gear: one weapon (spear: reach and pierce · bow: range · war axe: cleaves crowds · sword: fast, takes a shield), " +
                               "body armour (takes damage off every hit) and a shield (blocks some blows). Make them in carpentry, textiles and the forge.", "Faint"));
        _roster = _threatKey = "";
    }

    public override void Tick()
    {
        var mine = _ui.Mine.Where(c => !c.IsChild).ToList();
        string roster = string.Join(",", mine.Select(c => c.Id)) + "/" + _ui.Me;
        if (roster != _roster)
        {
            _roster = roster;
            BuildPeople(mine);
        }
        foreach (var r in _rows) Update(r);
        int drafted = mine.Count(c => c.Drafted), down = mine.Count(c => c.Downed);
        _summary.Text = $"{drafted}/{mine.Count} drafted" + (down > 0 ? $" · {down} down" : "") + (_ui.Sim.Deaths > 0 ? $" · {_ui.Sim.Deaths} dead" : "");
        UpdateThreats();
    }

    private void BuildPeople(List<Colonist> mine)
    {
        G.Clear(_people);
        _rows.Clear();
        var heads = new HBoxContainer();
        heads.AddChild(G.Text("", cols: 2));
        heads.AddChild(G.Text("Name", "Faint", cols: 13));
        heads.AddChild(G.Text("Health", "Faint", cols: 11));
        heads.AddChild(G.Text("Gear", "Faint", cols: 30));
        heads.AddChild(G.Text("Doing", "Faint", expand: true));
        _people.AddChild(heads);
        foreach (var c in mine)
        {
            var colonist = c;
            var row = new GlyphRow { Clickable = true };
            row.AddChild(new GlyphChip(c.IsAutomaton ? "◘" : "☺", GlyphTheme.Person(c.Id)));
            row.AddChild(G.Text(" ", cols: 1));
            row.AddChild(G.Text(c.Name, "Text", cols: 13));
            var health = new GlyphBar(1, 10);
            row.AddChild(health);
            row.AddChild(G.Text(" ", cols: 1));
            var gear = Clip(G.Text("", "Dim", cols: 30));
            row.AddChild(gear);
            var doing = Clip(G.Text("", "Dim", expand: true));
            row.AddChild(doing);
            var draft = new GlyphSwitch("", c.Drafted, on => _ui.Tool.Issue(new SetDrafted(colonist.Id, on)));
            row.AddChild(draft);
            row.Pressed += () => _ui.Main.FocusOn(colonist.Position);
            row.MouseEntered += () => GlyphStatus.Tip = $"{colonist.Name}: {colonist.Describe()} · {GearLine(colonist, full: true)} · click to find";
            row.MouseExited += () => GlyphStatus.Tip = null;
            _people.AddChild(row);
            _rows.Add((c, health, gear, doing, draft));
        }
    }

    private static Label Clip(Label l)
    {
        l.ClipText = true;
        l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        l.MouseFilter = Control.MouseFilterEnum.Pass;
        return l;
    }

    private static string Short(ItemType? t) => t is { } i ? i.Name.Replace("Armour", "").Replace("Wooden ", "Wood ").Trim() : "";

    public static string GearLine(Colonist c, bool full = false)
    {
        var parts = new List<string>();
        var a = c.Attack;
        parts.Add(c.Weapon is { } w ? (full ? $"{w.Name} ({a.Damage} dmg{(a.Reach > 1 ? $", reach {a.Reach}" : "")}{(a.Cleave > 1 ? $", cleaves {a.Cleave}" : "")})" : Short(w)) : full ? "bare hands" : "fists");
        if (c.Armour is { } b) parts.Add(full ? $"{b.Name} (-{c.Protect} a hit)" : Short(b));
        if (c.Shield is { } s && !c.TwoHanded) parts.Add(full ? $"{s.Name} (blocks {c.Block}%)" : Short(s));
        return string.Join(full ? ", " : " · ", parts);
    }

    private void Update((Colonist C, GlyphBar Health, Label Gear, Label Doing, GlyphSwitch Draft) r)
    {
        var c = r.C;
        float hp = c.Health / (float)Colonist.MaxHealth;
        r.Health.Set(hp, c.Downed ? "down" : $"{c.Health}", c.Downed || hp < 0.35f ? "bad" : hp < 0.7f ? "warn" : "good");
        r.Gear.Text = GearLine(c);
        r.Doing.Text = c.Downed ? "struck down" : c.Drafted || c.Task is ColonistTask.Fight or ColonistTask.Flee ? c.Describe() : ColonistsWindow.Doing(c);
        G.Retone(r.Doing, c.Downed ? "Bad" : c.Drafted ? "Warn" : "Dim");
        if (r.Draft.On != c.Drafted) r.Draft.On = c.Drafted;
    }

    private void UpdateThreats()
    {
        var sim = _ui.Sim;
        var mine = _ui.Mine;
        var near = new List<(Nest Nest, int Dist)>();
        foreach (var n in sim.Wildlife.Nests)
        {
            int best = int.MaxValue;
            foreach (var c in mine) best = Math.Min(best, Math.Max(Math.Abs(c.Position.X - n.Entrance.X), Math.Abs(c.Position.Y - n.Entrance.Y)));
            if (best <= ThreatRange) near.Add((n, best));
        }
        near.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        string key = string.Join(",", near.Take(8).Select(t => $"{t.Nest.Id}:{t.Dist / 4}:{t.Nest.Total}:{t.Nest.HostileTo(_ui.Me, sim.Tick)}:{t.Nest.Health}"));
        if (key == _threatKey) return;
        _threatKey = key;
        G.Clear(_threats);
        if (near.Count == 0)
        {
            _threats.AddChild(G.Text("No nests within reach of the colony.", "Faint"));
            return;
        }
        foreach (var (n, dist) in near.Take(8))
        {
            var nest = n;
            bool hostile = n.HostileTo(_ui.Me, sim.Tick);
            bool crowded = ((n.Crowded >> _ui.Me) & 1) != 0;
            var row = new GlyphRow();
            row.AddChild(new GlyphChip("♦", hostile ? "bad" : "dim"));
            row.AddChild(G.Text($" {n.Kind.Name} nest", "Text", cols: 17));
            row.AddChild(G.Text($"{n.Total} ants", "Hi", cols: 9));
            row.AddChild(G.Text($"{dist} tiles {Direction(n)}", "Dim", cols: 15));
            string mood = crowded ? "raiding (our land meets theirs)" : hostile ? "angry" : "calm";
            row.AddChild(Clip(G.Text(mood + (n.Health < n.Kind.NestHealth ? $", mound {n.Health * 100 / n.Kind.NestHealth}%" : ""), hostile ? "Bad" : "Good", expand: true)).WithTip(crowded ? "Their territory meets your rooms, stockpiles or farms: they raid now and then until the nest is gone or you pull back" : hostile ? "You struck them: they fight anyone of yours near their nest for two days" : "Leave them be and they leave you be"));
            row.AddChild(G.Button(" Look ", () => _ui.Main.FocusOn(nest.Entrance), "Show the nest"));
            row.AddChild(G.Button(" Attack ", () =>
            {
                if (!_ui.Mine.Any(c => c.Drafted)) _ui.Tool.Issue(new SetDrafted(-1, true));
                _ui.Tool.Issue(new SquadAttack(nest.Id));
            }, "Draft everyone (if nobody is) and send the squad to destroy this nest", "Primary"));
            _threats.AddChild(row);
        }
    }

    private string Direction(Nest n)
    {
        var land = _ui.MyFaction.Landing ?? _ui.Mine.FirstOrDefault()?.Position ?? n.Entrance;
        int dx = n.Entrance.X - land.X, dy = n.Entrance.Y - land.Y;
        if (dx == 0 && dy == 0) return "";
        double a = Math.Atan2(dy, dx) * 180 / Math.PI;
        string[] names = ["E", "NE", "N", "NW", "W", "SW", "S", "SE"];
        return names[(int)Math.Round((a + 360) % 360 / 45) % 8];
    }
}
