using SnapAnchor.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace SnapAnchor.Controls;

public partial class AnnotationEditorControl
{
    internal void ConfigureCaptureOverlay(Rect surfaceBounds, Size viewport, Rect? toolbarAnchor = null, bool showActions = false,
        double? toolbarLeft = null, double? toolbarTop = null, bool showPrimaryToolbar = true, bool showCancelAction = false,
        bool startWithNoTool = false, bool allowToolToggleOff = false)
    {
        _captureOverlayMode = true;
        _captureSurfaceBounds = surfaceBounds;
        _captureToolbarAnchor = toolbarAnchor ?? surfaceBounds;
        _captureViewport = viewport;
        _captureToolbarLeft = toolbarLeft;
        _captureToolbarTop = toolbarTop;
        Width = Math.Max(1, viewport.Width);
        Height = Math.Max(1, viewport.Height);
        ActionSeparator.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        CopyActionButton.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        SaveActionButton.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        ApplyActionButton.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        CancelActionButton.Visibility = showCancelAction ? Visibility.Visible : Visibility.Collapsed;
        PrimaryToolbarFrame.Visibility = showPrimaryToolbar ? Visibility.Visible : Visibility.Collapsed;
        _allowToolToggleOff = allowToolToggleOff;
        Grid.SetRowSpan(SurfaceHost, 2);
        SurfaceHost.BorderThickness = new Thickness(0);
        SurfaceHost.HorizontalAlignment = HorizontalAlignment.Left;
        SurfaceHost.VerticalAlignment = VerticalAlignment.Top;
        if (startWithNoTool)
            DeactivateTool();
        else if (FirstVisibleToolButton() is { } firstButton)
            ActivateTool(firstButton.Tag as string ?? "Rectangle", firstButton);
        Dispatcher.BeginInvoke(LayoutCaptureOverlay);
    }

    internal void UpdateCaptureToolbarPosition(double left, double top)
    {
        if (!_captureOverlayMode) return;
        _captureToolbarLeft = left;
        _captureToolbarTop = top;
        Dispatcher.BeginInvoke(LayoutCaptureOverlay);
    }

    internal void SetCapturePropertiesToolbarVisible(bool visible)
    {
        if (!_captureOverlayMode) return;
        PropertiesToolbarFrame.Visibility = visible && _tool is not ("Select" or "None")
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (visible) Dispatcher.BeginInvoke(LayoutCaptureOverlay);
    }

    internal void UndoForCapture() => Undo();
    internal void RedoForCapture() => Redo();

    internal void UpdateCaptureOverlayBounds(Rect surfaceBounds, Size viewport, Rect? toolbarAnchor = null,
        bool resetToolbarPosition = false)
    {
        if (!_captureOverlayMode) return;
        _captureSurfaceBounds = surfaceBounds;
        if (toolbarAnchor is { } anchor) _captureToolbarAnchor = anchor;
        if (resetToolbarPosition)
        {
            _captureToolbarLeft = null;
            _captureToolbarTop = null;
        }
        _captureViewport = viewport;
        Width = Math.Max(1, viewport.Width);
        Height = Math.Max(1, viewport.Height);
        Dispatcher.BeginInvoke(LayoutCaptureOverlay);
    }

    internal void TranslateCaptureOverlay(Vector delta)
    {
        if (!_captureOverlayMode || delta.LengthSquared < 0.0001) return;
        _captureSurfaceBounds.Offset(delta.X, delta.Y);
        _captureToolbarAnchor.Offset(delta.X, delta.Y);
        if (_captureToolbarLeft is { } left) _captureToolbarLeft = left + delta.X;
        if (_captureToolbarTop is { } top) _captureToolbarTop = top + delta.Y;
        Dispatcher.BeginInvoke(LayoutCaptureOverlay);
    }

