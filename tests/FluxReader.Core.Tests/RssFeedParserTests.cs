using System.Text;
using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class RssFeedParserTests
{
    private readonly RssFeedParser _parser = new();

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
        Assert.HasCount(1, result.Articles);
        Assert.AreEqual("Hello & WinUI", result.Articles[0].Title);
        Assert.AreEqual("A short summary.", result.Articles[0].Summary);
        Assert.AreEqual("Full article text.", result.Articles[0].Content);
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
        Assert.AreEqual("Body", result.Articles[0].Content);
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
