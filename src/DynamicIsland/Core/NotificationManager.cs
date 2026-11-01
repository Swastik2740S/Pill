using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland.Core;

public sealed class NotificationItem
{
    public NotificationItem(uint systemId, string title, string body, DateTimeOffset time)
    {
        SystemId = systemId;
        Title = title;
        Body = body;
        Time = time;
    }

    /// <summary>Id in the Windows notification center (used to clear it there too).</summary>
    public uint SystemId { get; }
    public string Title { get; }
    public string Body { get; }
    public DateTimeOffset Time { get; }
    public string TimeText => TimeLabel.Relative(Time);
}

internal static class TimeLabel
{
    public static string Relative(DateTimeOffset time)
    {
        var age = DateTimeOffset.Now - time;
        if (age < TimeSpan.FromMinutes(1)) return "now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }
}

/// <summary>One line of the saved notification history.</summary>
public sealed class HistoryEntry
{
    public HistoryEntry(DateTimeOffset time, string app, string title, string body, System.Windows.Media.ImageSource? icon)
    {
        Time = time;
        App = app;
        Title = title;
        Body = body;
        Icon = icon;
        var local = time.ToLocalTime();
        TimeText = local.Date == DateTime.Today ? local.ToString("t") : local.ToString("ddd t");
    }

    public DateTimeOffset Time { get; }
    public string App { get; }
    public string Title { get; }
    public string Body { get; }
    public System.Windows.Media.ImageSource? Icon { get; }
    public string TimeText { get; }
}

/// <summary>All unread notifications from one app, newest first. Bound by the expanded view.</summary>
public sealed class NotificationGroup : ObservableObject
{
    private const int MaxItems = 25;
    private readonly List<NotificationItem> _items = new();
    private ImageSource? _icon;
    private string _countText = "";
    private string _latestLine = "";
    private string _timeText = "";
    private IReadOnlyList<NotificationItem> _topItems = Array.Empty<NotificationItem>();
    private bool _isExpanded;

    // Messaging apps go under "Conversations" in the notifications card, like One UI.
    private static readonly string[] ChatApps =
    {
        "whatsapp", "telegram", "signal", "messenger", "teams", "slack", "discord", "skype",
        "phone link", "your phone", "messages", "instagram", "wechat", "viber", "zoom",
    };

    public NotificationGroup(string key, string appId, string appName)
    {
        Key = key;
        AppId = appId;
        AppName = string.IsNullOrWhiteSpace(appName) ? "Notification" : appName;
        string haystack = $"{AppName} {appId}".ToLowerInvariant();
        IsConversation = ChatApps.Any(haystack.Contains);
    }

    /// <summary>A messaging app (shown under Conversations).</summary>
    public bool IsConversation { get; }

    /// <summary>Expanded in the notifications card (its recent messages are listed).</summary>
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    /// <summary>More than one message waiting (the count badge shows).</summary>
    public bool HasMore => _items.Count > 1;

    public string Key { get; }
    public string AppId { get; }
    public string AppName { get; }
    public IReadOnlyList<NotificationItem> Items => _items;
    public int Count => _items.Count;
    public NotificationItem? Latest => _items.Count > 0 ? _items[0] : null;

    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public string CountText { get => _countText; private set => Set(ref _countText, value); }
    public string LatestLine { get => _latestLine; private set => Set(ref _latestLine, value); }
    public string TimeText { get => _timeText; private set => Set(ref _timeText, value); }

    /// <summary>The newest few messages, shown in this app's dashboard card.</summary>
    public IReadOnlyList<NotificationItem> TopItems { get => _topItems; private set => Set(ref _topItems, value); }

    internal void Add(NotificationItem item)
    {
        _items.Insert(0, item);
        if (_items.Count > MaxItems)
        {
            _items.RemoveAt(_items.Count - 1);
        }
        Refresh();
    }

    internal bool RemoveSystemIds(ISet<uint> ids)
    {
        int removed = _items.RemoveAll(i => ids.Contains(i.SystemId));
        if (removed > 0)
        {
            Refresh();
        }
        return removed > 0;
    }

    internal bool Contains(uint systemId) => _items.Exists(i => i.SystemId == systemId);

    internal bool IsDuplicate(NotificationInfo info) =>
        _items.Exists(i => i.Title == info.Title && i.Body == info.Body && (info.Time - i.Time).Duration() < TimeSpan.FromSeconds(15));

