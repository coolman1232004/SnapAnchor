using SnapAnchor.Services;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapAnchor.RecognitionSmoke;

internal static class CompatibilitySmoke
{
    internal static void Run(Func<int, int, BitmapSource> createPattern)
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 })
        {
            var logical = DpiLayoutService.AvailableLogicalSize(new Size(1920, 1080), scale, scale);
            if (logical.Width <= 0 || logical.Height <= 0 || logical.Width > 1920 || logical.Height > 1080)
                throw new InvalidOperationException($"Invalid logical work area at {scale:P0} scaling.");
        }

        var portrait = DpiLayoutService.AvailableLogicalSize(new Size(1080, 1920), 1.5, 1.5);
        var constrained = DpiLayoutService.AvailableLogicalSize(new Size(320, 240), 2, 2);
        if (portrait.Height <= portrait.Width || constrained.Width > 160 || constrained.Height > 120)
            throw new InvalidOperationException("Portrait or constrained-display sizing is invalid.");

        var nativeLogicalSize = DpiLayoutService.LogicalSizeForPhysicalPixels(1000, 625, 1.25, 1.25);
        var nativeZoom = DpiLayoutService.PhysicalZoomPercent(nativeLogicalSize.Width, 1.25, 1000);
        if (Math.Abs(nativeLogicalSize.Width - 800) > 0.01 || Math.Abs(nativeLogicalSize.Height - 500) > 0.01 || nativeZoom != 100)
            throw new InvalidOperationException("A native-size bitmap was not preserved at 125% display scaling.");

        if (PinScalingQuality.ModeFor(800, 1.25, 1000, animating: false) != BitmapScalingMode.NearestNeighbor ||
            PinScalingQuality.ModeFor(640, 1.25, 1000, animating: false) != BitmapScalingMode.LowQuality ||
            PinScalingQuality.ModeFor(640, 1.25, 1000, animating: true) != BitmapScalingMode.LowQuality)
            throw new InvalidOperationException("Pinned-image scaling did not preserve native pixels and sharp linearly sampled text.");

        if (PinScalingQuality.NextWheelZoomPercent(1000, 1000, 120) != 110 ||
            PinScalingQuality.NextWheelZoomPercent(1000, 1000, -120) != 90 ||
            PinScalingQuality.NextWheelZoomPercent(200, 1000, -120) != 20 ||
            PinScalingQuality.NextWheelZoomPercent(3000, 1000, 120) != 300 ||
            PinScalingQuality.PhysicalSizeForPercent(1001, 601, 110) != new System.Drawing.Size(1101, 661))
            throw new InvalidOperationException("Pinned-image physical zoom steps are not stable integer pixel sizes.");

        var virtualBounds = new System.Drawing.Rectangle(-1080, -1920, 3000, 3000);
        var portraitRegion = CaptureCoordinateService.ToBitmapRegion(
            new Point(-200, 500), new Point(-1000, -1800), virtualBounds, 3000, 3000);
        if (portraitRegion != new Int32Rect(80, 120, 800, 2300))
            throw new InvalidOperationException($"Negative-coordinate portrait mapping failed: {portraitRegion}");

        var clipped = CaptureCoordinateService.ToBitmapRegion(
            new Point(-5000, -5000), new Point(5000, 5000), virtualBounds, 3000, 3000);
        if (clipped != new Int32Rect(0, 0, 3000, 3000))
            throw new InvalidOperationException("Virtual-desktop clipping failed.");

        var edge = OverlayLayoutService.PlaceBelowAndKeepVisible(
            new Rect(300, 180, 20, 20), new Size(260, 82), new Size(320, 240));
        if (edge.X < 8 || edge.Y < 8 || edge.X + 260 > 312 || edge.Y + 82 > 232)
            throw new InvalidOperationException("Toolbar placement escaped a small display.");

        var devices = new[]
        {
            new RecordingDeviceOption(string.Empty, "System default"),
            new RecordingDeviceOption("usb-headset", "USB headset")
        };
        if (AdvancedRecordingSession.ResolveAudioDevice("usb-headset", devices) != "usb-headset" ||
            AdvancedRecordingSession.ResolveAudioDevice("removed-device", devices) is not null ||
            AdvancedRecordingSession.ResolveAudioDevice(string.Empty, devices) is not null)
            throw new InvalidOperationException("Audio-device fallback did not select the system default correctly.");

        var tall = createPattern(160, 1800);
        var frames = Enumerable.Range(0, 12)
            .Select(index => CaptureService.Crop(tall, new Int32Rect(0, index * 120, 160, 300)))
            .ToList();
        var stitched = ScrollingCaptureService.StitchFrames(frames);
        if (stitched.PixelWidth != 160 || stitched.PixelHeight <= 300 || stitched.PixelHeight > 50000)
            throw new InvalidOperationException($"Long-capture stress stitching produced {stitched.PixelWidth}x{stitched.PixelHeight}.");

        // A fixed web-app header must not confuse the content overlap below
        // it. This mirrors the most common long-capture failure mode.
        var stickyFrames = Enumerable.Range(0, 8)
            .Select(index => WithStickyHeader(
                CaptureService.Crop(tall, new Int32Rect(0, index * 120, 160, 300)), tall, 36))
            .ToList();
        var detectedShift = ScrollingCaptureService.FindVerticalShift(stickyFrames[0], stickyFrames[1]);
        var stickyStitched = ScrollingCaptureService.StitchFrames(stickyFrames);
        if (detectedShift is < 116 or > 124 || stickyStitched.PixelHeight is < 1120 or > 1160)
            throw new InvalidOperationException($"Sticky-header stitching failed: shift={detectedShift}, height={stickyStitched.PixelHeight}.");

        var runtime = RuntimeCompatibilityService.Snapshot();
        if (runtime.Displays.Count == 0 || string.IsNullOrWhiteSpace(RuntimeCompatibilityService.Describe(runtime)))
            throw new InvalidOperationException("Runtime display diagnostics were unavailable.");

        // Mixed-DPI spanning overlay: physical HWND mapping (not WPF PointToScreen).
        var hwnd = new System.Drawing.Rectangle(-1920, -200, 4480, 2520);
        var screen = CaptureCoordinateService.OverlayToScreen(new Point(2240, 1260), hwnd, 4480, 2520);
        var back = CaptureCoordinateService.ScreenToOverlay(screen, hwnd, 4480, 2520);
        if (Math.Abs(screen.X - 320) > 0.5 || Math.Abs(screen.Y - 1060) > 0.5 ||
            Math.Abs(back.X - 2240) > 0.5 || Math.Abs(back.Y - 1260) > 0.5)
            throw new InvalidOperationException($"Overlay HWND mapping failed: screen={screen}, back={back}");

        // Single-frame stitch is identity; empty is rejected.
        var single = createPattern(64, 48);
        var identity = ScrollingCaptureService.StitchFrames([single]);
        if (identity.PixelWidth != 64 || identity.PixelHeight != 48)
            throw new InvalidOperationException("Single-frame stitch must return the original dimensions.");
        try
        {
            ScrollingCaptureService.StitchFrames([]);
            throw new InvalidOperationException("Empty stitch should throw.");
        }
        catch (ArgumentException)
        {
            // expected
        }

        // Prefer DXGI is on by default; still captures through GDI fallback.
        var defaults = SettingsService.Normalize(new AppSettings());
        if (!defaults.PreferDxgiCapture)
            throw new InvalidOperationException("PreferDxgiCapture should default to true.");
        var sample = CaptureService.CaptureScreenRect(
            new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, 32, 32));
        if (sample.PixelWidth < 1 || sample.PixelHeight < 1)
            throw new InvalidOperationException("Capture pipeline returned an empty sample.");

        Console.WriteLine($"COMPATIBILITY: mixed DPI, portrait, negative coordinates, overlay mapping, DXGI/GDI capture, remote-session diagnostics, audio fallback and feature-matched stitching verified");
    }

    private static BitmapSource WithStickyHeader(BitmapSource frame, BitmapSource headerSource, int height)
    {
        var convertedFrame = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var convertedHeader = headerSource.Format == PixelFormats.Bgra32 ? headerSource : new FormatConvertedBitmap(headerSource, PixelFormats.Bgra32, null, 0);
        var writable = new WriteableBitmap(convertedFrame);
        var stride = convertedFrame.PixelWidth * 4;
        var bytes = new byte[stride * height];
        convertedHeader.CopyPixels(new Int32Rect(0, 0, convertedFrame.PixelWidth, height), bytes, stride, 0);
        writable.WritePixels(new Int32Rect(0, 0, convertedFrame.PixelWidth, height), bytes, stride, 0);
        writable.Freeze();
        return writable;
    }
}
