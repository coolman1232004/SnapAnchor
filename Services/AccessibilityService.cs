using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SnapAnchor.Services;

internal static class AccessibilityService
{
    private const string PaletteTag = "Palette";

    internal static void Apply(DependencyObject root)
    {
        ApplyElement(root);
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) Apply(child);
    }

    /// <summary>
    /// Makes capture and pin toolbars keyboard-reachable: names for screen readers
    /// and Tab/arrow focus. Call after the toolbar is built. Focus chrome lives in
    /// the toolbar button style (IsKeyboardFocused), not a second code-built ring.
    /// </summary>
    internal static void ApplyToolbar(DependencyObject root)
    {
        Apply(root);
        foreach (var element in EnumerateVisualTree(root))
        {
            if (element is not Button button) continue;
            if (!IsToolbarCommandButton(button)) continue;

            EnsureAccessibleName(button);
            button.Focusable = true;
            button.IsTabStop = button.IsEnabled;
            KeyboardNavigation.SetIsTabStop(button, button.IsEnabled);
        }

        if (root is FrameworkElement panel)
        {
            KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Continue);
            KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(panel, KeyboardNavigationMode.Once);
        }
    }

    /// <summary>
    /// Shared F6 / arrow-key toolbar navigation for capture, annotation, and pin windows.
    /// <paramref name="resolveHosts"/> returns candidate toolbar roots in focus order.
    /// </summary>
    internal static bool TryHandleToolbarNavigation(KeyEventArgs e, Func<IEnumerable<DependencyObject>> resolveHosts)
    {
        if (e.Key == Key.F6)
        {
            foreach (var host in resolveHosts())
            {
                if (host is null) continue;
                ApplyToolbar(host);
                if (FocusFirstToolbarButton(host))
                {
                    e.Handled = true;
                    return true;
                }
            }
            return false;
        }

        if (Keyboard.FocusedElement is not Button) return false;
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return false;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != ModifierKeys.None) return false;

        var direction = e.Key is Key.Left or Key.Up ? -1 : 1;
        foreach (var host in resolveHosts())
        {
            if (host is null) continue;
            if (MoveToolbarFocus(host, direction))
            {
                e.Handled = true;
                return true;
            }
        }
        return false;
    }

    internal static bool FocusFirstToolbarButton(DependencyObject root)
    {
        var button = EnumerateToolbarButtons(root).FirstOrDefault();
        return button?.Focus() == true;
    }

    /// <summary>
    /// Moves keyboard focus among visible toolbar buttons. Returns true when handled.
    /// </summary>
    internal static bool MoveToolbarFocus(DependencyObject root, int direction)
    {
        var buttons = EnumerateToolbarButtons(root).ToList();
        if (buttons.Count == 0) return false;

        var current = Keyboard.FocusedElement as Button;
        var index = current is null ? -1 : buttons.IndexOf(current);
        if (index < 0)
            return buttons[direction >= 0 ? 0 : buttons.Count - 1].Focus();

        var next = (index + Math.Sign(direction == 0 ? 1 : direction) + buttons.Count) % buttons.Count;
        return buttons[next].Focus();
    }

    internal static string AccessibleNameOf(FrameworkElement element)
    {
        var name = AutomationProperties.GetName(element);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return element switch
        {
            TextBlock text => text.Text ?? string.Empty,
            ContentControl { Content: string content } => content,
            HeaderedContentControl { Header: string header } => header,
            _ => element.ToolTip as string ?? string.Empty
        };
    }

    private static IEnumerable<Button> EnumerateToolbarButtons(DependencyObject root) =>
        EnumerateVisualTree(root)
            .OfType<Button>()
            .Where(IsToolbarCommandButton);

    private static bool IsToolbarCommandButton(Button button) =>
        button.IsVisible &&
        button.IsEnabled &&
        button.IsHitTestVisible &&
        !string.Equals(button.Tag as string, PaletteTag, StringComparison.OrdinalIgnoreCase) &&
        // Compact colour swatches historically used 13×13; skip dense grids without tags.
        !(button.Width > 0 && button.Width <= 16 && button.Height > 0 && button.Height <= 16);

    private static void ApplyElement(DependencyObject value)
    {
        if (value is FrameworkElement element)
            EnsureAccessibleName(element);

        if (!SystemParameters.HighContrast) return;
        if (value is Window window)
        {
            window.Background = SystemColors.WindowBrush;
            window.Foreground = SystemColors.WindowTextBrush;
        }
        else if (value is Control control)
        {
            control.Foreground = SystemColors.WindowTextBrush;
            if (control is Button or TextBox or ComboBox or ListBox)
                control.BorderBrush = SystemColors.WindowTextBrush;
        }
    }

    private static void EnsureAccessibleName(FrameworkElement element)
    {
        if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(element))) return;
        var name = AccessibleNameOf(element);
        if (!string.IsNullOrWhiteSpace(name))
            AutomationProperties.SetName(element, name.Trim());
    }

    private static IEnumerable<DependencyObject> EnumerateVisualTree(DependencyObject root)
    {
        yield return root;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            foreach (var nested in EnumerateVisualTree(child))
                yield return nested;
        }

        foreach (var logical in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (logical is Visual) continue;
            foreach (var nested in EnumerateVisualTree(logical))
                yield return nested;
        }
    }
}
