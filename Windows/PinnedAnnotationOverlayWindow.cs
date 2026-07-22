using SnapAnchor.Controls;
using SnapAnchor.Models;
using SnapAnchor.Services;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SnapAnchor.Windows;

/// <summary>
/// A transparent, virtual-desktop overlay that keeps the original pin visible
/// while hosting the same annotation toolbar used by region capture.
/// </summary>
internal sealed class PinnedAnnotationOverlayWindow : Window
{
    private readonly AnnotationEditorControl _editor;
    private readonly DispatcherTimer _placementTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private int _placementPasses;

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
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        SourceInitialized += (_, _) => BeginPlacementStabilization();

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
            var ownerDelta = ConvertDelta(requestedDelta, VisualTreeHelper.GetDpi(this), VisualTreeHelper.GetDpi(pin));
            var actualOwnerDelta = pin.MoveFromAnnotationOverlay(ownerDelta);
            var overlayDelta = ConvertDelta(actualOwnerDelta, VisualTreeHelper.GetDpi(pin), VisualTreeHelper.GetDpi(this));
            _editor.TranslateCaptureOverlay(overlayDelta);
        };
        _editor.ExternalSurfaceMoveCompleted += () =>
        {
            if (Owner is not PinnedImageWindow pin) return;
            pin.CompleteAnnotationOverlayMove();
            BeginPlacementStabilization();
        };

        _editor.Applied += document =>
        {
            Applied?.Invoke(document);
            Close();
        };
        _editor.DocumentStored += document => DocumentStored?.Invoke(document);
        _editor.Cancelled += (_, _) => Close();
        _placementTimer.Tick += PlacementTimer_Tick;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(BeginPlacementStabilization, DispatcherPriority.Render);
        Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            BeginPlacementStabilization();
            Activate();
            _editor.Focus();
        }, DispatcherPriority.ContextIdle);
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(BeginPlacementStabilization, DispatcherPriority.ContextIdle);
        Closed += (_, _) => _placementTimer.Stop();
    }

    private void BeginPlacementStabilization()
    {
        _placementPasses = 12;
        FitToOwnerMonitorPixels();
        AlignEditorToOwnerPin();
        _placementTimer.Start();
    }

    private void PlacementTimer_Tick(object? sender, EventArgs e)
    {
        FitToOwnerMonitorPixels();
        AlignEditorToOwnerPin();
        _placementPasses--;
        if (_placementPasses > 0) return;
        _placementTimer.Stop();
    }

    private void AlignEditorToOwnerPin()
    {
        if (Owner is not PinnedImageWindow pin) return;
        var overlayHandle = new WindowInteropHelper(this).Handle;
        var pinHandle = new WindowInteropHelper(pin).Handle;
        if (overlayHandle == IntPtr.Zero || pinHandle == IntPtr.Zero ||
            !NativeMethods.GetWindowRect(overlayHandle, out var overlayPixels) ||
            !NativeMethods.GetWindowRect(pinHandle, out var pinPixels)) return;

        var dpi = DpiLayoutService.WindowScale(this);
        var actualPinBounds = DpiLayoutService.PhysicalBoundsToLogical(this, pinPixels, overlayPixels);
        var overlayViewport = new Size(
            Math.Max(1, overlayPixels.Width / Math.Max(0.1, dpi.DpiScaleX)),
            Math.Max(1, overlayPixels.Height / Math.Max(0.1, dpi.DpiScaleY)));

        _editor.UpdateCaptureOverlayBounds(
            actualPinBounds,
            overlayViewport,
            actualPinBounds,
            resetToolbarPosition: true);
        if (_placementPasses <= 10) _editor.Opacity = 1;
    }

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

    private static Vector ConvertDelta(Vector delta, DpiScale from, DpiScale to)
        => new(
            delta.X * from.DpiScaleX / Math.Max(0.1, to.DpiScaleX),
            delta.Y * from.DpiScaleY / Math.Max(0.1, to.DpiScaleY));

    private void FitToOwnerMonitorPixels()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var ownerHandle = Owner is null ? IntPtr.Zero : new WindowInteropHelper(Owner).Handle;
        var bounds = ownerHandle == IntPtr.Zero
            ? DisplayTopologyService.VirtualBoundsPixels()
            : DisplayTopologyService.MonitorBoundsForWindowPixels(ownerHandle);
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }
}
