namespace FluxReader.Core.Services;

public static class ArticleImageRequestPolicy
{
    public static string? GetReferer(Uri? articleUri, Uri? imageUri)
    {
        if (!IsHttpUri(articleUri) || !IsHttpUri(imageUri) ||
            (articleUri!.Scheme == Uri.UriSchemeHttps && imageUri!.Scheme == Uri.UriSchemeHttp))
        {
            return null;
        }

        // Only send the origin, never article paths, query tokens, or credentials.
        return articleUri!.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + "/";
    }

    private static bool IsHttpUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
