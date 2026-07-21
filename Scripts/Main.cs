using Godot;

namespace HexaSphericalSandbox;

public partial class Main : Node3D
{
    [Export(PropertyHint.Range, "30,1200,10")] public float DayLengthSeconds { get; set; } = 300.0f;

    private DirectionalLight3D _sun = null!;
    private DirectionalLight3D _moon = null!;
    private MeshInstance3D _sunBody = null!;
    private MeshInstance3D _moonBody = null!;
    private WorldEnvironment _worldEnvironment = null!;
    private Node3D _player = null!;
    private Label _timeLabel = null!;
    private float _dayAngle = 1.0f;
    private float _saveCountdown = 10.0f;
    private HexPlanet _planet = null!;
    private MobManager _mobs = null!;
    public float Daylight { get; private set; } = 1f;
    private bool _localStorm;
    private float _stormBlend;

    public void SetLocalStorm(bool storming) => _localStorm = storming;

    public override void _Ready()
    {
        var environment = new Environment
        {
            BackgroundMode = Environment.BGMode.Color,
            BackgroundColor = new Color(0.42f, 0.72f, 0.94f),
            AmbientLightSource = Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.18f, 0.22f, 0.32f),
            AmbientLightEnergy = 0.65f,
            TonemapMode = Environment.ToneMapper.Filmic
        };

