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
/// camera rig), tapping Shift / Ctrl goes a level up / down. Box tools: click once to anchor a corner, move the mouse
/// (Z/X or the Shift/Ctrl taps extend the box across levels; the view follows), click again to apply; Shift+click
/// starts a removal box instead. A right click (not a drag) steps back: aborts the box, then drops the tool. Esc too.
/// </para>
/// <list type="bullet">
/// <item>Orders (their DF-style hotkeys, see <see cref="UI.OrdersRibbon"/>; Tab / Shift+Tab cycle): dig, stairs
/// (auto / up / down / up-down), chop trees and gather plants (2D areas draped on the surface), stockpile zones, farm
/// plots (grain, herbs, glowcaps), dam walls, and the cancel box (designations and zone cells).</item>
/// <item>Build (B opens the build menu): pick a workshop to paint its room (a box over the floor, any number
/// of levels; it must stay one walkable piece, which the core enforces), then pick one of its stations, tools,
/// lanterns or crates and click to place. R rotates, [ ] change size, F (Shift+F) changes what the machine
/// under the cursor makes. A ghost shows the footprint in green or red with the reason. Removal boxes erase
/// room cells (room tool) or remove parts (part tools).</item>
/// </list>
/// </summary>
public partial class DesignationTool : Node3D
{
    public enum ToolMode
    {
        // Orders
        None, Dig, Stairs, UpStair, DownStair, UpDownStair, Chop, Gather, Stockpile, FarmGrain, FarmHerbs, FarmGlowcaps, FarmFlax, Wall, Demolish, Cancel,
        // Build (which workshop / part / structure: see Workshop, Part and Structure)
        Room, Part, Structure, Outline, Edge, WallLine, Floor, Roof, Rack, TradeDepot,
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
    /// <summary>The edge piece placed by <see cref="ToolMode.Edge"/> (thin wall, door or window) and the wall material for both edge tools.</summary>
    public StructureKind EdgePiece { get; private set; } = StructureKind.EdgeWall;
    public int WallMaterial { get; private set; } = 2;
    private (TilePos Cell, int Side)? _hoverEdge;
    private (TilePos Cell, int Side) _lineStart, _lineEnd; // a wall line being dragged (WallLine)
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

    public string ModeName(ToolMode m)
    {
        if (PartFor(m) is { } part)
            return PartInfo.Sizes(part) > 1
                ? $"Build: {PartInfo.Name(part, SizeFor(part))} (size {SizeFor(part)}/{PartInfo.Sizes(part)}, [ ] to change)"
                : $"Build: {PartInfo.Name(part)}";
        if (m == ToolMode.Structure) return $"Build: {Constructions.Name(Structure)} (facing {FacingName(_facing)}, R turns)";
        string mat = WallMaterials.All[WallMaterial].Name.ToLowerInvariant();
        if (m == ToolMode.Outline) return $"Build: {mat} walls round a box (drag over the floor; Shift-drag clears walls)";
        if (m == ToolMode.Edge) return $"Build: {Constructions.Name(EdgePiece).ToLowerInvariant()} ({mat}) on the cell edge under the cursor; Shift+click removes";
        if (m == ToolMode.WallLine) return $"Build: a line of {Constructions.Name(EdgePiece).ToLowerInvariant()}s ({mat}): click an edge, drag along it, click; Shift starts a removal line";
        if (m == ToolMode.Floor) return $"Build: {FloorMaterials.All[FloorMaterial].Name.ToLowerInvariant()} over a box of floor (Shift-drag takes floors up)";
        if (m == ToolMode.Roof) return $"Build: {RoofMaterials.All[RoofMaterial].Name.ToLowerInvariant()} roof over a box of floor (Shift-drag takes roofs down)";
        if (m == ToolMode.Rack) return $"Build: storage racks over stockpile cells (fronts face {FacingName(_facing)}, R turns; Shift-drag takes them down)";
        if (m == ToolMode.TradeDepot) return "Zone: trade depot (other colonies' traders deliver here; Shift-drag removes)";
        return m == ToolMode.Room ? $"Build: {Workshop.Name} room" : BaseModeName(m);
    }

