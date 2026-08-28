using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class ArticleSearchMatcherTests
{
    [TestMethod]
    public void GetMatchRank_TitleMatchHasHighestPriority()
    {
        var rank = ArticleSearchMatcher.GetMatchRank(
            "WinUI search",
            "summary",
            "content",
            "winui");

        Assert.AreEqual(ArticleSearchMatcher.TitleMatchRank, rank);
    }

    [TestMethod]
    public void GetMatchRank_SummaryOrContentMatchHasBodyPriority()
    {
        var summaryRank = ArticleSearchMatcher.GetMatchRank(
            "title",
            "WinUI summary",
            "content",
            "winui");
        var contentRank = ArticleSearchMatcher.GetMatchRank(
            "title",
            "summary",
            "WinUI content",
            "winui");

        Assert.AreEqual(ArticleSearchMatcher.BodyMatchRank, summaryRank);
        Assert.AreEqual(ArticleSearchMatcher.BodyMatchRank, contentRank);
    }

    [TestMethod]
    public void GetMatchRank_NoMatchIsExcluded()
    {
        var rank = ArticleSearchMatcher.GetMatchRank(
            "title",
            "summary",
            "content",
            "missing");

        Assert.AreEqual(ArticleSearchMatcher.NoMatchRank, rank);
    }

    [TestMethod]
    public void GetMatchRank_TreatsSqlWildcardCharactersLiterally()
    {
        var matchingRank = ArticleSearchMatcher.GetMatchRank(
            "100% coverage",
            "summary",
            "content",
            "100%");
        var nonMatchingRank = ArticleSearchMatcher.GetMatchRank(
            "100 percent coverage",
            "summary",
            "content",
            "100%");

        Assert.AreEqual(ArticleSearchMatcher.TitleMatchRank, matchingRank);
        Assert.AreEqual(ArticleSearchMatcher.NoMatchRank, nonMatchingRank);
    }

    [TestMethod]
    public void GetMatchRank_SearchesRenderedTextInsteadOfHtmlTags()
    {
        var textRank = ArticleSearchMatcher.GetMatchRank(
            "title",
            "summary",
            "<section><p>Rendered content</p></section>",
            "rendered");
        var tagRank = ArticleSearchMatcher.GetMatchRank(
            "title",
            "summary",
            "<section><p>Rendered content</p></section>",
            "section");

        Assert.AreEqual(ArticleSearchMatcher.BodyMatchRank, textRank);
        Assert.AreEqual(ArticleSearchMatcher.NoMatchRank, tagRank);
    }
}
