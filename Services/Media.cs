using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace Glance.Services;

/// One app's media session as Windows reports it: Spotify, a browser tab (YouTube, SoundCloud, YouTube Music…), any player.
/// Fields are refreshed from the session's own change events; nothing is polled.
public sealed class Player
{
    internal readonly Session Session;
    public string Source { get; }
    /// The program whose mixer volume this player follows (a browser: the whole browser).
    public string Exe { get; }
    public bool IsBrowser => Source is "Chrome" or "Edge" or "Brave" or "Opera" or "Vivaldi" or "Firefox";

    /// Mixer volume of the player's app; null when it has no audio session right now (or plays elsewhere).
    public (float Level, bool Muted)? Volume => Audio.Get(Exe);
    public void SetVolume(float level) => Audio.Set(Exe, level);
    public void SetMuted(bool muted) => Audio.Mute(Exe, muted);
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public ImageSource? Cover { get; private set; }
    public bool Playing { get; private set; }
    public bool CanSeek { get; private set; }
    public bool CanPrev { get; private set; }
    public bool CanNext { get; private set; }
    public bool CanToggle { get; private set; }
    /// Zero when the source reports no timeline (live streams, some players).
    public TimeSpan Duration { get; private set; }

    TimeSpan start, position;
    DateTimeOffset updated;
    double rate = 1;
    string? coverFor;

    // Track-change alerts: wait for a burst of metadata updates to settle, then compare with the last song.
    readonly DispatcherTimer settle;
    string? lastTrack; // null until first seen: a player that shows up mid-song isn't "a new song"

