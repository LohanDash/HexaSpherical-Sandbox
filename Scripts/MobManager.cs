using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HexaSphericalSandbox;

public partial class MobManager : Node3D
{
    private const int MaximumMobs = 28;
    private const int MaximumMobsPerChunk = 5;
    private readonly Dictionary<string, AnimalMob> _mobs = [];
    private HexPlanet _planet = null!;
    private Node3D _player = null!;
    private Camera3D _camera = null!;
    private float _existingChunkAttempt = 7f;
    private float _streamingCheck;

    public override void _Ready()
    {
        _planet = GetNode<HexPlanet>("../Planet");
        _player = GetNode<Node3D>("../Player");
        _camera = GetNode<Camera3D>("../Player/Pivot/Camera3D");
        _planet.HighDetailChunkGenerated += OnNewHighDetailChunk;

        foreach (MobSaveData saved in GameSession.Current?.Mobs ?? [])
        {
            if (saved.Position.Length != 3 || _mobs.Count >= MaximumMobs) continue;
            Spawn(saved.Type, new Vector3(saved.Position[0], saved.Position[1], saved.Position[2]), saved.Id);
        }
    }

    public override void _Process(double delta)
    {
        _streamingCheck -= (float)delta;
        if (_streamingCheck <= 0f)
        {
            _streamingCheck = 0.35f;
            foreach (AnimalMob mob in _mobs.Values)
            {
                if (!IsInstanceValid(mob)) continue;
                bool rendered = _planet.IsChunkStreamed(mob.CurrentChunk);
                bool simulated = rendered && mob.GlobalPosition.DistanceTo(_player.GlobalPosition) <= 16f;
                mob.SetActivity(rendered, simulated);
            }
        }

        _existingChunkAttempt -= (float)delta;
        if (_existingChunkAttempt > 0f) return;
        _existingChunkAttempt = (float)GD.RandRange(6.0, 12.0);
        if (_mobs.Count >= MaximumMobs || GD.Randf() > 0.82f) return;

        Vector3 up = _player.GlobalPosition.Normalized();
        Vector3 right = _camera.GlobalBasis.X;
        Vector3 behind = _camera.GlobalBasis.Z;
        float side = (float)GD.RandRange(-0.9, 0.9);
        Vector3 direction = (up + behind * 0.42f + right * side * 0.32f).Normalized();
        TrySpawnHiddenGroup(direction);
    }

    private void OnNewHighDetailChunk(Vector3 direction)
    {
        if (_mobs.Count >= MaximumMobs || GD.Randf() > 0.28f) return;
        TrySpawnHiddenGroup(direction);
    }

    private void TrySpawnHiddenGroup(Vector3 direction)
    {
        Vector3 up = direction.Normalized();
        Vector3 tangentA = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        Vector3 tangentB = up.Cross(tangentA).Normalized();
        float angle = (float)GD.RandRange(0.0, Mathf.Tau);
        direction = (up + (tangentA * Mathf.Cos(angle) + tangentB * Mathf.Sin(angle)) * 0.12f).Normalized();
        Vector3 position = _planet.PassiveMobSurfacePosition(direction);
        Vector3 toSpawn = (position - _camera.GlobalPosition).Normalized();
        bool outsideView = (-_camera.GlobalBasis.Z).Dot(toSpawn) < 0.65f;
        if (!outsideView || position.DistanceTo(_player.GlobalPosition) < 6f
            || !_planet.IsPassiveMobHabitat(direction)) return;

        int targetChunk = _planet.ChunkAt(direction);
        int remainingCapacity = MaximumMobsPerChunk - CountMobsInChunk(targetChunk);
        if (targetChunk < 0 || remainingCapacity <= 0) return;

        string type = GD.Randf() < 0.58f ? "Chicken" : "Cow";
        int groupSize = Math.Min(GD.Randf() < 0.58f ? 2 : 3, remainingCapacity);
        for (int member = 0; member < groupSize && _mobs.Count < MaximumMobs; member++)
        {
            float groupAngle = (float)GD.RandRange(0.0, Mathf.Tau);
            float spread = member == 0 ? 0f : (float)GD.RandRange(0.018, 0.05);
            Vector3 memberDirection = (direction
                + tangentA * Mathf.Cos(groupAngle) * spread
                + tangentB * Mathf.Sin(groupAngle) * spread).Normalized();
            if (!_planet.IsPassiveMobHabitat(memberDirection)) continue;
            if (_planet.ChunkAt(memberDirection) != targetChunk) continue;
            Spawn(type, _planet.PassiveMobSurfacePosition(memberDirection));
        }
    }

    private int CountMobsInChunk(int chunk)
    {
        if (chunk < 0) return 0;
        int count = 0;
        foreach (AnimalMob mob in _mobs.Values)
            if (IsInstanceValid(mob) && mob.CurrentChunk == chunk) count++;
        return count;
    }

    private void Spawn(string type, Vector3 position, string? id = null)
    {
        var mob = new AnimalMob { Name = $"{type}_{_mobs.Count}" };
        mob.Initialize(_planet, type, id);
        AddChild(mob);
        mob.PlaceAt(position);
        _mobs[mob.MobId] = mob;
    }

    public void Capture(WorldData world)
    {
        world.Mobs = _mobs.Values.Where(IsInstanceValid).Select(mob => mob.Capture()).ToList();
    }
}
