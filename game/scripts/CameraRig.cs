using Godot;

namespace ColonyGame;

/// <summary>Orbiting strategy camera: WASD pan, Q/E rotate, wheel zoom, middle-mouse drag to orbit.</summary>
public partial class CameraRig : Node3D
{
    [Export] public float PanSpeed { get; set; } = 30f;
    [Export] public float RotateSpeed { get; set; } = 90f;

    private Camera3D _camera = null!;
    private float _yaw = 45f, _pitch = -50f, _distance = 120f;
    private bool _orbiting;

    /// <summary>Height (Godot Y) of the visible ground at a Godot (x, z), or null to leave the pivot height alone.</summary>
    public System.Func<float, float, float?>? GroundHeight { get; set; }

    public override void _Ready()
    {
        _camera = new Camera3D { Current = true, Far = 4000f };
        AddChild(_camera);
        ApplyTransform();
    }

    public void Focus(Vector3 point, float? distance = null, float? yaw = null, float? pitch = null)
    {
        Position = point;
        if (distance is { } d) _distance = d;
        if (yaw is { } y) _yaw = y;
        if (pitch is { } p) _pitch = p;
        ApplyTransform();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var input = Vector2.Zero;
        if (GetViewport().GuiGetFocusOwner() is LineEdit) return; // typing in a text box (the test menu's filter)
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

        // Keep the pivot on the ground that's actually visible (e.g. the cut surface after peeling levels
        // away), so zoom is always relative to what's on screen. Eased, so cliffs don't jolt the view.
        if (GroundHeight?.Invoke(Position.X, Position.Z) is { } ground)
            Position = Position with { Y = Mathf.Lerp(Position.Y, ground, 1f - Mathf.Exp(-dt * 10f)) };

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
                _distance = Mathf.Min(900f, _distance * 1.1f);
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
