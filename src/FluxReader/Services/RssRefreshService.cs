using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluxReader.Core.Models;
using FluxReader.Core.Services;
using FluxReader.Data;
using FluxReader.Models;

namespace FluxReader.Services;

public sealed class RssRefreshService : IDisposable
{
    private const int MaximumWebsiteHtmlCharacters = 1_000_000;
    private readonly HttpClient _httpClient;
    private readonly FeedIconCache _iconCache;
    private readonly LocalizationService _localization;
    private readonly RssFeedParser _parser;
    private readonly RssRepository _repository;

    public RssRefreshService(
        RssRepository repository,
        LocalizationService localization,
        string iconCacheDirectory)
    {
        _repository = repository;
        _localization = localization;
        _parser = new RssFeedParser(key => _localization.GetString(key switch
        {
            RssParserString.EmptyContent => "FeedContentEmpty",
            RssParserString.ParseFailed => "FeedParseFailed",
            RssParserString.InvalidFormat => "InvalidFeedFormat",
            RssParserString.MissingRssChannel => "MissingRssChannel",
            RssParserString.UntitledArticle => "UntitledArticle",
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
        }));
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FluxReader/1.0 (+Windows 11 RSS Reader)");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/atom+xml"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rss+xml"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml", 0.8));
        _iconCache = new FeedIconCache(_httpClient, iconCacheDirectory);
    }

    public async Task<Feed> AddFeedAsync(
        Uri feedUri,
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        EnsureSupportedUri(feedUri);
        var download = await DownloadAsync(feedUri, null, null, null, null, cancellationToken);
        if (download.ParsedFeed is null)
        {
            throw new InvalidOperationException(_localization.GetString("EmptyFeedResponse"));
        }

        return await _repository.AddFeedAsync(
            feedUri,
            download.ParsedFeed,
            download.ETag,
            download.LastModifiedAt,
            groupId,
            cancellationToken);
    }

    public async Task<FeedRefreshResult> RefreshAsync(Feed feed, CancellationToken cancellationToken = default)
    {
        var feedUri = new Uri(feed.Url);
        var download = await DownloadAsync(
            feedUri,
            feed.ETag,
            feed.LastModifiedAt,
            TryCreateIconUri(feed.IconUrl),
            TryCreateHttpUri(feed.SiteUrl),
            cancellationToken);

        if (download.NotModified)
        {
            var iconUrl = download.IconUri?.AbsoluteUri ?? string.Empty;
            await _repository.TouchFeedAsync(feed.Id, iconUrl, cancellationToken);
            return new FeedRefreshResult(
                iconUrl,
                Array.Empty<ParsedArticle>(),
                true);
        }

        var parsedFeed = download.ParsedFeed
                         ?? throw new InvalidOperationException(_localization.GetString("EmptyFeedResponse"));
        var insertedArticles = await _repository.UpdateFeedAsync(
            feed,
            parsedFeed,
            download.ETag,
            download.LastModifiedAt,
            cancellationToken);

        return new FeedRefreshResult(
            parsedFeed.IconUri?.AbsoluteUri ?? string.Empty,
            insertedArticles,
            false);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<FeedDownload> DownloadAsync(
        Uri uri,
        string? etag,
        DateTimeOffset? lastModifiedAt,
        Uri? existingIconUri,
        Uri? existingSiteUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(etag) && EntityTagHeaderValue.TryParse(etag, out var parsedTag))
        {
            request.Headers.IfNoneMatch.Add(parsedTag);
        }

        request.Headers.IfModifiedSince = lastModifiedAt;
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            var notModifiedIconUri = await ResolveFeedIconAsync(
                null,
                existingIconUri,
                existingSiteUri ?? uri,
                cancellationToken);
            return new FeedDownload(null, etag, lastModifiedAt, true, notModifiedIconUri);
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var parsedFeed = await _parser.ParseAsync(stream, response.RequestMessage?.RequestUri ?? uri, cancellationToken);
        var siteUri = parsedFeed.SiteUri ?? response.RequestMessage?.RequestUri ?? uri;
        var iconUri = await ResolveFeedIconAsync(
            parsedFeed.IconUri,
            existingIconUri,
            siteUri,
            cancellationToken);
        parsedFeed = parsedFeed with { IconUri = iconUri };

        return new FeedDownload(
            parsedFeed,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified,
            false,
            iconUri);
    }

    private async Task<Uri?> ResolveFeedIconAsync(
        Uri? parsedIconUri,
        Uri? existingIconUri,
        Uri siteUri,
        CancellationToken cancellationToken)
    {
        foreach (var sourceUri in new[] { parsedIconUri, existingIconUri }.OfType<Uri>().Distinct())
        {
            var cachedUri = await TryCacheIconAsync(sourceUri, siteUri, cancellationToken);
            if (cachedUri is not null)
            {
                return cachedUri;
            }
        }

        return await DiscoverWebsiteIconAsync(siteUri, cancellationToken);
    }

    private async Task<Uri?> DiscoverWebsiteIconAsync(Uri siteUri, CancellationToken cancellationToken)
    {
        var defaultIconUri = CreateDefaultIconUri(siteUri);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, siteUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength > MaximumWebsiteHtmlCharacters * 4)
            {
                return await TryCacheIconAsync(defaultIconUri, siteUri, cancellationToken);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var html = await ReadLimitedAsync(reader, cancellationToken);
            var finalPageUri = response.RequestMessage?.RequestUri ?? siteUri;
            var candidates = WebsiteIconParser.FindIconUris(html, finalPageUri)
                .Append(CreateDefaultIconUri(finalPageUri))
                .Distinct();
            foreach (var candidate in candidates)
            {
                var cachedUri = await TryCacheIconAsync(candidate, finalPageUri, cancellationToken);
                if (cachedUri is not null)
                {
                    return cachedUri;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
        }

        return await TryCacheIconAsync(defaultIconUri, siteUri, cancellationToken);
    }

    private async Task<Uri?> TryCacheIconAsync(
        Uri sourceUri,
        Uri siteUri,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _iconCache.GetAsync(sourceUri, siteUri, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is
                                           HttpRequestException or
                                           IOException or
                                           InvalidDataException or
                                           InvalidOperationException or
                                           UnauthorizedAccessException or
                                           System.Runtime.InteropServices.COMException or
                                           System.Xml.XmlException)
        {
            return null;
        }
    }

    private static async Task<string> ReadLimitedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var html = new StringBuilder();
        var buffer = new char[8_192];
        while (html.Length < MaximumWebsiteHtmlCharacters)
        {
            var remaining = Math.Min(buffer.Length, MaximumWebsiteHtmlCharacters - html.Length);
            var read = await reader.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken);
            if (read == 0)
            {
                break;
            }

            html.Append(buffer, 0, read);
        }

        return html.ToString();
    }

    private static Uri CreateDefaultIconUri(Uri siteUri) =>
        new(siteUri.GetLeftPart(UriPartial.Authority) + "/favicon.ico", UriKind.Absolute);

    private static Uri? TryCreateHttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    private static Uri? TryCreateIconUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps ||
         uri.Scheme == Uri.UriSchemeHttp ||
         uri.IsFile)
            ? uri
            : null;

    private void EnsureSupportedUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException(_localization.GetString("InvalidFeedAddress"), nameof(uri));
        }
    }

    private sealed record FeedDownload(
        ParsedFeed? ParsedFeed,
        string? ETag,
        DateTimeOffset? LastModifiedAt,
        bool NotModified,
        Uri? IconUri);
}

public sealed record FeedRefreshResult(
    string FeedIconUrl,
    IReadOnlyList<ParsedArticle> NewArticles,
    bool NotModified);
