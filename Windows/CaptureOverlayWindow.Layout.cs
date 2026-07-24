using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SnapAnchor.Services;

namespace SnapAnchor.Windows;

/// <summary>
/// Selection chrome, action-bar placement, and overlay coordinate mapping.
/// </summary>
public partial class CaptureOverlayWindow
{
    private void PositionActionBar()
    {
        var originalVisibility = ActionBar.Visibility;
        if (originalVisibility == Visibility.Collapsed) ActionBar.Visibility = Visibility.Hidden;
        ActionBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = ActionBar.DesiredSize.Width;
        var height = ActionBar.DesiredSize.Height;
        var placement = OverlayLayoutService.PlaceBelowAndKeepVisible(
            _selection, new Size(width, height), new Size(ActualWidth, ActualHeight));
        // Keep the bar fully on one physical monitor so mixed-DPI seams do not
        // split the toolbar across laptop + external (unclickable half-bars).
        placement = ClampToolbarToSingleMonitor(placement, new Size(width, height), _selection);
        Canvas.SetLeft(ActionBar, placement.X);
        var reservedHeight = _annotationMode
            ? 90
            : OcrPanel.Visibility == Visibility.Visible ? OcrPanel.Height + 5 : 0;
        var maximumTop = Math.Max(8, ActualHeight - height - reservedHeight - 8);
        Canvas.SetTop(ActionBar, Math.Min(placement.Y, maximumTop));
        ActionBar.Visibility = originalVisibility;
        if (_annotationMode && CaptureInlineEditor.Visibility == Visibility.Visible)
        {
            var propertyTop = Canvas.GetTop(ActionBar) + Math.Max(1, height) + 2;
            CaptureInlineEditor.UpdateCaptureToolbarPosition(Canvas.GetLeft(ActionBar), propertyTop);
        }
    }

    /// <summary>
    /// Converts a logical overlay placement to physical pixels, clamps the panel
    /// onto one monitor that best covers the selection, then maps back.
    /// </summary>
    private Point ClampToolbarToSingleMonitor(Point logicalTopLeft, Size logicalSize, Rect selectionLogical)
    {
        var topLeft = OverlayToScreen(logicalTopLeft);
        var bottomRight = OverlayToScreen(new Point(
            logicalTopLeft.X + Math.Max(1, logicalSize.Width),
            logicalTopLeft.Y + Math.Max(1, logicalSize.Height)));
        var panelPhysical = new System.Drawing.Rectangle(
            (int)Math.Round(Math.Min(topLeft.X, bottomRight.X)),
            (int)Math.Round(Math.Min(topLeft.Y, bottomRight.Y)),
            Math.Max(1, (int)Math.Round(Math.Abs(bottomRight.X - topLeft.X))),
            Math.Max(1, (int)Math.Round(Math.Abs(bottomRight.Y - topLeft.Y))));

        var selTopLeft = OverlayToScreen(selectionLogical.TopLeft);
        var selBottomRight = OverlayToScreen(selectionLogical.BottomRight);
        var selectionPhysical = new System.Drawing.Rectangle(
            (int)Math.Round(Math.Min(selTopLeft.X, selBottomRight.X)),
            (int)Math.Round(Math.Min(selTopLeft.Y, selBottomRight.Y)),
            Math.Max(1, (int)Math.Round(Math.Abs(selBottomRight.X - selTopLeft.X))),
            Math.Max(1, (int)Math.Round(Math.Abs(selBottomRight.Y - selTopLeft.Y))));

        var clamped = DisplayTopologyService.ClampPanelToSingleMonitor(panelPhysical, selectionPhysical);
        return ScreenToOverlay(new Point(clamped.Left, clamped.Top));
    }

    private Rect ActionBarBounds()
    {
        ActionBar.UpdateLayout();
        var width = ActionBar.ActualWidth > 1 ? ActionBar.ActualWidth : ActionBar.DesiredSize.Width;
        var height = ActionBar.ActualHeight > 1 ? ActionBar.ActualHeight : ActionBar.DesiredSize.Height;
        return new Rect(Canvas.GetLeft(ActionBar), Canvas.GetTop(ActionBar), Math.Max(1, width), Math.Max(1, height));
    }

