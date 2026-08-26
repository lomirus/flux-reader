using FluxReader.Models;
using FluxReader.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Windows.Globalization.NumberFormatting;

namespace FluxReader;

public sealed partial class SettingsPage : Page
{
    private bool _initialized;
    private int _refreshIntervalMinutes;

    public SettingsPage()
    {
        InitializeComponent();
        RefreshIntervalNumberBox.NumberFormatter = new DecimalFormatter
        {
            FractionDigits = 0,
            IntegerDigits = 1,
            IsGrouped = false
        };
    }

    public event EventHandler? BackRequested;

    public event EventHandler? ThemeChanged;

    public event EventHandler? LanguageChanged;

    public event EventHandler? RefreshIntervalChanged;

    public event EventHandler? ImportSubscriptionsRequested;

    public event EventHandler? ExportSubscriptionsRequested;

    public AppTheme SelectedTheme => (AppTheme)ThemeSelector.SelectedIndex;

    public AppLanguage SelectedLanguage => (AppLanguage)LanguageSelector.SelectedIndex;

    public int RefreshIntervalMinutes => _refreshIntervalMinutes;

    public void Initialize(AppTheme theme, AppLanguage language, int refreshIntervalMinutes)
    {
        ThemeSelector.SelectedIndex = (int)theme;
        LanguageSelector.SelectedIndex = (int)language;
        _refreshIntervalMinutes = refreshIntervalMinutes;
        RefreshIntervalNumberBox.Value = refreshIntervalMinutes;
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
        RefreshThemeSelectionBox();
        FeedsHeaderText.Text = localization.GetString("Feeds");
        RefreshIntervalTitleText.Text = localization.GetString("RefreshInterval");
        RefreshIntervalDescriptionText.Text = localization.GetString("RefreshIntervalDescription");
        RefreshIntervalUnitText.Text = localization.GetString("Minutes");
        AutomationProperties.SetName(
            RefreshIntervalNumberBox,
            localization.GetString("RefreshInterval"));
        SubscriptionManagementTitleText.Text = localization.GetString("SubscriptionManagement");
        SubscriptionManagementDescriptionText.Text = localization.GetString("SubscriptionManagementDescription");
        ImportSubscriptionsButton.Content = localization.GetString("ImportSubscriptions");
        ExportSubscriptionsButton.Content = localization.GetString("ExportSubscriptions");
        AutomationProperties.SetName(
            ImportSubscriptionsButton,
            localization.GetString("ImportSubscriptions"));
        AutomationProperties.SetName(
            ExportSubscriptionsButton,
            localization.GetString("ExportSubscriptions"));
        LanguageHeaderText.Text = localization.GetString("Language");
        LanguageTitleText.Text = localization.GetString("ApplicationLanguage");
        LanguageDescriptionText.Text = localization.GetString("LanguageDescription");
        AutomationProperties.SetName(LanguageSelector, localization.GetString("ApplicationLanguage"));
    }

    public void SetSubscriptionActionsEnabled(bool isEnabled)
    {
        ImportSubscriptionsButton.IsEnabled = isEnabled;
        ExportSubscriptionsButton.IsEnabled = isEnabled;
        SubscriptionProgressRing.IsActive = !isEnabled;
        SubscriptionProgressRing.Visibility = isEnabled
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (!isEnabled)
        {
            SubscriptionStatusBar.IsOpen = false;
            SubscriptionStatusBar.Visibility = Visibility.Collapsed;
        }
    }

    public void ShowSubscriptionStatus(string message, bool isError)
    {
        SubscriptionStatusBar.Message = message;
        SubscriptionStatusBar.Severity = isError
            ? InfoBarSeverity.Error
            : InfoBarSeverity.Success;
        SubscriptionStatusBar.Visibility = Visibility.Visible;
        SubscriptionStatusBar.IsOpen = true;
    }

    private void RefreshThemeSelectionBox()
    {
        var selectedIndex = ThemeSelector.SelectedIndex;
        if (selectedIndex < 0)
        {
            return;
        }

        var wasInitialized = _initialized;
        _initialized = false;

        try
        {
            ThemeSelector.SelectedIndex = -1;
            ThemeSelector.SelectedIndex = selectedIndex;
        }
        finally
        {
            _initialized = wasInitialized;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    private void ImportSubscriptionsButton_Click(object sender, RoutedEventArgs e) =>
        ImportSubscriptionsRequested?.Invoke(this, EventArgs.Empty);

    private void ExportSubscriptionsButton_Click(object sender, RoutedEventArgs e) =>
        ExportSubscriptionsRequested?.Invoke(this, EventArgs.Empty);

    private void SubscriptionStatusBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        sender.Visibility = Visibility.Collapsed;

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

    private void RefreshIntervalNumberBox_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (!_initialized)
        {
            return;
        }

        if (!double.IsFinite(args.NewValue) ||
            args.NewValue < 1 ||
            args.NewValue > int.MaxValue ||
            args.NewValue != Math.Truncate(args.NewValue))
        {
            _initialized = false;
            sender.Value = _refreshIntervalMinutes;
            _initialized = true;
            return;
        }

        var refreshIntervalMinutes = (int)args.NewValue;
        if (refreshIntervalMinutes == _refreshIntervalMinutes)
        {
            return;
        }

        _refreshIntervalMinutes = refreshIntervalMinutes;
        RefreshIntervalChanged?.Invoke(this, EventArgs.Empty);
    }
}
