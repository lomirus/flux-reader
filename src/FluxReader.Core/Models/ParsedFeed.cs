namespace FluxReader.Core.Models;

public sealed record ParsedFeed(
    string Title,
    Uri? SiteUri,
    Uri? IconUri,
    string Description,
    IReadOnlyList<ParsedArticle> Articles);
