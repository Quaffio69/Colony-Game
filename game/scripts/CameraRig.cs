using Godot;

namespace ColonyGame;

/// <summary>Orbiting strategy camera: WASD pan, Q/E rotate, wheel zoom, middle-mouse drag to orbit.</summary>
public partial class CameraRig : Node3D
{
    [Export] public float PanSpeed { get; set; } = 30f;
    [Export] public float RotateSpeed { get; set; } = 90f;

    private Camera3D _camera = null!;
    public Camera3D Camera => _camera;
    public float Distance => _distance;
    /// <summary>How far the camera sits from the pivot (more than Distance in ortho).</summary>
    public float CameraBack => Orthographic ? _distance + 250f : _distance;
    private float _yaw = 45f, _pitch = -50f, _distance = 120f, _targetDistance = 120f;
    /// <summary>Iso projection: the view keeps the size a perspective view would have at this distance, from far back.</summary>
    public bool Orthographic { get; set; }
    /// <summary>Closest zoom (tiles from the pivot) and furthest.</summary>
    public const float MinDistance = 4f, MaxDistance = 900f;
    /// <summary>Turns (yaw and pitch) are shown in steps of this many degrees; 0 = smooth. The camera itself snaps, so picking matches the picture.</summary>
    public float OrbitStep { get; set; }
    private float ShownYaw => OrbitStep > 0 ? Mathf.Round(_yaw / OrbitStep) * OrbitStep : _yaw;
    private float ShownPitch => OrbitStep > 0 ? Mathf.Clamp(Mathf.Round(_pitch / OrbitStep) * OrbitStep, -89f, -10f) : _pitch;
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
        if (distance is { } d) _distance = _targetDistance = d;
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
            var yawBasis = new Basis(Vector3.Up, Mathf.DegToRad(ShownYaw));
            var move = yawBasis * new Vector3(input.X, 0, input.Y).Normalized();
            Position += move * PanSpeed * dt * (_distance / 70f);
        }

        // Keep the pivot on the ground that's actually visible (e.g. the cut surface after peeling levels
        // away), so zoom is always relative to what's on screen. Eased, so cliffs don't jolt the view.
        // Zoom eases towards the wheel's target, so the pixel-art renderer's zoom transitions glide instead of jumping.
        _distance = Mathf.Lerp(_distance, _targetDistance, 1f - Mathf.Exp(-dt * 12f));
        if (Mathf.Abs(_distance - _targetDistance) < 0.01f) _distance = _targetDistance;

        if (GroundHeight?.Invoke(Position.X, Position.Z) is { } ground)
            Position = Position with { Y = Mathf.Lerp(Position.Y, ground, 1f - Mathf.Exp(-dt * 10f)) };

        ApplyTransform();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                _targetDistance = Mathf.Max(MinDistance, _targetDistance * 0.9f);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _targetDistance = Mathf.Min(MaxDistance, _targetDistance * 1.1f);
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
        RotationDegrees = new Vector3(0, ShownYaw, 0);
        var pitch = Mathf.DegToRad(ShownPitch);
        if (Orthographic)
        {
            // Same framing as perspective at this distance, seen from far enough back that nothing behind the pivot is clipped.
            _camera.Projection = Camera3D.ProjectionType.Orthogonal;
            _camera.Size = 2f * _distance * Mathf.Tan(Mathf.DegToRad(_camera.Fov * 0.5f));
            float back = _distance + 250f; // far enough back for hills between camera and pivot; shadows still reach
            _camera.Position = new Vector3(0, -Mathf.Sin(pitch) * back, Mathf.Cos(pitch) * back);
        }
        else
        {
            _camera.Projection = Camera3D.ProjectionType.Perspective;
            _camera.Position = new Vector3(0, -Mathf.Sin(pitch) * _distance, Mathf.Cos(pitch) * _distance);
        }
        _camera.LookAt(GlobalPosition, Vector3.Up);
    }
}
