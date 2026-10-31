using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;
using Windows.Media.Control;

namespace DynamicIsland.Services;

/// <summary>
/// Now Playing for every app that talks to Windows' media controls (Spotify, VLC, Apple Music,
/// and media in any browser: YouTube, JioHotstar, Netflix, Prime Video and so on).
/// Each session is tracked by its own identity, the source (app, browser, site) is detected
/// from the live session, and "open" returns to that exact app, window or browser tab.
/// Everything is event driven; the only polling is the progress bar, and only while it's visible.
/// </summary>
public sealed class MediaService : ObservableObject, IDisposable
{
    /// <summary>How long a paused track stays in the compact pill (counted from the moment of pausing).</summary>
    private static readonly TimeSpan PausedLinger = TimeSpan.FromMinutes(2);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _lingerTimer;
    private readonly List<GlobalSystemMediaTransportControlsSession> _sessions = new();
    private readonly Dictionary<GlobalSystemMediaTransportControlsSession, bool> _wasPlaying = new(ReferenceEqualityComparer.Instance);
    private readonly DispatcherTimer _tabProbeTimer;
    private GlobalSystemMediaTransportControlsSession? _userPick;
    private int _probeVersion;

    private MediaSourceInfo _source = MediaSourceInfo.None;
    private string? _service;
    private MediaKind _serviceKind;
    private MediaKind _reportedKind;
    private MediaKind _kind;
    private string _sourceLabel = "";
    private string _openHint = "Open player";

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _propertiesVersion;
    private bool _hadTrack;

    private TimeSpan _position;
    private TimeSpan _start;
    private bool _scrubbing;
    private bool _hasMedia;
    private bool _canSeek;
    private TimeSpan _duration;
    private DateTimeOffset _timelineUpdated;
    private DateTime _lastPlayingUtc = DateTime.MinValue;

    private string _title = "";
    private string _artist = "";
    private ImageSource? _artwork;
    private Brush _accentBrush = Palette.DefaultAccent;
    private bool _isPlaying;
    private bool _isActive;
    private bool _canSkipNext;
    private bool _canSkipPrevious;
    private bool _hasTimeline;
    private double _progress;
    private string _positionText = "";
    private string _remainingText = "";
    private ImageSource? _appIcon;
    private string _sourceAppId = "";

    public MediaService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _lingerTimer = new DispatcherTimer(DispatcherPriority.Background);
        _lingerTimer.Tick += (_, _) =>
        {
            _lingerTimer.Stop();
            UpdateActive();
        };

