using System.Text;
using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class RssFeedParserTests
{
    private readonly RssFeedParser _parser = new(key => key.ToString());

    [TestMethod]
    public async Task ParseAsync_Rss2_ExtractsArticleAndPlainText()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
              <channel>
                <title>Example Feed</title>
                <link>https://example.com/</link>
                <description>Example</description>
                <item>
                  <guid>article-1</guid>
                  <title>Hello &amp; WinUI</title>
                  <link>https://example.com/posts/1</link>
                  <pubDate>Tue, 25 Aug 2026 10:00:00 GMT</pubDate>
                  <description><![CDATA[<p>A short <strong>summary</strong>.</p>]]></description>
                  <content:encoded><![CDATA[<p>Full <em>article</em> text.</p>]]></content:encoded>
                </item>
              </channel>
            </rss>
            """;

        var result = await ParseAsync(xml);

        Assert.AreEqual("Example Feed", result.Title);
        Assert.IsNull(result.IconUri);
        Assert.HasCount(1, result.Articles);
        Assert.AreEqual("Hello & WinUI", result.Articles[0].Title);
        Assert.AreEqual("A short summary.", result.Articles[0].Summary);
        Assert.AreEqual("<p>Full <em>article</em> text.</p>", result.Articles[0].Content);
    }

    [TestMethod]
    public async Task ParseAsync_Rss2_ExtractsRelativeChannelImage()
    {
        const string xml = """
            <rss version="2.0">
              <channel>
                <title>Example Feed</title>
                <link>https://example.com/blog/</link>
                <image><url>/assets/feed.png</url></image>
              </channel>
            </rss>
            """;

        var result = await ParseAsync(xml);

        Assert.AreEqual(new Uri("https://example.com/assets/feed.png"), result.IconUri);
    }

    [TestMethod]
    public async Task ParseAsync_Atom_PrefersIconOverLogo()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom Feed</title>
              <icon>/icon.png</icon>
              <logo>/logo.png</logo>
            </feed>
            """;

        var result = await ParseAsync(xml);

        Assert.AreEqual(new Uri("https://example.com/icon.png"), result.IconUri);
    }

    [TestMethod]
    public async Task ParseAsync_Atom_ResolvesRelativeArticleLink()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom Feed</title>
              <entry>
                <id>tag:example.com,2026:1</id>
                <title>First entry</title>
                <link href="/posts/1" />
                <updated>2026-08-25T10:00:00Z</updated>
                <content type="html">&lt;p&gt;Body&lt;/p&gt;</content>
              </entry>
            </feed>
            """;

        var result = await ParseAsync(xml);

        Assert.HasCount(1, result.Articles);
        Assert.AreEqual(new Uri("https://example.com/posts/1"), result.Articles[0].Link);
        Assert.AreEqual("<p>Body</p>", result.Articles[0].Content);
    }

    [TestMethod]
    public async Task ParseAsync_Atom_PreservesImageFromHtmlContent()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Servo Blog</title>
              <entry>
                <id>https://servo.org/blog/example/</id>
                <title>Article with image</title>
                <link href="https://servo.org/blog/example/" />
                <updated>2026-07-31T00:00:00Z</updated>
                <content type="html">&lt;p&gt;Before&lt;/p&gt;&lt;figure&gt;&lt;a href=&quot;https://servo.org/img/blog/example.png&quot;&gt;&lt;img src=&quot;https://servo.org/img/blog/example.png&quot; alt=&quot;Servo screenshot&quot; /&gt;&lt;/a&gt;&lt;/figure&gt;&lt;p&gt;After&lt;/p&gt;</content>
              </entry>
            </feed>
            """;

        var result = await ParseAsync(xml);
        var content = result.Articles[0].Content;

        StringAssert.Contains(content, "<p>Before</p>");
        StringAssert.Contains(content, "src=\"https://servo.org/img/blog/example.png\"");
        StringAssert.Contains(content, "alt=\"Servo screenshot\"");
        StringAssert.Contains(content, "<p>After</p>");
    }

    [TestMethod]
    public async Task ParseAsync_Rss2_PreservesArticleImagesAsSafeContent()
    {
        const string xml = """
            <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
              <channel>
                <title>Example Feed</title>
                <item>
                  <guid>article-with-images</guid>
                  <title>Images</title>
                  <link>https://example.com/posts/228</link>
                  <content:encoded><![CDATA[
                    <p>Before</p>
                    <img src="./cover.png" alt="Cover">
                    <p>![](./All-in-One Clipboard Promotion.png)</p>
                    <img src="javascript:alert(1)" alt="Unsafe">
                  ]]></content:encoded>
                </item>
              </channel>
            </rss>
            """;

        var result = await ParseAsync(xml);
        var article = result.Articles[0];
        StringAssert.Contains(article.Content, "<p>Before</p>");
        StringAssert.Contains(article.Content, "src=\"https://example.com/posts/cover.png\"");
        Assert.IsFalse(article.Content.Contains("javascript:", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ParseAsync_Atom_PreservesXhtmlStructureWithoutScripts()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom Feed</title>
              <entry>
                <id>https://example.com/posts/1</id>
                <link href="https://example.com/posts/1" />
                <updated>2026-08-25T10:00:00Z</updated>
                <content type="xhtml">
                  <div xmlns="http://www.w3.org/1999/xhtml">
                    <h2>Heading</h2>
                    <pre><code>preserved()</code></pre>
                    <script>alert(1)</script>
                  </div>
                </content>
              </entry>
            </feed>
            """;

        var result = await ParseAsync(xml);
        var content = result.Articles[0].Content;

        StringAssert.Contains(content, "<h2>Heading</h2>");
        StringAssert.Contains(content, "<pre><code>preserved()</code></pre>");
        Assert.IsFalse(content.Contains("<script", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ParseAsync_EmptyValidFeed_IsAccepted()
    {
        const string xml = """
            <rss version="2.0">
              <channel>
                <title>New Feed</title>
                <link>https://example.com/</link>
                <description>No articles yet</description>
              </channel>
            </rss>
            """;

        var result = await ParseAsync(xml);

        Assert.IsEmpty(result.Articles);
    }

    [TestMethod]
    public async Task ParseAsync_Dtd_IsRejected()
    {
        const string xml = """
            <!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]>
            <rss version="2.0">
              <channel><title>&xxe;</title></channel>
            </rss>
            """;

        await Assert.ThrowsExactlyAsync<RssParseException>(() => ParseAsync(xml));
    }

    private async Task<FluxReader.Core.Models.ParsedFeed> ParseAsync(string xml)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await _parser.ParseAsync(stream, new Uri("https://example.com/feed.xml"));
    }
}
