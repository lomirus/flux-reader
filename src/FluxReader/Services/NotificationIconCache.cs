using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FluxReader.Services;

internal sealed class NotificationIconCache : IDisposable
{
    private const int MaximumIconBytes = 3 * 1024 * 1024;
    private static readonly string[] SupportedExtensions = [".svg", ".png", ".jpg"];
    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public NotificationIconCache(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = cacheDirectory;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FluxReader/0.1");
    }

    public async Task<Uri?> GetAsync(string? value, CancellationToken cancellationToken)
    {
        if (!TryCreateHttpUri(value, out var sourceUri))
        {
            return null;
        }

        var cacheKey = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(sourceUri.AbsoluteUri)));
        foreach (var cachedExtension in SupportedExtensions)
        {
            var existingPath = Path.Combine(_cacheDirectory, cacheKey + cachedExtension);
            if (File.Exists(existingPath))
            {
                return new Uri(existingPath, UriKind.Absolute);
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        request.Headers.Accept.ParseAdd("image/*, application/svg+xml");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaximumIconBytes)
        {
            throw new InvalidDataException("The notification icon exceeds the supported cache size.");
        }

        var bytes = await ReadLimitedAsync(response.Content, cancellationToken);
        var detectedExtension = DetectExtension(
            sourceUri,
            response.Content.Headers.ContentType?.MediaType,
            bytes);
        if (detectedExtension is null)
        {
            return null;
        }

        if (detectedExtension == ".svg")
        {
            bytes = NormalizeSvg(bytes);
        }

        Directory.CreateDirectory(_cacheDirectory);
        var cachePath = Path.Combine(_cacheDirectory, cacheKey + detectedExtension);
        var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            File.Move(temporaryPath, cachePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new Uri(cachePath, UriKind.Absolute);
    }

    public void Dispose() => _httpClient.Dispose();

    private static bool TryCreateHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            (candidate.Scheme == Uri.UriSchemeHttps || candidate.Scheme == Uri.UriSchemeHttp))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaximumIconBytes)
            {
                throw new InvalidDataException("The notification icon exceeds the supported cache size.");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static string? DetectExtension(Uri sourceUri, string? mediaType, byte[] bytes)
    {
        if (mediaType?.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase) == true ||
            LooksLikeSvg(bytes))
        {
            return ".svg";
        }

        if (mediaType?.Equals("image/png", StringComparison.OrdinalIgnoreCase) == true ||
            bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ".png";
        }

        if (mediaType?.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) == true ||
            bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return ".jpg";
        }

        return Path.GetExtension(sourceUri.AbsolutePath).ToLowerInvariant() switch
        {
            ".svg" => ".svg",
            ".png" => ".png",
            ".jpg" or ".jpeg" => ".jpg",
            _ => null
        };
    }

    private static bool LooksLikeSvg(byte[] bytes)
    {
        var prefixLength = Math.Min(bytes.Length, 512);
        var prefix = Encoding.UTF8.GetString(bytes, 0, prefixLength);
        return prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] NormalizeSvg(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root is null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The notification icon is not a valid SVG document.");
        }

        if (root.Attribute("viewBox") is null &&
            TryParseSvgLength(root.Attribute("width")?.Value, out var width) &&
            TryParseSvgLength(root.Attribute("height")?.Value, out var height))
        {
            root.SetAttributeValue(
                "viewBox",
                FormattableString.Invariant($"0 0 {width} {height}"));
        }

        root.SetAttributeValue("width", "100%");
        root.SetAttributeValue("height", "100%");
        root.SetAttributeValue("preserveAspectRatio", "xMidYMid meet");

        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
               {
                   Encoding = new UTF8Encoding(false),
                   Indent = false,
                   OmitXmlDeclaration = document.Declaration is null
               }))
        {
            document.Save(writer);
        }

        return output.ToArray();
    }

    private static bool TryParseSvgLength(string? value, out double length)
    {
        value = value?.Trim();
        if (value?.EndsWith("px", StringComparison.OrdinalIgnoreCase) == true)
        {
            value = value[..^2];
        }

        return double.TryParse(
                   value,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out length) &&
               length > 0;
    }
}