    private static string FacingName(int f) => f switch { 0 => "east", 1 => "south", 2 => "west", _ => "north" };

    private static string BaseModeName(ToolMode m) => m switch
    {
        ToolMode.None => "No tool (click: inspect · drag: levels)",
        ToolMode.Cancel => "Cancel orders (designations, plans and zone cells in a box)",
        ToolMode.Demolish => "Demolish (take down built walls, doors, windows, floors, roofs, racks, waterworks and room parts in a box)",
        ToolMode.Dig => "Dig",
        ToolMode.Stairs => "Stairs (auto)",
        ToolMode.UpStair => "Up stair",
        ToolMode.DownStair => "Down stair",
        ToolMode.UpDownStair => "Up/Down stair",
        ToolMode.Chop => "Chop trees",
        ToolMode.Gather => "Gather plants",
        ToolMode.Stockpile => "Stockpile",
        ToolMode.FarmGrain => "Farm plot: grain",
        ToolMode.FarmHerbs => "Farm plot: herbs",
        ToolMode.FarmGlowcaps => "Fungus farm: glowcaps (under a roof)",
        ToolMode.FarmFlax => "Farm plot: flax (fiber)",
        ToolMode.Wall => "Build wall (dams, canals; Z/X raise it)",
        _ => m.ToString(),
    };

    /// <summary>One HUD line about the current tool (selection in progress, or placement preview).</summary>
    public string? SelectionInfo
    {
        get
        {
            if (_selection != Selection.None)
            {
                var (a, b) = Corners();
                int w = Math.Abs(a.X - b.X) + 1, d = Math.Abs(a.Y - b.Y) + 1, h = Math.Abs(a.Z - b.Z) + 1;
                string what = _selection == Selection.Cancel ? RemovalName() : ModeName(Mode);
                if (IsAreaTool(Mode))
                    return $"{what}: {w}x{d} area on the surface   click to confirm, Esc/RMB: abort";
                string levels = h > 1 ? $"levels {Math.Min(a.Z, b.Z)}..{Math.Max(a.Z, b.Z)}" : $"level {a.Z}";
                return $"{what}: {w}x{d}x{h} ({levels})   click to confirm, Shift/Ctrl or Z/X: change level, Esc/RMB: abort";
            }
            if (Mode == ToolMode.Rack)
            {
                int planks = Constructions.Cost(StructureKind.Rack, Colony.Core.Items.Stockpiles.MaxRackLevels).Count;
                return $"Storage rack: rises up to {Colony.Core.Items.Stockpiles.MaxRackLevels} levels, holding {Colony.Core.Items.Stockpiles.Capacity(Colony.Core.Items.Stockpiles.MaxRackLevels)} stacks" +
                       $"   cost {Constructions.RackPlanksPerLevel} planks a level ({planks} full height)   R: turn   drag a box over stockpile cells";
            }
            if (Mode == ToolMode.Structure)
            {
                var (mat, n) = Constructions.Cost(Structure);
                string what = Structure == StructureKind.WaterWheel
                    ? "makes power from water flowing through its cell (best under a fall or in a fast current)"
                    : $"lifts water from its cell to the cell {FacingName(_facing)} and one level up, using power";
                return $"{Constructions.Name(Structure)}: {what}   cost {n} {mat.Name.ToLowerInvariant()}   R: turn   " +
                       (PlacementProblem is { } sp ? $"can't place: {sp}" : "click to place");
            }
            if (PartFor(Mode) is { } part)
            {
                int size = SizeFor(part);
                var (fw, fd) = PartInfo.Footprint(part, size);
                if (_rotated) (fw, fd) = (fd, fw);
                int tiles = fw * fd;
                var (material, cost) = PartInfo.Cost(part, size);
                string role = PartInfo.Role(part) switch
                {
                    PartRole.Machine => MachineInfo(part, size),
                    PartRole.Auxiliary =>
                        $"+{PartInfo.Bonus(part, size):P0} for up to {PartInfo.Coverage(part, size)} machine tiles " +
                        $"({PartInfo.Coverage(part, size) / (float)tiles:0.#} per own tile)",
                    PartRole.Input => "holds one of the room's inputs",
                    _ => "holds the room's products",
                };
                return $"{PartInfo.Name(part, size)} {fw}x{fd}: {role}   cost {cost} {material.Name.ToLowerInvariant()} ({cost / (float)tiles:0.#}/tile)   " +
                       "R: rotate   " + (PlacementProblem is { } p ? $"can't place: {p}" : "click to place");
            }
            return null;
        }
    }

