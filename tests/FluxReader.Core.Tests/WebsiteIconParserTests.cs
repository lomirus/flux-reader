using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class WebsiteIconParserTests
{
    [TestMethod]
    public void FindIconUri_ParsesRelativeUnquotedHref()
    {
        const string html = """
            <html><head>
              <link rel="shortcut icon" href=/assets/favicon.png type=image/png>
            </head></html>
            """;

        var result = WebsiteIconParser.FindIconUri(html, new Uri("https://bevy.org/news/"));

        Assert.AreEqual(new Uri("https://bevy.org/assets/favicon.png"), result);
    }

    [TestMethod]
    public void FindIconUri_PrefersLargestStandardIconOverAppleTouchIcon()
    {
        const string html = """
            <link rel="apple-touch-icon" sizes="180x180" href="/icons/apple-touch-icon.png">
            <link rel="icon" type="image/png" sizes="16x16" href="/icons/favicon-16x16.png">
            <link rel="icon" type="image/png" sizes="32x32" href="/icons/favicon-32x32.png">
            """;

        var result = WebsiteIconParser.FindIconUri(html, new Uri("https://joonaa.dev/"));

        Assert.AreEqual(new Uri("https://joonaa.dev/icons/favicon-32x32.png"), result);
    }

    [TestMethod]
    public void FindIconUri_RejectsNonHttpIcon()
    {
        const string html = "<link rel='icon' href='file:///c:/windows/win.ini'>";

        var result = WebsiteIconParser.FindIconUri(html, new Uri("https://example.com/"));

        Assert.IsNull(result);
    }

    [TestMethod]
    public void FindIconUris_ReturnsAllCandidatesInPreferenceOrder()
    {
        const string html = """
            <link rel="apple-touch-icon" sizes="180x180" href="/apple.png">
            <link rel="icon" sizes="16x16" href="/favicon-16.png">
            <link rel="icon" sizes="32x32" href="/favicon-32.png">
            <link rel="shortcut icon" sizes="32x32" href="/favicon-32.png">
            """;

        var result = WebsiteIconParser.FindIconUris(html, new Uri("https://example.com/"));

        CollectionAssert.AreEqual(
            new[]
            {
                new Uri("https://example.com/favicon-32.png"),
                new Uri("https://example.com/favicon-16.png"),
                new Uri("https://example.com/apple.png")
            },
            result.ToArray());
    }
}
