using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace FluxReader.Core.Services;

public static partial class ArticleContentParser
{
    private const string BlockedElementSelector =
        "script, style, iframe, frame, frameset, object, embed, applet, form, input, " +
        "button, textarea, select, option, meta, link, base, title, noscript, template";

    private static readonly HashSet<string> AllowedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "address", "article", "aside", "audio", "b", "bdi", "bdo",
        "blockquote", "br", "caption", "cite", "code", "col", "colgroup", "data",
        "dd", "del", "details", "dfn", "div", "dl", "dt", "em", "figcaption",
        "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header",
        "hgroup", "hr", "i", "img", "ins", "kbd", "li", "main", "mark", "ol",
        "p", "picture", "pre", "q", "rp", "rt", "ruby", "s", "samp", "section",
        "small", "source", "span", "strong", "sub", "summary", "sup", "table",
        "tbody", "td", "tfoot", "th", "thead", "time", "tr", "u", "ul", "var",
        "video", "wbr"
    };

    private static readonly HashSet<string> GlobalAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "dir", "id", "lang", "role", "title"
    };

    public static string PrepareHtml(
        string? value,
        Uri? baseUri,
        int maximumLength = 200_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var source = value.Length <= maximumLength
            ? value
            : string.Concat(value.AsSpan(0, maximumLength), "…");
        source = source.Trim();
        if (!ContainsHtmlMarkup(source))
        {
            return source;
        }

        var document = new HtmlParser().ParseDocument(source);
        var body = document.Body;
        if (body is null)
        {
            return string.Empty;
        }

        foreach (var element in body.QuerySelectorAll(BlockedElementSelector).ToArray())
        {
            element.Remove();
        }

        foreach (var element in body.QuerySelectorAll("*").ToArray())
        {
            if (!AllowedElements.Contains(element.LocalName))
            {
                Unwrap(element);
                continue;
            }

            PromoteLazyImageSource(element, baseUri);
            SanitizeAttributes(element, baseUri);
            ConfigureExternalLink(element);
        }

        return body.InnerHtml.Trim();
    }

    public static bool ContainsHtmlMarkup(string? value) =>
        !string.IsNullOrEmpty(value) && HtmlMarkupRegex().IsMatch(value);

    public static string ToPlainText(string? value, Uri? baseUri = null)
        => ToPlainText(value, baseUri, includeImageAlternativeText: true);

    private static string ToPlainText(
        string? value,
        Uri? baseUri,
        bool includeImageAlternativeText)
    {
        var html = PrepareHtml(value, baseUri, int.MaxValue);
        html = includeImageAlternativeText
            ? HtmlImageRegex().Replace(html, match =>
            {
                var alt = GetAttributeValue(match.Groups["attributes"].Value, "alt");
                return string.IsNullOrWhiteSpace(alt) ? string.Empty : $"\n{alt}\n";
            })
            : HtmlImageRegex().Replace(html, string.Empty);
        return HtmlTextConverter.ToPlainText(html, int.MaxValue);
    }

    public static string CreatePreviewText(
        string? summary,
        string? content,
        Uri? baseUri,
        int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        var source = string.IsNullOrWhiteSpace(summary) ? content : summary;
        var text = ToPlainText(source, baseUri, includeImageAlternativeText: false);
        var preview = string.Join(
            ' ',
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return LimitPreviewLength(preview, maximumLength);
    }

    private static string LimitPreviewLength(string preview, int maximumLength)
    {
        return preview.Length <= maximumLength
            ? preview
            : string.Concat(preview.AsSpan(0, maximumLength), "…");
    }

    private static void PromoteLazyImageSource(IElement element, Uri? baseUri)
    {
        if (!element.LocalName.Equals("img", StringComparison.OrdinalIgnoreCase) ||
            element.HasAttribute("src"))
        {
            return;
        }

        var lazySource = element.GetAttribute("data-src");
        var normalizedSource = NormalizeResourceUri(lazySource, baseUri, allowDataImage: true);
        if (normalizedSource is not null)
        {
            element.SetAttribute("src", normalizedSource);
        }
    }

    private static void SanitizeAttributes(IElement element, Uri? baseUri)
    {
        foreach (var attribute in element.Attributes.ToArray())
        {
            var name = attribute.Name;
            if (!IsAttributeAllowed(element.LocalName, name))
            {
                element.RemoveAttribute(name);
                continue;
            }

            if (name.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("cite", StringComparison.OrdinalIgnoreCase))
            {
                var normalizedUri = NormalizeLinkUri(attribute.Value, baseUri);
                if (normalizedUri is null)
                {
                    element.RemoveAttribute(name);
                }
                else
                {
                    element.SetAttribute(name, normalizedUri);
                }

                continue;
            }

            if (name.Equals("src", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("poster", StringComparison.OrdinalIgnoreCase))
            {
                var normalizedUri = NormalizeResourceUri(
                    attribute.Value,
                    baseUri,
                    allowDataImage: element.LocalName is "img" or "video");
                if (normalizedUri is null)
                {
                    element.RemoveAttribute(name);
                }
                else
                {
                    element.SetAttribute(name, normalizedUri);
                }
            }
        }
    }

    private static bool IsAttributeAllowed(string elementName, string attributeName)
    {
        if (GlobalAttributes.Contains(attributeName) ||
            attributeName.StartsWith("aria-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return elementName.ToLowerInvariant() switch
        {
            "a" => attributeName.Equals("href", StringComparison.OrdinalIgnoreCase),
            "audio" => IsOneOf(attributeName, "controls", "loop", "muted", "preload", "src"),
            "blockquote" or "q" or "del" or "ins" =>
                IsOneOf(attributeName, "cite", "datetime"),
            "col" or "colgroup" => attributeName.Equals("span", StringComparison.OrdinalIgnoreCase),
            "data" => attributeName.Equals("value", StringComparison.OrdinalIgnoreCase),
            "details" => attributeName.Equals("open", StringComparison.OrdinalIgnoreCase),
            "img" => IsOneOf(attributeName, "alt", "height", "loading", "src", "width"),
            "li" => attributeName.Equals("value", StringComparison.OrdinalIgnoreCase),
            "ol" => IsOneOf(attributeName, "reversed", "start", "type"),
            "source" => IsOneOf(attributeName, "media", "src", "type"),
            "td" or "th" => IsOneOf(attributeName, "colspan", "rowspan", "scope"),
            "time" => attributeName.Equals("datetime", StringComparison.OrdinalIgnoreCase),
            "video" => IsOneOf(
                attributeName,
                "controls",
                "height",
                "loop",
                "muted",
                "playsinline",
                "poster",
                "preload",
                "src",
                "width"),
            _ => false
        };
    }

    private static void ConfigureExternalLink(IElement element)
    {
        if (!element.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase) ||
            element.GetAttribute("href") is not { } href ||
            href.StartsWith('#'))
        {
            return;
        }

        element.SetAttribute("target", "_blank");
        element.SetAttribute("rel", "noopener noreferrer");
    }

    private static bool IsOneOf(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Equals(candidate, StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeLinkUri(string? value, Uri? baseUri)
    {
        var candidate = WebUtility.HtmlDecode(value ?? string.Empty).Trim();
        if (candidate.StartsWith('#'))
        {
            return candidate;
        }

        return TryResolveUri(candidate, baseUri, out var uri) &&
               (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri
            : null;
    }

    private static string? NormalizeResourceUri(
        string? value,
        Uri? baseUri,
        bool allowDataImage)
    {
        var candidate = WebUtility.HtmlDecode(value ?? string.Empty).Trim();
        if (allowDataImage && SafeDataImageRegex().IsMatch(candidate))
        {
            return candidate;
        }

        return TryResolveUri(candidate, baseUri, out var uri) &&
               (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri
            : null;
    }

    private static bool TryResolveUri(string value, Uri? baseUri, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var success = baseUri is null
            ? Uri.TryCreate(value, UriKind.Absolute, out var resolvedUri)
            : Uri.TryCreate(baseUri, value, out resolvedUri);
        if (!success || resolvedUri is null)
        {
            return false;
        }

        uri = resolvedUri;
        return true;
    }

    private static void Unwrap(IElement element)
    {
        var parent = element.Parent;
        if (parent is null)
        {
            return;
        }

        while (element.FirstChild is { } child)
        {
            parent.InsertBefore(child, element);
        }

        element.Remove();
    }

    private static string? GetAttributeValue(string attributes, string name)
    {
        foreach (Match match in HtmlAttributeRegex().Matches(attributes))
        {
            if (!match.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var groupName in new[] { "double", "single", "bare" })
            {
                var group = match.Groups[groupName];
                if (group.Success)
                {
                    return WebUtility.HtmlDecode(group.Value).Trim();
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"<\s*/?\s*[A-Za-z][^>]*>", RegexOptions.Singleline)]
    private static partial Regex HtmlMarkupRegex();

    [GeneratedRegex(@"<img\b(?<attributes>[^>]*)/?\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlImageRegex();

    [GeneratedRegex(@"(?<name>[A-Za-z_:][\w:.-]*)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s""'=<>`]+))", RegexOptions.Singleline)]
    private static partial Regex HtmlAttributeRegex();

    [GeneratedRegex(@"^data:image/(?:avif|gif|jpeg|png|webp);base64,[A-Za-z0-9+/=\r\n]+$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeDataImageRegex();
}
