using Godot;
using System.Collections.Generic;
using System.Linq;

namespace HexaSphericalSandbox;

public partial class NightMonsterManager : Node3D
{
    private const float SimulationStep = 0.1f;
    private const int MaximumSpawnEggMonsters = 48;
    public int EggMonsterCount => _monsters.Count(monster => monster.FromEgg);
    public int MonsterCount => _monsters.Count;
    public int MonsterSceneNodeCount => GetChildren().Count(child => child is AnimatableBody3D);
    public Vector3 FirstMonsterAimPosition => _monsters.Count == 0
        ? Vector3.Zero
        : _monsters[0].Body.GlobalPosition + _monsters[0].Body.GlobalPosition.Normalized() * (_monsters[0].Brute ? 0.8f : 0.42f);

    private sealed class Monster
    {
        public required AnimatableBody3D Body;
        public required Node3D Model;
        public readonly List<Node3D> Legs = [];
        public readonly List<Node3D> Arms = [];
        public Vector3 PreviousPosition;
        public Vector3 TargetPosition;
        public Vector3 Forward = Vector3.Forward;
        public int Cell = -1;
        public float Speed;
        public float Damage;
        public float Cooldown;
        public float Health;
        public float HitRadius;
        public float WalkPhase;
        public float AttackReaction;
        public float HitReaction;
        public int Chunk = -1;
        public bool Brute;
        public bool FromEgg;
    }

    private readonly List<Monster> _monsters = [];
    private HexPlanet _planet = null!;
    private SphericalPlayer _player = null!;
    private Main _main = null!;
    private float _simulationAccumulator;
    private float _spawnTimer = 2f;

    public override void _Ready()
    {
        _planet = GetNode<HexPlanet>("../Planet");
        _player = GetNode<SphericalPlayer>("../Player");
        _main = GetNode<Main>("..");
    }

    public override void _Process(double deltaValue)
    {
        float delta = (float)deltaValue;
        _spawnTimer -= delta;
        bool active = !GameSession.IsCreative && _main.Daylight < 0.18f;
        if (!active) ClearNaturalMonsters();
        else if (_spawnTimer <= 0f && _monsters.Count < 6)
        {
            _spawnTimer = 4f;
            Spawn();
        }

        _simulationAccumulator += delta;
        while (_simulationAccumulator >= SimulationStep)
        {
            _simulationAccumulator -= SimulationStep;
            SimulateMonsters(SimulationStep);
        }

        float interpolation = Mathf.Clamp(_simulationAccumulator / SimulationStep, 0f, 1f);
        foreach (Monster monster in _monsters)
            RenderMonster(monster, interpolation, delta);
    }

    private void SimulateMonsters(float delta)
    {
        foreach (Monster monster in _monsters)
        {
            monster.Cooldown -= delta;
            monster.AttackReaction = Mathf.Max(0f, monster.AttackReaction - delta * 2.8f);
            monster.HitReaction = Mathf.Max(0f, monster.HitReaction - delta * 5.5f);
            monster.PreviousPosition = monster.TargetPosition;
            Vector3 up = monster.TargetPosition.Normalized();
            Vector3 toward = _player.GlobalPosition - monster.TargetPosition;
            Vector3 tangent = toward - up * toward.Dot(up);
            if (tangent.LengthSquared() > 0.0001f)
            {
                tangent = tangent.Normalized();
                monster.Forward = tangent;
                Vector3 direction = (monster.TargetPosition + tangent * monster.Speed * delta).Normalized();
                float bodyHeight = monster.Brute ? 1.55f : 0.72f;
                if (_planet.ResolveMobSurfaceNear(direction, monster.TargetPosition.Length(), bodyHeight,
                    ref monster.Cell, out Vector3 surface, out _))
                {
                    monster.TargetPosition = surface + direction * 0.06f;
                    monster.Chunk = _planet.ChunkAt(direction);
                }
            }

            if (monster.TargetPosition.DistanceTo(_player.GlobalPosition) < 1.35f && monster.Cooldown <= 0f)
            {
                _player.ApplyDamage(monster.Damage);
                monster.Cooldown = 1.1f;
                monster.AttackReaction = 1f;
                SoundManager.Play(SoundKind.Monster, -16f);
            }
        }
    }

