using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxReader.Core.Models;
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
    private readonly Dictionary<long, string> _feedRefreshErrors = [];
    private readonly HashSet<long> _selectedFeedIds = [];
    private long _articleLoadVersion;
    private long _navigationSelectionVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArticleListTitle))]
    public partial Feed? SelectedFeed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArticleListTitle))]
    public partial FeedGroup? SelectedGroup { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArticleListGlyph))]
    public partial FeedNavigationItem? SelectedNavigationItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedArticleNavigationItem))]
    public partial Article? SelectedArticle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArticleListTitle))]
    [NotifyPropertyChangedFor(nameof(IsUnreadFilterEnabled))]
    public partial ArticleFilter CurrentFilter { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string ArticleCountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ArticleSearchQuery { get; set; } = string.Empty;

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

    public event EventHandler<StatusNotificationRequestedEventArgs>? StatusNotificationRequested;

    public ObservableCollection<Feed> Feeds { get; } = [];

    public ObservableCollection<FeedGroup> FeedGroups { get; } = [];

    public ObservableCollection<FeedNavigationItem> FeedNavigationRows { get; } = [];

    public ObservableCollection<Article> Articles { get; } = [];

    public IReadOnlySet<long> SelectedFeedIds => _selectedFeedIds;

    public int SelectedFeedCount => _selectedFeedIds.Count;

    public DateTimeOffset? LastRefreshedAt => Feeds
        .Select(feed => feed.LastRefreshedAt)
        .OrderByDescending(value => value)
        .FirstOrDefault();

    public bool IsUnreadFilterEnabled => CurrentFilter == ArticleFilter.Unread;

    public string ArticleListTitle => SelectedFeedCount > 1
        ? _localization.Format("SelectedFeeds", SelectedFeedCount)
        : SelectedFeed?.Title ??
          SelectedGroup?.Name ??
          (CurrentFilter switch
          {
              ArticleFilter.Unread => _localization.GetString("UnreadArticles"),
              _ => _localization.GetString("AllArticles")
          });

    public string ArticleListGlyph => SelectedFeedCount > 1
        ? "\uE762"
        : SelectedNavigationItem?.Glyph ?? "\uE8F1";

    public FeedNavigationItem? SelectedArticleNavigationItem => SelectedArticle is null
        ? null
        : FindFeedNavigationItem(SelectedArticle.FeedId);

    public void ApplyLocalization()
    {
        OnPropertyChanged(nameof(ArticleListTitle));
        UpdateArticleCount();
        foreach (var article in Articles)
        {
            article.RefreshLocalization();
        }

        RebuildFeedNavigation(_selectedFeedIds.ToArray(), SelectedGroup?.Id);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        var startedAt = Stopwatch.GetTimestamp();
        DiagnosticLog.Information("view_model.initialize_started");
        try
        {
            await _repository.InitializeAsync(cancellationToken);
            await ReloadFeedsAsync([], null, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            if (!_notifications.IsAvailable)
            {
                ShowStatus(
                    _localization.GetString("NotificationUnavailable"),
                    StatusNotificationSeverity.Warning);
            }

            DiagnosticLog.MemorySnapshot(
                "view_model.initialize_completed",
                new
                {
                    feedCount = Feeds.Count,
                    articleCount = Articles.Count,
                    elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
                });
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("view_model.initialize_failed", exception);
            ShowStatus(
                _localization.Format("InitializationFailed", exception.Message),
                StatusNotificationSeverity.Error);
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
            ShowStatus(
                _localization.GetString("InvalidFeedAddress"),
                StatusNotificationSeverity.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var feed = await _refreshService.AddFeedAsync(uri, groupId, cancellationToken);
            await ReloadFeedsAsync([feed.Id], null, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(
                _localization.Format("SubscribedToFeed", feed.Title),
                StatusNotificationSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus(
                _localization.Format("AddFeedFailed", exception.Message),
                StatusNotificationSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public IReadOnlyList<SubscriptionOutline> GetSubscriptionsForExport()
    {
        var groupNames = FeedGroups.ToDictionary(group => group.Id, group => group.Name);
        return Feeds
            .Select(feed => new SubscriptionOutline(
                feed.Title,
                new Uri(feed.Url),
                TryCreateHttpUri(feed.SiteUrl),
                feed.GroupId is { } groupId && groupNames.TryGetValue(groupId, out var groupName)
                    ? groupName
                    : null))
            .ToArray();
    }

    public async Task<SubscriptionImportResult> ImportSubscriptionsAsync(
        SubscriptionDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (IsBusy)
        {
            throw new InvalidOperationException(_localization.GetString("SubscriptionOperationBusy"));
        }

        IsBusy = true;
        var startedAt = Stopwatch.GetTimestamp();
        var importedCount = 0;
        var skippedCount = document.SkippedOutlineCount;
        var failedCount = 0;
        var importedFeedIds = new List<long>();
        DiagnosticLog.Information(
            "opml.import_started",
            new
            {
                subscriptionCount = document.Subscriptions.Count,
                document.SkippedOutlineCount,
                existingFeedCount = Feeds.Count
            });
        try
        {
            var existingFeedUris = new HashSet<string>(
                Feeds.Select(feed => feed.Url),
                StringComparer.OrdinalIgnoreCase);
            var groupIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in FeedGroups)
            {
                groupIds.TryAdd(group.Name, group.Id);
            }

            foreach (var subscription in document.Subscriptions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (existingFeedUris.Contains(subscription.FeedUri.AbsoluteUri))
                {
                    skippedCount++;
                    continue;
                }

                try
                {
                    var groupId = await GetOrCreateImportedGroupIdAsync(
                        subscription.Group,
                        groupIds,
                        cancellationToken);
                    var importedFeedId = await _repository.AddImportedFeedAsync(
                        subscription,
                        groupId,
                        cancellationToken);
                    if (importedFeedId is null)
                    {
                        skippedCount++;
                        continue;
                    }

                    existingFeedUris.Add(subscription.FeedUri.AbsoluteUri);
                    importedFeedIds.Add(importedFeedId.Value);
                    importedCount++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failedCount++;
                    DiagnosticLog.Error(
                        "opml.subscription_import_failed",
                        exception,
                        new
                        {
                            feedHost = subscription.FeedUri.Host,
                            subscription.Group
                        });
                }
            }

            var selectedFeedIds = _selectedFeedIds.ToArray();
            var selectedGroupId = SelectedGroup?.Id;
            await ReloadFeedsAsync(selectedFeedIds, selectedGroupId, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            DiagnosticLog.MemorySnapshot(
                "opml.import_completed",
                new
                {
                    importedCount,
                    skippedCount,
                    failedCount,
                    feedCount = Feeds.Count,
                    elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
                });
            return new SubscriptionImportResult(
                importedCount,
                skippedCount,
                failedCount,
                importedFeedIds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Information(
                "opml.import_cancelled",
                new { elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds });
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(
                "opml.import_failed",
                exception,
                new { elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds });
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task SelectFeedAsync(Feed feed, CancellationToken cancellationToken = default) =>
        SelectFeedsAsync([feed.Id], cancellationToken);

    public async Task SelectFeedsAsync(
        IReadOnlyCollection<long> feedIds,
        CancellationToken cancellationToken = default)
    {
        ApplyNavigationSelection(feedIds, selectedGroupId: null);
        await ReloadArticlesAsync(cancellationToken);
    }

    public async Task SelectGroupAsync(FeedGroup group, CancellationToken cancellationToken = default)
    {
        ApplyNavigationSelection([], group.Id);
        await ReloadArticlesAsync(cancellationToken);
    }

    public void ToggleFeedNavigationGroup(FeedNavigationItem item)
    {
        if (!item.IsGroup)
        {
            return;
        }

        var groupIndex = FeedNavigationRows.IndexOf(item);
        if (groupIndex < 0)
        {
            return;
        }

        if (item.IsExpanded)
        {
            foreach (var child in item.Children)
            {
                FeedNavigationRows.Remove(child);
            }
        }
        else
        {
            for (var index = 0; index < item.Children.Count; index++)
            {
                FeedNavigationRows.Insert(groupIndex + index + 1, item.Children[index]);
            }
        }

        item.IsExpanded = !item.IsExpanded;
    }

    public async Task SelectAllArticlesAsync(CancellationToken cancellationToken = default)
    {
        ApplyNavigationSelection([], selectedGroupId: null);
        await ReloadArticlesAsync(cancellationToken);
    }

    public async Task SetArticleFilterAsync(ArticleFilter filter, CancellationToken cancellationToken = default)
    {
        CurrentFilter = filter;
        await ReloadArticlesAsync(cancellationToken);
    }

    public async Task SetArticleSearchQueryAsync(
        string? searchQuery,
        CancellationToken cancellationToken = default)
    {
        var normalizedSearchQuery = searchQuery?.Trim() ?? string.Empty;
        if (string.Equals(ArticleSearchQuery, normalizedSearchQuery, StringComparison.Ordinal))
        {
            return;
        }

        ArticleSearchQuery = normalizedSearchQuery;
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

    public async Task<Article?> NavigateToArticleAsync(
        long articleId,
        CancellationToken cancellationToken = default)
    {
        var targetArticle = await _repository.GetArticleAsync(articleId, cancellationToken);
        if (targetArticle is null)
        {
            return null;
        }

        CurrentFilter = ArticleFilter.All;
        ArticleSearchQuery = string.Empty;
        ApplyNavigationSelection([targetArticle.FeedId], selectedGroupId: null);
        await ReloadArticlesAsync(cancellationToken);

        var article = Articles.FirstOrDefault(item => item.Id == articleId) ?? targetArticle;
        await SelectArticleAsync(article, cancellationToken);
        return article;
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RefreshFeedsAsync(Feeds.ToArray());

    public Task RefreshFeedAsync(
        Feed feed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return RefreshFeedsAsync([feed], cancellationToken);
    }

    private async Task RefreshFeedsAsync(
        IReadOnlyCollection<Feed> feeds,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy || feeds.Count == 0)
        {
            if (feeds.Count == 0)
            {
                ShowStatus(
                    _localization.GetString("AddFeedFirst"),
                    StatusNotificationSeverity.Informational);
            }

            return;
        }

        IsBusy = true;
        var startedAt = Stopwatch.GetTimestamp();
        DiagnosticLog.Information(
            "refresh.started",
            new { feedCount = feeds.Count });
        try
        {
            var tasks = feeds.Select(feed => RefreshFeedCoreAsync(feed, cancellationToken));
            var outcomes = await Task.WhenAll(tasks);
            var newArticleCount = outcomes
                .Where(outcome => outcome.Result is not null)
                .Sum(outcome => outcome.Result!.NewArticles.Count);
            var failures = outcomes
                .Where(outcome => outcome.Error is not null)
                .Select(outcome => new StatusNotificationDetail(
                    outcome.Feed.Title,
                    GetRefreshErrorMessage(outcome.Error!)))
                .OrderBy(failure => failure.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            UpdateFeedRefreshErrors(outcomes);
            var selectedFeedIds = _selectedFeedIds.ToArray();
            var selectedGroupId = SelectedGroup?.Id;
            await ReloadFeedsAsync(
                selectedFeedIds,
                selectedGroupId,
                cancellationToken,
                preserveNavigationItems: true);
            await ReloadArticlesAsync(cancellationToken, preserveSelectedArticle: true);

            foreach (var result in outcomes
                         .Select(outcome => outcome.Result)
                         .OfType<FeedRefreshResult>())
            {
                await _notifications.ShowNewArticlesAsync(
                    result.NewArticles,
                    result.FeedIconUrl,
                    cancellationToken);
            }

            if (feeds.Count == 1 && failures.Length == 1)
            {
                ShowStatus(
                    failures[0].Description,
                    StatusNotificationSeverity.Error,
                    _localization.GetString("FeedRefreshFailed"));
            }
            else
            {
                ShowStatus(
                    failures.Length == 0
                        ? _localization.FormatRefreshComplete(newArticleCount)
                        : _localization.Format("RefreshPartialFailureSummary", newArticleCount, failures.Length),
                    failures.Length == 0
                        ? StatusNotificationSeverity.Success
                        : StatusNotificationSeverity.Warning,
                    failures.Length == 0
                        ? null
                        : _localization.GetString("RefreshPartialFailureTitle"),
                    failures.Length == 0
                        ? null
                        : _localization.GetString("ViewDetails"),
                    failures);
            }

            DiagnosticLog.MemorySnapshot(
                "refresh.completed",
                new
                {
                    feedCount = feeds.Count,
                    newArticleCount,
                    failureCount = failures.Length,
                    elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Information(
                "refresh.cancelled",
                new
                {
                    feedCount = feeds.Count,
                    elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
                });
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(
                "refresh.failed",
                exception,
                new
                {
                    feedCount = feeds.Count,
                    elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
                });
            throw;
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

        ShowStatus(
            _localization.GetString("MarkedAllRead"),
            StatusNotificationSeverity.Success);
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
            ShowStatus(
                _localization.GetString("ArticleLinkUnavailable"),
                StatusNotificationSeverity.Warning);
            return;
        }

        await Launcher.LaunchUriAsync(uri);
    }

    public async Task<bool> DeleteFeedsAsync(
        IReadOnlyCollection<Feed> feeds,
        CancellationToken cancellationToken = default)
    {
        if (feeds.Count == 0)
        {
            return false;
        }

        if (IsBusy)
        {
            DiagnosticLog.Warning(
                "feed.delete_rejected",
                new { reason = "view_model_busy", feedCount = feeds.Count });
            ShowStatus(
                _localization.GetString("SubscriptionOperationBusy"),
                StatusNotificationSeverity.Warning);
            return false;
        }

        IsBusy = true;
        try
        {
            var normalizedFeeds = feeds.DistinctBy(feed => feed.Id).ToArray();
            var deletedFeedIds = normalizedFeeds.Select(feed => feed.Id).ToHashSet();
            var selectedFeedIds = _selectedFeedIds
                .Where(feedId => !deletedFeedIds.Contains(feedId))
                .ToArray();
            var selectedGroupId = SelectedGroup?.Id;
            await _repository.DeleteFeedsAsync(deletedFeedIds, cancellationToken);
            RemoveDeletedFeeds(deletedFeedIds, selectedFeedIds, selectedGroupId);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(
                normalizedFeeds.Length == 1
                    ? _localization.Format("FeedRemoved", normalizedFeeds[0].Title)
                    : _localization.Format("FeedsRemoved", normalizedFeeds.Length),
                StatusNotificationSeverity.Success);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RemoveDeletedFeeds(
        IReadOnlySet<long> deletedFeedIds,
        IReadOnlyCollection<long> selectedFeedIds,
        long? selectedGroupId)
    {
        for (var index = Feeds.Count - 1; index >= 0; index--)
        {
            if (deletedFeedIds.Contains(Feeds[index].Id))
            {
                Feeds.RemoveAt(index);
            }
        }

        // Deselect deleted rows while their navigation items still exist. This lets
        // MainWindow synchronize ListView selection without treating the subsequent
        // collection removals as a new user selection.
        ApplyNavigationSelection(selectedFeedIds, selectedGroupId);

        foreach (var groupItem in FeedNavigationRows.Where(item => item.IsGroup))
        {
            groupItem.RemoveFeedChildren(deletedFeedIds);
        }

        for (var index = FeedNavigationRows.Count - 1; index >= 0; index--)
        {
            if (FeedNavigationRows[index].Feed is { } feed &&
                deletedFeedIds.Contains(feed.Id))
            {
                FeedNavigationRows.RemoveAt(index);
            }
        }

        foreach (var feedId in deletedFeedIds)
        {
            _feedRefreshErrors.Remove(feedId);
        }

        OnPropertyChanged(nameof(LastRefreshedAt));
        UnreadTotal = Feeds.Sum(feed => feed.UnreadCount);
        OnPropertyChanged(nameof(SelectedArticleNavigationItem));
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
            ShowStatus(
                _localization.GetString("InvalidGroupName"),
                StatusNotificationSeverity.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var group = await _repository.AddFeedGroupAsync(normalizedName, cancellationToken);
            await ReloadFeedsAsync([], group.Id, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(
                _localization.Format("GroupCreated", group.Name),
                StatusNotificationSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus(
                _localization.Format("GroupOperationFailed", exception.Message),
                StatusNotificationSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task UpdateFeedSubscriptionAsync(
        Feed feed,
        string name,
        string address,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
        {
            ShowStatus(
                _localization.GetString("InvalidFeedName"),
                StatusNotificationSeverity.Warning);
            return;
        }

        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            ShowStatus(
                _localization.GetString("InvalidFeedAddress"),
                StatusNotificationSeverity.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var selectedFeedIds = _selectedFeedIds.ToArray();
            var selectedGroupId = SelectedGroup?.Id;
            var updated = await _repository.UpdateFeedSubscriptionAsync(
                feed.Id,
                normalizedName,
                uri.AbsoluteUri,
                cancellationToken);
            if (!updated)
            {
                ShowStatus(
                    _localization.GetString("FeedAddressAlreadySubscribed"),
                    StatusNotificationSeverity.Warning);
                return;
            }

            if (!string.Equals(feed.Url, uri.AbsoluteUri, StringComparison.Ordinal))
            {
                _feedRefreshErrors.Remove(feed.Id);
            }

            await ReloadFeedsAsync(selectedFeedIds, selectedGroupId, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(
                _localization.Format("FeedUpdated", normalizedName),
                StatusNotificationSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus(
                _localization.Format("FeedUpdateFailed", exception.Message),
                StatusNotificationSeverity.Error);
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
            ShowStatus(
                _localization.GetString("InvalidGroupName"),
                StatusNotificationSeverity.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.RenameFeedGroupAsync(group.Id, normalizedName, cancellationToken);
            await ReloadFeedsAsync([], group.Id, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(
                _localization.Format("GroupRenamed", normalizedName),
                StatusNotificationSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus(
                _localization.Format("GroupOperationFailed", exception.Message),
                StatusNotificationSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> DeleteFeedGroupAsync(
        FeedGroup group,
        bool deleteFeeds,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            DiagnosticLog.Warning(
                "feed_group.delete_rejected",
                new { reason = "view_model_busy", groupId = group.Id, deleteFeeds });
            ShowStatus(
                _localization.GetString("SubscriptionOperationBusy"),
                StatusNotificationSeverity.Warning);
            return false;
        }

        IsBusy = true;
        try
        {
            var deletedFeedIds = deleteFeeds
                ? Feeds
                    .Where(feed => feed.GroupId == group.Id)
                    .Select(feed => feed.Id)
                    .ToHashSet()
                : new HashSet<long>();
            var selectedFeedIds = _selectedFeedIds
                .Where(feedId => !deletedFeedIds.Contains(feedId))
                .ToArray();
            var selectedGroupId = SelectedGroup?.Id == group.Id ? null : SelectedGroup?.Id;
            await _repository.DeleteFeedGroupAsync(group.Id, deleteFeeds, cancellationToken);
            await ReloadFeedsAsync(selectedFeedIds, selectedGroupId, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(
                _localization.Format(
                    deleteFeeds ? "GroupAndFeedsRemoved" : "GroupRemoved",
                    group.Name),
                StatusNotificationSeverity.Success);
            return true;
        }
        catch (Exception exception)
        {
            ShowStatus(
                _localization.Format("GroupOperationFailed", exception.Message),
                StatusNotificationSeverity.Error);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SetFeedsGroupAsync(
        IReadOnlyCollection<Feed> feeds,
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy || feeds.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var normalizedFeeds = feeds.DistinctBy(feed => feed.Id).ToArray();
            var selectedFeedIds = _selectedFeedIds.ToArray();
            var selectedGroupId = SelectedGroup?.Id;
            await _repository.SetFeedsGroupAsync(
                normalizedFeeds.Select(feed => feed.Id).ToArray(),
                groupId,
                cancellationToken);
            await ReloadFeedsAsync(selectedFeedIds, selectedGroupId, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(
                normalizedFeeds.Length == 1
                    ? _localization.Format("FeedGroupChanged", normalizedFeeds[0].Title)
                    : _localization.Format("FeedsGroupChanged", normalizedFeeds.Length),
                StatusNotificationSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus(
                _localization.Format("GroupOperationFailed", exception.Message),
                StatusNotificationSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadFeedsAsync(
        IReadOnlyCollection<long> selectedFeedIds,
        long? selectedGroupId,
        CancellationToken cancellationToken = default,
        bool preserveNavigationItems = false)
    {
        var groups = await _repository.GetFeedGroupsAsync(cancellationToken);
        var feeds = await _repository.GetFeedsAsync(cancellationToken);
        ApplyFeedRefreshErrors(feeds);

        if (!preserveNavigationItems || !TrySynchronizeFeedsAndNavigation(groups, feeds))
        {
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

            RebuildFeedNavigation(selectedFeedIds, selectedGroupId);
        }

        OnPropertyChanged(nameof(LastRefreshedAt));
        UnreadTotal = Feeds.Sum(feed => feed.UnreadCount);
        OnPropertyChanged(nameof(ArticleListTitle));
    }

    private bool TrySynchronizeFeedsAndNavigation(
        IReadOnlyList<FeedGroup> groups,
        IReadOnlyList<Feed> feeds)
    {
        if (groups.Count != FeedGroups.Count || feeds.Count != Feeds.Count)
        {
            return false;
        }

        var currentGroups = FeedGroups.ToDictionary(group => group.Id);
        var currentFeeds = Feeds.ToDictionary(feed => feed.Id);
        if (groups.Any(group =>
                !currentGroups.TryGetValue(group.Id, out var currentGroup) ||
                !string.Equals(currentGroup.Name, group.Name, StringComparison.Ordinal)) ||
            feeds.Any(feed =>
                !currentFeeds.TryGetValue(feed.Id, out var currentFeed) ||
                !string.Equals(currentFeed.Url, feed.Url, StringComparison.Ordinal) ||
                currentFeed.GroupId != feed.GroupId))
        {
            return false;
        }

        var rootItems = FeedNavigationRows
            .Where(item => !item.IsChild)
            .ToArray();
        var feedItems = rootItems
            .Where(item => item.Feed is not null)
            .ToDictionary(item => item.Feed!.Id);
        var groupItems = rootItems
            .Where(item => item.Group is not null)
            .ToDictionary(item => item.Group!.Id);
        if (feedItems.Count != feeds.Count(feed => feed.GroupId is null) ||
            groupItems.Count != groups.Count ||
            feeds.Any(feed => feed.GroupId is null && !feedItems.ContainsKey(feed.Id)) ||
            groups.Any(group => !groupItems.ContainsKey(group.Id)))
        {
            return false;
        }

        foreach (var group in groups)
        {
            var childIds = groupItems[group.Id].Children
                .Select(item => item.Feed!.Id)
                .ToHashSet();
            if (!childIds.SetEquals(feeds
                    .Where(feed => feed.GroupId == group.Id)
                    .Select(feed => feed.Id)))
            {
                return false;
            }
        }

        foreach (var feed in feeds)
        {
            UpdateFeed(currentFeeds[feed.Id], feed);
        }

        var synchronizedGroups = groups
            .Select(group => currentGroups[group.Id])
            .ToArray();
        var synchronizedFeeds = feeds
            .Select(feed => currentFeeds[feed.Id])
            .ToArray();
        SynchronizeItems(FeedGroups, synchronizedGroups);
        SynchronizeItems(Feeds, synchronizedFeeds);

        var navigationRows = new List<FeedNavigationItem>();
        foreach (var feed in synchronizedFeeds.Where(feed => feed.GroupId is null))
        {
            navigationRows.Add(feedItems[feed.Id]);
        }

        foreach (var group in synchronizedGroups)
        {
            var groupItem = groupItems[group.Id];
            var childItems = groupItem.Children.ToDictionary(item => item.Feed!.Id);
            var synchronizedChildren = synchronizedFeeds
                .Where(feed => feed.GroupId == group.Id)
                .Select(feed => childItems[feed.Id])
                .ToArray();
            SynchronizeItems(groupItem.Children, synchronizedChildren);
            navigationRows.Add(groupItem);
            if (groupItem.IsExpanded)
            {
                navigationRows.AddRange(synchronizedChildren);
            }
        }

        SynchronizeItems(FeedNavigationRows, navigationRows);
        return true;
    }

    private static void UpdateFeed(Feed target, Feed source)
    {
        target.Title = source.Title;
        target.SiteUrl = source.SiteUrl;
        target.Description = source.Description;
        target.IconUrl = source.IconUrl;
        target.LastRefreshedAt = source.LastRefreshedAt;
        target.LastRefreshError = source.LastRefreshError;
        target.UnreadCount = source.UnreadCount;
        target.ETag = source.ETag;
        target.LastModifiedAt = source.LastModifiedAt;
    }

    private async Task ReloadArticlesAsync(
        CancellationToken cancellationToken = default,
        bool preserveSelectedArticle = false)
    {
        var loadVersion = Interlocked.Increment(ref _articleLoadVersion);
        var selectionVersion = Volatile.Read(ref _navigationSelectionVersion);
        var feedIds = _selectedFeedIds.ToArray();
        var groupId = SelectedGroup?.Id;
        var filter = CurrentFilter;
        var searchQuery = ArticleSearchQuery;
        var articles = await _repository.GetArticlesAsync(
            feedIds.Length == 0 ? null : feedIds,
            groupId,
            filter,
            searchQuery,
            cancellationToken);
        if (loadVersion != Volatile.Read(ref _articleLoadVersion) ||
            selectionVersion != Volatile.Read(ref _navigationSelectionVersion) ||
            groupId != SelectedGroup?.Id ||
            filter != CurrentFilter ||
            !string.Equals(searchQuery, ArticleSearchQuery, StringComparison.Ordinal))
        {
            return;
        }

        var feedTitleVisibility = SelectedFeedCount == 1
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
        foreach (var article in articles)
        {
            article.FeedTitleVisibility = feedTitleVisibility;
        }

        var selectedArticle = preserveSelectedArticle ? SelectedArticle : null;
        if (selectedArticle is not null && articles.Any(article => article.Id == selectedArticle.Id))
        {
            SynchronizeArticles(articles, selectedArticle);
        }
        else
        {
            Articles.Clear();
            foreach (var article in articles)
            {
                Articles.Add(article);
            }

            SelectedArticle = null;
        }

        UpdateArticleCount();
        OnPropertyChanged(nameof(ArticleListTitle));
    }

    private void SynchronizeArticles(IReadOnlyList<Article> articles, Article selectedArticle)
    {
        var currentArticles = Articles.ToDictionary(article => article.Id);
        currentArticles[selectedArticle.Id] = selectedArticle;
        var synchronizedArticles = articles
            .Select(article => currentArticles.GetValueOrDefault(article.Id) ?? article)
            .ToArray();

        SynchronizeItems(Articles, synchronizedArticles);
    }

    private static void SynchronizeItems<T>(
        ObservableCollection<T> collection,
        IReadOnlyList<T> items)
        where T : class
    {
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (index < collection.Count && ReferenceEquals(collection[index], item))
            {
                continue;
            }

            var currentIndex = collection.IndexOf(item);
            if (currentIndex >= 0)
            {
                collection.Move(currentIndex, index);
            }
            else
            {
                collection.Insert(index, item);
            }
        }

        while (collection.Count > items.Count)
        {
            collection.RemoveAt(collection.Count - 1);
        }
    }

    private void UpdateArticleCount(int? count = null) =>
        ArticleCountText = _localization.FormatArticleCount(count ?? Articles.Count);

    private void RebuildFeedNavigation(
        IReadOnlyCollection<long> selectedFeedIds,
        long? selectedGroupId)
    {
        var expansionStates = FeedNavigationRows
            .Where(item => item.Group is not null)
            .ToDictionary(item => item.Group!.Id, item => item.IsExpanded);
        var actionLabels = new FeedNavigationItem.ActionLabels(
            _localization.GetString("RefreshFeed"),
            _localization.GetString("EditFeed"),
            _localization.GetString("ChangeGroup"),
            _localization.GetString("Remove"),
            _localization.GetString("RenameGroup"),
            _localization.GetString("RemoveGroup"),
            _localization.GetString("FeedRefreshFailed"));
        FeedNavigationRows.Clear();

        var ungroupedItems = Feeds
            .Where(feed => feed.GroupId is null)
            .Select(feed => FeedNavigationItem.ForFeed(feed, actionLabels))
            .ToArray();
        foreach (var item in ungroupedItems)
        {
            FeedNavigationRows.Add(item);
        }

        foreach (var group in FeedGroups)
        {
            var item = FeedNavigationItem.ForGroup(
                group,
                Feeds.Where(feed => feed.GroupId == group.Id),
                actionLabels);
            item.IsExpanded = !expansionStates.TryGetValue(group.Id, out var isExpanded) || isExpanded;
            FeedNavigationRows.Add(item);
            if (item.IsExpanded)
            {
                foreach (var child in item.Children)
                {
                    FeedNavigationRows.Add(child);
                }
            }
        }

        ApplyNavigationSelection(selectedFeedIds, selectedGroupId);
        OnPropertyChanged(nameof(SelectedArticleNavigationItem));
    }

    private void ApplyNavigationSelection(
        IReadOnlyCollection<long> selectedFeedIds,
        long? selectedGroupId)
    {
        var availableFeedIds = Feeds.Select(feed => feed.Id).ToHashSet();
        var normalizedFeedIds = selectedFeedIds
            .Where(availableFeedIds.Contains)
            .ToHashSet();
        var selectedGroup = normalizedFeedIds.Count == 0 && selectedGroupId is not null
            ? FeedGroups.FirstOrDefault(group => group.Id == selectedGroupId.Value)
            : null;
        var selectionChanged = !_selectedFeedIds.SetEquals(normalizedFeedIds) ||
                               SelectedGroup?.Id != selectedGroup?.Id;

        _selectedFeedIds.Clear();
        _selectedFeedIds.UnionWith(normalizedFeedIds);
        SelectedFeed = _selectedFeedIds.Count == 1
            ? Feeds.FirstOrDefault(feed => _selectedFeedIds.Contains(feed.Id))
            : null;
        SelectedGroup = selectedGroup;
        SelectedNavigationItem = SelectedFeed is not null
            ? FindFeedNavigationItem(SelectedFeed.Id)
            : SelectedGroup is not null
                ? FeedNavigationRows.FirstOrDefault(item => item.Group?.Id == SelectedGroup.Id)
                : null;

        if (selectionChanged)
        {
            Interlocked.Increment(ref _navigationSelectionVersion);
        }

        OnPropertyChanged(nameof(SelectedFeedIds));
        OnPropertyChanged(nameof(SelectedFeedCount));
        OnPropertyChanged(nameof(ArticleListTitle));
        OnPropertyChanged(nameof(ArticleListGlyph));
    }

    private FeedNavigationItem? FindFeedNavigationItem(long feedId) =>
        FeedNavigationRows
            .Where(item => !item.IsChild)
            .SelectMany(item => item.IsGroup ? item.Children : [item])
            .FirstOrDefault(item => item.Feed?.Id == feedId);

    public void RefreshImportedFeedsInBackground(
        IReadOnlyList<long> feedIds,
        CancellationToken cancellationToken = default)
    {
        if (feedIds.Count > 0)
        {
            DiagnosticLog.Information(
                "opml.background_refresh_scheduled",
                new
                {
                    importedFeedCount = feedIds.Count,
                    IsBusy
                });
            _ = RefreshImportedFeedsCoreAsync(feedIds, cancellationToken);
        }
    }

    private async Task RefreshImportedFeedsCoreAsync(
        IReadOnlyList<long> feedIds,
        CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            DiagnosticLog.Warning(
                "opml.background_refresh_rejected",
                new
                {
                    reason = "view_model_busy",
                    importedFeedCount = feedIds.Count
                });
            return;
        }

        IsBusy = true;
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var importedFeedIds = feedIds.ToHashSet();
            var importedFeeds = Feeds
                .Where(feed => importedFeedIds.Contains(feed.Id))
                .ToArray();
            var outcomes = await Task.WhenAll(
                importedFeeds.Select(feed => RefreshFeedCoreAsync(feed, cancellationToken)));
            if (cancellationToken.IsCancellationRequested)
            {
                DiagnosticLog.Information(
                    "opml.background_refresh_cancelled",
                    new
                    {
                        importedFeedCount = feedIds.Count,
                        elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
                    });
                return;
            }

            UpdateFeedRefreshErrors(outcomes);
            var selectedFeedIds = _selectedFeedIds.ToArray();
            var selectedGroupId = SelectedGroup?.Id;
            await ReloadFeedsAsync(
                selectedFeedIds,
                selectedGroupId,
                cancellationToken,
                preserveNavigationItems: true);
            await ReloadArticlesAsync(cancellationToken, preserveSelectedArticle: true);
            DiagnosticLog.MemorySnapshot(
                "opml.background_refresh_completed",
                new
                {
                    requestedFeedCount = feedIds.Count,
                    refreshedFeedCount = importedFeeds.Length,
                    failureCount = outcomes.Count(outcome => outcome.Error is not null),
                    newArticleCount = outcomes
                        .Where(outcome => outcome.Result is not null)
                        .Sum(outcome => outcome.Result!.NewArticles.Count),
                    elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Information(
                "opml.background_refresh_cancelled",
                new
                {
                    importedFeedCount = feedIds.Count,
                    elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
                });
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(
                "opml.background_refresh_failed",
                exception,
                new
                {
                    importedFeedCount = feedIds.Count,
                    elapsedMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
                });
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<long?> GetOrCreateImportedGroupIdAsync(
        string? groupName,
        IDictionary<string, long> groupIds,
        CancellationToken cancellationToken)
    {
        var normalizedName = groupName?.Trim();
        if (string.IsNullOrEmpty(normalizedName))
        {
            return null;
        }

        if (normalizedName.Length > 100)
        {
            normalizedName = normalizedName[..100].TrimEnd();
        }

        if (groupIds.TryGetValue(normalizedName, out var existingGroupId))
        {
            return existingGroupId;
        }

        var group = await _repository.AddFeedGroupAsync(normalizedName, cancellationToken);
        groupIds.Add(group.Name, group.Id);
        return group.Id;
    }

    private static Uri? TryCreateHttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    private async Task<FeedRefreshOutcome> RefreshFeedCoreAsync(
        Feed feed,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return new FeedRefreshOutcome(
                feed,
                await _refreshService.RefreshAsync(feed, cancellationToken),
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(
                "refresh.feed_failed",
                exception,
                new
                {
                    feedId = feed.Id,
                    feedHost = TryCreateHttpUri(feed.Url)?.Host
                });
            return new FeedRefreshOutcome(feed, null, exception);
        }
    }

    private void UpdateFeedRefreshErrors(IEnumerable<FeedRefreshOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            if (outcome.Error is null)
            {
                _feedRefreshErrors.Remove(outcome.Feed.Id);
            }
            else
            {
                _feedRefreshErrors[outcome.Feed.Id] = GetRefreshErrorMessage(outcome.Error);
            }
        }
    }

    private void ApplyFeedRefreshErrors(IReadOnlyList<Feed> feeds)
    {
        var availableFeedIds = feeds.Select(feed => feed.Id).ToHashSet();
        foreach (var removedFeedId in _feedRefreshErrors.Keys
                     .Where(feedId => !availableFeedIds.Contains(feedId))
                     .ToArray())
        {
            _feedRefreshErrors.Remove(removedFeedId);
        }

        foreach (var feed in feeds)
        {
            if (_feedRefreshErrors.TryGetValue(feed.Id, out var error))
            {
                feed.LastRefreshError = error;
            }
        }
    }

    private string GetRefreshErrorMessage(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? _localization.GetString("UnknownRefreshError")
            : exception.Message.Trim();

    private void ShowStatus(
        string message,
        StatusNotificationSeverity severity = StatusNotificationSeverity.Informational,
        string? title = null,
        string? actionText = null,
        IReadOnlyList<StatusNotificationDetail>? details = null) =>
        StatusNotificationRequested?.Invoke(
            this,
            new StatusNotificationRequestedEventArgs(message, severity, title, actionText, details));

    private sealed record FeedRefreshOutcome(
        Feed Feed,
        FeedRefreshResult? Result,
        Exception? Error);
}

public sealed record SubscriptionImportResult(
    int ImportedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<long> ImportedFeedIds);
