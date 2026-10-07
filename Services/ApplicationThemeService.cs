using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace SnapAnchor.Services;

internal static class ApplicationThemeService
{
    private static string _mode = "System";
    private static bool _listening;
    internal static string Normalize(string? mode) => mode is "Light" or "Dark" ? mode : "System";

    internal static void EnsureResources(FrameworkElement element)
    {
        // Isolated WPF hosts (including the layout harness) have no App.xaml resources.
        if (Application.Current?.TryFindResource("AppButtonStyle") is not null) return;
        element.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SnapAnchor;component/Resources/ToolbarResources.xaml", UriKind.Relative) });
        element.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SnapAnchor;component/Resources/ApplicationTheme.xaml", UriKind.Relative) });
    }

    internal static void Apply(string? mode)
    {
        _mode = Normalize(mode);
        var application = Application.Current;
        if (application is null) return;
        if (!_listening)
        {
            SystemEvents.UserPreferenceChanged += PreferencesChanged;
            application.Exit += (_, _) => { SystemEvents.UserPreferenceChanged -= PreferencesChanged; _listening = false; };
            _listening = true;
        }
        var dark = _mode == "Dark" || (_mode == "System" && !UsesLightTheme());
        var colors = new Dictionary<string, string>
        {
            ["PageBrush"] = dark ? "#202124" : "#F6F7F9",
            ["PanelBrush"] = dark ? "#2B2C30" : "#FFFFFF",
            ["TextBrush"] = dark ? "#F4F4F5" : "#202124",
            ["MutedBrush"] = dark ? "#B6BAC3" : "#616773",
            ["AccentBrush"] = dark ? "#75BAFF" : "#0067C0",
            ["AccentTextBrush"] = dark ? "#082B4B" : "#FFFFFF",
            ["NeutralBrush"] = dark ? "#45474E" : "#DFE2E7",
            ["SecondaryAccentBrush"] = dark ? "#34363B" : "#EEF0F3",
            ["SelectionBrush"] = dark ? "#243E56" : "#E9F2FC",
            ["ThumbnailBrush"] = dark ? "#191B20" : "#E7EAF0",
            ["SuccessBrush"] = dark ? "#84D4AD" : "#19704B",
            ["DangerBrush"] = dark ? "#FFB4AB" : "#B42318"
        };
        foreach (var (key, value) in colors)
            application.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        if (SystemParameters.HighContrast)
        {
            foreach (var key in new[] { "PageBrush", "PanelBrush", "ThumbnailBrush", "SecondaryAccentBrush" }) application.Resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "TextBrush", "MutedBrush", "NeutralBrush", "SuccessBrush", "DangerBrush" }) application.Resources[key] = SystemColors.WindowTextBrush;
            application.Resources["AccentBrush"] = SystemColors.HighlightBrush;
            application.Resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
            application.Resources["SelectionBrush"] = SystemColors.ControlBrush;
        }
        var toolbar = new Dictionary<string, string>
        {
            ["ToolbarSurfaceBrush"] = "PanelBrush", ["ToolbarBorderBrush"] = "NeutralBrush", ["ToolbarInkBrush"] = "TextBrush",
            ["ToolbarHoverBrush"] = "SecondaryAccentBrush", ["ToolbarPressedBrush"] = "SelectionBrush", ["ToolbarActiveBrush"] = "SelectionBrush",
            ["ToolbarActiveInkBrush"] = "TextBrush", ["ToolbarSeparatorBrush"] = "NeutralBrush", ["ToolbarGripBrush"] = "MutedBrush"
        };
        foreach (var (key, value) in toolbar) application.Resources[key] = application.Resources[value];
    }

    private static bool UsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch { return true; }
    }

    private static void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var application = Application.Current;
        if (application is not null && !application.Dispatcher.HasShutdownStarted)
            application.Dispatcher.BeginInvoke(() => Apply(_mode));
    }
}
