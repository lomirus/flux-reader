using System.Xml;
using System.Xml.Linq;
using FluxReader.Core.Models;

namespace FluxReader.Core.Services;

public static class OpmlSubscriptionSerializer
{
    private const int MaximumDocumentCharacters = 5_000_000;
    private const int MaximumGroupDepth = 32;

    public static SubscriptionDocument Parse(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (content.Length > MaximumDocumentCharacters)
        {
            throw new FormatException("The OPML document is too large.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            XmlResolver = null
        };

        XDocument document;
        using (var stringReader = new StringReader(content))
        using (var xmlReader = XmlReader.Create(stringReader, settings))
        {
            document = XDocument.Load(xmlReader, LoadOptions.None);
        }

        var root = document.Root;
        var body = root?.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        if (root is null ||
            !root.Name.LocalName.Equals("opml", StringComparison.OrdinalIgnoreCase) ||
            body is null)
        {
            throw new FormatException("The file is not a valid OPML document.");
        }

        var subscriptions = new List<SubscriptionOutline>();
        var seenFeedUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedOutlineCount = 0;
        foreach (var outline in body.Elements().Where(IsOutline))
        {
            ReadOutline(
                outline,
                [],
                subscriptions,
                seenFeedUris,
                depth: 0,
                ref skippedOutlineCount);
        }

        return new SubscriptionDocument(subscriptions, skippedOutlineCount);
    }

    public static string Serialize(
        IEnumerable<SubscriptionOutline> subscriptions,
        string documentTitle = "FluxReader subscriptions")
    {
        ArgumentNullException.ThrowIfNull(subscriptions);

        var body = new XElement("body");
        var normalizedSubscriptions = subscriptions.ToArray();
        foreach (var subscription in normalizedSubscriptions.Where(subscription =>
                     string.IsNullOrWhiteSpace(subscription.Group)))
        {
            body.Add(CreateFeedOutline(subscription));
        }

        foreach (var group in normalizedSubscriptions
                     .Where(subscription => !string.IsNullOrWhiteSpace(subscription.Group))
                     .GroupBy(subscription => subscription.Group!.Trim(), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var groupOutline = new XElement(
                "outline",
                new XAttribute("text", group.Key),
                new XAttribute("title", group.Key));
            foreach (var subscription in group.OrderBy(
                         subscription => subscription.Title,
                         StringComparer.OrdinalIgnoreCase))
            {
                groupOutline.Add(CreateFeedOutline(subscription));
            }

            body.Add(groupOutline);
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "opml",
                new XAttribute("version", "2.0"),
                new XElement("head", new XElement("title", documentTitle)),
                body));
        return $"{document.Declaration}{Environment.NewLine}{document}";
    }

    private static void ReadOutline(
        XElement outline,
        IReadOnlyList<string> groupPath,
        ICollection<SubscriptionOutline> subscriptions,
        ISet<string> seenFeedUris,
        int depth,
        ref int skippedOutlineCount)
    {
        if (depth > MaximumGroupDepth)
        {
            throw new FormatException("The OPML group hierarchy is too deep.");
        }

        var feedAddress = GetAttribute(outline, "xmlUrl");
        if (feedAddress is not null)
        {
            if (!TryCreateHttpUri(feedAddress, out var feedUri) ||
                !seenFeedUris.Add(feedUri.AbsoluteUri))
            {
                skippedOutlineCount++;
                return;
            }

            var title = GetAttribute(outline, "title") ??
                        GetAttribute(outline, "text") ??
                        feedUri.Host;
            var siteAddress = GetAttribute(outline, "htmlUrl");
            TryCreateHttpUri(siteAddress, out var siteUri);
            subscriptions.Add(new SubscriptionOutline(
                title,
                feedUri,
                siteUri,
                groupPath.Count == 0 ? null : string.Join(" / ", groupPath)));
            return;
        }

        var nextGroupPath = groupPath;
        var groupName = GetAttribute(outline, "title") ?? GetAttribute(outline, "text");
        if (!string.IsNullOrWhiteSpace(groupName))
        {
            nextGroupPath = [.. groupPath, groupName];
        }

        var children = outline.Elements().Where(IsOutline).ToArray();
        if (children.Length == 0 &&
            string.Equals(GetAttribute(outline, "type"), "rss", StringComparison.OrdinalIgnoreCase))
        {
            skippedOutlineCount++;
        }

        foreach (var child in children)
        {
            ReadOutline(
                child,
                nextGroupPath,
                subscriptions,
                seenFeedUris,
                depth + 1,
                ref skippedOutlineCount);
        }
    }

    private static XElement CreateFeedOutline(SubscriptionOutline subscription)
    {
        var title = string.IsNullOrWhiteSpace(subscription.Title)
            ? subscription.FeedUri.Host
            : subscription.Title.Trim();
        var outline = new XElement(
            "outline",
            new XAttribute("type", "rss"),
            new XAttribute("text", title),
            new XAttribute("title", title),
            new XAttribute("xmlUrl", subscription.FeedUri.AbsoluteUri));
        if (subscription.SiteUri is not null)
        {
            outline.Add(new XAttribute("htmlUrl", subscription.SiteUri.AbsoluteUri));
        }

        return outline;
    }

    private static string? GetAttribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(
                localName,
                StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim() is { Length: > 0 } value
                ? value
                : null;

    private static bool IsOutline(XElement element) =>
        element.Name.LocalName.Equals("outline", StringComparison.OrdinalIgnoreCase);

    private static bool TryCreateHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            (candidate.Scheme == Uri.UriSchemeHttps || candidate.Scheme == Uri.UriSchemeHttp))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }
}