    private static void RenderMonster(Monster monster, float interpolation, float delta)
    {
        Vector3 position = monster.PreviousPosition.Lerp(monster.TargetPosition, interpolation);
        Vector3 up = position.Normalized();
        Vector3 forward = monster.Forward - up * monster.Forward.Dot(up);
        if (forward.LengthSquared() < 0.001f) forward = up.Cross(Vector3.Right);
        forward = forward.Normalized();
        Vector3 right = forward.Cross(up).Normalized();
        monster.Body.GlobalTransform = new Transform3D(new Basis(right, up, -forward).Orthonormalized(), position);

        float movement = monster.PreviousPosition.DistanceTo(monster.TargetPosition);
        monster.WalkPhase += delta * (monster.Brute ? 5.2f : 6.5f) * Mathf.Clamp(movement / 0.08f, 0.25f, 1f);
        for (int i = 0; i < monster.Legs.Count; i++)
            monster.Legs[i].Rotation = new Vector3(Mathf.Sin(monster.WalkPhase + i * Mathf.Pi) * 0.52f, 0f, 0f);
        for (int i = 0; i < monster.Arms.Count; i++)
            monster.Arms[i].Rotation = new Vector3(-monster.AttackReaction * 1.45f + Mathf.Sin(monster.WalkPhase + (i + 1) * Mathf.Pi) * 0.42f, 0f, 0f);
        monster.Model.Position = Vector3.Up * (Mathf.Sin(monster.WalkPhase * 2f) * 0.025f)
            + Vector3.Back * monster.HitReaction * 0.14f
            + Vector3.Forward * monster.AttackReaction * (monster.Brute ? 0.08f : 0.24f);
        monster.Model.Scale = Vector3.One * (1f + Mathf.Sin(monster.HitReaction * Mathf.Pi) * 0.1f);
    }

    private void Spawn()
    {
        Vector3 up = _player.GlobalPosition.Normalized();
        Vector3 a = up.Cross(Vector3.Right).Normalized();
        if (a.LengthSquared() < 0.1f) a = up.Cross(Vector3.Forward).Normalized();
        Vector3 b = up.Cross(a).Normalized();
        float angle = (float)GD.RandRange(0, Mathf.Tau);
        float distance = (float)GD.RandRange(11, 18);
        Vector3 direction = (up + (a * Mathf.Cos(angle) + b * Mathf.Sin(angle)) * distance / _planet.Radius).Normalized();
        CreateMonster(direction, GD.Randf() < 0.35f, false);
    }

    public bool SpawnEgg(string type, Vector3 direction)
    {
        if (_monsters.Count >= MaximumSpawnEggMonsters) return false;
        CreateMonster(direction, type.Contains("Brute"), true);
        return true;
    }

    public bool SpawnEggAt(string type, Vector3 position)
    {
        if (_monsters.Count >= MaximumSpawnEggMonsters) return false;
        CreateMonster(position.Normalized(), type.Contains("Brute"), true, position);
        return true;
    }

    public bool SpawnNaturalForValidation(Vector3 direction)
    {
        int chunk = _planet.ChunkAt(direction);
        if (CountInChunk(chunk) >= 3) return false;
        CreateMonster(direction, false, false);
        return true;
    }

    public bool TryHit(Vector3 origin, Vector3 direction)
    {
        direction = direction.Normalized();
        Monster? target = null;
        float nearest = 5.2f;
        foreach (Monster monster in _monsters)
        {
            if (!IsInstanceValid(monster.Body)) continue;
            Vector3 up = monster.Body.GlobalPosition.Normalized();
            Vector3 toMonster = monster.Body.GlobalPosition + up * (monster.Brute ? 0.8f : 0.42f) - origin;
            float along = toMonster.Dot(direction);
            if (along < 0f || along >= nearest || (toMonster - direction * along).Length() > monster.HitRadius) continue;
            nearest = along;
            target = monster;
        }
        if (target == null) return false;

        target.Health -= GameSession.IsCreative ? 100f : 1f;
        target.HitReaction = 1f;
        SoundManager.Play(SoundKind.Hit, -11f);
        if (target.Health > 0f) return true;
        AnimateDeath(target);
        _monsters.Remove(target);
        return true;
    }

