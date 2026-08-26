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

    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromMinutes(15)
    };
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
        _settings = _settings with { Language = languagePreference };
        App.Current.Localization.SetLanguage(
            App.Current.Localization.ResolveLanguage(languagePreference));
        ApplyLocalization();
        ViewModel.ApplyLocalization();
        ApplyTheme(_settings.Theme);
        ApplySavedPaneWidths();
        _settingsLoaded = true;
        await ViewModel.InitializeAsync(_lifetime.Token);
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
            FeedTree.SelectedItem = ViewModel.SelectedNavigationItem;
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
            FeedTree.SelectedItem = ViewModel.SelectedNavigationItem;
            HideArticleReader();
        }
    }

    private async void FeedTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        var selectedItem = args.AddedItems.LastOrDefault();
        var item = selectedItem as FeedNavigationItem ??
                   (selectedItem as TreeViewNode)?.Content as FeedNavigationItem ??
                   sender.SelectedItem as FeedNavigationItem;
        if (item is null)
        {
            return;
        }

        if (item.Feed is not null)
        {
            if (ViewModel.SelectedFeed?.Id == item.Feed.Id && ViewModel.SelectedGroup is null)
            {
                return;
            }

            await ViewModel.SelectFeedAsync(item.Feed, _lifetime.Token);
        }
        else if (item.Group is not null)
        {
            if (ViewModel.SelectedGroup?.Id == item.Group.Id && ViewModel.SelectedFeed is null)
            {
                return;
            }

            await ViewModel.SelectGroupAsync(item.Group, _lifetime.Token);
        }

        HideArticleReader();
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
            await ChangeFeedGroupAsync(item.Feed);
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
            await ConfirmDeleteFeedAsync(item.Feed);
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
        FeedTree.SelectedItem = null;
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

        settingsPage.Initialize(_settings.Theme, App.Current.Localization.CurrentLanguage);
        settingsPage.BackRequested += SettingsPage_BackRequested;
        settingsPage.ThemeChanged += SettingsPage_ThemeChanged;
        settingsPage.LanguageChanged += SettingsPage_LanguageChanged;
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

    private void SettingsPage_BackRequested(object? sender, EventArgs e) => CloseSettingsPage();

    private void AboutPage_BackRequested(object? sender, EventArgs e) => CloseSettingsPage();

    private void CloseSettingsPage()
    {
        if (SettingsFrame.Content is SettingsPage settingsPage)
        {
            settingsPage.BackRequested -= SettingsPage_BackRequested;
            settingsPage.ThemeChanged -= SettingsPage_ThemeChanged;
            settingsPage.LanguageChanged -= SettingsPage_LanguageChanged;
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

    private async Task ChangeFeedGroupAsync(Feed feed)
    {
        var localization = App.Current.Localization;
        var groupSelector = CreateGroupSelector(feed.GroupId);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = localization.GetString("ChangeGroup"),
            Content = groupSelector,
            PrimaryButtonText = localization.GetString("Save"),
            CloseButtonText = localization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.SetFeedGroupAsync(feed, GetSelectedGroupId(groupSelector), _lifetime.Token);
            FeedTree.SelectedItem = ViewModel.SelectedNavigationItem;
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
            FeedTree.SelectedItem = ViewModel.SelectedNavigationItem;
        }
    }

    private async Task ConfirmDeleteFeedAsync(Feed feed)
    {
        var localization = App.Current.Localization;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = localization.GetString("RemoveFeedTitle"),
            Content = localization.Format("RemoveFeedMessage", feed.Title),
            PrimaryButtonText = localization.GetString("Remove"),
            CloseButtonText = localization.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteFeedAsync(feed, _lifetime.Token);
            FeedTree.SelectedItem = null;
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
            FeedTree.SelectedItem = null;
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
