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
        float reach = ground?.Distance + 0.01f ?? 5000f;
        var hit = VoxelRaycast.Cast(_sim.World, origin, dir, reach, _view.ViewLevel, (x, y, z) => belts.Contains(new TilePos(x, y, z)));
        if (hit is { } h && belts.Contains(h.Tile)) HoveredBelt = h.Tile;
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
    }

    /// <summary>Each frame with a conveyor tool: the start marked, the route to the hovered cell planned when it changes.</summary>
    private void UpdateBeltTool()
    {
        if (!IsBeltTool(Mode))
        {
            if (_beltFrom is not null || BeltNote is not null) ClearBelt();
            return;
        }
        EnsureBeltPreview();
        var target = BeltTarget();
        if (_beltFrom is not { } from)
        {
            _beltPreview!.Visible = false;
            // Before the first click: what a click there would start from.
            if (target is not { } t) { BeltNote = null; _beltMark!.Visible = false; return; }
            var port = StartPort(t, out var why);
            ShowMark(t, port is not null);
            BeltNote = port is { } p ? ($"start {Conveyors.Describe(_sim, p, true)}", true) : (why ?? "can't start here", false);
            return;
        }
        ShowMark(_beltFromCell, true);
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
            _beltPreview!.Visible = false;
            return;
        }
        var to = Conveyors.PortAt(_sim, Player, end, false, out var bad);
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
        BeltNote = ($"{Conveyors.Describe(_sim, to, false)}: {route.Count} tiles{(shape.Length > 0 ? $" ({shape})" : "")} · {route.Count - 1} planks + 1 wooden gears", true);
        ShowRoute(route, true);
    }

    /// <summary>Where a belt may start for this tool: not on a belt (the conveyor tool), only on one (the splitter tool).</summary>
    private Port? StartPort(TilePos cell, out string? why)
    {
        var port = Conveyors.PortAt(_sim, Player, cell, true, out why);
        if (port is not { } p) return null;
        if (Mode == ToolMode.Splitter && p.Kind != PortKind.Belt) { why = "the splitter goes on a belt: click one"; return null; }
        if (Mode == ToolMode.Conveyor && p.Kind == PortKind.Belt) { why = "to branch off a belt, use the splitter"; return null; }
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
        for (int i = 0; i < cells.Count; i++) mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, ConveyorView.Point(cells[i])));
        _beltPreview.MaterialOverride = ok ? _ghostOk : _ghostBad;
        _beltPreview.Visible = true;
    }
}
