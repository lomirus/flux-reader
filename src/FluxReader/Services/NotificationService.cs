using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FluxReader.Services;

public sealed class NotificationService : IDisposable
{
    private readonly NotificationIconCache _iconCache;
    private readonly string _logPath;
    private readonly object _logSync = new();
    private AppNotificationManager? _manager;
    private bool _registered;

    public NotificationService(string logPath, string iconCacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconCacheDirectory);
        _logPath = logPath;
        _iconCache = new NotificationIconCache(iconCacheDirectory);
    }

    public event EventHandler? Activated;

    public bool IsAvailable => _registered;

    public string LogPath => _logPath;

    public void Register()
    {
        AppNotificationManager? manager = null;

        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                return;
            }

            manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            _manager = manager;
            _registered = true;
        }
        catch (Exception exception)
        {
            if (manager is not null)
            {
                try
                {
                    manager.NotificationInvoked -= OnNotificationInvoked;
                }
                catch
                {
                    // Notification cleanup must not prevent the app from starting.
                }
            }

            _manager = null;
            _registered = false;
            LogFailure("Register", exception);
        }
    }

    public async Task ShowNewArticlesAsync(
        string feedTitle,
        IReadOnlyList<string> articleTitles,
        string? feedIconUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(articleTitles);

        var manager = _manager;
        if (!_registered || manager is null || articleTitles.Count == 0)
        {
            return;
        }

        Uri? iconUri = null;
        try
        {
            iconUri = await _iconCache.GetAsync(feedIconUrl, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailure("CacheIcon", exception);
        }

        manager = _manager;
        if (!_registered || manager is null)
        {
            return;
        }

        foreach (var articleTitle in articleTitles)
        {
            ShowNewArticle(manager, feedTitle, articleTitle, iconUri);
        }
    }

    private void ShowNewArticle(
        AppNotificationManager manager,
        string feedTitle,
        string articleTitle,
        Uri? iconUri)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "open")
                .AddText(feedTitle)
                .AddText(articleTitle);
            if (iconUri is not null)
            {
                builder.SetAppLogoOverride(iconUri);
            }

            var notification = builder.BuildNotification();
            notification.Expiration = DateTimeOffset.Now.AddHours(8);
            manager.Show(notification);
        }
        catch (Exception exception)
        {
            LogFailure("Show", exception);
        }
    }

    public void Dispose()
    {
        var manager = _manager;
        if (!_registered || manager is null)
        {
            _iconCache.Dispose();
            return;
        }

        try
        {
            manager.NotificationInvoked -= OnNotificationInvoked;
            manager.Unregister();
        }
        catch (Exception exception)
        {
            LogFailure("Unregister", exception);
        }
        finally
        {
            _manager = null;
            _registered = false;
            _iconCache.Dispose();
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        Activated?.Invoke(this, EventArgs.Empty);

    private void LogFailure(string operation, Exception exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var entry = $"""
                [{DateTimeOffset.Now:O}] {operation} failed
                Exception: {exception.GetType().FullName}
                HResult: 0x{exception.HResult:X8}
                {exception}

                """;

            lock (_logSync)
            {
                File.AppendAllText(_logPath, entry);
            }
        }
        catch
        {
            // Logging must not turn optional notification failures into application failures.
        }
    }
}
