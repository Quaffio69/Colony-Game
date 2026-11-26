using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core;
using Colony.Core.Agents;
using Colony.Core.Combat;
using Colony.Core.Industry;
using Colony.Core.Orders;
using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>What the player clicked in the world (with no tool in hand): shown in the Inspect window and highlighted.</summary>
public abstract record Pick;
public sealed record PickColonist(Colonist Colonist) : Pick;
public sealed record PickRoom(Room Room) : Pick;
/// <summary>A stockpile zone (or farm field): every zoned cell joined to this one.</summary>
public sealed record PickZone(TilePos Cell, bool Farm) : Pick;
public sealed record PickPile(TilePos Cell) : Pick;
public sealed record PickSite(Blueprint Blueprint) : Pick;
public sealed record PickNest(Nest Nest) : Pick;
public sealed record PickTile(TilePos Tile) : Pick;

/// <summary>
/// The selection: a click with no tool picks the most interesting thing under the cursor (a colonist, a nest, a
/// workshop or home, a stockpile or farm field, a blueprint, a pile, else the tile) and the Inspect window shows it.
/// Highlighted in the world: accent plates over a room or zone, a ring under a colonist, a box round anything else.
/// Esc or a right click (once no tool is in hand) clears it. <see cref="Follow"/> keeps the camera on a colonist.
/// </summary>
public partial class SelectionView : Node3D
{
    private ColonySim _sim = null!;
    private WorldView _world = null!;
    private CameraRig _rig = null!;
    private DesignationTool _tool = null!;
    private MultiMeshInstance3D _hoverPlates = null!;
    private Pick? _hoverDrawn;
    private readonly HashSet<TilePos> _hoverCells = new();
    private int _hoverVersion = -1;
    private MultiMeshInstance3D _plates = null!;
    private MeshInstance3D _ring = null!, _box = null!;
    private Pick? _drawn;
    private int _drawnVersion = -1, _drawnLevel = int.MinValue;
    private readonly List<TilePos> _zone = new();
    private const int MaxZone = 20_000;

    public Pick? Current { get; private set; }
    /// <summary>The camera follows the selected colonist (WASD or a right-drag stops it).</summary>
    public bool Follow { get; set; }
    /// <summary>Bumps when the selection changes (the Inspect window redraws).</summary>
    public int Serial { get; private set; }

