using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core;
using Colony.Core.Commands;
using Colony.Core.Transport;
using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>
/// The conveyor tools. <see cref="ToolMode.Conveyor"/>: click what the belt starts from (an output crate, a stockpile, or
/// open ground for a hopper), then what it goes to (an input crate, a stockpile, the ground, or another belt to merge into);
/// the route between is planned as the mouse moves (<see cref="ConveyorRouter"/>: round rock, over hills on ramps, up
/// cliffs on lifts, across gaps on trestles) and shown, green with its tiles and cost or red with why not.
/// <see cref="ToolMode.Splitter"/> is the same, starting at a tile of an existing belt: a splitter there sends down the
/// branch whatever its sink wants. Shift+click a belt takes it down; a right click lets go of the start, then the tool.
/// </summary>
public partial class DesignationTool
{
    /// <summary>The belt cell under the mouse (raised belts are picked in mid-air), null if none.</summary>
    public TilePos? HoveredBelt { get; private set; }

    /// <summary>What the conveyor tools say on the tool card: the route's tiles and cost, or why there is none (bad).</summary>
    public (string Text, bool Good)? BeltNote { get; private set; }

    private Port? _beltFrom;
    private TilePos _beltFromCell;
    private List<TilePos>? _beltRoute;
    private Port? _beltTo;
    private TilePos _beltToCell;
    private (TilePos? Cell, Port? From, long World, int Belts, int Zones, int Rooms, int Sites) _beltKey;
    private MultiMeshInstance3D? _beltPreview;
    private MeshInstance3D? _beltMark;

    public static bool IsBeltTool(ToolMode m) => m is ToolMode.Conveyor or ToolMode.Splitter;

    /// <summary>The conveyor tool (or the splitter tool, whose belts branch off an existing one).</summary>
    public void SelectConveyor(bool splitter)
    {
        Mode = splitter ? ToolMode.Splitter : ToolMode.Conveyor;
        EndSelection();
        ClearBelt();
    }

    /// <summary>The cell a click joins: a belt tile under the mouse, the cell against a wall or cliff face pointed at, or the floor on top.</summary>
    private TilePos? BeltTarget() => HoveredBelt ?? StructureCell();

    /// <summary>Casts again for belt tiles in front of the ground (cheap: only while there are belts).</summary>
    private void PickBelt(System.Numerics.Vector3 origin, System.Numerics.Vector3 dir, RayHit? ground)
    {
        HoveredBelt = null;
        if (_sim.Conveyors.Count == 0 || OverMenu || !(Mode == ToolMode.None || IsBeltTool(Mode))) return;
        var belts = _sim.Conveyors;
        var d = System.Numerics.Vector3.Normalize(dir);
        float reach = ground?.Distance + 0.01f ?? 5000f;
        var from = origin;
        // A belt fills only the bottom of its cell: the ray must pass that low inside it (else it is the floor beyond that
        // was pointed at, and the cast goes on from where the ray leaves the cell). Lift towers fill their whole cell.
        for (int tries = 0; tries < 8 && reach > 0; tries++)
        {
            var hit = VoxelRaycast.Cast(_sim.World, from, d, reach, _view.ViewLevel, (x, y, z) => belts.Contains(new TilePos(x, y, z)));
            if (hit is not { } h || belts.At(h.Tile) is not { } at) return;
            var c = h.Tile;
            bool tower = at.Index + 1 < at.Belt.Length && Conveyors.StepOf(c, at.Belt.Cells[at.Index + 1]) == Conveyors.Step.Lift
                      || at.Index > 0 && Conveyors.StepOf(at.Belt.Cells[at.Index - 1], c) == Conveyors.Step.Lift;
            float t = h.Distance;
            for (; t < h.Distance + 1.8f; t += 0.04f)
            {
                var p = from + d * t;
                if ((int)MathF.Floor(p.X) != c.X || (int)MathF.Floor(p.Y) != c.Y || (int)MathF.Floor(p.Z) != c.Z)
                {
                    if (t > h.Distance + 0.05f) break; // left the cell
                    continue;
                }
                if (tower || p.Z - c.Z < 0.45f)
                {
                    HoveredBelt = c;
                    return;
                }
            }
            reach -= t + 0.02f;
            from += d * (t + 0.02f);
        }
    }

    private void ClearBelt()
    {
        _beltSearch = null;
        _beltFrom = null;
        _beltRoute = null;
        _beltTo = null;
        _beltKey = default;
        BeltNote = null;
        if (_beltPreview != null) _beltPreview.Visible = false;
        if (_beltMark != null) _beltMark.Visible = false;
        _endGround = null;
        HideReach();
    }

