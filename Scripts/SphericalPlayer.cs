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

    private Node3D _pivot = null!;
    private Camera3D _camera = null!;
    private MeshInstance3D _bodyMesh = null!;
    private HexPlanet _planet = null!;
    private float _pitch = -0.22f;
    private bool _surfaceGrounded;
    private bool _thirdPerson;

    public override void _Ready()
    {
        _pivot = GetNode<Node3D>("Pivot");
        _camera = GetNode<Camera3D>("Pivot/Camera3D");
        _bodyMesh = GetNode<MeshInstance3D>("BodyMesh");
        _planet = GetNode<HexPlanet>("../Planet");
        GlobalPosition = Vector3.Up * (_planet.Radius + _planet.Relief + 3.5f);
        Input.MouseMode = Input.MouseModeEnum.Captured;
        AlignToPlanet(Vector3.Up);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
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
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -1.15f, 0.65f);
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
        _surfaceGrounded = currentRadius <= minimumRadius + 0.12f;

        if (currentRadius >= minimumRadius) return;

        GlobalPosition = direction * minimumRadius;
        float inwardSpeed = Velocity.Dot(direction);
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
        bool noBodyRoom = !_planet.HasRoom(candidateDirection, feetRadius, BodyHeight);
        bool blockedByColumn = noBodyRoom || (candidateSurface > currentSurface + MaxStepHeight
                            && feetRadius < candidateSurface);

        if (!blockedByColumn)
            GlobalPosition = candidate;
        else
            Velocity -= tangentVelocity;

        GlobalPosition += currentDirection * radialVelocity.Dot(currentDirection) * delta;
    }
}