    private void CreateMonster(Vector3 direction, bool brute, bool fromEgg, Vector3? exactPosition = null)
    {
        int spawnChunk = _planet.ChunkAt(direction);
        if (!fromEgg && CountInChunk(spawnChunk) >= 3) return;
        var body = new AnimatableBody3D { Name = brute ? "NightBrute" : "NightCrawler" };
        Node3D model = brute ? BuildBruteModel() : BuildCrawlerModel();
        body.AddChild(model);
        body.AddChild(new CollisionShape3D
        {
            Name = "BodyShape",
            Shape = new CapsuleShape3D { Radius = brute ? 0.47f : 0.4f, Height = brute ? 1.55f : 0.72f },
            Position = Vector3.Up * (brute ? 0.76f : 0.38f)
        });
        AddChild(body);
        Vector3 position = exactPosition ?? _planet.PassiveMobSurfacePosition(direction, 0.06f);
        body.GlobalPosition = position;

        var monster = new Monster
        {
            Body = body,
            Model = model,
            PreviousPosition = position,
            TargetPosition = position,
            Brute = brute,
            // Slower than the old 3.7 / 2.1 values; movement stays dangerous
            // without looking like the creature is skating over the terrain.
            Speed = brute ? 1.25f : 1.75f,
            Damage = brute ? 24f : 12f,
            Health = brute ? 10f : 5f,
            HitRadius = brute ? 0.72f : 0.62f,
            Chunk = spawnChunk,
            FromEgg = fromEgg
        };
        CollectAnimatedParts(model, monster);
        _monsters.Add(monster);
    }

    private int CountInChunk(int chunk)
        => chunk < 0 ? 0 : _monsters.Count(monster => monster.Chunk == chunk && IsInstanceValid(monster.Body));

    public int MaximumCountInAnyChunk()
        => _monsters.GroupBy(monster => monster.Chunk).Select(group => group.Count()).DefaultIfEmpty(0).Max();

