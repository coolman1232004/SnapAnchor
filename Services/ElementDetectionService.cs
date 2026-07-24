using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace SnapAnchor.Services;

internal sealed record DetectedScreenRegion(Rect Bounds, string Name, bool IsElement);

/// <summary>
/// Fast window / UI-element hover detection for capture.
///
/// Performance (Snow Shot–style, independent code):
/// 1) Capture open → enumerate top-level window rects only (milliseconds, UI-safe).
/// 2) Mouse move → pure geometry hit-tests (no UI Automation on the hot path).
/// 3) UI elements → lazy, one window at a time, on Background priority, with a hard budget.
/// </summary>
internal static class ElementDetectionService
{
    private const int MaxElementsPerWindow = 80;
    private const int MaxElementDepth = 5;

    private sealed record CachedRegion(
        Rect Bounds,
        string Name,
        bool IsElement,
        int ZOrder,
        int Depth,
        IntPtr Window);

    private static readonly object Sync = new();
    private static List<CachedRegion> _windows = [];
    private static readonly Dictionary<IntPtr, List<CachedRegion>> ElementsByWindow = new();
    private static readonly HashSet<IntPtr> ElementLoadStarted = new();
    private static IntPtr _excludedHwnd;

    public static DetectedScreenRegion? Detect(Point screenPoint, IntPtr excludedWindow)
        => DetectHierarchy(screenPoint, excludedWindow, includeElements: true).LastOrDefault();

    public static IntPtr WindowHandleAt(Point screenPoint, IntPtr excludedWindow)
    {
        EnsureWindows(excludedWindow);
        return HitTestWindow(screenPoint)?.Window ?? IntPtr.Zero;
    }

