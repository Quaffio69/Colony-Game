using Godot;

namespace ColonyGame;

/// <summary>Orbiting strategy camera: WASD pan, Q/E rotate, wheel zoom, middle-mouse drag to orbit.</summary>
public partial class CameraRig : Node3D
{
    [Export] public float PanSpeed { get; set; } = 30f;
    [Export] public float RotateSpeed { get; set; } = 90f;

    private Camera3D _camera = null!;
    private float _yaw = 45f, _pitch = -50f, _distance = 70f;
    private bool _orbiting;

    public override void _Ready()
    {
        _camera = new Camera3D { Current = true, Far = 1000f };
        AddChild(_camera);
        ApplyTransform();
    }

    public void Focus(Vector3 point)
    {
        Position = point;
        ApplyTransform();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var input = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W)) input.Y -= 1;
        if (Input.IsKeyPressed(Key.S)) input.Y += 1;
        if (Input.IsKeyPressed(Key.A)) input.X -= 1;
        if (Input.IsKeyPressed(Key.D)) input.X += 1;
        if (Input.IsKeyPressed(Key.Q)) _yaw += RotateSpeed * dt;
        if (Input.IsKeyPressed(Key.E)) _yaw -= RotateSpeed * dt;

        if (input != Vector2.Zero)
        {
            var yawBasis = new Basis(Vector3.Up, Mathf.DegToRad(_yaw));
            var move = yawBasis * new Vector3(input.X, 0, input.Y).Normalized();
            Position += move * PanSpeed * dt * (_distance / 70f);
        }

        ApplyTransform();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                _distance = Mathf.Max(8f, _distance * 0.9f);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _distance = Mathf.Min(250f, _distance * 1.1f);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } mb:
                _orbiting = mb.Pressed;
                break;
            case InputEventMouseMotion motion when _orbiting:
                _yaw -= motion.Relative.X * 0.3f;
                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * 0.3f, -89f, -10f);
                break;
        }
    }

    private void ApplyTransform()
    {
        if (_camera == null) return;
        _camera.Rotation = Vector3.Zero;
        _camera.Position = Vector3.Zero;
        RotationDegrees = new Vector3(0, _yaw, 0);
        var pitch = Mathf.DegToRad(_pitch);
        _camera.Position = new Vector3(0, -Mathf.Sin(pitch) * _distance, Mathf.Cos(pitch) * _distance);
        _camera.LookAt(GlobalPosition, Vector3.Up);
    }
}
