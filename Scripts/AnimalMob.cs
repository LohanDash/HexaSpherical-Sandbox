using Godot;
using System;

namespace HexaSphericalSandbox;

public partial class AnimalMob : Node3D
{
    public string MobId { get; set; } = Guid.NewGuid().ToString("N");
    public string MobType { get; set; } = "Chicken";

    private HexPlanet _planet = null!;
    private Vector3 _heading;
    private float _decisionTimer;
    private float _speed;
    private bool _streamed = true;
    private int _surfaceCell = -1;
    private int _currentChunk = -1;
    private float _movementAccumulator;
    private Node3D? _visual;
    private Transform3D _modelLocalTransform;
    private Transform3D _previousVisualTransform;
    private Transform3D _currentVisualTransform;

    public int CurrentChunk => _currentChunk;

    public void Initialize(HexPlanet planet, string type, string? id = null)
    {
        _planet = planet;
        MobType = type;
        if (!string.IsNullOrWhiteSpace(id)) MobId = id;
    }

    public override void _Ready()
    {
        if (_planet == null) _planet = GetNode<HexPlanet>("../../Planet");
        string path = MobType == "Cow"
            ? "res://Models/Mobs/PolyPizza/cow_quaternius.glb"
            : "res://Models/Mobs/PolyPizza/chicken_jeremy.glb";
        PackedScene? packedModel = GD.Load<PackedScene>(path);
        if (packedModel?.Instantiate<Node3D>() is Node3D visual)
        {
            visual.Name = "Model";
            // Source files use radically different units. These calibrated
            // scales produce a ~1.3 m cow and a ~1.0 m chicken. The former
            // gigantic values are archived in MOB_DIMENSIONS.json for bosses.
            float scale = MobType == "Cow" ? 0.2525f : 0.00529f;
            visual.Scale = Vector3.One * scale;
            // AI forward is -Z. The chicken source faces -X and the cow
            // source faces +Z, so each asset needs its own yaw correction.
            visual.RotationDegrees = MobType == "Cow"
                ? new Vector3(0f, 180f, 0f)
                : new Vector3(0f, -90f, 0f);
            visual.Position = Vector3.Up * (MobType == "Cow" ? 0.08f : 0.04f);
            AddChild(visual);
            _modelLocalTransform = visual.Transform;
            visual.TopLevel = true;
            _visual = visual;
            _currentVisualTransform = GlobalTransform * _modelLocalTransform;
            _previousVisualTransform = _currentVisualTransform;
        }
        _speed = MobType == "Cow" ? 0.55f : 0.8f;
        _movementAccumulator = (float)GD.RandRange(0.0, 0.1);
        PickHeading();
    }

    public override void _PhysicsProcess(double deltaValue)
    {
        if (!_streamed || !IsInstanceValid(_planet) || GlobalPosition.LengthSquared() < 1f) return;
        _movementAccumulator += (float)deltaValue;
        if (_movementAccumulator < 0.1f)
        {
            RenderInterpolatedModel();
            return;
        }
        float delta = _movementAccumulator;
        _movementAccumulator = 0f;
        _previousVisualTransform = _currentVisualTransform;
        _decisionTimer -= delta;
        if (_decisionTimer <= 0f) PickHeading();

        Vector3 up = GlobalPosition.Normalized();
        _heading = (_heading - up * _heading.Dot(up)).Normalized();
        Vector3 candidateDirection = (GlobalPosition + _heading * _speed * delta).Normalized();
        if (_planet.ResolvePassiveMobSurface(candidateDirection, ref _surfaceCell,
            out Vector3 surfacePosition, out int chunk))
        {
            GlobalPosition = surfacePosition;
            _currentChunk = chunk;
        }
        else
            PickHeading();

        up = GlobalPosition.Normalized();
        Vector3 forward = (_heading - up * _heading.Dot(up)).Normalized();
        if (forward.LengthSquared() > 0.01f)
            GlobalBasis = new Basis(forward.Cross(up).Normalized(), up, -forward).Orthonormalized();
        _currentVisualTransform = GlobalTransform * _modelLocalTransform;
        RenderInterpolatedModel();
    }

    public void PlaceAt(Vector3 position)
    {
        GlobalPosition = position;
        _planet.ResolvePassiveMobSurface(position.Normalized(), ref _surfaceCell,
            out _, out _currentChunk);
        _currentVisualTransform = GlobalTransform * _modelLocalTransform;
        _previousVisualTransform = _currentVisualTransform;
        RenderInterpolatedModel();
    }

    public void SetActivity(bool rendered, bool simulated)
    {
        _streamed = simulated;
        Visible = rendered;
        SetPhysicsProcess(simulated);
        if (simulated && _visual != null)
        {
            _currentVisualTransform = GlobalTransform * _modelLocalTransform;
            _previousVisualTransform = _currentVisualTransform;
            _visual.GlobalTransform = _currentVisualTransform;
        }
    }

    private void RenderInterpolatedModel()
    {
        if (_visual == null) return;
        float amount = GameSession.Current?.InterpolationEnabled == false
            ? 1f : Mathf.Clamp(_movementAccumulator / 0.1f, 0f, 1f);
        _visual.GlobalTransform = _previousVisualTransform.InterpolateWith(_currentVisualTransform, amount);
    }

    private void PickHeading()
    {
        Vector3 up = GlobalPosition.LengthSquared() > 1f ? GlobalPosition.Normalized() : Vector3.Up;
        Vector3 tangentA = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        Vector3 tangentB = up.Cross(tangentA).Normalized();
        float angle = (float)GD.RandRange(0.0, Mathf.Tau);
        _heading = tangentA * Mathf.Cos(angle) + tangentB * Mathf.Sin(angle);
        _decisionTimer = (float)GD.RandRange(2.5, 7.0);
    }

    public MobSaveData Capture()
    {
        Vector3 p = GlobalPosition;
        return new MobSaveData { Id = MobId, Type = MobType, Position = [p.X, p.Y, p.Z] };
    }
}
