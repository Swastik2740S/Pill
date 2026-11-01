using System.Windows.Media;
using DynamicIsland.Core;
using Windows.Media.Control;

namespace DynamicIsland.Services;

/// <summary>
/// One media session Windows knows about (a player, or a browser's media), for the session
/// switcher on the Music card. Identity is the session object itself, so two sessions from
/// the same browser stay separate.
/// </summary>
public sealed class MediaSessionInfo : ObservableObject
{
    private string _title = "";
    private string _artist = "";
    private ImageSource? _thumbnail;
    private bool _isPlaying;
    private bool _isSelected;

    internal MediaSessionInfo(GlobalSystemMediaTransportControlsSession session, MediaSourceInfo source, ImageSource? icon)
    {
        Session = session;
        Source = source;
        Icon = icon;
    }

    internal GlobalSystemMediaTransportControlsSession Session { get; }
    public MediaSourceInfo Source { get; }
    public ImageSource? Icon { get; }

    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, value))
            {
                OnPropertyChanged(nameof(Tooltip));
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public string Artist
    {
        get => _artist;
        set
        {
            if (Set(ref _artist, value))
            {
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    /// <summary>Row title in "Also playing": the track or video, or the app when it has none.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(_title) ? Source.AppName : _title;

    /// <summary>Row subtitle: artist and app ("The Weeknd · Spotify").</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(_artist) ? Source.AppName : $"{_artist} \u00B7 {Source.AppName}";

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (Set(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(ShowThumbnail));
                OnPropertyChanged(nameof(ShowIcon));
            }
        }
    }

    public bool ShowThumbnail => _thumbnail is not null;
    public bool ShowIcon => _thumbnail is null;

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (Set(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(Tooltip));
            }
        }
    }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public string Tooltip =>
        (string.IsNullOrEmpty(_title) ? Source.AppName : $"{_title} · {Source.AppName}")
        + (_isPlaying ? " (playing)" : "");
}
