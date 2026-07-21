using Godot;
using System;
using System.Diagnostics;

namespace HexaSphericalSandbox.Tests;

public partial class PlanetGenerationTest : Node
{
    public override void _Ready()
    {
        try
        {
            GameSession.Current = new WorldData
            {
                Id = "__planet_generation_test__",
                Seed = 73421,
                GenerationPreset = "Indev",
                Quality = "Low"
            };
            AddChild(new Node3D { Name = "Player", Position = Vector3.Up * 292f });
            var stopwatch = Stopwatch.StartNew();
            var planet = new HexPlanet { Name = "Planet" };
            AddChild(planet);
            stopwatch.Stop();
            if (!Mathf.IsEqualApprox(planet.Radius, 288f) || planet.Subdivisions != 7
                || !Mathf.IsEqualApprox(planet.Relief, 22f) || !Mathf.IsZeroApprox(planet.CellGap))
                throw new InvalidOperationException("Indev planet dimensions are incorrect.");
            GD.Print($"PLANET_GENERATION_TEST: PASS ({stopwatch.Elapsed.TotalSeconds:F2}s, radius={planet.Radius}, subdivisions={planet.Subdivisions})");
            GameSession.Current = null;
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GameSession.Current = null;
            GD.PushError("PLANET_GENERATION_TEST: FAIL\n" + exception);
            GetTree().Quit(1);
        }
    }
}
