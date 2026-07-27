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
/// Default path is window-only geometry (smooth, Snow Shot–like window snap).
/// Optional UI-element refine loads one window at a time on a background STA
/// worker with hard budgets — never on the UI thread.
/// </summary>
internal static class ElementDetectionService
{
    private const int MaxElementsPerWindow = 48;
    private const int MaxElementDepth = 4;
    private const int MaxNodesVisited = 120;
    internal const int WindowCacheLifetimeMs = 180;
    internal const int ElementCacheLifetimeMs = 1200;

    private sealed record CachedRegion(
        Rect Bounds,
        string Name,
        bool IsElement,
        int ZOrder,
        int Depth,
        IntPtr Window);

    private sealed record ElementCache(List<CachedRegion> Regions, long LoadedTick);

    private static readonly object Sync = new();
    private static List<CachedRegion> _windows = [];
    private static readonly Dictionary<IntPtr, ElementCache> ElementsByWindow = new();
    private static readonly HashSet<IntPtr> ElementLoadStarted = new();
    private static IntPtr _excludedHwnd;
    private static long _lastWindowRefreshTick;
    private static IntPtr _lastCursorMonitor;
    private static Thread? _worker;
    private static readonly ConcurrentQueue<Action> WorkerQueue = new();
    private static readonly AutoResetEvent WorkerSignal = new(false);

    public static DetectedScreenRegion? Detect(Point screenPoint, IntPtr excludedWindow)
        => DetectHierarchy(screenPoint, excludedWindow, includeElements: false).LastOrDefault();

    public static IntPtr WindowHandleAt(Point screenPoint, IntPtr excludedWindow)
    {
        EnsureWindows(excludedWindow);
        return HitTestWindow(screenPoint)?.Window ?? IntPtr.Zero;
    }

    /// <summary>Instant window-only cache. UI-thread safe; no UI Automation.</summary>
    public static void RebuildWindowCache(IntPtr excludedOverlayHwnd)
    {
        var windows = EnumerateWindows(excludedOverlayHwnd);
        lock (Sync)
        {
            var oldWindows = _windows.ToDictionary(window => window.Window);
            var liveHandles = windows.Select(window => window.Window).ToHashSet();
            foreach (var handle in ElementsByWindow.Keys.ToArray())
            {
                var moved = oldWindows.TryGetValue(handle, out var previous) &&
                            windows.FirstOrDefault(window => window.Window == handle) is { } current &&
                            !NearlyEqual(previous.Bounds, current.Bounds);
                if (!liveHandles.Contains(handle) || moved)
                {
                    ElementsByWindow.Remove(handle);
                    ElementLoadStarted.Remove(handle);
                }
            }
            ElementLoadStarted.RemoveWhere(handle => !liveHandles.Contains(handle));
            _excludedHwnd = excludedOverlayHwnd;
            _windows = windows;
            _lastWindowRefreshTick = Environment.TickCount64;
        }
    }

    public static void RebuildCache(IntPtr excludedOverlayHwnd) => RebuildWindowCache(excludedOverlayHwnd);

    public static IReadOnlyList<DetectedScreenRegion> DetectHierarchy(
        Point screenPoint,
        IntPtr excludedWindow,
        bool includeElements = false)
    {
        EnsureWindows(excludedWindow);

        // When the cursor crosses monitors, refresh window rects (cheap EnumWindows).
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.NativePoint { X = (int)Math.Round(screenPoint.X), Y = (int)Math.Round(screenPoint.Y) },
            NativeMethods.MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero && monitor != _lastCursorMonitor)
        {
            _lastCursorMonitor = monitor;
            var now = Environment.TickCount64;
            if (now - _lastWindowRefreshTick > 80)
                RebuildWindowCache(excludedWindow);
        }

        var top = HitTestWindow(screenPoint);
        if (top is null)
        {
            var now = Environment.TickCount64;
            if (now - _lastWindowRefreshTick > 150)
            {
                RebuildWindowCache(excludedWindow);
                top = HitTestWindow(screenPoint);
            }
        }

        if (top is null) return Array.Empty<DetectedScreenRegion>();

        var hits = new List<CachedRegion>(8) { top };

        if (includeElements)
        {
            List<CachedRegion>? elements = null;
            var schedule = false;
            lock (Sync)
            {
                if (ElementsByWindow.TryGetValue(top.Window, out var ready) &&
                    !IsCacheExpired(ready.LoadedTick, Environment.TickCount64, ElementCacheLifetimeMs))
                {
                    elements = ready.Regions;
                }
                else
                {
                    ElementsByWindow.Remove(top.Window);
                    if (ElementLoadStarted.Add(top.Window))
                        schedule = true;
                }
            }

            if (schedule)
                EnqueueElementLoad(top.Window, top.Bounds, top.ZOrder);

            if (elements is { Count: > 0 })
            {
                foreach (var region in elements)
                {
                    if (!ContainsInclusive(region.Bounds, screenPoint)) continue;
                    if (hits.Any(existing => NearlyEqual(existing.Bounds, region.Bounds))) continue;
                    hits.Add(region);
                }
            }
        }

