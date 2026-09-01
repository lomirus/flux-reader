namespace FluxReader.Core.Services;

public static class FeedListSelectionResolver
{
    public static FeedListSelection Resolve(
        IEnumerable<long> selectedFeedIds,
        IEnumerable<long> selectedGroupIds)
    {
        ArgumentNullException.ThrowIfNull(selectedFeedIds);
        ArgumentNullException.ThrowIfNull(selectedGroupIds);

        var feedIds = selectedFeedIds.ToHashSet();
        if (feedIds.Count > 0)
        {
            return new FeedListSelection(feedIds, GroupId: null);
        }

        var groupIds = selectedGroupIds
            .Distinct()
            .Take(2)
            .ToArray();
        return groupIds.Length == 1
            ? new FeedListSelection(feedIds, groupIds[0])
            : new FeedListSelection(feedIds, GroupId: null);
    }

    public static FeedListSelection ResolveArticleNavigation(
        IEnumerable<long> selectedFeedIds,
        long? selectedGroupId,
        long articleFeedId)
    {
        ArgumentNullException.ThrowIfNull(selectedFeedIds);

        var feedIds = selectedFeedIds.ToHashSet();
        return (feedIds.Count == 0 && selectedGroupId is null) || feedIds.Contains(articleFeedId)
            ? new FeedListSelection(feedIds, GroupId: null)
            : new FeedListSelection(new HashSet<long> { articleFeedId }, GroupId: null);
    }
}

public sealed record FeedListSelection(
    IReadOnlySet<long> FeedIds,
    long? GroupId);
