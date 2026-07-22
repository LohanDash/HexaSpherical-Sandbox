using Godot;
using System;

namespace HexaSphericalSandbox;

public enum TerrainBiome { Plains, Desert, Mountains }

public readonly record struct TerrainBiomeSample(
    TerrainBiome Biome, float PlainsWeight, float DesertWeight, float MountainWeight,
    float Height, int SurfaceBlockType, float VegetationDensity);

/// <summary>
/// Deterministic Alpha Indev V2 terrain. Biome selection, macro shape,
/// biome-specific relief and surface material are intentionally separate.
/// </summary>
public sealed class IndevBiomeTerrain
{
    public const int CurrentVersion = 2;
    private readonly int _seed;

    public IndevBiomeTerrain(int seed) => _seed = seed;

    public TerrainBiomeSample Sample(Vector3 direction)
    {
        Vector3 p = direction.Normalized();

        // Very-low-frequency fields create continent-sized, continuous regions.
        float region = Fractal(p * 1.45f, 3, 0.52f, 11);
        float climate = Fractal((p + new Vector3(13.7f, -4.2f, 8.9f)) * 1.15f, 2, 0.55f, 37);
        float mountainSuitability = region + climate * 0.18f;
        float desertSuitability = -region + climate * 0.18f;

        // Smooth memberships overlap deliberately; height is blended from all
        // three formulas, so biome borders cannot create vertical walls.
        float mountainWeight = Mathf.SmoothStep(0.05f, 0.32f, mountainSuitability);
        float desertWeight = Mathf.SmoothStep(0.05f, 0.32f, desertSuitability);
        float plainsWeight = 0.18f + (1f - Mathf.SmoothStep(0.08f, 0.38f, Mathf.Abs(region))) * 0.86f;
        float total = plainsWeight + desertWeight + mountainWeight;
        plainsWeight /= total; desertWeight /= total; mountainWeight /= total;

        // Shared macro shape keeps coast/continental-scale elevation coherent.
        float macro = Fractal((p + new Vector3(-2.1f, 7.4f, 3.8f)) * 2.15f, 3, 0.5f, 71);

        float plainRoll = Fractal(p * 8.5f, 4, 0.48f, 101);
        float plainDetail = Fractal(p * 27f, 2, 0.42f, 131);
        float plainsHeight = 8.5f + macro * 3.2f + plainRoll * 4.4f + plainDetail * 0.65f;

        // Broad, low dunes: two offset fields avoid evenly spaced sine bands.
        float duneA = Fractal(p * 6.2f, 3, 0.46f, 173);
        float duneB = Fractal((p + new Vector3(5.3f, 1.7f, -9.1f)) * 4.1f, 2, 0.5f, 191);
        float dunes = Mathf.Abs(duneA * 0.72f + duneB * 0.28f);
        float desertHeight = 6.3f + macro * 1.15f + dunes * 2.8f;

        // Ridged multifractal creates connected crests rather than amplified
        // copies of the ordinary rolling terrain.
        float rangeMask = Mathf.SmoothStep(-0.2f, 0.72f,
            Fractal((p + new Vector3(-7.8f, 3.1f, 12.6f)) * 3.0f, 3, 0.52f, 223));
        float ridges = RidgedFractal(p * 10.5f, 5, 0.57f, 251);
        float secondaryRidges = RidgedFractal((p + new Vector3(4.6f, -8.2f, 2.4f)) * 19f, 3, 0.5f, 281);
        float mountainHeight = -2.0f + macro * 5.5f
            + rangeMask * (5.0f + Mathf.Pow(ridges, 1.38f) * 29.0f + secondaryRidges * 5.0f);

        float height = plainsHeight * plainsWeight + desertHeight * desertWeight
            + mountainHeight * mountainWeight;
        height = Mathf.Clamp(height, -3.5f, 31.0f);

        TerrainBiome biome = mountainWeight >= plainsWeight && mountainWeight >= desertWeight
            ? TerrainBiome.Mountains
            : desertWeight >= plainsWeight ? TerrainBiome.Desert : TerrainBiome.Plains;
        int surface = biome switch
        {
            TerrainBiome.Desert => 3,
            TerrainBiome.Mountains when height >= 22f => 4,
            TerrainBiome.Mountains => 2,
            _ => 0
        };
        float vegetation = plainsWeight * 0.18f + desertWeight * 0.004f + mountainWeight * 0.018f;
        return new TerrainBiomeSample(biome, plainsWeight, desertWeight, mountainWeight,
            height, surface, vegetation);
    }

    private float Fractal(Vector3 p, int octaves, float persistence, int salt)
    {
        float sum = 0f, amplitude = 1f, normalizer = 0f;
        for (int octave = 0; octave < octaves; octave++)
        {
            sum += ValueNoise(p, salt + octave * 1013) * amplitude;
            normalizer += amplitude;
            p = p * 2.03f + new Vector3(11.3f, -7.1f, 5.7f);
            amplitude *= persistence;
        }
        return sum / normalizer;
    }

    private float RidgedFractal(Vector3 p, int octaves, float persistence, int salt)
    {
        float sum = 0f, amplitude = 1f, normalizer = 0f;
        for (int octave = 0; octave < octaves; octave++)
        {
            float ridge = 1f - Mathf.Abs(ValueNoise(p, salt + octave * 1013));
            ridge *= ridge;
            sum += ridge * amplitude;
            normalizer += amplitude;
            p = p * 2.07f + new Vector3(-6.4f, 9.2f, 3.3f);
            amplitude *= persistence;
        }
        return sum / normalizer;
    }

    private float ValueNoise(Vector3 p, int salt)
    {
        int x = Mathf.FloorToInt(p.X), y = Mathf.FloorToInt(p.Y), z = Mathf.FloorToInt(p.Z);
        float fx = Fade(p.X - x), fy = Fade(p.Y - y), fz = Fade(p.Z - z);
        float x00 = Mathf.Lerp(Hash(x, y, z, salt), Hash(x + 1, y, z, salt), fx);
        float x10 = Mathf.Lerp(Hash(x, y + 1, z, salt), Hash(x + 1, y + 1, z, salt), fx);
        float x01 = Mathf.Lerp(Hash(x, y, z + 1, salt), Hash(x + 1, y, z + 1, salt), fx);
        float x11 = Mathf.Lerp(Hash(x, y + 1, z + 1, salt), Hash(x + 1, y + 1, z + 1, salt), fx);
        return Mathf.Lerp(Mathf.Lerp(x00, x10, fy), Mathf.Lerp(x01, x11, fy), fz);
    }

    private float Hash(int x, int y, int z, int salt)
    {
        uint hash = unchecked((uint)_seed) ^ unchecked((uint)salt * 0x9E3779B9u);
        hash ^= unchecked((uint)x * 0x85EBCA6Bu);
        hash ^= unchecked((uint)y * 0xC2B2AE35u);
        hash ^= unchecked((uint)z * 0x27D4EB2Fu);
        hash ^= hash >> 16; hash *= 0x7FEB352Du;
        hash ^= hash >> 15; hash *= 0x846CA68Bu;
        hash ^= hash >> 16;
        return (hash & 0x00FFFFFFu) / 8388607.5f - 1f;
    }

    private static float Fade(float value) => value * value * value * (value * (value * 6f - 15f) + 10f);
}
