using System.Diagnostics;
using System.IO;
using System.Text;
using Windows.Media;

namespace DynamicIsland.Services;

public enum MediaKind
{
    Unknown,
    Music,
    Video,
}

/// <summary>Who is playing: an app, a browser, or a web app installed from a browser.</summary>
public sealed record MediaSourceInfo(
    string AppId,
    string AppName,
    string ShortName,
    IReadOnlyList<string> ProcessNames,
    bool IsBrowser,
    bool IsPackaged,
    bool IsWebApp)
{
    public static readonly MediaSourceInfo None = new("", "", "", Array.Empty<string>(), false, false, false);
}

/// <summary>
/// Works out where media is coming from. Windows reports only the source app id (for a
/// browser that is just "MSEdge" or "Chrome"), so the site or service is recognised from the
/// media's own text and, for browsers, from the matching tab's title.
/// Nothing here maps to a fixed destination: every answer comes from the live session.
/// </summary>
public static class MediaSourceDetector
{
    private static readonly (string Key, string Name, string Short, string[] Processes)[] Browsers =
    {
        ("msedge", "Microsoft Edge", "Edge", new[] { "msedge" }),
        ("chrome", "Google Chrome", "Chrome", new[] { "chrome" }),
        ("brave", "Brave", "Brave", new[] { "brave" }),
        ("opera", "Opera", "Opera", new[] { "opera", "opera_gx" }),
        ("vivaldi", "Vivaldi", "Vivaldi", new[] { "vivaldi" }),
        ("firefox", "Firefox", "Firefox", new[] { "firefox" }),
        ("chromium", "Chromium", "Chromium", new[] { "chromium" }),
        ("arc", "Arc", "Arc", new[] { "arc" }),
    };

    // Longer names first, so "YouTube Music" wins over "YouTube".
    private static readonly (string Name, MediaKind Kind, string[] Keys)[] Services =
    {
        ("YouTube Music", MediaKind.Music, new[] { "youtube music", "music youtube" }),
        ("YouTube", MediaKind.Video, new[] { "youtube" }),
        ("JioHotstar", MediaKind.Video, new[] { "jiohotstar", "hotstar" }),
        ("Netflix", MediaKind.Video, new[] { "netflix" }),
        ("Prime Video", MediaKind.Video, new[] { "prime video", "primevideo", "amazon prime" }),
        ("Spotify", MediaKind.Music, new[] { "spotify" }),
        ("SoundCloud", MediaKind.Music, new[] { "soundcloud" }),
        ("JioSaavn", MediaKind.Music, new[] { "jiosaavn", "saavn" }),
        ("Apple Music", MediaKind.Music, new[] { "apple music" }),
        ("Gaana", MediaKind.Music, new[] { "gaana" }),
        ("Wynk Music", MediaKind.Music, new[] { "wynk" }),
        ("Twitch", MediaKind.Video, new[] { "twitch" }),
        ("Disney+", MediaKind.Video, new[] { "disney plus", "disneyplus" }),
        ("ZEE5", MediaKind.Video, new[] { "zee5" }),
        ("Sony LIV", MediaKind.Video, new[] { "sonyliv", "sony liv" }),
        ("VLC", MediaKind.Unknown, new[] { "vlc media player" }),
    };

    public static MediaSourceInfo Describe(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return MediaSourceInfo.None;
        }

        bool packaged = appId.Contains('!');
        bool webApp = appId.Contains("_crx_", StringComparison.OrdinalIgnoreCase);
        string baseName = BaseName(appId);

        foreach (var browser in Browsers)
        {
            bool matches = packaged
                ? appId.Contains(browser.Key, StringComparison.OrdinalIgnoreCase)
                : baseName.Equals(browser.Key, StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                continue;
            }
            if (webApp)
            {
                // An installed web app (YouTube, Hotstar...) has its own window and Start entry.
                string appName = AppIconProvider.DisplayName(appId) ?? browser.Name;
                return new MediaSourceInfo(appId, appName, appName, browser.Processes, false, packaged, true);
            }
            return new MediaSourceInfo(appId, browser.Name, browser.Short, browser.Processes, true, packaged, false);
        }

        // Firefox reports a 16 character hash of its install folder instead of a name.
        if (appId.Length == 16 && appId.All(Uri.IsHexDigit) && IsRunning("firefox"))
        {
            return new MediaSourceInfo(appId, "Firefox", "Firefox", new[] { "firefox" }, true, false, false);
        }

        string name = AppIconProvider.DisplayName(appId) ?? Pretty(baseName);
        var processes = packaged ? Array.Empty<string>() : new[] { baseName };
        return new MediaSourceInfo(appId, name, name, processes, false, packaged, false);
    }

    /// <summary>Recognise a known service from any of the given texts (title, artist, tab title).</summary>
    public static (string? Name, MediaKind Kind) ServiceFrom(params string?[] texts)
    {
        var joined = Normalize(string.Join(" ", texts.Where(t => !string.IsNullOrWhiteSpace(t))));
        if (joined.Length == 0)
        {
            return (null, MediaKind.Unknown);
        }
        foreach (var service in Services)
        {
            if (service.Keys.Any(k => ContainsWord(joined, k)))
            {
                return (service.Name, service.Kind);
            }
        }
        return (null, MediaKind.Unknown);
    }

    public static MediaKind KindFrom(MediaPlaybackType? type) => type switch
    {
        MediaPlaybackType.Music => MediaKind.Music,
        MediaPlaybackType.Video => MediaKind.Video,
        _ => MediaKind.Unknown,
    };

    /// <summary>Lowercase letters and digits separated by single spaces.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        var builder = new StringBuilder(text.Length);
        bool space = true;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (c == '+')
            {
                builder.Append(" plus");
                space = false;
            }
            else if (!space)
            {
                builder.Append(' ');
                space = true;
            }
        }
        return builder.ToString().Trim();
    }

    private static bool ContainsWord(string haystack, string phrase)
    {
        int index = haystack.IndexOf(phrase, StringComparison.Ordinal);
        while (index >= 0)
        {
            bool startOk = index == 0 || haystack[index - 1] == ' ';
            int end = index + phrase.Length;
            bool endOk = end == haystack.Length || haystack[end] == ' ';
            if (startOk && endOk)
            {
                return true;
            }
            index = haystack.IndexOf(phrase, index + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static string BaseName(string appId)
    {
        if (appId.Contains('\\') || appId.Contains('/'))
        {
            return Path.GetFileNameWithoutExtension(appId);
        }
        if (appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return appId[..^4];
        }
        int dot = appId.IndexOf('.');
        return dot > 0 ? appId[..dot] : appId;
    }

    private static string Pretty(string name) =>
        name.Length == 0 ? "Media" : char.ToUpperInvariant(name[0]) + name[1..];

    private static bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        bool running = processes.Length > 0;
        foreach (var p in processes)
        {
            p.Dispose();
        }
        return running;
    }
}