    /// <summary>Each frame with a conveyor tool: the start marked, the route to the hovered cell planned when it changes.</summary>
    private void UpdateBeltTool()
    {
        if (!IsBeltTool(Mode))
        {
            if (_beltFrom is not null || BeltNote is not null || _bubbles.Count > 0) ClearBelt();
            return;
        }
        EnsureBeltPreview();
        var target = BeltTarget();
        if (_beltFrom is not { } from)
        {
            _beltPreview!.Visible = false;
            // Before the first click: what a click there would start from.
            if (target is not { } t) { BeltNote = null; _beltMark!.Visible = false; HideReach(); return; }
            var port = StartPort(t, out var why);
            ShowMark(t, port is not null);
            BeltNote = port is { } p ? (p.Kind == PortKind.Belt ? (_sim.Conveyors.Extends(p) ? $"extend belt {p.Id} from its dead end" : $"a splitter on belt {p.Id}") : $"start here: {Conveyors.Describe(_sim, p, true, Player)}", true) : (why ?? "can't start here", false);
            if (port is { Kind: PortKind.Ground }) ShowReach(t, null);
            else HideReach();
            return;
        }
        ShowMark(_beltFromCell, true);
        // The start's 3x3, and the end's once known (where the route ends, or the open cell pointed at).
        ShowReach(from.Kind == PortKind.Belt ? null : _beltRoute?[0] ?? (from.Kind == PortKind.Ground ? _beltFromCell : null), _beltRoute is { } r && _beltTo is { Kind: not PortKind.Belt } ? r[^1] : _endGround);
        // Replanned when the target or the belts, zones or rooms change (not on every dig or build: a busy colony would
        // restart a long search before it ends; the sim checks the route again when it is placed).
        var key = (target, (Port?)from, 0L, _sim.Conveyors.Version, _sim.Stockpiles.Version, _sim.Rooms.Version, 0);
        if (key == _beltKey)
        {
            StepSearch(); // still planning: another slice
            return;
        }
        _beltKey = key;
        _beltSearch = null;
        _beltRoute = null;
        _beltTo = null;
        if (target is not { } end)
        {
            BeltNote = ("point at where it goes", true);
            _endGround = null;
            _beltPreview!.Visible = false;
            return;
        }
        var to = Conveyors.PortAt(_sim, Player, end, false, out var bad);
        _endGround = to is { Kind: PortKind.Ground } ? end : null;
        if (to is null)
        {
            BeltNote = (bad ?? "can't end here", false);
            ShowRoute([end], false);
            return;
        }
        if (to.Value.Kind == PortKind.Belt && from.Kind == PortKind.Belt && to.Value.Id == from.Id && to.Value.Index <= from.Index)
        {
            BeltNote = ("that would loop back into itself", false);
            ShowRoute([end], false);
            return;
        }
        var starts = Conveyors.EndCells(_sim, from, true);
        var goals = Conveyors.EndCells(_sim, to.Value, false);
        if (starts.Count == 0 || goals.Count == 0)
        {
            BeltNote = (starts.Count == 0 ? "no free tile beside the start" : "no free tile beside the end", false);
            ShowRoute([end], false);
            return;
        }
        // The search runs a slice a frame (a long or impossible route never stalls a frame).
        _beltSearch = new ConveyorRouter.Search(_sim, starts, goals);
        (_searchFrom, _searchTo, _searchEnd) = (from, to.Value, end);
        BeltNote = ("planning the route…", true);
        StepSearch();
    }

    private ConveyorRouter.Search? _beltSearch;
    private Port _searchFrom, _searchTo;
    private TilePos _searchEnd;
    /// <summary>Cells the route search looks at a frame (~a few ms at worst).</summary>
    private const int SearchSlice = 700;

