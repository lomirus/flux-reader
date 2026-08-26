using FluxReader.Models;
using FluxReader.Services;
using FluxReader.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluxReader;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromMinutes(15)
    };
    private bool _themeLoaded;

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
        var settings = await App.Current.Settings.LoadAsync(_lifetime.Token);
        ApplyTheme(settings.Theme);
        ThemeSelector.SelectedIndex = (int)settings.Theme;
        _themeLoaded = true;
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
        if (!_themeLoaded || ThemeSelector.SelectedIndex < 0)
        {
            return;
        }

        var theme = (AppTheme)ThemeSelector.SelectedIndex;
        ApplyTheme(theme);
        await App.Current.Settings.SaveAsync(new AppSettings(theme), _lifetime.Token);
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
