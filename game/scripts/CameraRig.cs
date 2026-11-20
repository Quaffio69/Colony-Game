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
    public float CameraBack => Orthographic ? OrthoBack() : _distance;
    /// <summary>Godot Y above which nothing is drawn (the world top, or the view level's cut): the iso camera stands back far enough to clear it.</summary>
    public float Ceiling { get; set; } = 400f;
    private float _yaw = 45f, _pitch = -50f, _distance = 120f, _targetDistance = 120f;
    /// <summary>Iso projection: the view keeps the size a perspective view would have at this distance, from far back.</summary>
    public bool Orthographic { get; set; }
    /// <summary>Closest zoom (tiles from the pivot) and furthest.</summary>
    public const float MinDistance = 4f, MaxDistance = 900f;
    /// <summary>Turns (yaw and pitch) are shown in steps of this many degrees; 0 = smooth. The camera itself snaps, so picking matches the picture.</summary>
    public float OrbitStep { get; set; }
    private float StepYaw => OrbitStep > 0 ? Mathf.Round(_yaw / OrbitStep) * OrbitStep : _yaw;
    private float StepPitch => OrbitStep > 0 ? Mathf.Clamp(Mathf.Round(_pitch / OrbitStep) * OrbitStep, -89f, -10f) : _pitch;

    /// <summary>
    /// Map view: zoomed out from <see cref="TopDownFrom"/> to <see cref="TopDownAt"/> (rig distances; 0 = off) the view eases
    /// to straight down and its turn to the nearest 90°, so far out the pixel grid lines up with the tiles and turning goes
    /// in quarter turns. Zooming back in restores the angle.
    /// </summary>
    public float TopDownFrom { get; set; }
    public float TopDownAt { get; set; }
    public float TopDown => TopDownAt <= 0 || TopDownFrom <= 0 || TopDownFrom >= TopDownAt ? 0f
        : Mathf.SmoothStep(0f, 1f, Mathf.Clamp(Mathf.Log(_distance / TopDownFrom) / Mathf.Log(TopDownAt / TopDownFrom), 0f, 1f));
    // In the map view (from the first moment it eases in) the turn is a quarter turn, and the camera glides to each one;
    // blending the angle towards the nearest quarter instead jolted by up to 90° x the blend whenever the turn crossed a
    // 45° boundary. Outside it the turn follows the input directly, as before (with a glide back when leaving the map view).
    private float YawTarget => TopDown > 0 ? Mathf.Round(StepYaw / 90f) * 90f : StepYaw;
    private float _shownYaw = float.NaN;
    private bool _yawGliding, _wasMap;
    private float ShownYaw => float.IsNaN(_shownYaw) ? YawTarget : _shownYaw;
    /// <summary>The turn the camera shows (degrees), and a dev hook to turn as the mouse would (orbit checks).</summary>
    public float ShownYawDegrees => ShownYaw;
    public void DevTurn(float degrees) => _yaw += degrees;

    private void UpdateShownYaw(float dt)
    {
        float target = YawTarget;
        bool map = TopDown > 0;
        if (map != _wasMap) { _wasMap = map; _yawGliding = true; }
        if (float.IsNaN(_shownYaw) || !(map || _yawGliding)) { _shownYaw = target; return; }
        float diff = Mathf.Wrap(target - _shownYaw, -180f, 180f);
        if (Mathf.Abs(diff) < 0.05f) { _shownYaw = target; _yawGliding = false; return; }
        _shownYaw += diff * (1f - Mathf.Exp(-dt * 10f));
    }
    private float ShownPitch => Mathf.Lerp(StepPitch, -90f, TopDown);
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
        _shownYaw = float.NaN; // a jump, not a glide
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

        UpdateShownYaw(dt);
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

    /// <summary>
    /// How far behind the pivot the iso camera stands: just enough that nothing drawable on screen is behind its near
    /// plane. A point x tiles towards the camera and h above the pivot (pitch p) is on screen while
    /// x sin p - h cos p &lt;= half the view height, and in front of the camera while x cos p + h sin p &lt; back; the worst
    /// case is the highest drawable point at the bottom edge: back = half-height · cot p + h_max / sin p.
    /// (A fixed 250 sliced mountains between the camera and a valley pivot, more so zoomed in.)
    /// </summary>
    private float OrthoBack()
    {
        float p = Mathf.DegToRad(Mathf.Max(5f, -ShownPitch));
        float halfH = _distance * Mathf.Tan(Mathf.DegToRad((_camera?.Fov ?? 75f) * 0.5f));
        float hMax = Mathf.Max(0f, Ceiling - GlobalPosition.Y);
        return halfH / Mathf.Tan(p) + hMax / Mathf.Sin(p) + 16f;
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
            float back = OrthoBack();
            _camera.Far = back + 4000f; // the map beyond the pivot, however far back the camera stands
            _camera.Position = new Vector3(0, -Mathf.Sin(pitch) * back, Mathf.Cos(pitch) * back);
        }
        else
        {
            _camera.Projection = Camera3D.ProjectionType.Perspective;
            _camera.Far = 4000f;
            _camera.Position = new Vector3(0, -Mathf.Sin(pitch) * _distance, Mathf.Cos(pitch) * _distance);
        }
        _camera.Rotation = new Vector3(pitch, 0, 0); // looking at the pivot from the offset above (also straight down: LookAt can't)
    }
}
