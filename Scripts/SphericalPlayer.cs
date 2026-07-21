using Godot;

namespace HexaSphericalSandbox;

public partial class SphericalPlayer : CharacterBody3D
{
    [Export] public float MoveSpeed { get; set; } = 7.0f;
    [Export] public float Acceleration { get; set; } = 24.0f;
    [Export] public float Gravity { get; set; } = 22.0f;
    [Export] public float JumpSpeed { get; set; } = 11.0f;
    [Export] public float MouseSensitivity { get; set; } = 0.0025f;
    [Export] public float GroundClearance { get; set; } = 0.78f;
    [Export] public float MaxStepHeight { get; set; } = 0.35f;
    [Export] public float BodyHeight { get; set; } = 1.5f;
    [Export] public float BodyRadius { get; set; } = 0.28f;
    public float Health { get; private set; } = 100f;

    private Node3D _pivot = null!;
    private Camera3D _camera = null!;
    private MeshInstance3D _bodyMesh = null!;
    private SpotLight3D _flashlight = null!;
    private MeshInstance3D _flashlightModel = null!;
    private HexPlanet _planet = null!;
    private float _pitch = -0.22f;
    private bool _surfaceGrounded;
    private bool _thirdPerson;
    private bool _flying;
    private double _lastJumpTapAt = -10.0;
    private float _flashlightProbeCooldown;
    private Label _flightLabel = null!;
    private Label _healthLabel = null!;
    private MusicManager _music = null!;

    public override void _Ready()
    {
        _pivot = GetNode<Node3D>("Pivot");
        _camera = GetNode<Camera3D>("Pivot/Camera3D");
        _bodyMesh = GetNode<MeshInstance3D>("BodyMesh");
        _flashlight = GetNode<SpotLight3D>("Pivot/FlashlightRig/Flashlight");
        _flashlightModel = GetNode<MeshInstance3D>("Pivot/FlashlightRig/FlashlightModel");
        _flightLabel = GetNode<Label>("../HUD/FlightLabel");
        _healthLabel = GetNode<Label>("../HUD/HealthLabel");
        _music = GetNode<MusicManager>("../MusicManager");
        _planet = GetNode<HexPlanet>("../Planet");
        Health = Mathf.Clamp(GameSession.Current?.Health ?? 100f, 1f, 100f);
        WorldData? world = GameSession.Current;
        float[] savedPosition = world?.PlayerPosition ?? [];
        bool positionIsCurrent = world?.SaveVersion >= 5 && savedPosition.Length == 3;
        Vector3 saved = positionIsCurrent
            ? new Vector3(savedPosition[0], savedPosition[1], savedPosition[2]) : Vector3.Zero;
        Vector3 savedDirection = saved.LengthSquared() > 1f ? saved.Normalized() : Vector3.Up;
        float savedFeetRadius = saved.Length() - GroundClearance;
        bool savedPositionHasRoom = positionIsCurrent && saved.LengthSquared() > 1f
            && _planet.HasRoom(savedDirection, savedFeetRadius, BodyHeight);
        GlobalPosition = savedPositionHasRoom
            ? saved
            : savedDirection * (_planet.SurfaceRadius(savedDirection) + GroundClearance + 1.2f);
        if (world != null && (world.SaveVersion < 5 || !savedPositionHasRoom))
        {
            world.SaveVersion = 5;
            world.PlayerPosition = [GlobalPosition.X, GlobalPosition.Y, GlobalPosition.Z];
        }
        Input.MouseMode = Input.MouseModeEnum.Captured;
        AlignToPlanet(Vector3.Up);
        UpdateHealthLabel();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("jump") && @event is not InputEventKey { Echo: true })
        {
            double now = Time.GetTicksMsec() / 1000.0;
            if (now - _lastJumpTapAt <= 0.32)
            {
                if (!GameSession.IsCreative) { _lastJumpTapAt = -10.0; return; }
                _flying = !_flying;
                Velocity = Vector3.Zero;
                _flightLabel.Visible = _flying;
                _lastJumpTapAt = -10.0;
            }
            else _lastJumpTapAt = now;
        }

