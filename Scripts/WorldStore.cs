using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HexaSphericalSandbox;

public sealed class WorldData
{
    public int SaveVersion { get; set; }
    public long SaveGeneration { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Nouveau monde";
    public string GameMode { get; set; } = "Creative";
    public int Seed { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public float[] PlayerPosition { get; set; } = [];
    public List<long> RemovedVoxels { get; set; } = [];
    public List<long> PlacedVoxels { get; set; } = [];
    public float DayAngle { get; set; } = 1f;
    public float Health { get; set; } = 100f;
    public string Quality { get; set; } = "Low";
    public List<MobSaveData> Mobs { get; set; } = [];
    public bool WeatherEnabled { get; set; } = true;
    public bool InterpolationEnabled { get; set; } = true;
    public float PlantPopulation { get; set; } = 1f;
    public float InsectPopulation { get; set; } = 1f;
    public float BirdPopulation { get; set; } = 1f;
    public float PredatorPopulation { get; set; } = 0.35f;
    public List<float[]> BirdDangerZones { get; set; } = [];
    public int MigratingBirds { get; set; }
    public long SimulationTicks { get; set; }
    public DateTime LastRealWorldUtc { get; set; } = DateTime.UtcNow;
}

public sealed class MobSaveData
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "Chicken";
    public float[] Position { get; set; } = [];
}

public static class GameSession
{
    public static WorldData? Current { get; set; }
    public static bool IsCreative => Current?.GameMode == "Creative";
}

public static class WorldStore
{
    public static List<WorldData> List() => WorldSaveManager.Instance.ListWorlds();

    public static WorldData Create(string name, string mode)
    {
        var world = new WorldData {
            Id = Guid.NewGuid().ToString("N"), Name = string.IsNullOrWhiteSpace(name) ? "Nouveau monde" : name.Trim(),
            GameMode = mode, Seed = Random.Shared.Next(1, int.MaxValue), SaveVersion = 5
        };
        WorldSaveManager.Instance.Flush(world, TimeSpan.FromSeconds(8));
        return world;
    }

    public static void Save(WorldData world)
    {
        WorldSaveManager.Instance.QueueSave(world);
    }

    public static bool SaveAndFlush(WorldData world, TimeSpan timeout) => WorldSaveManager.Instance.Flush(world, timeout);

    public static void Delete(WorldData world)
    {
        WorldSaveManager.Instance.DeleteWorld(world.Id);
    }
}
