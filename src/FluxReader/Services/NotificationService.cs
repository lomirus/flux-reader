using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FluxReader.Services;

public sealed class NotificationService : IDisposable
{
    private readonly string _logPath;
    private readonly object _logSync = new();
    private bool _registered;

    public NotificationService(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        _logPath = logPath;
    }

    public event EventHandler? Activated;

    public bool IsAvailable => _registered;

    public string LogPath => _logPath;

    public void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception exception)
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            _registered = false;
            LogFailure("Register", exception);
        }
    }

    public void ShowNewArticles(int count, string? latestTitle)
    {
        if (!_registered || count <= 0)
        {
            return;
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "open")
                .AddText($"发现 {count} 篇新文章")
                .AddText(string.IsNullOrWhiteSpace(latestTitle) ? "打开 FluxReader 查看" : latestTitle);
            var notification = builder.BuildNotification();
            notification.Expiration = DateTimeOffset.Now.AddHours(8);
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception exception)
        {
            LogFailure("Show", exception);
        }
    }

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception exception)
        {
            LogFailure("Unregister", exception);
        }
        finally
        {
            _registered = false;
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
