using FluxReader.Models;
using FluxReader.Services;
using FluxReader.ViewModels;
using Microsoft.UI.Xaml;
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
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var app = App.Current;
        ViewModel = new MainViewModel(app.Repository, app.RefreshService, app.Notifications);
        RootGrid.DataContext = ViewModel;
        RootGrid.Loaded += RootGrid_Loaded;
        _refreshTimer.Tick += RefreshTimer_Tick;
        Closed += MainWindow_Closed;
    }

    public MainViewModel ViewModel { get; }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        _settings = await App.Current.Settings.LoadAsync(_lifetime.Token);
        ApplyTheme(_settings.Theme);
        ApplySavedPaneWidths();
        ThemeSelector.SelectedIndex = (int)_settings.Theme;
        _settingsLoaded = true;
        await ViewModel.InitializeAsync(_lifetime.Token);
        _refreshTimer.Start();
    }

    private async void AddFeed_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            Header = "RSS 或 Atom 地址",
            PlaceholderText = "https://example.com/feed.xml"
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "添加订阅",
            Content = input,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.AddFeedAsync(input.Text, _lifetime.Token);
            FeedList.SelectedItem = ViewModel.SelectedFeed;
            HideArticleReader();
        }
    }

    private async void DeleteFeed_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFeed is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "移除订阅？",
            Content = $"“{ViewModel.SelectedFeed.Title}”及其本地文章将被删除。",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteSelectedFeedAsync(_lifetime.Token);
            FeedList.SelectedItem = null;
            HideArticleReader();
        }
    }

    private async void FeedList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Feed feed)
        {
            return;
        }

        await ViewModel.SelectFeedAsync(feed, _lifetime.Token);
        HideArticleReader();
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
        FeedList.SelectedItem = null;
        await ViewModel.SelectSmartFilterAsync(ArticleFilter.All, _lifetime.Token);
        HideArticleReader();
    }

    private async void UnreadArticles_Click(object sender, RoutedEventArgs e)
    {
        FeedList.SelectedItem = null;
        await ViewModel.SelectSmartFilterAsync(ArticleFilter.Unread, _lifetime.Token);
        HideArticleReader();
    }

    private async void StarredArticles_Click(object sender, RoutedEventArgs e)
    {
        FeedList.SelectedItem = null;
        await ViewModel.SelectSmartFilterAsync(ArticleFilter.Starred, _lifetime.Token);
        HideArticleReader();
    }

    private async void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded || ThemeSelector.SelectedIndex < 0)
        {
            return;
        }

        var theme = (AppTheme)ThemeSelector.SelectedIndex;
        ApplyTheme(theme);
        _settings = _settings with { Theme = theme };
        await SaveSettingsAsync();
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

    private void HideArticleReader()
    {
        ArticleList.SelectedItem = null;
        ArticleEmptyView.Visibility = Visibility.Visible;
        ArticleReaderView.Visibility = Visibility.Collapsed;
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