    private static void AnimateDeath(Monster monster)
    {
        monster.Body.CollisionLayer = 0;
        monster.Body.CollisionMask = 0;
        if (monster.Body.GetNodeOrNull<CollisionShape3D>("BodyShape") is { } shape)
            shape.SetDeferred(CollisionShape3D.PropertyName.Disabled, true);
        SoundManager.Play(SoundKind.MonsterDeath, -10f);
        Tween tween = monster.Body.CreateTween().SetParallel();
        tween.TweenProperty(monster.Model, "rotation:z", monster.Model.Rotation.Z + 1.5f, 0.42f)
            .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
        tween.TweenProperty(monster.Model, "scale", Vector3.One * 0.02f, 0.58f)
            .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Back);
        tween.Chain().TweenCallback(Callable.From(monster.Body.QueueFree));
    }

    private static Node3D BuildCrawlerModel()
    {
        var root = new Node3D { Name = "CrawlerModel" };
        StandardMaterial3D shell = Material(new Color(0.075f, 0.22f, 0.1f));
        StandardMaterial3D underside = Material(new Color(0.025f, 0.055f, 0.03f));
        StandardMaterial3D eye = Material(new Color(0.8f, 1f, 0.25f), new Color(0.25f, 0.8f, 0.05f));
        AddBox(root, "Thorax", new Vector3(0.68f, 0.3f, 0.82f), new Vector3(0f, 0.36f, 0.05f), shell);
        AddBox(root, "Head", new Vector3(0.58f, 0.28f, 0.44f), new Vector3(0f, 0.34f, -0.56f), shell);
        AddBox(root, "Abdomen", new Vector3(0.78f, 0.38f, 0.62f), new Vector3(0f, 0.39f, 0.66f), underside);
        AddBox(root, "LeftEye", new Vector3(0.09f, 0.08f, 0.05f), new Vector3(-0.17f, 0.42f, -0.79f), eye);
        AddBox(root, "RightEye", new Vector3(0.09f, 0.08f, 0.05f), new Vector3(0.17f, 0.42f, -0.79f), eye);
        for (int side = -1; side <= 1; side += 2)
        for (int pair = 0; pair < 3; pair++)
        {
            var leg = new Node3D { Name = $"Leg_{side}_{pair}", Position = new Vector3(side * 0.3f, 0.34f, -0.28f + pair * 0.3f) };
            root.AddChild(leg);
            MeshInstance3D segment = AddBox(leg, "Segment", new Vector3(0.13f, 0.13f, 0.68f), new Vector3(side * 0.26f, -0.15f, 0f), underside);
            segment.Rotation = new Vector3(0f, 0f, side * 0.72f);
        }
        return root;
    }

    private static Node3D BuildBruteModel()
    {
        var root = new Node3D { Name = "BruteModel" };
        StandardMaterial3D skin = Material(new Color(0.3f, 0.045f, 0.055f));
        StandardMaterial3D armour = Material(new Color(0.095f, 0.075f, 0.09f));
        StandardMaterial3D eye = Material(new Color(1f, 0.16f, 0.06f), new Color(0.7f, 0.025f, 0f));
        AddBox(root, "Torso", new Vector3(0.9f, 0.8f, 0.55f), new Vector3(0f, 0.98f, 0f), armour);
        AddBox(root, "Head", new Vector3(0.62f, 0.52f, 0.52f), new Vector3(0f, 1.62f, -0.04f), skin);
        AddBox(root, "LeftEye", new Vector3(0.1f, 0.08f, 0.05f), new Vector3(-0.17f, 1.68f, -0.32f), eye);
        AddBox(root, "RightEye", new Vector3(0.1f, 0.08f, 0.05f), new Vector3(0.17f, 1.68f, -0.32f), eye);
        for (int side = -1; side <= 1; side += 2)
        {
            var leg = new Node3D { Name = $"Leg_{side}", Position = new Vector3(side * 0.25f, 0.64f, 0f) };
            root.AddChild(leg);
            AddBox(leg, "Limb", new Vector3(0.27f, 0.7f, 0.3f), new Vector3(0f, -0.34f, 0f), skin);
            var arm = new Node3D { Name = $"Arm_{side}", Position = new Vector3(side * 0.56f, 1.25f, 0f) };
            root.AddChild(arm);
            AddBox(arm, "Limb", new Vector3(0.3f, 0.82f, 0.32f), new Vector3(0f, -0.34f, 0f), skin);
            MeshInstance3D horn = AddBox(root, $"Horn_{side}", new Vector3(0.15f, 0.42f, 0.15f), new Vector3(side * 0.24f, 1.98f, 0f), armour);
            horn.Rotation = new Vector3(0f, 0f, side * -0.38f);
        }
        return root;
    }

    private static void CollectAnimatedParts(Node3D model, Monster monster)
    {
        foreach (Node child in model.GetChildren())
        {
            if (child is not Node3D part) continue;
            if (part.Name.ToString().StartsWith("Leg_")) monster.Legs.Add(part);
            else if (part.Name.ToString().StartsWith("Arm_")) monster.Arms.Add(part);
        }
    }

    private static MeshInstance3D AddBox(Node parent, string name, Vector3 size, Vector3 position, Material material)
    {
        var mesh = new MeshInstance3D
        {
            Name = name,
            Mesh = new BoxMesh { Size = size, Material = material },
            Position = position
        };
        parent.AddChild(mesh);
        return mesh;
    }

    private static StandardMaterial3D Material(Color colour, Color? emission = null) => new()
    {
        AlbedoColor = colour,
        Roughness = 0.9f,
        EmissionEnabled = emission.HasValue,
        Emission = emission ?? Colors.Black,
        EmissionEnergyMultiplier = emission.HasValue ? 1.6f : 1f
    };

    private void ClearNaturalMonsters()
    {
        for (int i = _monsters.Count - 1; i >= 0; i--)
        {
            if (_monsters[i].FromEgg) continue;
            _monsters[i].Body.QueueFree();
            _monsters.RemoveAt(i);
        }
    }
}
