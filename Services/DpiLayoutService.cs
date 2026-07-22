using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace SnapAnchor.Services;

internal static class DpiLayoutService
{
    public static void Attach(Window window)
    {
        var preferredMinimumWidth = window.MinWidth;
        var preferredMinimumHeight = window.MinHeight;
        string? currentDisplay = null;
        void ConstrainForCurrentDisplay()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            currentDisplay = Forms.Screen.FromHandle(handle).DeviceName;
            Constrain(window, preferredMinimumWidth, preferredMinimumHeight);
        }
        void DisplaySettingsChanged(object? sender, EventArgs args) => window.Dispatcher.BeginInvoke(ConstrainForCurrentDisplay);

        window.Loaded += (_, _) =>
        {
            ConstrainForCurrentDisplay();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        };
        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(ConstrainForCurrentDisplay);
        window.LocationChanged += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var display = Forms.Screen.FromHandle(handle).DeviceName;
            if (!string.Equals(display, currentDisplay, StringComparison.OrdinalIgnoreCase))
                window.Dispatcher.BeginInvoke(ConstrainForCurrentDisplay);
        };
        window.Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
    }

    private static void Constrain(Window window, double preferredMinimumWidth, double preferredMinimumHeight)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var screen = Forms.Screen.FromHandle(handle);
        var dpi = VisualTreeHelper.GetDpi(window);
        var available = AvailableLogicalSize(
            new Size(screen.WorkingArea.Width, screen.WorkingArea.Height),
            dpi.DpiScaleX,
            dpi.DpiScaleY);
        var availableWidth = available.Width;
        var availableHeight = available.Height;

        window.MinWidth = Math.Min(preferredMinimumWidth, availableWidth);
        window.MinHeight = Math.Min(preferredMinimumHeight, availableHeight);
        window.MaxWidth = availableWidth;
        window.MaxHeight = availableHeight;
        if (window.Width > availableWidth) window.Width = availableWidth;
        if (window.Height > availableHeight) window.Height = availableHeight;
    }

    internal static Size AvailableLogicalSize(Size workingAreaPixels, double scaleX, double scaleY) => new(
        Math.Max(1, workingAreaPixels.Width / Math.Max(0.1, scaleX) - 24),
        Math.Max(1, workingAreaPixels.Height / Math.Max(0.1, scaleY) - 24));

    internal static double PhysicalZoomPercent(double logicalWidth, double dpiScaleX, int imagePixelWidth) =>
        Math.Max(1, Math.Round(logicalWidth * Math.Max(0.1, dpiScaleX) / Math.Max(1, imagePixelWidth) * 100));

    internal static Size LogicalSizeForPhysicalPixels(int pixelWidth, int pixelHeight, double scaleX, double scaleY) => new(
        Math.Max(1, pixelWidth / Math.Max(0.1, scaleX)),
        Math.Max(1, pixelHeight / Math.Max(0.1, scaleY)));
}
