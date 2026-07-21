using Godot;
using System;

namespace HexaSphericalSandbox;

public partial class StarlingFlock : Node3D
{
    private const int BirdCount = 84;
    private const int NeighbourCount = 7;
    private const float TickInterval = 0.05f;
    private readonly Vector3[] _positions = new Vector3[BirdCount];
    private readonly Vector3[] _previousPositions = new Vector3[BirdCount];
    private readonly Vector3[] _velocities = new Vector3[BirdCount];
    private readonly Vector3[] _nextVelocities = new Vector3[BirdCount];
    private MultiMesh _multiMesh = null!;
    private Node3D _player = null!;
    private HexPlanet _planet = null!;
    private Main _main = null!;
    private MeshInstance3D _hawk = null!;
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

    public override void _Ready()
    {
        _player = GetNode<Node3D>("../Player");
        _planet = GetNode<HexPlanet>("../Planet");
        _main = GetNode<Main>("..");
        _planet.VoxelEdited += OnTerrainEdited;
        _multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            InstanceCount = BirdCount,
            Mesh = CreateBirdMesh()
        };
        AddChild(new MultiMeshInstance3D { Name = "Murmuration", Multimesh = _multiMesh });
        _hawk = new MeshInstance3D { Name = "AerialPredator", Mesh = CreateBirdMesh(new Color(0.24f, 0.12f, 0.045f)) };
        _hawk.Scale = Vector3.One * 0.95f;
        AddChild(_hawk);

        Vector3 up = _player.GlobalPosition.Normalized();
        Vector3 tangentA = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        Vector3 tangentB = up.Cross(tangentA).Normalized();
        Vector3 centre = up * (_planet.Radius + 11f);
        for (int i = 0; i < BirdCount; i++)
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
            Array.Copy(_positions, _previousPositions, BirdCount);
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
        foreach (Vector3 position in _positions) flockCentre += position;
        flockCentre /= BirdCount;

        Vector3 orbitTangent = playerUp.Cross(Vector3.Up);
        if (orbitTangent.LengthSquared() < 0.01f) orbitTangent = playerUp.Cross(Vector3.Right);
        orbitTangent = orbitTangent.Normalized();
        Vector3 dayCentre = playerUp * (_planet.Radius + 11.5f)
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
        Vector3 desiredCentre = dayCentre.Lerp(roostCentre, roosting);

        _hawkCycle += delta;
        bool hunting = Mathf.PosMod(_hawkCycle, 42f) < 17f && _main.Daylight > 0.18f;
        Vector3 hawkTarget = hunting ? flockCentre : playerUp * (_planet.Radius + 24f) - orbitTangent * 30f;
        Vector3 hawkDesired = (hawkTarget - _hawkPosition).Normalized() * (hunting ? 9.5f : 7f);
        _hawkVelocity = _hawkVelocity.MoveToward(hawkDesired, 4.2f * delta);
        _hawkPosition += _hawkVelocity * delta;

        for (int bird = 0; bird < BirdCount; bird++)
        {
            if (bird >= BirdCount - _birdsAway)
                continue;
            Array.Fill(_nearestDistances, float.MaxValue);
            Array.Fill(_nearest, -1);

            for (int other = 0; other < BirdCount; other++)
            {
                if (other == bird || other >= BirdCount - _birdsAway) continue;
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
            float desiredRadius = _planet.Radius + 11.5f + Mathf.Sin(wavePhase) * 2.1f;
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

            Vector3 acceleration = alignment * 1.7f + cohesion * 0.48f
                + separation * 2.4f + (desiredCentre - flockCentre) * 0.32f
                + altitudeWave * 1.15f + collectiveTurn + predatorEscape;
            acceleration = acceleration.LimitLength(9f);
            Vector3 velocity = _velocities[bird] + acceleration * delta;
            _nextVelocities[bird] = velocity.Normalized() * Mathf.Clamp(velocity.Length(), 4.4f, 7.2f);

            if (hunting && hawkDistance < 0.42f && _eatCooldown <= 0f)
            {
                // The population remains stable for now: an eaten bird is
                // immediately reintroduced on the opposite side of the world.
                Vector3 opposite = -_positions[bird].Normalized();
                _positions[bird] = opposite * (_planet.Radius + 11f);
                _nextVelocities[bird] = opposite.Cross(Vector3.Up).Normalized() * 5.5f;
                _eatCooldown = 1.4f;
            }
        }

        for (int bird = 0; bird < BirdCount; bird++)
        {
            _velocities[bird] = _nextVelocities[bird];
            _positions[bird] += _velocities[bird] * delta;
        }
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
        for (int bird = BirdCount - _birdsAway; bird < BirdCount; bird++)
        {
            Vector3 opposite = -_player.GlobalPosition.Normalized();
            _positions[bird] = opposite * (_planet.Radius + 11f)
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

    private void UpdateTransforms(float interpolation = 1f)
    {
        for (int bird = 0; bird < BirdCount; bird++)
        {
            if (bird >= BirdCount - _birdsAway)
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
            Basis basis = new Basis(right * flap, up, -forward).Scaled(Vector3.One * 0.32f);
            _multiMesh.SetInstanceTransform(bird, new Transform3D(basis, renderedPosition));
        }
        Vector3 hawkForward = _hawkVelocity.Normalized();
        Vector3 renderedHawk = _previousHawkPosition.Lerp(_hawkPosition, interpolation);
        Vector3 hawkUp = renderedHawk.Normalized();
        Vector3 hawkRight = hawkForward.Cross(hawkUp).Normalized();
        _hawk.GlobalTransform = new Transform3D(new Basis(hawkRight, hawkUp, -hawkForward), renderedHawk);
    }

    private static ArrayMesh CreateBirdMesh() => CreateBirdMesh(new Color(0.035f, 0.045f, 0.06f));

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
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D { AlbedoColor = dark, Roughness = 0.9f });
        return mesh;
    }

    private static void AddTriangle(SurfaceTool tool, Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        tool.SetColor(color); tool.AddVertex(a);
        tool.SetColor(color); tool.AddVertex(b);
        tool.SetColor(color); tool.AddVertex(c);
    }
}
