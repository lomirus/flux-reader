namespace FluxReader.Core.Models;

public sealed record ParsedArticle(
    string ExternalId,
    string Title,
    Uri? Link,
    string Author,
    DateTimeOffset? PublishedAt,
    string Summary,
    string Content);
