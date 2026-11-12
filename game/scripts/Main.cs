using Godot;

namespace ColonyGame;

/// <summary>Scene root: sets up lighting and environment, and points the camera at the world.</summary>
public partial class Main : Node3D
{
    public override void _Ready()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.45f, 0.6f, 0.8f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.65f, 0.68f, 0.75f),
            AmbientLightEnergy = 0.55f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };
        AddChild(new WorldEnvironment { Environment = env });

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, 35, 0),
            LightEnergy = 1.1f,
            ShadowEnabled = true,
        });

        var world = GetNode<WorldView>("WorldView");
        var rig = GetNode<CameraRig>("CameraRig");
        rig.Focus(world.Center);
    }
}
