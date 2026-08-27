using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class FeedSelectionResolverTests
{
    private static readonly IReadOnlyList<long> FeedOrder = [10, 20, 21, 22, 30];

    [TestMethod]
    public void PlainClickReplacesExistingSelection()
    {
        var result = Resolve(new HashSet<long> { 10, 20 }, clickedFeedId: 30);

        CollectionAssert.AreEquivalent(new long[] { 30 }, result.SelectedFeedIds.ToArray());
        Assert.AreEqual(30, result.AnchorFeedId);
    }

    [TestMethod]
    public void ControlClickAddsAndRemovesClickedFeed()
    {
        var added = Resolve(new HashSet<long> { 10 }, clickedFeedId: 20, isControlPressed: true);
        var removed = Resolve(new HashSet<long> { 10, 20 }, clickedFeedId: 20, isControlPressed: true);

        CollectionAssert.AreEquivalent(new long[] { 10, 20 }, added.SelectedFeedIds.ToArray());
        CollectionAssert.AreEquivalent(new long[] { 10 }, removed.SelectedFeedIds.ToArray());
        Assert.AreEqual(20, added.AnchorFeedId);
        Assert.AreEqual(20, removed.AnchorFeedId);
    }

    [TestMethod]
    public void ShiftClickSelectsEveryFeedInNavigationOrderIncludingCollapsedGroupChildren()
    {
        var result = Resolve(
            new HashSet<long> { 10 },
            clickedFeedId: 30,
            anchorFeedId: 10,
            isShiftPressed: true);

        CollectionAssert.AreEquivalent(FeedOrder.ToArray(), result.SelectedFeedIds.ToArray());
        Assert.AreEqual(10, result.AnchorFeedId);
    }

    [TestMethod]
    public void ShiftClickSupportsReverseRanges()
    {
        var result = Resolve(
            new HashSet<long> { 30 },
            clickedFeedId: 20,
            anchorFeedId: 30,
            isShiftPressed: true);

        CollectionAssert.AreEquivalent(new long[] { 20, 21, 22, 30 }, result.SelectedFeedIds.ToArray());
        Assert.AreEqual(30, result.AnchorFeedId);
    }

    [TestMethod]
    public void ControlShiftClickAddsRangeToExistingSelection()
    {
        var result = Resolve(
            new HashSet<long> { 10, 30 },
            clickedFeedId: 22,
            anchorFeedId: 20,
            isControlPressed: true,
            isShiftPressed: true);

        CollectionAssert.AreEquivalent(new long[] { 10, 20, 21, 22, 30 }, result.SelectedFeedIds.ToArray());
        Assert.AreEqual(20, result.AnchorFeedId);
    }

    [TestMethod]
    public void MissingShiftAnchorFallsBackToClickedFeed()
    {
        var result = Resolve(
            new HashSet<long> { 10, 20 },
            clickedFeedId: 30,
            anchorFeedId: 999,
            isShiftPressed: true);

        CollectionAssert.AreEquivalent(new long[] { 30 }, result.SelectedFeedIds.ToArray());
        Assert.AreEqual(30, result.AnchorFeedId);
    }

    private static FeedSelectionResult Resolve(
        IReadOnlySet<long> currentSelection,
        long clickedFeedId,
        long? anchorFeedId = null,
        bool isControlPressed = false,
        bool isShiftPressed = false) =>
        FeedSelectionResolver.Resolve(
            currentSelection,
            FeedOrder,
            clickedFeedId,
            anchorFeedId,
            isControlPressed,
            isShiftPressed);
}
