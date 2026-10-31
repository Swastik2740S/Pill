using System.Windows.Automation;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>What we know about the playing media, used to recognise its tab or window.</summary>
public sealed record MediaHint(string Title, string Artist, string? Service, bool IsPlaying);

/// <summary>Scores how well a tab or window title matches the playing media.</summary>
internal static class MediaMatcher
{
    public const int Threshold = 50;

    public static int Score(string candidate, MediaHint hint)
    {
        string name = MediaSourceDetector.Normalize(candidate);
        if (name.Length == 0)
        {
            return 0;
        }

        int score = 0;
        string title = MediaSourceDetector.Normalize(hint.Title);
        if (title.Length >= 3 && name.Contains(title, StringComparison.Ordinal))
        {
            score += 100 + Math.Min(title.Length, 40);
        }
        else if (title.Length > 0)
        {
            var words = title.Split(' ').Where(w => w.Length >= 3).Distinct().ToList();
            if (words.Count > 0)
            {
                var nameWords = new HashSet<string>(name.Split(' '));
                double share = words.Count(nameWords.Contains) / (double)words.Count;
                score += (int)(share * 80);
            }
        }

        string artist = MediaSourceDetector.Normalize(hint.Artist);
        if (artist.Length >= 3 && name.Contains(artist, StringComparison.Ordinal))
        {
            score += 15;
        }

        if (hint.Service is { } service
            && MediaSourceDetector.ServiceFrom(candidate).Name == service)
        {
            score += 25;
        }

        // Chromium and Firefox add an audio state to the tab's accessible name.
        if (hint.IsPlaying && (name.Contains("audio playing") || name.Contains("playing audio")))
        {
            score += 30;
        }
        return score;
    }
}

/// <summary>A browser tab that matches the media, and the window it lives in.</summary>
internal sealed record TabMatch(IntPtr Window, string TabName, int Score);

/// <summary>
/// Finds the browser tab that is playing, through Windows UI Automation (the accessibility API
/// browsers support for screen readers). Only the browser's own tab strip is read; web page
/// content is never walked, so the browser is not pushed into full accessibility mode.
/// Works with Edge, Chrome, Brave, Opera, Vivaldi and Firefox. No extension needed.
/// Call off the UI thread: each step is a cross-process call.
/// </summary>
internal static class BrowserMediaIntegration
{
    private const int MaxDepth = 14;
    private const int MaxNodes = 1500;

    /// <summary>Best matching tab across all windows of the browser, or null.</summary>
    /// <param name="select">Also switch the browser to that tab.</param>
    public static TabMatch? FindTab(IReadOnlyCollection<string> browserProcesses, MediaHint hint, bool select)
    {
        TabMatch? best = null;
        AutomationElement? bestTab = null;

        foreach (var window in WindowActivator.FindWindows(browserProcesses))
        {
            AutomationElement root;
            try
            {
                root = AutomationElement.FromHandle(window.Handle);
            }
            catch (Exception ex)
            {
                Log.Warn("UI Automation could not open a browser window", ex);
                continue;
            }

            foreach (var tab in TabsOf(root))
            {
                string name;
                try
                {
                    name = tab.Current.Name ?? "";
                }
                catch
                {
                    continue;
                }
                int score = MediaMatcher.Score(name, hint);
                if (best is null || score > best.Score)
                {
                    best = new TabMatch(window.Handle, name, score);
                    bestTab = tab;
                }
            }
        }

        if (best is null || best.Score < MediaMatcher.Threshold)
        {
            return null;
        }
        if (select && bestTab is not null && !Select(bestTab))
        {
            Log.Warn($"Found tab '{best.TabName}' but the browser did not let us switch to it");
        }
        return best;
    }

    /// <summary>Tab items of the browser's tab strip, breadth first, skipping web content.</summary>
    private static IEnumerable<AutomationElement> TabsOf(AutomationElement root)
    {
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        int visited = 0;

        while (queue.Count > 0 && visited < MaxNodes)
        {
            var (element, depth) = queue.Dequeue();
            visited++;

            ControlType type;
            try
            {
                type = element.Current.ControlType;
            }
            catch
            {
                continue;
            }

            if (type == ControlType.Document)
            {
                continue; // a web page: never walk into it
            }
            if (type == ControlType.TabItem)
            {
                yield return element;
                continue;
            }
            if (depth >= MaxDepth)
            {
                continue;
            }

            AutomationElement? child = null;
            try
            {
                child = walker.GetFirstChild(element);
            }
            catch
            {
                // Element went away while walking.
            }
            while (child is not null)
            {
                queue.Enqueue((child, depth + 1));
                try
                {
                    child = walker.GetNextSibling(child);
                }
                catch
                {
                    child = null;
                }
            }
        }
    }

    private static bool Select(AutomationElement tab)
    {
        try
        {
            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selection))
            {
                ((SelectionItemPattern)selection).Select();
                return true;
            }
            if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out object invoke))
            {
                ((InvokePattern)invoke).Invoke();
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Switching browser tab failed", ex);
        }
        return false;
    }
}
