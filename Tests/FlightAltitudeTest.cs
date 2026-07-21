using Godot;
using System;

namespace HexaSphericalSandbox.Tests;

public partial class FlightAltitudeTest : Node
{
    public override async void _Ready()
    {
        try
        {
            GameSession.Current = new WorldData
            {
                Id = "flight-altitude-test", Name = "Flight Altitude Test", Seed = 448812,
                GenerationPreset = "Indev", GameMode = "Creative", WeatherEnabled = true
            };
            PackedScene scene = GD.Load<PackedScene>("res://Main.tscn");
            Node main = scene.Instantiate();
            AddChild(main);
            for (int frame = 0; frame < 180; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var flock = main.GetNode<StarlingFlock>("StarlingFlock");
            var weather = main.GetNode<EcosystemManager>("EcosystemManager");
            float birdClearance = flock.MinimumTerrainClearance();
            float cloudClearance = weather.MinimumCloudClearance();
            if (birdClearance < 5.4f) throw new Exception($"Bird clipped terrain: clearance={birdClearance}");
            if (cloudClearance < 95f) throw new Exception($"Cloud too low: clearance={cloudClearance}");
            GD.Print($"FLIGHT_ALTITUDE_TEST: PASS (birds={birdClearance:F2}, clouds={cloudClearance:F2})");
            GameSession.Current = null;
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GameSession.Current = null;
            GD.PushError("FLIGHT_ALTITUDE_TEST: FAIL\n" + exception);
            GetTree().Quit(1);
        }
    }
}