    public void Refresh()
    {
        CountText = Count > 1 ? $"{Count} new" : "";
        var latest = Latest;
        LatestLine = latest is null ? ""
            : string.IsNullOrWhiteSpace(latest.Body) ? latest.Title
            : $"{latest.Title}: {latest.Body}";
        TimeText = latest is null ? "" : TimeLabel.Relative(latest.Time);
        // A new list instance each time, so bound item lists refresh (and times re-render).
        TopItems = _items.Take(NotificationManager.CardItemLimit).ToList();
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasMore));
    }
}

/// <summary>
/// Groups Windows notifications by app, removes duplicates, keeps unread counts and a local
/// history, mirrors each group into the activity manager, and stays in sync with the Windows
/// notification center (clearing there clears here, dismissing here clears there).
/// </summary>
public sealed class NotificationManager
{
    private const int VisibleGroupLimit = 3;
    public const int CardItemLimit = 4;

    private readonly NotificationService _service;
    private readonly ActivityManager _activities;
    private readonly NotificationHistory _history = new();
    private readonly Dictionary<string, NotificationGroup> _groups = new();
    private readonly List<NotificationGroup> _order = new(); // newest activity first

    public NotificationManager(NotificationService service, ActivityManager activities)
    {
        _service = service;
        _activities = activities;
        service.NotificationArrived += Add;
        service.NotificationsRemoved += OnRemovedInWindows;
    }

    /// <summary>A new notification landed (after grouping and de-duplication).</summary>
    public event Action<NotificationGroup, NotificationItem>? Arrived;

    /// <summary>Groups changed in any way (counts, added, removed).</summary>
    public event Action? Changed;

    /// <summary>The newest few groups, shown as rows in the expanded island.</summary>
    public ObservableCollection<NotificationGroup> VisibleGroups { get; } = new();

    public int GroupCount => _order.Count;

    /// <summary>All groups, newest activity first.</summary>
    public IReadOnlyList<NotificationGroup> Groups => _order;

    public static string ActivityId(NotificationGroup group) => "notif:" + group.Key;

    public NotificationGroup? FindByActivityId(string activityId) =>
        _order.FirstOrDefault(g => ActivityId(g) == activityId);

    private void Add(NotificationInfo info)
    {
        string key = string.IsNullOrWhiteSpace(info.AppId) ? info.App : info.AppId;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = "unknown";
        }

        if (!_groups.TryGetValue(key, out var group))
        {
            group = new NotificationGroup(key, info.AppId, info.App);
            _groups[key] = group;
        }
        else if (group.Contains(info.Id) || group.IsDuplicate(info))
        {
            return; // apps sometimes re-post the same toast
        }

        group.Icon ??= info.Logo ?? AppIconProvider.ForAppUserModelId(info.AppId);
        var item = new NotificationItem(info.Id, info.Title, info.Body, info.Time);
        group.Add(item);

        _order.Remove(group);
        _order.Insert(0, group);
        _history.Append(info);

