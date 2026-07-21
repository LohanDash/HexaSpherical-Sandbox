using System;

namespace HexaSphericalSandbox;

public sealed class PlanetEcologyState
{
    public string PlanetId { get; set; } = "central";
    public float Plants { get; set; } = 1f;
    public float Insects { get; set; } = 1f;
    public float Birds { get; set; } = 1f;
    public float Predators { get; set; } = 0.35f;
    public long LastSimulatedTick { get; set; }

    public static PlanetEcologyState FromWorld(WorldData world) => new()
    {
        Plants = world.PlantPopulation, Insects = world.InsectPopulation,
        Birds = world.BirdPopulation, Predators = world.PredatorPopulation,
        LastSimulatedTick = world.SimulationTicks
    };

    public void ApplyTo(WorldData world)
    {
        world.PlantPopulation = Plants; world.InsectPopulation = Insects;
        world.BirdPopulation = Birds; world.PredatorPopulation = Predators;
        world.SimulationTicks = LastSimulatedTick;
    }
}

public static class SimulationClock
{
    public const long TicksPerSecond = 20;
    private static readonly TimeSpan MaximumOfflineTime = TimeSpan.FromHours(24);
    private static readonly TimeSpan AbstractStep = TimeSpan.FromMinutes(5);

    public static PlanetEcologyState AdvanceOffline(WorldData world, DateTime utcNow)
    {
        PlanetEcologyState state = PlanetEcologyState.FromWorld(world);
        TimeSpan elapsed = utcNow - world.LastRealWorldUtc;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed > MaximumOfflineTime) elapsed = MaximumOfflineTime;
        long steps = (long)Math.Floor(elapsed.TotalSeconds / AbstractStep.TotalSeconds);
        for (long step = 0; step < steps; step++)
        {
            long stepIndex = state.LastSimulatedTick / (TicksPerSecond * (long)AbstractStep.TotalSeconds);
            SimulateAbstract(state, world.Seed, stepIndex);
            state.LastSimulatedTick += TicksPerSecond * (long)AbstractStep.TotalSeconds;
        }
        world.LastRealWorldUtc = utcNow;
        state.ApplyTo(world);
        return state;
    }

    public static void AdvanceLoaded(WorldData world, double seconds)
    {
        world.SimulationTicks += Math.Max(0L, (long)Math.Round(seconds * TicksPerSecond));
        world.LastRealWorldUtc = DateTime.UtcNow;
    }

    private static void SimulateAbstract(PlanetEcologyState state, int worldSeed, long stepIndex)
    {
        ulong random = DeterministicHash((ulong)(uint)worldSeed, StableStringHash(state.PlanetId), (ulong)stepIndex, 0x45434F4CUL);
        float variation = ((random >> 40) / (float)(1UL << 24) - 0.5f) * 0.012f;
        state.Plants = Math.Clamp(state.Plants + 0.018f - state.Insects * 0.009f + variation, 0.2f, 1.5f);
        state.Insects = Math.Clamp(state.Insects + state.Plants * 0.012f - state.Birds * 0.01f, 0.15f, 1.5f);
        state.Birds = Math.Clamp(state.Birds + state.Insects * 0.008f - state.Predators * 0.007f, 0.25f, 1.4f);
        state.Predators = Math.Clamp(state.Predators + (state.Birds - 0.8f) * 0.003f, 0.1f, 0.8f);
    }

    private static ulong DeterministicHash(ulong a, ulong b, ulong c, ulong d)
    {
        ulong value = 1469598103934665603UL;
        foreach (ulong part in new[] { a, b, c, d })
        {
            value ^= part; value *= 1099511628211UL;
            value ^= value >> 32; value *= 0xD6E8FEB86659FD93UL;
        }
        return value ^ (value >> 32);
    }

    private static ulong StableStringHash(string text)
    {
        ulong value = 1469598103934665603UL;
        foreach (char character in text) { value ^= character; value *= 1099511628211UL; }
        return value;
    }
}
