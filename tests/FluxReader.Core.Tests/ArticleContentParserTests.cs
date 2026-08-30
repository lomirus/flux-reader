using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class ArticleContentParserTests
{
    [TestMethod]
    public void PrepareHtml_PreservesSemanticArticleMarkup()
    {
        const string content = """
            <h2>Example</h2>
            <p>Before <strong>important</strong> text.</p>
            <pre><code>client.messages.create(
                model="example"
            )</code></pre>
            <ul><li>First</li><li>Second</li></ul>
            """;

        var html = ArticleContentParser.PrepareHtml(content, null);

        StringAssert.Contains(html, "<h2>Example</h2>");
        StringAssert.Contains(html, "<strong>important</strong>");
        StringAssert.Contains(html, "<pre><code>");
        StringAssert.Contains(html, "model=\"example\"");
        StringAssert.Contains(html, "<ul><li>First</li><li>Second</li></ul>");
    }

    [TestMethod]
    public void PrepareHtml_RemovesActiveContentAndEventHandlers()
    {
        const string content = """
            <p onclick="alert(1)">Safe text</p>
            <script src="https://example.com/tracker.js">alert(1)</script>
            <iframe src="https://example.com/frame"></iframe>
            <form action="https://example.com/submit"><input name="secret"></form>
            <img src="javascript:alert(1)" onerror="alert(1)" alt="Unsafe">
            """;

        var html = ArticleContentParser.PrepareHtml(
            content,
            new Uri("https://example.com/articles/1"));

        StringAssert.Contains(html, "Safe text");
        Assert.IsFalse(html.Contains("onclick", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<iframe", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<form", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<input", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("javascript:", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("onerror", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PrepareHtml_ResolvesRelativeLinksAndOpensExternalLinksOutsideTheDocument()
    {
        const string content = """
            <a href="../guide">Guide</a>
            <a href="#section">Section</a>
            """;

        var html = ArticleContentParser.PrepareHtml(
            content,
            new Uri("https://example.com/posts/2026/entry"));

        StringAssert.Contains(html, "href=\"https://example.com/posts/guide\"");
        StringAssert.Contains(html, "target=\"_blank\"");
        StringAssert.Contains(html, "rel=\"noopener noreferrer\"");
        StringAssert.Contains(html, "<a href=\"#section\">Section</a>");
    }

    [TestMethod]
    public void PrepareHtml_PromotesLazyImageSource()
    {
        const string content = "<img data-src=\"./images/photo.png\" alt=\"A photo\">";

        var html = ArticleContentParser.PrepareHtml(
            content,
            new Uri("https://example.com/posts/2026/entry"));

        StringAssert.Contains(html, "src=\"https://example.com/posts/2026/images/photo.png\"");
        Assert.IsFalse(html.Contains("data-src", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ToPlainText_UsesImageAlternativeText()
    {
        const string content = "<p>Before</p><img src=\"https://example.com/photo.png\" alt=\"A photo\"><p>After</p>";

        var text = ArticleContentParser.ToPlainText(content);

        Assert.AreEqual("Before\n\nA photo\nAfter", text);
    }

    [TestMethod]
    public void CreatePreviewText_PrefersSummaryAndConvertsItToPlainText()
    {
        var preview = ArticleContentParser.CreatePreviewText(
            "<p>Summary <strong>text</strong></p>",
            "<p>Content text</p>",
            null,
            256);

        Assert.AreEqual("Summary text", preview);
    }

    [TestMethod]
    public void CreatePreviewText_UsesContentWhenSummaryIsEmptyAndLimitsLength()
    {
        var preview = ArticleContentParser.CreatePreviewText(
            "  ",
            "<p>Content text</p>",
            null,
            7);

        Assert.AreEqual("Content…", preview);
    }

    [TestMethod]
    public void CreatePreviewText_OmitsImageAlternativeTextAndCollapsesWhitespace()
    {
        const string content = """
            <div>
              <img src="https://example.com/avatar.png" alt="@ickshonpe">
              <a href="https://example.com/ickshonpe">ickshonpe</a>
              pushed to
              <a href="https://example.com/ui-layout-tree">ui-layout-tree</a>

              in <a href="https://example.com/bevy">ickshonpe/bevy</a>
            </div>
            """;

        var preview = ArticleContentParser.CreatePreviewText(
            "",
            content,
            null,
            256);

        Assert.AreEqual("ickshonpe pushed to ui-layout-tree in ickshonpe/bevy", preview);
    }

    [TestMethod]
    public void CreateHtmlDocument_DisablesScriptsAndStylesCodeBlocks()
    {
        var document = ArticleHtmlDocumentBuilder.Create(
            "<pre><code>message = client.messages.create()</code></pre>",
            null,
            useDarkTheme: true);

        StringAssert.Contains(document, "script-src 'none'");
        StringAssert.Contains(document, "frame-src 'none'");
        StringAssert.Contains(document, "color-scheme: dark");
        StringAssert.Contains(document, "background: transparent");
        StringAssert.Contains(document, "font-family: \"Cascadia Mono\"");
        StringAssert.Contains(document, "<pre><code>message = client.messages.create()</code></pre>");
    }

    [TestMethod]
    public void CreateHtmlDocument_LoadsExternalStylesheetsOnlyWhenProvided()
    {
        var withoutStylesheets = ArticleHtmlDocumentBuilder.Create(
            "<p>Article</p>",
            new Uri("https://example.com/articles/entry"),
            useDarkTheme: false);
        var withStylesheets = ArticleHtmlDocumentBuilder.Create(
            "<p>Article</p>",
            new Uri("https://example.com/articles/entry"),
            useDarkTheme: false,
            [
                new WebsiteStylesheetReference(
                    new Uri("https://cdn.example.com/site.css?theme=reader&v=2"),
                    "screen and (min-width: 40em)")
            ]);

        Assert.IsFalse(withoutStylesheets.Contains("style-src 'unsafe-inline' https: http:"));
        Assert.IsFalse(withoutStylesheets.Contains("rel=\"stylesheet\""));
        StringAssert.Contains(withStylesheets, "style-src 'unsafe-inline' https: http:");
        StringAssert.Contains(
            withStylesheets,
            "<link rel=\"stylesheet\" href=\"https://cdn.example.com/site.css?theme=reader&amp;v=2\" media=\"screen and (min-width: 40em)\">");
    }
}
