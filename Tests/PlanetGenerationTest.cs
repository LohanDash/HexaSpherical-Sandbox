using Godot;
using System;
using System.Diagnostics;

namespace HexaSphericalSandbox.Tests;

public partial class PlanetGenerationTest : Node
{
    public override async void _Ready()
    {
        try
        {
            GameSession.Current = new WorldData
            {
                Id = "__planet_generation_test__",
                Seed = 73421,
                GenerationPreset = "Indev",
                TerrainGenerationVersion = IndevBiomeTerrain.CurrentVersion,
                Quality = "Low"
            };
            var player = new Node3D { Name = "Player", Position = Vector3.Up * 292f };
            AddChild(player);
            var stopwatch = Stopwatch.StartNew();
            var planet = new HexPlanet { Name = "Planet" };
            AddChild(planet);
            stopwatch.Stop();
            if (!Mathf.IsEqualApprox(planet.Radius, 288f) || planet.Subdivisions != 7
                || !Mathf.IsEqualApprox(planet.Relief, 22f) || !Mathf.IsZeroApprox(planet.CellGap))
                throw new InvalidOperationException("Indev planet dimensions are incorrect.");
            if (!planet.ValidateDistantCliffCoverage(out int cliffCount))
                throw new InvalidOperationException("Distant LOD leaves at least one mountain cliff open.");
            if (!planet.ValidateMountainCaveCeiling(out int forbiddenMountainCaves))
                throw new InvalidOperationException($"V3 mountain mass contains {forbiddenMountainCaves} caves above the crust ceiling.");
            double maximumFrameMilliseconds = 0;
            for (int frame = 0; frame < 24; frame++)
            {
                ulong before = Time.GetTicksUsec();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                maximumFrameMilliseconds = Math.Max(maximumFrameMilliseconds, (Time.GetTicksUsec() - before) / 1000.0);
            }
            double traversalTotalMilliseconds = 0;
            for (int frame = 0; frame < 120; frame++)
            {
                float angle = frame * 0.075f;
                Vector3 direction = new Vector3(Mathf.Cos(angle), 0.28f, Mathf.Sin(angle)).Normalized();
                player.Position = direction * (planet.Radius + 140f);
                ulong before = Time.GetTicksUsec();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                double frameMilliseconds = (Time.GetTicksUsec() - before) / 1000.0;
                traversalTotalMilliseconds += frameMilliseconds;
                maximumFrameMilliseconds = Math.Max(maximumFrameMilliseconds, frameMilliseconds);
            }
            GD.Print($"PLANET_GENERATION_TEST: distant LOD closes {cliffCount} steep cliff edges");
            long managedBytes = GC.GetTotalMemory(true);
            GD.Print($"PLANET_PROFILE: startup={planet.StartupMilliseconds:F1}ms " +
                $"topology={planet.TopologyMilliseconds:F1}ms (ico={planet.IcosphereMilliseconds:F1}, dual={planet.DualCellMilliseconds:F1}) " +
                $"terrain={planet.TerrainDataMilliseconds:F1}ms " +
                $"caves={planet.CaveDataMilliseconds:F1}ms chunks={planet.ChunkIndexMilliseconds:F1}ms " +
                $"cells={planet.GeneratedCellCount} cave_cells={planet.DetailedCaveCellCount} " +
                $"detailed={planet.LoadedDetailedChunkCount} lod={planet.LoadedLodChunkCount} " +
                $"chunk_avg={planet.AverageChunkBuildMilliseconds:F2}ms chunk_max={planet.MaximumChunkBuildMilliseconds:F2}ms " +
                $"travel_avg={traversalTotalMilliseconds / 120.0:F2}ms frame_max={maximumFrameMilliseconds:F2}ms " +
                $"fps_monitor={Performance.GetMonitor(Performance.Monitor.TimeFps):F1} " +
                $"managed={managedBytes / 1048576.0:F1}MiB");
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
