using System.Text.Json;
using FluxReader.Models;

namespace FluxReader.Services;

public sealed class SettingsService
{
    public const int DefaultRefreshConcurrencyLimit = 8;
    public const int DefaultRequestTimeoutSeconds = 30;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public SettingsService(string path)
    {
        _path = path;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                   ?? new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}

public sealed record AppSettings(
    AppTheme Theme = AppTheme.System,
    double FeedPaneWidth = 248,
    double ArticleListPaneWidth = 420,
    AppLanguage? Language = null,
    int RefreshIntervalMinutes = 15,
    int RefreshConcurrencyLimit = SettingsService.DefaultRefreshConcurrencyLimit,
    int RequestTimeoutSeconds = SettingsService.DefaultRequestTimeoutSeconds,
    bool LoadExternalArticleStylesheets = false,
    ProxyMode ProxyMode = ProxyMode.System,
    string CustomProxyAddress = "");
