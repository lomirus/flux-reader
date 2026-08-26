using FluxReader.Data;
using FluxReader.Services;
using Microsoft.UI.Xaml;

namespace FluxReader;

public partial class App : Application
{
    private readonly NotificationService _notificationService;
    private Window? _window;

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
            Path.Combine(dataDirectory, "notifications.log"),
            Localization);
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
        _window = new MainWindow();
        _window.Closed += Window_Closed;
        _window.Activate();
    }

    private void NotificationService_Activated(object? sender, EventArgs e)
    {
        _window?.DispatcherQueue.TryEnqueue(() => _window.Activate());
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _notificationService.Activated -= NotificationService_Activated;
        _notificationService.Dispose();
        RefreshService.Dispose();
    }
}
