using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapAnchor.Services;

internal sealed record ScrollingCaptureProgress(int FrameCount, int RejectedFrames, string Message);
internal sealed record ScrollingCaptureResult(BitmapSource Image, int FrameCount, bool Cancelled, int RejectedFrames = 0, string StopReason = "Completed");

internal static class ScrollingCaptureService
{
    private const int EscapeKey = 0x1B;

    public static async Task<ScrollingCaptureResult> CaptureAsync(
        Rect screenRegion,
        AppSettings settings,
        IntPtr scrollTarget = default,
        NativeMethods.NativePoint scrollPoint = default,
        CancellationToken cancellationToken = default,
        IProgress<ScrollingCaptureProgress>? progress = null,
        Func<bool>? stopRequested = null)
    {
        var frames = new List<BitmapSource> { CaptureService.CaptureScreenRect(screenRegion) };
        var cancelled = false;
        var rejectedFrames = 0;
        var consecutiveDuplicates = 0;
        var consecutiveAlignmentFailures = 0;
        var stopReason = "Maximum frame count reached";
        var maxFrames = Math.Clamp(settings.ScrollCaptureMaxFrames, 2, 60);
        var delay = Math.Clamp(settings.ScrollCaptureDelayMs, 150, 3000);
        var wheelDelta = -120 * Math.Clamp(settings.ScrollCaptureWheelClicks, 1, 20);
        var activeWheelDelta = wheelDelta;

        progress?.Report(new ScrollingCaptureProgress(1, 0, LocalizationService.Current("Captured first frame")));
        for (var attempt = 1; frames.Count < maxFrames && attempt < maxFrames * 2; attempt++)
        {
            if (cancellationToken.IsCancellationRequested || stopRequested?.Invoke() == true ||
                (NativeMethods.GetAsyncKeyState(EscapeKey) & 0x8000) != 0)
            {
                cancelled = true;
                stopReason = "Stopped by user";
                break;
            }
            Scroll(scrollTarget, scrollPoint, activeWheelDelta, consecutiveDuplicates > 0);
            await Task.Delay(delay, CancellationToken.None);
            var current = CaptureService.CaptureScreenRect(screenRegion);
            var previousPixels = PixelFrame.From(frames[^1]);
            var currentPixels = PixelFrame.From(current);
            if (AverageDifference(previousPixels, currentPixels) < 2.4)
            {
                rejectedFrames++;
                consecutiveDuplicates++;
                progress?.Report(new ScrollingCaptureProgress(frames.Count, rejectedFrames, LocalizationService.Current("Duplicate frame ignored")));
                if (consecutiveDuplicates >= 2)
                {
                    stopReason = "Reached the end of the scrollable content";
                    break;
                }
                continue;
            }
            consecutiveDuplicates = 0;
            var alignment = FindVerticalAlignment(previousPixels, currentPixels);
            // Animated pages sometimes have not settled at the configured
            // delay. One stationary recapture is cheaper and more reliable
            // than accepting a bad seam.
            if (alignment is null)
            {
                await Task.Delay(Math.Max(80, delay / 2), CancellationToken.None);
                current = CaptureService.CaptureScreenRect(screenRegion);
                currentPixels = PixelFrame.From(current);
                alignment = FindVerticalAlignment(previousPixels, currentPixels);
            }
            if (alignment is null)
            {
                rejectedFrames++;
                consecutiveAlignmentFailures++;
                // Roll back the failed wheel move and retry with a smaller
                // step. This prevents one transient mismatch from skipping a
                // section of the page.
                Scroll(scrollTarget, scrollPoint, -activeWheelDelta, true);
                await Task.Delay(Math.Max(80, delay / 2), CancellationToken.None);
                activeWheelDelta = Math.Sign(wheelDelta) * Math.Max(120, Math.Abs(activeWheelDelta) / 2);
                progress?.Report(new ScrollingCaptureProgress(frames.Count, rejectedFrames, LocalizationService.Current("Unmatched frame rolled back; retrying")));
                if (consecutiveAlignmentFailures >= 2)
                {
                    stopReason = "Could not find a reliable overlap";
                    break;
                }
                continue;
            }
            consecutiveAlignmentFailures = 0;
            frames.Add(current);
            if (Math.Abs(activeWheelDelta) < Math.Abs(wheelDelta))
                activeWheelDelta = Math.Sign(wheelDelta) * Math.Min(Math.Abs(wheelDelta), Math.Abs(activeWheelDelta) + 120);
            progress?.Report(new ScrollingCaptureProgress(frames.Count, rejectedFrames, LocalizationService.Format("Captured {0} frames", frames.Count)));
            if (frames[0].PixelHeight + EstimateAddedHeight(frames) >= 50000)
            {
                stopReason = "Reached the 50,000 pixel height limit";
                break;
            }
        }

        return new ScrollingCaptureResult(StitchFrames(frames), frames.Count, cancelled, rejectedFrames, stopReason);
    }

