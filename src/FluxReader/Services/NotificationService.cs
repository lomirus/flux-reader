using FluxReader.Core.Models;
using FluxReader.Core.Services;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FluxReader.Services;

public sealed class NotificationService : IDisposable
{
    private const int MaximumArticleDescriptionLength = 256;
    private readonly string _logPath;
    private readonly object _logSync = new();
    private AppNotificationManager? _manager;
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

    public Task ShowNewArticlesAsync(
        IReadOnlyList<ParsedArticle> articles,
        string? feedIconUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(articles);

        var manager = _manager;
        if (!_registered || manager is null || articles.Count == 0)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var iconUri = TryCreateIconUri(feedIconUrl);

        manager = _manager;
        if (!_registered || manager is null)
        {
            return Task.CompletedTask;
        }

        foreach (var article in articles)
        {
            var description = ArticleContentParser.CreatePreviewText(
                article.Summary,
                article.Content,
                article.Link,
                MaximumArticleDescriptionLength);
            ShowNewArticle(manager, article.Title, description, iconUri);
        }

        return Task.CompletedTask;
    }

    private void ShowNewArticle(
        AppNotificationManager manager,
        string articleTitle,
        string articleDescription,
        Uri? iconUri)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "open")
                .AddText(articleTitle)
                .AddText(articleDescription);
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
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        Activated?.Invoke(this, EventArgs.Empty);

    private static Uri? TryCreateIconUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.IsFile &&
        File.Exists(uri.LocalPath) &&
        Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".png" or ".jpg" or ".svg"
            ? uri
            : null;

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
