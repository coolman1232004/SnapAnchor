using SnapAnchor.Controls;
using SnapAnchor.Models;
using SnapAnchor.Services;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SnapAnchor.Windows;

/// <summary>
/// A transparent, virtual-desktop overlay that keeps the original pin visible
/// while hosting the same annotation toolbar used by region capture.
/// Spans the full physical virtual desktop so mixed-DPI multi-monitor drag
/// does not thrash between single-monitor windows.
/// </summary>
internal sealed class PinnedAnnotationOverlayWindow : Window
{
    private readonly AnnotationEditorControl _editor;
    private readonly DispatcherTimer _placementTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private int _placementPasses;
    private bool _draggingPin;

    internal event Action<AnnotationAppliedEventArgs>? Applied;
    internal event Action<AnnotationAppliedEventArgs>? DocumentStored;

    internal PinnedAnnotationOverlayWindow(
        BitmapSource source,
        IEnumerable<AnnotationItem>? items,
        Rect pinScreenBounds)
    {
        Title = "SnapAnchor Pin toolbar";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Logical seed; physical span is applied in SourceInitialized.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        SourceInitialized += (_, _) =>
        {
            FitToVirtualDesktopPixels();
            BeginPlacementStabilization();
        };
        PreviewMouseWheel += Overlay_PreviewMouseWheel;

        _editor = new AnnotationEditorControl();
        // Keep one almost-transparent hit-test layer alive until the monitor
        // DPI transition settles; a fully transparent layered window lets
        // pointer input fall through to the pin beneath it.
        _editor.Opacity = 0.01;
        Content = _editor;
        _editor.LoadImage(source, items);
        _editor.SetExternalBackgroundMode(true);

        var relativePinBounds = new Rect(
            pinScreenBounds.Left - Left,
            pinScreenBounds.Top - Top,
            pinScreenBounds.Width,
            pinScreenBounds.Height);
        _editor.ConfigureCaptureOverlay(
            relativePinBounds,
            new Size(Width, Height),
            relativePinBounds,
            showActions: true,
            showPrimaryToolbar: true,
            showCancelAction: true,
            startWithNoTool: true,
            allowToolToggleOff: true);

        _editor.ExternalSurfaceMoved += requestedDelta =>
        {
            if (Owner is not PinnedImageWindow pin) return;
            _draggingPin = true;
            // Move the pin in physical pixels (mixed-DPI safe). Overlay surface
            // is re-aligned from HWND rects after the move — no dual-DPI vector math.
            pin.MoveFromAnnotationOverlayPhysical(requestedDelta, VisualTreeHelper.GetDpi(this));
            AlignEditorToOwnerPin(resetToolbar: false);
        };
        _editor.ExternalSurfaceMoveCompleted += () =>
        {
            if (Owner is not PinnedImageWindow pin) return;
            pin.CompleteAnnotationOverlayMove();
            _draggingPin = false;
            BeginPlacementStabilization();
        };
        _editor.ExternalSurfaceDoubleClicked += () =>
        {
            // Match bare-pin double-click close when the toolbar is open.
            if (Owner is PinnedImageWindow pin)
            {
                pin.Close();
                return;
            }
            Close();
        };

        _editor.Applied += document =>
        {
            Applied?.Invoke(document);
            Close();
        };
        _editor.DocumentStored += document => DocumentStored?.Invoke(document);
        _editor.Cancelled += (_, _) => Close();
        _placementTimer.Tick += PlacementTimer_Tick;
        DpiChanged += (_, _) =>
        {
            if (_draggingPin) return;
            Dispatcher.BeginInvoke(BeginPlacementStabilization, DispatcherPriority.Render);
        };
        Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            FitToVirtualDesktopPixels();
            BeginPlacementStabilization();
            Activate();
            _editor.Focus();
        }, DispatcherPriority.ContextIdle);
        ContentRendered += (_, _) =>
        {
            if (!_draggingPin)
                Dispatcher.BeginInvoke(BeginPlacementStabilization, DispatcherPriority.ContextIdle);
        };
        Closed += (_, _) => _placementTimer.Stop();
    }

    private void Overlay_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Owner is not PinnedImageWindow pin) return;
        var pinHandle = new WindowInteropHelper(pin).Handle;
        var cursor = new NativeMethods.CursorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.CursorInfo>()
        };
        if (pinHandle == IntPtr.Zero || !NativeMethods.GetWindowRect(pinHandle, out var bounds) ||
            !NativeMethods.GetCursorInfo(ref cursor)) return;
        var point = cursor.ScreenPosition;
        if (point.X < bounds.Left || point.X >= bounds.Right || point.Y < bounds.Top || point.Y >= bounds.Bottom)
            return;

        pin.HandleAnnotationOverlayWheel(e.Delta, Keyboard.Modifiers);
        AlignEditorToOwnerPin(resetToolbar: false);
        e.Handled = true;
    }

    private void BeginPlacementStabilization()
    {
        _placementPasses = 8;
        FitToVirtualDesktopPixels();
        AlignEditorToOwnerPin(resetToolbar: true);
        _placementTimer.Start();
    }

    private void PlacementTimer_Tick(object? sender, EventArgs e)
    {
        if (_draggingPin)
        {
            AlignEditorToOwnerPin(resetToolbar: false);
            return;
        }

        FitToVirtualDesktopPixels();
        AlignEditorToOwnerPin(resetToolbar: true);
        _placementPasses--;
        if (_placementPasses > 0) return;
        _placementTimer.Stop();
    }

    private void AlignEditorToOwnerPin(bool resetToolbar)
    {
        if (Owner is not PinnedImageWindow pin) return;
        var overlayHandle = new WindowInteropHelper(this).Handle;
        var pinHandle = new WindowInteropHelper(pin).Handle;
        if (overlayHandle == IntPtr.Zero || pinHandle == IntPtr.Zero ||
            !NativeMethods.GetWindowRect(overlayHandle, out var overlayPixels) ||
            !NativeMethods.GetWindowRect(pinHandle, out var pinPixels) ||
            overlayPixels.Width < 1 || overlayPixels.Height < 1) return;

        // Map pin physical rect into overlay client space via linear HWND mapping
        // (same approach as capture multi-monitor detection).
        var logicalPin = new Rect(
            (pinPixels.Left - overlayPixels.Left) * (ActualWidth > 1 ? ActualWidth : Width) / overlayPixels.Width,
            (pinPixels.Top - overlayPixels.Top) * (ActualHeight > 1 ? ActualHeight : Height) / overlayPixels.Height,
            Math.Max(1, pinPixels.Width * (ActualWidth > 1 ? ActualWidth : Width) / (double)overlayPixels.Width),
            Math.Max(1, pinPixels.Height * (ActualHeight > 1 ? ActualHeight : Height) / (double)overlayPixels.Height));

        var overlayViewport = new Size(
            Math.Max(1, ActualWidth > 1 ? ActualWidth : Width),
            Math.Max(1, ActualHeight > 1 ? ActualHeight : Height));

        _editor.UpdateCaptureOverlayBounds(
            logicalPin,
            overlayViewport,
            logicalPin,
            resetToolbarPosition: resetToolbar);
        if (_placementPasses <= 6 || _editor.Opacity < 1) _editor.Opacity = 1;
    }

    /// <summary>
    /// Cover the full physical virtual desktop — never a single owner monitor —
    /// so dragging a pin between mixed-DPI displays does not resize/recreate the overlay.
    /// </summary>
    private void FitToVirtualDesktopPixels()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var bounds = DisplayTopologyService.VirtualBoundsPixels();
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    /// <summary>Maps a physical pin rect into overlay client space (uniform scale).</summary>
    internal static Rect PhysicalBoundsToOverlay(
        NativeMethods.NativeRect target,
        NativeMethods.NativeRect overlay,
        double overlayScaleX,
        double overlayScaleY)
    {
        var scaleX = Math.Max(0.1, overlayScaleX);
        var scaleY = Math.Max(0.1, overlayScaleY);
        return new Rect(
            (target.Left - overlay.Left) / scaleX,
            (target.Top - overlay.Top) / scaleY,
            Math.Max(1, target.Width / scaleX),
            Math.Max(1, target.Height / scaleY));
    }
}