    private void LayoutCaptureOverlay()
    {
        if (!_captureOverlayMode || _captureViewport.Width <= 1 || _captureViewport.Height <= 1) return;
        var bounds = Rect.Intersect(new Rect(new Point(), _captureViewport), _captureSurfaceBounds);
        if (bounds.IsEmpty) return;
        SurfaceHost.Width = Math.Max(1, bounds.Width);
        SurfaceHost.Height = Math.Max(1, bounds.Height);
        SurfaceHost.Margin = new Thickness(bounds.X, bounds.Y, 0, 0);

        var availableWidth = Math.Max(240, _captureViewport.Width - 16);
        ToolbarHost.Width = double.NaN;
        ToolbarHost.Measure(new Size(availableWidth, double.PositiveInfinity));
        var toolbarWidth = Math.Min(availableWidth, Math.Ceiling(ToolbarHost.DesiredSize.Width));
        ToolbarHost.Width = toolbarWidth;
        ToolbarHost.HorizontalAlignment = HorizontalAlignment.Left;
        ToolbarHost.Margin = new Thickness(0);
        ToolbarHost.RenderTransform = Transform.Identity;
        ToolbarHost.Measure(new Size(toolbarWidth, double.PositiveInfinity));
        UpdateLayout();
        var toolbarHeight = Math.Max(1, ToolbarHost.ActualHeight);
        var anchor = _captureToolbarAnchor.IsEmpty ? bounds : _captureToolbarAnchor;
        var placement = OverlayLayoutService.PlaceBelowAndKeepVisible(anchor, new Size(toolbarWidth, toolbarHeight), _captureViewport, 5);
        var maximumLeft = Math.Max(5, _captureViewport.Width - toolbarWidth - 5);
        var stableLeft = Math.Clamp(_captureToolbarLeft ?? placement.X, 5, maximumLeft);
        var maximumTop = Math.Max(5, _captureViewport.Height - toolbarHeight - 5);
        var stableTop = Math.Clamp(_captureToolbarTop ?? placement.Y, 5, maximumTop);
        (stableLeft, stableTop) = ClampOverlayToolbarToSingleMonitor(
            stableLeft, stableTop, toolbarWidth, toolbarHeight, anchor);
        _captureToolbarLeft = stableLeft;
        _captureToolbarTop = stableTop;
        ToolbarHost.Margin = new Thickness(0);
        UpdateLayout();
        var naturalPosition = ToolbarHost.TranslatePoint(new Point(0, 0), this);
        ToolbarHost.RenderTransform = new TranslateTransform(
            stableLeft - naturalPosition.X,
            stableTop - naturalPosition.Y);
    }

    private (double Left, double Top) ClampOverlayToolbarToSingleMonitor(
        double left, double top, double width, double height, Rect anchor)
    {
        var host = Window.GetWindow(this);
        if (host is null) return (left, top);
        var handle = new WindowInteropHelper(host).Handle;
        if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out var overlay) ||
            overlay.Width < 1 || overlay.Height < 1) return (left, top);

        var hostWidth = Math.Max(1.0, ActualWidth > 1 ? ActualWidth : host.ActualWidth);
        var hostHeight = Math.Max(1.0, ActualHeight > 1 ? ActualHeight : host.ActualHeight);

        Point ToPhysical(double x, double y) => new(
            overlay.Left + x * overlay.Width / hostWidth,
            overlay.Top + y * overlay.Height / hostHeight);

        var panelTl = ToPhysical(left, top);
        var panelBr = ToPhysical(left + width, top + height);
        var panel = new System.Drawing.Rectangle(
            (int)Math.Round(Math.Min(panelTl.X, panelBr.X)),
            (int)Math.Round(Math.Min(panelTl.Y, panelBr.Y)),
            Math.Max(1, (int)Math.Round(Math.Abs(panelBr.X - panelTl.X))),
            Math.Max(1, (int)Math.Round(Math.Abs(panelBr.Y - panelTl.Y))));

        var aTl = ToPhysical(anchor.Left, anchor.Top);
        var aBr = ToPhysical(anchor.Right, anchor.Bottom);
        var selection = new System.Drawing.Rectangle(
            (int)Math.Round(Math.Min(aTl.X, aBr.X)),
            (int)Math.Round(Math.Min(aTl.Y, aBr.Y)),
            Math.Max(1, (int)Math.Round(Math.Abs(aBr.X - aTl.X))),
            Math.Max(1, (int)Math.Round(Math.Abs(aBr.Y - aTl.Y))));

        var clamped = DisplayTopologyService.ClampPanelToSingleMonitor(panel, selection);
        var logicalLeft = (clamped.Left - overlay.Left) * hostWidth / overlay.Width;
        var logicalTop = (clamped.Top - overlay.Top) * hostHeight / overlay.Height;
        return (
            Math.Clamp(logicalLeft, 5, Math.Max(5, hostWidth - width - 5)),
            Math.Clamp(logicalTop, 5, Math.Max(5, hostHeight - height - 5)));
    }
}
