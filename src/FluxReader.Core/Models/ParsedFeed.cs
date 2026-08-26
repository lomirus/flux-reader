namespace FluxReader.Core.Models;

public sealed record ParsedFeed(
    string Title,
    Uri? SiteUri,
    string Description,
    IReadOnlyList<ParsedArticle> Articles);
