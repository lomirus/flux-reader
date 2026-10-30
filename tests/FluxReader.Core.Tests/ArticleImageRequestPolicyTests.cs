using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class ArticleImageRequestPolicyTests
{
    [TestMethod]
    [DataRow("https://example.com/details.php?id=123#section", "https://images.example.com/photo.webp", "https://example.com/")]
    [DataRow("https://user:password@example.com:8443/private?token=secret", "https://cdn.example.net/photo.webp", "https://example.com:8443/")]
    [DataRow("http://example.com/article", "http://images.example.com/photo.webp", "http://example.com/")]
    [DataRow("http://example.com/article", "https://images.example.com/photo.webp", "http://example.com/")]
    public void GetReferer_SendsOnlyArticleOrigin(string articleUrl, string imageUrl, string expected)
    {
        var referer = ArticleImageRequestPolicy.GetReferer(new Uri(articleUrl), new Uri(imageUrl));

        Assert.AreEqual(expected, referer);
    }

    [TestMethod]
    [DataRow("https://example.com/article", "http://images.example.com/photo.webp")]
    [DataRow(null, "https://images.example.com/photo.webp")]
    [DataRow("about:blank", "https://images.example.com/photo.webp")]
    [DataRow("/relative/article", "https://images.example.com/photo.webp")]
    [DataRow("https://example.com/article", null)]
    [DataRow("https://example.com/article", "data:image/png;base64,AAAA")]
    [DataRow("https://example.com/article", "/relative/photo.webp")]
    public void GetReferer_OmitsOriginForDowngradesAndNonHttpUris(string? articleUrl, string? imageUrl)
    {
        var articleUri = articleUrl is null ? null : new Uri(articleUrl, UriKind.RelativeOrAbsolute);
        var imageUri = imageUrl is null ? null : new Uri(imageUrl, UriKind.RelativeOrAbsolute);

        var referer = ArticleImageRequestPolicy.GetReferer(articleUri, imageUri);

        Assert.IsNull(referer);
    }
}
