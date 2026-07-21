using Godot;

namespace HexaSphericalSandbox;

public partial class MusicManager : Node
{
    public enum WorldMusicContext { Surface, Ocean, Space, Jukebox }
    private enum Track { None, Aria, Mice, Cave13, Docks, Far, Chirp, Mellohi, Stal }

    private readonly RandomNumberGenerator _random = new();
    private AudioStreamPlayer _audio = null!;
    private HexPlanet _planet = null!;
    private Node3D _player = null!;
    private Track _current;
    private Track _scheduled;
    private WorldMusicContext _context = WorldMusicContext.Surface;
    private bool _inCave;
    private bool _deathMusic;
    private bool _caveTrackTriggered;
    private double _playAt;
    private double _checkCaveAt;
    private double _caveRollStartsAt;
    private double _nextCaveRollAt;
    private double _checkContextAt;

    private static readonly string[] Paths =
    [
        "",
        "res://Music/Aria Math - C418 (Minecraft Piano Cover).mp3",
        "res://Music/Minecraft - Mice on Venus  The Best Piano Cover.mp3",
        "res://Music/13 - Minecraft Music Disc - C418.mp3",
        "res://Music/Super Mario 64 Soundtrack - Dire, Dire Docks.mp3",
        "res://Music/Far - Minecraft Music Disc - C418.mp3",
        "res://Music/Chirp - Minecraft Music Disc - C418.mp3",
        "res://Music/Mellohi - Minecraft Music Disc - C418.mp3",
        "res://Music/Stal- Minecraft Music Disc - C418.mp3"
    ];

    public override void _Ready()
    {
        _random.Randomize();
        _audio = new AudioStreamPlayer { Name = "DynamicMusic", VolumeDb = -8.0f };
        AddChild(_audio);
        _audio.Finished += OnFinished;
        _planet = GetNode<HexPlanet>("../Planet");
        _player = GetNode<Node3D>("../Player");
        Schedule(RandomSurfaceTrack(), true);
    }

    public override void _Process(double delta)
    {
        double now = Now();
        if (now >= _checkContextAt && !_inCave && !_deathMusic)
        {
            _checkContextAt = now + 1.0;
            SetWorldContext(_planet.IsOcean(_player.GlobalPosition.Normalized())
                ? WorldMusicContext.Ocean : WorldMusicContext.Surface);
        }
        if (now >= _checkCaveAt)
        {
            _checkCaveAt = now + 0.25;
            SetCaveState(_planet.HasCeiling(_player.GlobalPosition));
        }
        if (_inCave && !_deathMusic && !_caveTrackTriggered
            && now >= _caveRollStartsAt && now >= _nextCaveRollAt)
        {
            _nextCaveRollAt = now + 1.0;
            if (_random.Randf() < 0.01f)
            {
                _caveTrackTriggered = true;
                Play(Track.Cave13);
            }
        }
        if (!_deathMusic && !_inCave && !_audio.Playing && now >= _playAt)
            Play(_scheduled == Track.None ? ContextTrack() : _scheduled);
    }

    // Ready for the future ocean, space and jukebox gameplay systems.
    public void SetWorldContext(WorldMusicContext context)
    {
        if (_context == context) return;
        _context = context;
        if (_deathMusic || _inCave) return;
        Stop();
        Schedule(ContextTrack(), true);
    }

    // Cave rules never interrupt these future death tracks.
    public void PlayDeathMusic(bool hardcore)
    {
        _deathMusic = true;
        Play(hardcore ? Track.Mellohi : Track.Chirp);
    }

    private void SetCaveState(bool value)
    {
        if (value == _inCave || _deathMusic) return;
        _inCave = value;
        Stop();
        if (value)
        {
            _playAt = double.PositiveInfinity;
            _caveTrackTriggered = false;
            _caveRollStartsAt = Now() + _random.RandfRange(5.0f, 10.0f);
            _nextCaveRollAt = _caveRollStartsAt;
        }
        else
        {
            _caveTrackTriggered = false;
            Schedule(RandomSurfaceTrack(), true);
        }
    }

    private void OnFinished()
    {
        Track finished = _current;
        _current = Track.None;
        if (_deathMusic)
        {
            _deathMusic = false;
            if (!_inCave) Schedule(ContextTrack(), true);
            return;
        }
        if (_inCave) return;
        if (_context != WorldMusicContext.Surface) { Schedule(ContextTrack(), true); return; }

        if (finished == Track.Mice)
            Play(_random.Randf() < 0.70f ? Track.Aria : Track.Mice);
        else if (finished == Track.Aria)
            Schedule(_random.Randf() < 0.70f ? Track.Mice : Track.Aria, true);
        else
            Schedule(RandomSurfaceTrack(), true);
    }

    private Track ContextTrack() => _context switch
    {
        WorldMusicContext.Ocean => Track.Docks,
        WorldMusicContext.Space => Track.Far,
        WorldMusicContext.Jukebox => Track.Stal,
        _ => RandomSurfaceTrack()
    };

    private Track RandomSurfaceTrack() => _random.Randf() < 0.5f ? Track.Aria : Track.Mice;

    private void Schedule(Track track, bool withGap)
    {
        _scheduled = track;
        _playAt = Now() + (withGap ? _random.RandfRange(5.0f, 10.0f) : 0.0f);
    }

    private void Play(Track track)
    {
        string path = Paths[(int)track];
        if (!ResourceLoader.Exists(path))
        {
            GD.PushWarning($"Optional music file missing: {path}");
            _current = Track.None;
            if (!_inCave && !_deathMusic) Schedule(ContextTrack(), true);
            return;
        }
        _scheduled = Track.None;
        _current = track;
        _audio.Stream = GD.Load<AudioStream>(path);
        _audio.Play();
        _playAt = double.PositiveInfinity;
    }

    private void Stop()
    {
        _audio.Stop();
        _current = Track.None;
        _scheduled = Track.None;
    }

    private static double Now() => Time.GetTicksMsec() / 1000.0;
}