    private void StepSearch()
    {
        if (_beltSearch is not { } search) return;
        var done = search.Run(SearchSlice);
        if (done is null) return;
        _beltSearch = null;
        var (from, to, end) = (_searchFrom, _searchTo, _searchEnd);
        if (search.Result is not { } route)
        {
            BeltNote = ("no way there (walled in, or too far: draw it in pieces)", false);
            ShowRoute([end], false);
            return;
        }
        if (Conveyors.CanPlace(_sim, Player, route, from, to) is { } why2)
        {
            BeltNote = (why2, false);
            ShowRoute(route, false);
            return;
        }
        _beltRoute = route;
        _beltTo = to;
        _beltToCell = end;
        int lifts = 0, ramps = 0, raised = 0;
        for (int i = 1; i < route.Count; i++)
        {
            var s = Conveyors.StepOf(route[i - 1], route[i]);
            if (s == Conveyors.Step.Lift) lifts++;
            else if (s != Conveyors.Step.Flat) ramps++;
        }
        foreach (var c in route)
            if (!Colony.Core.Navigation.Movement.IsStandable(_sim.World, c)) raised++;
        string shape = string.Join(", ", new[] { (ramps, "ramp"), (lifts, "lift"), (raised, "raised") }.Where(x => x.Item1 > 0).Select(x => x.Item2 == "raised" ? $"{x.Item1} raised" : UI.G.Plural(x.Item1, x.Item2)));
        BeltNote = ($"{(_sim.Conveyors.Extends(from) ? $"extends belt {from.Id}, " : "")}{Conveyors.Describe(_sim, to, false, Player)}: {route.Count} tiles{(shape.Length > 0 ? $" ({shape})" : "")} · {(_sim.Conveyors.Extends(from) ? $"{route.Count} planks" : $"{route.Count - 1} planks + 1 wooden gears")}", true);
        ShowRoute(route, true);
    }

    /// <summary>Where a belt may start for this tool: not on a belt (the conveyor tool), only on one (the splitter tool).</summary>
    private Port? StartPort(TilePos cell, out string? why)
    {
        var port = Conveyors.PortAt(_sim, Player, cell, true, out why);
        if (port is not { } p) return null;
        if (Mode == ToolMode.Splitter && p.Kind != PortKind.Belt) { why = "the splitter goes on a belt: click one"; return null; }
        if (Mode == ToolMode.Conveyor && p.Kind == PortKind.Belt && !_sim.Conveyors.Extends(p)) { why = "to branch off a belt, use the splitter (its dead end can be extended)"; return null; }
        if (p.Kind == PortKind.Belt && _sim.Conveyors.At(cell) is { } at && _sim.Conveyors.BranchAt(at.Belt, at.Index) is not null) { why = "there is a splitter there already"; return null; }
        return p;
    }

    /// <summary>A click with a conveyor tool: Shift takes a belt down; else the start, then the end (which places it).</summary>
    private void BeltClick(bool shift)
    {
        var target = BeltTarget();
        if (shift)
        {
            if (HoveredBelt is { } hb || target is { } t && _sim.Conveyors.Contains(t)) Issue(new RemoveConveyor(HoveredBelt ?? target!.Value));
            return;
        }
        if (_beltFrom is null)
        {
            if (target is not { } t) return;
            if (StartPort(t, out var why) is not { } port)
            {
                UI.GlyphStatus.Say($"Can't start a belt here: {why}", 3);
                return;
            }
            _beltFrom = port;
            _beltFromCell = t;
            _beltKey = default;
            return;
        }
        if (_beltRoute is not { } route || _beltTo is not { } to)
        {
            if (BeltNote is { Good: false } note) UI.GlyphStatus.Say($"Can't place the belt: {note.Text}", 3);
            return;
        }
        var from = _beltFrom.Value;
        Issue(new PlaceConveyor(route[0], Conveyors.Encode(route), from.Kind, _beltFromCell, to.Kind, _beltToCell));
        ClearBelt();
    }

    /// <summary>Esc / a right click with a start picked: lets go of it (the tool stays in hand).</summary>
    private bool CancelBelt()
    {
        if (!IsBeltTool(Mode) || _beltFrom is null) return false;
        ClearBelt();
        return true;
    }

