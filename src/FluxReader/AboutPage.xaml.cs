using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluxReader;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        ApplyLocalization();
    }

    public event EventHandler? BackRequested;

    private void ApplyLocalization()
    {
        var localization = App.Current.Localization;
        Language = localization.LanguageTag;

        var back = localization.GetString("Back");
        AutomationProperties.SetName(BackButton, back);
        ToolTipService.SetToolTip(BackButton, back);
        AutomationProperties.SetName(BrandIcon, localization.GetString("AppIconAutomation"));
        AboutTitleText.Text = localization.GetString("About");
        VersionText.Text = localization.Format("VersionDisplay", GetCurrentVersion());
    }

    private static string GetCurrentVersion()
    {
        var assembly = typeof(AboutPage).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);
}
