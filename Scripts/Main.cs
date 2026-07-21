using Godot;

namespace HexaSphericalSandbox;

public partial class Main : Node3D
{
    public override void _Ready()
    {
        var environment = new Environment
        {
            BackgroundMode = Environment.BGMode.Sky,
            AmbientLightSource = Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.18f, 0.22f, 0.32f),
            AmbientLightEnergy = 0.65f,
            TonemapMode = Environment.ToneMapper.Filmic
        };

        var sky = new Sky();
        var skyMaterial = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.015f, 0.035f, 0.11f),
            SkyHorizonColor = new Color(0.35f, 0.5f, 0.7f),
            GroundBottomColor = new Color(0.01f, 0.01f, 0.02f),
            GroundHorizonColor = new Color(0.16f, 0.22f, 0.3f)
        };
        sky.SkyMaterial = skyMaterial;
        environment.Sky = sky;
        GetNode<WorldEnvironment>("WorldEnvironment").Environment = environment;
    }
}

