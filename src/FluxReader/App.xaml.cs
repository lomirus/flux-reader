using System.ComponentModel;
using System.Runtime.InteropServices;
using FluxReader.Data;
using FluxReader.Interop;
using FluxReader.Services;
using FluxReader.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace FluxReader;

public partial class App : Application
{
    private const int ShowWindowNormally = 5;
    private const int RestoreWindow = 9;
    private readonly NotificationService _notificationService;
    private readonly object _pendingNotificationSync = new();
    private readonly Queue<long> _pendingNotificationArticleIds = [];
    private SystemTrayIcon? _systemTrayIcon;
    private Window? _window;
    private bool _exitRequested;
    private bool _hasUnreadArticles;

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
        var unreadIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "fluxreader-icon-unread.ico");
        _systemTrayIcon = new SystemTrayIcon(window, Localization, iconPath, unreadIconPath);
        _systemTrayIcon.OpenRequested += SystemTrayIcon_OpenRequested;
        _systemTrayIcon.RefreshRequested += SystemTrayIcon_RefreshRequested;
        _systemTrayIcon.ExitRequested += SystemTrayIcon_ExitRequested;
        window.ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateUnreadIndicator();
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

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.UnreadTotal))
        {
            UpdateUnreadIndicator();
        }
    }

    private void UpdateUnreadIndicator()
    {
        if (_window is not MainWindow window)
        {
            return;
        }

        var hasUnreadArticles = window.ViewModel.UnreadTotal > 0;
        if (_hasUnreadArticles == hasUnreadArticles)
        {
            return;
        }

        window.SetWindowIcon(hasUnreadArticles);
        _systemTrayIcon?.SetHasUnreadArticles(hasUnreadArticles);
        _hasUnreadArticles = hasUnreadArticles;
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
        if (_window is not { } window)
        {
            return;
        }

        var showCommand = window.AppWindow.Presenter is OverlappedPresenter
            {
                State: OverlappedPresenterState.Minimized
            }
                ? RestoreWindow
                : ShowWindowNormally;
        var windowHandle = WindowNative.GetWindowHandle(window);
        ShowWindow(windowHandle, showCommand);
        SetForegroundWindow(windowHandle);
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
        if (sender is MainWindow mainWindow)
        {
            mainWindow.ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

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

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);
}
