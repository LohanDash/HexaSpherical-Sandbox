using Godot;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexaSphericalSandbox.Tests;

public partial class SaveSystemTest : Node
{
    public override void _Ready()
    {
        WorldData? world = null;
        try
        {
            world = WorldStore.Create("__save_torture_test__", "Creative", "PreIndev");
            string root = ProjectSettings.GlobalizePath($"user://worlds/{world.Id}");
            Assert(File.Exists(Path.Combine(root, "world.save")), "initial world.save missing");
            Assert(world.GenerationPreset == "PreIndev", "generation preset was not stored");
            Assert(world.TerrainGenerationVersion == 0, "PreIndev unexpectedly opted into biome terrain");
            WorldData biomeWorld = WorldStore.Create("__biome_version_test__", "Creative", "Indev");
            Assert(biomeWorld.TerrainGenerationVersion == IndevBiomeTerrain.CurrentVersion,
                "new Indev world did not opt into the current biome generator");
            Assert(WorldStore.Load(biomeWorld.Id).TerrainGenerationVersion == IndevBiomeTerrain.CurrentVersion,
                "terrain generation version was not persisted");
            biomeWorld.TerrainGenerationVersion = 2;
            Assert(WorldStore.SaveAndFlush(biomeWorld, TimeSpan.FromSeconds(8)), "V2 compatibility save timed out");
            WorldData loadedV2 = WorldStore.Load(biomeWorld.Id);
            Assert(loadedV2.GenerationPreset == "Indev" && loadedV2.TerrainGenerationVersion == 2,
                "existing V2 world was silently upgraded or renamed");
            WorldStore.Delete(biomeWorld);

            world.Health = 73f;
            world.RenderDistance = 52f;
            world.HexBlocks = 37;
            world.HotbarItems[2] = "Purple Block";
            world.HotbarCounts[2] = 19;
            world.SelectedHotbarSlot = 2;
            world.PlacedVoxelTypes[1234] = 5;
            world.FoodPoisoned = true;
            world.InventoryItems["Twig"] = 2;
            world.InventorySlotItems[4] = "Pebble";
            world.InventorySlotCounts[4] = 3;
            world.CraftSlotItems[0] = "Stone Block";
            world.CraftSlotCounts[0] = 1;
            world.ToolDurability["Primitive Pickaxe"] = 2;
            world.DestroyedTrees.Add(17);
            world.CollectedTwigs.Add(26);
            world.Campfires.Add([1f, 2f, 3f]);
            world.Beds.Add([4f, 5f, 6f]);
            world.RespawnBedPosition = [4f, 5f, 6f];
            world.Mobs.Add(new MobSaveData
            {
                Id = "saved-sheep", Type = "Sheep", Position = [7f, 8f, 9f],
                Sheared = true, WoolRegrowSeconds = 173f
            });
            Assert(WorldStore.SaveAndFlush(world, TimeSpan.FromSeconds(8)), "generation 2 flush timed out");
            Assert(File.Exists(Path.Combine(root, "world.backup")), "world.backup missing after rotation");

            for (int value = 10; value <= 40; value += 10)
            {
                world.Health = value;
                WorldStore.Save(world);
            }
            world.Health = 99f;
            Assert(WorldStore.SaveAndFlush(world, TimeSpan.FromSeconds(8)), "concurrent flush timed out");
            int worldFolderCount = Directory.GetDirectories(ProjectSettings.GlobalizePath("user://worlds")).Length;
            WorldData loaded = WorldStore.Load(world.Id);
            Assert(Directory.GetDirectories(ProjectSettings.GlobalizePath("user://worlds")).Length == worldFolderCount,
                "loading a world unexpectedly created another world folder");
            Assert(loaded.Id == world.Id && loaded.Seed == world.Seed,
                "loading did not preserve immutable world identity");
            Assert(Math.Abs(loaded.Health - 99f) < 0.01f, "newest queued snapshot was not retained");
            Assert(Math.Abs(loaded.RenderDistance - 52f) < 0.01f, "render distance was not retained");
            Assert(loaded.HexBlocks == 37, "hotbar stack was not retained");
            Assert(loaded.HotbarItems[2] == "Purple Block" && loaded.HotbarCounts[2] == 19
                && loaded.SelectedHotbarSlot == 2, "nine-slot hotbar was not retained");
            Assert(loaded.PlacedVoxelTypes.TryGetValue(1234, out int savedType) && savedType == 5,
                "placed block type was not retained");
            Assert(loaded.FoodPoisoned
                && loaded.InventoryItems.TryGetValue("Twig", out int twigs) && twigs == 2,
                "survival poisoning or inventory was not retained");
            Assert(loaded.ToolDurability.TryGetValue("Primitive Pickaxe", out int pickaxeUses)
                && loaded.InventorySlotItems[4] == "Pebble" && loaded.InventorySlotCounts[4] == 3
                && loaded.CraftSlotItems[0] == "Stone Block" && loaded.CraftSlotCounts[0] == 1
                && pickaxeUses == 2,
                "slot inventory, crafting grid, or tool durability was not retained");
            Assert(loaded.DestroyedTrees.Contains(17) && loaded.CollectedTwigs.Contains(26)
                && loaded.Campfires.Count == 1 && loaded.Beds.Count == 1
                && loaded.RespawnBedPosition.Length == 3,
                "survival world objects or bed respawn were not retained");
            Assert(loaded.Mobs.Find(mob => mob.Id == "saved-sheep") is
                { Type: "Sheep", Sheared: true, WoolRegrowSeconds: 173f },
                "sheep wool state was not retained");

            // Destroy only the test world's primary candidate. Recovery must
            // select its valid backup rather than invent a new world/seed.
            int immutableSeed = world.Seed;
            File.WriteAllText(Path.Combine(root, "world.save"), "{ truncated");
            loaded = WorldStore.Load(world.Id);
            Assert(loaded.Seed == immutableSeed, "recovery changed immutable seed");

            // Create two individually valid but different contents for one
            // generation. UNIQUENESS requires a controlled conflict.
            string savePath = Path.Combine(root, "world.save");
            string tmpPath = Path.Combine(root, "world.tmp");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(savePath));
            JsonElement envelope = document.RootElement;
            byte[] data = Convert.FromBase64String(envelope.GetProperty("data_base64").GetString()!);
            using JsonDocument dataDocument = JsonDocument.Parse(data);
            var changedData = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(data)!;
            changedData["health"] = 12.345f;
            byte[] changedBytes = JsonSerializer.SerializeToUtf8Bytes(changedData,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            string changedChecksum = Convert.ToHexString(SHA256.HashData(changedBytes));
            var conflictingEnvelope = new
            {
                format_version = envelope.GetProperty("format_version").GetInt32(),
                seed = envelope.GetProperty("seed").GetInt32(),
                save_generation = envelope.GetProperty("save_generation").GetInt64(),
                checksum = changedChecksum,
                data_base64 = Convert.ToBase64String(changedBytes)
            };
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(conflictingEnvelope));
            Assert(!WorldStore.List().Any(item => item.Id == world.Id), "same-generation conflict was silently resolved");
            Assert(File.Exists(savePath) && File.Exists(tmpPath), "conflict candidates were not preserved");

            GD.Print("SAVE_TORTURE_TEST: PASS");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError("SAVE_TORTURE_TEST: FAIL\n" + exception);
            GetTree().Quit(1);
        }
        finally
        {
            if (world != null) WorldStore.Delete(world);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
