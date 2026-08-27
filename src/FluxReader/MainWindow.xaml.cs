using System.ComponentModel;
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
using Windows.Storage;
using Windows.Storage.Pickers;
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

    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromMinutes(DefaultRefreshIntervalMinutes)
    };
    private readonly Storyboard _refreshIconSpinStoryboard = new();
    private bool _isSynchronizingFeedListSelection;
    private AppSettings _settings = new();
    private bool _settingsLoaded;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureRefreshIconAnimation();
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
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ApplyLocalization();
        RootGrid.Loaded += RootGrid_Loaded;
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

    private void FeedGroupChevron_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FeedNavigationItem item })
        {
            ViewModel.ToggleFeedNavigationGroup(item);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
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
        }
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
        var desiredItems = ViewModel.SelectedGroup is { } selectedGroup
            ? ViewModel.FeedNavigationRows
                .Where(item => item.Group?.Id == selectedGroup.Id)
                .ToHashSet()
            : ViewModel.FeedNavigationRows
                .Where(item => item.Feed is not null && selectedFeedIds.Contains(item.Feed.Id))
                .ToHashSet();
        ApplyFeedListSelection(desiredItems);
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
            await ViewModel.DeleteFeedsAsync(feeds, _lifetime.Token);
            HideArticleReader();
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
            PrimaryButtonText = localization.GetString("Remove"),
            PrimaryButtonStyle = (Style)App.Current.Resources["DestructiveButtonStyle"],
            CloseButtonText = localization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.None
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteFeedGroupAsync(group, _lifetime.Token);
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
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
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
