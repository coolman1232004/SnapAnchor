using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace SnapAnchor.Services;

internal sealed record DetectedScreenRegion(Rect Bounds, string Name, bool IsElement);

/// <summary>
/// Fast multi-monitor window / UI-element hover detection.
///
/// Design (Snow Shot–like, independent):
/// - Capture open: enumerate top-level window rects only (milliseconds).
/// - Mouse move: pure geometry (never call UIA on the UI thread).
/// - UI elements: lazy, one window at a time, on a dedicated STA worker thread
///   with hard budgets so the capture overlay stays responsive.
/// - Coordinates are always physical desktop pixels (GetWindowRect / GetCursorPos).
/// </summary>
internal static class ElementDetectionService
{
    private const int MaxElementsPerWindow = 64;
    private const int MaxElementDepth = 4;
    private const int MaxNodesVisited = 160;

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
    private static long _lastWindowRefreshTick;
    private static Thread? _worker;
    private static readonly ConcurrentQueue<Action> WorkerQueue = new();
    private static readonly AutoResetEvent WorkerSignal = new(false);

    public static DetectedScreenRegion? Detect(Point screenPoint, IntPtr excludedWindow)
        => DetectHierarchy(screenPoint, excludedWindow, includeElements: true).LastOrDefault();

    public static IntPtr WindowHandleAt(Point screenPoint, IntPtr excludedWindow)
    {
        EnsureWindows(excludedWindow, forceRefresh: false);
        return HitTestWindow(screenPoint)?.Window ?? IntPtr.Zero;
    }

    /// <summary>Instant window-only cache. UI-thread safe; no UI Automation.</summary>
    public static void RebuildWindowCache(IntPtr excludedOverlayHwnd)
    {
        var windows = EnumerateWindows(excludedOverlayHwnd);
        lock (Sync)
        {
            _excludedHwnd = excludedOverlayHwnd;
            _windows = windows;
            ElementsByWindow.Clear();
            ElementLoadStarted.Clear();
            _lastWindowRefreshTick = Environment.TickCount64;
        }
    }

    public static void RebuildCache(IntPtr excludedOverlayHwnd) => RebuildWindowCache(excludedOverlayHwnd);

    public static IReadOnlyList<DetectedScreenRegion> DetectHierarchy(
        Point screenPoint,
        IntPtr excludedWindow,
        bool includeElements = true)
    {
        EnsureWindows(excludedWindow, forceRefresh: false);
        var top = HitTestWindow(screenPoint);

        // Cursor outside every cached window (common after monitor switch / new app):
        // cheap refresh of top-level rects only.
        if (top is null)
        {
            var now = Environment.TickCount64;
            if (now - _lastWindowRefreshTick > 200)
            {
                RebuildWindowCache(excludedWindow);
                top = HitTestWindow(screenPoint);
            }
        }

        if (top is null) return Array.Empty<DetectedScreenRegion>();

        var hits = new List<CachedRegion>(12) { top };

        if (includeElements)
        {
            List<CachedRegion>? elements = null;
            var schedule = false;
            lock (Sync)
            {
                if (ElementsByWindow.TryGetValue(top.Window, out var ready))
                    elements = ready;
                else if (ElementLoadStarted.Add(top.Window))
                    schedule = true;
            }

            if (schedule)
                EnqueueElementLoad(top.Window, top.Bounds, top.ZOrder);

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

    private static List<CachedRegion> EnumerateWindows(IntPtr excludedOverlayHwnd)
    {
        var windows = new List<CachedRegion>(64);
        var zOrder = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsEligibleWindow(hwnd, excludedOverlayHwnd)) return true;
            var rect = WindowRect(hwnd);
            if (rect.IsEmpty || rect.Width < 8 || rect.Height < 8) return true;
            // Keep titles short; avoid long GetWindowText on every enum during refresh.
            windows.Add(new CachedRegion(rect, "Window", false, zOrder, 0, hwnd));
            zOrder++;
            return true;
        }, IntPtr.Zero);

