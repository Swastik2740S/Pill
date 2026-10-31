using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicIsland.Core;

public enum IslandStyle
{
    /// <summary>Frosted glass: live background blur, smoky tint, sheen and rim light.</summary>
    Glass,
    /// <summary>Classic iPhone look: pure black.</summary>
    Solid,
}

/// <summary>What the island does when the window you are using reaches under it.</summary>
public enum CoverBehavior
{
    /// <summary>Shrink to a thin click-through line so tabs and title bars underneath work.</summary>
    TuckAway,
    /// <summary>Always stay as it is, on top of everything.</summary>
    StayOnTop,
}

/// <summary>User preferences, stored in %LOCALAPPDATA%\DynamicIsland\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");
    private static readonly string SettingsFile = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public IslandStyle Style { get; set; } = IslandStyle.Glass;

    /// <summary>Global shortcut that opens and closes the island, e.g. "Ctrl+Alt+Space".</summary>
    public string Shortcut { get; set; } = DefaultShortcut;

    public const string DefaultShortcut = "Ctrl+Alt+Space";

    public CoverBehavior WhenCovered { get; set; } = CoverBehavior.TuckAway;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Settings unreadable, using defaults", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save settings", ex);
        }
    }
}
