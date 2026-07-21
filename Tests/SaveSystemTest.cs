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
            world = WorldStore.Create("__save_torture_test__", "Creative");
            string root = ProjectSettings.GlobalizePath($"user://worlds/{world.Id}");
            Assert(File.Exists(Path.Combine(root, "world.save")), "initial world.save missing");

            world.Health = 73f;
            Assert(WorldStore.SaveAndFlush(world, TimeSpan.FromSeconds(8)), "generation 2 flush timed out");
            Assert(File.Exists(Path.Combine(root, "world.backup")), "world.backup missing after rotation");

            for (int value = 10; value <= 40; value += 10)
            {
                world.Health = value;
                WorldStore.Save(world);
            }
            world.Health = 99f;
            Assert(WorldStore.SaveAndFlush(world, TimeSpan.FromSeconds(8)), "concurrent flush timed out");
            WorldData loaded = WorldStore.List().Single(item => item.Id == world.Id);
            Assert(Math.Abs(loaded.Health - 99f) < 0.01f, "newest queued snapshot was not retained");

            // Destroy only the test world's primary candidate. Recovery must
            // select its valid backup rather than invent a new world/seed.
            int immutableSeed = world.Seed;
            File.WriteAllText(Path.Combine(root, "world.save"), "{ truncated");
            loaded = WorldStore.List().Single(item => item.Id == world.Id);
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