        SyncActivity(group);
        SyncVisible();
        Arrived?.Invoke(group, item);
        Changed?.Invoke();
    }

    /// <summary>Remove a whole group here and in the Windows notification center.</summary>
    public void Dismiss(NotificationGroup group)
    {
        _service.Remove(group.Items.Select(i => i.SystemId).ToList());
        RemoveGroup(group);
        Changed?.Invoke();
    }

    /// <summary>Remove one message here and in the Windows notification center.</summary>
    public void DismissItem(NotificationGroup group, NotificationItem item)
    {
        _service.Remove(new[] { item.SystemId });
        group.RemoveSystemIds(new HashSet<uint> { item.SystemId });
        if (group.Count == 0)
        {
            RemoveGroup(group);
        }
        else
        {
            SyncActivity(group);
        }
        Changed?.Invoke();
    }

    /// <summary>The most recent saved notifications, newest first.</summary>
    public IReadOnlyList<HistoryEntry> LoadHistory(int count) => _history.Load(count);

    /// <summary>How many notifications the local history holds.</summary>
    public int HistoryCount() => _history.Count();

    /// <summary>Delete the local history file (the Windows notification center is untouched).</summary>
    public void ClearHistory() => _history.Clear();

    /// <summary>Unread notifications waiting across all apps.</summary>
    public int WaitingCount => _order.Sum(g => g.Count);

    /// <summary>Bring the source app to the front, then clear its notifications.</summary>
    public void Open(NotificationGroup group)
    {
        AppLauncher.Activate(group.AppId);
        Dismiss(group);
    }

    public void ClearAll()
    {
        foreach (var group in _order.ToList())
        {
            _service.Remove(group.Items.Select(i => i.SystemId).ToList());
            RemoveGroup(group);
        }
        Changed?.Invoke();
    }

    /// <summary>Recompute "2m ago" style labels; called when the expanded view opens.</summary>
    public void RefreshTimes()
    {
        foreach (var group in _order)
        {
            group.Refresh();
        }
    }

    private void OnRemovedInWindows(IReadOnlyCollection<uint> ids)
    {
        var set = new HashSet<uint>(ids);
        bool changed = false;
        foreach (var group in _order.ToList())
        {
            if (!group.RemoveSystemIds(set))
            {
                continue;
            }
            changed = true;
            if (group.Count == 0)
            {
                RemoveGroup(group);
            }
            else
            {
                SyncActivity(group);
            }
        }

        if (changed)
        {
            SyncVisible();
            Changed?.Invoke();
        }
    }

    private void RemoveGroup(NotificationGroup group)
    {
        _groups.Remove(group.Key);
        _order.Remove(group);
        _activities.Remove(ActivityId(group));
        SyncVisible();
    }

    private void SyncActivity(NotificationGroup group)
    {
        _activities.Upsert(
            ActivityId(group),
            () => new Activity(ActivityId(group), ActivityType.Notifications, ActivityLifetime.Temporary, 50, group.AppId),
            a =>
            {
                a.SourceName = group.AppName;
                a.Icon = group.Icon;
                a.Badge = group.Count;
                a.Title = group.Latest?.Title ?? "";
                a.Subtitle = group.Latest?.Body ?? "";
                a.Data = group;
            });
    }

    private void SyncVisible()
    {
        var wanted = _order.Take(VisibleGroupLimit).ToList();
        if (wanted.SequenceEqual(VisibleGroups))
        {
            return;
        }
        VisibleGroups.Clear();
        foreach (var group in wanted)
        {
            VisibleGroups.Add(group);
        }
    }
}

/// <summary>Append-only local history (%LOCALAPPDATA%\DynamicIsland\history.jsonl), trimmed at startup.</summary>
internal sealed class NotificationHistory
{
    private const int KeepLines = 500;
    private static readonly string HistoryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "history.jsonl");

    public NotificationHistory()
    {
        try
        {
            if (File.Exists(HistoryFile))
            {
                var lines = File.ReadAllLines(HistoryFile);
                if (lines.Length > KeepLines * 2)
                {
                    File.WriteAllLines(HistoryFile, lines[^KeepLines..]);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not trim notification history", ex);
        }
    }

    public void Append(NotificationInfo info)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
            string line = JsonSerializer.Serialize(new
            {
                time = info.Time,
                app = info.App,
                appId = info.AppId,
                title = info.Title,
                body = info.Body,
            });
            File.AppendAllText(HistoryFile, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not write notification history", ex);
        }
    }

    public int Count()
    {
        try
        {
            return File.Exists(HistoryFile) ? File.ReadLines(HistoryFile).Count(l => l.Length > 0) : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(HistoryFile))
            {
                File.Delete(HistoryFile);
            }
            Log.Info("Notification history cleared");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not clear notification history", ex);
        }
    }

    public IReadOnlyList<HistoryEntry> Load(int count)
    {
        var entries = new List<HistoryEntry>();
        try
        {
            if (!File.Exists(HistoryFile))
            {
                return entries;
            }
            foreach (string line in File.ReadLines(HistoryFile).TakeLast(count).Reverse())
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    var time = root.TryGetProperty("time", out var t) && t.TryGetDateTimeOffset(out var parsed) ? parsed : DateTimeOffset.MinValue;
                    entries.Add(new HistoryEntry(time, Text("app"), Text("title"), Text("body"),
                        AppIconProvider.ForAppUserModelId(Text("appId"))));
                }
                catch
                {
                    // Skip a damaged line.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read notification history", ex);
        }
        return entries;
    }
}
