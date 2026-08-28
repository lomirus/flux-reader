using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FluxReader.Services;

internal sealed class FeedIconCache
{
    private const int MaximumIconBytes = 3 * 1024 * 1024;
    private static readonly string[] SupportedExtensions = [".svg", ".png", ".jpg"];
    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;

    public FeedIconCache(HttpClient httpClient, string cacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        _httpClient = httpClient;
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    public async Task<Uri?> GetAsync(
        Uri? sourceUri,
        Uri? referrerUri,
        CancellationToken cancellationToken)
    {
        if (sourceUri is null)
        {
            return null;
        }

        if (sourceUri.IsFile)
        {
            return TryGetExistingFileUri(sourceUri);
        }

        if (sourceUri.Scheme != Uri.UriSchemeHttps && sourceUri.Scheme != Uri.UriSchemeHttp)
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
                return CreateFileUri(existingPath);
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        request.Headers.Accept.ParseAdd("image/*, application/svg+xml");
        request.Headers.Referrer = CreateOriginUri(referrerUri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaximumIconBytes)
        {
            throw new InvalidDataException("The feed icon exceeds the supported cache size.");
        }

        var bytes = await ReadLimitedAsync(response.Content, cancellationToken);
        var format = DetectFormat(bytes);
        if (format is null)
        {
            return null;
        }

        string extension;
        switch (format)
        {
            case IconFormat.Svg:
                bytes = NormalizeSvg(bytes);
                extension = ".svg";
                break;
            case IconFormat.Png:
                extension = ".png";
                break;
            case IconFormat.Jpeg:
                extension = ".jpg";
                break;
            case IconFormat.Ico:
                bytes = await ConvertIcoToPngAsync(bytes);
                extension = ".png";
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        Directory.CreateDirectory(_cacheDirectory);
        var fileName = cacheKey + extension;
        var cachePath = Path.Combine(_cacheDirectory, fileName);
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

        return CreateFileUri(cachePath);
    }

    private Uri? TryGetExistingFileUri(Uri sourceUri)
    {
        string path;
        try
        {
            path = Path.GetFullPath(sourceUri.LocalPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!string.Equals(Path.GetDirectoryName(path), _cacheDirectory, StringComparison.OrdinalIgnoreCase) ||
            !SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(path) ? CreateFileUri(path) : null;
    }

    private static Uri CreateFileUri(string path) => new(path, UriKind.Absolute);

    private static Uri? CreateOriginUri(Uri? value) =>
        value is not null &&
        value.IsAbsoluteUri &&
        (value.Scheme == Uri.UriSchemeHttps || value.Scheme == Uri.UriSchemeHttp)
            ? new Uri(value.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute)
            : null;

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
                throw new InvalidDataException("The feed icon exceeds the supported cache size.");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static IconFormat? DetectFormat(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return IconFormat.Png;
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return IconFormat.Jpeg;
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0x01, 0x00 }))
        {
            return IconFormat.Ico;
        }

        return LooksLikeSvg(bytes) ? IconFormat.Svg : null;
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
            throw new InvalidDataException("The feed icon is not a valid SVG document.");
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

    private static async Task<byte[]> ConvertIcoToPngAsync(byte[] bytes)
    {
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        var frame = await decoder.GetFrameAsync(0);
        var largestArea = (ulong)frame.PixelWidth * frame.PixelHeight;
        for (uint index = 1; index < decoder.FrameCount; index++)
        {
            var candidate = await decoder.GetFrameAsync(index);
            var candidateArea = (ulong)candidate.PixelWidth * candidate.PixelHeight;
            if (candidateArea > largestArea)
            {
                frame = candidate;
                largestArea = candidateArea;
            }
        }

        using var bitmap = await frame.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        output.Seek(0);
        var outputLength = checked((uint)output.Size);
        var converted = new byte[outputLength];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync(outputLength);
        reader.ReadBytes(converted);
        return converted;
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

    private enum IconFormat
    {
        Svg,
        Png,
        Jpeg,
        Ico
    }
}
