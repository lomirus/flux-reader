using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Xml;
using FluxReader.Core.Services;
using FluxReader.Models;
using FluxReader.Services;
using FluxReader.ViewModels;
using FluxReader.Interop;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace FluxReader;

public sealed partial class MainWindow : Window
{
    private const double DefaultFeedPaneWidth = 248;
    private const double DefaultArticleListPaneWidth = 420;
    private const double MinimumFeedPaneWidth = 180;
    private const double MaximumFeedPaneWidth = 480;
    private const double MinimumArticleListPaneWidth = 300;
    private const double MaximumArticleListPaneWidth = 720;
    private const double MinimumReaderPaneWidth = 360;
    private const double SplitterWidth = 8;
    private const int DefaultRefreshIntervalMinutes = 15;
    private static readonly string[] FeedSelectionIndicatorBrushResourceKeys =
    [
        "ListViewItemSelectionIndicatorBrush",
        "ListViewItemSelectionIndicatorPointerOverBrush",
        "ListViewItemSelectionIndicatorPressedBrush",
        "ListViewItemSelectionIndicatorDisabledBrush"
    ];
    private static readonly TimeSpan DefaultStatusNotificationDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarningStatusNotificationDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ArticleSearchDebounceDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FeedGroupExitAnimationDuration = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FeedGroupRepositionAnimationDuration = TimeSpan.FromMilliseconds(167);
    private const double FeedGroupAnimationVerticalOffset = 8;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _feedGroupAnimationLock = new(1, 1);
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromMinutes(DefaultRefreshIntervalMinutes)
    };
    private readonly DispatcherTimer _diagnosticTimer = new()
    {
        Interval = TimeSpan.FromSeconds(30)
    };
    private readonly DispatcherTimer _statusNotificationTimer = new();
    private readonly Storyboard _refreshIconSpinStoryboard = new();
    private readonly Storyboard _statusInfoBarEntranceStoryboard = new();
    private readonly Queue<ArticleNavigationRequest> _pendingArticleNavigations = new();
    private readonly Dictionary<ulong, ArticleNavigationRequest> _articleNavigations = new();
    private readonly ArticleStylesheetService _articleStylesheetService;
    private readonly SolidColorBrush _transparentFeedSelectionIndicatorBrush = new(Colors.Transparent);
    private long _statusInfoBarIsOpenCallbackToken;
    private bool _areFeedGroupSelectionIndicatorsVisible = true;
    private bool _articleWebViewConfigured;
    private Task<CoreWebView2Environment>? _articleWebViewEnvironmentTask;
    private Task? _articleWebViewInitializationTask;
    private long _articleRenderVersion;
    private CancellationTokenSource? _articleSearchDebounce;
    private bool _isSynchronizingFeedListSelection;
    private bool _isOpeningNotificationArticle;
    private AppSettings _settings = new();
    private long? _pendingNotificationArticleId;
    private bool _settingsLoaded;
    private bool _viewModelInitialized;

    private readonly record struct ArticleNavigationRequest(
        long RenderVersion,
        long ArticleId,
        long FeedId);

    private sealed record FeedContainerAnimationState(
        ListViewItem Container,
        Transform? RenderTransform,
        double Opacity);

    private sealed record FeedContainerAnimationBatch(
        Storyboard Storyboard,
        IReadOnlyList<FeedContainerAnimationState> States);

    public MainWindow()
    {
        InitializeComponent();
        ConfigureRefreshIconAnimation();
        ConfigureStatusInfoBarAnimation();
        Title = "FluxReader";
        SetWindowIcon();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        UpdateTitleBarButtonColors();
        ArticleWebView.DefaultBackgroundColor = Colors.Transparent;

        var app = App.Current;
        _articleStylesheetService = new ArticleStylesheetService(app.Proxy);
        ViewModel = new MainViewModel(
            app.Repository,
            app.RefreshService,
            app.Notifications,
            app.Localization);
        RootGrid.DataContext = ViewModel;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        ViewModel.Articles.CollectionChanged += Articles_CollectionChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.StatusNotificationRequested += ViewModel_StatusNotificationRequested;
        ApplyLocalization();
        RootGrid.Loaded += RootGrid_Loaded;
        _refreshTimer.Tick += RefreshTimer_Tick;
        _diagnosticTimer.Tick += DiagnosticTimer_Tick;
        _statusNotificationTimer.Tick += StatusNotificationTimer_Tick;
        Closed += MainWindow_Closed;
    }

    public MainViewModel ViewModel { get; }

    public void OpenArticleFromNotification(long articleId)
    {
        if (articleId <= 0)
        {
            return;
        }

        _pendingNotificationArticleId = articleId;
        if (_viewModelInitialized)
        {
            _ = OpenPendingNotificationArticleAsync();
        }
    }

    private void SetWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "fluxreader-icon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private void FeedIcon_ImageOpened(object sender, RoutedEventArgs e)
    {
        SetFeedIconFallbackVisibility(sender, isImageLoaded: true);
    }

    private void FeedIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        SetFeedIconFallbackVisibility(sender, isImageLoaded: false);
    }

    private static void SetFeedIconFallbackVisibility(object sender, bool isImageLoaded)
    {
        if (sender is not Image image)
        {
            return;
        }

        if (image.Tag is FeedNavigationItem item)
        {
            item.IconFallbackVisibility = isImageLoaded && item.IconSource is not null
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private async Task RenderSelectedArticleAsync()
    {
        var article = ViewModel.SelectedArticle;
        var renderVersion = ++_articleRenderVersion;
        if (article is null)
        {
            HideArticleContent();
            return;
        }

        ShowArticleLoading();

        try
        {
            await EnsureArticleWebViewInitializedAsync();
            if (renderVersion != _articleRenderVersion)
            {
                return;
            }

            IReadOnlyList<WebsiteStylesheetReference> externalStylesheets = [];
            if (_settings.LoadExternalArticleStylesheets && article.ContentBaseUri is { } pageUri)
            {
                externalStylesheets = await _articleStylesheetService.GetStylesheetsAsync(
                    pageUri,
                    _lifetime.Token);
                if (renderVersion != _articleRenderVersion)
                {
                    return;
                }
            }

            var document = ArticleHtmlDocumentBuilder.Create(
                article.DisplayContent,
                article.ContentBaseUri,
                RootGrid.ActualTheme == ElementTheme.Dark,
                externalStylesheets);
            var navigation = new ArticleNavigationRequest(
                renderVersion,
                article.Id,
                article.FeedId);
            _pendingArticleNavigations.Enqueue(navigation);
            ArticleWebView.NavigateToString(document);
        }
        catch (Exception exception)
        {
            if (renderVersion != _articleRenderVersion)
            {
                return;
            }

            DiagnosticLog.Error(
                "article.html_render_failed",
                exception,
                new
                {
                    articleId = article.Id,
                    feedId = article.FeedId,
                    appBaseDirectory = AppContext.BaseDirectory,
                    webView2LoaderExists = File.Exists(
                        Path.Combine(AppContext.BaseDirectory, "WebView2Loader.dll"))
                });
            _pendingArticleNavigations.Clear();
            HideArticleContent();
        }
    }

    private Task EnsureArticleWebViewInitializedAsync() =>
        _articleWebViewInitializationTask ??= InitializeArticleWebViewAsync();

    private async Task InitializeArticleWebViewAsync()
    {
        var environment = await GetArticleWebViewEnvironmentAsync();
        await ArticleWebView.EnsureCoreWebView2Async(environment);
        ConfigureArticleWebView();
    }

    private Task<CoreWebView2Environment> GetArticleWebViewEnvironmentAsync() =>
        _articleWebViewEnvironmentTask ??= CreateArticleWebViewEnvironmentAsync();

    private static async Task<CoreWebView2Environment> CreateArticleWebViewEnvironmentAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluxReader",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);
        return await CoreWebView2Environment.CreateWithOptionsAsync(
            browserExecutableFolder: null,
            userDataFolder,
            new CoreWebView2EnvironmentOptions());
    }

    private void ConfigureArticleWebView()
    {
        if (_articleWebViewConfigured || ArticleWebView.CoreWebView2 is not { } coreWebView)
        {
            return;
        }

        var settings = coreWebView.Settings;
        settings.IsScriptEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = true;
        settings.IsStatusBarEnabled = false;

        coreWebView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Script);
        coreWebView.WebResourceRequested += ArticleWebView_WebResourceRequested;
        coreWebView.DOMContentLoaded += ArticleWebView_DOMContentLoaded;
        coreWebView.WebMessageReceived += ArticleWebView_WebMessageReceived;
        ArticleWebView.NavigationStarting += ArticleWebView_NavigationStarting;
        ArticleWebView.NavigationCompleted += ArticleWebView_NavigationCompleted;
        coreWebView.NewWindowRequested += ArticleWebView_NewWindowRequested;
        _articleWebViewConfigured = true;
    }

    private static void ArticleWebView_WebResourceRequested(
        CoreWebView2 sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (args.ResourceContext == CoreWebView2WebResourceContext.Script)
        {
            args.Response = sender.Environment.CreateWebResourceResponse(
                new InMemoryRandomAccessStream(),
                403,
                "Blocked",
                "Content-Type: text/plain");
        }
    }

    private async void ArticleWebView_NavigationStarting(
        WebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        // NavigateToString does not expose its navigation ID. The request is
        // enqueued immediately before the call, so the next starting navigation
        // is the reliable point at which to associate the ID.
        if (_pendingArticleNavigations.TryDequeue(out var navigation))
        {
            _articleNavigations[args.NavigationId] = navigation;
            DiagnosticLog.Information(
                "article.html_navigation_started",
                new
                    {
                        args.NavigationId,
                        navigation.RenderVersion,
                        navigation.ArticleId,
                        navigation.FeedId
                    });
            return;
        }

        // WebView2 initializes itself with an empty document before the first
        // article navigation.
        if (args.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        args.Cancel = true;
        if (TryCreateExternalArticleUri(args.Uri, out var uri))
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private void ArticleWebView_DOMContentLoaded(
        CoreWebView2 sender,
        CoreWebView2DOMContentLoadedEventArgs args)
    {
        if (!_articleNavigations.TryGetValue(args.NavigationId, out var navigation))
        {
            return;
        }

        DiagnosticLog.Information(
            "article.html_dom_content_loaded",
            new
            {
                args.NavigationId,
                navigation.RenderVersion,
                currentRenderVersion = _articleRenderVersion,
                navigation.ArticleId,
                navigation.FeedId
            });
        // The second callback runs after the first article frame has been painted.
        _ = sender.ExecuteScriptAsync($$"""
            requestAnimationFrame(() => requestAnimationFrame(() =>
                window.chrome.webview.postMessage({{navigation.RenderVersion}})));
            """);
    }

    private void ArticleWebView_WebMessageReceived(
        CoreWebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        // Article scripts remain disabled; accept only the current host-issued render version.
        if (long.TryParse(args.WebMessageAsJson, out var renderVersion) &&
            renderVersion == _articleRenderVersion)
        {
            ShowArticleContent();
        }
    }

    private void ArticleWebView_NavigationCompleted(
        WebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!_articleNavigations.Remove(args.NavigationId, out var navigation))
        {
            DiagnosticLog.Warning(
                "article.html_navigation_completed_untracked",
                new
                {
                    args.NavigationId,
                    args.IsSuccess,
                    webErrorStatus = args.WebErrorStatus.ToString()
                });
            return;
        }

        DiagnosticLog.Information(
            "article.html_navigation_completed",
            new
            {
                args.NavigationId,
                args.IsSuccess,
                navigation.RenderVersion,
                currentRenderVersion = _articleRenderVersion,
                navigation.ArticleId,
                navigation.FeedId,
                webErrorStatus = args.WebErrorStatus.ToString()
            });
        if (navigation.RenderVersion != _articleRenderVersion)
        {
            return;
        }

        if (!args.IsSuccess)
        {
            HideArticleContent();
            DiagnosticLog.Warning(
                "article.html_navigation_failed",
                new
                {
                    articleId = navigation.ArticleId,
                    feedId = navigation.FeedId,
                    webErrorStatus = args.WebErrorStatus.ToString()
                });
            return;
        }

        ShowArticleContent();
    }

    private async void ArticleWebView_NewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (TryCreateExternalArticleUri(args.Uri, out var uri))
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private static bool TryCreateExternalArticleUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var resolvedUri) || resolvedUri is null)
        {
            return false;
        }

        if (!resolvedUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !resolvedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !resolvedUri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = resolvedUri;
        return true;
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        DiagnosticLog.Information("window.loading");
        _settings = await App.Current.Settings.LoadAsync(_lifetime.Token);
        AppLanguage? languagePreference = _settings.Language is { } savedLanguage && Enum.IsDefined(savedLanguage)
            ? savedLanguage
            : null;
        var proxyMode = Enum.IsDefined(_settings.ProxyMode)
            ? _settings.ProxyMode
            : ProxyMode.System;
        var customProxyAddress = ConfigurableWebProxy.TryNormalizeAddress(
            _settings.CustomProxyAddress,
            out var normalizedProxyAddress)
                ? normalizedProxyAddress
                : string.Empty;
        if (proxyMode == ProxyMode.Custom && string.IsNullOrEmpty(customProxyAddress))
        {
            proxyMode = ProxyMode.System;
        }

        _settings = _settings with
        {
            Language = languagePreference,
            RefreshIntervalMinutes = NormalizeRefreshInterval(_settings.RefreshIntervalMinutes),
            RefreshConcurrencyLimit = NormalizeRefreshConcurrencyLimit(_settings.RefreshConcurrencyLimit),
            RequestTimeoutSeconds = NormalizeRequestTimeout(_settings.RequestTimeoutSeconds),
            ProxyMode = proxyMode,
            CustomProxyAddress = customProxyAddress
        };
        App.Current.Proxy.Configure(proxyMode, customProxyAddress);
        App.Current.Localization.SetLanguage(
            App.Current.Localization.ResolveLanguage(languagePreference));
        ApplyLocalization();
        ViewModel.ApplyLocalization();
        ApplyTheme(_settings.Theme);
        ApplySavedPaneWidths();
        ApplyRefreshInterval(_settings.RefreshIntervalMinutes);
        ViewModel.RefreshConcurrencyLimit = _settings.RefreshConcurrencyLimit;
        ApplyRequestTimeout(_settings.RequestTimeoutSeconds);
        ResetSettingsFrame();
        _settingsLoaded = true;
        var articleWebViewInitialization = EnsureArticleWebViewInitializedAsync();
        await ViewModel.InitializeAsync(_lifetime.Token);
        _viewModelInitialized = true;
        await OpenPendingNotificationArticleAsync();

        if (ViewModel.Feeds.Count > 0 && ViewModel.RefreshCommand.CanExecute(null))
        {
            await ViewModel.RefreshCommand.ExecuteAsync(null);
        }

        try
        {
            await articleWebViewInitialization;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("article.webview_initialization_failed", exception);
        }

        _refreshTimer.Start();
        _diagnosticTimer.Start();
        DiagnosticLog.MemorySnapshot(
            "window.loaded",
            new
            {
                feedCount = ViewModel.Feeds.Count,
                articleCount = ViewModel.Articles.Count
            });
    }

    private async void AddFeed_Click(object sender, RoutedEventArgs e)
    {
        var localization = App.Current.Localization;
        var input = new TextBox
        {
            Header = localization.GetString("FeedAddress"),
            PlaceholderText = "https://example.com/feed.xml"
        };
        var groupSelector = CreateGroupSelector(null);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(input);
        content.Children.Add(groupSelector);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = localization.GetString("AddFeed"),
            Content = content,
            PrimaryButtonText = localization.GetString("Add"),
            CloseButtonText = localization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.AddFeedAsync(input.Text, GetSelectedGroupId(groupSelector), _lifetime.Token);
            HideArticleReader();
        }
    }

    private async void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var localization = App.Current.Localization;
        var input = new TextBox
        {
            Header = localization.GetString("GroupName"),
            MaxLength = 100
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = localization.GetString("AddGroup"),
            Content = input,
            PrimaryButtonText = localization.GetString("Create"),
            CloseButtonText = localization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.AddFeedGroupAsync(input.Text, _lifetime.Token);
            HideArticleReader();
        }
    }

    private async void FeedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingFeedListSelection || FeedList.Items.Count == 0)
        {
            return;
        }

        var selectedItems = FeedList.SelectedItems
            .OfType<FeedNavigationItem>()
            .ToArray();
        var selection = FeedListSelectionResolver.Resolve(
            selectedItems
                .Where(item => item.Feed is not null)
                .Select(item => item.Feed!.Id),
            selectedItems
                .Where(item => item.Group is not null)
                .Select(item => item.Group!.Id));
        SetFeedGroupSelectionIndicatorsVisible(selection.FeedIds.Count == 0);
        NormalizeFeedListSelection(selectedItems, selection);

        if (selection.GroupId is { } groupId)
        {
            if (ViewModel.SelectedGroup?.Id == groupId && ViewModel.SelectedFeedIds.Count == 0)
            {
                return;
            }

            var group = selectedItems
                .Select(item => item.Group)
                .FirstOrDefault(candidate => candidate?.Id == groupId);
            if (group is null)
            {
                SynchronizeFeedListSelection();
                return;
            }

            await ViewModel.SelectGroupAsync(group, _lifetime.Token);
            HideArticleReader();
            return;
        }

        if (ViewModel.SelectedGroup is null &&
            ViewModel.SelectedFeedIds.SetEquals(selection.FeedIds))
        {
            return;
        }

        await ViewModel.SelectFeedsAsync(selection.FeedIds, _lifetime.Token);
        HideArticleReader();
    }

    private async void FeedGroupChevron_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FeedNavigationItem item })
        {
            return;
        }

        await _feedGroupAnimationLock.WaitAsync();
        try
        {
            if (!ViewModel.FeedNavigationRows.Contains(item))
            {
                return;
            }

            if (item.Children.Count == 0 || !new UISettings().AnimationsEnabled)
            {
                ViewModel.ToggleFeedNavigationGroup(item);
                return;
            }

            await ToggleFeedNavigationGroupAnimatedAsync(item);
        }
        finally
        {
            _feedGroupAnimationLock.Release();
        }
    }

    private async Task ToggleFeedNavigationGroupAnimatedAsync(FeedNavigationItem item)
    {
        var originalTransitions = FeedList.ItemContainerTransitions;
        FeedList.ItemContainerTransitions = new TransitionCollection();

        try
        {
            if (item.IsExpanded)
            {
                var exitAnimation = CreateFeedGroupExitAnimation(item);
                await RunStoryboardAsync(exitAnimation.Storyboard);

                var previousPositions = CaptureFeedContainerPositions();
                ViewModel.ToggleFeedNavigationGroup(item);
                RestoreFeedContainerAnimationStates(exitAnimation.States);
                FeedList.UpdateLayout();

                var repositionAnimation = CreateFeedRepositionAnimation(
                    previousPositions,
                    FeedGroupAnimationVerticalOffset);
                try
                {
                    await RunStoryboardAsync(repositionAnimation.Storyboard);
                }
                finally
                {
                    RestoreFeedContainerAnimationStates(repositionAnimation.States);
                }

                return;
            }

            var positions = CaptureFeedContainerPositions();
            ViewModel.ToggleFeedNavigationGroup(item);
            FeedList.UpdateLayout();

            var expansionAnimation = CreateFeedRepositionAnimation(
                positions,
                -FeedGroupAnimationVerticalOffset);
            try
            {
                await RunStoryboardAsync(expansionAnimation.Storyboard);
            }
            finally
            {
                RestoreFeedContainerAnimationStates(expansionAnimation.States);
            }
        }
        finally
        {
            FeedList.ItemContainerTransitions = originalTransitions;
        }
    }

    private Dictionary<FeedNavigationItem, double> CaptureFeedContainerPositions()
    {
        var positions = new Dictionary<FeedNavigationItem, double>();
        foreach (var item in ViewModel.FeedNavigationRows)
        {
            if (FeedList.ContainerFromItem(item) is ListViewItem container)
            {
                positions[item] = GetFeedContainerVerticalPosition(container);
            }
        }

        return positions;
    }

    private FeedContainerAnimationBatch CreateFeedGroupExitAnimation(FeedNavigationItem item)
    {
        var storyboard = new Storyboard();
        var states = new List<FeedContainerAnimationState>();
        foreach (var child in item.Children)
        {
            if (FeedList.ContainerFromItem(child) is not ListViewItem container)
            {
                continue;
            }

            var transform = new TranslateTransform();
            states.Add(new FeedContainerAnimationState(
                container,
                container.RenderTransform,
                container.Opacity));
            container.RenderTransform = transform;

            AddFeedContainerTranslationAnimation(
                storyboard,
                transform,
                -FeedGroupAnimationVerticalOffset,
                FeedGroupExitAnimationDuration,
                EasingMode.EaseIn);
            AddFeedContainerOpacityAnimation(
                storyboard,
                container,
                0,
                FeedGroupExitAnimationDuration,
                EasingMode.EaseIn);
        }

        return new FeedContainerAnimationBatch(storyboard, states);
    }

    private FeedContainerAnimationBatch CreateFeedRepositionAnimation(
        IReadOnlyDictionary<FeedNavigationItem, double> previousPositions,
        double newContainerOffset)
    {
        var storyboard = new Storyboard();
        var states = new List<FeedContainerAnimationState>();
        foreach (var item in ViewModel.FeedNavigationRows)
        {
            if (FeedList.ContainerFromItem(item) is not ListViewItem container)
            {
                continue;
            }

            var isNewlyRealized = !previousPositions.TryGetValue(item, out var previousPosition);
            var offset = isNewlyRealized
                ? newContainerOffset
                : previousPosition - GetFeedContainerVerticalPosition(container);
            if (Math.Abs(offset) < 0.5 && !isNewlyRealized)
            {
                continue;
            }

            var transform = new TranslateTransform
            {
                Y = offset
            };
            states.Add(new FeedContainerAnimationState(
                container,
                container.RenderTransform,
                container.Opacity));
            container.RenderTransform = transform;
            AddFeedContainerTranslationAnimation(
                storyboard,
                transform,
                0,
                FeedGroupRepositionAnimationDuration,
                EasingMode.EaseOut);

            if (isNewlyRealized)
            {
                container.Opacity = 0;
                AddFeedContainerOpacityAnimation(
                    storyboard,
                    container,
                    1,
                    FeedGroupRepositionAnimationDuration,
                    EasingMode.EaseOut);
            }
        }

        return new FeedContainerAnimationBatch(storyboard, states);
    }

    private double GetFeedContainerVerticalPosition(ListViewItem container) =>
        container.TransformToVisual(FeedList).TransformPoint(new Point()).Y;

    private static void AddFeedContainerTranslationAnimation(
        Storyboard storyboard,
        TranslateTransform target,
        double to,
        TimeSpan duration,
        EasingMode easingMode)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase
            {
                EasingMode = easingMode
            }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, nameof(TranslateTransform.Y));
        storyboard.Children.Add(animation);
    }

    private static void AddFeedContainerOpacityAnimation(
        Storyboard storyboard,
        ListViewItem target,
        double to,
        TimeSpan duration,
        EasingMode easingMode)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase
            {
                EasingMode = easingMode
            }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        storyboard.Children.Add(animation);
    }

    private static Task RunStoryboardAsync(Storyboard storyboard)
    {
        if (storyboard.Children.Count == 0)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource();
        void Storyboard_Completed(object? sender, object e)
        {
            storyboard.Completed -= Storyboard_Completed;
            completion.TrySetResult();
        }

        storyboard.Completed += Storyboard_Completed;
        storyboard.Begin();
        return completion.Task;
    }

    private static void RestoreFeedContainerAnimationStates(
        IEnumerable<FeedContainerAnimationState> states)
    {
        foreach (var state in states)
        {
            state.Container.RenderTransform = state.RenderTransform;
            state.Container.Opacity = state.Opacity;
        }
    }

    private void FeedList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue ||
            args.ItemContainer is not ListViewItem container ||
            args.Item is not FeedNavigationItem item)
        {
            return;
        }

        SetFeedSelectionIndicatorVisible(
            container,
            !item.IsGroup || _areFeedGroupSelectionIndicatorsVisible);
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedFeedIds))
        {
            SynchronizeFeedListSelection();
        }

        if (e.PropertyName == nameof(MainViewModel.LastRefreshedAt))
        {
            UpdateRefreshButtonToolTip();
        }

        if (e.PropertyName == nameof(MainViewModel.IsBusy))
        {
            UpdateRefreshButtonVisualState();
            if (!ViewModel.IsBusy && _viewModelInitialized)
            {
                _ = OpenPendingNotificationArticleAsync();
            }
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedArticle))
        {
            var article = ViewModel.SelectedArticle;
            var renderTask = RenderSelectedArticleAsync();
            DiagnosticLog.MemorySnapshot(
                "article.selection_changed",
                new
                {
                    articleId = article?.Id,
                    feedId = article?.FeedId,
                    contentCharacterCount = article?.DisplayContent.Length,
                    containsHtml = ArticleContentParser.ContainsHtmlMarkup(article?.DisplayContent)
                });
            await renderTask;
        }
    }

    private void ViewModel_StatusNotificationRequested(
        object? sender,
        StatusNotificationRequestedEventArgs args)
    {
        _statusNotificationTimer.Stop();
        StatusInfoBar.Title = args.Title ?? string.Empty;
        StatusInfoBar.Message = args.Message;
        StatusInfoBar.Severity = args.Severity switch
        {
            StatusNotificationSeverity.Success => InfoBarSeverity.Success,
            StatusNotificationSeverity.Warning => InfoBarSeverity.Warning,
            StatusNotificationSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational
        };

        StatusInfoBar.ActionButton = null;
        if (args.Details.Count > 0 && !string.IsNullOrWhiteSpace(args.ActionText))
        {
            var detailsButton = new Button
            {
                Content = args.ActionText,
                Tag = args
            };
            detailsButton.Click += StatusNotificationDetails_Click;
            StatusInfoBar.ActionButton = detailsButton;
        }

        StatusInfoBar.IsOpen = true;
        var duration = args.Details.Count > 0
            ? null
            : args.Severity switch
            {
                StatusNotificationSeverity.Error => (TimeSpan?)null,
                StatusNotificationSeverity.Warning => WarningStatusNotificationDuration,
                _ => DefaultStatusNotificationDuration
            };
        if (duration is { } interval)
        {
            _statusNotificationTimer.Interval = interval;
            _statusNotificationTimer.Start();
        }
    }

    private void StatusNotificationTimer_Tick(object? sender, object e)
    {
        _statusNotificationTimer.Stop();
        StatusInfoBar.IsOpen = false;
    }

    private async Task OpenPendingNotificationArticleAsync()
    {
        if (_isOpeningNotificationArticle ||
            !_viewModelInitialized ||
            ViewModel.IsBusy ||
            _lifetime.IsCancellationRequested)
        {
            return;
        }

        _isOpeningNotificationArticle = true;
        try
        {
            while (_pendingNotificationArticleId is { } articleId)
            {
                _pendingNotificationArticleId = null;
                Article? article;
                try
                {
                    article = await ViewModel.NavigateToArticleAsync(articleId, _lifetime.Token);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    DiagnosticLog.Error(
                        "notification.article_navigation_failed",
                        exception,
                        new { articleId });
                    continue;
                }

                if (article is null)
                {
                    DiagnosticLog.Warning(
                        "notification.article_not_found",
                        new { articleId });
                    continue;
                }

                ArticleSearchBox.Text = string.Empty;
                CloseSettingsPage();
                ArticleEmptyView.Visibility = Visibility.Collapsed;
                ArticleReaderView.Visibility = Visibility.Visible;
                if (ViewModel.Articles.Contains(article))
                {
                    ArticleList.SelectedItem = article;
                    ArticleList.ScrollIntoView(article);
                }

                DiagnosticLog.Information(
                    "notification.article_opened",
                    new { articleId, article.FeedId });
            }
        }
        finally
        {
            _isOpeningNotificationArticle = false;
            if (_pendingNotificationArticleId is not null &&
                !_lifetime.IsCancellationRequested)
            {
                _ = OpenPendingNotificationArticleAsync();
            }
        }
    }

    private async void StatusNotificationDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: StatusNotificationRequestedEventArgs args })
        {
            return;
        }

        var localization = App.Current.Localization;
        var detailsList = new ListView
        {
            Width = 480,
            MaxHeight = 360,
            IsItemClickEnabled = false,
            ItemContainerTransitions = new TransitionCollection(),
            ItemTemplate = (DataTemplate)RootGrid.Resources["StatusNotificationDetailTemplate"],
            ItemsSource = args.Details,
            SelectionMode = ListViewSelectionMode.None
        };
        var dialogContent = new StackPanel
        {
            Spacing = 8
        };
        dialogContent.Children.Add(new TextBlock
        {
            Text = localization.GetString("RefreshFailureDetailsDescription"),
            TextWrapping = TextWrapping.Wrap
        });
        dialogContent.Children.Add(detailsList);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = localization.GetString("RefreshFailureDetailsTitle"),
            Content = dialogContent,
            CloseButtonText = localization.GetString("Close"),
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private void NormalizeFeedListSelection(
        IReadOnlyCollection<FeedNavigationItem> selectedItems,
        FeedListSelection selection)
    {
        var desiredItems = selection.FeedIds.Count > 0
            ? selectedItems.Where(item => item.Feed is not null)
            : selection.GroupId is { } groupId
                ? selectedItems.Where(item => item.Group?.Id == groupId)
                : Array.Empty<FeedNavigationItem>();
        ApplyFeedListSelection(desiredItems.ToHashSet());
    }

    private void SynchronizeFeedListSelection()
    {
        var selectedFeedIds = ViewModel.SelectedFeedIds;
        SetFeedGroupSelectionIndicatorsVisible(selectedFeedIds.Count == 0);
        var desiredItems = ViewModel.SelectedGroup is { } selectedGroup
            ? ViewModel.FeedNavigationRows
                .Where(item => item.Group?.Id == selectedGroup.Id)
                .ToHashSet()
            : ViewModel.FeedNavigationRows
                .Where(item => item.Feed is not null && selectedFeedIds.Contains(item.Feed.Id))
                .ToHashSet();
        ApplyFeedListSelection(desiredItems);
    }

    private void SetFeedGroupSelectionIndicatorsVisible(bool value)
    {
        if (_areFeedGroupSelectionIndicatorsVisible == value)
        {
            return;
        }

        // Extended range selection briefly selects intervening group rows before
        // SelectionChanged removes them. Keep their native indicator visually hidden
        // while feeds are selected so that transient state cannot animate onscreen.
        _areFeedGroupSelectionIndicatorsVisible = value;
        foreach (var item in ViewModel.FeedNavigationRows.Where(item => item.IsGroup))
        {
            if (FeedList.ContainerFromItem(item) is ListViewItem container)
            {
                SetFeedSelectionIndicatorVisible(container, value);
            }
        }
    }

    private void SetFeedSelectionIndicatorVisible(
        ListViewItem container,
        bool isVisible)
    {
        // Keep the native indicator element alive. WinUI pointer visual states can
        // still update its background after SelectionIndicatorVisualEnabled is disabled.
        foreach (var resourceKey in FeedSelectionIndicatorBrushResourceKeys)
        {
            if (isVisible)
            {
                container.Resources.Remove(resourceKey);
            }
            else
            {
                container.Resources[resourceKey] = _transparentFeedSelectionIndicatorBrush;
            }
        }
    }

    private void ApplyFeedListSelection(IReadOnlySet<FeedNavigationItem> desiredItems)
    {
        var currentItems = FeedList.SelectedItems
            .OfType<FeedNavigationItem>()
            .ToHashSet();
        var itemsToRemove = currentItems.Except(desiredItems).ToArray();
        var itemsToAdd = desiredItems.Except(currentItems).ToArray();
        if (itemsToRemove.Length == 0 && itemsToAdd.Length == 0)
        {
            return;
        }

        _isSynchronizingFeedListSelection = true;
        try
        {
            foreach (var item in itemsToRemove)
            {
                FeedList.SelectedItems.Remove(item);
            }

            foreach (var item in itemsToAdd)
            {
                FeedList.SelectedItems.Add(item);
            }
        }
        finally
        {
            _isSynchronizingFeedListSelection = false;
        }
    }

    private void NavigationItemMenu_Opened(object sender, object e)
    {
        if (sender is MenuFlyout menu)
        {
            foreach (var item in menu.Items)
            {
                item.IsEnabled = !ViewModel.IsBusy;
            }
        }

        // TODO(winui): Remove this workaround after microsoft-ui-xaml#9542 is fixed
        // and the project uses a Windows App SDK version that contains the fix.
        // Opening a ContextFlyout can currently leave a loading or resize cursor
        // active until the pointer moves again.
        NativeCursor.SetArrow();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, NativeCursor.SetArrow);
    }

    private async void RefreshNavigationFeedMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: FeedNavigationItem item })
        {
            return;
        }

        if (item.Feed is { } feed)
        {
            await ViewModel.RefreshFeedAsync(feed, _lifetime.Token);
        }
        else if (item.Group is { } group)
        {
            await ViewModel.RefreshGroupAsync(group, _lifetime.Token);
        }
    }

    private async void EditNavigationFeedMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: FeedNavigationItem { Feed: { } feed } })
        {
            await EditFeedAsync(feed);
        }
    }

    private async void PrimaryNavigationItemMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: FeedNavigationItem item })
        {
            return;
        }

        if (item.Feed is not null)
        {
            await ChangeFeedGroupAsync(GetActionFeeds(item));
        }
        else if (item.Group is not null)
        {
            await RenameGroupAsync(item.Group);
        }
    }

    private async void RemoveNavigationItemMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: FeedNavigationItem item })
        {
            return;
        }

        if (item.Feed is not null)
        {
            await ConfirmDeleteFeedsAsync(GetActionFeeds(item));
        }
        else if (item.Group is not null)
        {
            await ConfirmDeleteGroupAsync(item.Group);
        }
    }

    private async void ArticleList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Article article)
        {
            return;
        }

        ArticleEmptyView.Visibility = Visibility.Collapsed;
        ArticleReaderView.Visibility = Visibility.Visible;
        await ViewModel.SelectArticleAsync(article, _lifetime.Token);
    }

    private void Articles_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.SelectedArticle is { } article && ViewModel.Articles.Contains(article))
        {
            ArticleList.SelectedItem = article;
        }
    }

    private async void AllArticles_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SelectAllArticlesAsync(_lifetime.Token);
        HideArticleReader();
    }

    private async void UnreadFilterToggle_Click(object sender, RoutedEventArgs e)
    {
        var filter = UnreadFilterToggleButton.IsChecked == true
            ? ArticleFilter.Unread
            : ArticleFilter.All;
        await ViewModel.SetArticleFilterAsync(filter, _lifetime.Token);
    }

    private async void ArticleSearchBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _articleSearchDebounce?.Cancel();
        var searchDebounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _articleSearchDebounce = searchDebounce;
        try
        {
            await Task.Delay(ArticleSearchDebounceDelay, searchDebounce.Token);
            await ViewModel.SetArticleSearchQueryAsync(sender.Text, searchDebounce.Token);
            HideArticleReader();
        }
        catch (OperationCanceledException) when (searchDebounce.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_articleSearchDebounce, searchDebounce))
            {
                _articleSearchDebounce = null;
            }

            searchDebounce.Dispose();
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || SettingsFrame.Visibility == Visibility.Visible)
        {
            return;
        }

        SettingsFrame.Visibility = Visibility.Visible;
        SettingsFrame.Navigate(
            typeof(SettingsPage),
            null,
            new EntranceNavigationTransitionInfo());
        if (SettingsFrame.Content is not SettingsPage settingsPage)
        {
            ResetSettingsFrame();
            return;
        }

        settingsPage.Initialize(
            _settings.Theme,
            App.Current.Localization.CurrentLanguage,
            _settings.RefreshIntervalMinutes,
            _settings.RefreshConcurrencyLimit,
            _settings.RequestTimeoutSeconds,
            _settings.LoadExternalArticleStylesheets,
            _settings.ProxyMode,
            _settings.CustomProxyAddress);
        settingsPage.BackRequested += SettingsPage_BackRequested;
        settingsPage.ThemeChanged += SettingsPage_ThemeChanged;
        settingsPage.LanguageChanged += SettingsPage_LanguageChanged;
        settingsPage.RefreshIntervalChanged += SettingsPage_RefreshIntervalChanged;
        settingsPage.RefreshConcurrencyLimitChanged += SettingsPage_RefreshConcurrencyLimitChanged;
        settingsPage.RequestTimeoutChanged += SettingsPage_RequestTimeoutChanged;
        settingsPage.ExternalStylesheetsChanged += SettingsPage_ExternalStylesheetsChanged;
        settingsPage.ProxyChanged += SettingsPage_ProxyChanged;
        settingsPage.ImportSubscriptionsRequested += SettingsPage_ImportSubscriptionsRequested;
        settingsPage.ExportSubscriptionsRequested += SettingsPage_ExportSubscriptionsRequested;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || SettingsFrame.Visibility == Visibility.Visible)
        {
            return;
        }

        SettingsFrame.Visibility = Visibility.Visible;
        SettingsFrame.Navigate(
            typeof(AboutPage),
            null,
            new EntranceNavigationTransitionInfo());
        if (SettingsFrame.Content is not AboutPage aboutPage)
        {
            ResetSettingsFrame();
            return;
        }

        aboutPage.BackRequested += AboutPage_BackRequested;
    }

    private async void SettingsPage_ThemeChanged(object? sender, EventArgs e)
    {
        if (sender is not SettingsPage settingsPage)
        {
            return;
        }

        var theme = settingsPage.SelectedTheme;
        ApplyTheme(theme);
        _settings = _settings with { Theme = theme };
        await SaveSettingsAsync();
    }

    private async void SettingsPage_LanguageChanged(object? sender, EventArgs e)
    {
        if (sender is not SettingsPage settingsPage)
        {
            return;
        }

        var language = settingsPage.SelectedLanguage;
        App.Current.Localization.SetLanguage(language);
        _settings = _settings with { Language = language };
        StatusInfoBar.IsOpen = false;
        ApplyLocalization();
        ViewModel.ApplyLocalization();
        settingsPage.ApplyLocalization();
        await SaveSettingsAsync();
    }

    private async void SettingsPage_RefreshIntervalChanged(object? sender, EventArgs e)
    {
        if (sender is not SettingsPage settingsPage)
        {
            return;
        }

        var refreshIntervalMinutes = NormalizeRefreshInterval(settingsPage.RefreshIntervalMinutes);
        ApplyRefreshInterval(refreshIntervalMinutes);
        _settings = _settings with { RefreshIntervalMinutes = refreshIntervalMinutes };
        await SaveSettingsAsync();
    }

    private async void SettingsPage_RefreshConcurrencyLimitChanged(object? sender, EventArgs e)
    {
        if (sender is not SettingsPage settingsPage)
        {
            return;
        }

        var refreshConcurrencyLimit = NormalizeRefreshConcurrencyLimit(
            settingsPage.RefreshConcurrencyLimit);
        ViewModel.RefreshConcurrencyLimit = refreshConcurrencyLimit;
        _settings = _settings with { RefreshConcurrencyLimit = refreshConcurrencyLimit };
        await SaveSettingsAsync();
    }

    private async void SettingsPage_RequestTimeoutChanged(object? sender, EventArgs e)
    {
        if (sender is not SettingsPage settingsPage)
        {
            return;
        }

        var requestTimeoutSeconds = NormalizeRequestTimeout(settingsPage.RequestTimeoutSeconds);
        ApplyRequestTimeout(requestTimeoutSeconds);
        _settings = _settings with { RequestTimeoutSeconds = requestTimeoutSeconds };
        await SaveSettingsAsync();
    }

    private async void SettingsPage_ExternalStylesheetsChanged(object? sender, EventArgs e)
    {
        if (sender is not SettingsPage settingsPage)
        {
            return;
        }

        _settings = _settings with
        {
            LoadExternalArticleStylesheets = settingsPage.LoadExternalArticleStylesheets
        };
        await SaveSettingsAsync();
        await RenderSelectedArticleAsync();
    }

    private async void SettingsPage_ProxyChanged(object? sender, EventArgs e)
    {
        if (sender is not SettingsPage settingsPage ||
            !settingsPage.TryGetProxyConfiguration(out var proxyMode, out var customProxyAddress))
        {
            return;
        }

        App.Current.Proxy.Configure(proxyMode, customProxyAddress);
        _settings = _settings with
        {
            ProxyMode = proxyMode,
            CustomProxyAddress = customProxyAddress
        };
        await SaveSettingsAsync();
    }

    private async void SettingsPage_ImportSubscriptionsRequested(object? sender, EventArgs e)
    {
        if (sender is not SettingsPage settingsPage)
        {
            return;
        }

        var localization = App.Current.Localization;
        if (ViewModel.IsBusy)
        {
            DiagnosticLog.Warning("opml.import_rejected", new { reason = "view_model_busy" });
            settingsPage.ShowSubscriptionStatus(
                localization.GetString("SubscriptionOperationBusy"),
                isError: true);
            return;
        }

        settingsPage.SetSubscriptionActionsEnabled(false);
        var importStartedAt = Stopwatch.GetTimestamp();
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".opml");
            picker.FileTypeFilter.Add(".xml");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                DiagnosticLog.Information("opml.file_picker_cancelled");
                return;
            }

            var content = await FileIO.ReadTextAsync(file);
            var document = OpmlSubscriptionSerializer.Parse(content);
            DiagnosticLog.Information(
                "opml.file_parsed",
                new
                {
                    fileName = file.Name,
                    contentCharacterCount = content.Length,
                    subscriptionCount = document.Subscriptions.Count,
                    document.SkippedOutlineCount
                });
            if (document.Subscriptions.Count == 0)
            {
                settingsPage.ShowSubscriptionStatus(
                    localization.GetString("NoValidSubscriptionsInFile"),
                    isError: true);
                return;
            }

            var result = await ViewModel.ImportSubscriptionsAsync(document, _lifetime.Token);
            DiagnosticLog.MemorySnapshot(
                "opml.import_ui_completed",
                new
                {
                    result.ImportedCount,
                    result.SkippedCount,
                    result.FailedCount,
                    elapsedMilliseconds = Stopwatch.GetElapsedTime(importStartedAt).TotalMilliseconds
                });
            settingsPage.ShowSubscriptionStatus(
                localization.Format(
                    "SubscriptionImportComplete",
                    result.ImportedCount,
                    result.SkippedCount,
                    result.FailedCount),
                isError: result.FailedCount > 0);
            ViewModel.RefreshImportedFeedsInBackground(
                result.ImportedFeedIds,
                _lifetime.Token);
        }
        catch (Exception exception) when (exception is XmlException or FormatException or ArgumentException)
        {
            DiagnosticLog.Error("opml.parse_failed", exception);
            settingsPage.ShowSubscriptionStatus(
                localization.GetString("InvalidSubscriptionFile"),
                isError: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            DiagnosticLog.Information("opml.import_cancelled", new { reason = "window_closing" });
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("opml.import_ui_failed", exception);
            settingsPage.ShowSubscriptionStatus(
                localization.Format("SubscriptionImportFailed", exception.Message),
                isError: true);
        }
        finally
        {
            settingsPage.SetSubscriptionActionsEnabled(true);
        }
    }

    private async void SettingsPage_ExportSubscriptionsRequested(object? sender, EventArgs e)
    {
        if (sender is not SettingsPage settingsPage)
        {
            return;
        }

        var localization = App.Current.Localization;
        if (ViewModel.IsBusy)
        {
            settingsPage.ShowSubscriptionStatus(
                localization.GetString("SubscriptionOperationBusy"),
                isError: true);
            return;
        }

        var subscriptions = ViewModel.GetSubscriptionsForExport();
        if (subscriptions.Count == 0)
        {
            settingsPage.ShowSubscriptionStatus(
                localization.GetString("NoSubscriptionsToExport"),
                isError: true);
            return;
        }

        settingsPage.SetSubscriptionActionsEnabled(false);
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "FluxReader-subscriptions"
            };
            picker.FileTypeChoices.Add("OPML", new List<string> { ".opml" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            var content = OpmlSubscriptionSerializer.Serialize(subscriptions);
            await FileIO.WriteTextAsync(file, content);
            settingsPage.ShowSubscriptionStatus(
                localization.Format("SubscriptionExportComplete", subscriptions.Count),
                isError: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            settingsPage.ShowSubscriptionStatus(
                localization.Format("SubscriptionExportFailed", exception.Message),
                isError: true);
        }
        finally
        {
            settingsPage.SetSubscriptionActionsEnabled(true);
        }
    }

    private void SettingsPage_BackRequested(object? sender, EventArgs e) => CloseSettingsPage();

    private void AboutPage_BackRequested(object? sender, EventArgs e) => CloseSettingsPage();

    private void CloseSettingsPage()
    {
        if (SettingsFrame.Content is SettingsPage settingsPage)
        {
            settingsPage.BackRequested -= SettingsPage_BackRequested;
            settingsPage.ThemeChanged -= SettingsPage_ThemeChanged;
            settingsPage.LanguageChanged -= SettingsPage_LanguageChanged;
            settingsPage.RefreshIntervalChanged -= SettingsPage_RefreshIntervalChanged;
            settingsPage.RefreshConcurrencyLimitChanged -= SettingsPage_RefreshConcurrencyLimitChanged;
            settingsPage.RequestTimeoutChanged -= SettingsPage_RequestTimeoutChanged;
            settingsPage.ExternalStylesheetsChanged -= SettingsPage_ExternalStylesheetsChanged;
            settingsPage.ProxyChanged -= SettingsPage_ProxyChanged;
            settingsPage.ImportSubscriptionsRequested -= SettingsPage_ImportSubscriptionsRequested;
            settingsPage.ExportSubscriptionsRequested -= SettingsPage_ExportSubscriptionsRequested;
        }
        else if (SettingsFrame.Content is AboutPage aboutPage)
        {
            aboutPage.BackRequested -= AboutPage_BackRequested;
        }

        ResetSettingsFrame();
    }

    private void ResetSettingsFrame()
    {
        SettingsFrame.Visibility = Visibility.Collapsed;
        SettingsFrame.Navigate(
            typeof(OverlayPlaceholderPage),
            null,
            new SuppressNavigationTransitionInfo());
        SettingsFrame.BackStack.Clear();
    }

    private void ApplyTheme(AppTheme theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void ApplyRefreshInterval(int refreshIntervalMinutes)
    {
        var restartTimer = _refreshTimer.IsEnabled;
        if (restartTimer)
        {
            _refreshTimer.Stop();
        }

        _refreshTimer.Interval = TimeSpan.FromMinutes(refreshIntervalMinutes);
        if (restartTimer)
        {
            _refreshTimer.Start();
        }
    }

    private void ApplyRequestTimeout(int requestTimeoutSeconds)
    {
        App.Current.RefreshService.RequestTimeoutSeconds = requestTimeoutSeconds;
        _articleStylesheetService.RequestTimeoutSeconds = requestTimeoutSeconds;
    }

    private void ApplyLocalization()
    {
        var localization = App.Current.Localization;
        RootGrid.Language = localization.LanguageTag;
        AutomationProperties.SetName(BrandIcon, localization.GetString("AppIconAutomation"));

        var addFeed = localization.GetString("AddFeed");
        AutomationProperties.SetName(AddFeedButton, addFeed);
        ToolTipService.SetToolTip(AddFeedButton, addFeed);

        var refreshAllFeeds = localization.GetString("RefreshAllFeeds");
        AutomationProperties.SetName(RefreshButton, refreshAllFeeds);
        UpdateRefreshButtonToolTip();

        var addGroup = localization.GetString("AddGroup");
        AutomationProperties.SetName(AddGroupButton, addGroup);
        ToolTipService.SetToolTip(AddGroupButton, addGroup);

        var settings = localization.GetString("Settings");
        AutomationProperties.SetName(SettingsButton, settings);
        ToolTipService.SetToolTip(SettingsButton, settings);

        var about = localization.GetString("About");
        AutomationProperties.SetName(AboutButton, about);
        ToolTipService.SetToolTip(AboutButton, about);
        AllArticlesText.Text = localization.GetString("AllArticles");
        FeedsHeaderText.Text = localization.GetString("Feeds");

        var showUnreadOnly = localization.GetString("ShowUnreadOnly");
        AutomationProperties.SetName(UnreadFilterToggleButton, showUnreadOnly);
        ToolTipService.SetToolTip(UnreadFilterToggleButton, showUnreadOnly);

        var searchArticles = localization.GetString("SearchArticles");
        ArticleSearchBox.PlaceholderText = searchArticles;
        AutomationProperties.SetName(ArticleSearchBox, searchArticles);

        var markAllRead = localization.GetString("MarkAllRead");
        AutomationProperties.SetName(MarkCurrentListReadButton, markAllRead);
        ToolTipService.SetToolTip(MarkCurrentListReadButton, markAllRead);

        var resizeTooltip = localization.GetString("ResizePaneTooltip");
        AutomationProperties.SetName(FeedPaneSplitterThumb, localization.GetString("ResizeFeedPane"));
        ToolTipService.SetToolTip(FeedPaneSplitterThumb, resizeTooltip);
        AutomationProperties.SetName(
            ArticleListPaneSplitterThumb,
            localization.GetString("ResizeArticleListPane"));
        ToolTipService.SetToolTip(ArticleListPaneSplitterThumb, resizeTooltip);

        EmptyArticleTitleText.Text = localization.GetString("SelectArticle");
        EmptyArticleDescriptionText.Text = localization.GetString("ArticleContentHint");
        var loadingArticle = localization.GetString("LoadingArticle");
        ArticleLoadingText.Text = loadingArticle;
        AutomationProperties.SetName(ArticleLoadingProgressRing, loadingArticle);
        OpenInBrowserText.Text = localization.GetString("OpenInBrowser");
        MarkUnreadText.Text = localization.GetString("MarkUnread");
    }

    private void ShowArticleLoading()
    {
        // Keep WebView2 rendered while it navigates. The opaque loading layer
        // covers the stale document without throttling the browser process.
        ArticleWebView.Visibility = Visibility.Visible;
        ArticleWebView.IsHitTestVisible = false;
        ArticleLoadingView.Visibility = Visibility.Visible;
        ArticleLoadingProgressRing.IsActive = true;
    }

    private void ShowArticleContent()
    {
        ArticleLoadingProgressRing.IsActive = false;
        ArticleLoadingView.Visibility = Visibility.Collapsed;
        ArticleWebView.IsHitTestVisible = true;
    }

    private void HideArticleContent()
    {
        ArticleLoadingProgressRing.IsActive = false;
        ArticleLoadingView.Visibility = Visibility.Collapsed;
        ArticleWebView.IsHitTestVisible = false;
        ArticleWebView.Visibility = Visibility.Collapsed;
    }

    private void UpdateRefreshButtonToolTip()
    {
        var localization = App.Current.Localization;
        var refreshAllFeeds = localization.GetString("RefreshAllFeeds");
        var lastRefreshedAt = ViewModel.LastRefreshedAt;
        var toolTip = lastRefreshedAt is { } value
            ? $"{refreshAllFeeds}\n{localization.Format(
                "LastRefreshedAt",
                value.ToLocalTime().ToString("G", localization.CurrentCulture))}"
            : refreshAllFeeds;
        ToolTipService.SetToolTip(RefreshButton, toolTip);
    }

    private void UpdateRefreshButtonVisualState()
    {
        if (ViewModel.IsBusy)
        {
            _refreshIconSpinStoryboard.Begin();
            return;
        }

        _refreshIconSpinStoryboard.Stop();
        RefreshIconRotation.Angle = 0;
    }

    private void ConfigureRefreshIconAnimation()
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromMilliseconds(800),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, RefreshIconRotation);
        Storyboard.SetTargetProperty(animation, nameof(RotateTransform.Angle));
        _refreshIconSpinStoryboard.Children.Add(animation);
    }

    private void ConfigureStatusInfoBarAnimation()
    {
        var animation = new PopInThemeAnimation();
        Storyboard.SetTarget(animation, StatusInfoBar);
        _statusInfoBarEntranceStoryboard.Children.Add(animation);
        _statusInfoBarIsOpenCallbackToken = StatusInfoBar.RegisterPropertyChangedCallback(
            InfoBar.IsOpenProperty,
            StatusInfoBar_IsOpenChanged);
    }

    private void StatusInfoBar_IsOpenChanged(DependencyObject sender, DependencyProperty property)
    {
        if (StatusInfoBar.IsOpen)
        {
            _statusInfoBarEntranceStoryboard.Begin();
        }
    }

    private async void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateTitleBarButtonColors();
        await RenderSelectedArticleAsync();
    }

    private void UpdateTitleBarButtonColors()
    {
        var isDarkTheme = RootGrid.ActualTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonForegroundColor = isDarkTheme ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Colors.Gray;
    }

    private void HideArticleReader()
    {
        ArticleList.SelectedItem = null;
        ArticleEmptyView.Visibility = Visibility.Visible;
        ArticleReaderView.Visibility = Visibility.Collapsed;
    }

    private ComboBox CreateGroupSelector(long? selectedGroupId)
    {
        var selector = new ComboBox
        {
            Header = App.Current.Localization.GetString("FeedGroup"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        selector.Items.Add(new ComboBoxItem
        {
            Content = App.Current.Localization.GetString("NoGroup")
        });
        var selectedIndex = 0;
        foreach (var group in ViewModel.FeedGroups)
        {
            selector.Items.Add(new ComboBoxItem
            {
                Content = group.Name,
                Tag = group.Id
            });
            if (group.Id == selectedGroupId)
            {
                selectedIndex = selector.Items.Count - 1;
            }
        }

        selector.SelectedIndex = selectedIndex;
        return selector;
    }

    private static long? GetSelectedGroupId(ComboBox selector) =>
        selector.SelectedItem is ComboBoxItem { Tag: long groupId } ? groupId : null;

    private IReadOnlyList<Feed> GetActionFeeds(FeedNavigationItem item)
    {
        if (item.Feed is null)
        {
            return [];
        }

        if (!FeedList.SelectedItems.Contains(item) || FeedList.SelectedItems.Count <= 1)
        {
            return [item.Feed];
        }

        return FeedList.SelectedItems
            .OfType<FeedNavigationItem>()
            .Where(selectedItem => selectedItem.Feed is not null)
            .Select(selectedItem => selectedItem.Feed!)
            .ToArray();
    }

    private async Task ChangeFeedGroupAsync(IReadOnlyList<Feed> feeds)
    {
        if (feeds.Count == 0)
        {
            return;
        }

        var localization = App.Current.Localization;
        var groupIds = feeds.Select(feed => feed.GroupId).Distinct().ToArray();
        var hasSharedGroup = groupIds.Length == 1;
        var groupSelector = CreateGroupSelector(hasSharedGroup ? groupIds[0] : null);
        if (!hasSharedGroup)
        {
            groupSelector.SelectedIndex = -1;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = localization.GetString("ChangeGroup"),
            Content = groupSelector,
            PrimaryButtonText = localization.GetString("Save"),
            CloseButtonText = localization.GetString("Cancel"),
            // TODO: Recheck when WinUI preserves the default button's accent style after
            // releasing a Close-button press outside it.
            // Related: https://github.com/microsoft/microsoft-ui-xaml/issues/5035
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = hasSharedGroup
        };
        groupSelector.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = true;

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.SetFeedsGroupAsync(feeds, GetSelectedGroupId(groupSelector), _lifetime.Token);
            HideArticleReader();
        }
    }

    private async Task RenameGroupAsync(FeedGroup group)
    {
        var localization = App.Current.Localization;
        var input = new TextBox
        {
            Header = localization.GetString("GroupName"),
            MaxLength = 100,
            Text = group.Name
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = localization.GetString("RenameGroup"),
            Content = input,
            PrimaryButtonText = localization.GetString("Save"),
            CloseButtonText = localization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RenameFeedGroupAsync(group, input.Text, _lifetime.Token);
        }
    }

    private async Task EditFeedAsync(Feed feed)
    {
        var localization = App.Current.Localization;
        var nameInput = new TextBox
        {
            Header = localization.GetString("FeedName"),
            MaxLength = 200,
            Text = feed.Title
        };
        var addressInput = new TextBox
        {
            Header = localization.GetString("FeedAddress"),
            MaxLength = 2048,
            Text = feed.Url
        };
        var content = new StackPanel
        {
            Width = 320,
            Spacing = 12
        };
        content.Children.Add(nameInput);
        content.Children.Add(addressInput);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = localization.GetString("EditFeed"),
            Content = content,
            PrimaryButtonText = localization.GetString("Save"),
            CloseButtonText = localization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.UpdateFeedSubscriptionAsync(
                feed,
                nameInput.Text,
                addressInput.Text,
                _lifetime.Token);
        }
    }

    private async Task ConfirmDeleteFeedsAsync(IReadOnlyList<Feed> feeds)
    {
        if (feeds.Count == 0)
        {
            return;
        }

        var localization = App.Current.Localization;
        var isBatch = feeds.Count > 1;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = localization.GetString(isBatch ? "RemoveFeedsTitle" : "RemoveFeedTitle"),
            Content = isBatch
                ? localization.Format("RemoveFeedsMessage", feeds.Count)
                : localization.Format("RemoveFeedMessage", feeds[0].Title),
            PrimaryButtonText = localization.GetString("Remove"),
            PrimaryButtonStyle = (Style)App.Current.Resources["DestructiveButtonStyle"],
            CloseButtonText = localization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.None
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (await ViewModel.DeleteFeedsAsync(feeds, _lifetime.Token))
            {
                HideArticleReader();
            }
        }
    }

    private async Task ConfirmDeleteGroupAsync(FeedGroup group)
    {
        var localization = App.Current.Localization;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = localization.GetString("RemoveGroupTitle"),
            Content = localization.Format("RemoveGroupMessage", group.Name),
            PrimaryButtonText = localization.GetString("RemoveGroupAndFeeds"),
            PrimaryButtonStyle = (Style)App.Current.Resources["WrappingDestructiveButtonStyle"],
            SecondaryButtonText = localization.GetString("RemoveGroupOnly"),
            SecondaryButtonStyle = (Style)App.Current.Resources["WrappingDialogButtonStyle"],
            CloseButtonText = localization.GetString("Cancel"),
            CloseButtonStyle = (Style)App.Current.Resources["WrappingDialogButtonStyle"],
            DefaultButton = ContentDialogButton.None
        };

        var result = await dialog.ShowAsync();
        if (result is ContentDialogResult.Primary or ContentDialogResult.Secondary)
        {
            var deleteFeeds = result == ContentDialogResult.Primary;
            if (await ViewModel.DeleteFeedGroupAsync(group, deleteFeeds, _lifetime.Token))
            {
                HideArticleReader();
            }
        }
    }

    private void FeedPaneSplitter_DragDelta(object sender, DragDeltaEventArgs e) =>
        SetFeedPaneWidth(FeedPaneColumn.ActualWidth + e.HorizontalChange);

    private void ArticleListPaneSplitter_DragDelta(object sender, DragDeltaEventArgs e) =>
        SetArticleListPaneWidth(ArticleListPaneColumn.ActualWidth + e.HorizontalChange);

    private async void PaneSplitter_DragCompleted(object sender, DragCompletedEventArgs e) =>
        await PersistPaneWidthsAsync();

    private async void FeedPaneSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SetFeedPaneWidth(DefaultFeedPaneWidth);
        await PersistPaneWidthsAsync();
    }

    private async void ArticleListPaneSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SetArticleListPaneWidth(DefaultArticleListPaneWidth);
        await PersistPaneWidthsAsync();
    }

    private async void FeedPaneSplitter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!TryGetKeyboardResizeDelta(e, out var delta))
        {
            return;
        }

        SetFeedPaneWidth(FeedPaneColumn.ActualWidth + delta);
        e.Handled = true;
        await PersistPaneWidthsAsync();
    }

    private async void ArticleListPaneSplitter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!TryGetKeyboardResizeDelta(e, out var delta))
        {
            return;
        }

        SetArticleListPaneWidth(ArticleListPaneColumn.ActualWidth + delta);
        e.Handled = true;
        await PersistPaneWidthsAsync();
    }

    private void MainContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            return;
        }

        SetFeedPaneWidth(FeedPaneColumn.ActualWidth);
        SetArticleListPaneWidth(ArticleListPaneColumn.ActualWidth);
    }

    private void ApplySavedPaneWidths()
    {
        SetFeedPaneWidth(IsValidSavedWidth(_settings.FeedPaneWidth)
            ? _settings.FeedPaneWidth
            : DefaultFeedPaneWidth);
        SetArticleListPaneWidth(IsValidSavedWidth(_settings.ArticleListPaneWidth)
            ? _settings.ArticleListPaneWidth
            : DefaultArticleListPaneWidth);
    }

    private void SetFeedPaneWidth(double requestedWidth)
    {
        var availableMaximum = MainContentGrid.ActualWidth -
                               ArticleListPaneColumn.ActualWidth -
                               MinimumReaderPaneWidth -
                               (SplitterWidth * 2);
        var maximum = Math.Max(MinimumFeedPaneWidth, Math.Min(MaximumFeedPaneWidth, availableMaximum));
        FeedPaneColumn.Width = new GridLength(Math.Clamp(requestedWidth, MinimumFeedPaneWidth, maximum));
    }

    private void SetArticleListPaneWidth(double requestedWidth)
    {
        var availableMaximum = MainContentGrid.ActualWidth -
                               FeedPaneColumn.ActualWidth -
                               MinimumReaderPaneWidth -
                               (SplitterWidth * 2);
        var maximum = Math.Max(
            MinimumArticleListPaneWidth,
            Math.Min(MaximumArticleListPaneWidth, availableMaximum));
        ArticleListPaneColumn.Width = new GridLength(
            Math.Clamp(requestedWidth, MinimumArticleListPaneWidth, maximum));
    }

    private async Task PersistPaneWidthsAsync()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        _settings = _settings with
        {
            FeedPaneWidth = FeedPaneColumn.ActualWidth,
            ArticleListPaneWidth = ArticleListPaneColumn.ActualWidth
        };
        await SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await App.Current.Settings.SaveAsync(_settings, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private static bool IsValidSavedWidth(double width) => double.IsFinite(width) && width > 0;

    private static int NormalizeRefreshInterval(int refreshIntervalMinutes) =>
        refreshIntervalMinutes > 0 ? refreshIntervalMinutes : DefaultRefreshIntervalMinutes;

    private static int NormalizeRefreshConcurrencyLimit(int refreshConcurrencyLimit) =>
        refreshConcurrencyLimit >= 0
            ? refreshConcurrencyLimit
            : SettingsService.DefaultRefreshConcurrencyLimit;

    private static int NormalizeRequestTimeout(int requestTimeoutSeconds) =>
        requestTimeoutSeconds is >= 0 and <= RequestTimeoutHandler.MaximumTimeoutSeconds
            ? requestTimeoutSeconds
            : SettingsService.DefaultRequestTimeoutSeconds;

    private static bool TryGetKeyboardResizeDelta(KeyRoutedEventArgs e, out double delta)
    {
        delta = e.Key switch
        {
            Windows.System.VirtualKey.Left => -16,
            Windows.System.VirtualKey.Right => 16,
            _ => 0
        };
        return delta != 0;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        Closed -= MainWindow_Closed;
        CloseSettingsPage();
        RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
        ViewModel.Articles.CollectionChanged -= Articles_CollectionChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.StatusNotificationRequested -= ViewModel_StatusNotificationRequested;
        if (_articleWebViewConfigured && ArticleWebView.CoreWebView2 is { } coreWebView)
        {
            coreWebView.WebResourceRequested -= ArticleWebView_WebResourceRequested;
            coreWebView.DOMContentLoaded -= ArticleWebView_DOMContentLoaded;
            coreWebView.WebMessageReceived -= ArticleWebView_WebMessageReceived;
            coreWebView.NewWindowRequested -= ArticleWebView_NewWindowRequested;
        }

        ArticleWebView.NavigationStarting -= ArticleWebView_NavigationStarting;
        ArticleWebView.NavigationCompleted -= ArticleWebView_NavigationCompleted;
        StatusInfoBar.UnregisterPropertyChangedCallback(
            InfoBar.IsOpenProperty,
            _statusInfoBarIsOpenCallbackToken);
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        _diagnosticTimer.Stop();
        _diagnosticTimer.Tick -= DiagnosticTimer_Tick;
        _statusNotificationTimer.Stop();
        _statusNotificationTimer.Tick -= StatusNotificationTimer_Tick;
        _articleSearchDebounce?.Cancel();
        _lifetime.Cancel();
        _articleStylesheetService.Dispose();
        _lifetime.Dispose();
    }

    private void RefreshTimer_Tick(object? sender, object e)
    {
        if (!ViewModel.IsBusy && ViewModel.Feeds.Count > 0 && ViewModel.RefreshCommand.CanExecute(null))
        {
            DiagnosticLog.Information(
                "refresh.timer_triggered",
                new { feedCount = ViewModel.Feeds.Count });
            ViewModel.RefreshCommand.Execute(null);
        }
    }

    private void DiagnosticTimer_Tick(object? sender, object e)
    {
        var article = ViewModel.SelectedArticle;
        DiagnosticLog.MemorySnapshot(
            "app.heartbeat",
            new
            {
                ViewModel.IsBusy,
                feedCount = ViewModel.Feeds.Count,
                articleCount = ViewModel.Articles.Count,
                selectedArticleId = article?.Id,
                selectedFeedId = article?.FeedId,
                selectedContentContainsHtml = ArticleContentParser.ContainsHtmlMarkup(article?.DisplayContent)
            });
    }
}
