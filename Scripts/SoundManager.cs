using Godot;
using System;
using System.Collections.Generic;

namespace HexaSphericalSandbox;

public enum SoundKind { Footstep, BlockBreak, BlockPlace, Cow, Chicken, Hawk, Monster, MonsterDeath, Hit, Tree, Pickup, Craft, Rain }

/// <summary>Compact sound bank using recorded animal calls and procedural gameplay effects.</summary>
public partial class SoundManager : Node
{
    private static SoundManager? _instance;
    private static readonly Dictionary<SoundKind, int> _playCounts = [];
    private readonly Dictionary<SoundKind, AudioStream> _sounds = [];
    private AudioStreamPlayer _rain = null!;

    public override void _Ready()
    {
        _instance = this;
        _playCounts.Clear();
        foreach (SoundKind kind in Enum.GetValues<SoundKind>())
        {
            AudioStream? stream = LoadRecordedAnimal(kind) ?? (IsAnimal(kind) ? null : Build(kind, loop: kind == SoundKind.Rain));
            if (stream != null) _sounds[kind] = stream;
        }
        _rain = new AudioStreamPlayer { Name = "RainAmbience", Stream = _sounds[SoundKind.Rain], VolumeDb = -22f };
        AddChild(_rain);
    }

    public override void _ExitTree() { if (_instance == this) _instance = null; }

    public static void Play(SoundKind kind, float volumeDb = -8f)
    {
        if (_instance == null || !_instance._sounds.TryGetValue(kind, out AudioStream? stream)) return;
        _playCounts[kind] = _playCounts.GetValueOrDefault(kind) + 1;
        var player = new AudioStreamPlayer { Stream = stream, VolumeDb = volumeDb };
        _instance.AddChild(player);
        player.Finished += player.QueueFree;
        player.Play();
    }

    public static int PlayCountForValidation(SoundKind kind) => _playCounts.GetValueOrDefault(kind);

    private static bool IsAnimal(SoundKind kind) => kind is SoundKind.Cow or SoundKind.Chicken or SoundKind.Hawk;

    private static AudioStream? LoadRecordedAnimal(SoundKind kind)
    {
        string? path = kind switch
        {
            SoundKind.Cow => "res://Audio/Animals/cow_moo.ogg",
            SoundKind.Chicken => "res://Audio/Animals/chicken_clucks.ogg",
            SoundKind.Hawk => "res://Audio/Animals/hawk_call.mp3",
            _ => null
        };
        return path == null ? null : ResourceLoader.Load<AudioStream>(path);
    }

    public static void SetRain(bool raining)
    {
        if (_instance == null || _instance._rain.Playing == raining) return;
        if (raining) _instance._rain.Play(); else _instance._rain.Stop();
    }

    private static AudioStreamWav Build(SoundKind kind, bool loop)
    {
        int rate = 22050;
        float duration = kind == SoundKind.Rain ? 1.8f : kind == SoundKind.Footstep ? 0.12f : 0.22f;
        int count = Mathf.CeilToInt(rate * duration);
        byte[] data = new byte[count * 2];
        var random = new Random((int)kind * 7919 + 17);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)rate;
            float fade = loop ? 1f : Mathf.Pow(1f - i / (float)count, 1.4f);
            float noise = (float)(random.NextDouble() * 2.0 - 1.0);
            float value = kind switch
            {
                SoundKind.Footstep => noise * 0.12f + Mathf.Sin(t * 78f * Mathf.Tau) * 0.3f,
                SoundKind.BlockBreak => noise * 0.58f + Mathf.Sin(t * 290f) * 0.12f,
                SoundKind.BlockPlace => noise * 0.22f + Mathf.Sin(t * 180f) * 0.34f,
                SoundKind.Monster => Mathf.Sin(t * 72f * Mathf.Tau) * 0.45f + noise * 0.12f,
                SoundKind.MonsterDeath => Mathf.Sin(t * (105f - t * 300f) * Mathf.Tau) * 0.48f + noise * 0.16f,
                SoundKind.Hit => noise * 0.3f + Mathf.Sin(t * 165f * Mathf.Tau) * 0.22f,
                SoundKind.Tree => noise * 0.45f + Mathf.Sin(t * 105f) * 0.22f,
                SoundKind.Pickup => Mathf.Sin(t * (520f + t * 900f) * Mathf.Tau) * 0.38f,
                SoundKind.Craft => Mathf.Sin(t * (340f + t * 700f) * Mathf.Tau) * 0.42f,
                SoundKind.Rain => noise * 0.22f,
                _ => 0f
            };
            short sample = (short)Mathf.Clamp(value * fade * 28000f, short.MinValue, short.MaxValue);
            data[i * 2] = (byte)(sample & 0xff);
            data[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = false, Data = data,
            LoopMode = loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled,
            LoopEnd = loop ? count : 0
        };
    }

}
