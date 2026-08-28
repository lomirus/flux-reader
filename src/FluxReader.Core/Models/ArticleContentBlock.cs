namespace FluxReader.Core.Models;

public enum ArticleContentBlockKind
{
    Text,
    Image
}

public sealed record ArticleContentBlock(
    ArticleContentBlockKind Kind,
    string Text,
    Uri? ImageUri = null);
