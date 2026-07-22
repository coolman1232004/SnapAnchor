using SnapAnchor.Services;
using System.Windows;
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

        var runtime = RuntimeCompatibilityService.Snapshot();
        if (runtime.Displays.Count == 0 || string.IsNullOrWhiteSpace(RuntimeCompatibilityService.Describe(runtime)))
            throw new InvalidOperationException("Runtime display diagnostics were unavailable.");

        Console.WriteLine($"COMPATIBILITY: mixed DPI, portrait, negative coordinates, remote-session diagnostics, audio fallback and {frames.Count}-frame stitching verified");
    }
}
