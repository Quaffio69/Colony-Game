using System;
using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Orders;
using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>
/// Mouse picking and DF-style box designations. Hover highlights the tile under the cursor (respecting the view
/// slice). Click once to anchor a corner, move the mouse (and Z/X to extend the box across levels; the view
/// follows the selection's level), click again to apply. Right-click starts a cancel box, or aborts a pending
/// selection; Esc aborts too. Tab / Shift+Tab cycle the tool:
/// <list type="bullet">
/// <item>Dig: mine out every tile in the box.</item>
/// <item>Stairs: a connected staircase: down stairs on the top level, up/down between, up stairs at the bottom.</item>
/// <item>Up / Down / Up-Down stair: carve exactly that stair type into every tile in the box.</item>
/// <item>Chop trees: fell every tree standing on the box.</item>
/// <item>Stockpile: zone the floor on top of the box for storage (haulers bring loose items there).</item>
/// </list>
/// </summary>
public partial class DesignationTool : Node3D
{
    public enum ToolMode { Dig, Stairs, UpStair, DownStair, UpDownStair, Chop, Stockpile }
    private enum Selection { None, Designate, Cancel }

    private ColonySim _sim = null!;
    private WorldView _view = null!;

    private MeshInstance3D _cursor = null!;
    private MeshInstance3D _box = null!;
    private readonly Dictionary<ToolMode, StandardMaterial3D> _modeMats = new();
    private StandardMaterial3D _cancelMat = null!;

    private Selection _selection;
    private TilePos _anchor;
    private int _cornerZ, _cornerX, _cornerY;

    public ToolMode Mode { get; private set; } = ToolMode.Dig;
    public TilePos? Hovered { get; private set; }
    public string? LastAction { get; private set; }

    public static string ModeName(ToolMode m) => m switch
    {
        ToolMode.Dig => "Dig",
        ToolMode.Stairs => "Stairs (auto)",
        ToolMode.UpStair => "Up stair",
        ToolMode.DownStair => "Down stair",
        ToolMode.UpDownStair => "Up/Down stair",
        ToolMode.Chop => "Chop trees",
        _ => "Stockpile",
    };

    /// <summary>Describes the pending selection for the HUD, or null when none.</summary>
    public string? SelectionInfo
    {
        get
        {
            if (_selection == Selection.None) return null;
            var (a, b) = Corners();
            int w = Math.Abs(a.X - b.X) + 1, d = Math.Abs(a.Y - b.Y) + 1, h = Math.Abs(a.Z - b.Z) + 1;
            string what = _selection == Selection.Cancel ? "Cancel" : ModeName(Mode);
            string levels = h > 1 ? $"levels {Math.Min(a.Z, b.Z)}..{Math.Max(a.Z, b.Z)}" : $"level {a.Z}";
            return $"{what}: {w}x{d}x{h} ({levels})   click to confirm, Z/X: change level, Esc/RMB: abort";
        }
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
            _modeMats[mode] = Overlay.Material(mode == ToolMode.Stockpile
                ? ItemView.StockpileColor with { A = 0.3f }
                : WorldView.DesignationColor(KindFor(mode, 0, 0, 0)) with { A = 0.3f });
        _cancelMat = Overlay.Material(new Color(1f, 0.25f, 0.2f, 0.3f));
        _box = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = Vector3.One },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_box);
    }

    /// <summary>Designation kind a mode places on level z of a box spanning bottom..top (Stairs mode varies by level).</summary>
    private static DesignationKind KindFor(ToolMode mode, int z, int bottom, int top) => mode switch
    {
        ToolMode.Dig => DesignationKind.Dig,
        ToolMode.UpStair => DesignationKind.UpStair,
        ToolMode.DownStair => DesignationKind.DownStair,
        ToolMode.UpDownStair => DesignationKind.UpDownStair,
        ToolMode.Chop => DesignationKind.Chop,
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

        var hit = VoxelRaycast.Cast(_sim.World, origin, dir, 5000f, _view.ViewLevel);
        Hovered = hit?.Tile;
        _cursor.Visible = Hovered.HasValue && _selection == Selection.None;
        if (Hovered is { } h) _cursor.Position = SimSpace.TileCenter(h);

        if (_selection != Selection.None)
        {
            // The free corner slides on the top plane of its level, so it tracks the mouse over open air too.
            if (VoxelRaycast.IntersectLevelPlane(origin, dir, _cornerZ + 1) is { } pt)
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

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right } mb
                when Hovered is { } start:
                _selection = mb.ButtonIndex == MouseButton.Left ? Selection.Designate : Selection.Cancel;
                _anchor = start;
                (_cornerX, _cornerY, _cornerZ) = (start.X, start.Y, start.Z);
                UpdateBox();
                Handled();
                break;

            case InputEventKey { Pressed: true, Keycode: Key.Escape } when _selection != Selection.None:
                EndSelection();
                Handled();
                break;

            // While selecting, Z/X move the free corner between levels and the view follows it (DF-style cursor).
            case InputEventKey { Pressed: true, Keycode: Key.Z or Key.X or Key.Pagedown or Key.Pageup } k
                when _selection != Selection.None:
                int step = k.Keycode is Key.X or Key.Pageup ? 1 : -1;
                _cornerZ = Math.Clamp(_cornerZ + step, 1, _sim.World.Height - 1);
                _view.ViewLevel = _cornerZ;
                Handled();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Tab } tab:
                int n = Enum.GetValues<ToolMode>().Length;
                Mode = (ToolMode)(((int)Mode + (tab.ShiftPressed ? n - 1 : 1)) % n);
                Handled();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter }:
                DebugCompleteAll();
                break;
        }
    }

    private void Handled() => GetViewport().SetInputAsHandled();

    private (TilePos A, TilePos B) Corners() => (_anchor, new TilePos(_cornerX, _cornerY, _cornerZ));

    private void Apply()
    {
        var d = _sim.Designations;
        var (a, b) = Corners();
        if (_selection == Selection.Cancel)
        {
            // Box corners are ground tiles: also clear tree/stockpile cells standing on them.
            int orders = d.CancelBox(a with { Z = Math.Min(a.Z, b.Z) }, b with { Z = Math.Max(a.Z, b.Z) + 1 });
            int zones = _sim.Stockpiles.RemoveBox(a, b);
            LastAction = $"Cancelled {orders} designations, removed {zones} stockpile cells";
        }
        else
        {
            int n = Mode switch
            {
                ToolMode.Stairs => d.DesignateStairs(a, b),
                // Corners are picked on the ground; trees stand in the cell above it.
                ToolMode.Chop => d.DesignateBox(a.Offset(0, 0, 1), b.Offset(0, 0, 1), DesignationKind.Chop),
                ToolMode.Stockpile => _sim.Stockpiles.AddFloorBox(a, b),
                _ => d.DesignateBox(a, b, KindFor(Mode, 0, 0, 0)),
            };
            LastAction = $"{ModeName(Mode)}: designated {n} tiles";
        }
        EndSelection();
    }

    private void EndSelection()
    {
        _selection = Selection.None;
        _box.Visible = false;
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

    private void UpdateBox()
    {
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
}
