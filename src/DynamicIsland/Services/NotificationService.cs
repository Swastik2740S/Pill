using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Services;

/// <summary>
/// Reads Windows toast notifications from every app (WhatsApp, Teams, Outlook, browsers...)
/// through the official notification listener, and reports arrivals and removals.
///
/// Windows decides whether an app may use the listener; on this PC access is granted. If it
/// isn't, the service turns itself off quietly and the rest of the island keeps working.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly Dictionary<string, ImageSource?> _logoCache = new();
    private HashSet<uint> _seen = new();
    private UserNotificationListener? _listener;
    private DispatcherTimer? _poll;
    private bool _busy;

    public bool IsAvailable { get; private set; }

    /// <summary>Raised on the UI thread for each new notification, oldest first.</summary>
    public event Action<NotificationInfo>? NotificationArrived;

    /// <summary>Raised on the UI thread with ids that disappeared from the Windows notification center.</summary>
    public event Action<IReadOnlyCollection<uint>>? NotificationsRemoved;

    public async Task InitializeAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                Log.Info($"Notification access: {access}. Notification previews are off.");
                return;
            }

            // Whatever is already in the notification center counts as seen.
            foreach (var existing in await _listener.GetNotificationsAsync(NotificationKinds.Toast))
            {
                _seen.Add(existing.Id);
            }

            IsAvailable = true;
            // The change event is unreliable for desktop apps, so poll gently instead.
            _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _poll.Tick += async (_, _) => await PollAsync();
            _poll.Start();
            Log.Info("Notification previews enabled");
        }
        catch (Exception ex)
        {
            Log.Info($"Notification listener unavailable (needs package identity): {ex.Message}");
        }
    }

    /// <summary>Remove notifications from the Windows notification center as well.</summary>
    public void Remove(IReadOnlyCollection<uint> ids)
    {
        if (_listener is null)
        {
            return;
        }
        foreach (uint id in ids)
        {
            try
            {
                _listener.RemoveNotification(id);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not remove notification {id}", ex);
            }
            _seen.Remove(id);
        }
    }

    private async Task PollAsync()
    {
        if (_busy || _listener is null)
        {
            return;
        }

        _busy = true;
        try
        {
            var current = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var ids = new HashSet<uint>();
            var fresh = new List<UserNotification>();
            foreach (var n in current)
            {
                ids.Add(n.Id);
                if (!_seen.Contains(n.Id))
                {
                    fresh.Add(n);
                }
            }

            var removed = _seen.Where(id => !ids.Contains(id)).ToList();
            _seen = ids;

            if (removed.Count > 0)
            {
                NotificationsRemoved?.Invoke(removed);
            }

            foreach (var n in fresh.OrderBy(n => n.CreationTime))
            {
                if (await ParseAsync(n) is { } info)
                {
                    NotificationArrived?.Invoke(info);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Notification poll failed", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<NotificationInfo?> ParseAsync(UserNotification notification)
    {
        string appName = "";
        string appId = "";
        try
        {
            appName = notification.AppInfo.DisplayInfo.DisplayName;
            appId = notification.AppInfo.AppUserModelId;
        }
        catch
        {
            // Some system notifications have no app info.
        }

        var texts = new List<string>();
        try
        {
            var binding = notification.Notification.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding is not null)
            {
                foreach (var element in binding.GetTextElements())
                {
                    if (!string.IsNullOrWhiteSpace(element.Text))
                    {
                        texts.Add(element.Text.Trim());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read notification text", ex);
        }

        if (texts.Count == 0 && appName.Length == 0)
        {
            return null;
        }

        string title = texts.Count > 0 ? texts[0] : appName;
        string body = texts.Count > 1 ? string.Join("  ", texts.Skip(1)) : "";

        return new NotificationInfo(
            notification.Id,
            appId,
            appName,
            title,
            body,
            await GetLogoAsync(notification, appId),
            notification.CreationTime);
    }

    /// <summary>The app's own icon as Windows supplies it, cached per app.</summary>
    private async Task<ImageSource?> GetLogoAsync(UserNotification notification, string appId)
    {
        string key = string.IsNullOrEmpty(appId) ? "?" : appId;
        if (_logoCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ImageSource? logo = null;
        try
        {
            var reference = notification.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            logo = await ImageLoader.LoadAsync(reference, 64);
        }
        catch
        {
            // Fall back to the shell icon below.
        }

        logo ??= AppIconProvider.ForAppUserModelId(appId);
        _logoCache[key] = logo;
        return logo;
    }

    public void Dispose() => _poll?.Stop();
}
