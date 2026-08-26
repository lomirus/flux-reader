using FluxReader.Models;
using FluxReader.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace FluxReader;

public sealed partial class SettingsPage : Page
{
    private bool _initialized;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public event EventHandler? BackRequested;

    public event EventHandler? ThemeChanged;

    public event EventHandler? LanguageChanged;

    public AppTheme SelectedTheme => (AppTheme)ThemeSelector.SelectedIndex;

    public AppLanguage SelectedLanguage => (AppLanguage)LanguageSelector.SelectedIndex;

    public void Initialize(AppTheme theme, AppLanguage language)
    {
        ThemeSelector.SelectedIndex = (int)theme;
        LanguageSelector.SelectedIndex = (int)language;
        _initialized = true;
        ApplyLocalization();
    }

    public void ApplyLocalization()
    {
        var localization = App.Current.Localization;
        Language = localization.LanguageTag;
        var back = localization.GetString("Back");
        AutomationProperties.SetName(BackButton, back);
        ToolTipService.SetToolTip(BackButton, back);
        SettingsTitleText.Text = localization.GetString("Settings");
        AppearanceHeaderText.Text = localization.GetString("Appearance");
        ThemeTitleText.Text = localization.GetString("ApplicationTheme");
        ThemeDescriptionText.Text = localization.GetString("ThemeDescription");
        AutomationProperties.SetName(ThemeSelector, localization.GetString("ApplicationTheme"));
        SystemThemeItem.Content = localization.GetString("ThemeSystem");
        LightThemeItem.Content = localization.GetString("ThemeLight");
        DarkThemeItem.Content = localization.GetString("ThemeDark");
        LanguageHeaderText.Text = localization.GetString("Language");
        LanguageTitleText.Text = localization.GetString("ApplicationLanguage");
        LanguageDescriptionText.Text = localization.GetString("LanguageDescription");
        AutomationProperties.SetName(LanguageSelector, localization.GetString("ApplicationLanguage"));
        SimplifiedChineseItem.Content = localization.GetString("LanguageSimplifiedChinese");
        TraditionalChineseItem.Content = localization.GetString("LanguageTraditionalChinese");
        EnglishItem.Content = localization.GetString("LanguageEnglish");
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && ThemeSelector.SelectedIndex >= 0)
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && LanguageSelector.SelectedIndex >= 0)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