        if (@event is InputEventKey flashlightKey && flashlightKey.Pressed
            && !flashlightKey.Echo && flashlightKey.Keycode == Key.G)
        {
            _flashlight.Visible = !_flashlight.Visible;
            _flashlightModel.Visible = _flashlight.Visible;
        }

        if (@event is InputEventKey viewKey && viewKey.Pressed && !viewKey.Echo && viewKey.Keycode == Key.F5)
        {
            _thirdPerson = !_thirdPerson;
            _bodyMesh.Visible = _thirdPerson;
            _camera.Position = _thirdPerson ? new Vector3(0, 0.7f, 4.2f) : Vector3.Zero;
        }

        if (@event.IsActionPressed("toggle_mouse"))
            Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;

        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateObjectLocal(Vector3.Up, -motion.Relative.X * MouseSensitivity);
            // Almost the full vertical range: enough to mine directly below or
            // above without allowing the camera basis to flip upside down.
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -1.562f, 1.562f);
            _pivot.Rotation = new Vector3(_pitch, 0, 0);
        }

        if (@event is InputEventMouseButton mouseButton
            && mouseButton.Pressed
            && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            if (mouseButton.ButtonIndex == MouseButton.Left)
                _planet.TryEdit(_camera.GlobalPosition, -_camera.GlobalBasis.Z, -1);
            else if (mouseButton.ButtonIndex == MouseButton.Right)
                _planet.TryEdit(_camera.GlobalPosition, -_camera.GlobalBasis.Z, 1);
        }
    }

    public override void _PhysicsProcess(double deltaValue)
    {
        float delta = (float)deltaValue;
        Vector3 up = GlobalPosition.Normalized();
        AlignToPlanet(up);

        Vector2 input = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        Vector3 desired = (GlobalTransform.Basis.X * input.X + GlobalTransform.Basis.Z * input.Y).Normalized() * MoveSpeed;

        if (_flying)
        {
            float vertical = (Input.IsActionPressed("jump") ? 1.0f : 0.0f)
                           - (Input.IsPhysicalKeyPressed(Key.Shift) ? 1.0f : 0.0f);
            Vector3 flightTarget = desired + up * vertical * MoveSpeed;
            Velocity = Velocity.MoveToward(flightTarget, Acceleration * delta);
            GlobalPosition += Velocity * delta;
            UpdateFlashlight(delta);
            return;
        }

        Vector3 radialVelocity = up * Velocity.Dot(up);
        Vector3 tangentVelocity = Velocity - radialVelocity;
        tangentVelocity = tangentVelocity.MoveToward(desired, Acceleration * delta);

        bool grounded = _surfaceGrounded;
        if (grounded)
        {
            radialVelocity = Vector3.Zero;
            if (Input.IsActionJustPressed("jump")) radialVelocity = up * JumpSpeed;
        }
        else
        {
            radialVelocity -= up * Gravity * delta;
        }

        Velocity = tangentVelocity + radialVelocity;
        UpDirection = up;
        MoveAcrossVoxelSurface(tangentVelocity, radialVelocity, delta);
        KeepAboveProceduralSurface();
        UpdateFlashlight(delta);
    }

    private void AlignToPlanet(Vector3 up)
    {
        Vector3 forward = -GlobalTransform.Basis.Z;
        forward = (forward - up * forward.Dot(up)).Normalized();
        if (forward.LengthSquared() < 0.01f) forward = up.Cross(Vector3.Right).Normalized();
        Vector3 right = forward.Cross(up).Normalized();
        GlobalBasis = new Basis(right, up, -forward).Orthonormalized();
    }

    private void KeepAboveProceduralSurface()
    {
        Vector3 direction = GlobalPosition.Normalized();
        float feetRadius = GlobalPosition.Length() - GroundClearance;
        float floorRadius = _planet.FloorRadius(direction, feetRadius + MaxStepHeight);
        float minimumRadius = floorRadius + GroundClearance;
        float currentRadius = GlobalPosition.Length();
        bool wasGrounded = _surfaceGrounded;
        _surfaceGrounded = currentRadius <= minimumRadius + 0.12f;

        float inwardSpeed = Velocity.Dot(direction);
        if (_surfaceGrounded && !wasGrounded && inwardSpeed < -12.5f && !GameSession.IsCreative)
            ApplyDamage((Mathf.Abs(inwardSpeed) - 12.5f) * 7.0f);

        if (currentRadius >= minimumRadius) return;

        GlobalPosition = direction * minimumRadius;
        if (inwardSpeed < 0.0f)
            Velocity -= direction * inwardSpeed;
    }

    private void MoveAcrossVoxelSurface(Vector3 tangentVelocity, Vector3 radialVelocity, float delta)
    {
        Vector3 currentDirection = GlobalPosition.Normalized();
        float currentFeet = GlobalPosition.Length() - GroundClearance;
        float currentSurface = _planet.FloorRadius(currentDirection, currentFeet + MaxStepHeight);
        Vector3 tangentMove = tangentVelocity * delta;
        Vector3 candidate = GlobalPosition + tangentMove;
        Vector3 candidateDirection = candidate.Normalized();
        float candidateSurface = _planet.FloorRadius(candidateDirection, currentFeet + MaxStepHeight);

        // A taller neighbouring column behaves like a wall. It can only be
        // crossed once a jump has lifted the player's feet above its top.
        float feetRadius = GlobalPosition.Length() - GroundClearance;
        Vector3 right = GlobalBasis.X * BodyRadius;
        Vector3 forward = -GlobalBasis.Z * BodyRadius;
        Vector3[] bodySamples = [candidate, candidate + right, candidate - right,
            candidate + forward, candidate - forward];
        bool noBodyRoom = false;
        foreach (Vector3 sample in bodySamples)
        {
            if (_planet.HasRoom(sample.Normalized(), feetRadius, BodyHeight)) continue;
            noBodyRoom = true;
            break;
        }
        bool blockedByColumn = noBodyRoom || (candidateSurface > currentSurface + MaxStepHeight
                            && feetRadius < candidateSurface);

        if (!blockedByColumn)
            GlobalPosition = candidate;
        else
            Velocity -= tangentVelocity;

        GlobalPosition += currentDirection * radialVelocity.Dot(currentDirection) * delta;
    }

    private void ApplyDamage(float amount)
    {
        Health = Mathf.Max(0f, Health - amount);
        UpdateHealthLabel();
        if (Health > 0f) return;
        _music.PlayDeathMusic(false);
        Health = 100f;
        Velocity = Vector3.Zero;
        GlobalPosition = Vector3.Up * (_planet.SurfaceRadius(Vector3.Up) + GroundClearance + 1.2f);
        UpdateHealthLabel();
    }

    private void UpdateHealthLabel()
    {
        _healthLabel.Visible = !GameSession.IsCreative;
        _healthLabel.Text = $"Vie : {Mathf.CeilToInt(Health)} / 100";
    }

    private void UpdateFlashlight(float delta)
    {
        if (!_flashlight.Visible) return;
        _flashlightProbeCooldown -= delta;
        if (_flashlightProbeCooldown > 0f) return;
        _flashlightProbeCooldown = 0.1f;

        float hitDistance = _planet.GetRayHitDistance(
            _flashlight.GlobalPosition, -_flashlight.GlobalBasis.Z, _flashlight.SpotRange);
        float nearWallFactor = Mathf.SmoothStep(0.45f, 2.4f, hitDistance);
        _flashlight.LightEnergy = Mathf.Lerp(0.3f, 5.0f, nearWallFactor);
    }
}