    internal Player(Session s)
    {
        Session = s;
        Source = SourceName(s.SourceAppUserModelId);
        Exe = ExeName(s.SourceAppUserModelId, Source);
        settle = new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Background, (_, _) => Settled(), Application.Current.Dispatcher);
        settle.Stop();
    }

    /// Where playback is now: the last reported position, moved on by the time since, while playing.
    public TimeSpan Position
    {
        get
        {
            var p = Playing ? position + (DateTimeOffset.Now - updated) * rate : position;
            return p < TimeSpan.Zero ? TimeSpan.Zero : Duration > TimeSpan.Zero && p > Duration ? Duration : p;
        }
    }

    internal async Task LoadProps()
    {
        var p = await Session.TryGetMediaPropertiesAsync();
        Title = p.Title ?? "";
        Artist = p.Artist is { Length: > 0 } a ? a : p.AlbumArtist ?? "";
        Album = p.AlbumTitle ?? "";
        var key = Track;
        if (coverFor != key)
        {
            coverFor = key;
            Cover = await Picture(p.Thumbnail);
        }
        settle.Stop();
        settle.Start();
    }

    internal void LoadPlayback()
    {
        var i = Session.GetPlaybackInfo();
        position = Position; // re-anchor before the rate or state changes
        updated = DateTimeOffset.Now;
        Playing = i.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        rate = i.PlaybackRate is > 0 and var r ? r : 1;
        CanSeek = i.Controls.IsPlaybackPositionEnabled;
        CanPrev = i.Controls.IsPreviousEnabled;
        CanNext = i.Controls.IsNextEnabled;
        CanToggle = i.Controls.IsPlayPauseToggleEnabled || i.Controls.IsPlayEnabled || i.Controls.IsPauseEnabled;
    }

    internal void LoadTimeline()
    {
        var t = Session.GetTimelineProperties();
        start = t.StartTime;
        Duration = t.EndTime > t.StartTime ? t.EndTime - t.StartTime : TimeSpan.Zero;
        position = t.Position - t.StartTime;
        // Some players never set the update time; then the position is as of now.
        updated = t.LastUpdatedTime.Year > 2000 ? t.LastUpdatedTime : DateTimeOffset.Now;
    }

    string Track => $"{Title}\n{Artist}\n{Album}";

    void Settled()
    {
        settle.Stop();
        var key = Track;
        if (lastTrack is not null && key != lastTrack && Title.Length > 0 && Playing) Media.Announce(this);
        lastTrack = key; // repeat-one, pause, seek: same song, no alert
    }

    internal void Stop() => settle.Stop();

    internal async Task Toggle() => await Session.TryTogglePlayPauseAsync();
    internal async Task Prev() => await Session.TrySkipPreviousAsync();
    internal async Task Next() => await Session.TrySkipNextAsync();
    internal async Task Seek(TimeSpan to)
    {
        (position, updated) = (to, DateTimeOffset.Now); // the bar stays where it was dropped until the player confirms
        await Session.TryChangePlaybackPositionAsync((start + to).Ticks);
    }

    static async Task<ImageSource?> Picture(IRandomAccessStreamReference? r)
    {
        if (r is null) return null;
        try
        {
            using var s = await r.OpenReadAsync();
            var bytes = new MemoryStream();
            await s.AsStreamForRead().CopyToAsync(bytes);
            bytes.Position = 0;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 320;
            img.StreamSource = bytes;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception e) when (e is COMException or IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null; // no cover is fine
        }
    }

    static readonly (string Key, string Name)[] Known =
    [
        ("spotify", "Spotify"), ("msedge", "Edge"), ("chrome", "Chrome"), ("brave", "Brave"), ("opera", "Opera"), ("vivaldi", "Vivaldi"),
        ("firefox", "Firefox"), ("308046b0af4a39cb", "Firefox"), ("zunemusic", "Media Player"), ("zunevideo", "Movies & TV"),
        ("vlc", "VLC"), ("applemusic", "Apple Music"), ("tidal", "TIDAL"), ("deezer", "Deezer"),
    ];

    /// Process name for the mixer: "Spotify.exe" → Spotify, the Chrome tab → chrome.
    internal static string ExeName(string aumid, string source)
    {
        if (aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(aumid);
        return source switch
        {
            "Chrome" => "chrome", "Edge" => "msedge", "Firefox" => "firefox", "Media Player" => "Microsoft.Media.Player", "Movies & TV" => "Video.UI",
            _ => source.ToLowerInvariant(),
        };
    }

    /// "Spotify.exe", "Chrome", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → a name people know.
    internal static string SourceName(string aumid)
    {
        var lower = aumid.ToLowerInvariant();
        foreach (var (key, name) in Known) if (lower.Contains(key)) return name;
        var app = aumid.Split('!')[0].Split('_')[0];
        if (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app = app[..^4];
        app = app[(app.LastIndexOf('.') + 1)..];
        return app.Length > 0 ? app : "Player";
    }
}

/// What's playing on this PC, through Windows' media sessions (the same source as the volume flyout's media controls).
public static class Media
{
    static GlobalSystemMediaTransportControlsSessionManager? manager;
    static readonly List<Player> players = [];
    static Player? picked; // chosen by hand; dropped when another player starts playing

    public static IReadOnlyList<Player> Players => players;
    /// The player shown: the one picked, else the one playing, else the last one shown.
    public static Player? Current { get; private set; }

    /// Something about the players changed (always raised on the UI thread).
    public static event Action? Changed;
    /// A different song started (debounced; repeats, pauses and seeks don't count).
    public static event Action<Player>? TrackChanged;

    public static async void Start()
    {
        try { manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException) { Db.Error("media", e); return; }
        manager.SessionsChanged += (_, _) => Ui(Sync);
        Sync();
    }

    static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    static void Sync()
    {
        IReadOnlyList<Session> sessions;
        try { sessions = manager!.GetSessions(); }
        catch (COMException) { return; }

        foreach (var p in players.Where(p => !sessions.Contains(p.Session)).ToList())
        {
            p.Stop();
            players.Remove(p); // the app closed or the tab went away: its events stop with it
        }
        foreach (var s in sessions.Where(s => players.All(p => p.Session != s)))
        {
            var p = new Player(s);
            players.Add(p);
            s.MediaPropertiesChanged += (_, _) => Ui(() => Refresh(p, p.LoadProps));
            s.PlaybackInfoChanged += (_, _) => Ui(() => Refresh(p, () => { p.LoadPlayback(); return Task.CompletedTask; }));
            s.TimelinePropertiesChanged += (_, _) => Ui(() => Refresh(p, () => { p.LoadTimeline(); return Task.CompletedTask; }));
            Refresh(p, async () => { p.LoadTimeline(); p.LoadPlayback(); await p.LoadProps(); });
        }
        Choose();
    }

    /// Reads from a session, which can vanish at any moment (player closed mid-song): then it's simply dropped on the next sync.
    static async void Refresh(Player p, Func<Task> load)
    {
        if (!players.Contains(p)) return;
        var wasPlaying = p.Playing;
        try { await load(); }
        catch (Exception e) when (e is COMException or ObjectDisposedException or InvalidOperationException) { return; }
        if (p.Playing && !wasPlaying && picked != p) picked = null; // something else started: follow it
        Choose();
    }

    static void Choose()
    {
        if (picked is not null && !players.Contains(picked)) picked = null;
        Current = picked
            ?? (Current is not null && players.Contains(Current) && (Current.Playing || !players.Any(p => p.Playing)) ? Current : null)
            ?? players.FirstOrDefault(p => p.Playing)
            ?? players.FirstOrDefault();
        Changed?.Invoke();
    }

    public static void Pick(Player p)
    {
        picked = p;
        Choose();
    }

    internal static void Announce(Player p) => TrackChanged?.Invoke(p);

    /// Player commands; a player that refuses or has gone away just doesn't respond.
    public static async void Do(Func<Task> command)
    {
        try { await command(); }
        catch (Exception e) when (e is COMException or ObjectDisposedException or InvalidOperationException) { }
    }
}