    /// <summary>"batch of 4: 4 logs -> 16 planks, 0.6 s labour per unit; also makes: ..." for a station.</summary>
    private static string MachineInfo(PartType part, int size)
    {
        var first = part.Recipes[0];
        var batch = PartInfo.MachineBatchOf(part, size, first);
        double labour = batch.WorkTicks / (double)batch.Units / ColonySim.TicksPerSecond;
        string others = part.Recipes.Count > 1 ? $"; also: {string.Join(", ", part.Recipes.Skip(1).Select(r => r.Name.ToLowerInvariant()))}" : "";
        return $"batch of {batch.Units}: {first.Describe(batch.Units)}, {labour:0.0} s labour per unit{others}";
    }

    private string RemovalName() => Mode switch
    {
        ToolMode.Room => "Erase room",
        ToolMode.Part => "Remove parts",
        ToolMode.Structure => "Remove waterworks",
        ToolMode.Rack => "Take racks down",
        ToolMode.Wall => "Cancel walls",
        _ => "Cancel",
    };

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
                : mode == ToolMode.Wall ? new Color(0.75f, 0.72f, 0.68f, 0.35f)
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

    public void SelectEdgeTool(ToolMode mode, StructureKind piece)
    {
        Mode = mode;
        EdgePiece = piece;
        EndSelection();
    }

    public void SelectWallMaterial(int material) => WallMaterial = WallMaterials.Clamp(material);

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
            if (_selection == Selection.None || Mode == ToolMode.WallLine) return null;
            var (a, b) = Corners();
            return (Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1, IsAreaTool(Mode) ? 1 : Math.Abs(a.Z - b.Z) + 1);
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
        if (_selection != Selection.None && !IsAreaTool(Mode))
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

    private void PlaceEdgePiece((TilePos Cell, int Side) edge) => Issue(new PlaceEdge(EdgePiece, edge.Cell, edge.Side, WallMaterial));

    /// <summary>Shows the wall line being dragged as a thin box along its edges.</summary>
    private void UpdateLine()
    {
        var (start, side) = _lineStart;
        int lo = side == EdgeInfo.East ? Math.Min(start.Y, _cornerY) : Math.Min(start.X, _cornerX);
        int hi = side == EdgeInfo.East ? Math.Max(start.Y, _cornerY) : Math.Max(start.X, _cornerX);
        float len = hi - lo + 1, mid = (lo + hi + 1) / 2f;
        _box.Position = side == EdgeInfo.East ? new Vector3(start.X + 1, start.Z + 0.5f, mid) : new Vector3(mid, start.Z + 0.5f, start.Y + 1);
        _box.Scale = side == EdgeInfo.East ? new Vector3(0.2f, 1.05f, len) : new Vector3(len, 1.05f, 0.2f);
        _box.MaterialOverride = _selection == Selection.Cancel ? _cancelMat : _ghostOk;
        _box.Visible = true;
    }

    private void RemoveEdgePiece((TilePos Cell, int Side) edge) => Issue(new RemoveEdge(edge.Cell, edge.Side));

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
    private bool OverMenu => UI.GlyphManager.Instance?.Blocks(GetViewport().GetMousePosition()) ?? false;

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
        var mouse = GetViewport().GetMousePosition();
        var origin = SimSpace.ToSim(camera.ProjectRayOrigin(mouse));
        var dir = SimSpace.ToSim(camera.ProjectRayNormal(mouse));

