using System.Collections.ObjectModel;
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
    public partial Article? SelectedArticle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArticleListTitle))]
    [NotifyPropertyChangedFor(nameof(IsUnreadFilterEnabled))]
    public partial ArticleFilter CurrentFilter { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

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
        }
        catch (Exception exception)
        {
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
        var importedCount = 0;
        var skippedCount = document.SkippedOutlineCount;
        var failedCount = 0;
        var importedFeedIds = new List<long>();
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
                catch (Exception)
                {
                    failedCount++;
                }
            }

            var selectedFeedIds = _selectedFeedIds.ToArray();
            var selectedGroupId = SelectedGroup?.Id;
            await ReloadFeedsAsync(selectedFeedIds, selectedGroupId, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            return new SubscriptionImportResult(
                importedCount,
                skippedCount,
                failedCount,
                importedFeedIds);
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
                ShowStatus(
                    _localization.GetString("AddFeedFirst"),
                    StatusNotificationSeverity.Informational);
            }

            return;
        }

        IsBusy = true;
        try
        {
            var tasks = Feeds.Select(feed => RefreshFeedAsync(feed));
            var outcomes = await Task.WhenAll(tasks);
            var newTitles = outcomes
                .Where(outcome => outcome.Result is not null)
                .SelectMany(outcome => outcome.Result!.NewArticleTitles)
                .ToArray();
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
            await ReloadFeedsAsync(selectedFeedIds, selectedGroupId);
            await ReloadArticlesAsync();

            if (newTitles.Length > 0)
            {
                _notifications.ShowNewArticles(newTitles.Length, newTitles[0]);
            }

            ShowStatus(
                failures.Length == 0
                    ? _localization.FormatRefreshComplete(newTitles.Length)
                    : _localization.Format("RefreshPartialFailureSummary", newTitles.Length, failures.Length),
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

    public async Task DeleteFeedsAsync(
        IReadOnlyCollection<Feed> feeds,
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
            var deletedFeedIds = normalizedFeeds.Select(feed => feed.Id).ToHashSet();
            var selectedFeedIds = _selectedFeedIds
                .Where(feedId => !deletedFeedIds.Contains(feedId))
                .ToArray();
            var selectedGroupId = SelectedGroup?.Id;
            await _repository.DeleteFeedsAsync(deletedFeedIds, cancellationToken);
            await ReloadFeedsAsync(selectedFeedIds, selectedGroupId, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
            ShowStatus(
                normalizedFeeds.Length == 1
                    ? _localization.Format("FeedRemoved", normalizedFeeds[0].Title)
                    : _localization.Format("FeedsRemoved", normalizedFeeds.Length),
                StatusNotificationSeverity.Success);
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

    public async Task DeleteFeedGroupAsync(
        FeedGroup group,
        bool deleteFeeds,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
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
        CancellationToken cancellationToken = default)
    {
        var groups = await _repository.GetFeedGroupsAsync(cancellationToken);
        var feeds = await _repository.GetFeedsAsync(cancellationToken);
        ApplyFeedRefreshErrors(feeds);

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

        OnPropertyChanged(nameof(LastRefreshedAt));
        UnreadTotal = Feeds.Sum(feed => feed.UnreadCount);
        RebuildFeedNavigation(selectedFeedIds, selectedGroupId);
    }

    private async Task ReloadArticlesAsync(CancellationToken cancellationToken = default)
    {
        var loadVersion = Interlocked.Increment(ref _articleLoadVersion);
        var selectionVersion = Volatile.Read(ref _navigationSelectionVersion);
        var feedIds = _selectedFeedIds.ToArray();
        var groupId = SelectedGroup?.Id;
        var filter = CurrentFilter;
        var articles = await _repository.GetArticlesAsync(
            feedIds.Length == 0 ? null : feedIds,
            groupId,
            filter,
            cancellationToken);
        if (loadVersion != Volatile.Read(ref _articleLoadVersion) ||
            selectionVersion != Volatile.Read(ref _navigationSelectionVersion) ||
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

    private void RebuildFeedNavigation(
        IReadOnlyCollection<long> selectedFeedIds,
        long? selectedGroupId)
    {
        var expansionStates = FeedNavigationRows
            .Where(item => item.Group is not null)
            .ToDictionary(item => item.Group!.Id, item => item.IsExpanded);
        var actionLabels = new FeedNavigationItem.ActionLabels(
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
            _ = RefreshImportedFeedsCoreAsync(feedIds, cancellationToken);
        }
    }

    private async Task RefreshImportedFeedsCoreAsync(
        IReadOnlyList<long> feedIds,
        CancellationToken cancellationToken)
    {
        try
        {
            var importedFeedIds = feedIds.ToHashSet();
            var importedFeeds = Feeds
                .Where(feed => importedFeedIds.Contains(feed.Id))
                .ToArray();
            var outcomes = await Task.WhenAll(
                importedFeeds.Select(feed => RefreshFeedAsync(feed, cancellationToken)));
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            UpdateFeedRefreshErrors(outcomes);
            var selectedFeedIds = _selectedFeedIds.ToArray();
            var selectedGroupId = SelectedGroup?.Id;
            await ReloadFeedsAsync(selectedFeedIds, selectedGroupId, cancellationToken);
            await ReloadArticlesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
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

    private async Task<FeedRefreshOutcome> RefreshFeedAsync(
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
