using System.Xml;
using FluxReader.Core.Models;
using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class OpmlSubscriptionSerializerTests
{
    [TestMethod]
    public void SerializeAndParse_PreservesSubscriptionsAndGroups()
    {
        SubscriptionOutline[] subscriptions =
        [
            new(
                "Example & News",
                new Uri("https://example.com/feed.xml"),
                new Uri("https://example.com/")),
            new(
                "Grouped feed",
                new Uri("https://feeds.example.net/rss"),
                Group: "Technology")
        ];

        var content = OpmlSubscriptionSerializer.Serialize(subscriptions, "Test subscriptions");
        var result = OpmlSubscriptionSerializer.Parse(content);

        Assert.AreEqual(0, result.SkippedOutlineCount);
        Assert.HasCount(2, result.Subscriptions);
        Assert.AreEqual("Example & News", result.Subscriptions[0].Title);
        Assert.AreEqual("https://example.com/", result.Subscriptions[0].SiteUri?.AbsoluteUri);
        Assert.AreEqual("Technology", result.Subscriptions[1].Group);
    }

    [TestMethod]
    public void Parse_FlattensNestedGroupsAndSkipsInvalidOrDuplicateFeeds()
    {
        const string content = """
            <?xml version="1.0"?>
            <opml version="2.0">
              <body>
                <outline text="News">
                  <outline title="International">
                    <outline text="Valid" xmlUrl="https://example.com/feed" />
                    <outline text="Duplicate" xmlUrl="https://EXAMPLE.com/feed" />
                    <outline text="Local file" xmlUrl="file:///feed.xml" />
                    <outline type="rss" text="Missing address" />
                  </outline>
                </outline>
              </body>
            </opml>
            """;

        var result = OpmlSubscriptionSerializer.Parse(content);

        Assert.HasCount(1, result.Subscriptions);
        Assert.AreEqual("News / International", result.Subscriptions[0].Group);
        Assert.AreEqual(3, result.SkippedOutlineCount);
    }

    [TestMethod]
    public void Parse_RejectsDocumentsThatAreNotOpml()
    {
        Assert.ThrowsExactly<FormatException>(() =>
            OpmlSubscriptionSerializer.Parse("<rss><channel /></rss>"));
    }

    [TestMethod]
    public void Parse_RejectsDocumentTypeDeclarations()
    {
        const string content = """
            <!DOCTYPE opml [<!ENTITY test "value">]>
            <opml version="2.0"><body /></opml>
            """;

        Assert.ThrowsExactly<XmlException>(() => OpmlSubscriptionSerializer.Parse(content));
    }
}
