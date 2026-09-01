using FluxReader.Data;
using FluxReader.Interop;
using FluxReader.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace FluxReader;

public partial class App : Application
{
    private readonly NotificationService _notificationService;
    private readonly object _pendingNotificationSync = new();
    private readonly Queue<long> _pendingNotificationArticleIds = [];
    private SystemTrayIcon? _systemTrayIcon;
    private Window? _window;
    private bool _exitRequested;

    public App()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluxReader");
        DiagnosticLog.Initialize(dataDirectory);
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        DiagnosticLog.Information("app.constructing");

        InitializeComponent();

        Localization = new LocalizationService();
        Repository = new RssRepository(Path.Combine(dataDirectory, "reader.db"), Localization);
        Settings = new SettingsService(Path.Combine(dataDirectory, "settings.json"));
        Proxy = new ConfigurableWebProxy();
        const string iconCacheFolderName = "feed-icons";
        RefreshService = new RssRefreshService(
            Repository,
            Localization,
            Path.Combine(dataDirectory, iconCacheFolderName),
            Proxy);
        _notificationService = new NotificationService(
            Path.Combine(dataDirectory, "notifications.log"));
        _notificationService.Activated += NotificationService_Activated;
        _notificationService.Register();
    }

    public static new App Current => (App)Application.Current;

    public RssRepository Repository { get; }

    public LocalizationService Localization { get; }

    public RssRefreshService RefreshService { get; }

    public SettingsService Settings { get; }

    internal ConfigurableWebProxy Proxy { get; }

    public NotificationService Notifications => _notificationService;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DiagnosticLog.Information("app.launching", new { arguments = args.Arguments });
        var window = new MainWindow();
        lock (_pendingNotificationSync)
        {
            _window = window;
        }
        window.AppWindow.Closing += Window_Closing;
        window.Closed += Window_Closed;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "fluxreader-icon.ico");
        _systemTrayIcon = new SystemTrayIcon(window, Localization, iconPath);
        _systemTrayIcon.OpenRequested += SystemTrayIcon_OpenRequested;
        _systemTrayIcon.RefreshRequested += SystemTrayIcon_RefreshRequested;
        _systemTrayIcon.ExitRequested += SystemTrayIcon_ExitRequested;
        window.Activate();
        ProcessPendingNotificationActivations();
        DiagnosticLog.MemorySnapshot("app.launched");
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) =>
        DiagnosticLog.Error(
            "exception.xaml_unhandled",
            args.Exception,
            new { args.Handled });

    private static void CurrentDomain_UnhandledException(
        object sender,
        System.UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            DiagnosticLog.Error(
                "exception.app_domain_unhandled",
                exception,
                new { args.IsTerminating });
            return;
        }

        DiagnosticLog.Warning(
            "exception.app_domain_unhandled_non_exception",
            new
            {
                args.IsTerminating,
                exceptionObject = args.ExceptionObject?.ToString()
            });
    }

    private static void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs args) =>
        DiagnosticLog.Error(
            "exception.unobserved_task",
            args.Exception,
            new { args.Observed });

    private void NotificationService_Activated(object? sender, NotificationInvokedEventArgs e)
    {
        Window? window;
        lock (_pendingNotificationSync)
        {
            if (e.ArticleId is { } articleId)
            {
                _pendingNotificationArticleIds.Enqueue(articleId);
            }

            window = _window;
        }

        window?.DispatcherQueue.TryEnqueue(ProcessPendingNotificationActivations);
    }

    private void ProcessPendingNotificationActivations()
    {
        ShowMainWindow();
        if (_window is not MainWindow window)
        {
            return;
        }

        long[] articleIds;
        lock (_pendingNotificationSync)
        {
            articleIds = _pendingNotificationArticleIds.ToArray();
            _pendingNotificationArticleIds.Clear();
        }

        foreach (var articleId in articleIds)
        {
            window.OpenArticleFromNotification(articleId);
        }
    }

    private void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            DiagnosticLog.Information("window.closing", new { exitRequested = true });
            return;
        }

        DiagnosticLog.Information("window.close_intercepted", new { action = "hide_to_tray" });
        args.Cancel = true;
        sender.Hide();
    }

    private void SystemTrayIcon_OpenRequested(object? sender, EventArgs e)
    {
        _window?.DispatcherQueue.TryEnqueue(ShowMainWindow);
    }

    private void SystemTrayIcon_RefreshRequested(object? sender, EventArgs e)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_window is not MainWindow window ||
                !window.ViewModel.RefreshCommand.CanExecute(null))
            {
                return;
            }

            DiagnosticLog.Information(
                "refresh.tray_triggered",
                new { feedCount = window.ViewModel.Feeds.Count });
            window.ViewModel.RefreshCommand.Execute(null);
        });
    }

    private void SystemTrayIcon_ExitRequested(object? sender, EventArgs e)
    {
        _window?.DispatcherQueue.TryEnqueue(ExitApplication);
    }

    private void ExitApplication()
    {
        DiagnosticLog.Information("app.exit_requested", new { source = "system_tray" });
        _exitRequested = true;
        DisposeSystemTrayIcon();
        _window?.Close();
    }

    private void ShowMainWindow()
    {
        if (_window?.AppWindow is not { } appWindow)
        {
            return;
        }

        if (appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore(true);
            return;
        }

        appWindow.Show(true);
    }

    private void DisposeSystemTrayIcon()
    {
        if (_systemTrayIcon is null)
        {
            return;
        }

        _systemTrayIcon.OpenRequested -= SystemTrayIcon_OpenRequested;
        _systemTrayIcon.RefreshRequested -= SystemTrayIcon_RefreshRequested;
        _systemTrayIcon.ExitRequested -= SystemTrayIcon_ExitRequested;
        _systemTrayIcon.Dispose();
        _systemTrayIcon = null;
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        if (sender is Window window)
        {
            window.AppWindow.Closing -= Window_Closing;
            window.Closed -= Window_Closed;
        }

        DisposeSystemTrayIcon();
        _notificationService.Activated -= NotificationService_Activated;
        _notificationService.Dispose();
        RefreshService.Dispose();
        _window = null;
        DiagnosticLog.CompleteSession(_exitRequested ? "requested_exit" : "window_closed");
    }
}
