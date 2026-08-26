using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxReader.Data;
using FluxReader.Models;
using FluxReader.Services;
using Windows.System;

namespace FluxReader.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly NotificationService _notifications;
    private readonly RssRefreshService _refreshService;
    private readonly RssRepository _repository;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArticleListTitle))]
    public partial Feed? SelectedFeed { get; set; }

    [ObservableProperty]
    public partial Article? SelectedArticle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArticleListTitle))]
    public partial ArticleFilter CurrentFilter { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsStatusOpen { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ArticleCountText { get; set; } = "0 篇文章";

    [ObservableProperty]
    public partial int UnreadTotal { get; set; }

    public MainViewModel(
        RssRepository repository,
        RssRefreshService refreshService,
        NotificationService notifications)
    {
        _repository = repository;
        _refreshService = refreshService;
        _notifications = notifications;
    }

    public ObservableCollection<Feed> Feeds { get; } = [];

    public ObservableCollection<Article> Articles { get; } = [];

    public string ArticleListTitle => SelectedFeed?.Title ?? CurrentFilter switch
    {
        ArticleFilter.Unread => "未读文章",
        _ => "所有文章"
    };

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.InitializeAsync(cancellationToken);
            await ReloadFeedsAsync(null, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            if (!_notifications.IsAvailable)
            {
                ShowStatus("Windows 系统通知当前不可用，错误已写入 notifications.log；阅读和刷新功能不受影响。");
            }
        }
        catch (Exception exception)
        {
            ShowStatus($"初始化失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AddFeedAsync(string input, CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            ShowStatus("请输入有效的 HTTP 或 HTTPS 订阅地址。");
            return;
        }

        IsBusy = true;
        try
        {
            var feed = await _refreshService.AddFeedAsync(uri, cancellationToken);
            CurrentFilter = ArticleFilter.All;
            await ReloadFeedsAsync(feed.Id, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus($"已订阅“{feed.Title}”。");
        }
        catch (Exception exception)
        {
            ShowStatus($"添加订阅失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SelectFeedAsync(Feed feed, CancellationToken cancellationToken = default)
    {
        SelectedFeed = feed;
        CurrentFilter = ArticleFilter.All;
        await ReloadArticlesAsync(cancellationToken);
    }

    public async Task SelectSmartFilterAsync(ArticleFilter filter, CancellationToken cancellationToken = default)
    {
        SelectedFeed = null;
        CurrentFilter = filter;
        await ReloadArticlesAsync(cancellationToken);
    }

    public async Task SelectArticleAsync(Article article, CancellationToken cancellationToken = default)
    {
        SelectedArticle = article;
        if (article.IsRead)
        {
            return;
        }

        article.IsRead = true;
        await _repository.SetArticleReadAsync(article.Id, true, cancellationToken);
        var feed = Feeds.FirstOrDefault(item => item.Id == article.FeedId);
        if (feed is not null && feed.UnreadCount > 0)
        {
            feed.UnreadCount--;
            UnreadTotal = Math.Max(0, UnreadTotal - 1);
        }

        if (CurrentFilter == ArticleFilter.Unread)
        {
            ArticleCountText = $"{Articles.Count(articleItem => !articleItem.IsRead)} 篇文章";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy || Feeds.Count == 0)
        {
            if (Feeds.Count == 0)
            {
                ShowStatus("请先添加一个 RSS 或 Atom 订阅。");
            }

            return;
        }

        IsBusy = true;
        try
        {
            var tasks = Feeds.Select(async feed =>
            {
                try
                {
                    return (Result: await _refreshService.RefreshAsync(feed), Error: (Exception?)null);
                }
                catch (Exception exception)
                {
                    return (Result: (FeedRefreshResult?)null, Error: exception);
                }
            });
            var outcomes = await Task.WhenAll(tasks);
            var newTitles = outcomes
                .Where(outcome => outcome.Result is not null)
                .SelectMany(outcome => outcome.Result!.NewArticleTitles)
                .ToArray();
            var errorCount = outcomes.Count(outcome => outcome.Error is not null);
            var selectedFeedId = SelectedFeed?.Id;
            await ReloadFeedsAsync(selectedFeedId);
            await ReloadArticlesAsync();

            if (newTitles.Length > 0)
            {
                _notifications.ShowNewArticles(newTitles.Length, newTitles[0]);
            }

            ShowStatus(errorCount == 0
                ? $"刷新完成，发现 {newTitles.Length} 篇新文章。"
                : $"刷新完成，发现 {newTitles.Length} 篇新文章；{errorCount} 个订阅失败。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task MarkAllReadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        await _repository.MarkAllReadAsync(SelectedFeed?.Id);
        foreach (var article in Articles)
        {
            article.IsRead = true;
        }

        foreach (var feed in Feeds)
        {
            if (SelectedFeed is null || feed.Id == SelectedFeed.Id)
            {
                feed.UnreadCount = 0;
            }
        }

        UnreadTotal = Feeds.Sum(feed => feed.UnreadCount);
        if (CurrentFilter == ArticleFilter.Unread)
        {
            Articles.Clear();
            ArticleCountText = "0 篇文章";
            SelectedArticle = null;
        }

        ShowStatus("已将当前范围内的文章标为已读。");
    }

    [RelayCommand]
    private async Task OpenArticleAsync()
    {
        if (SelectedArticle is null ||
            !Uri.TryCreate(SelectedArticle.Link, UriKind.Absolute, out var uri))
        {
            ShowStatus("这篇文章没有可打开的原始链接。");
            return;
        }

        await Launcher.LaunchUriAsync(uri);
    }

    public async Task DeleteSelectedFeedAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedFeed is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var title = SelectedFeed.Title;
            await _repository.DeleteFeedAsync(SelectedFeed.Id, cancellationToken);
            SelectedFeed = null;
            CurrentFilter = ArticleFilter.All;
            await ReloadFeedsAsync(null, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus($"已移除“{title}”。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadFeedsAsync(long? selectedFeedId, CancellationToken cancellationToken = default)
    {
        var feeds = await _repository.GetFeedsAsync(cancellationToken);
        Feeds.Clear();
        foreach (var feed in feeds)
        {
            Feeds.Add(feed);
        }

        UnreadTotal = Feeds.Sum(feed => feed.UnreadCount);
        SelectedFeed = selectedFeedId is null
            ? null
            : Feeds.FirstOrDefault(feed => feed.Id == selectedFeedId.Value);
    }

    private async Task ReloadArticlesAsync(CancellationToken cancellationToken = default)
    {
        var articles = await _repository.GetArticlesAsync(SelectedFeed?.Id, CurrentFilter, cancellationToken);
        Articles.Clear();
        foreach (var article in articles)
        {
            Articles.Add(article);
        }

        SelectedArticle = null;
        ArticleCountText = $"{Articles.Count} 篇文章";
        OnPropertyChanged(nameof(ArticleListTitle));
    }

    private void ShowStatus(string message)
    {
        StatusMessage = message;
        IsStatusOpen = true;
    }
}
