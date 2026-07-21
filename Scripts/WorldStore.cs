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
    public string Name { get; set; } = "New World";
    public string GameMode { get; set; } = "Creative";
    // Missing in legacy saves by design: deserialization keeps them Pre-Indev.
    public string GenerationPreset { get; set; } = "PreIndev";
    public int Seed { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public float[] PlayerPosition { get; set; } = [];
    public List<long> RemovedVoxels { get; set; } = [];
    public List<long> PlacedVoxels { get; set; } = [];
    public float DayAngle { get; set; } = 1f;
    public float Health { get; set; } = 100f;
    public bool FoodPoisoned { get; set; }
    public string Quality { get; set; } = "Low";
    public float RenderDistance { get; set; }
    public int HexBlocks { get; set; } = 64;
    public string[] HotbarItems { get; set; } = new string[9];
    public int[] HotbarCounts { get; set; } = new int[9];
    // Added fields have empty defaults so old saves deserialize safely. The
    // legacy InventoryItems dictionary is migrated into these real slots once.
    public string[] InventorySlotItems { get; set; } = new string[27];
    public int[] InventorySlotCounts { get; set; } = new int[27];
    public string[] CraftSlotItems { get; set; } = new string[9];
    public int[] CraftSlotCounts { get; set; } = new int[9];
    public Dictionary<string, int> ToolDurability { get; set; } = [];
    public int SelectedHotbarSlot { get; set; }
    public Dictionary<long, int> PlacedVoxelTypes { get; set; } = [];
    public Dictionary<string, int> InventoryItems { get; set; } = [];
    public List<int> DestroyedTrees { get; set; } = [];
    public List<int> CollectedTwigs { get; set; } = [];
    public List<float[]> Campfires { get; set; } = [];
    public List<MobSaveData> Mobs { get; set; } = [];
    public bool WeatherEnabled { get; set; } = true;
    public bool InterpolationEnabled { get; set; } = true;
    public float PlantPopulation { get; set; } = 1f;
    public float InsectPopulation { get; set; } = 1f;
    public float BirdPopulation { get; set; } = 1f;
    public float PredatorPopulation { get; set; } = 0.35f;
    public List<float[]> BirdDangerZones { get; set; } = [];
    public int MigratingBirds { get; set; }
    public int StarlingCount { get; set; } = 84;
    public List<int> OccupiedTreeNests { get; set; } = [];
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

    public static WorldData Load(string id) => WorldSaveManager.Instance.LoadWorld(id);

    public static WorldData Create(string name, string mode, string generationPreset = "Indev")
    {
        var world = new WorldData {
            Id = Guid.NewGuid().ToString("N"), Name = string.IsNullOrWhiteSpace(name) ? "New World" : name.Trim(),
            GameMode = mode, GenerationPreset = generationPreset,
            Seed = Random.Shared.Next(1, int.MaxValue), SaveVersion = 6
        };
        if (mode == "Survival") world.HexBlocks = 0;
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
