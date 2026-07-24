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

    internal static Drawing.Rectangle MonitorBoundsContainingPointPixels(int x, int y)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.NativePoint { X = x, Y = y },
            NativeMethods.MonitorDefaultToNearest);
        return MonitorBoundsFromHandle(monitor);
    }

    internal static Drawing.Rectangle WorkingAreaContainingPointPixels(int x, int y)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.NativePoint { X = x, Y = y },
            NativeMethods.MonitorDefaultToNearest);
        return WorkingAreaFromHandle(monitor);
    }

    /// <summary>
    /// Picks the monitor that best covers <paramref name="selection"/> and
    /// returns a panel rectangle of the same size fully inside that monitor.
    /// Prevents toolbars from straddling mixed-DPI monitor edges.
    /// </summary>
    internal static Drawing.Rectangle ClampPanelToSingleMonitor(
        Drawing.Rectangle preferredPanel,
        Drawing.Rectangle selection)
        => ClampPanelToSingleMonitor(preferredPanel, selection, EnumerateMonitorBoundsPixels(), useWorkingArea: true);

    /// <summary>Testable overload with an explicit monitor list (physical pixels).</summary>
    internal static Drawing.Rectangle ClampPanelToSingleMonitor(
        Drawing.Rectangle preferredPanel,
        Drawing.Rectangle selection,
        IReadOnlyList<Drawing.Rectangle> monitors,
        bool useWorkingArea = false)
    {
        if (monitors.Count == 0) return preferredPanel;

        Drawing.Rectangle best = monitors[0];
        long bestArea = -1;
        foreach (var monitor in monitors)
        {
            var hit = Drawing.Rectangle.Intersect(monitor, selection);
            var area = (long)Math.Max(0, hit.Width) * Math.Max(0, hit.Height);
            if (area > bestArea)
            {
                bestArea = area;
                best = monitor;
            }
        }

        if (bestArea <= 0)
        {
            var cx = selection.Left + selection.Width / 2;
            var cy = selection.Top + selection.Height / 2;
            best = monitors
                .OrderBy(m => DistanceSquared(cx, cy, m))
                .First();
        }

        var work = useWorkingArea
            ? WorkingAreaContainingPointPixels(best.Left + best.Width / 2, best.Top + best.Height / 2)
            : best;
        if (work.Width < 1 || work.Height < 1) work = best;
        // Keep clamp within the chosen monitor even if working area is empty/odd.
        work = Drawing.Rectangle.Intersect(work, best);
        if (work.Width < 1 || work.Height < 1) work = best;

        var width = Math.Min(preferredPanel.Width, work.Width);
        var height = Math.Min(preferredPanel.Height, work.Height);
        var left = Math.Clamp(preferredPanel.Left, work.Left, work.Right - width);
        var top = Math.Clamp(preferredPanel.Top, work.Top, work.Bottom - height);
        return new Drawing.Rectangle(left, top, Math.Max(1, width), Math.Max(1, height));
    }

    private static long DistanceSquared(int x, int y, Drawing.Rectangle monitor)
    {
        var cx = monitor.Left + monitor.Width / 2;
        var cy = monitor.Top + monitor.Height / 2;
        long dx = x - cx;
        long dy = y - cy;
        return dx * dx + dy * dy;
    }

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
        return (MonitorBoundsFromHandle(monitor), WorkingAreaFromHandle(monitor));
    }

    private static Drawing.Rectangle MonitorBoundsFromHandle(IntPtr monitor)
    {
        var info = new NativeMethods.NativeMonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NativeMonitorInfo>()
        };
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
            return ToRectangle(info.Monitor);
        return VirtualBoundsPixels();
    }

    private static Drawing.Rectangle WorkingAreaFromHandle(IntPtr monitor)
    {
        var info = new NativeMethods.NativeMonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NativeMonitorInfo>()
        };
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
            return ToRectangle(info.Work);
        return VirtualBoundsPixels();
    }

    private static Drawing.Rectangle ToRectangle(NativeMethods.NativeRect bounds)
        => new(bounds.Left, bounds.Top, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
}
