using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace SnapAnchor.Services;

internal sealed record DetectedScreenRegion(Rect Bounds, string Name, bool IsElement);

/// <summary>
/// Resolves the window and UI Automation element under the pointer for capture
/// hover outlines. Uses public Windows APIs only (WindowFromPoint, UI Automation).
/// Behaviour is inspired by common screenshot tools; the implementation is independent.
/// </summary>
internal static class ElementDetectionService
{
    private static readonly object CacheSync = new();
    private static ElementSnapshot? _snapshot;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1.25);

    private sealed record CachedElement(Rect Bounds, string Name, int ParentIndex, int Depth);
    private sealed record ElementSnapshot(IntPtr Window, Rect WindowBounds, DateTime CreatedUtc, IReadOnlyList<CachedElement> Elements);

    public static DetectedScreenRegion? Detect(Point screenPoint, IntPtr excludedWindow)
        => DetectHierarchy(screenPoint, excludedWindow).LastOrDefault();

    public static IntPtr WindowHandleAt(Point screenPoint, IntPtr excludedWindow)
        => FindWindow(screenPoint, excludedWindow);

    public static IReadOnlyList<DetectedScreenRegion> DetectHierarchy(Point screenPoint, IntPtr excludedWindow)
    {
        var window = FindWindow(screenPoint, excludedWindow);
        if (window == IntPtr.Zero) return [];
        var windowRect = Rectangle(window);
        if (windowRect.IsEmpty) return [];
        var regions = new List<DetectedScreenRegion> { new(windowRect, WindowLabel(window), false) };

        try
        {
            // Prefer the live element under the cursor (fast, accurate on modern
            // apps). Fall back to a short parent/child walk of a cached tree.
            if (TryElementFromPoint(screenPoint, windowRect, out var livePath))
            {
                foreach (var element in livePath)
                {
                    if (!element.Bounds.Contains(screenPoint) || regions.Any(region => NearlyEqual(region.Bounds, element.Bounds)))
                        continue;
                    regions.Add(new DetectedScreenRegion(element.Bounds, element.Name, true));
                }
            }
            else
            {
                var snapshot = SnapshotFor(window, windowRect);
                var candidateIndex = snapshot.Elements
                    .Select((element, index) => (Element: element, Index: index))
                    .Where(item => item.Element.Bounds.Contains(screenPoint))
                    .OrderByDescending(item => item.Element.Depth)
                    .ThenBy(item => item.Element.Bounds.Width * item.Element.Bounds.Height)
                    .Select(item => item.Index)
                    .FirstOrDefault(-1);

                if (candidateIndex >= 0)
                {
                    var path = new Stack<CachedElement>();
                    while (candidateIndex >= 0 && candidateIndex < snapshot.Elements.Count)
                    {
                        var element = snapshot.Elements[candidateIndex];
                        path.Push(element);
                        candidateIndex = element.ParentIndex;
                    }
                    foreach (var element in path)
                    {
                        if (!element.Bounds.Contains(screenPoint) || regions.Any(region => NearlyEqual(region.Bounds, element.Bounds)))
                            continue;
                        regions.Add(new DetectedScreenRegion(element.Bounds, element.Name, true));
                    }
                }
            }
        }
        catch
        {
            // Elevated or custom-rendered windows may not expose UI Automation.
            // The top-level window outline is still returned above.
        }

        return regions;
    }

    public static DetectedScreenRegion? TopExternalWindow()
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsEligible(hwnd, IntPtr.Zero)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return null;
        var rect = Rectangle(found);
        return rect.IsEmpty ? null : new DetectedScreenRegion(rect, WindowLabel(found), false);
    }

    private static bool TryElementFromPoint(Point screenPoint, Rect windowBounds, out List<CachedElement> path)
    {
        path = [];
        try
        {
            var element = AutomationElement.FromPoint(screenPoint);
            if (element is null) return false;

            var chain = new List<CachedElement>();
            var current = element;
            var depth = 0;
            while (current is not null && depth < 16)
            {
                Rect bounds;
                string name;
                try
                {
                    bounds = current.Current.BoundingRectangle;
                    name = current.Current.Name;
                    if (current.Current.ControlType == ControlType.Window && chain.Count > 0)
                        break;
                }
                catch
                {
                    break;
                }

                if (!bounds.IsEmpty && bounds.Width >= 2 && bounds.Height >= 2 &&
                    bounds.IntersectsWith(windowBounds) && bounds.Width <= windowBounds.Width + 4 &&
                    bounds.Height <= windowBounds.Height + 4)
                {
                    chain.Add(new CachedElement(
                        bounds,
                        string.IsNullOrWhiteSpace(name) ? ControlLabel(current) : name.Trim(),
                        -1,
                        depth));
                }

                try { current = TreeWalker.ControlViewWalker.GetParent(current); }
                catch { break; }
                depth++;
            }

            // Root → leaf order for Tab/wheel cycling (window already separate).
            chain.Reverse();
            // Drop a near-duplicate of the whole window.
            path = chain
                .Where(item => !NearlyEqual(item.Bounds, windowBounds))
                .GroupBy(item => $"{item.Bounds.X:0},{item.Bounds.Y:0},{item.Bounds.Width:0},{item.Bounds.Height:0}")
                .Select(group => group.First())
                .ToList();
            return path.Count > 0;
        }
        catch
        {
            path = [];
            return false;
        }
    }

    private static string ControlLabel(AutomationElement element)
    {
        try
        {
            var type = element.Current.ControlType?.ProgrammaticName;
            if (!string.IsNullOrWhiteSpace(type))
            {
                var shortName = type.Replace("ControlType.", string.Empty, StringComparison.Ordinal);
                if (!string.IsNullOrWhiteSpace(shortName)) return shortName;
            }
        }
        catch { }
        return "UI element";
    }

    private static string WindowLabel(IntPtr hwnd)
    {
        try
        {
            var length = NativeMethods.GetWindowTextLength(hwnd);
            if (length > 0)
            {
                var buffer = new StringBuilder(length + 1);
                if (NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity) > 0 &&
                    !string.IsNullOrWhiteSpace(buffer.ToString()))
                    return buffer.ToString().Trim();
            }
        }
        catch { }
        return "Window";
    }

    private static IntPtr FindWindow(Point point, IntPtr excludedWindow)
    {
        // WindowFromPoint sees through our full-screen capture overlay when the
        // overlay is excluded by process filter on the ancestor chain.
        var native = new NativeMethods.NativePoint
        {
            X = (int)Math.Round(point.X),
            Y = (int)Math.Round(point.Y)
        };
        var hit = NativeMethods.WindowFromPoint(native);
        if (hit != IntPtr.Zero)
        {
            var root = NativeMethods.GetAncestor(hit, NativeMethods.GaRoot);
            if (root == IntPtr.Zero) root = hit;
            if (IsEligible(root, excludedWindow) && Rectangle(root).Contains(point))
                return root;
        }

        // Fall back to Z-order enumeration (covers edge cases where WindowFromPoint
        // returns a non-client or owned helper window we cannot use).
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsEligible(hwnd, excludedWindow)) return true;
            var rect = Rectangle(hwnd);
            if (!rect.Contains(point)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsEligible(IntPtr hwnd, IntPtr excludedWindow)
    {
        if (hwnd == IntPtr.Zero || hwnd == excludedWindow || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == Environment.ProcessId) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width < 8 || rect.Height < 8) return false;
        var className = new StringBuilder(128);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return className.ToString() is not ("Shell_TrayWnd" or "Progman" or "WorkerW" or "Shell_SecondaryTrayWnd");
    }

    private static Rect Rectangle(IntPtr hwnd) =>
        NativeMethods.GetWindowRect(hwnd, out var rect)
            ? new Rect(rect.Left, rect.Top, Math.Max(0, rect.Width), Math.Max(0, rect.Height))
            : Rect.Empty;

    private static ElementSnapshot SnapshotFor(IntPtr window, Rect windowRect)
    {
        lock (CacheSync)
        {
            if (_snapshot is { } existing && existing.Window == window &&
                DateTime.UtcNow - existing.CreatedUtc <= CacheLifetime && NearlyEqual(existing.WindowBounds, windowRect))
                return existing;
        }

        var elements = new List<CachedElement>();
        try
        {
            var root = AutomationElement.FromHandle(window);
            BuildSnapshot(root, windowRect, -1, 0, elements);
        }
        catch { }

        var created = new ElementSnapshot(window, windowRect, DateTime.UtcNow, elements);
        lock (CacheSync) _snapshot = created;
        return created;
    }

    private static void BuildSnapshot(AutomationElement parent, Rect windowBounds, int parentIndex, int depth,
        List<CachedElement> destination)
    {
        if (depth >= 12 || destination.Count >= 1800) return;
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? child;
        try { child = walker.GetFirstChild(parent); }
        catch { return; }

        while (child is not null && destination.Count < 1800)
        {
            var next = default(AutomationElement);
            try { next = walker.GetNextSibling(child); } catch { }
            try
            {
                var bounds = child.Current.BoundingRectangle;
                if (!bounds.IsEmpty && bounds.Width >= 4 && bounds.Height >= 4 && bounds.IntersectsWith(windowBounds) &&
                    !child.Current.IsOffscreen)
                {
                    var name = child.Current.Name;
                    var currentIndex = destination.Count;
                    destination.Add(new CachedElement(bounds,
                        string.IsNullOrWhiteSpace(name) ? ControlLabel(child) : name.Trim(), parentIndex, depth + 1));
                    BuildSnapshot(child, windowBounds, currentIndex, depth + 1, destination);
                }
            }
            catch
            {
                // A provider may disappear while its window is changing.
            }
            child = next;
        }
    }

    private static bool NearlyEqual(Rect first, Rect second) =>
        Math.Abs(first.X - second.X) < 1 && Math.Abs(first.Y - second.Y) < 1 &&
        Math.Abs(first.Width - second.Width) < 1 && Math.Abs(first.Height - second.Height) < 1;
}
