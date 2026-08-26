using System.Net;
using System.Text.RegularExpressions;

namespace FluxReader.Core.Services;

public static partial class HtmlTextConverter
{
    public static string ToPlainText(string? value, int maximumLength = 200_000)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = ScriptAndStyleRegex().Replace(value, string.Empty);
        text = BlockBreakRegex().Replace(text, "\n");
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
        text = WindowsNewlineRegex().Replace(text, "\n");
        text = HorizontalWhitespaceRegex().Replace(text, " ");
        text = ExcessiveNewlineRegex().Replace(text, "\n\n").Trim();

        return text.Length <= maximumLength ? text : string.Concat(text.AsSpan(0, maximumLength), "…");
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptAndStyleRegex();

    [GeneratedRegex(@"<(br\s*/?|/p|/div|/li|/h[1-6])\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\r\n?")]
    private static partial Regex WindowsNewlineRegex();

    [GeneratedRegex(@"[\t\f\v ]+")]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveNewlineRegex();
}