        _worldEnvironment = GetNode<WorldEnvironment>("WorldEnvironment");
        _worldEnvironment.Environment = environment;
        _sun = GetNode<DirectionalLight3D>("Sun");
        _moon = GetNode<DirectionalLight3D>("Moon");
        _sunBody = GetNode<MeshInstance3D>("SunBody");
        _moonBody = GetNode<MeshInstance3D>("MoonBody");
        _sun.ShadowEnabled = GameSession.Current?.Quality != "Low";
        _player = GetNode<Node3D>("Player");
        _timeLabel = GetNode<Label>("HUD/TimeLabel");
        _planet = GetNode<HexPlanet>("Planet");
        _mobs = GetNode<MobManager>("MobManager");
        if (GameSession.Current != null)
        {
            SimulationClock.AdvanceOffline(GameSession.Current, System.DateTime.UtcNow);
            _dayAngle = GameSession.Current.DayAngle;
        }
    }

    public override void _Process(double deltaValue)
    {
        float delta = (float)deltaValue;
        _stormBlend = Mathf.MoveToward(_stormBlend, _localStorm ? 1f : 0f, delta * 0.32f);
        if (GameSession.Current != null) SimulationClock.AdvanceLoaded(GameSession.Current, delta);
        _saveCountdown -= delta;
        if (_saveCountdown <= 0f) { _saveCountdown = 10f; SaveCurrentWorld(); }
        _dayAngle = Mathf.PosMod(_dayAngle + Mathf.Tau * delta / DayLengthSeconds, Mathf.Tau);

        const float axialTilt = 0.32f;
        Vector3 sunlightDirection = new Vector3(
            Mathf.Cos(_dayAngle),
            Mathf.Sin(_dayAngle) * Mathf.Cos(axialTilt),
            Mathf.Sin(_dayAngle) * Mathf.Sin(axialTilt)
        ).Normalized();
        Vector3 basisUp = Mathf.Abs(sunlightDirection.Dot(Vector3.Up)) > 0.96f
            ? Vector3.Right : Vector3.Up;
        _sun.GlobalBasis = Basis.LookingAt(-sunlightDirection, basisUp);
        _moon.GlobalBasis = Basis.LookingAt(sunlightDirection, basisUp);
        Vector3 localUp = _player.GlobalPosition.Normalized();
        float sunHeight = localUp.Dot(sunlightDirection);
        float daylight = Mathf.SmoothStep(-0.18f, 0.22f, sunHeight);
        Daylight = daylight;
        float twilight = 1.0f - Mathf.Clamp(Mathf.Abs(sunHeight) / 0.28f, 0f, 1f);

        _sun.LightEnergy = Mathf.Lerp(0.08f, 1.25f, daylight);
        _sun.LightColor = new Color(1.0f, 0.72f, 0.48f).Lerp(Colors.White, daylight);
        _moon.LightEnergy = Mathf.Lerp(0.22f, 0.0f, daylight);

        const float celestialDistance = 180f;
        _sunBody.GlobalPosition = _player.GlobalPosition + sunlightDirection * celestialDistance;
        _moonBody.GlobalPosition = _player.GlobalPosition - sunlightDirection * celestialDistance;
        _sunBody.Visible = sunHeight > -0.16f;
        _moonBody.Visible = sunHeight < 0.16f;

        var environment = _worldEnvironment.Environment;
        environment.AmbientLightEnergy = Mathf.Lerp(0.16f, 0.58f, daylight);
        environment.AmbientLightColor = new Color(0.08f, 0.12f, 0.28f)
            .Lerp(new Color(0.55f, 0.65f, 0.82f), daylight);

        Color nightTop = new(0.008f, 0.014f, 0.045f);
        Color dayTop = new(0.42f, 0.72f, 0.94f);
        Color sunset = new(0.95f, 0.28f, 0.07f);
        Color uniformSky = nightTop.Lerp(dayTop, daylight).Lerp(sunset, twilight * 0.55f);
        if (_stormBlend > 0.001f)
        {
            Color stormSky = new Color(0.22f, 0.25f, 0.29f)
                .Lerp(new Color(0.4f, 0.42f, 0.44f), daylight);
            uniformSky = uniformSky.Lerp(stormSky, _stormBlend);
            _sun.LightEnergy *= Mathf.Lerp(1f, 0.42f, _stormBlend);
            environment.AmbientLightEnergy *= Mathf.Lerp(1f, 0.68f, _stormBlend);
            environment.AmbientLightColor = environment.AmbientLightColor
                .Lerp(new Color(0.34f, 0.37f, 0.4f), 0.72f * _stormBlend);
        }
        environment.BackgroundColor = uniformSky;

        float localA = localUp.X;
        float localB = localUp.Y * Mathf.Cos(axialTilt) + localUp.Z * Mathf.Sin(axialTilt);
        float localNoonAngle = Mathf.Atan2(localB, localA);
        float localHour = Mathf.PosMod((_dayAngle - localNoonAngle) / Mathf.Tau * 24f + 12f, 24f);
        int hours = Mathf.FloorToInt(localHour);
        int minutes = Mathf.FloorToInt((localHour - hours) * 60f);
        string phase = daylight > 0.65f ? "Jour" : daylight < 0.15f ? "Nuit" : "Crépuscule";
        _timeLabel.Text = $"{hours:00}:{minutes:00} — {phase}";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F10 })
        {
            SaveCurrentWorld();
            GetTree().ChangeSceneToFile("res://MainMenu.tscn");
        }
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F8 }
            && GameSession.Current != null)
            GameSession.Current.WeatherEnabled = !GameSession.Current.WeatherEnabled;
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F6 }
            && GameSession.Current != null)
            GameSession.Current.InterpolationEnabled = !GameSession.Current.InterpolationEnabled;
    }

    public override void _ExitTree()
    {
        SaveCurrentWorld(flush: true);
    }

    private void SaveCurrentWorld(bool flush = false)
    {
        var world = GameSession.Current;
        if (world == null || !IsInstanceValid(_player) || !IsInstanceValid(_planet)) return;
        Vector3 position = _player.GlobalPosition;
        world.PlayerPosition = [position.X, position.Y, position.Z];
        if (_player is SphericalPlayer sphericalPlayer) world.Health = sphericalPlayer.Health;
        world.DayAngle = _dayAngle;
        _planet.CaptureVoxelChanges(world);
        _mobs.Capture(world);
        if (flush)
        {
            try
            {
                if (!WorldStore.SaveAndFlush(world, System.TimeSpan.FromSeconds(3)))
                    GD.PushWarning("Final save timed out; world.tmp was preserved for recovery.");
            }
            catch (System.Exception exception) { GD.PushError($"Final save failed: {exception.Message}"); }
        }
        else WorldStore.Save(world);
    }
}
