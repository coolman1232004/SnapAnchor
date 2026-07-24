using System.Windows;

namespace SnapAnchor.Services;

internal static class ToolbarThemeService
{
    internal static readonly string[] Modes = ["Compact", "Standard", "Large"];
    private static string _currentMode = "Compact";

    internal static string Normalize(string? mode) =>
        Modes.FirstOrDefault(item => item.Equals(mode, StringComparison.OrdinalIgnoreCase)) ?? "Compact";

    internal static void Apply(string? mode)
    {
        _currentMode = Normalize(mode);
        if (Application.Current is not null)
            ApplyTo(Application.Current.Resources, _currentMode);
    }

    internal static void ApplyTo(ResourceDictionary resources, string? mode = null)
    {
        var normalized = mode is null ? _currentMode : Normalize(mode);
        // Compact targets ~32–34 px outer height; larger modes scale icons evenly.
        var metrics = normalized switch
        {
            "Large" => new Metrics(34, 20, 14, 20, new Thickness(4), new Thickness(2.5, 0, 2.5, 0), new Thickness(7, 4, 7, 4), new Thickness(5, 6, 5, 6)),
            "Standard" => new Metrics(30, 18, 13, 18, new Thickness(3.5), new Thickness(2, 0, 2, 0), new Thickness(6, 3.5, 6, 3.5), new Thickness(4, 5, 4, 5)),
            _ => new Metrics(26, 16, 12, 16, new Thickness(3), new Thickness(2, 0, 2, 0), new Thickness(5, 3, 5, 3), new Thickness(4, 4, 4, 4))
        };
        resources["ToolbarButtonSize"] = metrics.Button;
        resources["ToolbarIconSize"] = metrics.Icon;
        resources["ToolbarGripWidth"] = metrics.Grip;
        resources["ToolbarSeparatorHeight"] = metrics.Separator;
        resources["ToolbarButtonPadding"] = metrics.ButtonPadding;
        resources["ToolbarButtonMargin"] = metrics.ButtonMargin;
        resources["ToolbarFramePadding"] = metrics.FramePadding;
        resources["ToolbarSeparatorMargin"] = metrics.SeparatorMargin;
    }

    private readonly record struct Metrics(double Button, double Icon, double Grip, double Separator,
        Thickness ButtonPadding, Thickness ButtonMargin, Thickness FramePadding, Thickness SeparatorMargin);
}