    private void EnsureBeltPreview()
    {
        if (_beltPreview != null) return;
        _beltPreview = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new BoxMesh { Size = new Vector3(0.56f, 0.12f, 0.56f) },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_beltPreview);
        _beltMark = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.04f, 0.3f, 1.04f) },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_beltMark);
    }

    private void ShowMark(TilePos cell, bool ok)
    {
        _beltMark!.Position = new Vector3(cell.X + 0.5f, cell.Z + 0.15f, cell.Y + 0.5f);
        _beltMark.MaterialOverride = ok ? _ghostOk : _ghostBad;
        _beltMark.Visible = true;
    }

    private void ShowRoute(List<TilePos> cells, bool ok)
    {
        var mm = _beltPreview!.Multimesh;
        mm.InstanceCount = cells.Count;
        for (int i = 0; i < cells.Count; i++) mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, ConveyorView.Tile(cells, i).Centre));
        _beltPreview.MaterialOverride = ok ? _ghostOk : _ghostBad;
        _beltPreview.Visible = true;
    }

    // ---- What each end works with: its 3x3, the crates and stockpiles there, and a bubble of what it picks up --------

    /// <summary>A bubble over a belt end (Godot position): what it picks up (or delivers into) and the most plentiful items.</summary>
    public readonly record struct BeltBubble(Vector3 At, string Caption, List<(Colony.Core.Items.ItemType Type, int Count)> Items, int More, bool Source);

    /// <summary>Bubbles the conveyor tools show (drawn by the UI over the world).</summary>
    public IReadOnlyList<BeltBubble> BeltBubbles => IsBeltTool(Mode) ? _bubbles : [];
    private readonly List<BeltBubble> _bubbles = new();
    private MultiMeshInstance3D? _beltArea, _beltReach;
    private (TilePos? A, TilePos? B, int Items, int Zones, int Rooms) _reachKey = (null, null, -1, -1, -1);
    private TilePos? _endGround;

    private MultiMeshInstance3D Plates(Color color, float size)
    {
        var mmi = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new BoxMesh { Size = new Vector3(size, 0.04f, size), Material = Overlay.Material(color) },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(mmi);
        return mmi;
    }

    /// <summary>
    /// Marks the 3x3 round a belt's start (and its end, if known) and the crates and whole stockpiles there that it loads
    /// from (or delivers into), with a bubble each. Redone when the cells, the goods or the zones change.
    /// </summary>
    private void ShowReach(TilePos? start, TilePos? end)
    {
        var key = (start, end, _sim.Items.LayoutVersion, _sim.Stockpiles.Version, _sim.Rooms.Version);
        if (key == _reachKey) return;
        _reachKey = key;
        _beltArea ??= Plates(new Color(1f, 1f, 1f, 0.16f), 0.94f);
        _beltReach ??= Plates(new Color(1f, 0.72f, 0.3f, 0.42f), 0.9f);
        _bubbles.Clear();
        var area = new List<Transform3D>();
        var reach = new List<Transform3D>();
        var marked = new HashSet<TilePos>();
        void Mark(TilePos c)
        {
            if (marked.Count < 4000 && marked.Add(c)) reach.Add(new Transform3D(Basis.Identity, new Vector3(c.X + 0.5f, c.Z + 0.05f, c.Y + 0.5f)));
        }
        foreach (var (cell, source) in new[] { (start, true), (end, false) })
        {
            if (cell is not { } c) continue;
            foreach (var a in Conveyors.Around(c)) area.Add(new Transform3D(Basis.Identity, new Vector3(a.X + 0.5f, a.Z + 0.03f, a.Y + 0.5f)));
            var (outs, ins, stores) = Conveyors.Reach(_sim, Player, c);
            var counts = new Dictionary<Colony.Core.Items.ItemType, int>();
            void Count(TilePos p)
            {
                if (_sim.Items.TryGet(p, out var pile) && pile.Count > 0) counts[pile.Type] = counts.GetValueOrDefault(pile.Type) + pile.Count;
            }
            foreach (var crate in source ? outs : ins)
            {
                Mark(crate.Cells[0]);
                if (source) Count(crate.Cells[0]);
                else foreach (var (item, wanted) in crate.Room.InputNeeds()) counts[item] = counts.GetValueOrDefault(item) + wanted;
            }
            int scanned = 0;
            foreach (var z in stores)
                foreach (var p in _sim.Stockpiles.CellsOf(z))
                {
                    Mark(p);
                    if (source && ++scanned < 6000) Count(p);
                }
            var top = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Index).ToList();
            string caption = Conveyors.Describe(_sim, new Port(PortKind.Ground, c), source, Player);
            if (caption == "from a hopper") caption = "a hopper: haulers bring it loose goods";
            else if (caption.StartsWith("from ")) caption = "picks up from " + caption[5..];
            if (!source && outs.Count + ins.Count + stores.Count == 0) caption = "drops a workshop's products here";
            var items = top.Take(4).Select(kv => (kv.Key, kv.Value)).ToList();
            _bubbles.Add(new BeltBubble(new Vector3(c.X + 0.5f, c.Z + 1.4f, c.Y + 0.5f), caption, items, Math.Max(0, top.Count - items.Count), source));
        }
        void Fill(MultiMeshInstance3D mmi, List<Transform3D> list)
        {
            mmi.Multimesh.InstanceCount = list.Count;
            for (int i = 0; i < list.Count; i++) mmi.Multimesh.SetInstanceTransform(i, list[i]);
            mmi.Visible = list.Count > 0;
        }
        Fill(_beltArea, area);
        Fill(_beltReach, reach);
    }

    private void HideReach()
    {
        _bubbles.Clear();
        _reachKey = (null, null, -1, -1, -1);
        if (_beltArea != null) _beltArea.Visible = false;
        if (_beltReach != null) _beltReach.Visible = false;
    }
}
