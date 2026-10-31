using System.Windows.Media;

namespace DynamicIsland.Core;

public enum ActivityType
{
    Media,
    Timer,
    Notifications,
}

/// <summary>How long an activity naturally lives.</summary>
public enum ActivityLifetime
{
    /// <summary>Lives while something is happening: music playing, a timer running.</summary>
    Persistent,
    /// <summary>Arrives, waits to be seen, then goes away: messages, emails.</summary>
    Temporary,
    /// <summary>The user is doing something in it: reading or replying to an email.</summary>
    Interactive,
}

public enum ActivityState
{
    Active,
    Background,
    Minimized,
    Completed,
    Dismissed,
}

/// <summary>
/// One thing the island is tracking. Services never touch the UI; they create and update
/// activities, and the presentation layer decides where each one appears (pill, bubble,
/// peek or expanded card).
/// </summary>
public sealed class Activity : ObservableObject
{
    private int _priority;
    private ActivityState _state = ActivityState.Active;
    private ImageSource? _icon;
    private int _badge;
    private string _title = "";
    private string _subtitle = "";
    private string _sourceName = "";
    private DateTimeOffset _updatedAt;

    public Activity(string id, ActivityType type, ActivityLifetime lifetime, int priority, string sourceAppId = "")
    {
        Id = id;
        Type = type;
        Lifetime = lifetime;
        SourceAppId = sourceAppId;
        _priority = priority;
        CreatedAt = DateTimeOffset.Now;
        _updatedAt = CreatedAt;
    }

    public string Id { get; }
    public ActivityType Type { get; }
    public ActivityLifetime Lifetime { get; }

    /// <summary>Windows app identity (AppUserModelId) of the source, when known.</summary>
    public string SourceAppId { get; }

    public DateTimeOffset CreatedAt { get; }

    public string SourceName { get => _sourceName; set => Set(ref _sourceName, value); }
    public int Priority { get => _priority; set => Set(ref _priority, value); }
    public ActivityState State { get => _state; set => Set(ref _state, value); }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    /// <summary>Unread count shown on the bubble.</summary>
    public int Badge { get => _badge; set => Set(ref _badge, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }
    public DateTimeOffset UpdatedAt { get => _updatedAt; set => Set(ref _updatedAt, value); }

    /// <summary>The service object behind this activity (MediaService, NotificationGroup, ...).</summary>
    public object? Data { get; set; }
}

/// <summary>
/// Central registry of everything live on the island. Activities are keyed by id, so an
/// update to one can never overwrite another.
/// </summary>
public sealed class ActivityManager
{
    private readonly Dictionary<string, Activity> _activities = new();

    /// <summary>Raised on the UI thread whenever an activity is added, changed or removed.</summary>
    public event Action? Changed;

    public Activity? Get(string id) => _activities.TryGetValue(id, out var activity) ? activity : null;

    /// <summary>Create the activity if needed, apply <paramref name="update"/>, stamp it, notify once.</summary>
    public Activity Upsert(string id, Func<Activity> create, Action<Activity> update)
    {
        if (!_activities.TryGetValue(id, out var activity))
        {
            activity = create();
            _activities[id] = activity;
        }
        update(activity);
        activity.UpdatedAt = DateTimeOffset.Now;
        Changed?.Invoke();
        return activity;
    }

    public void Remove(string id)
    {
        if (_activities.Remove(id))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Visible activities, most important first, newest first within a priority.</summary>
    public IReadOnlyList<Activity> Ordered() =>
        _activities.Values
            .Where(a => a.State is not ActivityState.Dismissed and not ActivityState.Completed)
            .OrderByDescending(a => a.Priority)
            .ThenByDescending(a => a.UpdatedAt)
            .ToList();

    public IReadOnlyList<Activity> OfType(ActivityType type) =>
        Ordered().Where(a => a.Type == type).ToList();
}
