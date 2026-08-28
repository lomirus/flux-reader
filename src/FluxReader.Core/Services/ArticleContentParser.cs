using System.Net;
using System.Text.RegularExpressions;
using FluxReader.Core.Models;

namespace FluxReader.Core.Services;

public static partial class ArticleContentParser
{
    public static string Normalize(
        string? value,
        Uri? baseUri,
        int maximumLength = 200_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = ScriptAndStyleRegex().Replace(value, string.Empty);
        text = HtmlImageRegex().Replace(text, match => NormalizeHtmlImage(match, baseUri));
        text = HtmlTextConverter.ToPlainText(text, int.MaxValue);
        text = MarkdownImageRegex().Replace(text, match => NormalizeMarkdownImage(match, baseUri));

        return text.Length <= maximumLength
            ? text
            : string.Concat(text.AsSpan(0, maximumLength), "…");
    }

    public static IReadOnlyList<ArticleContentBlock> Parse(string? value, Uri? baseUri = null)
    {
        var text = Normalize(value, baseUri, int.MaxValue);
        if (text.Length == 0)
        {
            return [];
        }

        var blocks = new List<ArticleContentBlock>();
        var textStart = 0;
        foreach (Match match in MarkdownImageRegex().Matches(text))
        {
            if (!TryResolveImageUri(match.Groups["target"].Value, baseUri, out var imageUri))
            {
                continue;
            }

            AddTextBlock(blocks, text[textStart..match.Index]);
            blocks.Add(new ArticleContentBlock(
                ArticleContentBlockKind.Image,
                WebUtility.HtmlDecode(match.Groups["alt"].Value).Trim(),
                imageUri));
            textStart = match.Index + match.Length;
        }

        AddTextBlock(blocks, text[textStart..]);
        return blocks;
    }

    public static string ToPlainText(string? value, Uri? baseUri = null)
    {
        var parts = Parse(value, baseUri)
            .Select(block => block.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text));
        return string.Join("\n\n", parts);
    }

    public static string CreatePreviewText(
        string? summary,
        string? content,
        Uri? baseUri,
        int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        var source = string.IsNullOrWhiteSpace(summary) ? content : summary;
        var preview = ToPlainText(source, baseUri);
        return preview.Length <= maximumLength
            ? preview
            : string.Concat(preview.AsSpan(0, maximumLength), "…");
    }

    private static string NormalizeHtmlImage(Match match, Uri? baseUri)
    {
        var attributes = match.Groups["attributes"].Value;
        var source = GetAttributeValue(attributes, "src") ??
                     GetAttributeValue(attributes, "data-src");
        if (!TryResolveImageUri(source, baseUri, out var imageUri))
        {
            return string.Empty;
        }

        var alternativeText = HtmlTextConverter.ToPlainText(
            GetAttributeValue(attributes, "alt"),
            500);
        return $"\n\n![{EscapeAlternativeText(alternativeText)}]({GetMarkdownUri(imageUri)})\n\n";
    }

    private static string NormalizeMarkdownImage(Match match, Uri? baseUri)
    {
        if (!TryResolveImageUri(match.Groups["target"].Value, baseUri, out var imageUri))
        {
            return match.Value;
        }

        var alternativeText = WebUtility.HtmlDecode(match.Groups["alt"].Value).Trim();
        return $"![{EscapeAlternativeText(alternativeText)}]({GetMarkdownUri(imageUri)})";
    }

    private static bool TryResolveImageUri(string? value, Uri? baseUri, out Uri imageUri)
    {
        imageUri = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = WebUtility.HtmlDecode(value).Trim();
        if (candidate.Length >= 2 && candidate[0] == '<' && candidate[^1] == '>')
        {
            candidate = candidate[1..^1].Trim();
        }
        else
        {
            var titleMatch = MarkdownTitleRegex().Match(candidate);
            if (titleMatch.Success)
            {
                candidate = titleMatch.Groups["target"].Value.Trim();
            }
        }

        if (!Uri.TryCreate(baseUri, candidate, out var resolvedUri) ||
            !resolvedUri.IsAbsoluteUri ||
            (resolvedUri.Scheme != Uri.UriSchemeHttps && resolvedUri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        imageUri = resolvedUri;
        return true;
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

    private static string EscapeAlternativeText(string value) =>
        value.Replace('[', '(').Replace(']', ')').ReplaceLineEndings(" ");

    private static string GetMarkdownUri(Uri uri) =>
        uri.AbsoluteUri.Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);

    private static void AddTextBlock(List<ArticleContentBlock> blocks, string value)
    {
        var text = value.Trim();
        if (text.Length > 0)
        {
            blocks.Add(new ArticleContentBlock(ArticleContentBlockKind.Text, text));
        }
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptAndStyleRegex();

    [GeneratedRegex(@"<img\b(?<attributes>[^>]*)/?\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlImageRegex();

    [GeneratedRegex(@"(?<name>[A-Za-z_:][\w:.-]*)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s""'=<>`]+))", RegexOptions.Singleline)]
    private static partial Regex HtmlAttributeRegex();

    [GeneratedRegex(@"!\[(?<alt>[^\]\r\n]*)\]\(\s*(?<target><[^>\r\n]+>|[^)\r\n]+?)\s*\)")]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"^(?<target>.+?)\s+(?:""[^""\r\n]*""|'[^'\r\n]*')$")]
    private static partial Regex MarkdownTitleRegex();
}