        // Browser tabs are looked up a moment after a track settles, to name the site.
        _tabProbeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.2) };
        _tabProbeTimer.Tick += (_, _) =>
        {
            _tabProbeTimer.Stop();
            _ = ProbeTabAsync();
        };
    }

    /// <summary>Raised (on the UI thread) when a new track starts, after its artwork loaded.</summary>
    public event Action? TrackChanged;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Artist { get => _artist; private set => Set(ref _artist, value); }
    public ImageSource? Artwork { get => _artwork; private set => Set(ref _artwork, value); }
    public Brush AccentBrush { get => _accentBrush; private set => Set(ref _accentBrush, value); }
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }

    /// <summary>True when the island should show music (playing, or paused very recently).</summary>
    public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }

    public bool CanSkipNext { get => _canSkipNext; private set => Set(ref _canSkipNext, value); }
    public bool CanSkipPrevious { get => _canSkipPrevious; private set => Set(ref _canSkipPrevious, value); }
    public bool HasTimeline { get => _hasTimeline; private set => Set(ref _hasTimeline, value); }

    /// <summary>A player is open with a track loaded (playing or paused for any length of time).</summary>
    public bool HasMedia { get => _hasMedia; private set => Set(ref _hasMedia, value); }

    /// <summary>The player accepts seek requests, so the progress bar can be dragged.</summary>
    public bool CanSeek { get => _canSeek; private set => Set(ref _canSeek, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string PositionText { get => _positionText; private set => Set(ref _positionText, value); }
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }

    /// <summary>Icon of the app that is playing (Spotify, Chrome, ...), straight from Windows.</summary>
    public ImageSource? AppIcon { get => _appIcon; private set => Set(ref _appIcon, value); }

    /// <summary>Windows app identity of the playing app.</summary>
    public string SourceAppId { get => _sourceAppId; private set => Set(ref _sourceAppId, value); }

    /// <summary>The playing app: browser, app or installed web app.</summary>
    public MediaSourceInfo Source => _source;

    /// <summary>"JioHotstar · Edge", "YouTube Music · Chrome", "Spotify".</summary>
    public string SourceLabel { get => _sourceLabel; private set => Set(ref _sourceLabel, value); }

    /// <summary>Tooltip for the artwork: where clicking it goes.</summary>
    public string OpenHint { get => _openHint; private set => Set(ref _openHint, value); }

    public MediaKind Kind
    {
        get => _kind;
        private set
        {
            if (Set(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsVideo));
            }
        }
    }

    public bool IsVideo => _kind == MediaKind.Video;

    /// <summary>Every media session Windows reports, for the switcher.</summary>
    public ObservableCollection<MediaSessionInfo> Sessions { get; } = new();

    public bool HasMultipleSessions => Sessions.Count > 1;

    /// <summary>Show a specific session; it stays until another one starts playing.</summary>
    public void SelectSessionByUser(MediaSessionInfo info)
    {
        if (!_sessions.Any(s => ReferenceEquals(s, info.Session)))
        {
            return;
        }
        _userPick = info.Session;
        Apply(info.Session);
    }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Media session manager unavailable", ex);
            return;
        }

        _manager.SessionsChanged += (_, _) => Post(RefreshSessions);
        _manager.CurrentSessionChanged += (_, _) => Post(SelectSession);
        RefreshSessions();
        Log.Info($"Media: {_sessions.Count} session(s) found");
    }

    // ---------------------------------------------------------------- commands

    public async Task TogglePlayPauseAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }
        // Playing until this very moment (or about to play): the pause linger starts now.
        _lastPlayingUtc = DateTime.UtcNow;
        // Optimistic flip so the button responds instantly; the real event confirms it.
        IsPlaying = !IsPlaying;
        UpdateActive();
        try
        {
            await session.TryTogglePlayPauseAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Play/pause failed", ex);
            UpdatePlayback();
        }
    }

    public async Task SkipNextAsync()
    {
        try
        {
            if (_session is { } s) await s.TrySkipNextAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Skip next failed", ex);
        }
    }

    public async Task SkipPreviousAsync()
    {
        try
        {
            if (_session is { } s) await s.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Skip previous failed", ex);
        }
    }

    /// <summary>Go back to where the media is playing: the exact browser tab, window or app.</summary>
    public void OpenPlayer() => _ = ReturnToSourceAsync();

    public async Task ReturnToSourceAsync()
    {
        var source = _source;
        if (source.AppId.Length == 0)
        {
            return;
        }
        var hint = CurrentHint();
        try
        {
            if (source.IsBrowser)
            {
                // Find the tab by what is playing, switch to it, then raise its window.
                var match = await Task.Run(() => BrowserMediaIntegration.FindTab(source.ProcessNames, hint, select: true));
                if (match is not null)
                {
                    WindowActivator.Focus(match.Window);
                    Log.Info($"Media: back to tab '{Shorten(match.TabName)}' in {source.ShortName}");
                    return;
                }
                if (WindowActivator.FocusProcessWindow(source.ProcessNames, hint))
                {
                    Log.Info($"Media: playing tab not identified, focused the best {source.ShortName} window");
                    return;
                }
                Log.Warn($"Media: no {source.ShortName} window is open");
                return;
            }

            if (source.IsPackaged || source.IsWebApp)
            {
                AppLauncher.Activate(source.AppId); // the shell brings the existing window forward
                return;
            }
            if (WindowActivator.FocusProcessWindow(source.ProcessNames, hint)
                || WindowActivator.ReopenSingleInstance(source.ProcessNames))
            {
                return;
            }
            AppLauncher.Activate(source.AppId);
        }
        catch (Exception ex)
        {
            Log.Warn($"Returning to {source.AppName} failed", ex);
        }
    }

    private MediaHint CurrentHint() => new(_title, _artist, _service, _isPlaying);

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "...";

    // ---------------------------------------------------------------- seeking

    /// <summary>The user grabbed the progress bar: stop the playhead from moving underneath them.</summary>
    public void BeginScrub()
    {
        if (_duration > TimeSpan.Zero)
        {
            _scrubbing = true;
        }
    }

    /// <summary>Preview a position (0..1) while dragging, without telling the player yet.</summary>
    public void UpdateScrub(double fraction)
    {
        if (!_scrubbing)
        {
            return;
        }
        fraction = Math.Clamp(fraction, 0, 1);
        var position = TimeSpan.FromTicks((long)(_duration.Ticks * fraction));
        Progress = fraction;
        PositionText = Format(position);
        RemainingText = "-" + Format(_duration - position);
    }

    /// <summary>Released: jump the player to the chosen position.</summary>
    public async Task EndScrubAsync(double fraction)
    {
        if (!_scrubbing)
        {
            return;
        }
        _scrubbing = false;

        var session = _session;
        fraction = Math.Clamp(fraction, 0, 1);
        var target = TimeSpan.FromTicks((long)(_duration.Ticks * fraction));

        // Show the new position immediately; the player's own event confirms it shortly.
        _position = _start + target;
        _timelineUpdated = DateTimeOffset.Now;
        RefreshPosition();

        if (session is null)
        {
            return;
        }
        try
        {
            await session.TryChangePlaybackPositionAsync((_start + target).Ticks);
        }
        catch (Exception ex)
        {
            Log.Warn("Seek failed", ex);
        }
    }

    /// <summary>Recomputes the interpolated playhead. Called by the expanded view while visible.</summary>
    public void RefreshPosition()
    {
        if (_scrubbing)
        {
            return; // the user's finger wins while dragging
        }
        if (_duration <= TimeSpan.Zero)
        {
            HasTimeline = false;
            Progress = 0;
            PositionText = "";
            RemainingText = "";
            return;
        }

        var position = _position - _start;
        // Many apps only report position on state changes, so extrapolate while playing.
        if (_isPlaying && _timelineUpdated.Year > 2000)
        {
            position += DateTimeOffset.Now - _timelineUpdated;
        }
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > _duration) position = _duration;

        HasTimeline = true;
        Progress = position.TotalMilliseconds / _duration.TotalMilliseconds;
        PositionText = Format(position);
        RemainingText = "-" + Format(_duration - position);
    }

    // ---------------------------------------------------------------- sessions

    private void Post(Action action) => _dispatcher.InvokeAsync(action);

    private void RefreshSessions()
    {
        if (_manager is null)
        {
            return;
        }

        var latest = new List<GlobalSystemMediaTransportControlsSession>();
        try
        {
            latest.AddRange(_manager.GetSessions());
        }
        catch (Exception ex)
        {
            Log.Warn("GetSessions failed", ex);
        }

        // Sessions are matched by object identity, so two tabs of one browser never merge.
        foreach (var gone in _sessions.Where(s => !latest.Any(l => ReferenceEquals(l, s))).ToList())
        {
            Unhook(gone);
            _wasPlaying.Remove(gone);
        }
        foreach (var added in latest.Where(l => !_sessions.Any(s => ReferenceEquals(l, s))))
        {
            Hook(added);
            _wasPlaying[added] = IsPlayingNow(added);
        }
        _sessions.Clear();
        _sessions.AddRange(latest);

        if (_userPick is not null && !_sessions.Any(s => ReferenceEquals(s, _userPick)))
        {
            _userPick = null; // the session the user picked has ended
        }
        SyncSessionList();
        SelectSession();
    }

    private void SelectSession()
    {
        if (_manager is null)
        {
            return;
        }
        GlobalSystemMediaTransportControlsSession? pick = null;
        try
        {
            pick = ChooseSession();
        }
        catch (Exception ex)
        {
            Log.Warn("Session selection failed", ex);
        }
        Apply(pick);
    }

    /// <summary>
    /// Which session to show: the user's pick, else the current one while it plays (or while
    /// nothing else does), else whatever is playing, else Windows' own choice.
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? ChooseSession()
    {
        if (_userPick is not null)
        {
            return _userPick;
        }
        var current = Find(_session);
        bool anyPlaying = _sessions.Any(IsPlayingNow);
        if (current is not null && (IsPlayingNow(current) || !anyPlaying))
        {
            return current;
        }
        return _sessions.FirstOrDefault(IsPlayingNow)
            ?? current
            ?? Find(_manager?.GetCurrentSession())
            ?? _sessions.FirstOrDefault();
    }

    private void Apply(GlobalSystemMediaTransportControlsSession? pick)
    {
        bool changed = !SameSession(pick, _session);
        _session = pick;
        foreach (var info in Sessions)
        {
            info.IsSelected = pick is not null && ReferenceEquals(info.Session, pick);
        }

        if (pick is null)
        {
            _source = MediaSourceInfo.None;
            _service = null;
            SourceAppId = "";
            AppIcon = null;
            UpdateSourceLabels();
            ClearMedia();
            return;
        }

        if (changed)
        {
            string appId = SafeAppId(pick);
            _source = MediaSourceDetector.Describe(appId);
            _service = null;
            _serviceKind = MediaKind.Unknown;
            _reportedKind = MediaKind.Unknown;
            SourceAppId = appId;
            AppIcon = AppIconProvider.ForAppUserModelId(appId);
            Log.Info($"Media: following {_source.AppName} ({appId}){(_source.IsBrowser ? ", browser" : "")}");
            UpdateSourceLabels();
            _ = LoadMediaPropertiesAsync();
        }
        UpdatePlayback();
        UpdateTimeline();
    }

    private void SyncSessionList()
    {
        var infos = _sessions
            .Select(s => Sessions.FirstOrDefault(i => ReferenceEquals(i.Session, s)) ?? CreateInfo(s))
            .ToList();
        if (infos.SequenceEqual(Sessions))
        {
            return;
        }
        Sessions.Clear();
        foreach (var info in infos)
        {
            Sessions.Add(info);
        }
        OnPropertyChanged(nameof(HasMultipleSessions));
    }

    private MediaSessionInfo CreateInfo(GlobalSystemMediaTransportControlsSession session)
    {
        var source = MediaSourceDetector.Describe(SafeAppId(session));
        var info = new MediaSessionInfo(session, source, AppIconProvider.ForAppUserModelId(source.AppId))
        {
            IsPlaying = IsPlayingNow(session),
        };
        _ = LoadInfoAsync(info);
        return info;
    }

    private static async Task LoadInfoAsync(MediaSessionInfo info)
    {
        try
        {
            var props = await info.Session.TryGetMediaPropertiesAsync();
            if (props is null)
            {
                return;
            }
            info.Title = props.Title ?? "";
            info.Artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            info.Thumbnail = await ImageLoader.LoadAsync(props.Thumbnail, 64);
        }
        catch (Exception ex)
        {
            Log.Warn("Reading a media session failed", ex);
        }
    }

    private MediaSessionInfo? InfoFor(GlobalSystemMediaTransportControlsSession session) =>
        Sessions.FirstOrDefault(i => ReferenceEquals(i.Session, session));

    private GlobalSystemMediaTransportControlsSession? Find(GlobalSystemMediaTransportControlsSession? session) =>
        session is null ? null : _sessions.FirstOrDefault(s => SameSession(s, session));

    /// <summary>A session that started playing takes over the island, like iOS.</summary>
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender)
    {
        bool now = IsPlayingNow(sender);
        bool before = _wasPlaying.TryGetValue(sender, out bool was) && was;
        _wasPlaying[sender] = now;
        if (InfoFor(sender) is { } info)
        {
            info.IsPlaying = now;
        }

        if (IsCurrent(sender))
        {
            UpdatePlayback();
        }
        else if (now && !before)
        {
            _userPick = null;
            Apply(sender);
        }
    }

    // ---------------------------------------------------------------- source

    private void UpdateSourceLabels()
    {
        var source = _source;
        var (appService, appKind) = MediaSourceDetector.ServiceFrom(source.AppName);
        string? service = _service ?? (source.IsBrowser ? null : appService);
        var serviceKind = _service is not null ? _serviceKind : appKind;
        Kind = serviceKind != MediaKind.Unknown ? serviceKind : _reportedKind;

        if (source.AppId.Length == 0)
        {
            SourceLabel = "";
            OpenHint = "Open player";
        }
        else if (source.IsBrowser)
        {
            SourceLabel = service is null ? source.AppName : $"{service} \u00B7 {source.ShortName}";
            OpenHint = service is null ? $"Go to the playing tab in {source.ShortName}" : $"Go to {service} in {source.ShortName}";
        }
        else
        {
            SourceLabel = source.AppName;
            OpenHint = $"Open {source.AppName}";
        }
    }

    /// <summary>Read the browser's tab strip to name the site that is playing.</summary>
    private async Task ProbeTabAsync()
    {
        var source = _source;
        if (!source.IsBrowser || _title.Length == 0)
        {
            return;
        }
        int version = ++_probeVersion;
        var hint = CurrentHint();
        TabMatch? match = null;
        try
        {
            match = await Task.Run(() => BrowserMediaIntegration.FindTab(source.ProcessNames, hint, select: false));
        }
        catch (Exception ex)
        {
            Log.Warn("Browser tab lookup failed", ex);
        }
        if (version != _probeVersion || !ReferenceEquals(source, _source) || match is null)
        {
            return;
        }
        var (service, kind) = MediaSourceDetector.ServiceFrom(match.TabName);
        if (service is not null)
        {
            _service = service;
            _serviceKind = kind;
            UpdateSourceLabels();
        }
    }

    private void Hook(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            s.MediaPropertiesChanged += OnMediaPropertiesChanged;
            s.PlaybackInfoChanged += OnPlaybackInfoChanged;
            s.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not subscribe to media session", ex);
        }
    }

    private void Unhook(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            s.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            s.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            s.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch
        {
            // Session already gone.
        }
    }

    // WinRT raises these on thread pool threads; always hop to the UI thread.
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        Post(() =>
        {
            if (InfoFor(sender) is { } info) _ = LoadInfoAsync(info);
            if (IsCurrent(sender)) _ = LoadMediaPropertiesAsync();
        });

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        Post(() => OnPlaybackChanged(sender));

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        Post(() =>
        {
            if (IsCurrent(sender)) UpdateTimeline();
        });

    // ---------------------------------------------------------------- state

    private async Task LoadMediaPropertiesAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        int version = ++_propertiesVersion;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _propertiesVersion || props is null)
            {
                return;
            }

            string title = props.Title ?? "";
            string artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            bool trackChanged = title != _title || artist != _artist;

            // Music or video, and the service when the media names it (album, artist).
            // A new track in a browser may come from another site, so the old site is dropped.
            _reportedKind = MediaSourceDetector.KindFrom(props.PlaybackType);
            if (trackChanged)
            {
                _service = null;
                _serviceKind = MediaKind.Unknown;
            }
            var (service, serviceKind) = MediaSourceDetector.ServiceFrom(title, artist, props.AlbumTitle);
            if (service is not null)
            {
                _service = service;
                _serviceKind = serviceKind;
            }

            // Apps often send text first and artwork a moment later, so always reload the art.
            var art = await ImageLoader.LoadAsync(props.Thumbnail, 192);
            if (version != _propertiesVersion)
            {
                return;
            }

            Title = title;
            Artist = artist;
            UpdateSourceLabels();
            if (_source.IsBrowser && (trackChanged || _service is null))
            {
                _tabProbeTimer.Stop();
                _tabProbeTimer.Start();
            }
            if (art is not null || trackChanged)
            {
                Artwork = art;
                AccentBrush = art is null ? Palette.DefaultAccent : Palette.Make(ImageLoader.ExtractAccent(art));
            }

            UpdateActive();

            if (trackChanged && title.Length > 0)
            {
                if (_hadTrack)
                {
                    TrackChanged?.Invoke();
                }
                _hadTrack = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Reading media properties failed", ex);
        }
    }

    private void UpdatePlayback()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            var info = session.GetPlaybackInfo();
            bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            CanSkipNext = info?.Controls?.IsNextEnabled ?? false;
            CanSkipPrevious = info?.Controls?.IsPreviousEnabled ?? false;
            CanSeek = info?.Controls?.IsPlaybackPositionEnabled ?? false;
            // If it was playing until now (or still is), the pause linger counts from now.
            if (playing || _isPlaying)
            {
                _lastPlayingUtc = DateTime.UtcNow;
            }
            IsPlaying = playing;
        }
        catch (Exception ex)
        {
            Log.Warn("Reading playback info failed", ex);
        }

        UpdateActive();
        RefreshPosition();
    }

    private void UpdateTimeline()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            var timeline = session.GetTimelineProperties();
            _position = timeline.Position;
            _start = timeline.StartTime;
            _duration = timeline.EndTime - timeline.StartTime;
            _timelineUpdated = timeline.LastUpdatedTime;
        }
        catch (Exception ex)
        {
            Log.Warn("Reading timeline failed", ex);
            _duration = TimeSpan.Zero;
        }
        RefreshPosition();
    }

    private void UpdateActive()
    {
        var sincePlaying = DateTime.UtcNow - _lastPlayingUtc;
        bool active = _session is not null
            && _title.Length > 0
            && (_isPlaying || sincePlaying < PausedLinger);
        IsActive = active;
        HasMedia = _session is not null && _title.Length > 0;

        _lingerTimer.Stop();
        if (active && !_isPlaying)
        {
            var remaining = PausedLinger - sincePlaying + TimeSpan.FromMilliseconds(100);
            _lingerTimer.Interval = remaining > TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100);
            _lingerTimer.Start();
        }
    }

    private void ClearMedia()
    {
        _propertiesVersion++;
        Title = "";
        Artist = "";
        Artwork = null;
        AccentBrush = Palette.DefaultAccent;
        IsPlaying = false;
        _duration = TimeSpan.Zero;
        RefreshPosition();
        UpdateActive();
    }

    // ---------------------------------------------------------------- helpers

    private bool IsCurrent(GlobalSystemMediaTransportControlsSession sender) => SameSession(sender, _session);

    /// <summary>
    /// Same session object. Falls back to the app id only when that app has a single session,
    /// so a YouTube tab and a JioHotstar tab in the same browser are never confused.
    /// </summary>
    private bool SameSession(GlobalSystemMediaTransportControlsSession? a, GlobalSystemMediaTransportControlsSession? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        string id = SafeAppId(a);
        return id.Length > 0
            && id == SafeAppId(b)
            && _sessions.Count(s => SafeAppId(s) == id) <= 1;
    }

    private static bool IsPlayingNow(GlobalSystemMediaTransportControlsSession s) =>
        StatusOf(s) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    private static string SafeAppId(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            return s.SourceAppUserModelId ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static GlobalSystemMediaTransportControlsSessionPlaybackStatus? StatusOf(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            return s.GetPlaybackInfo()?.PlaybackStatus;
        }
        catch
        {
            return null;
        }
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    public void Dispose()
    {
        _lingerTimer.Stop();
        _tabProbeTimer.Stop();
        foreach (var s in _sessions)
        {
            Unhook(s);
        }
        _sessions.Clear();
    }
}
