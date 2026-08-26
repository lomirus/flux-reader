using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FluxReader.Services;

public sealed class NotificationService : IDisposable
{
    private bool _registered;

    public event EventHandler? Activated;

    public bool IsAvailable => _registered;

    public void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            _registered = false;
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
        catch
        {
            // Notifications are an optional surface; a disabled notification center must not break refresh.
        }
    }

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
        AppNotificationManager.Default.Unregister();
        _registered = false;
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        Activated?.Invoke(this, EventArgs.Empty);
}
