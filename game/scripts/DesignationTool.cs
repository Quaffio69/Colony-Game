using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core;
using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Nature;
using Colony.Core.Orders;
using Colony.Core.World;
using Godot;
using static ColonyGame.MeshKit;

namespace ColonyGame;

/// <summary>
/// Mouse picking, DF-style box orders, zones, and the factory builder.
/// <para>
/// No tool by default (<see cref="ToolMode.None"/>): left-drag moves the view up and down levels, right-drag pans (the
/// camera rig), tapping Shift / Ctrl goes a level up / down. Box tools: drag a box and let go, or click one corner and
/// then the other (Z/X or the Shift/Ctrl taps extend the box across levels; the view follows); Shift starts a removal
/// box instead. A right click (not a drag) steps back: aborts the box, then drops the tool. Esc too.
/// </para>
/// <list type="bullet">
/// <item>Orders (their DF-style hotkeys, see <see cref="UI.Tools.Hotkeys"/>; Tab / Shift+Tab cycle): dig, stairs
/// (auto / up / down / up-down), chop trees and gather plants (2D areas draped on the surface), stockpile zones, farms
/// (grain, herbs, flax, glowcaps), stone blocks, and the cancel and demolish boxes.</item>
/// <item>Build (B opens the build menu): pick a workshop to mark out its room (a box over the floor, any number
/// of levels; it must stay one walkable piece, which the core enforces), then pick one of its stations, tools,
/// lanterns or crates and click to place. R rotates, [ ] change size, F (Shift+F) changes what the machine
/// under the cursor makes. A ghost shows the footprint in green or red with the reason. Removal boxes erase
/// workshop cells (room tool) or remove parts (part tools).</item>
/// <item>One wall tool (<see cref="ToolMode.Edge"/>) for walls, doors and windows: click an edge for one piece, drag
/// along it for a line, or drag away from it to wall round a box (walls only).</item>
/// </list>
/// Names, icons and hints for every mode live in <see cref="UI.Tools"/>.
/// </summary>
public partial class DesignationTool : Node3D
{
    public enum ToolMode
    {
        // Orders
        None, Dig, Stairs, UpStair, DownStair, UpDownStair, Chop, Gather, Stockpile, FarmGrain, FarmHerbs, FarmGlowcaps, FarmFlax, Block, Demolish, Cancel,
        // Build (which workshop / part / structure: see Workshop, Part and Structure)
        Room, Part, Structure, Edge, Floor, Roof, Rack, TradeDepot, House, Conveyor, Splitter,
        // Dev mode: drop an item, machine or colonist with a click (see SpawnItem)
        Spawn,
    }

    private const ToolMode LastOrder = ToolMode.Cancel;
    private enum Selection { None, Designate, Cancel }

    private ColonySim _sim = null!;
    private WorldView _view = null!;

    private MeshInstance3D _cursor = null!;
    private MeshInstance3D _box = null!;
    private MeshInstance3D _ghost = null!;
    private readonly Dictionary<ToolMode, StandardMaterial3D> _modeMats = new();
    private StandardMaterial3D _cancelMat = null!, _ghostOk = null!, _ghostBad = null!;

    private Selection _selection;
    private TilePos _anchor;
    private int _cornerZ, _cornerX, _cornerY;
    private bool _rotated;
    private readonly Dictionary<PartType, int> _sizes = new();

    // Plant orders (chop, gather) are picked as 2D areas drawn draped over the surface, whatever level is in view.
    private MultiMeshInstance3D _drape = null!;
    private StandardMaterial3D _drapeCancelMat = null!;
    private readonly Dictionary<ToolMode, StandardMaterial3D> _drapeMats = new();
    private readonly Dictionary<(int X, int Y), int> _surface = new();
    private (int X0, int Y0, int X1, int Y1) _drapeRect = (-1, -1, -1, -1);
    private const int MaxDrapeColumns = 160_000; // beyond this only the outline is drawn

    /// <summary>Selected size (1..<see cref="PartInfo.Sizes"/>) for a part family; [ and ] change it.</summary>
    public int SizeFor(PartType part) => PartInfo.ClampSize(part, _sizes.GetValueOrDefault(part, 1));
    private static bool IsAreaTool(ToolMode m) => m is ToolMode.Chop or ToolMode.Gather;
    private static PlantKind? CropFor(ToolMode m) => m switch
    {
        ToolMode.FarmGrain => PlantKind.Wheat,
        ToolMode.FarmHerbs => PlantKind.Herbs,
        ToolMode.FarmGlowcaps => PlantKind.Glowcap,
        ToolMode.FarmFlax => PlantKind.Flax,
        _ => null,
    };

    public ToolMode Mode { get; private set; } = ToolMode.None;
    /// <summary>The workshop the build menu is on (its room is painted by <see cref="ToolMode.Room"/>).</summary>
    public RoomType Workshop { get; private set; } = RoomType.Carpentry;
    /// <summary>The part placed by <see cref="ToolMode.Part"/>.</summary>
    public PartType Part { get; private set; } = PartType.Sawbench;
    /// <summary>The structure placed by <see cref="ToolMode.Structure"/> (water wheel or pump).</summary>
    public StructureKind Structure { get; private set; } = StructureKind.WaterWheel;
    private int _facing;
    /// <summary>The edge piece placed by <see cref="ToolMode.Edge"/> (wall, door or window) and the wall material.</summary>
    public StructureKind EdgePiece { get; private set; } = StructureKind.EdgeWall;
    public int WallMaterial { get; private set; } = 2;
    private (TilePos Cell, int Side)? _hoverEdge;
    /// <summary>The edge a wall drag started on, and where the mouse is on that level's floor plane.</summary>
    private (TilePos Cell, int Side) _lineStart;
    private System.Numerics.Vector2 _edgeAt;
    /// <summary>Where (screen) the press that started a box was: letting go far from it applies the box (drag-and-release).</summary>
    private Vector2? _pressAt;
    /// <summary>Floor and roof material for the Floor and Roof box tools.</summary>
    public int FloorMaterial { get; private set; } = 1;
    public int RoofMaterial { get; private set; } = 1;
    public TilePos? Hovered { get; private set; }
    public string? LastAction { get; private set; }
    /// <summary>Why the part under the cursor can't be placed (null if it can, or not placing).</summary>
    public string? PlacementProblem { get; private set; }

