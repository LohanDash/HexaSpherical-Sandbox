using Godot;
using System;

namespace HexaSphericalSandbox;

public partial class AnimalMob : Node3D
{
    private const float MovementTick = 0.05f;
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
    private AnimationPlayer? _animationPlayer;
    private float _walkPhase;
    private float _health;
    private float _damageReaction;
    private bool _dying;
    private Node3D? _leftChickenLeg;
    private Node3D? _rightChickenLeg;
    private Vector3 _lastKnownPosition;

    public int CurrentChunk => _currentChunk;
    public bool HasActiveWalkAnimation => MobType == "Cow"
        ? _animationPlayer?.IsPlaying() == true && _animationPlayer.CurrentAnimation.ToString().Contains("Walk")
        : _leftChickenLeg != null && _rightChickenLeg != null;

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
            _animationPlayer = visual.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
            PlayLocomotionAnimation();
            if (MobType == "Chicken") BuildChickenLegs();
            _currentVisualTransform = GlobalTransform * _modelLocalTransform;
            _previousVisualTransform = _currentVisualTransform;
        }
        AddCollisionBody();
        _speed = MobType == "Cow" ? 0.55f : 0.8f;
        _health = MobType == "Cow" ? 8f : 3f;
        _movementAccumulator = (float)GD.RandRange(0.0, 0.1);
        PickHeading();
    }

    public override void _PhysicsProcess(double deltaValue)
    {
        if (_dying || !_streamed || !IsInstanceValid(_planet) || GlobalPosition.LengthSquared() < 1f) return;
        _damageReaction = Mathf.Max(0f, _damageReaction - (float)deltaValue);
        _walkPhase += (float)deltaValue * (MobType == "Cow" ? 5.5f : 9f);
        AnimateChickenLegs();
        _movementAccumulator += (float)deltaValue;
        if (_movementAccumulator < MovementTick)
        {
            RenderInterpolatedModel();
            return;
        }
        float delta = _movementAccumulator;
        _movementAccumulator = 0f;
        if (_animationPlayer != null && MobType == "Cow")
            _animationPlayer.SpeedScale = Mathf.Clamp(_speed / 0.55f, 0.65f, 1.35f);
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
            _lastKnownPosition = GlobalPosition;
            _currentChunk = chunk;
        }
        else
            PickHeading();

        up = GlobalPosition.Normalized();
        Vector3 forward = (_heading - up * _heading.Dot(up)).Normalized();
        if (forward.LengthSquared() > 0.01f)
            GlobalBasis = new Basis(forward.Cross(up).Normalized(), up, -forward).Orthonormalized();
        _currentVisualTransform = GlobalTransform * AnimatedModelTransform();
        RenderInterpolatedModel();
    }

    public void PlaceAt(Vector3 position)
    {
        GlobalPosition = position;
        _lastKnownPosition = position;
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
            ? 1f : Mathf.Clamp(_movementAccumulator / MovementTick, 0f, 1f);
        _visual.GlobalTransform = _previousVisualTransform.InterpolateWith(_currentVisualTransform, amount);
    }

    private Transform3D AnimatedModelTransform()
    {
        float bob = Mathf.Sin(_walkPhase * 2f) * (MobType == "Cow" ? 0.018f : 0.032f);
        float sway = Mathf.Sin(_walkPhase) * (MobType == "Cow" ? 0.018f : 0.045f);
        Transform3D animated = _modelLocalTransform;
        animated.Origin += Vector3.Up * bob;
        animated.Basis = new Basis(Vector3.Forward, sway) * animated.Basis;
        if (_damageReaction > 0f)
        {
            float kick = _damageReaction / 0.18f;
            animated.Origin += Vector3.Back * kick * 0.16f;
            animated.Basis = animated.Basis.Scaled(Vector3.One * (1f + Mathf.Sin(kick * Mathf.Pi) * 0.08f));
        }
        return animated;
    }

    private void PlayLocomotionAnimation()
    {
        if (_animationPlayer == null) return;
        string[] preferred = ["Armature|Walk", "Armature|WalkSlow", "Walk", "WalkSlow", "Idle"];
        foreach (string animation in preferred)
        {
            if (!_animationPlayer.HasAnimation(animation)) continue;
            _animationPlayer.Play(animation);
            _animationPlayer.SpeedScale = MobType == "Cow" ? 1.15f : 1f;
            return;
        }
    }

    public bool TakeDamage(float amount)
    {
        if (_dying) return false;
        _health -= amount;
        _damageReaction = 0.18f;
        SoundManager.Play(SoundKind.Hit, -12f);
        SoundManager.Play(MobType == "Cow" ? SoundKind.Cow : SoundKind.Chicken, -8f);
        return _health <= 0f;
    }

    public async void BeginDeath()
    {
        if (_dying) return;
        _dying = true;
        SetPhysicsProcess(false);
        if (GetNodeOrNull<StaticBody3D>("AnimalCollision") is { } collision) collision.ProcessMode = ProcessModeEnum.Disabled;
        SoundManager.Play(SoundKind.MonsterDeath, -15f);
        if (_visual != null)
        {
            Tween tween = CreateTween().SetParallel();
            tween.TweenProperty(_visual, "rotation:z", _visual.Rotation.Z + 1.35f, 0.48f)
                .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
            tween.TweenProperty(_visual, "scale", _visual.Scale * 0.08f, 0.58f)
                .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Back);
        }
        await ToSignal(GetTree().CreateTimer(0.62f), SceneTreeTimer.SignalName.Timeout);
        QueueFree();
    }

    private void BuildChickenLegs()
    {
        StandardMaterial3D material = new() { AlbedoColor = new Color(0.55f, 0.26f, 0.05f), Roughness = 0.9f };
        _leftChickenLeg = BuildLeg("ProceduralLeftLeg", -0.13f, material);
        _rightChickenLeg = BuildLeg("ProceduralRightLeg", 0.13f, material);
    }

    private Node3D BuildLeg(string name, float x, Material material)
    {
        var pivot = new Node3D { Name = name, Position = new Vector3(x, 0.34f, 0.02f) };
        AddChild(pivot);
        pivot.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.055f, 0.28f, 0.055f), Material = material },
            Position = Vector3.Down * 0.14f
        });
        return pivot;
    }

    private void AnimateChickenLegs()
    {
        if (_leftChickenLeg == null || _rightChickenLeg == null) return;
        float step = Mathf.Sin(_walkPhase) * 0.72f;
        _leftChickenLeg.Rotation = new Vector3(step, 0f, 0f);
        _rightChickenLeg.Rotation = new Vector3(-step, 0f, 0f);
    }

    private void AddCollisionBody()
    {
        float radius = MobType == "Cow" ? 0.46f : 0.25f;
        float height = MobType == "Cow" ? 1.25f : 0.72f;
        var body = new StaticBody3D { Name = "AnimalCollision" };
        body.AddChild(new CollisionShape3D
        {
            Name = "BodyShape",
            Shape = new CapsuleShape3D { Radius = radius, Height = height },
            Position = Vector3.Up * (height * 0.5f)
        });
        AddChild(body);
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
        Vector3 p = IsInsideTree() ? GlobalPosition : _lastKnownPosition;
        return new MobSaveData { Id = MobId, Type = MobType, Position = [p.X, p.Y, p.Z] };
    }
}
