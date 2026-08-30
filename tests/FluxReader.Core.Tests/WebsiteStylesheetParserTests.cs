using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class WebsiteStylesheetParserTests
{
    [TestMethod]
    public void FindStylesheets_ResolvesRelativeLinksAndPreservesMedia()
    {
        const string html = """
            <html><head>
              <link rel="stylesheet" href="/assets/site.css">
              <link rel="stylesheet" href="print.css" media="print">
            </head></html>
            """;

        var result = WebsiteStylesheetParser.FindStylesheets(
            html,
            new Uri("https://example.com/articles/entry"));

        CollectionAssert.AreEqual(
            new[]
            {
                new WebsiteStylesheetReference(new Uri("https://example.com/assets/site.css"), string.Empty),
                new WebsiteStylesheetReference(new Uri("https://example.com/articles/print.css"), "print")
            },
            result.ToArray());
    }

    [TestMethod]
    public void FindStylesheets_UsesDocumentBaseAndRejectsInactiveOrUnsafeLinks()
    {
        const string html = """
            <html><head>
              <base href="https://cdn.example.com/v2/">
              <link rel="stylesheet" href="site.css">
              <link rel="alternate stylesheet" href="alternate.css">
              <link rel="stylesheet" href="disabled.css" disabled>
              <link rel="stylesheet" href="javascript:alert(1)">
              <link rel="stylesheet" href="styles.less" type="text/less">
            </head></html>
            """;

        var result = WebsiteStylesheetParser.FindStylesheets(
            html,
            new Uri("https://example.com/articles/entry"));

        CollectionAssert.AreEqual(
            new[]
            {
                new WebsiteStylesheetReference(new Uri("https://cdn.example.com/v2/site.css"), string.Empty)
            },
            result.ToArray());
    }

    [TestMethod]
    public void FindStylesheets_RemovesExactDuplicates()
    {
        const string html = """
            <link rel="stylesheet" href="/site.css">
            <link href="/site.css" rel="stylesheet">
            """;

        var result = WebsiteStylesheetParser.FindStylesheets(
            html,
            new Uri("https://example.com/"));

        Assert.AreEqual(1, result.Count);
    }
}
