using Drawing = System.Drawing;

namespace SnapAnchor.Services;

/// <summary>
/// Provides display geometry exclusively in physical desktop pixels. Keeping
/// native window rectangles and captured bitmap coordinates in one coordinate
/// space prevents DPI virtualization from shrinking or offsetting secondary
/// monitor captures.
/// </summary>
internal static class DisplayTopologyService
{
    internal static Drawing.Rectangle VirtualBoundsPixels()
    {
        var monitors = EnumerateMonitorBoundsPixels();
        return monitors.Count == 0
            ? System.Windows.Forms.SystemInformation.VirtualScreen
            : UnionBounds(monitors);
    }

    internal static Drawing.Rectangle MonitorBoundsForWindowPixels(IntPtr window)
        => MonitorInfoForWindow(window).Monitor;

    internal static Drawing.Rectangle WorkingAreaForWindowPixels(IntPtr window)
        => MonitorInfoForWindow(window).Work;

    internal static IReadOnlyList<Drawing.Rectangle> EnumerateMonitorBoundsPixels()
    {
        var result = new List<Drawing.Rectangle>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr monitor, IntPtr _, ref NativeMethods.NativeRect bounds, IntPtr __) =>
            {
                result.Add(ToRectangle(bounds));
                return true;
            }, IntPtr.Zero);
        return result;
    }

    internal static Drawing.Rectangle UnionBounds(IEnumerable<Drawing.Rectangle> bounds)
    {
        using var enumerator = bounds.GetEnumerator();
        if (!enumerator.MoveNext()) return Drawing.Rectangle.Empty;
        var union = enumerator.Current;
        while (enumerator.MoveNext()) union = Drawing.Rectangle.Union(union, enumerator.Current);
        return union;
    }

    private static (Drawing.Rectangle Monitor, Drawing.Rectangle Work) MonitorInfoForWindow(IntPtr window)
    {
        var monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.NativeMonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NativeMonitorInfo>()
        };
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
            return (ToRectangle(info.Monitor), ToRectangle(info.Work));

        var fallback = VirtualBoundsPixels();
        return (fallback, fallback);
    }

    private static Drawing.Rectangle ToRectangle(NativeMethods.NativeRect bounds)
        => new(bounds.Left, bounds.Top, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
}
