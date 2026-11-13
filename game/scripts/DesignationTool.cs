using System;
using System.Collections.Generic;
using Colony.Core;
using Colony.Core.Orders;
using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>
/// Mouse picking and designations. Hover highlights the tile under the cursor (respecting the view slice).
/// Tab switches between tools:
/// <list type="bullet">
/// <item>Dig: left-drag designates a rectangle on the level where the drag started.</item>
/// <item>Stairs: left-drag a footprint (at least 2x2) to designate a spiral staircase going down
/// <see cref="StairDepth"/> levels ([ and ] adjust).</item>
/// </list>
/// Right-drag cancels designations in either tool.
/// </summary>
public partial class DesignationTool : Node3D
{
    public enum ToolMode { Dig, Stairs }
    private enum DragMode { None, Apply, Cancel }

    private ColonySim _sim = null!;
    private WorldView _view = null!;

    private MeshInstance3D _cursor = null!;
    private MeshInstance3D _dragBox = null!;
    private StandardMaterial3D _digMat = null!, _stairMat = null!, _cancelMat = null!;

    private DragMode _drag;
    private TilePos _dragStart, _dragEnd;

    public ToolMode Mode { get; private set; } = ToolMode.Dig;
    public int StairDepth { get; private set; } = 6;
    public TilePos? Hovered { get; private set; }
    public string? LastAction { get; private set; }

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

        _digMat = Overlay.Material(WorldView.DesignationColor(DesignationKind.Dig) with { A = 0.3f });
        _stairMat = Overlay.Material(WorldView.DesignationColor(DesignationKind.CarveStairs) with { A = 0.3f });
        _cancelMat = Overlay.Material(new Color(1f, 0.25f, 0.2f, 0.3f));
        _dragBox = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = Vector3.One },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_dragBox);
    }

    public override void _Process(double delta)
    {
        if (_sim == null) return;

        var camera = GetViewport().GetCamera3D();
        var mouse = GetViewport().GetMousePosition();
        var origin = SimSpace.ToSim(camera.ProjectRayOrigin(mouse));
        var dir = SimSpace.ToSim(camera.ProjectRayNormal(mouse));

        var hit = VoxelRaycast.Cast(_sim.World, origin, dir, 5000f, _view.ViewLevel);
        Hovered = hit?.Tile;
        _cursor.Visible = Hovered.HasValue && _drag == DragMode.None;
        if (Hovered is { } h) _cursor.Position = SimSpace.TileCenter(h);

        if (_drag != DragMode.None)
        {
            // Project onto the top of the drag level so the rectangle tracks the mouse even over open air.
            var p = VoxelRaycast.IntersectLevelPlane(origin, dir, _dragStart.Z + 1);
            if (p is { } pt)
            {
                _dragEnd = new TilePos(
                    Math.Clamp((int)MathF.Floor(pt.X), 0, _sim.World.Width - 1),
                    Math.Clamp((int)MathF.Floor(pt.Y), 0, _sim.World.Depth - 1),
                    _dragStart.Z);
            }
            UpdateDragBox();
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_sim == null) return;

        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right } mb:
                var mode = mb.ButtonIndex == MouseButton.Left ? DragMode.Apply : DragMode.Cancel;
                if (mb.Pressed && _drag == DragMode.None && Hovered is { } start)
                {
                    _drag = mode;
                    _dragStart = _dragEnd = start;
                    UpdateDragBox();
                }
                else if (!mb.Pressed && _drag == mode)
                {
                    ApplyDrag();
                }
                break;

            case InputEventKey { Pressed: true, Keycode: Key.Escape } when _drag != DragMode.None:
                EndDrag();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Tab }:
                Mode = Mode == ToolMode.Dig ? ToolMode.Stairs : ToolMode.Dig;
                break;

            case InputEventKey { Pressed: true, Keycode: Key.Bracketleft }:
                StairDepth = Math.Max(1, StairDepth - 1);
                break;

            case InputEventKey { Pressed: true, Keycode: Key.Bracketright }:
                StairDepth = Math.Min(_sim.World.Height - 1, StairDepth + 1);
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter }:
                DebugCompleteAll();
                break;
        }
    }

    /// <summary>The rectangle being dragged, grown to at least 2x2 for stairs.</summary>
    private (TilePos A, TilePos B) DragRect()
    {
        var a = _dragStart;
        var b = _dragEnd;
        if (_drag == DragMode.Apply && Mode == ToolMode.Stairs)
        {
            if (a.X == b.X) b = b with { X = a.X + 1 < _sim.World.Width ? a.X + 1 : a.X - 1 };
            if (a.Y == b.Y) b = b with { Y = a.Y + 1 < _sim.World.Depth ? a.Y + 1 : a.Y - 1 };
        }
        return (a, b);
    }

    private void ApplyDrag()
    {
        var d = _sim.Designations;
        var (a, b) = DragRect();
        if (_drag == DragMode.Cancel)
        {
            // Cancelling also sweeps the levels below in Stairs mode, so a whole staircase can be removed at once.
            var bottom = Mode == ToolMode.Stairs ? a with { Z = Math.Max(0, a.Z - StairDepth + 1) } : a;
            LastAction = $"Cancelled {d.CancelBox(bottom, b)} designations";
        }
        else if (Mode == ToolMode.Dig)
        {
            LastAction = $"Designated {d.DesignateBox(a, b)} tiles for digging";
        }
        else
        {
            int n = d.DesignateStairs(a, b, a.Z, StairDepth);
            LastAction = $"Designated a staircase, {StairDepth} levels deep ({n} tiles)";
        }
        EndDrag();
    }

    private void EndDrag()
    {
        _drag = DragMode.None;
        _dragBox.Visible = false;
    }

    /// <summary>Debug shortcut: instantly completes every designation.</summary>
    private void DebugCompleteAll()
    {
        var d = _sim.Designations;
        var all = new List<TilePos>(d.All.Keys);
        foreach (var p in all) d.Complete(p);
        LastAction = $"Debug: completed {all.Count} designations";
    }

    private void UpdateDragBox()
    {
        var (a, b) = DragRect();
        int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X) + 1;
        int y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y) + 1;
        bool stairs = _drag != DragMode.None && Mode == ToolMode.Stairs;
        int levels = stairs ? Math.Min(StairDepth, a.Z) : 1;
        int z0 = a.Z - levels + 1;

        const float pad = 0.08f;
        var size = SimSpace.ToGodot(x1 - x0, y1 - y0, levels);
        _dragBox.Position = SimSpace.ToGodot(x0, y0, z0) + size / 2f;
        _dragBox.Scale = size + Vector3.One * pad;
        _dragBox.MaterialOverride = _drag == DragMode.Cancel ? _cancelMat : stairs ? _stairMat : _digMat;
        _dragBox.Visible = true;
    }
}
