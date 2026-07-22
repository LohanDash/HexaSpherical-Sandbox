using Godot;
using System;
using System.Collections.Generic;

namespace HexaSphericalSandbox.Tests;

public partial class BiomeTerrainTest : Node
{
    public override void _Ready()
    {
        try
        {
            const int seed = 73421;
            var first = new IndevBiomeTerrain(seed);
            var second = new IndevBiomeTerrain(seed);
            var plains = new List<float>();
            var deserts = new List<float>();
            var mountains = new List<float>();
            double plainSlope = 0, desertSlope = 0;
            int plainSlopeCount = 0, desertSlopeCount = 0, transitionCount = 0;
            float maximumTransitionStep = 0f;

            const int samples = 16000;
            float goldenAngle = Mathf.Pi * (3f - Mathf.Sqrt(5f));
            for (int index = 0; index < samples; index++)
            {
                float y = 1f - 2f * (index + 0.5f) / samples;
                float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float angle = index * goldenAngle;
                Vector3 direction = new(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius);
                TerrainBiomeSample a = first.Sample(direction);
                TerrainBiomeSample b = second.Sample(direction);
                if (!a.Equals(b)) throw new InvalidOperationException("Equal seeds produced different terrain bytes.");

                Vector3 tangent = direction.Cross(Mathf.Abs(direction.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
                Vector3 nearby = (direction + tangent * 0.0015f).Normalized();
                float localStep = Mathf.Abs(first.Sample(nearby).Height - a.Height);
                if (a.Biome == TerrainBiome.Plains && a.PlainsWeight > 0.72f)
                {
                    plains.Add(a.Height); plainSlope += localStep; plainSlopeCount++;
                }
                else if (a.Biome == TerrainBiome.Desert && a.DesertWeight > 0.72f)
                {
                    deserts.Add(a.Height); desertSlope += localStep; desertSlopeCount++;
                }
                else if (a.Biome == TerrainBiome.Mountains && a.MountainWeight > 0.72f)
                    mountains.Add(a.Height);

                float strongest = Mathf.Max(a.PlainsWeight, Mathf.Max(a.DesertWeight, a.MountainWeight));
                if (strongest < 0.62f)
                {
                    transitionCount++;
                    maximumTransitionStep = Mathf.Max(maximumTransitionStep, localStep);
                }
            }

            if (plains.Count < 300 || deserts.Count < 300 || mountains.Count < 300)
                throw new InvalidOperationException($"Biome regions are missing: P={plains.Count}, D={deserts.Count}, M={mountains.Count}.");
            double averagePlainSlope = plainSlope / plainSlopeCount;
            double averageDesertSlope = desertSlope / desertSlopeCount;
            if (averageDesertSlope >= averagePlainSlope * 0.82)
                throw new InvalidOperationException($"Desert is not flatter than plains: D={averageDesertSlope:F4}, P={averagePlainSlope:F4}.");
            float plainAmplitude = Range(plains);
            float mountainAmplitude = Range(mountains);
            float highestMountain = Maximum(mountains);
            int colossalSamples = mountains.FindAll(height => height >= 55f).Count;
            if (mountainAmplitude < plainAmplitude * 2.4f || mountainAmplitude < 48f
                || highestMountain < 70f || colossalSamples < 120)
                throw new InvalidOperationException($"Mountains lack amplitude: M={mountainAmplitude:F2}, P={plainAmplitude:F2}, peak={highestMountain:F2}, colossal={colossalSamples}.");
            // V3's eighty-metre massifs legitimately have steeper continuous
            // foothills than V2, while still forbidding abrupt vertical walls.
            if (transitionCount < 100 || maximumTransitionStep > 1.75f)
                throw new InvalidOperationException($"Biome transition discontinuity: count={transitionCount}, max step={maximumTransitionStep:F3}.");

            ValidatePreIndevFingerprint(seed);
            GD.Print($"BIOME_TERRAIN_TEST: PASS (P={plains.Count}, D={deserts.Count}, M={mountains.Count}, slopes={averagePlainSlope:F4}/{averageDesertSlope:F4})");
            GameSession.Current = null;
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GameSession.Current = null;
            GD.PushError("BIOME_TERRAIN_TEST: FAIL\n" + exception);
            GetTree().Quit(1);
        }
    }

    private void ValidatePreIndevFingerprint(int seed)
    {
        GameSession.Current = new WorldData
        {
            Id = "__preindev_fingerprint__", Seed = seed,
            GenerationPreset = "PreIndev", TerrainGenerationVersion = 0, Quality = "Low"
        };
        AddChild(new Node3D { Name = "Player", Position = Vector3.Up * 42f });
        var planet = new HexPlanet { Name = "Planet" };
        AddChild(planet);
        Vector3 diagonal = new Vector3(1f, 1f, 1f).Normalized();
        (Vector3 Direction, float Expected)[] fingerprint =
        [
            (Vector3.Right, 0.177468825f),
            (Vector3.Up, -0.137925219f),
            (Vector3.Back, 1.063812246f),
            (diagonal, 0.074964950f)
        ];
        foreach ((Vector3 direction, float expected) in fingerprint)
        {
            float actual = planet.TerrainSampleAt(direction).Height;
            if (Mathf.Abs(actual - expected) > 0.00002f)
                throw new InvalidOperationException($"PreIndev changed at {direction}: expected {expected}, got {actual}.");
        }
        planet.QueueFree();
        GetNode<Node3D>("Player").QueueFree();
    }

    private static float Range(List<float> values)
    {
        float minimum = float.MaxValue, maximum = float.MinValue;
        foreach (float value in values) { minimum = Mathf.Min(minimum, value); maximum = Mathf.Max(maximum, value); }
        return maximum - minimum;
    }

    private static float Maximum(List<float> values)
    {
        float maximum = float.MinValue;
        foreach (float value in values) maximum = Mathf.Max(maximum, value);
        return maximum;
    }
}
