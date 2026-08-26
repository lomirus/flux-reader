using System.Globalization;
using FluxReader.Models;
using Microsoft.Windows.ApplicationModel.Resources;

namespace FluxReader.Services;

public sealed class LocalizationService
{
    private readonly ResourceContext _resourceContext;
    private readonly ResourceMap _resources;

    public LocalizationService()
    {
        var resourceManager = new ResourceManager();
        _resourceContext = resourceManager.CreateResourceContext();
        _resources = resourceManager.MainResourceMap.GetSubtree("Resources");
        DetectedSystemLanguage = DetectSystemLanguage(CultureInfo.InstalledUICulture.Name);
        SetLanguage(DetectedSystemLanguage);
    }

    public AppLanguage CurrentLanguage { get; private set; }

    public AppLanguage DetectedSystemLanguage { get; }

    public CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    public string LanguageTag => GetLanguageTag(CurrentLanguage);

    public void SetLanguage(AppLanguage language)
    {
        if (!Enum.IsDefined(language))
        {
            language = AppLanguage.English;
        }

        CurrentLanguage = language;
        var languageTag = GetLanguageTag(language);
        CurrentCulture = CultureInfo.GetCultureInfo(languageTag);
        _resourceContext.QualifierValues["Language"] = languageTag;
        CultureInfo.CurrentCulture = CurrentCulture;
        CultureInfo.CurrentUICulture = CurrentCulture;
        CultureInfo.DefaultThreadCurrentCulture = CurrentCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CurrentCulture;
    }

    public AppLanguage ResolveLanguage(AppLanguage? language) =>
        language is { } value && Enum.IsDefined(value)
            ? value
            : DetectedSystemLanguage;

    public string GetString(string key) =>
        _resources.GetValue(key, _resourceContext).ValueAsString;

    public string Format(string key, params object?[] arguments) =>
        string.Format(CurrentCulture, GetString(key), arguments);

    public string FormatArticleCount(int count) =>
        Format(count == 1 ? "ArticleCountOne" : "ArticleCountOther", count);

    public string FormatRefreshComplete(int newArticleCount) =>
        Format(newArticleCount == 1 ? "RefreshCompleteOne" : "RefreshCompleteOther", newArticleCount);

    public string FormatNewArticleNotification(int newArticleCount) =>
        Format(newArticleCount == 1 ? "NotificationNewArticleOne" : "NotificationNewArticleOther", newArticleCount);

    internal static AppLanguage DetectSystemLanguage(string languageTag)
    {
        var subtags = languageTag.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (subtags.Length == 0)
        {
            return AppLanguage.English;
        }

        var language = subtags[0];
        if (language.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            return subtags.Any(subtag =>
                    subtag.Equals("Hant", StringComparison.OrdinalIgnoreCase) ||
                    subtag.Equals("CHT", StringComparison.OrdinalIgnoreCase) ||
                    subtag.Equals("TW", StringComparison.OrdinalIgnoreCase) ||
                    subtag.Equals("HK", StringComparison.OrdinalIgnoreCase) ||
                    subtag.Equals("MO", StringComparison.OrdinalIgnoreCase))
                ? AppLanguage.TraditionalChinese
                : AppLanguage.SimplifiedChinese;
        }

        if (language.Equals("es", StringComparison.OrdinalIgnoreCase))
        {
            return subtags.Any(subtag => subtag.Equals("ES", StringComparison.OrdinalIgnoreCase))
                ? AppLanguage.SpanishSpain
                : AppLanguage.SpanishLatinAmerica;
        }

        return language.ToLowerInvariant() switch
        {
            "fr" => AppLanguage.French,
            "de" => AppLanguage.German,
            "it" => AppLanguage.Italian,
            "pt" => AppLanguage.PortugueseBrazil,
            "pl" => AppLanguage.Polish,
            "ru" => AppLanguage.Russian,
            "ja" => AppLanguage.Japanese,
            "ko" => AppLanguage.Korean,
            _ => AppLanguage.English
        };
    }

    private static string GetLanguageTag(AppLanguage language) => language switch
    {
        AppLanguage.TraditionalChinese => "zh-TW",
        AppLanguage.English => "en-US",
        AppLanguage.French => "fr-FR",
        AppLanguage.German => "de-DE",
        AppLanguage.Italian => "it-IT",
        AppLanguage.SpanishSpain => "es-ES",
        AppLanguage.SpanishLatinAmerica => "es-419",
        AppLanguage.PortugueseBrazil => "pt-BR",
        AppLanguage.Polish => "pl-PL",
        AppLanguage.Russian => "ru-RU",
        AppLanguage.Japanese => "ja-JP",
        AppLanguage.Korean => "ko-KR",
        _ => "zh-CN"
    };
}