        // Window first, then larger → smaller so deepest is last.
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
            windows.Add(new CachedRegion(rect, "Window", false, zOrder, 0, hwnd));
            zOrder++;
            return true;
        }, IntPtr.Zero);

        for (var i = 0; i < windows.Count && i < 16; i++)
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
                lock (Sync)
                {
                    if (_windows.FirstOrDefault(item => item.Window == window) is { } current &&
                        NearlyEqual(current.Bounds, windowBounds))
                        ElementsByWindow[window] = new ElementCache(collected, Environment.TickCount64);
                    ElementLoadStarted.Remove(window);
                }
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    if (_windows.FirstOrDefault(item => item.Window == window) is { } current &&
                        NearlyEqual(current.Bounds, windowBounds))
                        ElementsByWindow[window] = new ElementCache([], Environment.TickCount64);
                    ElementLoadStarted.Remove(window);
                }
                DiagnosticsService.Log("element-detection", ex.Message, ex);
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
                catch { }
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
                        var bounds = child.Current.BoundingRectangle;
                        var offscreen = child.Current.IsOffscreen;
                        if (!offscreen && !bounds.IsEmpty && bounds.Width >= 8 && bounds.Height >= 8 &&
                            bounds.IntersectsWith(windowBounds))
                        {
                            var clipped = Rect.Intersect(bounds, windowBounds);
                            if (!clipped.IsEmpty && clipped.Width >= 6 && clipped.Height >= 6 &&
                                !NearlyEqual(clipped, windowBounds))
                            {
                                collected.Add(new CachedRegion(
                                    clipped,
                                    ShortControlType(child.Current.ControlType),
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

    private static void EnsureWindows(IntPtr excludedWindow)
    {
        var refresh = false;
        lock (Sync)
        {
            refresh = _windows.Count == 0 ||
                      _excludedHwnd != excludedWindow ||
                      IsCacheExpired(_lastWindowRefreshTick, Environment.TickCount64, WindowCacheLifetimeMs);
        }
        if (refresh) RebuildWindowCache(excludedWindow);
    }

    internal static bool IsCacheExpired(long loadedTick, long nowTick, int lifetimeMs) =>
        loadedTick <= 0 || nowTick - loadedTick >= Math.Max(1, lifetimeMs);

    private static CachedRegion? HitTestWindow(Point screenPoint)
    {
        List<CachedRegion> windows;
        lock (Sync) windows = _windows;
        CachedRegion? top = null;
        foreach (var region in windows)
        {
            if (!ContainsInclusive(region.Bounds, screenPoint)) continue;
            if (top is null || region.ZOrder < top.ZOrder)
                top = region;
        }
        return top;
    }

    /// <summary>
    /// Prefer visible frame bounds (DWM) so maximized / multi-monitor windows
    /// match what the user sees; fall back to GetWindowRect.
    /// </summary>
    private static Rect WindowRect(IntPtr hwnd)
    {
        if (TryExtendedFrameBounds(hwnd, out var extended) && extended.Width >= 8 && extended.Height >= 8)
            return extended;
        return NativeMethods.GetWindowRect(hwnd, out var rect)
            ? new Rect(rect.Left, rect.Top, Math.Max(0, rect.Width), Math.Max(0, rect.Height))
            : Rect.Empty;
    }

    private static bool TryExtendedFrameBounds(IntPtr hwnd, out Rect bounds)
    {
        bounds = Rect.Empty;
        try
        {
            // DWMWA_EXTENDED_FRAME_BOUNDS = 9
            var native = new NativeMethods.NativeRect();
            if (NativeMethods.DwmGetWindowAttributeRect(hwnd, 9, ref native, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NativeRect>()) != 0)
                return false;
            bounds = new Rect(native.Left, native.Top, Math.Max(0, native.Width), Math.Max(0, native.Height));
            return !bounds.IsEmpty;
        }
        catch
        {
            return false;
        }
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

    private static bool ContainsInclusive(Rect bounds, Point point) =>
        point.X >= bounds.X - 1 &&
        point.X <= bounds.X + bounds.Width + 1 &&
        point.Y >= bounds.Y - 1 &&
        point.Y <= bounds.Y + bounds.Height + 1;

    private static bool NearlyEqual(Rect first, Rect second) =>
        Math.Abs(first.X - second.X) < 2 && Math.Abs(first.Y - second.Y) < 2 &&
        Math.Abs(first.Width - second.Width) < 2 && Math.Abs(first.Height - second.Height) < 2;
}