    /// <summary>
    /// Instant window-only cache. Safe on the UI thread when capture opens.
    /// Does not touch UI Automation.
    /// </summary>
    public static void RebuildWindowCache(IntPtr excludedOverlayHwnd)
    {
        var windows = new List<CachedRegion>(64);
        var zOrder = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsEligibleWindow(hwnd, excludedOverlayHwnd)) return true;
            var rect = WindowRect(hwnd);
            if (rect.IsEmpty || rect.Width < 8 || rect.Height < 8) return true;
            windows.Add(new CachedRegion(rect, WindowTitle(hwnd), false, zOrder, 0, hwnd));
            zOrder++;
            return true;
        }, IntPtr.Zero);

        lock (Sync)
        {
            _excludedHwnd = excludedOverlayHwnd;
            _windows = windows;
            ElementsByWindow.Clear();
            ElementLoadStarted.Clear();
        }
    }

    public static void RebuildCache(IntPtr excludedOverlayHwnd) => RebuildWindowCache(excludedOverlayHwnd);

    public static IReadOnlyList<DetectedScreenRegion> DetectHierarchy(
        Point screenPoint,
        IntPtr excludedWindow,
        bool includeElements = true)
    {
        EnsureWindows(excludedWindow);
        var top = HitTestWindow(screenPoint);
        if (top is null) return Array.Empty<DetectedScreenRegion>();

        var hits = new List<CachedRegion>(12) { top };

        if (includeElements)
        {
            List<CachedRegion>? elements = null;
            var shouldSchedule = false;
            lock (Sync)
            {
                if (ElementsByWindow.TryGetValue(top.Window, out var ready))
                    elements = ready;
                else if (ElementLoadStarted.Add(top.Window))
                    shouldSchedule = true;
            }

            if (shouldSchedule)
                ScheduleElementLoad(top.Window, top.Bounds, top.ZOrder);

            if (elements is { Count: > 0 })
            {
                foreach (var region in elements)
                {
                    if (!region.Bounds.Contains(screenPoint)) continue;
                    if (hits.Any(existing => NearlyEqual(existing.Bounds, region.Bounds))) continue;
                    hits.Add(region);
                }
            }
        }

        hits.Sort((left, right) =>
        {
            if (left.IsElement != right.IsElement)
                return left.IsElement ? 1 : -1;
            var area = (right.Bounds.Width * right.Bounds.Height)
                .CompareTo(left.Bounds.Width * left.Bounds.Height);
            return area != 0 ? area : left.Depth.CompareTo(right.Depth);
        });

        return hits
            .Select(region => new DetectedScreenRegion(region.Bounds, region.Name, region.IsElement))
            .ToList();
    }

    public static DetectedScreenRegion? TopExternalWindow()
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsEligibleWindow(hwnd, IntPtr.Zero)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return null;
        var rect = WindowRect(found);
        return rect.IsEmpty ? null : new DetectedScreenRegion(rect, WindowTitle(found), false);
    }

    private static void ScheduleElementLoad(IntPtr window, Rect windowBounds, int zOrder)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // Non-WPF callers (smoke tests): load synchronously with the same budget.
            LoadElementsForWindow(window, windowBounds, zOrder);
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try { LoadElementsForWindow(window, windowBounds, zOrder); }
            catch { /* best effort */ }
        });
    }

    private static void LoadElementsForWindow(IntPtr window, Rect windowBounds, int zOrder)
    {
        var collected = new List<CachedRegion>(MaxElementsPerWindow);
        try
        {
            var root = AutomationElement.FromHandle(window);
            // Breadth-first with hard caps — never unbounded recursion on huge trees.
            var queue = new Queue<(AutomationElement Element, int Depth)>();
            queue.Enqueue((root, 0));
            var visited = 0;
            const int maxVisited = 250;

            while (queue.Count > 0 && collected.Count < MaxElementsPerWindow && visited < maxVisited)
            {
                var (element, depth) = queue.Dequeue();
                visited++;
                if (depth >= MaxElementDepth) continue;

                AutomationElement? child;
                try { child = TreeWalker.ControlViewWalker.GetFirstChild(element); }
                catch { continue; }

                while (child is not null && collected.Count < MaxElementsPerWindow && visited < maxVisited)
                {
                    AutomationElement? next = null;
                    try { next = TreeWalker.ControlViewWalker.GetNextSibling(child); } catch { }

                    try
                    {
                        var current = child.Current;
                        // Deliberately skip Name — it is one of the slowest UIA properties.
                        if (!current.IsOffscreen)
                        {
                            var bounds = current.BoundingRectangle;
                            if (!bounds.IsEmpty && bounds.Width >= 4 && bounds.Height >= 4 &&
                                bounds.IntersectsWith(windowBounds))
                            {
                                var clipped = Rect.Intersect(bounds, windowBounds);
                                if (!clipped.IsEmpty && clipped.Width >= 3 && clipped.Height >= 3 &&
                                    !NearlyEqual(clipped, windowBounds))
                                {
                                    collected.Add(new CachedRegion(
                                        clipped,
                                        ShortControlType(current.ControlType),
                                        true,
                                        zOrder,
                                        depth + 1,
                                        window));
                                }

                                if (depth + 1 < MaxElementDepth)
                                    queue.Enqueue((child, depth + 1));
                            }
                        }
                    }
                    catch { }

                    child = next;
                    visited++;
                }
            }
        }
        catch { }

        lock (Sync) ElementsByWindow[window] = collected;
    }

    private static void EnsureWindows(IntPtr excludedWindow)
    {
        lock (Sync)
        {
            if (_windows.Count > 0 && _excludedHwnd == excludedWindow) return;
        }
        RebuildWindowCache(excludedWindow);
    }

    private static CachedRegion? HitTestWindow(Point screenPoint)
    {
        List<CachedRegion> windows;
        lock (Sync) windows = _windows;
        CachedRegion? top = null;
        foreach (var region in windows)
        {
            if (!region.Bounds.Contains(screenPoint)) continue;
            if (top is null || region.ZOrder < top.ZOrder)
                top = region;
        }
        return top;
    }

    private static bool IsEligibleWindow(IntPtr hwnd, IntPtr excludedOverlayHwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == excludedOverlayHwnd) return false;
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == Environment.ProcessId) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width < 8 || rect.Height < 8)
            return false;

        var className = new StringBuilder(96);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        var name = className.ToString();
        if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or
            "Windows.UI.Core.CoreWindow")
            return false;

        if (IsCloaked(hwnd)) return false;
        return true;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            if (NativeMethods.DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0)
                return cloaked != 0;
        }
        catch { }
        return false;
    }

    private static Rect WindowRect(IntPtr hwnd) =>
        NativeMethods.GetWindowRect(hwnd, out var rect)
            ? new Rect(rect.Left, rect.Top, Math.Max(0, rect.Width), Math.Max(0, rect.Height))
            : Rect.Empty;

    private static string WindowTitle(IntPtr hwnd)
    {
        try
        {
            var length = NativeMethods.GetWindowTextLength(hwnd);
            if (length <= 0) return "Window";
            var buffer = new StringBuilder(Math.Min(length + 1, 96));
            if (NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity) > 0 &&
                !string.IsNullOrWhiteSpace(buffer.ToString()))
            {
                var title = buffer.ToString().Trim();
                return title.Length > 40 ? title[..37] + "..." : title;
            }
        }
        catch { }
        return "Window";
    }

    private static string ShortControlType(ControlType? type)
    {
        if (type is null) return "UI element";
        var name = type.ProgrammaticName;
        if (string.IsNullOrWhiteSpace(name)) return "UI element";
        return name.Replace("ControlType.", string.Empty, StringComparison.Ordinal);
    }

    private static bool NearlyEqual(Rect first, Rect second) =>
        Math.Abs(first.X - second.X) < 2 && Math.Abs(first.Y - second.Y) < 2 &&
        Math.Abs(first.Width - second.Width) < 2 && Math.Abs(first.Height - second.Height) < 2;
}
