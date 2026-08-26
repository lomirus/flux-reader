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
    private readonly LocalizationService _localization;
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
    public partial string ArticleCountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int UnreadTotal { get; set; }

    public MainViewModel(
        RssRepository repository,
        RssRefreshService refreshService,
        NotificationService notifications,
        LocalizationService localization)
    {
        _repository = repository;
        _refreshService = refreshService;
        _notifications = notifications;
        _localization = localization;
        ApplyLocalization();
    }

    public ObservableCollection<Feed> Feeds { get; } = [];

    public ObservableCollection<Article> Articles { get; } = [];

    public string ArticleListTitle => SelectedFeed?.Title ?? CurrentFilter switch
    {
        ArticleFilter.Unread => _localization.GetString("UnreadArticles"),
        _ => _localization.GetString("AllArticles")
    };

    public void ApplyLocalization()
    {
        OnPropertyChanged(nameof(ArticleListTitle));
        UpdateArticleCount();
        IsStatusOpen = false;
        foreach (var article in Articles)
        {
            article.RefreshLocalization();
        }
    }

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
                ShowStatus(_localization.GetString("NotificationUnavailable"));
            }
        }
        catch (Exception exception)
        {
            ShowStatus(_localization.Format("InitializationFailed", exception.Message));
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
            ShowStatus(_localization.GetString("InvalidFeedAddress"));
            return;
        }

        IsBusy = true;
        try
        {
            var feed = await _refreshService.AddFeedAsync(uri, cancellationToken);
            CurrentFilter = ArticleFilter.All;
            await ReloadFeedsAsync(feed.Id, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(_localization.Format("SubscribedToFeed", feed.Title));
        }
        catch (Exception exception)
        {
            ShowStatus(_localization.Format("AddFeedFailed", exception.Message));
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
            UpdateArticleCount(Articles.Count(articleItem => !articleItem.IsRead));
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy || Feeds.Count == 0)
        {
            if (Feeds.Count == 0)
            {
                ShowStatus(_localization.GetString("AddFeedFirst"));
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
                ? _localization.FormatRefreshComplete(newTitles.Length)
                : _localization.Format("RefreshCompleteWithErrors", newTitles.Length, errorCount));
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
            UpdateArticleCount();
            SelectedArticle = null;
        }

        ShowStatus(_localization.GetString("MarkedAllRead"));
    }

    [RelayCommand]
    private async Task OpenArticleAsync()
    {
        if (SelectedArticle is null ||
            !Uri.TryCreate(SelectedArticle.Link, UriKind.Absolute, out var uri))
        {
            ShowStatus(_localization.GetString("ArticleLinkUnavailable"));
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
            ShowStatus(_localization.Format("FeedRemoved", title));
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
        UpdateArticleCount();
        OnPropertyChanged(nameof(ArticleListTitle));
    }

    private void UpdateArticleCount(int? count = null) =>
        ArticleCountText = _localization.FormatArticleCount(count ?? Articles.Count);

    private void ShowStatus(string message)
    {
        StatusMessage = message;
        IsStatusOpen = true;
    }
}
