using System.Net;
using System.Net.Http.Headers;
using FluxReader.Core.Models;
using FluxReader.Core.Services;
using FluxReader.Data;
using FluxReader.Models;

namespace FluxReader.Services;

public sealed class RssRefreshService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly LocalizationService _localization;
    private readonly RssFeedParser _parser;
    private readonly RssRepository _repository;

    public RssRefreshService(RssRepository repository, LocalizationService localization)
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
    }

    public async Task<Feed> AddFeedAsync(
        Uri feedUri,
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        EnsureSupportedUri(feedUri);
        var download = await DownloadAsync(feedUri, null, null, cancellationToken);
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
            cancellationToken);

        if (download.NotModified)
        {
            await _repository.TouchFeedAsync(feed.Id, cancellationToken);
            return new FeedRefreshResult(feed, Array.Empty<string>(), true);
        }

        var parsedFeed = download.ParsedFeed
                         ?? throw new InvalidOperationException(_localization.GetString("EmptyFeedResponse"));
        var insertedTitles = await _repository.UpdateFeedAsync(
            feed,
            parsedFeed,
            download.ETag,
            download.LastModifiedAt,
            cancellationToken);

        return new FeedRefreshResult(feed, insertedTitles, false);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<FeedDownload> DownloadAsync(
        Uri uri,
        string? etag,
        DateTimeOffset? lastModifiedAt,
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
            return new FeedDownload(null, etag, lastModifiedAt, true);
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var parsedFeed = await _parser.ParseAsync(stream, response.RequestMessage?.RequestUri ?? uri, cancellationToken);
        return new FeedDownload(
            parsedFeed,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified,
            false);
    }

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
        bool NotModified);
}

public sealed record FeedRefreshResult(
    Feed Feed,
    IReadOnlyList<string> NewArticleTitles,
    bool NotModified);
