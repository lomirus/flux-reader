using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class FeedListSelectionResolverTests
{
    [TestMethod]
    public void Resolve_ReturnsSelectedFeeds()
    {
        var result = FeedListSelectionResolver.Resolve([10, 20], []);

        CollectionAssert.AreEquivalent(new long[] { 10, 20 }, result.FeedIds.ToArray());
        Assert.IsNull(result.GroupId);
    }

    [TestMethod]
    public void Resolve_IgnoresGroupRowsInsideFeedRange()
    {
        var result = FeedListSelectionResolver.Resolve([10, 20], [100]);

        CollectionAssert.AreEquivalent(new long[] { 10, 20 }, result.FeedIds.ToArray());
        Assert.IsNull(result.GroupId);
    }

    [TestMethod]
    public void Resolve_ReturnsSingleSelectedGroup()
    {
        var result = FeedListSelectionResolver.Resolve([], [100]);

        Assert.IsEmpty(result.FeedIds);
        Assert.AreEqual(100, result.GroupId);
    }

    [TestMethod]
    public void Resolve_TreatsAmbiguousGroupRangeAsNoNavigationSelection()
    {
        var result = FeedListSelectionResolver.Resolve([], [100, 200]);

        Assert.IsEmpty(result.FeedIds);
        Assert.IsNull(result.GroupId);
    }

    [TestMethod]
    public void Resolve_TreatsEmptySelectionAsNoNavigationSelection()
    {
        var result = FeedListSelectionResolver.Resolve([], []);

        Assert.IsEmpty(result.FeedIds);
        Assert.IsNull(result.GroupId);
    }

    [TestMethod]
    public void ResolveArticleNavigation_PreservesMatchingScopeOtherwiseSelectsArticleFeed()
    {
        Assert.IsEmpty(FeedListSelectionResolver.ResolveArticleNavigation([], null, 10).FeedIds);
        CollectionAssert.AreEquivalent(
            new long[] { 10 },
            FeedListSelectionResolver.ResolveArticleNavigation([10], null, 10).FeedIds.ToArray());
        CollectionAssert.AreEquivalent(
            new long[] { 10 },
            FeedListSelectionResolver.ResolveArticleNavigation([20], null, 10).FeedIds.ToArray());
    }
}
