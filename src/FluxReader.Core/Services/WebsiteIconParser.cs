using System.Net;
using System.Text.RegularExpressions;

namespace FluxReader.Core.Services;

public static class WebsiteIconParser
{
    private static readonly Regex LinkPattern = new(
        "<link\\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    private static readonly Regex AttributePattern = new(
        """(?<name>[^\s=/>]+)(?:\s*=\s*(?:"(?<double>[^"]*)"|'(?<single>[^']*)'|(?<bare>[^\s"'=<>`]+)))?""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    public static Uri? FindIconUri(string html, Uri pageUri)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUri);

        var candidates = new List<IconCandidate>();
        var order = 0;
        foreach (Match linkMatch in LinkPattern.Matches(html))
        {
            var attributes = ParseAttributes(linkMatch.Value);
            if (!attributes.TryGetValue("rel", out var relation) ||
                !attributes.TryGetValue("href", out var href) ||
                string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var priority = GetRelationPriority(relation);
            if (priority is null)
            {
                continue;
            }

            href = WebUtility.HtmlDecode(href).Trim();
            if (!Uri.TryCreate(pageUri, href, out var iconUri) ||
                (iconUri.Scheme != Uri.UriSchemeHttps && iconUri.Scheme != Uri.UriSchemeHttp))
            {
                continue;
            }

            attributes.TryGetValue("sizes", out var sizes);
            candidates.Add(new IconCandidate(iconUri, priority.Value, GetSizeScore(sizes), order++));
        }

        return candidates
            .OrderBy(candidate => candidate.RelationPriority)
            .ThenByDescending(candidate => candidate.SizeScore)
            .ThenBy(candidate => candidate.Order)
            .Select(candidate => candidate.Uri)
            .FirstOrDefault();
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributePattern.Matches(tag[5..^1]))
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["double"].Success
                ? match.Groups["double"].Value
                : match.Groups["single"].Success
                    ? match.Groups["single"].Value
                    : match.Groups["bare"].Value;
            attributes.TryAdd(name, value);
        }

        return attributes;
    }

    private static int? GetRelationPriority(string relation)
    {
        var tokens = relation.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(token => token.Equals("icon", StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        if (tokens.Any(token =>
                token.Equals("apple-touch-icon", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("apple-touch-icon-precomposed", StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        return tokens.Any(token => token.Equals("mask-icon", StringComparison.OrdinalIgnoreCase)) ? 2 : null;
    }

    private static int GetSizeScore(string? sizes)
    {
        if (string.IsNullOrWhiteSpace(sizes))
        {
            return 0;
        }

        if (sizes.Trim().Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        var largest = 0;
        foreach (var size in sizes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var dimensions = size.Split('x', 2, StringSplitOptions.TrimEntries);
            if (dimensions.Length == 2 &&
                int.TryParse(dimensions[0], out var width) &&
                int.TryParse(dimensions[1], out var height))
            {
                largest = Math.Max(largest, Math.Min(width, height));
            }
        }

        return largest;
    }

    private sealed record IconCandidate(Uri Uri, int RelationPriority, int SizeScore, int Order);
}
