using FluxReader.Core.Models;
using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class ArticleContentParserTests
{
    [TestMethod]
    public void Parse_ResolvesMarkdownImageWithRelativePathAndSpaces()
    {
        const string content = "Before\n\n![](./All-in-One Clipboard Promotion.png)\n\nAfter";

        var blocks = ArticleContentParser.Parse(
            content,
            new Uri("https://example.com/posts/228"));

        Assert.HasCount(3, blocks);
        Assert.AreEqual(ArticleContentBlockKind.Text, blocks[0].Kind);
        Assert.AreEqual("Before", blocks[0].Text);
        Assert.AreEqual(ArticleContentBlockKind.Image, blocks[1].Kind);
        Assert.AreEqual(
            new Uri("https://example.com/posts/All-in-One%20Clipboard%20Promotion.png"),
            blocks[1].ImageUri);
        Assert.AreEqual("After", blocks[2].Text);
    }

    [TestMethod]
    public void Parse_ExtractsHtmlImageAndPreservesItsPosition()
    {
        const string content = """
            <p>Before</p>
            <img src="../images/photo.png" alt="A photo">
            <p>After</p>
            """;

        var blocks = ArticleContentParser.Parse(
            content,
            new Uri("https://example.com/posts/2026/entry"));

        Assert.HasCount(3, blocks);
        Assert.AreEqual("Before", blocks[0].Text);
        Assert.AreEqual(ArticleContentBlockKind.Image, blocks[1].Kind);
        Assert.AreEqual("A photo", blocks[1].Text);
        Assert.AreEqual(
            new Uri("https://example.com/posts/images/photo.png"),
            blocks[1].ImageUri);
        Assert.AreEqual("After", blocks[2].Text);
    }

    [TestMethod]
    public void Parse_DoesNotLoadNonHttpImage()
    {
        const string content = "Before ![unsafe](file:///C:/secret.png) After";

        var blocks = ArticleContentParser.Parse(content);

        Assert.HasCount(1, blocks);
        Assert.AreEqual(ArticleContentBlockKind.Text, blocks[0].Kind);
        StringAssert.Contains(blocks[0].Text, "file:///C:/secret.png");
    }

    [TestMethod]
    public void CreatePreviewText_PrefersSummaryAndConvertsItToPlainText()
    {
        var preview = ArticleContentParser.CreatePreviewText(
            "<p>Summary <strong>text</strong></p>",
            "Content text",
            null,
            256);

        Assert.AreEqual("Summary text", preview);
    }

    [TestMethod]
    public void CreatePreviewText_UsesContentWhenSummaryIsEmptyAndLimitsLength()
    {
        var preview = ArticleContentParser.CreatePreviewText(
            "  ",
            "Content text",
            null,
            7);

        Assert.AreEqual("Content…", preview);
    }
}
