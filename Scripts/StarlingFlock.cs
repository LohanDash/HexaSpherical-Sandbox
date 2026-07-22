using Godot;
using System;

namespace HexaSphericalSandbox;

public partial class StarlingFlock : Node3D
{
    // Keep one clearly visible murmuration around the player. One hundred birds
    // remain inexpensive through MultiMesh and leave room for nest offspring.
    private const int MaxBirdCount = 120;
    private const int InitialBirdCount = 100;
    private const int NeighbourCount = 7;
    private const float TickInterval = 0.05f;
    private const float CruiseAltitude = 30f;
    private readonly Vector3[] _positions = new Vector3[MaxBirdCount];
    private readonly Vector3[] _previousPositions = new Vector3[MaxBirdCount];
    private readonly Vector3[] _velocities = new Vector3[MaxBirdCount];
    private readonly Vector3[] _nextVelocities = new Vector3[MaxBirdCount];
    private MultiMesh _multiMesh = null!;
    private Node3D _player = null!;
    private HexPlanet _planet = null!;
    private Main _main = null!;
    private NatureSystem _nature = null!;
    private Node3D _hawk = null!;
    private Vector3 _hawkPosition;
    private Vector3 _previousHawkPosition;
    private Vector3 _hawkVelocity;
    private float _hawkCycle;
    private float _eatCooldown;
    private Vector3 _roostDirection = new Vector3(0.62f, 0.48f, -0.62f).Normalized();
    private float _migrationClock;
    private float _migrationDuration;
    private int _birdsAway;
    private float _accumulator;
    private float _flockTime;
    private readonly float[] _nearestDistances = new float[NeighbourCount];
    private readonly int[] _nearest = new int[NeighbourCount];
    private int _activeBirdCount = InitialBirdCount;
    private int _nestTarget = -1;
    private float _nestBuildTime;
    private int _hawkTargetBird = -1;
    public int BirdCount => _activeBirdCount;

    public float MinimumTerrainClearance()
    {
        float minimum = float.MaxValue;
        for (int bird = 0; bird < _activeBirdCount; bird++)
        {
            Vector3 direction = _positions[bird].Normalized();
            minimum = Math.Min(minimum, _positions[bird].Length() - _planet.SurfaceRadius(direction));
        }
        return minimum;
    }

    public override void _Ready()
    {
        _player = GetNode<Node3D>("../Player");
        _planet = GetNode<HexPlanet>("../Planet");
        _main = GetNode<Main>("..");
        _nature = GetNode<NatureSystem>("../NatureSystem");
        _activeBirdCount = Mathf.Clamp(GameSession.Current?.StarlingCount ?? InitialBirdCount, InitialBirdCount, MaxBirdCount);
        _planet.VoxelEdited += OnTerrainEdited;
        _multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            InstanceCount = MaxBirdCount,
            Mesh = CreateBirdMesh()
        };
        AddChild(new MultiMeshInstance3D
        {
            Name = "Murmuration",
            Multimesh = _multiMesh,
            CustomAabb = new Aabb(new Vector3(-700f, -700f, -700f), new Vector3(1400f, 1400f, 1400f))
        });
        PackedScene hawkScene = GD.Load<PackedScene>("res://Models/Mobs/PolyPizza/hawk_sherkiz.glb");
        _hawk = hawkScene.Instantiate<Node3D>();
        _hawk.Name = "AerialPredator";
        _hawk.Scale = Vector3.One * 0.35f;
        AddChild(_hawk);
        if (_hawk.FindChild("AnimationPlayer", true, false) is AnimationPlayer animationPlayer)
        {
            animationPlayer.Play("metarig|Fly");
            animationPlayer.SpeedScale = 1.35f;
        }

