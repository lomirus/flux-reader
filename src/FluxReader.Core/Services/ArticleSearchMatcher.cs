namespace FluxReader.Core.Services;

public static class ArticleSearchMatcher
{
    public const int NoMatchRank = -1;
    public const int TitleMatchRank = 0;
    public const int BodyMatchRank = 1;

    public static int GetMatchRank(
        string? title,
        string? summary,
        string? content,
        string? searchQuery)
    {
        var normalizedSearchQuery = searchQuery?.Trim();
        if (string.IsNullOrEmpty(normalizedSearchQuery))
        {
            return TitleMatchRank;
        }

        if (Contains(title, normalizedSearchQuery))
        {
            return TitleMatchRank;
        }

        return Contains(summary, normalizedSearchQuery) ||
               Contains(content, normalizedSearchQuery)
            ? BodyMatchRank
            : NoMatchRank;
    }

    private static bool Contains(string? value, string searchQuery) =>
        value?.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) == true;
}