        var hit = OverMenu ? null : VoxelRaycast.Cast(_sim.World, origin, dir, 5000f, _view.ViewLevel);
        Hovered = hit?.Tile;
        _hoverNormal = hit?.Normal ?? new TilePos(0, 0, 1);
        bool placing = (PartFor(Mode) is not null || Mode == ToolMode.Structure) && _selection == Selection.None;
        _cursor.Visible = Hovered.HasValue && _selection == Selection.None && !placing;
        if (Hovered is { } h)
            _cursor.Position = SimSpace.TileCenter(IsAreaTool(Mode) && SurfaceAt(h.X, h.Y) is >= 0 and var s ? h with { Z = s } : h);
        UpdateGhost(placing);
        _hoverEdge = Mode is ToolMode.Edge or ToolMode.WallLine && _selection == Selection.None ? EdgeUnderMouse(origin, dir) : null;
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
            if (IsAreaTool(Mode) && Hovered is { } col)
                (_cornerX, _cornerY) = (col.X, col.Y);
            else if (VoxelRaycast.IntersectLevelPlane(origin, dir, _cornerZ + 1) is { } pt)
            {
                _cornerX = Math.Clamp((int)MathF.Floor(pt.X), 0, _sim.World.Width - 1);
                _cornerY = Math.Clamp((int)MathF.Floor(pt.Y), 0, _sim.World.Depth - 1);
            }
            UpdateBox();
        }
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
        if (_sim == null || !_sim.WaterReady) return; // the world can't be edited while its water settles

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

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } when _selection != Selection.None:
                Apply();
                Handled();
                break;

            // A wall line: click an edge to start it (Shift: a removal line); then the free end follows the mouse along it (see UpdateLine).
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } lb when Mode == ToolMode.WallLine && _selection == Selection.None:
                if (_hoverEdge is { } first)
                {
                    _selection = lb.ShiftPressed ? Selection.Cancel : Selection.Designate;
                    _lineStart = _lineEnd = first;
                    _anchor = first.Cell.Offset(0, 0, -1);
                    (_cornerX, _cornerY, _cornerZ) = (_anchor.X, _anchor.Y, _anchor.Z);
                    UpdateBox();
                }
                Handled();
                break;

            // Edge pieces: click the edge under the cursor; Shift+click removes what is on it.
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } eb when Mode == ToolMode.Edge && _selection == Selection.None:
                if (_hoverEdge is { } edge)
                {
                    if (!eb.ShiftPressed) PlaceEdgePiece(edge);
                    else RemoveEdgePiece(edge);
                }
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
                _anchor = start;
                (_cornerX, _cornerY, _cornerZ) = (start.X, start.Y, start.Z);
                _surface.Clear(); // the ground may have changed since the last selection
                _drapeRect = (-1, -1, -1, -1);
                UpdateBox();
                Handled();
                break;

            // While selecting, Z/X move the free corner between levels and the view follows it (DF-style cursor).
            case InputEventKey { Pressed: true, Keycode: Key.Z or Key.X or Key.Pagedown or Key.Pageup } k
                when _selection != Selection.None && !IsAreaTool(Mode):
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
                when PartFor(Mode) is { } sized && PartInfo.Sizes(sized) > 1:
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

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter }:
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
            ToolMode.WallLine => new EdgeLine(EdgePiece, _lineStart.Cell, _lineStart.Side,
                _lineStart.Side == EdgeInfo.East ? _cornerY : _cornerX, WallMaterial, cancel),
            ToolMode.Floor => new Covers(StructureKind.Floor, a, b, FloorMaterial, cancel),
            ToolMode.Roof => new Covers(StructureKind.Roof, a, b, RoofMaterial, cancel),
            ToolMode.Outline => cancel ? new ClearWalls(a, b) : new PlaceOutline(a, b, WallMaterial),
            ToolMode.Room => cancel ? new EraseRoom(a, b) : new PaintRoom(Workshop.Id, a, b),
            ToolMode.Wall => cancel ? new CancelWalls(a, b) : new PlaceWalls(a, b),
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
        LastAction = message;
        ActionSerial++;
    }

    /// <summary>Counts issued commands (so the UI shows a message even when it repeats).</summary>
    public int ActionSerial { get; private set; }

    /// <summary>The colony this player controls: every command is issued as it.</summary>
    public int Player { get; set; }

    private void EndSelection()
    {
        _selection = Selection.None;
        _box.Visible = false;
        _drape.Visible = false;
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
        if (Mode == ToolMode.WallLine)
        {
            UpdateLine();
            return;
        }
        if (IsAreaTool(Mode))
        {
            UpdateDrape();
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
