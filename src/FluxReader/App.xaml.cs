using FluxReader.Data;
using FluxReader.Interop;
using FluxReader.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace FluxReader;

public partial class App : Application
{
    private readonly NotificationService _notificationService;
    private SystemTrayIcon? _systemTrayIcon;
    private Window? _window;
    private bool _exitRequested;

    public App()
    {
        InitializeComponent();

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluxReader");
        Localization = new LocalizationService();
        Repository = new RssRepository(Path.Combine(dataDirectory, "reader.db"), Localization);
        Settings = new SettingsService(Path.Combine(dataDirectory, "settings.json"));
        RefreshService = new RssRefreshService(Repository, Localization);
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

    public NotificationService Notifications => _notificationService;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;
        window.AppWindow.Closing += Window_Closing;
        window.Closed += Window_Closed;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "fluxreader-icon.ico");
        _systemTrayIcon = new SystemTrayIcon(window, Localization, iconPath);
        _systemTrayIcon.OpenRequested += SystemTrayIcon_OpenRequested;
        _systemTrayIcon.ExitRequested += SystemTrayIcon_ExitRequested;
        window.Activate();
    }

    private void NotificationService_Activated(object? sender, EventArgs e)
    {
        _window?.DispatcherQueue.TryEnqueue(ShowMainWindow);
    }

    private void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
    }

    private void SystemTrayIcon_OpenRequested(object? sender, EventArgs e)
    {
        _window?.DispatcherQueue.TryEnqueue(ShowMainWindow);
    }

    private void SystemTrayIcon_ExitRequested(object? sender, EventArgs e)
    {
        _window?.DispatcherQueue.TryEnqueue(ExitApplication);
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        DisposeSystemTrayIcon();
        _window?.Close();
    }

    private void ShowMainWindow()
    {
        _window?.AppWindow.Show(true);
    }

    private void DisposeSystemTrayIcon()
    {
        if (_systemTrayIcon is null)
        {
            return;
        }

        _systemTrayIcon.OpenRequested -= SystemTrayIcon_OpenRequested;
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
    }
}
