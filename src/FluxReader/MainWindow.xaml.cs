using System.Xml;
using FluxReader.Controls;
using FluxReader.Core.Services;
using FluxReader.Models;
using FluxReader.Services;
using FluxReader.ViewModels;
using FluxReader.Interop;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using WinRT.Interop;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using VirtualKey = Windows.System.VirtualKey;

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

    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromMinutes(DefaultRefreshIntervalMinutes)
    };
    private readonly Dictionary<TreeViewItem, SelectionIndicatorMonitor> _selectionIndicatorMonitors = [];
    private long? _feedSelectionAnchorId;
    private TreeViewItem? _feedPointerContainer;
    private bool _isFeedPointerPressed;
    private bool _isFeedSelectionVisualUpdateQueued;
    private AppSettings _settings = new();
    private bool _settingsLoaded;

    public MainWindow()
    {
        InitializeComponent();
        Title = "FluxReader";
        SetWindowIcon();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        UpdateTitleBarButtonColors();

        var app = App.Current;
        ViewModel = new MainViewModel(
            app.Repository,
            app.RefreshService,
            app.Notifications,
            app.Localization);
        RootGrid.DataContext = ViewModel;
        ApplyLocalization();
        RootGrid.Loaded += RootGrid_Loaded;
        FeedTree.LayoutUpdated += FeedTree_LayoutUpdated;
        FeedTree.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(FeedTree_PointerMoved), true);
        FeedTree.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(FeedTree_PointerExited), true);
        FeedTree.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(FeedTree_PointerPressed), true);
        FeedTree.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(FeedTree_PointerReleased), true);
        FeedTree.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(FeedTree_PointerCanceled), true);
        FeedTree.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(FeedTree_PointerCaptureLost), true);
        _refreshTimer.Tick += RefreshTimer_Tick;
        Closed += MainWindow_Closed;
    }

    public MainViewModel ViewModel { get; }

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

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        _settings = await App.Current.Settings.LoadAsync(_lifetime.Token);
        AppLanguage? languagePreference = _settings.Language is { } savedLanguage && Enum.IsDefined(savedLanguage)
            ? savedLanguage
            : null;
        _settings = _settings with
        {
            Language = languagePreference,
            RefreshIntervalMinutes = NormalizeRefreshInterval(_settings.RefreshIntervalMinutes)
        };
        App.Current.Localization.SetLanguage(
            App.Current.Localization.ResolveLanguage(languagePreference));
        ApplyLocalization();
        ViewModel.ApplyLocalization();
        ApplyTheme(_settings.Theme);
        ApplySavedPaneWidths();
        ApplyRefreshInterval(_settings.RefreshIntervalMinutes);
        _settingsLoaded = true;
        await ViewModel.InitializeAsync(_lifetime.Token);
        if (ViewModel.Feeds.Count > 0 && ViewModel.RefreshCommand.CanExecute(null))
        {
            await ViewModel.RefreshCommand.ExecuteAsync(null);
        }

        _refreshTimer.Start();
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
            Title = localization.GetString("AddFeed"),
            Content = content,
            PrimaryButtonText = localization.GetString("Add"),
            CloseButtonText = localization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.AddFeedAsync(input.Text, GetSelectedGroupId(groupSelector), _lifetime.Token);
            _feedSelectionAnchorId = ViewModel.SelectedFeed?.Id;
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
            Title = localization.GetString("AddGroup"),
            Content = input,
            PrimaryButtonText = localization.GetString("Create"),
            CloseButtonText = localization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.AddFeedGroupAsync(input.Text, _lifetime.Token);
            _feedSelectionAnchorId = null;
            HideArticleReader();
        }
    }

    private async void FeedNavigationItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FeedNavigationItem item } element)
        {
            return;
        }

        if (item.IsGroup &&
            IsWithinNamedElement(e.OriginalSource as DependencyObject, element, "ExpandCollapseChevron"))
        {
            return;
        }

        e.Handled = true;
        await SelectFeedNavigationItemAsync(
            item,
            IsKeyPressed(VirtualKey.Control),
            IsKeyPressed(VirtualKey.Shift));
    }

    private async void FeedTree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Space))
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var item = FindAncestorOrSelf<FeedTreeViewItem>(source)?.Tag as FeedNavigationItem;
        if (item is null && FindOuterTreeViewItem(source) is { } container)
        {
            item = FeedTree.ItemFromContainer(container) as FeedNavigationItem;
        }

        if (item is null)
        {
            return;
        }

        e.Handled = true;
        await SelectFeedNavigationItemAsync(
            item,
            IsKeyPressed(VirtualKey.Control),
            IsKeyPressed(VirtualKey.Shift));
    }

    private async Task SelectFeedNavigationItemAsync(
        FeedNavigationItem item,
        bool isControlPressed,
        bool isShiftPressed)
    {
        if (item.Feed is null)
        {
            _feedSelectionAnchorId = null;
            if (item.Group is not null && ViewModel.SelectedGroup?.Id != item.Group.Id)
            {
                await ViewModel.SelectGroupAsync(item.Group, _lifetime.Token);
                HideArticleReader();
            }

            UpdateFeedSelectionVisuals();
            return;
        }

        var feedId = item.Feed.Id;
        var selection = FeedSelectionResolver.Resolve(
            ViewModel.SelectedFeedIds,
            GetFeedIdsInNavigationOrder(),
            feedId,
            _feedSelectionAnchorId,
            isControlPressed,
            isShiftPressed);
        _feedSelectionAnchorId = selection.AnchorFeedId;
        if (ViewModel.SelectedFeedIds.SetEquals(selection.SelectedFeedIds))
        {
            UpdateFeedSelectionVisuals();
            return;
        }

        await ViewModel.SelectFeedsAsync(selection.SelectedFeedIds, _lifetime.Token);
        UpdateFeedSelectionVisuals();
        HideArticleReader();
    }

    private void FeedTree_LayoutUpdated(object? sender, object e) =>
        ApplyFeedSelectionVisuals(useTransitions: false);

    private void FeedTree_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var container = FindOuterTreeViewItem(e.OriginalSource as DependencyObject);
        if (ReferenceEquals(container, _feedPointerContainer))
        {
            return;
        }

        _feedPointerContainer = container;
        _isFeedPointerPressed = false;
        UpdateFeedSelectionVisuals();
    }

    private void FeedTree_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _feedPointerContainer = null;
        _isFeedPointerPressed = false;
        UpdateFeedSelectionVisuals();
    }

    private void FeedTree_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _feedPointerContainer = FindOuterTreeViewItem(e.OriginalSource as DependencyObject);
        _isFeedPointerPressed = e.GetCurrentPoint(FeedTree).Properties.IsLeftButtonPressed;
        UpdateFeedSelectionVisuals();
    }

    private void FeedTree_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _feedPointerContainer = FindOuterTreeViewItem(e.OriginalSource as DependencyObject);
        _isFeedPointerPressed = false;
        UpdateFeedSelectionVisuals();
    }

    private void FeedTree_PointerCanceled(object sender, PointerRoutedEventArgs e) =>
        ResetFeedPointerPressed();

    private void FeedTree_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        ResetFeedPointerPressed();

    private void ResetFeedPointerPressed()
    {
        _isFeedPointerPressed = false;
        UpdateFeedSelectionVisuals();
    }

    private TreeViewItem? FindOuterTreeViewItem(DependencyObject? source)
    {
        TreeViewItem? outermostItem = null;
        for (var current = source; current is not null && current != FeedTree;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is TreeViewItem item)
            {
                outermostItem = item;
            }
        }

        return outermostItem;
    }

    private void UpdateFeedSelectionVisuals(bool useTransitions = true)
    {
        ApplyFeedSelectionVisuals(useTransitions);
        QueueFeedSelectionVisualUpdate();
    }

    private void QueueFeedSelectionVisualUpdate()
    {
        if (_isFeedSelectionVisualUpdateQueued)
        {
            return;
        }

        _isFeedSelectionVisualUpdateQueued = DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                _isFeedSelectionVisualUpdateQueued = false;
                ApplyFeedSelectionVisuals(useTransitions: false);
            });
    }

    private void ApplyFeedSelectionVisuals(bool useTransitions)
    {
        foreach (var container in EnumerateFeedTreeContainers(FeedTree))
        {
            if (FeedTree.ItemFromContainer(container) is FeedNavigationItem item)
            {
                ApplyFeedSelectionVisual(container, item, useTransitions);
            }
        }
    }

    private static IEnumerable<TreeViewItem> EnumerateFeedTreeContainers(DependencyObject root)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is TreeViewItem container)
            {
                yield return container;
            }

            foreach (var descendant in EnumerateFeedTreeContainers(child))
            {
                yield return descendant;
            }
        }
    }

    private void ApplyFeedSelectionVisual(
        TreeViewItem container,
        FeedNavigationItem item,
        bool useTransitions)
    {
        var isPointerOver = ReferenceEquals(container, _feedPointerContainer);
        var state = !container.IsEnabled
            ? item.IsSelected ? "SelectedDisabled" : "Disabled"
            : item.IsSelected
                ? _isFeedPointerPressed && isPointerOver ? "PressedSelected" : "Selected"
                : _isFeedPointerPressed && isPointerOver
                    ? "Pressed"
                    : isPointerOver ? "PointerOver" : "Normal";

        VisualStateManager.GoToState(container, state, useTransitions);

        if (EnsureSelectionIndicatorMonitor(container) is { } indicator)
        {
            indicator.Opacity = item.IsSelected ? 1 : 0;
        }
    }

    private Rectangle? EnsureSelectionIndicatorMonitor(TreeViewItem container)
    {
        if (_selectionIndicatorMonitors.TryGetValue(container, out var existingMonitor))
        {
            if (VisualTreeHelper.GetParent(existingMonitor.Indicator) is not null)
            {
                return existingMonitor.Indicator;
            }

            existingMonitor.Indicator.UnregisterPropertyChangedCallback(
                UIElement.OpacityProperty,
                existingMonitor.OpacityChangedToken);
            _selectionIndicatorMonitors.Remove(container);
        }

        var indicator = FindNamedDescendant<Rectangle>(container, "SelectionIndicator");
        if (indicator is null)
        {
            return null;
        }

        var opacityChangedToken = indicator.RegisterPropertyChangedCallback(
            UIElement.OpacityProperty,
            (_, _) => SelectionIndicator_OpacityChanged(container, indicator));
        _selectionIndicatorMonitors[container] = new SelectionIndicatorMonitor(
            indicator,
            opacityChangedToken);
        return indicator;
    }

    private void SelectionIndicator_OpacityChanged(
        TreeViewItem container,
        Rectangle indicator)
    {
        if (indicator.Opacity >= 1 ||
            FeedTree.ItemFromContainer(container) is not FeedNavigationItem { IsSelected: true })
        {
            return;
        }

        indicator.Opacity = 1;
    }

    private static T? FindNamedDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T { Name: var childName } match && childName == name)
            {
                return match;
            }

            if (child is TreeViewItem)
            {
                continue;
            }

            if (FindNamedDescendant<T>(child, name) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindAncestorOrSelf<T>(DependencyObject? source)
        where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static bool IsWithinNamedElement(
        DependencyObject? source,
        DependencyObject boundary,
        string elementName)
    {
        for (var current = source; current is not null && current != boundary;)
        {
            if (current is FrameworkElement { Name: var name } && name == elementName)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private List<long> GetFeedIdsInNavigationOrder()
    {
        var feedIds = new List<long>();
        foreach (var item in ViewModel.FeedNavigationItems)
        {
            if (item.Feed is not null)
            {
                feedIds.Add(item.Feed.Id);
            }
            else
            {
                feedIds.AddRange(item.Children
                    .Where(child => child.Feed is not null)
                    .Select(child => child.Feed!.Id));
            }
        }

        return feedIds;
    }

    private static bool IsKeyPressed(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private sealed record SelectionIndicatorMonitor(
        Rectangle Indicator,
        long OpacityChangedToken);

    private void NavigationItemMenu_Opened(object sender, object e)
    {
        // TODO(winui): Remove this workaround after microsoft-ui-xaml#9542 is fixed
        // and the project uses a Windows App SDK version that contains the fix.
        // Opening a ContextFlyout can currently leave a loading or resize cursor
        // active until the pointer moves again.
        NativeCursor.SetArrow();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, NativeCursor.SetArrow);
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

        await ViewModel.SelectArticleAsync(article, _lifetime.Token);
        ArticleEmptyView.Visibility = Visibility.Collapsed;
        ArticleReaderView.Visibility = Visibility.Visible;
    }

    private async void AllArticles_Click(object sender, RoutedEventArgs e)
    {
        _feedSelectionAnchorId = null;
        await ViewModel.SelectAllArticlesAsync(_lifetime.Token);
        HideArticleReader();
    }

    private async void UnreadFilterToggle_Click(object sender, RoutedEventArgs e)
    {
        var filter = UnreadFilterToggleButton.IsChecked == true
            ? ArticleFilter.Unread
            : ArticleFilter.All;
        await ViewModel.SetArticleFilterAsync(filter, _lifetime.Token);
        HideArticleReader();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || SettingsFrame.Visibility == Visibility.Visible)
        {
            return;
        }

        SettingsFrame.Navigate(typeof(SettingsPage));
        if (SettingsFrame.Content is not SettingsPage settingsPage)
        {
            return;
        }

        settingsPage.Initialize(
            _settings.Theme,
            App.Current.Localization.CurrentLanguage,
            _settings.RefreshIntervalMinutes);
        settingsPage.BackRequested += SettingsPage_BackRequested;
        settingsPage.ThemeChanged += SettingsPage_ThemeChanged;
        settingsPage.LanguageChanged += SettingsPage_LanguageChanged;
        settingsPage.RefreshIntervalChanged += SettingsPage_RefreshIntervalChanged;
        settingsPage.ImportSubscriptionsRequested += SettingsPage_ImportSubscriptionsRequested;
        settingsPage.ExportSubscriptionsRequested += SettingsPage_ExportSubscriptionsRequested;
        SettingsFrame.Visibility = Visibility.Visible;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || SettingsFrame.Visibility == Visibility.Visible)
        {
            return;
        }

        SettingsFrame.Navigate(typeof(AboutPage));
        if (SettingsFrame.Content is not AboutPage aboutPage)
        {
            return;
        }

        aboutPage.BackRequested += AboutPage_BackRequested;
        SettingsFrame.Visibility = Visibility.Visible;
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

    private async void SettingsPage_ImportSubscriptionsRequested(object? sender, EventArgs e)
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

        settingsPage.SetSubscriptionActionsEnabled(false);
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
                return;
            }

            var content = await FileIO.ReadTextAsync(file);
            var document = OpmlSubscriptionSerializer.Parse(content);
            if (document.Subscriptions.Count == 0)
            {
                settingsPage.ShowSubscriptionStatus(
                    localization.GetString("NoValidSubscriptionsInFile"),
                    isError: true);
                return;
            }

            var result = await ViewModel.ImportSubscriptionsAsync(document, _lifetime.Token);
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
            settingsPage.ShowSubscriptionStatus(
                localization.GetString("InvalidSubscriptionFile"),
                isError: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
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
            settingsPage.ImportSubscriptionsRequested -= SettingsPage_ImportSubscriptionsRequested;
            settingsPage.ExportSubscriptionsRequested -= SettingsPage_ExportSubscriptionsRequested;
        }
        else if (SettingsFrame.Content is AboutPage aboutPage)
        {
            aboutPage.BackRequested -= AboutPage_BackRequested;
        }

        SettingsFrame.Visibility = Visibility.Collapsed;
        SettingsFrame.Content = null;
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

    private void ApplyLocalization()
    {
        var localization = App.Current.Localization;
        RootGrid.Language = localization.LanguageTag;
        AutomationProperties.SetName(BrandIcon, localization.GetString("AppIconAutomation"));

        var addFeed = localization.GetString("AddFeed");
        AutomationProperties.SetName(AddFeedButton, addFeed);
        ToolTipService.SetToolTip(AddFeedButton, addFeed);
        ToolTipService.SetToolTip(RefreshButton, localization.GetString("RefreshAllFeeds"));

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
        OpenInBrowserText.Text = localization.GetString("OpenInBrowser");
        MarkUnreadText.Text = localization.GetString("MarkUnread");
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args) =>
        UpdateTitleBarButtonColors();

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

        if (!item.IsSelected || ViewModel.SelectedFeedCount <= 1)
        {
            return [item.Feed];
        }

        return ViewModel.Feeds
            .Where(feed => ViewModel.SelectedFeedIds.Contains(feed.Id))
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
            Title = localization.GetString("ChangeGroup"),
            Content = groupSelector,
            PrimaryButtonText = localization.GetString("Save"),
            CloseButtonText = localization.GetString("Cancel"),
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
            Title = localization.GetString(isBatch ? "RemoveFeedsTitle" : "RemoveFeedTitle"),
            Content = isBatch
                ? localization.Format("RemoveFeedsMessage", feeds.Count)
                : localization.Format("RemoveFeedMessage", feeds[0].Title),
            PrimaryButtonText = localization.GetString("Remove"),
            CloseButtonText = localization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteFeedsAsync(feeds, _lifetime.Token);
            if (_feedSelectionAnchorId is { } anchorId && feeds.Any(feed => feed.Id == anchorId))
            {
                _feedSelectionAnchorId = null;
            }

            HideArticleReader();
        }
    }

    private async Task ConfirmDeleteGroupAsync(FeedGroup group)
    {
        var localization = App.Current.Localization;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = localization.GetString("RemoveGroupTitle"),
            Content = localization.Format("RemoveGroupMessage", group.Name),
            PrimaryButtonText = localization.GetString("Remove"),
            CloseButtonText = localization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteFeedGroupAsync(group, _lifetime.Token);
            _feedSelectionAnchorId = null;
            HideArticleReader();
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
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void RefreshTimer_Tick(object? sender, object e)
    {
        if (!ViewModel.IsBusy && ViewModel.Feeds.Count > 0 && ViewModel.RefreshCommand.CanExecute(null))
        {
            ViewModel.RefreshCommand.Execute(null);
        }
    }
}
