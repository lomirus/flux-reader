using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluxReader.Core.Services;

namespace FluxReader.Services;

public sealed class ArticleStylesheetService : IDisposable
{
    private const int MaximumPageHtmlCharacters = 1_000_000;
    private const int MaximumStylesheetsPerPage = 32;
    private readonly Dictionary<Uri, IReadOnlyList<WebsiteStylesheetReference>> _cache = [];
    private readonly Lock _cacheLock = new();
    private readonly HttpClient _httpClient;
    private readonly RequestTimeoutHandler _timeoutHandler;

    public ArticleStylesheetService(IWebProxy proxy)
    {
        _timeoutHandler = new RequestTimeoutHandler(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            Proxy = proxy,
            UseProxy = true
        }, SettingsService.DefaultRequestTimeoutSeconds);
        _httpClient = new HttpClient(_timeoutHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var applicationVersion = typeof(ArticleStylesheetService).Assembly.GetName().Version?.ToString(3)
                                 ?? "0.0.0";
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"FluxReader/{applicationVersion} (+Windows 11 RSS Reader)");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/html"));
    }

    public int RequestTimeoutSeconds
    {
        get => _timeoutHandler.TimeoutSeconds;
        set => _timeoutHandler.TimeoutSeconds = value;
    }

    public async Task<IReadOnlyList<WebsiteStylesheetReference>> GetStylesheetsAsync(
        Uri pageUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageUri);
        if (!IsSupportedUri(pageUri))
        {
            return [];
        }

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(pageUri, out var cachedStylesheets))
            {
                return cachedStylesheets;
            }
        }

        try
        {
            using var response = await _httpClient.GetAsync(
                pageUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.Content.Headers.ContentLength > MaximumPageHtmlCharacters * 4)
            {
                DiagnosticLog.Warning(
                    "article.stylesheets_discovery_skipped",
                    new
                    {
                        pageHost = pageUri.Host,
                        statusCode = (int)response.StatusCode,
                        reason = "response_too_large",
                        response.Content.Headers.ContentLength
                    });
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var html = await ReadLimitedAsync(reader, cancellationToken);
            var finalPageUri = response.RequestMessage?.RequestUri ?? pageUri;
            var stylesheets = WebsiteStylesheetParser.FindStylesheets(html, finalPageUri)
                .Take(MaximumStylesheetsPerPage)
                .ToArray();

            lock (_cacheLock)
            {
                _cache[pageUri] = stylesheets;
                _cache[finalPageUri] = stylesheets;
            }

            DiagnosticLog.Information(
                "article.stylesheets_discovered",
                new
                {
                    pageHost = finalPageUri.Host,
                    statusCode = (int)response.StatusCode,
                    stylesheetCount = stylesheets.Length
                });
            return stylesheets;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception exception) when (exception is
                                           HttpRequestException or
                                           IOException or
                                           InvalidOperationException)
        {
            DiagnosticLog.Warning(
                "article.stylesheets_discovery_failed",
                new
                {
                    pageHost = pageUri.Host,
                    exceptionType = exception.GetType().FullName,
                    exception.Message
                });
            return [];
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static async Task<string> ReadLimitedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var html = new StringBuilder();
        var buffer = new char[8_192];
        while (html.Length < MaximumPageHtmlCharacters)
        {
            var remaining = Math.Min(buffer.Length, MaximumPageHtmlCharacters - html.Length);
            var read = await reader.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken);
            if (read == 0)
            {
                break;
            }

            html.Append(buffer, 0, read);
        }

        return html.ToString();
    }

    private static bool IsSupportedUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
}
