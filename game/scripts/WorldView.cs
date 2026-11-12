using System.Collections.Generic;
using Colony.Core.Procedural;
using Colony.Core.World;
using Godot;

namespace ColonyGame;

/// <summary>
/// Renders a <see cref="VoxelWorld"/> as instanced cubes, sliced at the current view level
/// (everything above the view level is hidden, Dwarf Fortress style).
/// Simulation z (vertical) maps to Godot Y.
/// </summary>
public partial class WorldView : Node3D
{
    [Export] public int Seed { get; set; } = 12345;
    [Export] public int WorldWidth { get; set; } = 64;
    [Export] public int WorldDepth { get; set; } = 64;
    [Export] public int WorldHeight { get; set; } = 40;

    private VoxelWorld _world = null!;
    private MultiMeshInstance3D _blocks = null!;
    private Label _hud = null!;
    private int _viewLevel;

    public Vector3 Center => new(WorldWidth / 2f, WorldHeight * 0.4f, WorldDepth / 2f);

    public override void _Ready()
    {
        _world = WorldGenerator.Generate(new WorldGenSettings(Seed, WorldWidth, WorldDepth, WorldHeight));
        _viewLevel = WorldHeight - 1;

        var cube = new BoxMesh
        {
            Size = Vector3.One,
            Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.9f },
        };
        _blocks = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = cube },
        };
        AddChild(_blocks);

        var ui = new CanvasLayer();
        _hud = new Label { Position = new Vector2(12, 10) };
        _hud.AddThemeFontSizeOverride("font_size", 18);
        ui.AddChild(_hud);
        AddChild(ui);

        Rebuild();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key) return;

        int delta = key.Keycode switch
        {
            Key.Pageup or Key.X => 1,
            Key.Pagedown or Key.Z => -1,
            _ => 0,
        };
        if (delta == 0) return;

        int next = Mathf.Clamp(_viewLevel + delta, 0, WorldHeight - 1);
        if (next == _viewLevel) return;
        _viewLevel = next;
        Rebuild();
    }

    private void Rebuild()
    {
        var visible = new List<(int x, int y, int z, TileType t)>();

        for (int z = 0; z <= _viewLevel; z++)
        for (int y = 0; y < WorldDepth; y++)
        for (int x = 0; x < WorldWidth; x++)
        {
            var t = _world.Get(x, y, z);
            if (t != TileType.Air && IsExposed(x, y, z))
                visible.Add((x, y, z, t));
        }

        var mm = _blocks.Multimesh;
        mm.InstanceCount = visible.Count;
        for (int i = 0; i < visible.Count; i++)
        {
            var (x, y, z, t) = visible[i];
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(x + 0.5f, z + 0.5f, y + 0.5f)));

            // Cut-plane tiles get a lighter tint so the current level stands out.
            var c = ColorFor(t);
            if (z == _viewLevel) c = c.Lightened(0.25f);
            mm.SetInstanceColor(i, c);
        }

        _hud.Text = $"Level {_viewLevel} / {WorldHeight - 1}   ({visible.Count} visible blocks)\n" +
                    "Z / PgDn: down a level    X / PgUp: up a level\n" +
                    "WASD: pan   Q/E: rotate   Wheel: zoom   Middle-drag: orbit";
    }

    /// <summary>A block is drawn if any neighbour is air, or is above the view slice (and so hidden).</summary>
    private bool IsExposed(int x, int y, int z)
    {
        if (z == _viewLevel) return true;
        return !_world.IsSolid(x + 1, y, z) || !_world.IsSolid(x - 1, y, z)
            || !_world.IsSolid(x, y + 1, z) || !_world.IsSolid(x, y - 1, z)
            || !_world.IsSolid(x, y, z + 1) || !_world.IsSolid(x, y, z - 1);
    }

    private static Color ColorFor(TileType t) => t switch
    {
        TileType.Grass => new Color(0.36f, 0.62f, 0.28f),
        TileType.Soil => new Color(0.48f, 0.34f, 0.22f),
        TileType.Stone => new Color(0.52f, 0.52f, 0.55f),
        TileType.Ore => new Color(0.80f, 0.55f, 0.25f),
        _ => Colors.Magenta,
    };
}
