using System.Windows.Media;

namespace DynamicIsland.Core;

/// <summary>The four shapes the island can take.</summary>
public enum IslandMode
{
    /// <summary>Small resting pill.</summary>
    Idle,
    /// <summary>Wider pill showing live activities (music, timer).</summary>
    Compact,
    /// <summary>Large card shown on hover or click.</summary>
    Expanded,
    /// <summary>Short-lived banner for system events (volume, battery, Bluetooth, timer done).</summary>
    Toast,
    /// <summary>Brief stretch of the pill announcing a notification, then it settles into a bubble.</summary>
    Peek,
    /// <summary>The Settings panel: the island grows into a large card with a sidebar and search.</summary>
    Settings,
    /// <summary>A window you are using sits under the island: it becomes a thin click-through line.</summary>
    Tucked,
}

/// <summary>Island geometry in device independent pixels.</summary>
public static class IslandMetrics
{
    // Fixed transparent canvas; big enough for the expanded dashboard and the mini bubbles.
    // As wide as the Settings canvas, so switching to and from Settings only changes the height
    // (anchored at the top): a sideways resize shows one stale, shifted frame, a visible jolt.
    public const double WindowWidth = 800;
    public const double WindowHeight = 470;
    public const double TopMargin = 6;

    public const double IdleWidth = 124;
    public const double IdleHeight = 30;

    public const double CompactHeight = 34;

    public const double ToastHeight = 48;

    // Tucked: a thin line hugging the top edge (moved up by TuckedLift from the normal top margin).
    public const double TuckedWidth = 96;
    public const double TuckedHeight = 4;
    public const double TuckedLift = 4;
    // With music, a timer or waiting apps it grows into a slim mini bar (width follows its content).
    public const double TuckedBarHeight = 16;

    // Settings panel, and the larger window canvas used only while it is open.
    public const double SettingsWidth = 760;
    public const double SettingsHeight = 566;
    public const double SettingsRadius = 28;
    public const double SettingsCanvasWidth = 800;
    public const double SettingsCanvasHeight = 606;

    public const double ExpandedWidth = 400;
    public const double ExpandedRadius = 30;

    public const double PeekWidth = 360;
    public const double PeekHeight = 46;

    public const double BubbleSize = 30;
    public const double BubbleGap = 6;
    public const double BubblePillGap = 7;
    public const int MaxBubbles = 3;
}

public enum ToastKind
{
    Volume,
    Battery,
    Bluetooth,
    Notification,
    Media,
    Timer,
    Update,
}

/// <summary>Everything a toast banner needs to render. Immutable, so it can be replaced atomically.</summary>
public sealed record ToastContent(ToastKind Kind, string Title)
{
    public string? Subtitle { get; init; }
    public string? Glyph { get; init; }
    public Brush Accent { get; init; } = Palette.White;
    public ImageSource? Image { get; init; }
    /// <summary>When set, a level bar (0..1) replaces the text block. Used for volume.</summary>
    public double? Level { get; init; }
    public string? Trailing { get; init; }
    /// <summary>When set, a battery glyph (0..1) is drawn at the trailing edge.</summary>
    public double? Battery { get; init; }
    public bool Charging { get; init; }
    public double Width { get; init; } = 320;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(2.6);
}

/// <summary>One Windows notification as read from the notification listener.</summary>
public sealed record NotificationInfo(
    uint Id,
    string AppId,
    string App,
    string Title,
    string Body,
    ImageSource? Logo,
    DateTimeOffset Time);
