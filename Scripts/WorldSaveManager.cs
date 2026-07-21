using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HexaSphericalSandbox;

public sealed class SaveConflictException(string message) : IOException(message);

public sealed class WorldSaveSnapshot
{
    public required string WorldId { get; init; }
    public required int Seed { get; init; }
    public required long Generation { get; init; }
    public required WorldData Data { get; init; }
}

internal sealed class WorldSaveEnvelope
{
    public int FormatVersion { get; set; }
    public int Seed { get; set; }
    public long SaveGeneration { get; set; }
    public string Checksum { get; set; } = "";
    public string DataBase64 { get; set; } = "";
}

internal sealed record ValidSave(string Path, WorldSaveEnvelope Envelope, WorldData Data);

public sealed class WorldSaveManager
{
    public const int CurrentFormatVersion = 1;
    public static WorldSaveManager Instance { get; } = new();

    private static readonly JsonSerializerOptions DataJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };
    private static readonly JsonSerializerOptions EnvelopeJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
    private readonly object _gate = new();
    private readonly Dictionary<string, WorldSaveSnapshot> _pending = [];
    private readonly Dictionary<string, long> _nextGeneration = [];
    private readonly Dictionary<string, long> _completedGeneration = [];
    private readonly Dictionary<string, Exception> _lastFailures = [];
    private bool _workerRunning;

    private static string WorldsFolder => ProjectSettings.GlobalizePath("user://worlds");
    private static string WorldFolder(string id) => Path.Combine(WorldsFolder, id);

    public WorldSaveSnapshot CaptureSnapshot(WorldData liveWorld)
    {
        WorldData frozen = Clone(liveWorld);
        long generation;
        lock (_gate)
        {
            long known = Math.Max(liveWorld.SaveGeneration, _nextGeneration.GetValueOrDefault(liveWorld.Id));
            generation = known + 1;
            _nextGeneration[liveWorld.Id] = generation;
        }
        frozen.SaveGeneration = generation;
        frozen.UpdatedUtc = DateTime.UtcNow;
        liveWorld.SaveGeneration = generation;
        liveWorld.UpdatedUtc = frozen.UpdatedUtc;
        return new WorldSaveSnapshot { WorldId = frozen.Id, Seed = frozen.Seed, Generation = generation, Data = frozen };
    }

    public long QueueSave(WorldData liveWorld)
    {
        WorldSaveSnapshot snapshot = CaptureSnapshot(liveWorld);
        lock (_gate)
        {
            // Only the newest not-yet-started snapshot for a world matters.
            _pending[snapshot.WorldId] = snapshot;
            _lastFailures.Remove(snapshot.WorldId);
            if (!_workerRunning)
            {
                _workerRunning = true;
                _ = Task.Run(WorkerLoop);
            }
            Monitor.PulseAll(_gate);
        }
        return snapshot.Generation;
    }

    public bool Flush(WorldData liveWorld, TimeSpan timeout)
    {
        long generation = QueueSave(liveWorld);
        Stopwatch stopwatch = Stopwatch.StartNew();
        lock (_gate)
        {
            while (_completedGeneration.GetValueOrDefault(liveWorld.Id) < generation)
            {
                if (_lastFailures.TryGetValue(liveWorld.Id, out Exception? failure))
                    throw new IOException("World save worker failed.", failure);
                TimeSpan remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero) return false; // world.tmp is deliberately retained.
                Monitor.Wait(_gate, remaining);
            }
        }
        return true;
    }

    public List<WorldData> ListWorlds()
    {
        Directory.CreateDirectory(WorldsFolder);
        var worlds = new Dictionary<string, WorldData>();
        foreach (string folder in Directory.GetDirectories(WorldsFolder))
        {
            try
            {
                WorldData? world = Recover(folder, true);
                if (world != null)
                {
                    // Alpha migration must not make every old world look newly
                    // played and reorder several identically named saves.
                    string migratedMetadata = Path.Combine(WorldsFolder, world.Id + ".json.migrated");
                    if (world.SaveGeneration <= 1 && File.Exists(migratedMetadata))
                    {
                        try
                        {
                            WorldData? legacy = JsonSerializer.Deserialize<WorldData>(File.ReadAllText(migratedMetadata));
                            if (legacy?.Id == world.Id && legacy.Seed == world.Seed)
                            {
                                world.CreatedUtc = legacy.CreatedUtc;
                                world.UpdatedUtc = legacy.UpdatedUtc;
                            }
                        }
                        catch { /* Metadata fallback is optional. */ }
                    }
                    worlds[world.Id] = world;
                }
            }
            catch (Exception exception) { GD.PushWarning($"Save conflict in {folder}: {exception.Message}"); }
        }

        // One-time migration from Alpha 0.0.3's single JSON files.
        foreach (string legacyPath in Directory.GetFiles(WorldsFolder, "*.json"))
        {
            try
            {
                WorldData? legacy = JsonSerializer.Deserialize<WorldData>(File.ReadAllText(legacyPath));
                if (legacy == null || worlds.ContainsKey(legacy.Id)) continue;
                legacy.SaveGeneration = 0;
                if (!Flush(legacy, TimeSpan.FromSeconds(8))) continue;
                worlds[legacy.Id] = legacy;
                File.Move(legacyPath, legacyPath + ".migrated", true);
            }
            catch (Exception exception) { GD.PushWarning($"Legacy save migration failed: {exception.Message}"); }
        }
        return worlds.Values.OrderByDescending(world => world.UpdatedUtc).ToList();
    }

    public void DeleteWorld(string id)
    {
        string folder = WorldFolder(id);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        string legacy = Path.Combine(WorldsFolder, id + ".json");
        if (File.Exists(legacy)) File.Delete(legacy);
    }

    private async Task WorkerLoop()
    {
        while (true)
        {
            WorldSaveSnapshot snapshot;
            lock (_gate)
            {
                if (_pending.Count == 0) { _workerRunning = false; Monitor.PulseAll(_gate); return; }
                snapshot = _pending.Values.OrderByDescending(item => item.Generation).First();
                _pending.Remove(snapshot.WorldId);
            }
            try
            {
                await WriteSnapshot(snapshot).ConfigureAwait(false);
                lock (_gate)
                {
                    _completedGeneration[snapshot.WorldId] = Math.Max(
                        _completedGeneration.GetValueOrDefault(snapshot.WorldId), snapshot.Generation);
                    _lastFailures.Remove(snapshot.WorldId);
                    Monitor.PulseAll(_gate);
                }
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    _lastFailures[snapshot.WorldId] = exception;
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    private static async Task WriteSnapshot(WorldSaveSnapshot snapshot)
    {
        string folder = WorldFolder(snapshot.WorldId);
        Directory.CreateDirectory(folder);
        string save = Path.Combine(folder, "world.save");
        string temporary = Path.Combine(folder, "world.tmp");
        string backup = Path.Combine(folder, "world.backup");

        ValidSave? current = RecoverCandidate(folder);
        if (current != null)
        {
            if (current.Envelope.Seed != snapshot.Seed)
                throw new InvalidDataException("The immutable world seed changed.");
            if (snapshot.Generation < current.Envelope.SaveGeneration)
                return; // Monotonicity: never promote an obsolete generation.
        }

        byte[] dataBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot.Data, DataJson);
        string checksum = Convert.ToHexString(SHA256.HashData(dataBytes));
        var envelope = new WorldSaveEnvelope
        {
            FormatVersion = CurrentFormatVersion,
            Seed = snapshot.Seed,
            SaveGeneration = snapshot.Generation,
            Checksum = checksum,
            DataBase64 = Convert.ToBase64String(dataBytes)
        };
        byte[] envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, EnvelopeJson);
        await using (var stream = new FileStream(temporary, FileMode.Create, System.IO.FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(envelopeBytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(true);
        }

        ValidSave validatedTmp = ReadAndValidate(temporary);
        if (validatedTmp.Envelope.SaveGeneration != snapshot.Generation
            || validatedTmp.Envelope.Checksum != checksum)
            throw new InvalidDataException("world.tmp read-back validation failed.");

        ValidSave? newestBeforePromotion = RecoverCandidate(folder);
        if (newestBeforePromotion != null && newestBeforePromotion.Path != temporary
            && newestBeforePromotion.Envelope.SaveGeneration > snapshot.Generation) return;

        if (File.Exists(save)) File.Move(save, backup, true);
        File.Move(temporary, save, true);
        DurableFileOperations.FlushDirectory(folder);
    }

    private static WorldData? Recover(string folder, bool repair)
    {
        ValidSave? best = RecoverCandidate(folder);
        if (best == null) return null;
        if (repair && Path.GetFileName(best.Path) != "world.save")
        {
            string save = Path.Combine(folder, "world.save");
            File.Copy(best.Path, save, true);
            DurableFileOperations.FlushDirectory(folder);
        }
        best.Data.SaveGeneration = best.Envelope.SaveGeneration;
        return best.Data;
    }

    private static ValidSave? RecoverCandidate(string folder)
    {
        string[] names = ["world.save", "world.tmp", "world.backup"];
        var valid = new List<ValidSave>();
        foreach (string name in names)
        {
            string path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            try { valid.Add(ReadAndValidate(path)); }
            catch { /* Invalid candidates are preserved for diagnostics. */ }
        }
        if (valid.Count == 0) return null;
        long newestGeneration = valid.Max(item => item.Envelope.SaveGeneration);
        ValidSave[] newest = valid.Where(item => item.Envelope.SaveGeneration == newestGeneration).ToArray();
        string[] checksums = newest.Select(item => item.Envelope.Checksum).Distinct(StringComparer.Ordinal).ToArray();
        if (checksums.Length > 1)
            throw new SaveConflictException($"Generation {newestGeneration} has multiple valid contents: "
                + string.Join(", ", newest.Select(item => $"{Path.GetFileName(item.Path)}={item.Envelope.Checksum}")));
        return newest.FirstOrDefault(item => Path.GetFileName(item.Path) == "world.save") ?? newest[0];
    }

    private static ValidSave ReadAndValidate(string path)
    {
        WorldSaveEnvelope envelope = JsonSerializer.Deserialize<WorldSaveEnvelope>(File.ReadAllBytes(path), EnvelopeJson)
            ?? throw new InvalidDataException("Empty save envelope.");
        if (envelope.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException($"Unsupported save format {envelope.FormatVersion}.");
        byte[] dataBytes = Convert.FromBase64String(envelope.DataBase64);
        string checksum = Convert.ToHexString(SHA256.HashData(dataBytes));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(checksum), Encoding.ASCII.GetBytes(envelope.Checksum)))
            throw new InvalidDataException("Save checksum mismatch.");
        WorldData data = JsonSerializer.Deserialize<WorldData>(dataBytes, DataJson)
            ?? throw new InvalidDataException("Empty save data.");
        if (data.Seed != envelope.Seed || data.SaveGeneration != envelope.SaveGeneration)
            throw new InvalidDataException("Envelope/data seed or generation mismatch.");
        return new ValidSave(path, envelope, data);
    }

    private static WorldData Clone(WorldData source) => new()
    {
        SaveVersion = source.SaveVersion, SaveGeneration = source.SaveGeneration, Id = source.Id,
        Name = source.Name, GameMode = source.GameMode, Seed = source.Seed,
        CreatedUtc = source.CreatedUtc, UpdatedUtc = source.UpdatedUtc,
        PlayerPosition = [.. source.PlayerPosition], RemovedVoxels = [.. source.RemovedVoxels],
        PlacedVoxels = [.. source.PlacedVoxels], DayAngle = source.DayAngle, Health = source.Health,
        Quality = source.Quality, Mobs = source.Mobs.Select(mob => new MobSaveData
            { Id = mob.Id, Type = mob.Type, Position = [.. mob.Position] }).ToList(),
        WeatherEnabled = source.WeatherEnabled, InterpolationEnabled = source.InterpolationEnabled,
        PlantPopulation = source.PlantPopulation,
        InsectPopulation = source.InsectPopulation, BirdPopulation = source.BirdPopulation,
        PredatorPopulation = source.PredatorPopulation,
        BirdDangerZones = source.BirdDangerZones.Select(zone => zone.ToArray()).ToList(),
        MigratingBirds = source.MigratingBirds, SimulationTicks = source.SimulationTicks,
        LastRealWorldUtc = source.LastRealWorldUtc
    };
}

internal static class DurableFileOperations
{
    public static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        int descriptor = open(path, 0);
        if (descriptor < 0) return;
        try { _ = fsync(descriptor); }
        finally { _ = close(descriptor); }
    }

    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int descriptor);
    [DllImport("libc", SetLastError = true)] private static extern int close(int descriptor);
}
