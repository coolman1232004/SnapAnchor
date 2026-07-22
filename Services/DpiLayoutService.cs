using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SnapAnchor.Services;

internal static class DpiLayoutService
{
    internal static DpiScale WindowScale(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            var nativeDpi = NativeMethods.GetDpiForWindow(handle);
            if (nativeDpi > 0)
            {
                var scale = nativeDpi / 96d;
                return new DpiScale(scale, scale);
            }
        }
        return VisualTreeHelper.GetDpi(window);
    }

    public static void Attach(Window window)
    {
        var preferredMinimumWidth = window.MinWidth;
        var preferredMinimumHeight = window.MinHeight;
        System.Drawing.Rectangle? currentDisplay = null;
        void ConstrainForCurrentDisplay()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            currentDisplay = DisplayTopologyService.MonitorBoundsForWindowPixels(handle);
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
            var display = DisplayTopologyService.MonitorBoundsForWindowPixels(handle);
            if (display != currentDisplay)
                window.Dispatcher.BeginInvoke(ConstrainForCurrentDisplay);
        };
        window.Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
    }

    private static void Constrain(Window window, double preferredMinimumWidth, double preferredMinimumHeight)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var workingArea = DisplayTopologyService.WorkingAreaForWindowPixels(handle);
        var dpi = WindowScale(window);
        var available = AvailableLogicalSize(
            new Size(workingArea.Width, workingArea.Height),
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

    internal static Rect PhysicalBoundsToLogical(Window window, NativeMethods.NativeRect target, NativeMethods.NativeRect origin)
    {
        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice;
        if (transform is { } matrix)
        {
            var topLeft = matrix.Transform(new Point(target.Left - origin.Left, target.Top - origin.Top));
            var bottomRight = matrix.Transform(new Point(target.Right - origin.Left, target.Bottom - origin.Top));
            return new Rect(topLeft, bottomRight);
        }

        var scale = WindowScale(window);
        return new Rect(
            (target.Left - origin.Left) / Math.Max(0.1, scale.DpiScaleX),
            (target.Top - origin.Top) / Math.Max(0.1, scale.DpiScaleY),
            Math.Max(1, target.Width / Math.Max(0.1, scale.DpiScaleX)),
            Math.Max(1, target.Height / Math.Max(0.1, scale.DpiScaleY)));
    }
}
