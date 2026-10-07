using System.Windows;
using SnapAnchor.Services;
using SnapAnchor.Controls;

namespace SnapAnchor.Windows;

public partial class PreferencesWindow : Window
{
    internal PreferencesView View { get; }
    public event EventHandler? SettingsApplied;
    public PreferencesWindow(AppSettings settings)
    {
        ApplicationThemeService.EnsureResources(this);
        InitializeComponent();
        DpiLayoutService.Attach(this);
        View = new PreferencesView(settings);
        ViewHost.Content = View;
        View.SettingsApplied += (_, _) => SettingsApplied?.Invoke(this, EventArgs.Empty);
        View.Completed += (_, _) => DialogResult = true;
        View.Cancelled += (_, _) => Close();
    }
}