    public static bool IsBuild(ToolMode m) => m > LastOrder;

    /// <summary>The squad tool is on: clicks order the drafted about instead of designating (G, or the Military window).</summary>
    public bool SquadOrders
    {
        get => _squadOrders;
        set
        {
            _squadOrders = value;
            if (value) EndSelection();
        }
    }
    private bool _squadOrders;
    public PartType? PartFor(ToolMode m) => m == ToolMode.Part ? Part : null;

    /// <summary>The box (or wall drag) in progress removes rather than places.</summary>
    public bool Removing => _selection == Selection.Cancel;
    /// <summary>Chop and gather: areas on the surface, whatever level is in view.</summary>
    public bool IsArea => IsAreaTool(Mode);

    /// <summary>Pieces the wall drag in progress covers (a line, or round a box), null when not dragging a wall.</summary>
    public int? EdgePieces
    {
        get
        {
            if (_selection == Selection.None || Mode != ToolMode.Edge) return null;
            var d = EdgeDrag();
            return d.Box ? 2 * (Math.Abs(d.A.X - d.B.X) + 1) + 2 * (Math.Abs(d.A.Y - d.B.Y) + 1) : Math.Abs(d.To - d.From) + 1;
        }
    }

    /// <summary>The wall drag so far: a line along the start edge (From..To along it), or a box beside it (A..B, ground level).</summary>
    private (bool Box, int From, int To, TilePos A, TilePos B) EdgeDrag()
    {
        var (cell, side) = _lineStart;
        bool east = side == EdgeInfo.East;
        float along = east ? _edgeAt.Y : _edgeAt.X, across = east ? _edgeAt.X : _edgeAt.Y;
        int line = east ? cell.X + 1 : cell.Y + 1; // the grid line the edge lies on
        int from = east ? cell.Y : cell.X;
        int to = Math.Clamp((int)MathF.Floor(along), 0, (east ? _sim.World.Depth : _sim.World.Width) - 1);
        // Within half a cell of the edge's line: a line of pieces. Further out (walls only): the box between the edge and the mouse.
        if (EdgePiece != StructureKind.EdgeWall || MathF.Abs(across - line) < 0.5f) return (false, from, to, default, default);
        int first = across > line ? line : line - 1;
        int far = Math.Clamp((int)MathF.Floor(across), 0, (east ? _sim.World.Width : _sim.World.Depth) - 1);
        int z = cell.Z - 1; // the ground under the walls (PlaceOutline builds a level above it)
        return east
            ? (true, from, to, new TilePos(first, from, z), new TilePos(far, to, z))
            : (true, from, to, new TilePos(from, first, z), new TilePos(to, far, z));
    }

