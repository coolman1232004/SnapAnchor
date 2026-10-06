using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapAnchor.Services;

internal sealed record ScreenRecordingResult(string FilePath, BitmapSource Preview, TimeSpan Duration, int FrameCount);

internal static class ScreenRecordingService
{
    public static Task<ScreenRecordingResult> CaptureGifAsync(
        Rect screenBounds,
        AppSettings settings,
        Func<bool> isPaused,
        Func<bool> shouldStop,
        Action<TimeSpan, int>? progress,
        CancellationToken cancellationToken)
        => Task.Run(() => CaptureGifCoreAsync(screenBounds, settings, isPaused, shouldStop, progress, cancellationToken), cancellationToken);

    private static async Task<ScreenRecordingResult> CaptureGifCoreAsync(Rect screenBounds, AppSettings settings,
        Func<bool> isPaused, Func<bool> shouldStop, Action<TimeSpan, int>? progress, CancellationToken cancellationToken)
    {
        var fps = Math.Clamp(settings.RecordingFrameRate, 2, 20);
        var interval = TimeSpan.FromMilliseconds(1000d / fps);
        var maximum = TimeSpan.FromSeconds(Math.Clamp(settings.RecordingMaxDurationSeconds, 5, 600));
        var maximumFrames = Math.Max(1, (int)Math.Ceiling(maximum.TotalSeconds * fps));
        var active = Stopwatch.StartNew();
        var total = Stopwatch.StartNew();
        var wasPaused = false;
        var output = SettingsService.CreateOutputPath(settings.RecordingFolder, "SnapAnchor_$yyyy-MM-dd_HH-mm-ss.gif", ".gif");
        BitmapSource? preview = null;
        StreamingGifWriter? writer = null;
        var frameCount = 0;
        BitmapSource? pendingFrame = null;
        var pendingStarted = TimeSpan.Zero;

        try
        {
            while (!shouldStop() && active.Elapsed < maximum && frameCount < maximumFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isPaused())
                {
                    if (!wasPaused) active.Stop();
                    wasPaused = true;
                    progress?.Invoke(active.Elapsed, frameCount);
                    await Task.Delay(80, cancellationToken);
                    continue;
                }

                if (wasPaused)
                {
                    active.Start();
                    wasPaused = false;
                }

                var frameStarted = total.Elapsed;
                var capturedAt = active.Elapsed;
                var captured = CaptureService.CaptureScreenRect(screenBounds, settings.RecordingIncludeCursor);
                var frame = ScaleToMaximumWidth(captured, Math.Clamp(settings.RecordingMaxWidth, 320, 1920));
                preview ??= frame;
                writer ??= new StreamingGifWriter(output, frame.PixelWidth, frame.PixelHeight, interval);
                // Hold only one frame so its duration reflects the actual next capture time,
                // including slow encoding/capture but excluding paused time.
                if (pendingFrame is not null) writer.AppendFrame(pendingFrame, capturedAt - pendingStarted);
                pendingFrame = frame;
                pendingStarted = capturedAt;
                frameCount++;
                progress?.Invoke(active.Elapsed, frameCount);
                var remaining = interval - (total.Elapsed - frameStarted);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
            }

            if (frameCount == 0)
            {
                var frame = ScaleToMaximumWidth(CaptureService.CaptureScreenRect(screenBounds, settings.RecordingIncludeCursor), settings.RecordingMaxWidth);
                preview = frame;
                writer = new StreamingGifWriter(output, frame.PixelWidth, frame.PixelHeight, interval);
                writer.AppendFrame(frame);
                frameCount = 1;
            }

            active.Stop();
            if (pendingFrame is not null) writer!.AppendFrame(pendingFrame, active.Elapsed - pendingStarted);
            writer!.Complete();
            return new ScreenRecordingResult(output, preview!, active.Elapsed, frameCount);
        }
        catch
        {
            writer?.Dispose();
            if (File.Exists(output)) File.Delete(output);
            throw;
        }
    }

    internal static void SaveGif(IReadOnlyList<BitmapSource> frames, string path, TimeSpan frameDuration)
    {
        if (frames.Count == 0) throw new ArgumentException("At least one frame is required.", nameof(frames));
        using var writer = new StreamingGifWriter(path, frames[0].PixelWidth, frames[0].PixelHeight, frameDuration);
        foreach (var frame in frames) writer.AppendFrame(frame);
        writer.Complete();
    }

    private static BitmapSource ScaleToMaximumWidth(BitmapSource source, int maximumWidth)
    {
        if (source.PixelWidth <= maximumWidth) return source;
        var scale = maximumWidth / (double)source.PixelWidth;
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }
}
