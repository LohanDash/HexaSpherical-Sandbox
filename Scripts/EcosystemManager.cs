using Godot;
using System;

namespace HexaSphericalSandbox;

public partial class EcosystemManager : Node3D
{
    private const int RainCount = 72;
    private const float CloudAltitude = 120f;
    private Main _main = null!;
    private Node3D _player = null!;
    private HexPlanet _planet = null!;
    private MeshInstance3D _clouds = null!;
    private ShaderMaterial _cloudMaterial = null!;
    private MultiMeshInstance3D _rain = null!;
    private MeshInstance3D _owl = null!;
    private readonly Vector3[] _rainOffsets = new Vector3[RainCount];
    private float _time;
    private float _visualTick;
    private float _ecologyTick;
    private float _rareRoll;
    private float _rainDecisionTick;
    private float _weatherSampleTick;
    private int _weatherBand = -1;
    private bool _localRain;
    private Label _eventLabel = null!;

    public float MinimumCloudClearance()
    {
        return CloudAltitude;
    }

    public override void _Ready()
    {
        _main = GetNode<Main>("..");
        _player = GetNode<Node3D>("../Player");
        _planet = GetNode<HexPlanet>("../Planet");
        _eventLabel = GetNode<Label>("../HUD/RareEventLabel");
        _clouds = CreateCloudInstances();
        _rain = CreateInstances("LocalRain", new BoxMesh { Size = new Vector3(0.025f, 0.55f, 0.025f) },
            RainCount, new Color(0.35f, 0.58f, 0.9f, 0.72f), true);
        _owl = new MeshInstance3D { Name = "NightOwl", Mesh = new SphereMesh { Radius = 0.32f, Height = 0.7f, RadialSegments = 7, Rings = 4 } };
        _owl.MaterialOverride = Material(new Color(0.22f, 0.16f, 0.11f), false);
        AddChild(_owl);

        for (int i = 0; i < RainCount; i++) _rainOffsets[i] = RandomTangentOffset(7f, 8f);
        UpdateVisuals(0f);
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
        _clouds.Visible = weather;
        _cloudMaterial.SetShaderParameter("daylight", _main.Daylight);
        UpdateLocalRain(weather, CloudDensity(up, _time), delta);
        _main.SetLocalStorm(_localRain);
        _rain.Visible = _localRain;
        SoundManager.SetRain(_localRain);
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
        string[] events = ["CHICKEN RAIN", "A SNAIL CROSSES THE SKY",
            "84 STARLINGS SUMMON MEGAKOTKOT", "A COW ENTERS ORBIT"];
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

    private MeshInstance3D CreateCloudInstances()
    {
        var shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode blend_mix, depth_prepass_alpha, cull_disabled, unshaded;
                uniform float daylight = 1.0;
                varying vec3 sphere_direction;

                float random3(vec3 p) {
                    return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453);
                }
                float value_noise(vec3 p) {
                    vec3 i = floor(p);
                    vec3 f = fract(p);
                    f = f * f * (3.0 - 2.0 * f);
                    float a = mix(random3(i), random3(i + vec3(1,0,0)), f.x);
                    float b = mix(random3(i + vec3(0,1,0)), random3(i + vec3(1,1,0)), f.x);
                    float c = mix(random3(i + vec3(0,0,1)), random3(i + vec3(1,0,1)), f.x);
                    float d = mix(random3(i + vec3(0,1,1)), random3(i + vec3(1,1,1)), f.x);
                    return mix(mix(a, b, f.y), mix(c, d, f.y), f.z);
                }
                float fbm(vec3 p) {
                    float total = 0.0;
                    float amplitude = 0.56;
                    for (int octave = 0; octave < 4; octave++) {
                        total += value_noise(p) * amplitude;
                        p = p * 2.03 + vec3(17.2, 9.4, 13.7);
                        amplitude *= 0.48;
                    }
                    return total;
                }
                void vertex() {
                    sphere_direction = normalize(VERTEX);
                }
                void fragment() {
                    vec3 direction = normalize(sphere_direction);
                    vec3 wind = vec3(TIME * 0.0045, TIME * 0.0012, -TIME * 0.0028);
                    float continent = fbm(direction * 2.15 + wind);
                    float formations = fbm(direction * 6.4 + wind * 1.8 + vec3(8.1, 2.4, 5.7));
                    float detail = fbm(direction * 17.0 - wind * 0.7);
                    float field = continent * 0.70 + formations * 0.25 + detail * 0.10;
                    float density = smoothstep(0.49, 0.64, field);
                    if (density < 0.02) discard;
                    float lightness = mix(0.66, 1.0, detail);
                    vec3 day_cloud = vec3(lightness, lightness * 1.01, lightness * 1.035);
                    vec3 night_cloud = vec3(0.018, 0.022, 0.035) * mix(0.65, 1.35, detail);
                    ALBEDO = mix(night_cloud, day_cloud, smoothstep(0.08, 0.48, daylight));
                    ALPHA = density * 0.88;
                }
                """
        };
        float radius = _planet.Radius + CloudAltitude;
        _cloudMaterial = new ShaderMaterial { Shader = shader };
        var sphere = new SphereMesh
        {
            Radius = radius,
            Height = radius * 2f,
            RadialSegments = 96,
            Rings = 48,
            Material = _cloudMaterial
        };
        var instance = new MeshInstance3D { Name = "GlobalCloudLayer", Mesh = sphere };
        AddChild(instance);
        return instance;
    }

    private static float CloudDensity(Vector3 direction, float time)
    {
        // Cheap CPU counterpart of the broad shader field. It creates long
        // weather fronts with genuinely clear regions without sampling the GPU.
        Vector3 p = direction.Normalized();
        float a = Mathf.Sin(p.X * 5.1f + p.Y * 2.7f + time * 0.018f);
        float b = Mathf.Sin(p.Y * 7.3f - p.Z * 4.4f - time * 0.011f);
        float c = Mathf.Sin((p.X + p.Z) * 11.2f + time * 0.007f);
        return 0.5f + (a * 0.23f + b * 0.18f + c * 0.09f);
    }

    private void UpdateLocalRain(bool weatherEnabled, float density, float delta)
    {
        if (!weatherEnabled)
        {
            _localRain = false;
            _weatherBand = -1;
            _rainDecisionTick = 0f;
            _weatherSampleTick = 0f;
            return;
        }

        // Five visible coverage bands. Only the top two are capable of rain:
        // very cloudy = 50%, cloudy = 10%, every clearer band = 0%.
        _weatherSampleTick -= delta;
        _rainDecisionTick -= delta;
        if (_weatherSampleTick > 0f) return;
        _weatherSampleTick = 2f;

        int band = density >= 0.72f ? 4
            : density >= 0.58f ? 3
            : density >= 0.46f ? 2
            : density >= 0.34f ? 1 : 0;
        if (band == _weatherBand && _rainDecisionTick > 0f) return;

        _weatherBand = band;
        _rainDecisionTick = 24f;
        float rainChance = band == 4 ? 0.50f : band == 3 ? 0.10f : 0f;
        _localRain = rainChance > 0f && GD.Randf() < rainChance;
    }

    private static StandardMaterial3D Material(Color color, bool emission) => new()
    {
        AlbedoColor = color, Transparency = color.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
        ShadingMode = emission ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel,
        EmissionEnabled = emission, Emission = color, EmissionEnergyMultiplier = emission ? 2.4f : 1f
    };

    private static Vector3 RandomTangentOffset(float radius, float height) => new(
        (float)GD.RandRange(-radius, radius), (float)GD.RandRange(0.4, height), (float)GD.RandRange(-radius, radius));
}
