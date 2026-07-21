using Godot;
using System;

namespace HexaSphericalSandbox;

public partial class EcosystemManager : Node3D
{
    private const int CloudCount = 26;
    private const int RainCount = 52;
    private Main _main = null!;
    private Node3D _player = null!;
    private HexPlanet _planet = null!;
    private MultiMeshInstance3D _clouds = null!;
    private MultiMeshInstance3D _rain = null!;
    private MeshInstance3D _owl = null!;
    private readonly Vector3[] _cloudDirections = new Vector3[CloudCount];
    private readonly Vector3[] _rainOffsets = new Vector3[RainCount];
    private Vector3 _stormDirection = new(0.7f, 0.2f, -0.68f);
    private float _time;
    private float _visualTick;
    private float _ecologyTick;
    private float _rareRoll;
    private Label _eventLabel = null!;

    public override void _Ready()
    {
        _main = GetNode<Main>("..");
        _player = GetNode<Node3D>("../Player");
        _planet = GetNode<HexPlanet>("../Planet");
        _eventLabel = GetNode<Label>("../HUD/RareEventLabel");
        _clouds = CreateInstances("Clouds", new SphereMesh { Radius = 2.5f, Height = 3.2f, RadialSegments = 8, Rings = 4 },
            CloudCount, new Color(0.82f, 0.87f, 0.9f, 0.82f), false);
        _rain = CreateInstances("LocalRain", new BoxMesh { Size = new Vector3(0.025f, 0.55f, 0.025f) },
            RainCount, new Color(0.35f, 0.58f, 0.9f, 0.72f), true);
        _owl = new MeshInstance3D { Name = "NightOwl", Mesh = new SphereMesh { Radius = 0.32f, Height = 0.7f, RadialSegments = 7, Rings = 4 } };
        _owl.MaterialOverride = Material(new Color(0.22f, 0.16f, 0.11f), false);
        AddChild(_owl);

        for (int i = 0; i < CloudCount; i++)
        {
            float y = 1f - 2f * (i + 0.5f) / CloudCount;
            float radius = Mathf.Sqrt(1f - y * y);
            float angle = i * 2.399963f;
            _cloudDirections[i] = new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius);
        }
        for (int i = 0; i < RainCount; i++) _rainOffsets[i] = RandomTangentOffset(7f, 8f);
    }

    public override void _Process(double deltaValue)
    {
        float delta = (float)deltaValue;
        _time += delta;
        _visualTick -= delta;
        _ecologyTick -= delta;
        _rareRoll -= delta;
        bool interpolate = GameSession.Current?.InterpolationEnabled ?? true;
        if (interpolate)
            UpdateVisuals(delta);
        else if (_visualTick <= 0f) { _visualTick = 0.2f; UpdateVisuals(0.2f); }
        if (_ecologyTick <= 0f) { _ecologyTick = 10f; UpdateFoodChain(); }
        if (_rareRoll <= 0f) { _rareRoll = 30f; RollRareEvent(); }
        if (_eventLabel.Visible)
        {
            _eventLabel.Modulate = new Color(1, 1, 1, Mathf.Clamp(_eventLabel.Modulate.A - delta * 0.035f, 0f, 1f));
            if (_eventLabel.Modulate.A <= 0.01f) _eventLabel.Visible = false;
        }
    }

    private void UpdateVisuals(float delta)
    {
        WorldData? world = GameSession.Current;
        bool weather = world?.WeatherEnabled ?? true;
        Vector3 up = _player.GlobalPosition.Normalized();
        Basis rotation = new Basis(Vector3.Up, _time * 0.012f);
        for (int i = 0; i < CloudCount; i++)
        {
            Vector3 direction = rotation * _cloudDirections[i];
            float scale = 0.65f + (i % 5) * 0.12f;
            _clouds.Multimesh.SetInstanceTransform(i,
                new Transform3D(Basis.Identity.Scaled(new Vector3(1.8f, 0.55f, 1f) * scale), direction * (_planet.Radius + 16f)));
        }
        _clouds.Visible = weather;
        _stormDirection = (new Basis(Vector3.Up, delta * 0.04f) * _stormDirection).Normalized();
        bool localRain = weather && up.Dot(_stormDirection) > 0f;
        _main.SetLocalStorm(localRain);
        _rain.Visible = localRain;
        Vector3 tangentA = up.Cross(Mathf.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        Vector3 tangentB = up.Cross(tangentA).Normalized();
        for (int i = 0; i < RainCount; i++)
        {
            _rainOffsets[i].Y -= 7f * delta;
            if (_rainOffsets[i].Y < 0.4f) _rainOffsets[i] = RandomTangentOffset(7f, 8f);
            Vector3 p = _player.GlobalPosition + tangentA * _rainOffsets[i].X
                + up * _rainOffsets[i].Y + tangentB * _rainOffsets[i].Z;
            _rain.Multimesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, p));
        }

        bool night = _main.Daylight < 0.22f;
        _owl.Visible = night;
        _owl.GlobalPosition = up * (_planet.Radius + 7f) + tangentA * Mathf.Cos(_time * 0.7f) * 8f
            + tangentB * Mathf.Sin(_time * 0.7f) * 8f;
    }

    private void UpdateFoodChain()
    {
        WorldData? world = GameSession.Current;
        if (world == null) return;
        world.PlantPopulation = Mathf.Clamp(world.PlantPopulation + (0.025f - world.InsectPopulation * 0.012f), 0.2f, 1.5f);
        world.InsectPopulation = Mathf.Clamp(world.InsectPopulation + (world.PlantPopulation * 0.018f - world.BirdPopulation * 0.015f), 0.15f, 1.5f);
        world.BirdPopulation = Mathf.Clamp(world.BirdPopulation + (world.InsectPopulation * 0.012f - world.PredatorPopulation * 0.01f), 0.25f, 1.4f);
        world.PredatorPopulation = Mathf.Clamp(world.PredatorPopulation + (world.BirdPopulation - 0.8f) * 0.004f, 0.1f, 0.8f);
    }

    private void RollRareEvent()
    {
        // Equivalent average probability to 1/10,000 each second, checked in
        // a single coarse batch every 30 seconds instead of every frame/second.
        if (GD.Randi() % 10000 >= 30) return;
        string[] events = ["PLUIE DE POULETS", "UN ESCARGOT TRAVERSE LE CIEL",
            "84 ÉTOURNEAUX INVOQUENT MEGAKOTKOT", "UNE VACHE ENTRE EN ORBITE"];
        _eventLabel.Text = events[GD.RandRange(0, events.Length - 1)];
        _eventLabel.Modulate = Colors.White;
        _eventLabel.Visible = true;
    }

    private MultiMeshInstance3D CreateInstances(string name, PrimitiveMesh mesh, int count, Color color, bool emission)
    {
        mesh.Material = Material(color, emission);
        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = count };
        var instance = new MultiMeshInstance3D { Name = name, Multimesh = multi };
        AddChild(instance);
        return instance;
    }

    private static StandardMaterial3D Material(Color color, bool emission) => new()
    {
        AlbedoColor = color, Transparency = color.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        EmissionEnabled = emission, Emission = color, EmissionEnergyMultiplier = emission ? 2.4f : 1f
    };

    private static Vector3 RandomTangentOffset(float radius, float height) => new(
        (float)GD.RandRange(-radius, radius), (float)GD.RandRange(0.4, height), (float)GD.RandRange(-radius, radius));
}
