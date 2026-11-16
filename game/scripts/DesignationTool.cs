using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core;
using Colony.Core.Industry;
using Colony.Core.Nature;
using Colony.Core.Orders;
using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>
/// Mouse picking, DF-style box orders, zones, and the factory builder.
/// <para>
/// Box tools: click once to anchor a corner, move the mouse (Z/X extend the box across levels; the view
/// follows), click again to apply. Right-click starts a removal box (or aborts a pending one); Esc aborts.
/// </para>
/// <list type="bullet">
/// <item>Orders (Tab / Shift+Tab): dig, stairs (auto / up / down / up-down), chop trees and gather plants
/// (2D areas draped on the surface), stockpile zones, and farm plots (grain, herbs, glowcaps). Removal boxes
/// cancel designations and remove zone cells.</item>
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
        Dig, Stairs, UpStair, DownStair, UpDownStair, Chop, Gather, Stockpile, FarmGrain, FarmHerbs, FarmGlowcaps,
        // Build (which workshop / part: see Workshop and Part)
        Room, Part,
    }

    private const ToolMode LastOrder = ToolMode.FarmGlowcaps;
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
        _ => null,
    };

    public ToolMode Mode { get; private set; } = ToolMode.Dig;
    /// <summary>The workshop the build menu is on (its room is painted by <see cref="ToolMode.Room"/>).</summary>
    public RoomType Workshop { get; private set; } = RoomType.Carpentry;
    /// <summary>The part placed by <see cref="ToolMode.Part"/>.</summary>
    public PartType Part { get; private set; } = PartType.Sawbench;
    public TilePos? Hovered { get; private set; }
    public string? LastAction { get; private set; }
    /// <summary>Why the part under the cursor can't be placed (null if it can, or not placing).</summary>
    public string? PlacementProblem { get; private set; }

    public static bool IsBuild(ToolMode m) => m > LastOrder;
    public PartType? PartFor(ToolMode m) => m == ToolMode.Part ? Part : null;

    public string ModeName(ToolMode m)
    {
        if (PartFor(m) is { } part)
            return PartInfo.Sizes(part) > 1
                ? $"Build: {PartInfo.Name(part, SizeFor(part))} (size {SizeFor(part)}/{PartInfo.Sizes(part)}, [ ] to change)"
                : $"Build: {PartInfo.Name(part)}";
        return m == ToolMode.Room ? $"Build: {Workshop.Name} room" : BaseModeName(m);
    }

    private static string BaseModeName(ToolMode m) => m switch
    {
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
                return $"{what}: {w}x{d}x{h} ({levels})   click to confirm, Z/X: change level, Esc/RMB: abort";
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

    private string RemovalName() => Mode == ToolMode.Room ? "Erase room" : Mode == ToolMode.Part ? "Remove parts" : "Cancel";

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
                : CropFor(mode) is not null ? ItemView.FarmColor with { A = 0.3f }
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
        BuildMenu();
    }

    // ---- Build menu -------------------------------------------------------------------------------------

    private PanelContainer _menu = null!;
    private VBoxContainer _partList = null!;
    private Label _partsTitle = null!;
    private readonly Dictionary<RoomType, Button> _workshopButtons = new();

    /// <summary>The build menu: every workshop (paints its room), then the selected workshop's parts.</summary>
    private void BuildMenu()
    {
        var layer = new CanvasLayer { Layer = 2 };
        AddChild(layer);
        _menu = new PanelContainer { Visible = false };
        _menu.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _menu.OffsetLeft = -380;
        _menu.OffsetTop = 10;
        _menu.OffsetRight = -10;
        layer.AddChild(_menu);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        _menu.AddChild(box);
        box.AddChild(new Label { Text = "Workshops (click to paint a room; B closes)" });
        var grid = new GridContainer { Columns = 2 };
        box.AddChild(grid);
        foreach (var w in Catalog.Workshops)
        {
            var b = new Button { Text = w.Name, ToggleMode = true, TooltipText = $"{w.About}\nMakes: {string.Join(", ", w.Recipes.Select(r => r.Name))}" };
            b.AddThemeColorOverride("font_color", RoomView.RoomColor(w) with { A = 1f });
            b.CustomMinimumSize = new Vector2(176, 0);
            var ws = w;
            b.Pressed += () => SelectWorkshop(ws);
            grid.AddChild(b);
            _workshopButtons[w] = b;
        }
        _partsTitle = new Label();
        box.AddChild(_partsTitle);
        _partList = new VBoxContainer();
        box.AddChild(_partList);
        SelectWorkshop(Workshop, enterMode: false);
    }

    private void SelectWorkshop(RoomType w, bool enterMode = true)
    {
        Workshop = w;
        foreach (var (ws, b) in _workshopButtons) b.ButtonPressed = ws == w;
        _partsTitle.Text = $"{w.Name}: parts\n[ ] size   R rotate   F: recipe of the machine under the cursor";
        foreach (var child in _partList.GetChildren()) child.QueueFree();
        foreach (var part in w.Stations.Concat(Catalog.GenericParts))
        {
            string makes = part.Recipes.Count > 0 ? "  -  " + string.Join(", ", part.Recipes.Select(r => r.Name.ToLowerInvariant())) : "";
            var b = new Button { Text = part.Name + makes, Alignment = HorizontalAlignment.Left, ClipText = true, CustomMinimumSize = new Vector2(356, 0) };
            b.TooltipText = string.Join("\n", part.Recipes.Select(r => $"{r.Describe()}   ({Catalog.EducationName(r.Education)})"));
            var pt = part;
            b.Pressed += () => SelectPart(pt);
            _partList.AddChild(b);
        }
        if (enterMode)
        {
            Mode = ToolMode.Room;
            EndSelection();
        }
    }

    private void SelectPart(PartType part)
    {
        Part = part;
        Mode = ToolMode.Part;
        EndSelection();
    }

    /// <summary>Opens the build menu on a workshop (dev aid; players press B).</summary>
    public void OpenBuildMenu(string workshopId)
    {
        _menu.Visible = true;
        SelectWorkshop(Catalog.Workshops.FirstOrDefault(w => w.Id == workshopId) ?? Workshop);
    }

    /// <summary>True while the mouse is over the build menu (so clicks there don't reach the world).</summary>
    private bool OverMenu => _menu.Visible && _menu.GetGlobalRect().HasPoint(GetViewport().GetMousePosition());

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
        bool placing = PartFor(Mode) is not null && _selection == Selection.None;
        _cursor.Visible = Hovered.HasValue && _selection == Selection.None && !placing;
        if (Hovered is { } h)
            _cursor.Position = SimSpace.TileCenter(IsAreaTool(Mode) && SurfaceAt(h.X, h.Y) is >= 0 and var s ? h with { Z = s } : h);
        UpdateGhost(placing);

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

    public override void _UnhandledInput(InputEvent e)
    {
        if (_sim == null) return;
        if (e is InputEventMouseButton && OverMenu) return;

        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } when _selection != Selection.None:
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } when _selection == Selection.Cancel:
                Apply();
                Handled();
                break;

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } when _selection == Selection.Designate:
                EndSelection();
                Handled();
                break;

            // Parts go down with a single click (no box).
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } when PartFor(Mode) is { } part:
                if (Hovered is { } at) PlacePart(part, at.Offset(0, 0, 1));
                Handled();
                break;

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right } mb
                when Hovered is { } start:
                _selection = mb.ButtonIndex == MouseButton.Left ? Selection.Designate : Selection.Cancel;
                _anchor = start;
                (_cornerX, _cornerY, _cornerZ) = (start.X, start.Y, start.Z);
                _surface.Clear(); // the ground may have changed since the last selection
                _drapeRect = (-1, -1, -1, -1);
                UpdateBox();
                Handled();
                break;

            case InputEventKey { Pressed: true, Keycode: Key.Escape } when _selection != Selection.None:
                EndSelection();
                Handled();
                break;

            // While selecting, Z/X move the free corner between levels and the view follows it (DF-style cursor).
            case InputEventKey { Pressed: true, Keycode: Key.Z or Key.X or Key.Pagedown or Key.Pageup } k
                when _selection != Selection.None && !IsAreaTool(Mode):
                int step = k.Keycode is Key.X or Key.Pageup ? 1 : -1;
                _cornerZ = Math.Clamp(_cornerZ + step, 1, _sim.World.Height - 1);
                _view.ViewLevel = _cornerZ;
                Handled();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Tab } tab:
                Mode = Cycle(Mode, ToolMode.Dig, LastOrder, tab.ShiftPressed);
                _menu.Visible = false;
                EndSelection();
                Handled();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.B }:
                _menu.Visible = !_menu.Visible;
                if (_menu.Visible && !IsBuild(Mode)) Mode = ToolMode.Room;
                else if (!_menu.Visible && IsBuild(Mode)) Mode = ToolMode.Dig;
                EndSelection();
                Handled();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.R } when PartFor(Mode) is not null:
                _rotated = !_rotated;
                Handled();
                break;

            case InputEventKey { Pressed: true, Keycode: Key.Bracketleft or Key.Bracketright } sk
                when PartFor(Mode) is { } sized && PartInfo.Sizes(sized) > 1:
                _sizes[sized] = PartInfo.ClampSize(sized, SizeFor(sized) + (sk.Keycode == Key.Bracketright ? 1 : -1));
                Handled();
                break;

            // F: change what the machine under the cursor makes.
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.F } fk when HoveredMachine() is { } machine:
                if (_sim.Rooms.CycleRecipe(machine, fk.ShiftPressed))
                    LastAction = $"{machine.Name} in {machine.Room.Name} now makes {machine.Recipe!.Name.ToLowerInvariant()}: {machine.Recipe.Describe()}";
                else
                    LastAction = $"{machine.Name} only makes {machine.Recipe!.Name.ToLowerInvariant()}";
                Handled();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter }:
                DebugCompleteAll();
                break;
        }
    }

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

    private void PlacePart(PartType part, TilePos cell)
    {
        int size = SizeFor(part);
        string name = PartInfo.Name(part, size).ToLowerInvariant();
        var placed = _sim.Rooms.Place(part, cell, _rotated, size);
        LastAction = placed is not null
            ? (placed.Built ? $"Placed a {name} in {placed.Room.Name}" : $"Placed a {name} blueprint in {placed.Room.Name}: needs {placed.Cost.Count} {placed.Cost.Material.Name.ToLowerInvariant()} and a builder")
            : $"Can't place {name}: {_sim.Rooms.CanPlace(part, cell, _rotated, size)}";
    }

    private void Apply()
    {
        var d = _sim.Designations;
        var (a, b) = Corners();
        if (_selection == Selection.Cancel)
        {
            if (Mode == ToolMode.Room)
                LastAction = $"Erased {_sim.Rooms.Erase(a, b)} room cells";
            else if (PartFor(Mode) is not null)
                LastAction = $"Removed {_sim.Rooms.RemovePartsIn(a, b)} parts";
            else if (IsAreaTool(Mode))
                LastAction = $"Cancelled {d.CancelPlantArea(KindFor(Mode, 0, 0, 0), a.X, a.Y, b.X, b.Y)} {BaseModeName(Mode).ToLowerInvariant()} orders";
            else
            {
                // Box corners are ground tiles: also clear tree/stockpile cells standing on them.
                int orders = d.CancelBox(a with { Z = Math.Min(a.Z, b.Z) }, b with { Z = Math.Max(a.Z, b.Z) + 1 });
                int zones = _sim.Stockpiles.RemoveBox(a, b);
                int plots = _sim.Farms.RemoveBox(a, b);
                LastAction = $"Cancelled {orders} designations, removed {zones} stockpile and {plots} farm cells";
            }
        }
        else if (Mode == ToolMode.Room)
        {
            int n = _sim.Rooms.Paint(Workshop, a, b);
            LastAction = n > 0 ? $"{Workshop.Name}: painted {n} floor cells" : $"{Workshop.Name}: no free floor in that box";
        }
        else if (CropFor(Mode) is { } crop)
        {
            int n = _sim.Farms.AddFloorBox(crop, a, b);
            LastAction = n > 0 ? $"{BaseModeName(Mode)}: zoned {n} plots" :
                crop == PlantKind.Glowcap ? "No plots: glowcaps need a solid floor under a roof" : "No plots: crops need free grass or soil";
        }
        else
        {
            int n = Mode switch
            {
                ToolMode.Stairs => d.DesignateStairs(a, b),
                // Plant work is always on the surface: every plant in the area's columns, whatever the level.
                ToolMode.Chop or ToolMode.Gather => d.DesignatePlantArea(KindFor(Mode, 0, 0, 0), a.X, a.Y, b.X, b.Y),
                ToolMode.Stockpile => _sim.Stockpiles.AddFloorBox(a, b),
                _ => d.DesignateBox(a, b, KindFor(Mode, 0, 0, 0)),
            };
            LastAction = Mode switch
            {
                ToolMode.Chop => $"Chop trees: marked {n} trees",
                ToolMode.Gather => $"Gather plants: marked {n} ripe plants",
                _ => $"{ModeName(Mode)}: designated {n} tiles",
            };
        }
        EndSelection();
    }

    private void EndSelection()
    {
        _selection = Selection.None;
        _box.Visible = false;
        _drape.Visible = false;
    }

    /// <summary>Debug shortcut: instantly completes every designation.</summary>
    private void DebugCompleteAll()
    {
        var d = _sim.Designations;
        var all = new List<TilePos>(d.All.Keys);
        // Top-down, so stairs carve in the order a colonist would and cave-ins behave predictably.
        all.Sort((p, q) => q.Z.CompareTo(p.Z));
        foreach (var p in all) _sim.CompleteDesignation(p);
        LastAction = $"Debug: completed {all.Count} designations";
    }

    private void UpdateGhost(bool placing)
    {
        PlacementProblem = null;
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
