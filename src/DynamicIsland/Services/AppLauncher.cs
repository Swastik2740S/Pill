using System.Diagnostics;
using System.IO;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>
/// Brings an app to the front from its AppUserModelId (what media sessions and notifications
/// report). Store apps and installed web apps go through the shell, which raises their existing
/// window; classic apps get their most recently used window focused, so nothing opens twice.
/// Media uses the more precise <see cref="MediaService.ReturnToSourceAsync"/> path first.
/// </summary>
public static class AppLauncher
{
    public static void Activate(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return;
        }

        // Classic apps ("Spotify.exe", "MSEdge"): focus the most recent window of that process.
        // Web apps ("MSEdge._crx_...") share the browser's process, so they must use the shell.
        bool shellOnly = appId.Contains('!') || appId.Contains("_crx_", StringComparison.OrdinalIgnoreCase);
        if (!shellOnly)
        {
            string process = appId.Contains('\\') ? Path.GetFileNameWithoutExtension(appId)
                : appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? appId[..^4]
                : appId;
            if (WindowActivator.FocusProcessWindow(new[] { process }, new MediaHint("", "", null, false)))
            {
                return;
            }
        }

        // Store apps, web apps, and anything not running: the shell activates or launches it.
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {appId}", ex);
        }
    }
}