    public void Initialize(ColonySim sim, WorldView world, CameraRig rig, DesignationTool tool)
    {
        _sim = sim;
        _world = world;
        _rig = rig;
        _tool = tool;
        _hoverPlates = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new PlaneMesh { Size = new Vector2(1f, 1f), Material = Overlay.Material(new Color(1f, 1f, 1f, 0.12f)) },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_hoverPlates);
        var accent = new Color(1f, 0.82f, 0.35f);
        _plates = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new PlaneMesh { Size = new Vector2(1f, 1f), Material = Overlay.Material(accent with { A = 0.28f }) },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_plates);
        _ring = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.36f, OuterRadius = 0.46f, Rings = 24, RingSegments = 6 },
            MaterialOverride = Overlay.Material(accent with { A = 0.85f }),
            Scale = new Vector3(1, 0.3f, 1),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_ring);
        _box = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = Vector3.One * 1.08f },
            MaterialOverride = Overlay.Material(accent with { A = 0.22f }),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_box);
    }

    /// <summary>
    /// Selects what is at (or standing on) the clicked tile, if it is something of interest (a colonist or machine, a nest,
    /// a room, a zone, a blueprint); a bare tile or a pile of goods clears the selection instead (the hover card names those).
    /// </summary>
    public void PickAt(TilePos? tile, int player)
    {
        var pick = tile is { } t ? Find(t, player) : null;
        Set(Selectable(pick) ? pick : null);
    }

    /// <summary>Whether a click selects this (and opens Inspect): not a bare tile or a pile.</summary>
    public static bool Selectable(Pick? pick) => pick is not (null or PickTile or PickPile);

    public void Select(Pick? pick) => Set(pick);

    /// <summary>Clears the selection. False if there was none.</summary>
    public bool Clear()
    {
        if (Current == null) return false;
        Set(null);
        return true;
    }

    private void Set(Pick? pick)
    {
        Current = pick;
        if (pick is not PickColonist) Follow = false;
        Serial++;
    }

    /// <summary>What a click on this tile would select.</summary>
    public Pick PickFor(TilePos tile, int player) => Find(tile, player);

    private Pick Find(TilePos tile, int player)
    {
        var cell = tile.Offset(0, 0, 1);
        // A colonist in the cell (yours first); one beside it only when the cell holds nothing else (they are small targets).
        var c = _sim.Colonists.Where(k => k.Position == cell).OrderBy(k => k.Faction == player ? 0 : 1).FirstOrDefault();
        if (c != null) return new PickColonist(c);
        if (_sim.Wildlife.NestNear(cell, 1) is { } nest) return new PickNest(nest);
        if (_sim.Rooms.RoomAt(cell) is { } room) return new PickRoom(room);
        if (_sim.Stockpiles.Contains(cell)) return new PickZone(cell, false);
        if (_sim.Farms.Contains(cell)) return new PickZone(cell, true);
        if ((_sim.Constructions.At(cell) ?? _sim.Constructions.CoverAt(cell, false) ?? _sim.Constructions.CoverAt(cell, true)) is { } bp) return new PickSite(bp);
        if (_sim.Items.TryGet(cell, out var pile) && pile.Count > 0) return new PickPile(cell);
        var near = _sim.Colonists.Where(k => k.Position.Z == cell.Z && Math.Abs(k.Position.X - cell.X) <= 1 && Math.Abs(k.Position.Y - cell.Y) <= 1)
            .OrderBy(k => k.Faction == player ? 0 : 1).FirstOrDefault();
        return near != null ? new PickColonist(near) : new PickTile(tile);
    }

    /// <summary>The zone's cells (stockpile or farm) joined to <paramref name="start"/>: same level 8-way, or a level up or down.</summary>
    public List<TilePos> ZoneCells(TilePos start, bool farm)
    {
        // A stockpile is its own cells (two touching stockpiles stay apart); farms and depots are what joins up.
        if (!farm && _sim.Stockpiles.ZoneAt(start) is { } store) return _sim.Stockpiles.CellsOf(store).Take(MaxZone).ToList();
        bool In(TilePos p) => farm ? _sim.Farms.Contains(p) : _sim.Stockpiles.Contains(p) && _sim.Stockpiles.ZoneAt(p) == null;
        var seen = new HashSet<TilePos> { start };
        var queue = new Queue<TilePos>();
        queue.Enqueue(start);
        var result = new List<TilePos>();
        while (queue.Count > 0 && result.Count < MaxZone)
        {
            var p = queue.Dequeue();
            result.Add(p);
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;
                var n = p.Offset(dx, dy, dz);
                if (In(n) && seen.Add(n)) queue.Enqueue(n);
            }
        }
        return result;
    }

    /// <summary>Where the selection is in the world (for "Go to").</summary>
    public TilePos? Where => Current switch
    {
        PickColonist p => p.Colonist.Position,
        PickRoom p => p.Room.Cells.First(),
        PickZone p => p.Cell,
        PickPile p => p.Cell,
        PickSite p => p.Blueprint.Cell,
        PickNest p => p.Nest.Entrance,
        PickTile p => p.Tile,
        _ => null,
    };

    /// <summary>Still there? (a colonist who left, a removed room, a built blueprint ... clear the selection).</summary>
    private bool Valid(Pick p) => p switch
    {
        PickColonist c => _sim.Colonists.Contains(c.Colonist),
        PickRoom r => _sim.Rooms.All.Contains(r.Room),
        PickZone z => z.Farm ? _sim.Farms.Contains(z.Cell) : _sim.Stockpiles.Contains(z.Cell),
        PickPile pl => _sim.Items.TryGet(pl.Cell, out var pile) && pile.Count > 0,
        PickSite s => !s.Blueprint.Removed,
        PickNest n => _sim.Wildlife.Nests.Contains(n.Nest),
        _ => true,
    };

    public override void _Process(double delta)
    {
        if (_sim == null) return;
        if (Current is { } cur && !Valid(cur)) Set(null);
        int level = _world.ViewLevel;
        var pick = Current;

        // Follow: the camera keeps the colonist in the middle until the player pans.
        if (Follow && pick is PickColonist fc)
        {
            if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.D) || Input.IsMouseButtonPressed(MouseButton.Right))
                Follow = false;
            else
            {
                var target = SimSpace.TileCenter(fc.Colonist.Position);
                _rig.Position = _rig.Position.Lerp(new Vector3(target.X, _rig.Position.Y, target.Z), 1f - Mathf.Exp(-(float)delta * 6f));
            }
        }

        _ring.Visible = pick is PickColonist;
        if (pick is PickColonist pc)
        {
            var p = pc.Colonist.Position;
            var at = new Vector3(p.X + 0.5f, p.Z + 0.03f, p.Y + 0.5f);
            if (pc.Colonist.NextStep is { } next) at = at.Lerp(new Vector3(next.X + 0.5f, next.Z + 0.03f, next.Y + 0.5f), pc.Colonist.StepProgress);
            _ring.Position = at;
            _ring.Visible = p.Z <= level + 1;
        }

        var boxAt = pick switch
        {
            PickPile p => (TilePos?)p.Cell,
            PickSite p => p.Blueprint.Cell,
            PickNest p => p.Nest.Entrance,
            PickTile p => p.Tile,
            _ => null,
        };
        _box.Visible = boxAt is { } b && b.Z <= level + 1;
        if (boxAt is { } bc) _box.Position = SimSpace.TileCenter(bc);

        // Plates over a room's or zone's cells: redrawn when the selection, its cells or the view level change.
        int version = pick switch
        {
            PickRoom => _sim.Rooms.Version,
            PickZone z => z.Farm ? _sim.Farms.Version : _sim.Stockpiles.Version,
            _ => 0,
        };
        if (!ReferenceEquals(pick, _drawn) || version != _drawnVersion || level != _drawnLevel)
        {
            _drawn = pick;
            _drawnVersion = version;
            _drawnLevel = level;
            _zone.Clear();
            if (pick is PickRoom r) _zone.AddRange(r.Room.Cells);
            else if (pick is PickZone z) _zone.AddRange(ZoneCells(z.Cell, z.Farm));
            var visible = _zone.Where(c => c.Z <= level + 1).Take(MaxZone).ToList();
            var mm = _plates.Multimesh;
            mm.InstanceCount = visible.Count;
            for (int i = 0; i < visible.Count; i++)
                mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(visible[i].X + 0.5f, visible[i].Z + 0.05f, visible[i].Y + 0.5f)));
            _plates.Visible = visible.Count > 0;
        }
        UpdateHover(level);
    }

    /// <summary>
    /// With no tool in hand, the room or zone under the cursor gets faint plates: what a click would select. Redrawn only
    /// when the cursor leaves the drawn cells (or they change), so sliding over a big stockpile costs nothing.
    /// </summary>
    private void UpdateHover(int level)
    {
        Pick? hover = null;
        if (_tool.Mode == DesignationTool.ToolMode.None && !_tool.SquadOrders && _tool.Hovered is { } h)
        {
            var cell = h.Offset(0, 0, 1);
            if (_sim.Rooms.RoomAt(cell) is { } room) hover = new PickRoom(room);
            else if (_sim.Stockpiles.Contains(cell)) hover = new PickZone(cell, false);
            else if (_sim.Farms.Contains(cell)) hover = new PickZone(cell, true);
        }
        // Not when it is what is selected already (its own plates show it).
        if (hover is PickRoom hr && Current is PickRoom sr && hr.Room == sr.Room) hover = null;
        if (hover is PickZone hz && Current is PickZone sz && _zone.Contains(hz.Cell) && hz.Farm == sz.Farm) hover = null;

        int version = hover switch { PickRoom => _sim.Rooms.Version, PickZone z => z.Farm ? _sim.Farms.Version : _sim.Stockpiles.Version, _ => 0 };
        bool same = version == _hoverVersion && level == _drawnLevelHover && hover switch
        {
            null => _hoverDrawn == null,
            PickRoom r => _hoverDrawn is PickRoom d && d.Room == r.Room,
            PickZone z => _hoverDrawn is PickZone d && d.Farm == z.Farm && _hoverCells.Contains(z.Cell),
            _ => false,
        };
        if (same) return;
        _hoverDrawn = hover;
        _hoverVersion = version;
        _drawnLevelHover = level;
        _hoverCells.Clear();
        if (hover is PickRoom pr) _hoverCells.UnionWith(pr.Room.Cells);
        else if (hover is PickZone pz) _hoverCells.UnionWith(ZoneCells(pz.Cell, pz.Farm));
        var visible = _hoverCells.Where(c => c.Z <= level + 1).Take(MaxZone).ToList();
        var mm = _hoverPlates.Multimesh;
        mm.InstanceCount = visible.Count;
        for (int i = 0; i < visible.Count; i++)
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(visible[i].X + 0.5f, visible[i].Z + 0.045f, visible[i].Y + 0.5f)));
        _hoverPlates.Visible = visible.Count > 0;
    }

    private int _drawnLevelHover = int.MinValue;

    /// <summary>The selected zone's cells as last drawn (the Inspect window sums its contents).</summary>
    public IReadOnlyList<TilePos> Zone => _zone;
}
