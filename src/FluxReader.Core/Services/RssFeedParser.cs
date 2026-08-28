using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FluxReader.Core.Models;

namespace FluxReader.Core.Services;

public sealed class RssFeedParser
{
    private const long MaximumDocumentCharacters = 20_000_000;
    private readonly Func<RssParserString, string> _getString;

    public RssFeedParser(Func<RssParserString, string> getString)
    {
        ArgumentNullException.ThrowIfNull(getString);
        _getString = getString;
    }

    public async Task<ParsedFeed> ParseAsync(Stream stream, Uri sourceUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(sourceUri);

        try
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersInDocument = MaximumDocumentCharacters,
                XmlResolver = null
            };

            using var reader = XmlReader.Create(stream, settings, sourceUri.AbsoluteUri);
            var document = await XDocument.LoadAsync(
                reader,
                LoadOptions.SetBaseUri,
                cancellationToken);

            var root = document.Root ?? throw new RssParseException(_getString(RssParserString.EmptyContent));
            return root.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase)
                ? ParseAtom(root, sourceUri)
                : ParseRss(root, sourceUri);
        }
        catch (RssParseException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new RssParseException(_getString(RssParserString.ParseFailed), exception);
        }
    }

    private ParsedFeed ParseRss(XElement root, Uri sourceUri)
    {
        var rootName = root.Name.LocalName;
        var isRdf = rootName.Equals("RDF", StringComparison.OrdinalIgnoreCase);
        var isRss = rootName.Equals("rss", StringComparison.OrdinalIgnoreCase) ||
                    rootName.Equals("channel", StringComparison.OrdinalIgnoreCase);
        if (!isRdf && !isRss)
        {
            throw new RssParseException(_getString(RssParserString.InvalidFormat));
        }

        var channel = rootName.Equals("channel", StringComparison.OrdinalIgnoreCase)
            ? root
            : Child(root, "channel")
              ?? throw new RssParseException(_getString(RssParserString.MissingRssChannel));
        var title = CleanTitle(Value(channel, "title"), sourceUri.Host);
        var siteUri = ParseUri(Value(channel, "link"), sourceUri);
        var iconUri = RssIcon(root, channel, sourceUri);
        var description = HtmlTextConverter.ToPlainText(Value(channel, "description"), 4_000);
        var itemParent = isRdf ? root : channel;
        var itemElements = itemParent.Elements().Where(element =>
            element.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase));

        var articles = itemElements.Select(item => ParseRssItem(item, sourceUri)).ToArray();
        return new ParsedFeed(title, siteUri, iconUri, description, articles);
    }

    private ParsedArticle ParseRssItem(XElement item, Uri sourceUri)
    {
        var link = ParseUri(Value(item, "link"), sourceUri);
        var title = CleanTitle(Value(item, "title"), _getString(RssParserString.UntitledArticle));
        var author = FirstValue(item, "creator", "author");
        var publishedAt = ParseDate(FirstValue(item, "pubDate", "date", "published", "updated"));
        var summaryMarkup = FirstValue(item, "description", "summary");
        var contentMarkup = FirstValue(item, "encoded", "content");
        var summary = HtmlTextConverter.ToPlainText(summaryMarkup, 2_000);
        var content = ArticleContentParser.Normalize(
            string.IsNullOrWhiteSpace(contentMarkup) ? summaryMarkup : contentMarkup,
            link ?? sourceUri);
        var externalId = FirstValue(item, "guid", "id");

        return new ParsedArticle(
            BuildExternalId(externalId, link, title, publishedAt),
            title,
            link,
            HtmlTextConverter.ToPlainText(author, 300),
            publishedAt,
            summary,
            content);
    }

    private ParsedFeed ParseAtom(XElement root, Uri sourceUri)
    {
        var title = CleanTitle(Value(root, "title"), sourceUri.Host);
        var siteUri = AtomLink(root, sourceUri);
        var iconUri = ParseUri(FirstValue(root, "icon", "logo"), sourceUri);
        var description = HtmlTextConverter.ToPlainText(FirstValue(root, "subtitle", "tagline"), 4_000);
        var entries = root.Elements().Where(element =>
            element.Name.LocalName.Equals("entry", StringComparison.OrdinalIgnoreCase));

        var articles = entries.Select(entry => ParseAtomEntry(entry, sourceUri)).ToArray();
        return new ParsedFeed(title, siteUri, iconUri, description, articles);
    }

    private ParsedArticle ParseAtomEntry(XElement entry, Uri sourceUri)
    {
        var link = AtomLink(entry, sourceUri);
        var title = CleanTitle(Value(entry, "title"), _getString(RssParserString.UntitledArticle));
        var authorElement = Child(entry, "author");
        var author = authorElement is null ? string.Empty : FirstValue(authorElement, "name", "email");
        var publishedAt = ParseDate(FirstValue(entry, "published", "updated", "issued", "modified"));
        var summaryMarkup = Value(entry, "summary");
        var contentMarkup = Value(entry, "content");
        var summary = HtmlTextConverter.ToPlainText(summaryMarkup, 2_000);
        var content = ArticleContentParser.Normalize(
            string.IsNullOrWhiteSpace(contentMarkup) ? summaryMarkup : contentMarkup,
            link ?? sourceUri);

        return new ParsedArticle(
            BuildExternalId(Value(entry, "id"), link, title, publishedAt),
            title,
            link,
            HtmlTextConverter.ToPlainText(author, 300),
            publishedAt,
            summary,
            content);
    }

    private static Uri? AtomLink(XElement parent, Uri sourceUri)
    {
        var links = parent.Elements().Where(element =>
            element.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase));
        var link = links.FirstOrDefault(element =>
                       string.IsNullOrWhiteSpace((string?)element.Attribute("rel")) ||
                       string.Equals((string?)element.Attribute("rel"), "alternate", StringComparison.OrdinalIgnoreCase))
                   ?? links.FirstOrDefault();

        return ParseUri((string?)link?.Attribute("href") ?? link?.Value, sourceUri);
    }

    private static Uri? RssIcon(XElement root, XElement channel, Uri sourceUri)
    {
        var image = Child(channel, "image") ??
                    (root.Name.LocalName.Equals("RDF", StringComparison.OrdinalIgnoreCase)
                        ? Child(root, "image")
                        : null);
        var imageUrl = image is null ? null : FirstValue(image, "url");
        if (string.IsNullOrWhiteSpace(imageUrl) && image is not null)
        {
            imageUrl = (string?)image.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals("resource", StringComparison.OrdinalIgnoreCase) ||
                attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase));
        }

        return ParseUri(imageUrl, sourceUri);
    }

    private static string BuildExternalId(string? id, Uri? link, string title, DateTimeOffset? publishedAt)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id.Trim();
        }

        if (link is not null)
        {
            return link.AbsoluteUri;
        }

        var fallback = $"{title}\n{publishedAt:O}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fallback)));
    }

    private static Uri? ParseUri(string? value, Uri sourceUri)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(sourceUri, value.Trim(), out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        return uri;
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out var result)
            ? result
            : null;
    }

    private static XElement? Child(XContainer parent, string localName) =>
        parent.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static string Value(XContainer parent, string localName) =>
        Child(parent, localName)?.Value?.Trim() ?? string.Empty;

    private static string FirstValue(XContainer parent, params string[] localNames)
    {
        foreach (var localName in localNames)
        {
            var value = Value(parent, localName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string CleanTitle(string? title, string fallback)
    {
        var result = HtmlTextConverter.ToPlainText(title, 500);
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

}