        Vector3 up = _player.GlobalPosition.Normalized();
        Vector3 tangentA = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        Vector3 tangentB = up.Cross(tangentA).Normalized();
        Vector3 centre = up * (_planet.SurfaceRadius(up) + CruiseAltitude);
        for (int i = 0; i < _activeBirdCount; i++)
        {
            float angle = (float)GD.RandRange(0.0, Mathf.Tau);
            float radius = Mathf.Sqrt(GD.Randf()) * 7f;
            float height = (float)GD.RandRange(-2.2, 2.2);
            _positions[i] = centre + tangentA * Mathf.Cos(angle) * radius
                + tangentB * Mathf.Sin(angle) * radius + up * height;
            _velocities[i] = (tangentA + tangentB * (float)GD.RandRange(-0.35, 0.35)).Normalized() * 5.5f;
            _previousPositions[i] = _positions[i];
        }
        _hawkPosition = centre + tangentB * 24f + up * 5f;
        _previousHawkPosition = _hawkPosition;
        _hawkVelocity = -tangentB * 8f;
        UpdateTransforms();
    }

    public override void _Process(double deltaValue)
    {
        _accumulator += Math.Min((float)deltaValue, 0.15f);
        int catchUpSteps = 0;
        while (_accumulator >= TickInterval && catchUpSteps++ < 3)
        {
            Array.Copy(_positions, _previousPositions, MaxBirdCount);
            _previousHawkPosition = _hawkPosition;
            _accumulator -= TickInterval;
            _flockTime += TickInterval;
            UpdateMigration(TickInterval);
            _eatCooldown = Math.Max(0f, _eatCooldown - TickInterval);
            Simulate(TickInterval);
        }
        if (catchUpSteps >= 3) _accumulator = Math.Min(_accumulator, TickInterval);
        float interpolation = GameSession.Current?.InterpolationEnabled == false
            ? 1f : Mathf.Clamp(_accumulator / TickInterval, 0f, 1f);
        UpdateTransforms(interpolation);
    }

    private void Simulate(float delta)
    {
        Vector3 playerUp = _player.GlobalPosition.Normalized();
        Vector3 flockCentre = Vector3.Zero;
        for (int bird = 0; bird < _activeBirdCount; bird++) flockCentre += _positions[bird];
        flockCentre /= _activeBirdCount;

        Vector3 orbitTangent = playerUp.Cross(Vector3.Up);
        if (orbitTangent.LengthSquared() < 0.01f) orbitTangent = playerUp.Cross(Vector3.Right);
        orbitTangent = orbitTangent.Normalized();
        Vector3 dayCentre = playerUp * (_planet.SurfaceRadius(playerUp) + CruiseAltitude)
            + orbitTangent * Mathf.Sin(_flockTime * 0.22f) * 5f;
        Vector3 roostCentre = _roostDirection * (_planet.SurfaceRadius(_roostDirection) + 2.5f);
        WorldData? world = GameSession.Current;
        if (world != null)
        {
            foreach (float[] danger in world.BirdDangerZones)
            {
                if (danger.Length != 3) continue;
                Vector3 dangerDirection = new(danger[0], danger[1], danger[2]);
                float proximity = Mathf.Max(0f, _roostDirection.Dot(dangerDirection) - 0.88f);
                roostCentre += (_roostDirection - dangerDirection).Normalized() * proximity * 18f;
            }
        }
        float roosting = 1f - Mathf.SmoothStep(0.12f, 0.38f, _main.Daylight);
        if (_nestTarget < 0 || !_nature.IsAvailable(_nestTarget))
            _nestTarget = _nature.FindNearestAvailableTree(flockCentre, 180f);
        Vector3 nestDestination = _nestTarget >= 0 ? _nature.NestPosition(_nestTarget) + _nature.NestPosition(_nestTarget).Normalized() * 1.2f : dayCentre;
        Vector3 desiredCentre = roosting > 0.7f ? roostCentre : (_nestTarget >= 0 ? nestDestination : dayCentre);

        _hawkCycle += delta;
        // Predation is an occasional event, not the permanent state of the
        // flock. This gives the birds long calm windows to regroup and nest.
        bool hunting = Mathf.PosMod(_hawkCycle, 68f) < 7f && _main.Daylight > 0.18f;
        int attackedNest = hunting ? _nature.FindNearestNest(_hawkPosition) : -1;
        if (hunting && attackedNest < 0 && (_hawkTargetBird < 0 || _hawkTargetBird >= _activeBirdCount))
            _hawkTargetBird = NearestBirdTo(_hawkPosition);
        if (!hunting) _hawkTargetBird = -1;
        Vector3 hawkTarget = attackedNest >= 0 ? _nature.NestPosition(attackedNest)
            : hunting && _hawkTargetBird >= 0 ? _positions[_hawkTargetBird]
            : playerUp * (_planet.SurfaceRadius(playerUp) + CruiseAltitude + 8f) - orbitTangent * 30f;
        Vector3 hawkUpNow = _hawkPosition.Normalized();
        Vector3 hawkDisplacement = hawkTarget - _hawkPosition;
        Vector3 hawkTangent = hawkDisplacement - hawkUpNow * hawkDisplacement.Dot(hawkUpNow);
        float hawkMinimumRadius = _planet.SurfaceRadius(hawkUpNow) + (attackedNest >= 0 ? 4f : CruiseAltitude);
        Vector3 hawkRadialCorrection = hawkUpNow * (hawkMinimumRadius - _hawkPosition.Length()) * 1.8f;
        Vector3 hawkDesired = (hawkTangent + hawkRadialCorrection).Normalized() * (hunting ? 9.5f : 7f);
        _hawkVelocity = _hawkVelocity.MoveToward(hawkDesired, 4.2f * delta);
        _hawkPosition += _hawkVelocity * delta;
        ClampAboveTerrain(ref _hawkPosition, ref _hawkVelocity, attackedNest >= 0 ? 3.2f : CruiseAltitude);
        if (attackedNest >= 0 && _hawkPosition.DistanceTo(_nature.NestPosition(attackedNest)) < 1.1f)
            _nature.DestroyNest(attackedNest);

        int caughtBird = -1;
        for (int bird = 0; bird < _activeBirdCount; bird++)
        {
            if (bird >= _activeBirdCount - _birdsAway)
                continue;
            Array.Fill(_nearestDistances, float.MaxValue);
            Array.Fill(_nearest, -1);

            for (int other = 0; other < _activeBirdCount; other++)
            {
                if (other == bird || other >= _activeBirdCount - _birdsAway) continue;
                float distanceSquared = _positions[bird].DistanceSquaredTo(_positions[other]);
                for (int slot = 0; slot < NeighbourCount; slot++)
                {
                    if (distanceSquared >= _nearestDistances[slot]) continue;
                    for (int shift = NeighbourCount - 1; shift > slot; shift--)
                    {
                        _nearestDistances[shift] = _nearestDistances[shift - 1];
                        _nearest[shift] = _nearest[shift - 1];
                    }
                    _nearestDistances[slot] = distanceSquared;
                    _nearest[slot] = other;
                    break;
                }
            }

            Vector3 alignment = Vector3.Zero;
            Vector3 cohesion = Vector3.Zero;
            Vector3 separation = Vector3.Zero;
            for (int slot = 0; slot < NeighbourCount; slot++)
            {
                int neighbour = _nearest[slot];
                if (neighbour < 0) continue;
                alignment += _velocities[neighbour];
                cohesion += _positions[neighbour];
                if (_nearestDistances[slot] < 1.35f * 1.35f)
                    separation += (_positions[bird] - _positions[neighbour])
                        / Math.Max(_nearestDistances[slot], 0.04f);
            }
            alignment = (alignment / NeighbourCount).Normalized() * 5.8f - _velocities[bird];
            cohesion = cohesion / NeighbourCount - _positions[bird];

            Vector3 up = _positions[bird].Normalized();
            float wavePhase = _flockTime * 4.2f
                + _positions[bird].Dot(orbitTangent) * 0.42f;
            float terrainRadius = _planet.SurfaceRadius(up);
            float approachNest = _nestTarget >= 0 ? Mathf.Clamp(flockCentre.DistanceTo(nestDestination) / 24f, 0f, 1f) : 1f;
            float flightAltitude = Mathf.Lerp(4f, CruiseAltitude, approachNest);
            float desiredRadius = terrainRadius + flightAltitude + Mathf.Sin(wavePhase) * 2.4f;
            Vector3 altitudeWave = up * (desiredRadius - _positions[bird].Length());
            Vector3 collectiveTurn = up.Cross(_velocities[bird]).Normalized()
                * Mathf.Sin(_flockTime * 0.85f) * 1.25f;
            Vector3 fromHawk = _positions[bird] - _hawkPosition;
            float hawkDistance = fromHawk.Length();
            Vector3 predatorEscape = Vector3.Zero;
            if (hunting && hawkDistance < 9f)
            {
                float side = _positions[bird].Dot(orbitTangent) >= flockCentre.Dot(orbitTangent) ? 1f : -1f;
                predatorEscape = fromHawk.Normalized() * (10f - hawkDistance) * 2.2f
                    + orbitTangent * side * 4.5f;
            }

            Vector3 acceleration = alignment * 1.85f + cohesion * 0.82f
                + separation * 2.05f + (desiredCentre - flockCentre) * (_nestTarget >= 0 ? 2.4f : 0.78f)
                + altitudeWave * 1.15f + collectiveTurn + predatorEscape;
            acceleration = acceleration.LimitLength(9f);
            Vector3 velocity = _velocities[bird] + acceleration * delta;
            // Movement on a sphere must stay tangent. A direct Cartesian
            // velocity follows a chord and inevitably cuts through terrain.
            velocity -= up * velocity.Dot(up);
            _nextVelocities[bird] = velocity.Normalized() * Mathf.Clamp(velocity.Length(), 4.4f, 7.2f);

            if (hunting && hawkDistance < 0.42f && _eatCooldown <= 0f)
            {
                caughtBird = bird;
                _eatCooldown = 12f;
                break;
            }
        }

        for (int bird = 0; bird < _activeBirdCount; bird++)
        {
            _velocities[bird] = _nextVelocities[bird];
            _positions[bird] += _velocities[bird] * delta;
            float minimumFlightClearance = _nestTarget >= 0 ? 3.5f : CruiseAltitude - 3f;
            ClampAboveTerrain(ref _positions[bird], ref _velocities[bird], minimumFlightClearance);
            _nextVelocities[bird] = _velocities[bird];
        }
        if (caughtBird >= 0) KillBird(caughtBird);
        UpdateNesting(delta);
    }

    private void UpdateMigration(float delta)
    {
        _migrationClock += delta;
        if (_birdsAway == 0 && _migrationClock > 145f && _main.Daylight > 0.45f)
        {
            _migrationClock = 0f;
            _migrationDuration = 24f;
            _birdsAway = 14;
            if (GameSession.Current != null) GameSession.Current.MigratingBirds = _birdsAway;
        }
        if (_birdsAway == 0) return;
        _migrationDuration -= delta;
        if (_migrationDuration > 0f) return;
        for (int bird = _activeBirdCount - _birdsAway; bird < _activeBirdCount; bird++)
        {
            Vector3 opposite = -_player.GlobalPosition.Normalized();
            _positions[bird] = opposite * (_planet.SurfaceRadius(opposite) + CruiseAltitude)
                + opposite.Cross(Vector3.Up).Normalized() * (bird % 7);
            _velocities[bird] = opposite.Cross(Vector3.Right).Normalized() * 5.5f;
        }
        _birdsAway = 0;
        if (GameSession.Current != null) GameSession.Current.MigratingBirds = 0;
    }

    private void OnTerrainEdited(Vector3 direction)
    {
        if (direction.Normalized().Dot(_roostDirection) < 0.94f || GameSession.Current == null) return;
        WorldData world = GameSession.Current;
        Vector3 danger = direction.Normalized();
        world.BirdDangerZones.Add([danger.X, danger.Y, danger.Z]);
        if (world.BirdDangerZones.Count > 12) world.BirdDangerZones.RemoveAt(0);
        Vector3 escape = danger.Cross(Mathf.Abs(danger.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        _roostDirection = (_roostDirection + escape * 0.16f).Normalized();
    }

    private void UpdateNesting(float delta)
    {
        if (_nestTarget < 0 || !_nature.IsAvailable(_nestTarget))
        {
            _nestBuildTime = 0f;
            return;
        }
        Vector3 target = _nature.NestPosition(_nestTarget);
        int arrivals = 0;
        for (int bird = 0; bird < _activeBirdCount; bird++)
            if (_positions[bird].DistanceSquaredTo(target) < 5.5f * 5.5f && ++arrivals >= 2) break;
        if (arrivals < 2) { _nestBuildTime = 0f; return; }
        _nestBuildTime += delta;
        if (_nestBuildTime < 0.4f || !_nature.CreateNest(_nestTarget)) return;

        int child;
        if (_activeBirdCount < MaxBirdCount)
            child = _activeBirdCount++;
        else
        {
            // At the cap, recycle the bird farthest from the player. This is
            // effectively the requested off-camera despawn plus one newborn.
            child = 0;
            float farthest = -1f;
            for (int bird = 0; bird < _activeBirdCount; bird++)
            {
                float distance = _positions[bird].DistanceSquaredTo(_player.GlobalPosition);
                if (distance > farthest) { farthest = distance; child = bird; }
            }
        }
        Vector3 up = target.Normalized();
        Vector3 tangent = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        _positions[child] = target + up * 0.45f;
        _previousPositions[child] = _positions[child];
        _velocities[child] = tangent * 4.6f;
        _nextVelocities[child] = _velocities[child];
        if (GameSession.Current != null) GameSession.Current.StarlingCount = _activeBirdCount;
        _nestTarget = -1;
        _nestBuildTime = 0f;
    }

    private int NearestBirdTo(Vector3 position)
    {
        int nearest = -1;
        float distance = float.MaxValue;
        for (int bird = 0; bird < _activeBirdCount - _birdsAway; bird++)
        {
            float candidate = position.DistanceSquaredTo(_positions[bird]);
            if (candidate >= distance) continue;
            distance = candidate;
            nearest = bird;
        }
        return nearest;
    }

    private void KillBird(int bird)
    {
        if (bird < 0 || bird >= _activeBirdCount) return;
        // The ecosystem keeps its population: a caught bird is immediately
        // materialised on the far side of the planet, as specified by the
        // migration abstraction, then rejoins the flock naturally.
        Vector3 opposite = -_player.GlobalPosition.Normalized();
        Vector3 tangent = opposite.Cross(Mathf.Abs(opposite.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        Vector3 side = opposite.Cross(tangent).Normalized();
        _positions[bird] = opposite * (_planet.SurfaceRadius(opposite) + CruiseAltitude)
            + tangent * (float)GD.RandRange(-4.0, 4.0) + side * (float)GD.RandRange(-4.0, 4.0);
        _previousPositions[bird] = _positions[bird];
        _velocities[bird] = tangent * 5.5f;
        _nextVelocities[bird] = _velocities[bird];
        _hawkTargetBird = -1;
        SoundManager.Play(SoundKind.Hawk, -9f);
        SoundManager.Play(SoundKind.Hit, -14f);
        if (GameSession.Current != null) GameSession.Current.StarlingCount = _activeBirdCount;
    }

    public bool ValidateHawkCatch()
    {
        int before = _activeBirdCount;
        Vector3 previous = _positions[0];
        KillBird(0);
        return _activeBirdCount == before && _positions[0].DistanceTo(previous) > 20f;
    }

    public bool HasVisibleMurmurationForValidation()
    {
        if (_activeBirdCount < InitialBirdCount || _birdsAway > 20) return false;
        Vector3 centre = Vector3.Zero;
        for (int bird = 0; bird < _activeBirdCount - _birdsAway; bird++) centre += _positions[bird];
        centre /= Math.Max(1, _activeBirdCount - _birdsAway);
        int nearby = 0;
        for (int bird = 0; bird < _activeBirdCount - _birdsAway; bird++)
            if (_positions[bird].DistanceSquaredTo(centre) < 18f * 18f) nearby++;
        return nearby >= 5;
    }

    public bool ValidateNestConstruction()
    {
        int tree = _nature.FindNearestAvailableTree(_player.GlobalPosition, 500f);
        if (tree < 0) return false;
        _nestTarget = tree;
        Vector3 target = _nature.NestPosition(tree);
        _positions[0] = target;
        _positions[1] = target + target.Normalized() * 0.1f;
        _previousPositions[0] = _positions[0];
        _previousPositions[1] = _positions[1];
        UpdateNesting(0.5f);
        return _nature.HasNest(tree) && GameSession.Current?.OccupiedTreeNests.Contains(tree) == true;
    }

    private void ClampAboveTerrain(ref Vector3 position, ref Vector3 velocity, float clearance)
    {
        Vector3 up = position.Normalized();
        float minimumRadius = _planet.SurfaceRadius(up) + clearance;
        if (position.Length() < minimumRadius) position = up * minimumRadius;
        float inwardSpeed = velocity.Dot(up);
        if (inwardSpeed < 0f) velocity -= up * inwardSpeed;
    }

    private void UpdateTransforms(float interpolation = 1f)
    {
        for (int bird = 0; bird < MaxBirdCount; bird++)
        {
            if (bird >= _activeBirdCount || bird >= _activeBirdCount - _birdsAway)
            {
                _multiMesh.SetInstanceTransform(bird,
                    new Transform3D(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero));
                continue;
            }
            Vector3 renderedPosition = _previousPositions[bird].Lerp(_positions[bird], interpolation);
            Vector3 forward = _velocities[bird].Normalized();
            Vector3 up = renderedPosition.Normalized();
            Vector3 right = forward.Cross(up).Normalized();
            if (right.LengthSquared() < 0.01f) right = forward.Cross(Vector3.Up).Normalized();
            up = right.Cross(forward).Normalized();
            float flap = 0.72f + Mathf.Abs(Mathf.Sin(_flockTime * 13f + bird * 1.7f)) * 0.42f;
            Basis basis = new Basis(right * flap, up, -forward).Scaled(Vector3.One * 0.52f);
            _multiMesh.SetInstanceTransform(bird, new Transform3D(basis, renderedPosition));
        }
        Vector3 hawkForward = _hawkVelocity.Normalized();
        Vector3 renderedHawk = _previousHawkPosition.Lerp(_hawkPosition, interpolation);
        Vector3 hawkUp = renderedHawk.Normalized();
        hawkForward = (hawkForward - hawkUp * hawkForward.Dot(hawkUp)).Normalized();
        if (hawkForward.LengthSquared() < 0.01f) hawkForward = hawkUp.Cross(Vector3.Right).Normalized();
        Vector3 hawkRight = hawkForward.Cross(hawkUp).Normalized();
        _hawk.GlobalTransform = new Transform3D(
            new Basis(hawkRight, hawkUp, -hawkForward).Scaled(Vector3.One * 0.35f), renderedHawk);
    }

    private static ArrayMesh CreateBirdMesh() => CreateBirdMesh(new Color(0.13f, 0.15f, 0.18f));

    private static ArrayMesh CreateBirdMesh(Color dark)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        AddTriangle(tool, new Vector3(0, 0.04f, -0.42f), new Vector3(-0.72f, 0, 0.18f),
            new Vector3(0, 0, 0.08f), dark);
        AddTriangle(tool, new Vector3(0, 0.04f, -0.42f), new Vector3(0, 0, 0.08f),
            new Vector3(0.72f, 0, 0.18f), dark);
        AddTriangle(tool, new Vector3(-0.72f, 0, 0.18f), new Vector3(0.72f, 0, 0.18f),
            new Vector3(0, -0.08f, 0.35f), dark);
        tool.GenerateNormals();
        ArrayMesh mesh = tool.Commit();
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            AlbedoColor = dark,
            Roughness = 0.9f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            EmissionEnabled = true,
            Emission = dark,
            EmissionEnergyMultiplier = 0.32f
        });
        return mesh;
    }

    private static void AddTriangle(SurfaceTool tool, Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        tool.SetColor(color); tool.AddVertex(a);
        tool.SetColor(color); tool.AddVertex(b);
        tool.SetColor(color); tool.AddVertex(c);
    }
}
