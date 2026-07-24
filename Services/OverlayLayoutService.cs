using System.Windows;

namespace SnapAnchor.Services;

internal static class OverlayLayoutService
{
    /// <summary>
    /// Places a panel just under the selection. Small default gap keeps the
    /// capture/annotation toolbar visually attached to the selection chrome.
    /// </summary>
    public static Point PlaceBelowAndKeepVisible(Rect anchor, Size panel, Size viewport, double edgeMargin = 8, double preferredGap = 2)
    {
        var width = Math.Min(Math.Max(1, panel.Width), Math.Max(1, viewport.Width - edgeMargin * 2));
        var height = Math.Min(Math.Max(1, panel.Height), Math.Max(1, viewport.Height - edgeMargin * 2));
        var left = Math.Clamp(anchor.Right - width, edgeMargin, Math.Max(edgeMargin, viewport.Width - width - edgeMargin));
        // Prefer snug attachment under the selection; only clamp if the bar would leave the viewport.
        var naturalTop = anchor.Bottom + preferredGap;
        var top = Math.Clamp(naturalTop, edgeMargin, Math.Max(edgeMargin, viewport.Height - height - edgeMargin));
        return new Point(left, top);
    }
}