    internal static BitmapSource StitchFrames(IReadOnlyList<BitmapSource> frames)
    {
        if (frames.Count == 0) throw new ArgumentException("At least one frame is required.", nameof(frames));
        if (frames.Count == 1) return frames[0];
        var pixels = frames.Select(PixelFrame.From).ToList();
        var segments = new List<(PixelFrame Frame, int StartRow, int Height)> { (pixels[0], 0, pixels[0].Height) };
        for (var index = 1; index < pixels.Count; index++)
        {
            var alignment = FindVerticalAlignment(pixels[index - 1], pixels[index]);
            if (alignment is null) break;
            segments.Add((pixels[index], pixels[index].Height - alignment.Shift, alignment.Shift));
        }

        var width = segments.Min(segment => segment.Frame.Width);
        var outputHeight = Math.Min(50000, segments.Sum(segment => segment.Height));
        var stride = width * 4;
        var output = new byte[stride * outputHeight];
        var destinationRow = 0;
        foreach (var segment in segments)
        {
            var rows = Math.Min(segment.Height, outputHeight - destinationRow);
            if (rows <= 0) break;
            for (var row = 0; row < rows; row++)
                Buffer.BlockCopy(segment.Frame.Bytes, (segment.StartRow + row) * segment.Frame.Stride, output, (destinationRow + row) * stride, stride);
            destinationRow += rows;
        }

        var bitmap = BitmapSource.Create(width, destinationRow, 96, 96, PixelFormats.Bgra32, null, output, stride);
        bitmap.Freeze();
        return bitmap;
    }

    internal static int? FindVerticalShift(BitmapSource previous, BitmapSource current) =>
        FindVerticalAlignment(PixelFrame.From(previous), PixelFrame.From(current))?.Shift;

    private sealed record VerticalAlignment(int Shift, double Score, double Confidence);

    private static VerticalAlignment? FindVerticalAlignment(PixelFrame previous, PixelFrame current)
    {
        var width = Math.Min(previous.Width, current.Width);
        var height = Math.Min(previous.Height, current.Height);
        if (width < 24 || height < 40) return null;
        var minimumShift = Math.Max(8, height / 16);
        var maximumShift = Math.Max(minimumShift, (int)(height * 0.9));
        var coarseStep = Math.Max(1, height / 260);
        double bestScore = double.MaxValue;
        double secondScore = double.MaxValue;
        var bestShift = 0;

        for (var shift = minimumShift; shift <= maximumShift; shift += coarseStep)
        {
            var score = AlignmentScore(previous, current, width, height, shift);
            if (!double.IsFinite(score)) continue;
            if (score < bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                bestShift = shift;
            }
            else if (score < secondScore && Math.Abs(shift - bestShift) > coarseStep * 2)
                secondScore = score;
        }

        // Refine around the coarse winner so a one-pixel text row does not
        // leave a visible seam.
        var refinedStart = Math.Max(minimumShift, bestShift - coarseStep);
        var refinedEnd = Math.Min(maximumShift, bestShift + coarseStep);
        for (var shift = refinedStart; shift <= refinedEnd; shift++)
        {
            var score = AlignmentScore(previous, current, width, height, shift);
            if (score >= bestScore) continue;
            bestScore = score;
            bestShift = shift;
        }

        var confidence = double.IsFinite(secondScore)
            ? Math.Clamp((secondScore - bestScore) / Math.Max(1, secondScore), 0, 1)
            : 1;
        // Highly similar frames can have several neighbouring candidates;
        // score is authoritative there. Ambiguous, noisy frames require a
        // stronger margin over the runner-up.
        if (bestScore > 34 || (bestScore > 18 && confidence < 0.025)) return null;
        return new VerticalAlignment(bestShift, bestScore, confidence);
    }

