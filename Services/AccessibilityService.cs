using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SnapAnchor.Services;

internal static class AccessibilityService
{
    private static Style? _toolbarFocusVisual;

    internal static void Apply(DependencyObject root)
    {
        ApplyElement(root);
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) Apply(child);
    }

    /// <summary>
    /// Makes capture and pin toolbars keyboard-reachable: names for screen readers,
    /// Tab/arrow focus, and a visible focus ring. Call after the toolbar is built.
    /// </summary>
    internal static void ApplyToolbar(DependencyObject root)
    {
        Apply(root);
        foreach (var element in EnumerateVisualTree(root))
        {
            if (element is not Button button) continue;
            if (button.IsHitTestVisible == false) continue;
            if (button.Visibility != Visibility.Visible) continue;

            // Palette swatches stay mouse-only (too dense for sequential tab).
            if (button.Width <= 16 && button.Height <= 16) continue;

            EnsureAccessibleName(button);
            button.Focusable = true;
            button.IsTabStop = button.IsEnabled;
            button.FocusVisualStyle = ToolbarFocusVisualStyle();
            KeyboardNavigation.SetIsTabStop(button, button.IsEnabled);
        }

        if (root is FrameworkElement panel)
        {
            KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Continue);
            KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(panel, KeyboardNavigationMode.Once);
        }
    }

    internal static bool FocusFirstToolbarButton(DependencyObject root)
    {
        var button = EnumerateVisualTree(root)
            .OfType<Button>()
            .FirstOrDefault(candidate =>
                candidate.IsVisible &&
                candidate.IsEnabled &&
                candidate.Focusable &&
                candidate.IsHitTestVisible &&
                candidate.Width > 16);
        return button?.Focus() == true;
    }

    /// <summary>
    /// Moves keyboard focus among visible toolbar buttons. Returns true when handled.
    /// </summary>
    internal static bool MoveToolbarFocus(DependencyObject root, int direction)
    {
        var buttons = EnumerateVisualTree(root)
            .OfType<Button>()
            .Where(candidate =>
                candidate.IsVisible &&
                candidate.IsEnabled &&
                candidate.Focusable &&
                candidate.IsHitTestVisible &&
                candidate.Width > 16)
            .ToList();
        if (buttons.Count == 0) return false;

        var current = Keyboard.FocusedElement as Button;
        var index = current is null ? -1 : buttons.IndexOf(current);
        if (index < 0)
            return buttons[direction >= 0 ? 0 : buttons.Count - 1].Focus();

        var next = (index + Math.Sign(direction) + buttons.Count) % buttons.Count;
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

    private static Style ToolbarFocusVisualStyle()
    {
        if (_toolbarFocusVisual is not null) return _toolbarFocusVisual;
        var style = new Style(typeof(Control));
        var template = new ControlTemplate(typeof(Control));
        var borderFactory = new FrameworkElementFactory(typeof(Rectangle));
        borderFactory.SetValue(Shape.StrokeProperty, new SolidColorBrush(Color.FromRgb(37, 99, 235)));
        borderFactory.SetValue(Shape.StrokeThicknessProperty, 2.0);
        borderFactory.SetValue(Shape.StrokeDashArrayProperty, new DoubleCollection { 1, 1.5 });
        borderFactory.SetValue(Rectangle.RadiusXProperty, 4.0);
        borderFactory.SetValue(Rectangle.RadiusYProperty, 4.0);
        borderFactory.SetValue(UIElement.IsHitTestVisibleProperty, false);
        borderFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(-1));
        template.VisualTree = borderFactory;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        _toolbarFocusVisual = style;
        return style;
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

        // Toolbars are often still logical-only before measure.
        foreach (var logical in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (logical is Visual) continue;
            foreach (var nested in EnumerateVisualTree(logical))
                yield return nested;
        }
    }
}