    private void PositionSelectionHandles()
    {
        SelectionHandles.Width = Math.Max(1, ActualWidth);
        SelectionHandles.Height = Math.Max(1, ActualHeight);
        const double radius = 6.5;
        var centerX = _selection.X + _selection.Width / 2;
        var centerY = _selection.Y + _selection.Height / 2;
        SetHandle(HandleNW, _selection.Left - radius, _selection.Top - radius);
        SetHandle(HandleN, centerX - radius, _selection.Top - radius);
        SetHandle(HandleNE, _selection.Right - radius, _selection.Top - radius);
        SetHandle(HandleE, _selection.Right - radius, centerY - radius);
        SetHandle(HandleSE, _selection.Right - radius, _selection.Bottom - radius);
        SetHandle(HandleS, centerX - radius, _selection.Bottom - radius);
        SetHandle(HandleSW, _selection.Left - radius, _selection.Bottom - radius);
        SetHandle(HandleW, _selection.Left - radius, centerY - radius);
    }

    private void PositionFloatingPanels()
    {
        ShortcutHints.Width = Math.Min(330, Math.Max(220, ActualWidth - 32));
        OcrPanel.Width = Math.Min(580, Math.Max(300, ActualWidth - 36));
        OcrPanel.Height = Math.Min(390, Math.Max(250, ActualHeight - 36));
        Canvas.SetLeft(ShortcutHints, 16);
        ShortcutHints.Measure(new Size(ShortcutHints.Width, double.PositiveInfinity));
        Canvas.SetTop(ShortcutHints, Math.Max(16, ActualHeight - ShortcutHints.DesiredSize.Height - 18));
        if (ActionBar.Visibility == Visibility.Visible && _selection.Width >= 3 && _selection.Height >= 3)
        {
            PositionActionBar();
            var placement = OverlayLayoutService.PlaceBelowAndKeepVisible(
                ActionBarBounds(), new Size(OcrPanel.Width, OcrPanel.Height), new Size(ActualWidth, ActualHeight), 5);
            Canvas.SetLeft(OcrPanel, placement.X);
            Canvas.SetTop(OcrPanel, placement.Y);
        }
        else
        {
            Canvas.SetLeft(OcrPanel, Math.Max(10, ActualWidth - OcrPanel.Width - 18));
            Canvas.SetTop(OcrPanel, Math.Max(10, (ActualHeight - OcrPanel.Height) / 2));
        }
        PositionSelectionHandles();
        if (_selection.Width >= 3 && _selection.Height >= 3) RenderSelectionMask();
    }

    private Int32Rect SelectedPixelRegion() => PixelRegion(_selection);

    private Int32Rect PixelRegion(Rect area)
    {
        var virtualBounds = DisplayTopologyService.VirtualBoundsPixels();
        var topLeft = OverlayToScreen(area.TopLeft);
        var bottomRight = OverlayToScreen(area.BottomRight);
        return CaptureCoordinateService.ToBitmapRegion(topLeft, bottomRight, virtualBounds, _screen.PixelWidth, _screen.PixelHeight);
    }

    private void FitToVirtualScreenPixels()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var bounds = DisplayTopologyService.VirtualBoundsPixels();
        if (handle != IntPtr.Zero)
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    /// <summary>
    /// Physical screen rectangle of this overlay HWND. Spanning multi-monitor
    /// overlays must not use WPF PointToScreen/PointFromScreen — those break
    /// when the window covers monitors with different DPI.
    /// </summary>
    private System.Drawing.Rectangle OverlayHwndScreenRect()
    {
        var handle = _overlayHandle != IntPtr.Zero
            ? _overlayHandle
            : new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && NativeMethods.GetWindowRect(handle, out var native) &&
            native.Width > 0 && native.Height > 0)
            return new System.Drawing.Rectangle(native.Left, native.Top, native.Width, native.Height);

        return DisplayTopologyService.VirtualBoundsPixels();
    }

    private Point OverlayToScreen(Point point) =>
        CaptureCoordinateService.OverlayToScreen(point, OverlayHwndScreenRect(), ActualWidth, ActualHeight);

    private Point ScreenToOverlay(Point point) =>
        CaptureCoordinateService.ScreenToOverlay(point, OverlayHwndScreenRect(), ActualWidth, ActualHeight);

    private IEnumerable<DependencyObject> CaptureToolbarHosts()
    {
        if (CaptureInlineEditor.Visibility == Visibility.Visible)
            yield return CaptureInlineEditor;
        yield return ActionBar;
    }
}
