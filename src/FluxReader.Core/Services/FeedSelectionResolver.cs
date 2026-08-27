namespace FluxReader.Core.Services;

public static class FeedSelectionResolver
{
    public static FeedSelectionResult Resolve(
        IReadOnlySet<long> currentSelection,
        IReadOnlyList<long> feedIdsInNavigationOrder,
        long clickedFeedId,
        long? anchorFeedId,
        bool isControlPressed,
        bool isShiftPressed)
    {
        ArgumentNullException.ThrowIfNull(currentSelection);
        ArgumentNullException.ThrowIfNull(feedIdsInNavigationOrder);

        if (isShiftPressed && anchorFeedId is { } anchorId)
        {
            var anchorIndex = IndexOf(feedIdsInNavigationOrder, anchorId);
            var clickedIndex = IndexOf(feedIdsInNavigationOrder, clickedFeedId);
            if (anchorIndex >= 0 && clickedIndex >= 0)
            {
                var selectedFeedIds = isControlPressed
                    ? currentSelection.ToHashSet()
                    : [];
                var firstIndex = Math.Min(anchorIndex, clickedIndex);
                var lastIndex = Math.Max(anchorIndex, clickedIndex);
                for (var index = firstIndex; index <= lastIndex; index++)
                {
                    selectedFeedIds.Add(feedIdsInNavigationOrder[index]);
                }

                return new FeedSelectionResult(selectedFeedIds, anchorId);
            }

            return Single(clickedFeedId);
        }

        if (isControlPressed)
        {
            var selectedFeedIds = currentSelection.ToHashSet();
            if (!selectedFeedIds.Add(clickedFeedId))
            {
                selectedFeedIds.Remove(clickedFeedId);
            }

            return new FeedSelectionResult(selectedFeedIds, clickedFeedId);
        }

        return Single(clickedFeedId);
    }

    private static FeedSelectionResult Single(long feedId) =>
        new(new HashSet<long> { feedId }, feedId);

    private static int IndexOf(IReadOnlyList<long> feedIds, long feedId)
    {
        for (var index = 0; index < feedIds.Count; index++)
        {
            if (feedIds[index] == feedId)
            {
                return index;
            }
        }

        return -1;
    }
}

public sealed record FeedSelectionResult(
    IReadOnlySet<long> SelectedFeedIds,
    long AnchorFeedId);
