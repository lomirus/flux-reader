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
    private long _articleLoadVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArticleListTitle))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedFeed))]
    public partial Feed? SelectedFeed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArticleListTitle))]
    public partial FeedGroup? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial FeedNavigationItem? SelectedNavigationItem { get; set; }

    [ObservableProperty]
    public partial Article? SelectedArticle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArticleListTitle))]
    [NotifyPropertyChangedFor(nameof(IsUnreadFilterEnabled))]
    public partial ArticleFilter CurrentFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedFeed))]
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

    public ObservableCollection<FeedGroup> FeedGroups { get; } = [];

    public ObservableCollection<FeedNavigationItem> FeedNavigationItems { get; } = [];

    public ObservableCollection<Article> Articles { get; } = [];

    public bool IsUnreadFilterEnabled => CurrentFilter == ArticleFilter.Unread;

    public bool CanDeleteSelectedFeed => SelectedFeed is not null && !IsBusy;

    public string ArticleListTitle => SelectedFeed?.Title ?? SelectedGroup?.Name ??
        (CurrentFilter switch
        {
            ArticleFilter.Unread => _localization.GetString("UnreadArticles"),
            _ => _localization.GetString("AllArticles")
        });

    public void ApplyLocalization()
    {
        OnPropertyChanged(nameof(ArticleListTitle));
        UpdateArticleCount();
        IsStatusOpen = false;
        foreach (var article in Articles)
        {
            article.RefreshLocalization();
        }

        RebuildFeedNavigation();
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
            await ReloadFeedsAsync(null, null, cancellationToken);
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

    public async Task AddFeedAsync(
        string input,
        long? groupId,
        CancellationToken cancellationToken = default)
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
            var feed = await _refreshService.AddFeedAsync(uri, groupId, cancellationToken);
            await ReloadFeedsAsync(feed.Id, null, cancellationToken);
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
        SelectedGroup = null;
        SelectedNavigationItem = FindFeedNavigationItem(feed.Id);
        await ReloadArticlesAsync(cancellationToken);
    }

    public async Task SelectGroupAsync(FeedGroup group, CancellationToken cancellationToken = default)
    {
        SelectedFeed = null;
        SelectedGroup = group;
        SelectedNavigationItem = FeedNavigationItems.FirstOrDefault(item => item.Group?.Id == group.Id);
        await ReloadArticlesAsync(cancellationToken);
    }

    public async Task SelectAllArticlesAsync(CancellationToken cancellationToken = default)
    {
        SelectedFeed = null;
        SelectedGroup = null;
        SelectedNavigationItem = null;
        await ReloadArticlesAsync(cancellationToken);
    }

    public async Task SetArticleFilterAsync(ArticleFilter filter, CancellationToken cancellationToken = default)
    {
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
            var selectedGroupId = SelectedGroup?.Id;
            await ReloadFeedsAsync(selectedFeedId, selectedGroupId);
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
    private async Task MarkCurrentListReadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var unreadArticles = Articles.Where(article => !article.IsRead).ToArray();
        await _repository.MarkArticlesReadAsync(unreadArticles.Select(article => article.Id).ToArray());
        foreach (var article in unreadArticles)
        {
            article.IsRead = true;
        }

        foreach (var unreadGroup in unreadArticles.GroupBy(article => article.FeedId))
        {
            var feed = Feeds.FirstOrDefault(item => item.Id == unreadGroup.Key);
            if (feed is not null)
            {
                feed.UnreadCount = Math.Max(0, feed.UnreadCount - unreadGroup.Count());
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
    private async Task MarkSelectedArticleUnreadAsync()
    {
        var article = SelectedArticle;
        if (article is null || !article.IsRead)
        {
            return;
        }

        await _repository.SetArticleReadAsync(article.Id, false);
        article.IsRead = false;

        var feed = Feeds.FirstOrDefault(item => item.Id == article.FeedId);
        if (feed is not null)
        {
            feed.UnreadCount++;
        }

        UnreadTotal = Feeds.Sum(item => item.UnreadCount);
        if (CurrentFilter == ArticleFilter.Unread)
        {
            UpdateArticleCount(Articles.Count(item => !item.IsRead));
        }
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

    public Task DeleteSelectedFeedAsync(CancellationToken cancellationToken = default) =>
        SelectedFeed is null
            ? Task.CompletedTask
            : DeleteFeedAsync(SelectedFeed, cancellationToken);

    public async Task DeleteFeedAsync(Feed feed, CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var title = feed.Title;
            await _repository.DeleteFeedAsync(feed.Id, cancellationToken);
            SelectedFeed = null;
            SelectedGroup = null;
            SelectedNavigationItem = null;
            await ReloadFeedsAsync(null, null, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(_localization.Format("FeedRemoved", title));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AddFeedGroupAsync(string name, CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
        {
            ShowStatus(_localization.GetString("InvalidGroupName"));
            return;
        }

        IsBusy = true;
        try
        {
            var group = await _repository.AddFeedGroupAsync(normalizedName, cancellationToken);
            await ReloadFeedsAsync(null, group.Id, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(_localization.Format("GroupCreated", group.Name));
        }
        catch (Exception exception)
        {
            ShowStatus(_localization.Format("GroupOperationFailed", exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RenameFeedGroupAsync(
        FeedGroup group,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
        {
            ShowStatus(_localization.GetString("InvalidGroupName"));
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.RenameFeedGroupAsync(group.Id, normalizedName, cancellationToken);
            await ReloadFeedsAsync(null, group.Id, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(_localization.Format("GroupRenamed", normalizedName));
        }
        catch (Exception exception)
        {
            ShowStatus(_localization.Format("GroupOperationFailed", exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteFeedGroupAsync(
        FeedGroup group,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.DeleteFeedGroupAsync(group.Id, cancellationToken);
            SelectedFeed = null;
            SelectedGroup = null;
            SelectedNavigationItem = null;
            await ReloadFeedsAsync(null, null, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(_localization.Format("GroupRemoved", group.Name));
        }
        catch (Exception exception)
        {
            ShowStatus(_localization.Format("GroupOperationFailed", exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SetFeedGroupAsync(
        Feed feed,
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.SetFeedGroupAsync(feed.Id, groupId, cancellationToken);
            await ReloadFeedsAsync(feed.Id, null, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(_localization.Format("FeedGroupChanged", feed.Title));
        }
        catch (Exception exception)
        {
            ShowStatus(_localization.Format("GroupOperationFailed", exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadFeedsAsync(
        long? selectedFeedId,
        long? selectedGroupId,
        CancellationToken cancellationToken = default)
    {
        var groups = await _repository.GetFeedGroupsAsync(cancellationToken);
        var feeds = await _repository.GetFeedsAsync(cancellationToken);

        FeedGroups.Clear();
        foreach (var group in groups)
        {
            FeedGroups.Add(group);
        }

        Feeds.Clear();
        foreach (var feed in feeds)
        {
            Feeds.Add(feed);
        }

        UnreadTotal = Feeds.Sum(feed => feed.UnreadCount);
        RebuildFeedNavigation();
        SelectedFeed = selectedFeedId is null
            ? null
            : Feeds.FirstOrDefault(feed => feed.Id == selectedFeedId.Value);
        SelectedGroup = selectedGroupId is null
            ? null
            : FeedGroups.FirstOrDefault(group => group.Id == selectedGroupId.Value);
        SelectedNavigationItem = SelectedFeed is not null
            ? FindFeedNavigationItem(SelectedFeed.Id)
            : FeedNavigationItems.FirstOrDefault(item => item.Group?.Id == SelectedGroup?.Id);
    }

    private async Task ReloadArticlesAsync(CancellationToken cancellationToken = default)
    {
        var loadVersion = Interlocked.Increment(ref _articleLoadVersion);
        var feedId = SelectedFeed?.Id;
        var groupId = SelectedGroup?.Id;
        var filter = CurrentFilter;
        var articles = await _repository.GetArticlesAsync(
            feedId,
            groupId,
            filter,
            cancellationToken);
        if (loadVersion != Volatile.Read(ref _articleLoadVersion) ||
            feedId != SelectedFeed?.Id ||
            groupId != SelectedGroup?.Id ||
            filter != CurrentFilter)
        {
            return;
        }

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

    private void RebuildFeedNavigation()
    {
        var selectedFeedId = SelectedFeed?.Id;
        var selectedGroupId = SelectedGroup?.Id;
        var expansionStates = FeedNavigationItems
            .Where(item => item.Group is not null)
            .ToDictionary(item => item.Group!.Id, item => item.IsExpanded);
        var actionLabels = new FeedNavigationItem.ActionLabels(
            _localization.GetString("ChangeGroup"),
            _localization.GetString("Remove"),
            _localization.GetString("RenameGroup"),
            _localization.GetString("RemoveGroup"));
        FeedNavigationItems.Clear();
        foreach (var feed in Feeds.Where(feed => feed.GroupId is null))
        {
            FeedNavigationItems.Add(FeedNavigationItem.ForFeed(feed, actionLabels));
        }

        foreach (var group in FeedGroups)
        {
            var item = FeedNavigationItem.ForGroup(
                group,
                Feeds.Where(feed => feed.GroupId == group.Id),
                actionLabels);
            item.IsExpanded = !expansionStates.TryGetValue(group.Id, out var isExpanded) || isExpanded;
            FeedNavigationItems.Add(item);
        }

        SelectedNavigationItem = selectedFeedId is not null
            ? FindFeedNavigationItem(selectedFeedId.Value)
            : FeedNavigationItems.FirstOrDefault(item => item.Group?.Id == selectedGroupId);
    }

    private FeedNavigationItem? FindFeedNavigationItem(long feedId) =>
        FeedNavigationItems
            .SelectMany(item => item.IsGroup ? item.Children : [item])
            .FirstOrDefault(item => item.Feed?.Id == feedId);

    private void ShowStatus(string message)
    {
        StatusMessage = message;
        IsStatusOpen = true;
    }
}
