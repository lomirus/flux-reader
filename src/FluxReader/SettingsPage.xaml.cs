using FluxReader.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

    public AppTheme SelectedTheme => (AppTheme)ThemeSelector.SelectedIndex;

    public void Initialize(AppTheme theme)
    {
        ThemeSelector.SelectedIndex = (int)theme;
        _initialized = true;
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
}