    public void Initialize(ColonySim sim, WorldView view)
    {
        _sim = sim;
        _view = view;

        _cursor = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = Vector3.One * 1.06f, Material = Overlay.Material(new Color(1, 1, 1, 0.35f)) },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_cursor);

        foreach (var mode in Enum.GetValues<ToolMode>())
            _modeMats[mode] = Overlay.Material(
                mode == ToolMode.Stockpile ? ItemView.StockpileColor with { A = 0.3f }
                : mode == ToolMode.TradeDepot ? ItemView.DepotColor with { A = 0.35f }
                : CropFor(mode) is not null ? ItemView.FarmColor with { A = 0.3f }
                : mode == ToolMode.Block ? new Color(0.75f, 0.72f, 0.68f, 0.35f)
                : mode is ToolMode.Cancel or ToolMode.Demolish ? new Color(1f, 0.25f, 0.2f, 0.3f)
                : mode == ToolMode.None ? new Color(1, 1, 1, 0.2f)
                : IsBuild(mode) ? new Color(1f, 0.68f, 0.3f, 0.3f)
                : WorldView.DesignationColor(KindFor(mode, 0, 0, 0)) with { A = 0.3f });
        _cancelMat = Overlay.Material(new Color(1f, 0.25f, 0.2f, 0.3f));
        _ghostOk = Overlay.Material(new Color(0.3f, 1f, 0.4f, 0.4f));
        _ghostBad = Overlay.Material(new Color(1f, 0.3f, 0.25f, 0.4f));
        _box = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = Vector3.One },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_box);
        _ghost = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = Vector3.One },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_ghost);

        foreach (var m in new[] { ToolMode.Chop, ToolMode.Gather })
            _drapeMats[m] = Overlay.Material(WorldView.DesignationColor(KindFor(m, 0, 0, 0)) with { A = 0.35f });
        _drapeCancelMat = Overlay.Material(new Color(1f, 0.25f, 0.2f, 0.35f));
        _drape = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new PlaneMesh { Size = Vector2.One },
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_drape);
    }

    // ---- Tool selection (the Build window drives these; Tab cycles the orders) ---------------------------

    /// <summary>Picks an order or zone tool (dig, stairs, chop, gather, stockpile, farm plots, dam walls).</summary>
    public void SelectOrder(ToolMode mode)
    {
        Mode = mode;
        EndSelection();
    }

    /// <summary>The wall tool with a piece (wall, door or window) and, for walls, a material.</summary>
    public void SelectWall(StructureKind piece, int material)
    {
        Mode = ToolMode.Edge;
        EdgePiece = piece;
        WallMaterial = WallMaterials.Clamp(material);
        EndSelection();
    }

    public void SelectFloor(int material)
    {
        FloorMaterial = material;
        Mode = ToolMode.Floor;
        EndSelection();
    }

    public void SelectRoof(int material)
    {
        RoofMaterial = material;
        Mode = ToolMode.Roof;
        EndSelection();
    }

    /// <summary>Priority (1 first ... 5 last) the dig and stair tools give their orders ([ and ]).</summary>
    public int DigPriority { get; private set; } = Designations.DefaultPriority;

    /// <summary>Tools whose orders take a priority: digging and carving stairs.</summary>
    public static bool IsDigTool(ToolMode m) => m is ToolMode.Dig or ToolMode.Stairs or ToolMode.UpStair or ToolMode.DownStair or ToolMode.UpDownStair;

    /// <summary>Storeys of the house the House tool plans ([ and ], or Z/X while dragging).</summary>
    public int Storeys { get; private set; } = 1;

    /// <summary>The House tool: a whole building over a box, in the wall, floor and roof materials last picked.</summary>
    public void SelectHouse()
    {
        Mode = ToolMode.House;
        EndSelection();
    }

    private void ChangeStoreys(int step)
    {
        Storeys = Math.Clamp(Storeys + step, 1, Houses.MaxStoreys);
        if (_selection != Selection.None) UpdateBox();
    }

    /// <summary>R: rotates the part (or turns the waterworks) being placed.</summary>
    public void Rotate()
    {
        _rotated = !_rotated;
        _facing = (_facing + 1) % 4;
    }

    public bool Rotated => _rotated;

    /// <summary>[ and ]: the size of the part being placed.</summary>
    public void ChangeSize(int step)
    {
        if (Mode == ToolMode.House) { ChangeStoreys(step); return; }
        if (IsDigTool(Mode)) { DigPriority = Math.Clamp(DigPriority + step, 1, Designations.MaxPriority); return; }
        if (PartFor(Mode) is { } part && PartInfo.Sizes(part) > 1)
            _sizes[part] = PartInfo.ClampSize(part, SizeFor(part) + step);
    }

    /// <summary>
    /// Esc, or a right click: one step back. Ends squad orders, aborts a box or line in progress, else drops the tool
    /// (no tool: drag moves levels). False if there was nothing to undo.
    /// </summary>
    public bool CancelPlacement()
    {
        if (SquadOrders)
        {
            SquadOrders = false;
            return true;
        }
        if (CancelBelt()) return true;
        if (_selection != Selection.None)
        {
            EndSelection();
            return true;
        }
        if (Mode != ToolMode.None)
        {
            Mode = ToolMode.None;
            EndSelection();
            return true;
        }
        return NothingToCancel?.Invoke() ?? false;
    }

    /// <summary>The last step back, once no tool is in hand (the game clears what is selected). True if it did something.</summary>
    public Func<bool>? NothingToCancel { get; set; }

    /// <summary>A click (not a drag) with no tool in hand: the tile under the cursor (null: nothing), to select what is there.</summary>
    public event Action<TilePos?>? Clicked;

    /// <summary>The box being dragged, in tiles (null when not boxing).</summary>
    public (int W, int D, int H)? BoxSize
    {
        get
        {
            if (_selection == Selection.None || Mode == ToolMode.Edge) return null;
            var (a, b) = Corners();
            return (Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1, IsAreaTool(Mode) ? 1 : Mode == ToolMode.House ? Storeys : Math.Abs(a.Z - b.Z) + 1);
        }
    }

    /// <summary>Leaves any build tool (the Build window closed).</summary>
    public void LeaveBuild()
    {
        if (IsBuild(Mode)) Mode = ToolMode.None;
        EndSelection();
    }

    /// <summary>Storage racks over stockpile cells (a box; R turns their fronts).</summary>
    public void SelectRack()
    {
        Mode = ToolMode.Rack;
        EndSelection();
    }

    /// <summary>Dev mode: what a click with the spawn tool drops (a catalog item id, or "colonist"), and how many.</summary>
    public string SpawnItem { get; private set; } = "";
    public int SpawnCount { get; private set; } = 1;

    public void SelectSpawn(string itemId, int count)
    {
        (SpawnItem, SpawnCount) = (itemId, count);
        Mode = ToolMode.Spawn;
        EndSelection();
    }

    /// <summary>A trade depot zone (a box of floor, like a stockpile).</summary>
    public void SelectTradeDepot()
    {
        Mode = ToolMode.TradeDepot;
        EndSelection();
    }

    /// <summary>
    /// An order's hotkey (see <see cref="UI.OrdersRibbon"/>): picks it; the farm key again goes on to the next crop.
    /// </summary>
    public void PickOrder(ToolMode mode)
    {
        if (mode == ToolMode.FarmGrain && CropFor(Mode) is not null)
            mode = Mode switch { ToolMode.FarmGrain => ToolMode.FarmHerbs, ToolMode.FarmHerbs => ToolMode.FarmFlax, ToolMode.FarmFlax => ToolMode.FarmGlowcaps, _ => ToolMode.FarmGrain };
        SelectOrder(mode);
    }

    /// <summary>
    /// One level up (+1) or down: moves a box's free corner while boxing (the view follows), else the view level.
    /// Shift / Ctrl taps, Z/X and PgUp/PgDn.
    /// </summary>
    public void StepLevel(int step)
    {
        if (_sim == null) return;
        if (_selection != Selection.None && Mode == ToolMode.House)
        {
            ChangeStoreys(step); // a house rises by storeys from its ground floor
            return;
        }
        if (_selection != Selection.None && !IsAreaTool(Mode) && Mode != ToolMode.Edge)
        {
            _cornerZ = Math.Clamp(_cornerZ + step, 1, _sim.World.Height - 1);
            _view.ViewLevel = _cornerZ;
            UpdateBox();
        }
        else _view.ViewLevel += step;
    }

    public bool Selecting => _selection != Selection.None;

    /// <summary>The cell edge nearest the mouse on the floor of the hovered tile (null over nothing).</summary>
    private (TilePos Cell, int Side)? EdgeUnderMouse(System.Numerics.Vector3 origin, System.Numerics.Vector3 dir)
    {
        if (Hovered is not { } h || VoxelRaycast.IntersectLevelPlane(origin, dir, h.Z + 1) is not { } pt) return null;
        int x = (int)MathF.Floor(pt.X), y = (int)MathF.Floor(pt.Y), z = h.Z + 1;
        float fx = pt.X - x, fy = pt.Y - y;
        float west = fx, east = 1 - fx, south = fy, north = 1 - fy, best = MathF.Min(MathF.Min(west, east), MathF.Min(south, north));
        if (best == east) return (new TilePos(x, y, z), EdgeInfo.East);
        if (best == west) return (new TilePos(x - 1, y, z), EdgeInfo.East);
        if (best == north) return (new TilePos(x, y, z), EdgeInfo.North);
        return (new TilePos(x, y - 1, z), EdgeInfo.North);
    }

    /// <summary>
    /// Shows the wall drag: a line as a thin slab along its edges; a box as the cells it walls round (one level), with
    /// the slab still marking the edge it started from.
    /// </summary>
    private void UpdateWallDrag()
    {
        var d = EdgeDrag();
        var (start, side) = _lineStart;
        var mat = _selection == Selection.Cancel ? _cancelMat : _ghostOk;
        if (d.Box)
        {
            int x0 = Math.Min(d.A.X, d.B.X), x1 = Math.Max(d.A.X, d.B.X) + 1, y0 = Math.Min(d.A.Y, d.B.Y), y1 = Math.Max(d.A.Y, d.B.Y) + 1;
            _box.Position = new Vector3((x0 + x1) / 2f, start.Z + 0.5f, (y0 + y1) / 2f);
            _box.Scale = new Vector3(x1 - x0 + 0.08f, 1.05f, y1 - y0 + 0.08f);
            _box.MaterialOverride = mat;
            _box.Visible = true;
            _ghost.Visible = false;
            return;
        }
        int lo = Math.Min(d.From, d.To), hi = Math.Max(d.From, d.To);
        float len = hi - lo + 1, mid = (lo + hi + 1) / 2f;
        _box.Position = side == EdgeInfo.East ? new Vector3(start.X + 1, start.Z + 0.5f, mid) : new Vector3(mid, start.Z + 0.5f, start.Y + 1);
        _box.Scale = side == EdgeInfo.East ? new Vector3(0.2f, 1.05f, len) : new Vector3(len, 1.05f, 0.2f);
        _box.MaterialOverride = mat;
        _box.Visible = true;
        _ghost.Visible = false;
    }

    /// <summary>The wall drag let go: one piece, a line, or walls round a box (Shift: removes instead).</summary>
    private void ApplyWall()
    {
        var d = EdgeDrag();
        var (cell, side) = _lineStart;
        bool remove = _selection == Selection.Cancel;
        Command command = d.Box
            ? remove ? new ClearWalls(d.A, d.B) : new PlaceOutline(d.A, d.B, WallMaterial)
            : d.From == d.To
                ? remove ? new RemoveEdge(cell, side) : new PlaceEdge(EdgePiece, cell, side, WallMaterial)
                : new EdgeLine(EdgePiece, cell, side, d.To, WallMaterial, remove);
        Issue(command);
        EndSelection();
    }

    /// <summary>Paints rooms of this workshop (or homes).</summary>
    public void SelectWorkshop(RoomType w)
    {
        Workshop = w;
        Mode = ToolMode.Room;
        EndSelection();
    }

    public void SelectPart(PartType part)
    {
        Part = part;
        Mode = ToolMode.Part;
        EndSelection();
    }

    /// <summary>True while the mouse is over the UI (so clicks and hovering there don't reach the world).</summary>
    /// <summary>The mouse, from the input events (so injected events, as in the UI self-test, move it too).</summary>
    private Vector2 Mouse => _mouseFromEvents ?? GetViewport().GetMousePosition();
    private Vector2? _mouseFromEvents;

    private bool OverMenu => UI.GlyphManager.Instance?.Blocks(Mouse) ?? false;

    // ---- Picking ----------------------------------------------------------------------------------------

    /// <summary>Top of the ground in a column (the highest solid tile), cached for the current selection.</summary>
    private int SurfaceAt(int x, int y)
    {
        if (_surface.TryGetValue((x, y), out int z)) return z;
        var world = _sim.World;
        // Start just above the generated surface (digging only lowers it); fall back to a full scan if solid there.
        int start = world.Height - 1;
        if (_sim.Terrain is { } t)
        {
            int hint = Math.Min(world.Height - 1, t.SurfaceAt(x, y) + 8);
            if (!world.IsSolid(x, y, hint)) start = hint;
        }
        z = -1;
        for (int k = start; k >= 0; k--)
            if (world.IsSolid(x, y, k)) { z = k; break; }
        _surface[(x, y)] = z;
        return z;
    }

    /// <summary>Designation kind a mode places on level z of a box spanning bottom..top (Stairs mode varies by level).</summary>
    private static DesignationKind KindFor(ToolMode mode, int z, int bottom, int top) => mode switch
    {
        ToolMode.Dig => DesignationKind.Dig,
        ToolMode.UpStair => DesignationKind.UpStair,
        ToolMode.DownStair => DesignationKind.DownStair,
        ToolMode.UpDownStair => DesignationKind.UpDownStair,
        ToolMode.Chop => DesignationKind.Chop,
        ToolMode.Gather => DesignationKind.Gather,
        _ => top == bottom ? DesignationKind.UpDownStair
            : z == top ? DesignationKind.DownStair
            : z == bottom ? DesignationKind.UpStair
            : DesignationKind.UpDownStair,
    };

    public override void _Process(double delta)
    {
        if (_sim == null) return;

        var camera = GetViewport().GetCamera3D();
        var mouse = Mouse;
        var origin = SimSpace.ToSim(camera.ProjectRayOrigin(mouse));
        var dir = SimSpace.ToSim(camera.ProjectRayNormal(mouse));

        var hit = OverMenu ? null : VoxelRaycast.Cast(_sim.World, origin, dir, 5000f, _view.ViewLevel);
        Hovered = hit?.Tile;
        _hoverNormal = hit?.Normal ?? new TilePos(0, 0, 1);
        bool placing = (PartFor(Mode) is not null || Mode == ToolMode.Structure) && _selection == Selection.None;
        _cursor.Visible = Hovered.HasValue && _selection == Selection.None && !placing;
        if (Hovered is { } h)
            _cursor.Position = SimSpace.TileCenter(IsAreaTool(Mode) && SurfaceAt(h.X, h.Y) is >= 0 and var s ? h with { Z = s } : h);
        PickBelt(origin, dir, hit);
        if (IsBeltTool(Mode)) _cursor.Visible = false; // the tool marks its own cells
        UpdateGhost(placing);
        _hoverEdge = Mode == ToolMode.Edge && _selection == Selection.None ? EdgeUnderMouse(origin, dir) : null;
        if (_hoverEdge is { } he)
        {
            var (cell, side) = he;
            PlacementProblem = _sim.Constructions.CanPlaceEdge(EdgePiece, cell, side);
            _ghost.Position = side == EdgeInfo.East ? new Vector3(cell.X + 1, cell.Z + 0.5f, cell.Y + 0.5f) : new Vector3(cell.X + 0.5f, cell.Z + 0.5f, cell.Y + 1);
            _ghost.Scale = side == EdgeInfo.East ? new Vector3(0.14f, 1, 1) : new Vector3(1, 1, 0.14f);
            _ghost.MaterialOverride = PlacementProblem is null ? _ghostOk : _ghostBad;
            _ghost.Visible = true;
            _cursor.Visible = false;
        }

        if (_selection != Selection.None)
        {
            // Area tools follow the picked column; box tools slide the free corner on the top plane of its
            // level, so it tracks the mouse over open air too.
            if (Mode == ToolMode.Edge)
            {
                // A wall drag tracks the mouse on the floor plane of the level it started on.
                if (VoxelRaycast.IntersectLevelPlane(origin, dir, _lineStart.Cell.Z) is { } ep) _edgeAt = new System.Numerics.Vector2(ep.X, ep.Y);
            }
            else if (IsAreaTool(Mode) && Hovered is { } col)
                (_cornerX, _cornerY) = (col.X, col.Y);
            else if (VoxelRaycast.IntersectLevelPlane(origin, dir, _cornerZ + 1) is { } pt)
            {
                _cornerX = Math.Clamp((int)MathF.Floor(pt.X), 0, _sim.World.Width - 1);
                _cornerY = Math.Clamp((int)MathF.Floor(pt.Y), 0, _sim.World.Depth - 1);
            }
            UpdateBox();
        }
        UpdateBeltTool();
    }

    // ---- Mouse and keys -------------------------------------------------------------------------------------
    // RMB: a drag pans (the camera rig does it); a click steps back (CancelPlacement). LMB with no tool: a drag moves
    // the view level (up the screen = up). Shift / Ctrl tapped on their own: a level up / down.

    /// <summary>Pixels of mouse travel that turn a click into a drag.</summary>
    private const float ClickSlop = 6f;
    /// <summary>Pixels of vertical drag per level, with no tool.</summary>
    private const float PixelsPerLevel = 14f;
    private Vector2 _rightDown;
    private bool _rightPressed;
    private bool _levelDrag;
    private float _levelDragY;
    private Vector2 _levelDragAt;
    private int _levelDragFrom;
    private Key _tap = Key.None;

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouse me) _mouseFromEvents = me.Position;
        // Shift / Ctrl on their own (pressed and released with nothing else in between): a level up / down. Watched
        // here, before the UI, so a click or key while held (Shift+click, Ctrl+Tab) cancels the tap.
        switch (e)
        {
            case InputEventKey { Keycode: Key.Shift or Key.Ctrl, Echo: true }:
                break;
            case InputEventKey { Keycode: Key.Shift or Key.Ctrl, Pressed: true } mk:
                _tap = mk.Keycode;
                break;
            case InputEventKey { Keycode: Key.Shift or Key.Ctrl, Pressed: false } mk:
                if (_tap == mk.Keycode && GetViewport().GuiGetFocusOwner() is not LineEdit && !(UI.GlyphManager.Instance?.MenuOpen ?? false))
                    StepLevel(mk.Keycode == Key.Shift ? 1 : -1);
                _tap = Key.None;
                break;
            case InputEventKey { Pressed: true }:
            case InputEventMouseButton { Pressed: true }:
                _tap = Key.None;
                break;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_sim == null) return;
        if (!_sim.WaterReady) // the world can't be edited while its water settles: say so rather than ignore the player
        {
            if (e is InputEventKey { Pressed: true, Echo: false } or InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && !OverMenu)
                UI.GlyphStatus.Say("The world is still settling its water: it takes orders in a moment", 3);
            return;
        }

        // No tool: dragging moves the view level; let go to stop.
        if (_levelDrag)
        {
            if (e is InputEventMouseMotion lm)
            {
                _view.ViewLevel = _levelDragFrom + (int)MathF.Round((_levelDragY - lm.Position.Y) / PixelsPerLevel);
                Handled();
                return;
            }
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } lr)
            {
                _levelDrag = false;
                // Hardly moved: a click, which selects what is under the cursor.
                if (lr.Position.DistanceTo(_levelDragAt) <= ClickSlop && _view.ViewLevel == _levelDragFrom) Clicked?.Invoke(Hovered);
                Handled();
                return;
            }
        }
        // A right click (not a drag: the rig pans with that) steps back. The press isn't taken, so the rig sees it.
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right } rb)
        {
            if (rb.Pressed)
            {
                _rightPressed = !OverMenu;
                _rightDown = rb.Position;
            }
            else if (_rightPressed)
            {
                _rightPressed = false;
                if (rb.Position.DistanceTo(_rightDown) <= ClickSlop && CancelPlacement()) Handled();
            }
            return;
        }
        if (e is InputEventMouseButton && OverMenu) return;

        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } lp when Mode == ToolMode.None && !SquadOrders:
                _levelDrag = true;
                _levelDragAt = lp.Position;
                _levelDragY = lp.Position.Y;
                _levelDragFrom = _view.ViewLevel;
                Handled();
                break;

            // Squad orders: click the ground to march the drafted there, a nest to send them against it; RMB: done.
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } when SquadOrders && _selection == Selection.None:
                if (Hovered is { } spot)
                {
                    var cell = spot.Offset(0, 0, 1);
                    if (_sim.Wildlife.NestNear(cell, 2) is { } nest) Issue(new SquadAttack(nest.Id));
                    else Issue(new SquadMove(cell));
                }
                Handled();
                break;
            // G: draft everyone and give squad orders (again: done giving orders); Shift+G: stand everyone down.
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.G } gk:
                if (gk.ShiftPressed)
                {
                    Issue(new SetDrafted(-1, false));
                    SquadOrders = false;
                }
                else if (SquadOrders) SquadOrders = false;
                else
                {
                    if (!_sim.Colonists.Any(c => c.Faction == Player && c.Drafted)) Issue(new SetDrafted(-1, true));
                    SquadOrders = true;
                }
                Handled();
                break;

            // Letting go: a wall drag always ends (one piece if it never left its edge); a box ends if it was dragged
            // (a press and release in place leaves it open for a second click: click one corner, then the other).
            case InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left } when Mode == ToolMode.Edge && _selection != Selection.None:
                _pressAt = null;
                ApplyWall();
                Handled();
                break;
            case InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left } up when _pressAt is { } pressed:
                _pressAt = null;
                if (_selection != Selection.None && up.Position.DistanceTo(pressed) > ClickSlop)
                {
                    Apply();
                    Handled();
                }
                break;

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } when _selection != Selection.None:
                Apply();
                Handled();
                break;

            // The wall tool: press on an edge (Shift: removes), drag along it for a line or away from it for a box, let go.
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } eb when Mode == ToolMode.Edge:
                if (_hoverEdge is { } edge)
                {
                    _selection = eb.ShiftPressed ? Selection.Cancel : Selection.Designate;
                    _lineStart = edge;
                    _edgeAt = edge.Side == EdgeInfo.East
                        ? new System.Numerics.Vector2(edge.Cell.X + 1, edge.Cell.Y + 0.5f)
                        : new System.Numerics.Vector2(edge.Cell.X + 0.5f, edge.Cell.Y + 1);
                    UpdateBox();
                }
                Handled();
                break;

            // Dev mode: a click drops what the spawn tool holds on the floor there.
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } when Mode == ToolMode.Spawn:
                if (Hovered is { } drop) Issue(new Colony.Core.Commands.DevSpawn(drop.Offset(0, 0, 1), SpawnItem, SpawnCount));
                Handled();
                break;

            // Conveyors: click the start, then the end (Shift+click a belt takes it down).
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } bc when IsBeltTool(Mode):
                BeltClick(bc.ShiftPressed);
                Handled();
                break;

            // Waterworks go down with a single click too (Shift: a removal box).
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left, ShiftPressed: false } when Mode == ToolMode.Structure:
                if (StructureCell() is { } st) PlaceStructure(st);
                Handled();
                break;

            // Parts go down with a single click (no box; Shift: a removal box).
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left, ShiftPressed: false } when PartFor(Mode) is { } part:
                if (Hovered is { } at) PlacePart(part, at.Offset(0, 0, 1));
                Handled();
                break;

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb
                when Hovered is { } start && Mode != ToolMode.None:
                _selection = mb.ShiftPressed && Mode is not (ToolMode.Cancel or ToolMode.Demolish) ? Selection.Cancel : Selection.Designate;
                _pressAt = mb.Position;
                _anchor = start;
                (_cornerX, _cornerY, _cornerZ) = (start.X, start.Y, start.Z);
                _surface.Clear(); // the ground may have changed since the last selection
                _drapeRect = (-1, -1, -1, -1);
                UpdateBox();
                Handled();
                break;

            // While selecting, Z/X move the free corner between levels and the view follows it (DF-style cursor).
            case InputEventKey { Pressed: true, Keycode: Key.Z or Key.X or Key.Pagedown or Key.Pageup } k
                when _selection != Selection.None && !IsAreaTool(Mode) && Mode != ToolMode.Edge:
                StepLevel(k.Keycode is Key.X or Key.Pageup ? 1 : -1);
                Handled();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Tab, CtrlPressed: false } tab:
                Mode = Cycle(Mode, ToolMode.Dig, LastOrder, tab.ShiftPressed);
                EndSelection();
                Handled();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.R } when PartFor(Mode) is not null || Mode is ToolMode.Structure or ToolMode.Rack:
                Rotate();
                Handled();
                break;

            case InputEventKey { Pressed: true, Keycode: Key.Bracketleft or Key.Bracketright } sk
                when PartFor(Mode) is { } sized && PartInfo.Sizes(sized) > 1 || Mode == ToolMode.House:
                ChangeSize(sk.Keycode == Key.Bracketright ? 1 : -1);
                Handled();
                break;

            // F: change what the machine under the cursor makes.
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.F } fk when HoveredMachine() is { } machine:
                Issue(new Colony.Core.Commands.CycleRecipe(machine.Cells[0], fk.ShiftPressed));
                Handled();
                break;

            // F over a home: switch it to the next housing policy (H edits the policies themselves).
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.F } hk when HoveredHome() is { } home:
            {
                var policies = _sim.Factions[Player].Housing.Policies;
                int next = (policies.IndexOf(home.Policy) + (hk.ShiftPressed ? policies.Count - 1 : 1)) % policies.Count;
                Issue(new SetHomePolicy(home.Room.Cells.First(), next));
                Handled();
                break;
            }

            // The orders' hotkeys (DF-style; the ribbon shows them).
            case InputEventKey { Pressed: true, Echo: false, CtrlPressed: false, AltPressed: false } ok when UI.OrdersRibbon.OrderFor(ok.Keycode, ok.ShiftPressed) is { } order:
                PickOrder(order);
                Handled();
                break;

            // Developer builds only (released games have no cheats): finish every order at once.
            case InputEventKey { Pressed: true, Echo: false, CtrlPressed: true, Keycode: Key.Enter or Key.KpEnter } when GameInfo.DevBuild:
                Issue(new DebugCompleteAll());
                break;
        }
    }

    /// <summary>The home whose floor is the hovered tile's top, if any.</summary>
    public Home? HoveredHome() =>
        Hovered is { } h && _sim.Rooms.RoomAt(h.Offset(0, 0, 1)) is { Type.IsHome: true } room && room.Faction == Player ? _sim.Factions[Player].Housing.HomeOf(room) : null;

    /// <summary>The machine standing on (or in) the hovered tile, if any.</summary>
    public Part? HoveredMachine()
    {
        if (Hovered is not { } h) return null;
        var part = _sim.Rooms.PartAt(h.Offset(0, 0, 1)) ?? _sim.Rooms.PartAt(h);
        return part is { Role: PartRole.Machine } ? part : null;
    }

    /// <summary>Next mode within [first, last]; entering the group from outside starts at its first mode.</summary>
    private static ToolMode Cycle(ToolMode current, ToolMode first, ToolMode last, bool back)
    {
        if (current < first || current > last) return first;
        int n = last - first + 1, i = current - first;
        return first + (i + (back ? n - 1 : 1)) % n;
    }

    private void Handled() => GetViewport().SetInputAsHandled();

    private (TilePos A, TilePos B) Corners() => (_anchor, new TilePos(_cornerX, _cornerY, _cornerZ));

    private TilePos _hoverNormal = new(0, 0, 1);

    /// <summary>
    /// Where a wheel or pump goes: on top of the hovered tile, or, pointing at the side of a wall or cliff, in the cell
    /// against that face (so a wheel hangs on the wall, in the fall).
    /// </summary>
    private TilePos? StructureCell() => Hovered is not { } h ? null
        : _hoverNormal.Z == 0 && (_hoverNormal.X != 0 || _hoverNormal.Y != 0) ? h.Offset(_hoverNormal.X, _hoverNormal.Y, 0) : h.Offset(0, 0, 1);

    // A wheel hung on a wall turns its axle along the wall (its paddles stand out from it, in the fall).
    private void PlaceStructure(TilePos cell) => Issue(new Colony.Core.Commands.PlaceStructure(Structure, cell,
        Structure == StructureKind.WaterWheel && _hoverNormal.Z == 0 && (_hoverNormal.X != 0 || _hoverNormal.Y != 0) ? (_hoverNormal.X != 0 ? 0 : 1) : _facing));

    public void SelectStructure(StructureKind kind)
    {
        Structure = kind;
        Mode = ToolMode.Structure;
        EndSelection();
    }

    private void PlacePart(PartType part, TilePos cell) => Issue(new Colony.Core.Commands.PlacePart(part.Id, cell, _rotated, SizeFor(part)));

    private void Apply()
    {
        var (a, b) = Corners();
        bool cancel = _selection == Selection.Cancel;
        Command command = Mode switch
        {
            ToolMode.Floor => new Covers(StructureKind.Floor, a, b, FloorMaterial, cancel),
            ToolMode.Roof => new Covers(StructureKind.Roof, a, b, RoofMaterial, cancel),
            ToolMode.House => cancel
                ? new CancelOrders(a, new TilePos(b.X, b.Y, Math.Max(a.Z, b.Z) + Storeys - 1))
                : new PlaceHouse(a, b with { Z = a.Z }, Storeys, WallMaterial, FloorMaterial, RoofMaterial),
            ToolMode.Room => cancel ? new EraseRoom(a, b) : new PaintRoom(Workshop.Id, a, b),
            ToolMode.Block => cancel ? new CancelWalls(a, b) : new PlaceWalls(a, b),
            ToolMode.Structure => new RemoveStructures(a, b),
            ToolMode.Part => new RemoveParts(a, b),
            ToolMode.Rack => cancel ? new RemoveStructures(a, b) : new PlaceRacks(a, b, _facing),
            ToolMode.TradeDepot => cancel ? new CancelOrders(a, b) : new AddTradeDepot(a, b),
            ToolMode.Cancel => new CancelOrders(a, b),
            ToolMode.Demolish => new Colony.Core.Commands.Demolish(a, b),
            ToolMode.Chop or ToolMode.Gather => cancel
                ? new CancelPlants(KindFor(Mode, 0, 0, 0), a.X, a.Y, b.X, b.Y)
                : new DesignatePlants(KindFor(Mode, 0, 0, 0), a.X, a.Y, b.X, b.Y),
            _ when cancel => new CancelOrders(a, b),
            ToolMode.Stairs => new DesignateStairs(a, b),
            ToolMode.Stockpile => new AddStockpile(a, b),
            _ when CropFor(Mode) is { } crop => new AddFarm(crop, a, b),
            _ => new DesignateBox(a, b, KindFor(Mode, 0, 0, 0)),
        };
        Issue(command);
        // Dig orders carry the tool's priority (sent every time, so a box at 3 also resets orders it covers).
        if (!cancel && IsDigTool(Mode)) Issue(new SetOrderPriority(a, b, DigPriority));
        EndSelection();
    }

    /// <summary>
    /// Every change the player makes goes through here as a command (applied before the next tick and logged, so the
    /// game replays exactly; multiplayer will send it to the other players instead of applying it at once).
    /// </summary>
    public void Issue(Command command)
    {
        if (Remote is { } send)
        {
            send(command with { Player = Player }); // online: the host applies it, and its result comes back (ShowResult)
            return;
        }
        ShowResult(_sim.Commands.Execute(command with { Player = Player }));
    }

    /// <summary>Online as a guest: where commands go instead of the local sim.</summary>
    public Action<Command>? Remote { get; set; }

    /// <summary>What a command did (shown in the status pill).</summary>
    public void ShowResult(string message)
    {
        if (message.Length == 0) return; // a command that rides along with another (an order box's priority) says nothing
        LastAction = message;
        ActionSerial++;
    }

    /// <summary>Counts issued commands (so the UI shows a message even when it repeats).</summary>
    public int ActionSerial { get; private set; }

    /// <summary>The colony this player controls: every command is issued as it.</summary>
    public int Player { get; set; }

    private void EndSelection()
    {
        _pressAt = null;
        _selection = Selection.None;
        _box.Visible = false;
        _drape.Visible = false;
        if (_conflicts != null) _conflicts.Visible = false;
        _housePlan = null;
    }

    private void UpdateGhost(bool placing)
    {
        PlacementProblem = null;
        if (placing && Mode == ToolMode.Structure && Hovered is { } sh)
        {
            var cell = StructureCell() ?? sh.Offset(0, 0, 1);
            PlacementProblem = _sim.Constructions.CanPlace(Structure, cell);
            _ghost.Position = new Vector3(cell.X + 0.5f, cell.Z + 0.5f, cell.Y + 0.5f);
            _ghost.Scale = new Vector3(1, 1, 1);
            _ghost.MaterialOverride = PlacementProblem is null ? _ghostOk : _ghostBad;
            _ghost.Visible = true;
            return;
        }
        if (!placing || Hovered is not { } h || PartFor(Mode) is not { } part)
        {
            _ghost.Visible = false;
            return;
        }
        var origin = h.Offset(0, 0, 1);
        int partSize = SizeFor(part);
        PlacementProblem = _sim.Rooms.CanPlace(part, origin, _rotated, partSize);
        var cells = Rooms.Footprint(part, origin, _rotated, partSize);
        var last = cells[^1];
        var size = new Vector3(last.X - origin.X + 1, 0.7f, last.Y - origin.Y + 1);
        _ghost.Position = new Vector3(origin.X + size.X / 2f, origin.Z + size.Y / 2f, origin.Y + size.Z / 2f);
        _ghost.Scale = size;
        _ghost.MaterialOverride = PlacementProblem is null ? _ghostOk : _ghostBad;
        _ghost.Visible = true;
    }

    private void UpdateBox()
    {
        if (Mode == ToolMode.Edge)
        {
            UpdateWallDrag();
            return;
        }
        if (IsAreaTool(Mode))
        {
            UpdateDrape();
            return;
        }
        if (Mode == ToolMode.House)
        {
            UpdateHouse();
            return;
        }
        var (a, b) = Corners();
        int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X) + 1;
        int y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y) + 1;
        int z0 = Math.Min(a.Z, b.Z), z1 = Math.Max(a.Z, b.Z) + 1;

        const float pad = 0.08f;
        var size = SimSpace.ToGodot(x1 - x0, y1 - y0, z1 - z0);
        _box.Position = SimSpace.ToGodot(x0, y0, z0) + size / 2f;
        _box.Scale = size + Vector3.One * pad;
        _box.MaterialOverride = _selection == Selection.Cancel ? _cancelMat : _modeMats[Mode];
        _box.Visible = true;
    }

    // ---- The House tool's preview: the storeys as a box, green if it fits, red with every cell in the way marked ---------

    private MultiMeshInstance3D? _conflicts;
    private (TilePos A, TilePos B, int Storeys, long Revision, int Sites) _surveyed;
    private Houses.Plan? _housePlan;

    /// <summary>The house being dragged out: its plan (conflicts included), null when not dragging one.</summary>
    public Houses.Plan? HousePlan => _selection != Selection.None && Mode == ToolMode.House ? _housePlan : null;

    private void UpdateHouse()
    {
        var (a, b) = Corners();
        b = b with { Z = a.Z }; // the ground floor is the level above where the drag began
        var key = (a, b, Storeys, _sim.World.Revision, _sim.Constructions.Version);
        if (key != _surveyed || _housePlan is null)
        {
            _surveyed = key;
            _housePlan = Houses.Survey(_sim, a, b, Storeys);
            ShowConflicts(_housePlan);
        }
        var plan = _housePlan;
        bool remove = _selection == Selection.Cancel;
        PlacementProblem = remove ? null : plan.Problem;
        var size = new Vector3(plan.Width, plan.Storeys, plan.Depth);
        _box.Position = new Vector3(plan.X0, plan.Z0, plan.Y0) + size / 2f;
        _box.Scale = size + Vector3.One * 0.08f;
        _box.MaterialOverride = remove ? _cancelMat : plan.Conflicts.Count == 0 ? _ghostOk : _ghostBad;
        _box.Visible = true;
        if (_conflicts != null) _conflicts.Visible = !remove;
    }

    private void ShowConflicts(Houses.Plan plan)
    {
        if (_conflicts == null)
        {
            _conflicts = new MultiMeshInstance3D
            {
                Multimesh = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    Mesh = new BoxMesh { Size = Vector3.One * 1.02f, Material = Overlay.Material(new Color(1f, 0.2f, 0.15f, 0.55f)) },
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_conflicts);
        }
        var mm = _conflicts.Multimesh;
        int n = Math.Min(plan.Conflicts.Count, 4000);
        mm.InstanceCount = n;
        for (int i = 0; i < n; i++)
        {
            var c = plan.Conflicts[i].Cell;
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(c.X + 0.5f, c.Z + 0.5f, c.Y + 0.5f)));
        }
        _conflicts.Visible = true;
    }

    /// <summary>The selected area as one plate per column on top of the ground (only rebuilt when the area changes).</summary>
    private void UpdateDrape()
    {
        var (a, b) = Corners();
        var rect = (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        _drape.MaterialOverride = _selection == Selection.Cancel ? _drapeCancelMat : _drapeMats[Mode];
        _drape.Visible = true;
        if (rect == _drapeRect) return;
        _drapeRect = rect;

        var (x0, y0, x1, y1) = rect;
        bool outlineOnly = (long)(x1 - x0 + 1) * (y1 - y0 + 1) > MaxDrapeColumns;
        var columns = new List<(int X, int Y, int Z)>();
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            if (outlineOnly && x != x0 && x != x1 && y != y0 && y != y1) continue;
            int z = SurfaceAt(x, y);
            if (z >= 0) columns.Add((x, y, z));
        }
        var mm = _drape.Multimesh;
        mm.InstanceCount = columns.Count;
        for (int i = 0; i < columns.Count; i++)
        {
            var (x, y, z) = columns[i];
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(x + 0.5f, z + 1.04f, y + 0.5f)));
        }
    }
}