    private static double AlignmentScore(PixelFrame previous, PixelFrame current, int width, int height, int shift)
    {
        var overlap = height - shift;
        if (overlap < Math.Max(24, height / 12)) return double.PositiveInfinity;
        var xMargin = Math.Min(width / 6, 28);
        var yMargin = Math.Min(overlap / 6, Math.Max(3, height / 18));
        // Sticky app headers occupy the top of both frames equally and confuse
        // overlap matching; bias the score toward content below that band.
        var stickySkip = Math.Min(overlap / 4, Math.Max(12, height / 10));
        var yStart = yMargin + stickySkip;
        var xStep = Math.Max(3, width / 180);
        var yStep = Math.Max(2, overlap / 120);
        double total = 0;
        double weightTotal = 0;

        for (var y = yStart; y < overlap - yMargin; y += yStep)
        {
            double rowTotal = 0;
            double rowGradient = 0;
            var rowSamples = 0;
            for (var x = xMargin; x < width - xMargin; x += xStep)
            {
                var a = Luma(previous, x, y + shift);
                var b = Luma(current, x, y);
                var gradientA = Math.Abs(a - Luma(previous, Math.Min(width - 1, x + xStep), y + shift));
                var gradientB = Math.Abs(b - Luma(current, Math.Min(width - 1, x + xStep), y));
                // Truncation prevents a blinking caret or small animation from
                // dominating an otherwise exact page match.
                rowTotal += Math.Min(64, Math.Abs(a - b));
                rowTotal += 0.35 * Math.Min(64, Math.Abs(gradientA - gradientB));
                rowGradient += Math.Max(gradientA, gradientB);
                rowSamples++;
            }
            if (rowSamples == 0) continue;
            var textureWeight = Math.Clamp(rowGradient / (rowSamples * 12.0), 0.2, 1.0);
            total += (rowTotal / rowSamples) * textureWeight;
            weightTotal += textureWeight;
        }
        return weightTotal <= 0 ? double.PositiveInfinity : total / weightTotal;
    }

    private static int Luma(PixelFrame frame, int x, int y)
    {
        var offset = y * frame.Stride + x * 4;
        return (frame.Bytes[offset] * 29 + frame.Bytes[offset + 1] * 150 + frame.Bytes[offset + 2] * 77) >> 8;
    }

    private static double AverageDifference(PixelFrame first, PixelFrame second)
    {
        var width = Math.Min(first.Width, second.Width);
        var height = Math.Min(first.Height, second.Height);
        var xStep = Math.Max(4, width / 120);
        var yStep = Math.Max(4, height / 90);
        long difference = 0;
        var samples = 0;
        for (var y = 0; y < height; y += yStep)
        for (var x = 0; x < width; x += xStep)
        {
            var a = y * first.Stride + x * 4;
            var b = y * second.Stride + x * 4;
            difference += Math.Abs(first.Bytes[a] - second.Bytes[b]);
            difference += Math.Abs(first.Bytes[a + 1] - second.Bytes[b + 1]);
            difference += Math.Abs(first.Bytes[a + 2] - second.Bytes[b + 2]);
            samples += 3;
        }
        return samples == 0 ? double.MaxValue : difference / (double)samples;
    }

    private static int EstimateAddedHeight(IReadOnlyList<BitmapSource> frames)
    {
        var total = 0;
        for (var index = 1; index < frames.Count; index++)
            total += FindVerticalAlignment(PixelFrame.From(frames[index - 1]), PixelFrame.From(frames[index]))?.Shift ?? 0;
        return total;
    }

    private static void Scroll(IntPtr target, NativeMethods.NativePoint point, int wheelDelta, bool useSystemInput)
    {
        if (target != IntPtr.Zero && !useSystemInput)
        {
            var wheelParameter = new IntPtr(unchecked(wheelDelta << 16));
            var pointParameter = new IntPtr(unchecked((point.Y << 16) | (point.X & 0xFFFF)));
            if (NativeMethods.PostMessage(target, NativeMethods.WmMouseWheel, wheelParameter, pointParameter)) return;
        }

        var inputs = new[]
        {
            new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Data = new NativeMethods.InputUnion { Mouse = new NativeMethods.MouseInput { MouseData = unchecked((uint)wheelDelta), Flags = NativeMethods.MouseEventWheel } }
            }
        };
        NativeMethods.SendInput(1, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>());
    }

    private sealed record PixelFrame(int Width, int Height, int Stride, byte[] Bytes)
    {
        internal static PixelFrame From(BitmapSource source)
        {
            var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var bytes = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(bytes, stride, 0);
            return new PixelFrame(converted.PixelWidth, converted.PixelHeight, stride, bytes);
        }
    }
}