        // Fill titles only for the first few topmost windows (display text).
        for (var i = 0; i < windows.Count && i < 12; i++)
        {
            var item = windows[i];
            windows[i] = item with { Name = WindowTitle(item.Window) };
        }
        return windows;
    }

    private static void EnqueueElementLoad(IntPtr window, Rect windowBounds, int zOrder)
    {
        EnsureWorker();
        WorkerQueue.Enqueue(() =>
        {
            try
            {
                var collected = LoadElementsForWindow(window, windowBounds, zOrder);
                lock (Sync) ElementsByWindow[window] = collected;
            }
            catch
            {
                lock (Sync) ElementsByWindow[window] = [];
            }
        });
        WorkerSignal.Set();
    }

    private static void EnsureWorker()
    {
        if (_worker is { IsAlive: true }) return;
        lock (Sync)
        {
            if (_worker is { IsAlive: true }) return;
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "SnapAnchor.ElementDetection",
                Priority = ThreadPriority.BelowNormal
            };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            WorkerSignal.WaitOne(500);
            while (WorkerQueue.TryDequeue(out var work))
            {
                try { work(); }
                catch { /* ignore */ }
            }
        }
    }

    private static List<CachedRegion> LoadElementsForWindow(IntPtr window, Rect windowBounds, int zOrder)
    {
        var collected = new List<CachedRegion>(MaxElementsPerWindow);
        try
        {
            var root = AutomationElement.FromHandle(window);
            var queue = new Queue<(AutomationElement Element, int Depth)>();
            queue.Enqueue((root, 0));
            var visited = 0;

            while (queue.Count > 0 && collected.Count < MaxElementsPerWindow && visited < MaxNodesVisited)
            {
                var (element, depth) = queue.Dequeue();
                visited++;
                if (depth >= MaxElementDepth) continue;

                AutomationElement? child;
                try { child = TreeWalker.ControlViewWalker.GetFirstChild(element); }
                catch { continue; }

                while (child is not null && collected.Count < MaxElementsPerWindow && visited < MaxNodesVisited)
                {
                    AutomationElement? next = null;
                    try { next = TreeWalker.ControlViewWalker.GetNextSibling(child); } catch { }

                    try
                    {
                        // Only cheap properties — never Name (very slow).
                        var bounds = child.Current.BoundingRectangle;
                        var offscreen = child.Current.IsOffscreen;
                        if (!offscreen && !bounds.IsEmpty && bounds.Width >= 6 && bounds.Height >= 6 &&
                            bounds.IntersectsWith(windowBounds))
                        {
                            var clipped = Rect.Intersect(bounds, windowBounds);
                            if (!clipped.IsEmpty && clipped.Width >= 4 && clipped.Height >= 4 &&
                                !NearlyEqual(clipped, windowBounds))
                            {
                                var controlType = child.Current.ControlType;
                                collected.Add(new CachedRegion(
                                    clipped,
                                    ShortControlType(controlType),
                                    true,
                                    zOrder,
                                    depth + 1,
                                    window));
                            }

                            if (depth + 1 < MaxElementDepth)
                                queue.Enqueue((child, depth + 1));
                        }
                    }
                    catch { }

                    child = next;
                    visited++;
                }
            }
        }
        catch { }

        return collected;
    }

    private static void EnsureWindows(IntPtr excludedWindow, bool forceRefresh)
    {
        lock (Sync)
        {
            if (!forceRefresh && _windows.Count > 0 && _excludedHwnd == excludedWindow)
                return;
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
            var buffer = new StringBuilder(Math.Min(length + 1, 64));
            if (NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity) > 0 &&
                !string.IsNullOrWhiteSpace(buffer.ToString()))
            {
                var title = buffer.ToString().Trim();
                return title.Length > 36 ? title[..33] + "..." : title;
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
