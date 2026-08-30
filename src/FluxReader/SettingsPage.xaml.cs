using FluxReader.Models;
using FluxReader.Services;
using FluxReader.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Windows.Globalization.NumberFormatting;
using Windows.System;

namespace FluxReader;

public sealed partial class SettingsPage : Page
{
    private bool _initialized;
    private string _customProxyAddress = string.Empty;
    private int _refreshIntervalMinutes;
    private bool _showProxyValidationError;

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

    public event EventHandler? ExternalStylesheetsChanged;

    public event EventHandler? ProxyChanged;

    public event EventHandler? ImportSubscriptionsRequested;

    public event EventHandler? ExportSubscriptionsRequested;

    public AppTheme SelectedTheme => (AppTheme)ThemeSelector.SelectedIndex;

    public AppLanguage SelectedLanguage => (AppLanguage)LanguageSelector.SelectedIndex;

    public int RefreshIntervalMinutes => _refreshIntervalMinutes;

    public bool LoadExternalArticleStylesheets => ExternalStylesheetsToggleSwitch.IsOn;

    public ProxyMode SelectedProxyMode => ProxyModeSelector.SelectedIndex switch
    {
        0 => ProxyMode.Disabled,
        2 => ProxyMode.Custom,
        _ => ProxyMode.System
    };

    public void Initialize(
        AppTheme theme,
        AppLanguage language,
        int refreshIntervalMinutes,
        bool loadExternalArticleStylesheets,
        ProxyMode proxyMode,
        string customProxyAddress)
    {
        ThemeSelector.SelectedIndex = (int)theme;
        LanguageSelector.SelectedIndex = (int)language;
        _refreshIntervalMinutes = refreshIntervalMinutes;
        RefreshIntervalNumberBox.Value = refreshIntervalMinutes;
        ExternalStylesheetsToggleSwitch.IsOn = loadExternalArticleStylesheets;
        _customProxyAddress = ConfigurableWebProxy.TryNormalizeAddress(
            customProxyAddress,
            out var normalizedAddress)
                ? normalizedAddress
                : string.Empty;
        CustomProxyAddressTextBox.Text = _customProxyAddress;
        ProxyModeSelector.SelectedIndex = proxyMode switch
        {
            ProxyMode.Disabled => 0,
            ProxyMode.Custom => 2,
            _ => 1
        };
        _showProxyValidationError = false;
        _initialized = true;
        UpdateCustomProxyState();
        ApplyLocalization();
    }

    public bool TryGetProxyConfiguration(
        out ProxyMode proxyMode,
        out string customProxyAddress)
    {
        proxyMode = SelectedProxyMode;
        customProxyAddress = _customProxyAddress;
        if (proxyMode != ProxyMode.Custom)
        {
            return true;
        }

        if (!ConfigurableWebProxy.TryNormalizeAddress(
                CustomProxyAddressTextBox.Text,
                out customProxyAddress))
        {
            return false;
        }

        _customProxyAddress = customProxyAddress;
        return true;
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
        ExternalStylesheetsTitleText.Text = localization.GetString("LoadExternalStylesheets");
        ExternalStylesheetsDescriptionText.Text = localization.GetString("LoadExternalStylesheetsDescription");
        AutomationProperties.SetName(
            ExternalStylesheetsToggleSwitch,
            localization.GetString("LoadExternalStylesheets"));
        NetworkHeaderText.Text = localization.GetString("Network");
        ProxyTitleText.Text = localization.GetString("Proxy");
        ProxyDescriptionText.Text = localization.GetString("ProxyDescription");
        AutomationProperties.SetName(ProxyModeSelector, localization.GetString("Proxy"));
        DisabledProxyItem.Content = localization.GetString("ProxyDisabled");
        SystemProxyItem.Content = localization.GetString("ProxySystem");
        CustomProxyItem.Content = localization.GetString("ProxyCustom");
        RefreshProxySelectionBox();
        CustomProxyAddressTitleText.Text = localization.GetString("CustomProxyAddress");
        CustomProxyAddressTextBox.PlaceholderText = localization.GetString("CustomProxyAddressPlaceholder");
        InvalidProxyAddressText.Text = localization.GetString("InvalidProxyAddress");
        AutomationProperties.SetName(
            CustomProxyAddressTextBox,
            localization.GetString("CustomProxyAddress"));
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

    private void RefreshProxySelectionBox()
    {
        var selectedIndex = ProxyModeSelector.SelectedIndex;
        if (selectedIndex < 0)
        {
            return;
        }

        var wasInitialized = _initialized;
        _initialized = false;

        try
        {
            ProxyModeSelector.SelectedIndex = -1;
            ProxyModeSelector.SelectedIndex = selectedIndex;
        }
        finally
        {
            _initialized = wasInitialized;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        CommitProxyConfiguration();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

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

    private void ExternalStylesheetsToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            ExternalStylesheetsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ComboBox_DropDownOpened(object sender, object e)
    {
        // TODO(winui): Remove this workaround after
        // https://github.com/microsoft/microsoft-ui-xaml/issues/9542 is fixed.
        // Windowed popups can retain the resize cursor from the pane splitters.
        NativeCursor.SetArrow();
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            NativeCursor.SetArrow);
    }

    private void ProxyModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _showProxyValidationError = false;
        UpdateCustomProxyState();
        if (_initialized &&
            SelectedProxyMode == ProxyMode.Custom &&
            !TryGetProxyConfiguration(out _, out _))
        {
            DispatcherQueue.TryEnqueue(() =>
                CustomProxyAddressTextBox.Focus(FocusState.Programmatic));
            return;
        }

        CommitProxyConfiguration();
    }

    private void CustomProxyAddressTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox &&
            ConfigurableWebProxy.TryNormalizeAddress(textBox.Text, out _))
        {
            _showProxyValidationError = false;
        }

        UpdateCustomProxyState();
    }

    private void CustomProxyAddressTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _showProxyValidationError = true;
        CommitProxyConfiguration();
    }

    private void CustomProxyAddressTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            _showProxyValidationError = true;
            CommitProxyConfiguration();
        }
    }

    private void UpdateCustomProxyState()
    {
        var isCustom = ProxyModeSelector.SelectedIndex == 2;
        CustomProxyPanel.Visibility = isCustom
            ? Visibility.Visible
            : Visibility.Collapsed;
        var isValid = ConfigurableWebProxy.TryNormalizeAddress(
            CustomProxyAddressTextBox.Text,
            out _);
        InvalidProxyAddressText.Visibility = _initialized &&
                                             _showProxyValidationError &&
                                             isCustom &&
                                             !isValid
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CommitProxyConfiguration()
    {
        if (!_initialized || ProxyModeSelector.SelectedIndex < 0)
        {
            return;
        }

        if (!TryGetProxyConfiguration(out _, out _))
        {
            _showProxyValidationError = true;
            UpdateCustomProxyState();
            return;
        }

        _showProxyValidationError = false;
        InvalidProxyAddressText.Visibility = Visibility.Collapsed;
        ProxyChanged?.Invoke(this, EventArgs.Empty);
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
