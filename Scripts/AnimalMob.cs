using Godot;
using System;
using System.Collections.Generic;

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
    private bool _embeddedChickenLegsRemoved;
    private Vector3 _lastKnownPosition;
    private bool _sheared;
    private float _woolRegrowSeconds;

    public int CurrentChunk => _currentChunk;
    public bool IsDying => _dying;
    public bool HasActiveWalkAnimation => MobType is "Cow" or "Sheep"
        ? _animationPlayer?.IsPlaying() == true && _animationPlayer.CurrentAnimation.ToString().Contains("Walk")
        : _leftChickenLeg != null && _rightChickenLeg != null;
    public int ProceduralLegCount => (_leftChickenLeg != null ? 1 : 0) + (_rightChickenLeg != null ? 1 : 0);
    public bool HasExactlyOneVisibleLegSet => MobType is "Cow" or "Sheep"
        ? ProceduralLegCount == 0 && HasActiveWalkAnimation
        : _embeddedChickenLegsRemoved && ProceduralLegCount == 2;
    public string LocomotionDebug => $"{MobType}: player={_animationPlayer != null}, playing={_animationPlayer?.IsPlaying()}, current={_animationPlayer?.CurrentAnimation}, animations={(_animationPlayer == null ? "none" : string.Join(',', _animationPlayer.GetAnimationList()))}";

    public void Initialize(HexPlanet planet, string type, string? id = null,
        bool sheared = false, float woolRegrowSeconds = 0f)
    {
        _planet = planet;
        MobType = type;
        if (!string.IsNullOrWhiteSpace(id)) MobId = id;
        _sheared = type == "Sheep" && sheared;
        _woolRegrowSeconds = Mathf.Max(0f, woolRegrowSeconds);
    }

    public override void _Ready()
    {
        if (_planet == null) _planet = GetNode<HexPlanet>("../../Planet");
        string path = MobType switch
        {
            "Cow" => "res://Models/Mobs/PolyPizza/cow_quaternius.glb",
            "Sheep" => "res://Models/Mobs/PolyPizza/sheep_quaternius.glb",
            _ => "res://Models/Mobs/PolyPizza/chicken_jeremy.glb"
        };
        PackedScene? packedModel = GD.Load<PackedScene>(path);
        if (packedModel?.Instantiate<Node3D>() is Node3D visual)
        {
            visual.Name = "Model";
            // Source files use radically different units. These calibrated
            // scales produce a ~1.3 m cow and a ~1.0 m chicken. The former
            // gigantic values are archived in MOB_DIMENSIONS.json for bosses.
            float scale = MobType is "Cow" or "Sheep" ? 0.2525f : 0.00529f;
            visual.Scale = Vector3.One * scale;
            // AI forward is -Z. The chicken source faces -X and the cow
            // source faces +Z, so each asset needs its own yaw correction.
            visual.RotationDegrees = MobType is "Cow" or "Sheep"
                ? new Vector3(0f, 180f, 0f)
                : new Vector3(0f, -90f, 0f);
            visual.Position = Vector3.Up * (MobType is "Cow" or "Sheep" ? 0.08f : 0.04f);
            if (MobType == "Chicken")
                _embeddedChickenLegsRemoved = RemoveEmbeddedChickenLegGeometry(visual);
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
        _speed = MobType switch { "Cow" => 0.55f, "Sheep" => 0.62f, _ => 0.8f };
        _health = MobType switch { "Cow" => 8f, "Sheep" => 6f, _ => 3f };
        _movementAccumulator = (float)GD.RandRange(0.0, 0.1);
        PickHeading();
    }

    public override void _PhysicsProcess(double deltaValue)
    {
        if (_dying || !_streamed || !IsInstanceValid(_planet) || GlobalPosition.LengthSquared() < 1f) return;
        if (_sheared && (_woolRegrowSeconds -= (float)deltaValue) <= 0f)
        {
            _sheared = false;
            _woolRegrowSeconds = 0f;
        }
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
        if (_animationPlayer != null && MobType is "Cow" or "Sheep")
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
            StartLoopingAnimation(animation);
            return;
        }
        // Some glTF exporters prefix the armature name more than once. Match
        // the semantic suffix instead of coupling gameplay to that hierarchy.
        foreach (StringName animation in _animationPlayer.GetAnimationList())
        {
            string name = animation.ToString();
            if (!name.EndsWith("|Walk", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith("|WalkSlow", StringComparison.OrdinalIgnoreCase)) continue;
            StartLoopingAnimation(animation);
            return;
        }
    }

    private void StartLoopingAnimation(StringName animation)
    {
        if (_animationPlayer?.GetAnimation(animation) is Animation clip)
            clip.LoopMode = Animation.LoopModeEnum.Linear;
        _animationPlayer?.Play(animation);
        if (_animationPlayer != null)
            _animationPlayer.SpeedScale = MobType is "Cow" or "Sheep" ? 1.15f : 1f;
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

    public bool TryShear(out Vector3 dropPosition)
    {
        dropPosition = GlobalPosition + GlobalPosition.Normalized() * 0.45f;
        if (MobType != "Sheep" || _dying || _sheared) return false;
        _sheared = true;
        _woolRegrowSeconds = 300f;
        _damageReaction = 0.18f;
        SoundManager.Play(SoundKind.Craft, -11f);
        return true;
    }

    public async void BeginDeath()
    {
        if (_dying) return;
        _dying = true;
        SetPhysicsProcess(false);
        SetProcess(false);
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
            Name = "Shin",
            Mesh = new CylinderMesh
            {
                TopRadius = 0.025f, BottomRadius = 0.035f, Height = 0.25f,
                RadialSegments = 5, Rings = 1, Material = material
            },
            Position = Vector3.Down * 0.125f
        });
        pivot.AddChild(new MeshInstance3D
        {
            Name = "Foot",
            Mesh = new BoxMesh { Size = new Vector3(0.065f, 0.035f, 0.14f), Material = material },
            Position = new Vector3(0f, -0.255f, -0.04f)
        });
        return pivot;
    }

    private static bool RemoveEmbeddedChickenLegGeometry(Node3D visual)
    {
        bool removedAny = false;
        foreach (Node node in visual.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is not MeshInstance3D meshInstance || meshInstance.Mesh is not Mesh source) continue;
            var cleaned = new ArrayMesh();
            for (int surface = 0; surface < source.GetSurfaceCount(); surface++)
            {
                Godot.Collections.Array arrays = source.SurfaceGetArrays(surface);
                Vector3[] vertices = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
                int[] indices = (int[])arrays[(int)Mesh.ArrayType.Index];
                if (indices.Length == 0)
                {
                    cleaned.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                    cleaned.SurfaceSetMaterial(cleaned.GetSurfaceCount() - 1, source.SurfaceGetMaterial(surface));
                    continue;
                }

                var kept = new List<int>(indices.Length);
                for (int index = 0; index + 2 < indices.Length; index += 3)
                {
                    int a = indices[index], b = indices[index + 1], c = indices[index + 2];
                    // This asset is a single unrigged mesh. Inspection shows a
                    // clean gap between its fused legs (Y <= 56) and body
                    // (Y >= 110), so the threshold removes whole leg triangles
                    // without cutting into the body.
                    bool embeddedLeg = vertices[a].Y < 70f && vertices[b].Y < 70f && vertices[c].Y < 70f;
                    if (embeddedLeg) { removedAny = true; continue; }
                    kept.Add(a); kept.Add(b); kept.Add(c);
                }
                if (kept.Count == 0) continue;
                arrays[(int)Mesh.ArrayType.Index] = kept.ToArray();
                cleaned.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                cleaned.SurfaceSetMaterial(cleaned.GetSurfaceCount() - 1, source.SurfaceGetMaterial(surface));
            }
            meshInstance.Mesh = cleaned;
        }
        return removedAny;
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
        float radius = MobType switch { "Cow" => 0.46f, "Sheep" => 0.4f, _ => 0.25f };
        float height = MobType switch { "Cow" => 1.25f, "Sheep" => 1.05f, _ => 0.72f };
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
        return new MobSaveData { Id = MobId, Type = MobType, Position = [p.X, p.Y, p.Z],
            Sheared = _sheared, WoolRegrowSeconds = _woolRegrowSeconds };
    }
}
