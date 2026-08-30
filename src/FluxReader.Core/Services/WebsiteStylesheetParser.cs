using AngleSharp.Html.Parser;

namespace FluxReader.Core.Services;

public sealed record WebsiteStylesheetReference(Uri Uri, string Media);

public static class WebsiteStylesheetParser
{
    public static IReadOnlyList<WebsiteStylesheetReference> FindStylesheets(
        string html,
        Uri pageUri)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUri);

        var document = new HtmlParser().ParseDocument(html);
        var baseUri = ResolveBaseUri(document.Head?.QuerySelector("base[href]")?.GetAttribute("href"), pageUri);
        var stylesheets = new List<WebsiteStylesheetReference>();

        foreach (var link in document.QuerySelectorAll("link[rel][href]"))
        {
            var relationTokens = link.GetAttribute("rel")
                ?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                ?? [];
            if (!relationTokens.Any(token => token.Equals("stylesheet", StringComparison.OrdinalIgnoreCase)) ||
                relationTokens.Any(token => token.Equals("alternate", StringComparison.OrdinalIgnoreCase)) ||
                link.HasAttribute("disabled"))
            {
                continue;
            }

            var type = link.GetAttribute("type");
            if (!string.IsNullOrWhiteSpace(type) &&
                !type.Equals("text/css", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var href = link.GetAttribute("href")?.Trim();
            if (!Uri.TryCreate(baseUri, href, out var stylesheetUri) ||
                !IsSupportedUri(stylesheetUri))
            {
                continue;
            }

            var stylesheet = new WebsiteStylesheetReference(
                stylesheetUri,
                link.GetAttribute("media")?.Trim() ?? string.Empty);
            if (!stylesheets.Contains(stylesheet))
            {
                stylesheets.Add(stylesheet);
            }
        }

        return stylesheets;
    }

    private static Uri ResolveBaseUri(string? value, Uri pageUri) =>
        Uri.TryCreate(pageUri, value?.Trim(), out var baseUri) && IsSupportedUri(baseUri)
            ? baseUri
            : pageUri;

    private static bool IsSupportedUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
}
