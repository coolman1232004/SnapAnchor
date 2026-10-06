using SnapAnchor.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tesseract;
using ZXing;
using ZXing.Common;

namespace SnapAnchor.Services;

internal static class RecognitionService
{
    private sealed class EngineHolder : IDisposable
    {
        internal readonly SemaphoreSlim Sync = new(1, 1);
        internal readonly TesseractEngine Engine;
        internal EngineHolder(string dataPath, string language)
        {
            Engine = new TesseractEngine(dataPath, language, EngineMode.LstmOnly);
            Engine.SetVariable("preserve_interword_spaces", "1");
            Engine.SetVariable("user_defined_dpi", "300");
        }

        public void Dispose()
        {
            Sync.Wait();
            try { Engine.Dispose(); } finally { Sync.Release(); }
        }
    }

    private sealed record OcrCandidate(string Text, IReadOnlyList<RecognizedWord> Words, double Score);
    private static readonly ConcurrentDictionary<string, Lazy<EngineHolder>> Engines = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Workers = new(2, 2);

    static RecognitionService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var holder in Engines.Values) if (holder.IsValueCreated) holder.Value.Dispose();
            Engines.Clear();
        };
    }

    public static async Task<RecognitionResult> RecognizeAsync(BitmapSource image, string language, CancellationToken cancellationToken = default)
    {
        var frozen = image.IsFrozen ? image : Freeze(image);
        var detectOrientation = SettingsService.Read(settings => settings.OcrDetectOrientation);
        await Workers.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Recognize(frozen, language, detectOrientation, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { Workers.Release(); }
    }

    private static RecognitionResult Recognize(BitmapSource image, string language, bool detectOrientation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = new List<string>();
        var text = string.Empty;
        IReadOnlyList<RecognizedWord> words = Array.Empty<RecognizedWord>();
        var barcodeText = string.Empty;
        var barcodeFormat = string.Empty;

        try
        {
            var dataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
            if (!Directory.Exists(dataPath))
                throw new DirectoryNotFoundException(LocalizationService.Current("OCR language files are missing."));

            var normalizedLanguage = NormalizeLanguage(language);
            var lazy = Engines.GetOrAdd(normalizedLanguage, key => new Lazy<EngineHolder>(() => new EngineHolder(dataPath, key), LazyThreadSafetyMode.ExecutionAndPublication));
            EngineHolder holder;
            try { holder = lazy.Value; }
            catch { Engines.TryRemove(new KeyValuePair<string, Lazy<EngineHolder>>(normalizedLanguage, lazy)); throw; }
            OcrCandidate best;
            holder.Sync.Wait(cancellationToken);
            try
            {
                using var input = Pix.LoadFromMemory(ToPngBytes(image));
                best = Process(holder.Engine, image, PageSegMode.Auto, 1, 0, image.PixelWidth, image.PixelHeight, cancellationToken, input);
                if (best.Score < 54)
                    best = Better(best, Process(holder.Engine, image, PageSegMode.SparseText, 1, 0,
                        image.PixelWidth, image.PixelHeight, cancellationToken, input));

                if (best.Score < 60)
                {
                    var threshold = Threshold(image);
                    best = Better(best, Process(holder.Engine, threshold, PageSegMode.Auto, 1, 0,
                        image.PixelWidth, image.PixelHeight, cancellationToken));
                }

                // Small glyphs benefit considerably from a lossless 2x OCR
                // surface. Word boxes are mapped back to the original pixels.
                if (best.Score < 52 && image.PixelWidth <= 1800 && image.PixelHeight <= 1400)
                {
                    var scaled = Scale(image, 2);
                    best = Better(best, Process(holder.Engine, scaled, PageSegMode.Auto, 2, 0,
                        image.PixelWidth, image.PixelHeight, cancellationToken));
                }

                if (best.Score < 42 && detectOrientation)
                {
                    foreach (var angle in new[] { 90, 180, 270 })
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var rotated = Rotate(image, angle);
                        best = Better(best, Process(holder.Engine, rotated, PageSegMode.Auto, 1, angle,
                            image.PixelWidth, image.PixelHeight, cancellationToken));
                    }
                }
            }
            finally { holder.Sync.Release(); }
            text = best.Text;
            words = best.Words;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add($"OCR: {ex.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            var luminance = new RGBLuminanceSource(pixels, converted.PixelWidth, converted.PixelHeight, RGBLuminanceSource.BitmapFormat.BGRA32);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions { TryHarder = true, TryInverted = true }
            };
            var results = reader.DecodeMultiple(luminance) ?? [];
            if (results.Length > 0)
            {
                barcodeText = string.Join(Environment.NewLine, results.Select(result => result.Text).Distinct());
                barcodeFormat = string.Join(", ", results.Select(result => result.BarcodeFormat.ToString()).Distinct());
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Code scan: {ex.Message}");
        }

        return new RecognitionResult(text, barcodeText, barcodeFormat, string.Join(Environment.NewLine, errors), words);
    }

    private static OcrCandidate Process(TesseractEngine engine, BitmapSource image, PageSegMode mode, double scale,
        int rotation, int originalWidth, int originalHeight, CancellationToken cancellationToken, Pix? input = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var owned = input is null ? Pix.LoadFromMemory(ToPngBytes(image)) : null;
        using var page = engine.Process(input ?? owned!, mode);
        cancellationToken.ThrowIfCancellationRequested();
        var text = page.GetText()?.Trim() ?? string.Empty;
        var rawWords = ExtractWords(page, cancellationToken);
        var mapped = rawWords.Select(word => MapWord(word, scale, rotation, originalWidth, originalHeight)).ToList();
        var confidence = mapped.Count == 0 ? 0 : mapped.Average(word => word.Confidence);
        var usefulCharacters = text.Count(character => !char.IsWhiteSpace(character));
        var score = confidence + Math.Min(18, usefulCharacters / 8.0) + Math.Min(8, mapped.Count / 3.0);
        return new OcrCandidate(text, mapped, score);
    }

    private static OcrCandidate Better(OcrCandidate first, OcrCandidate second)
        => second.Score > first.Score ? second : first;

    private static RecognizedWord MapWord(RecognizedWord word, double scale, int rotation, int width, int height)
    {
        var x = word.X / scale;
        var y = word.Y / scale;
        var w = word.Width / scale;
        var h = word.Height / scale;
        (double X, double Y, double W, double H) mapped = rotation switch
        {
            90 => (y, height - (x + w), h, w),
            180 => (width - (x + w), height - (y + h), w, h),
            270 => (width - (y + h), x, h, w),
            _ => (x, y, w, h)
        };
        return word with
        {
            X = Math.Clamp((int)Math.Round(mapped.X), 0, Math.Max(0, width - 1)),
            Y = Math.Clamp((int)Math.Round(mapped.Y), 0, Math.Max(0, height - 1)),
            Width = Math.Clamp((int)Math.Round(mapped.W), 1, width),
            Height = Math.Clamp((int)Math.Round(mapped.H), 1, height)
        };
    }

    private static IReadOnlyList<RecognizedWord> ExtractWords(Page page, CancellationToken cancellationToken)
    {
        var words = new List<RecognizedWord>();
        using var iterator = page.GetIterator();
        iterator.Begin();
        var lineIndex = -1;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lineIndex < 0 || iterator.IsAtBeginningOf(PageIteratorLevel.TextLine)) lineIndex++;
            var value = iterator.GetText(PageIteratorLevel.Word)?.Trim();
            if (string.IsNullOrWhiteSpace(value) ||
                !iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds) ||
                bounds.Width <= 0 || bounds.Height <= 0)
                continue;
            words.Add(new RecognizedWord(value, bounds.X1, bounds.Y1, bounds.Width, bounds.Height,
                Math.Max(0, lineIndex), iterator.GetConfidence(PageIteratorLevel.Word)));
        }
        while (iterator.Next(PageIteratorLevel.Word));
        return words;
    }

    private static BitmapSource Threshold(BitmapSource source)
    {
        var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        var stride = gray.PixelWidth;
        var pixels = new byte[stride * gray.PixelHeight];
        gray.CopyPixels(pixels, stride, 0);
        var histogram = new int[256];
        foreach (var value in pixels) histogram[value]++;
        var threshold = Otsu(histogram, pixels.Length);
        for (var index = 0; index < pixels.Length; index++) pixels[index] = pixels[index] >= threshold ? (byte)255 : (byte)0;
        var result = BitmapSource.Create(gray.PixelWidth, gray.PixelHeight, 96, 96, PixelFormats.Gray8, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static int Otsu(IReadOnlyList<int> histogram, int total)
    {
        long sum = 0;
        for (var i = 0; i < 256; i++) sum += (long)i * histogram[i];
        long backgroundSum = 0;
        var backgroundWeight = 0;
        double maximum = -1;
        var chosen = 127;
        for (var i = 0; i < 256; i++)
        {
            backgroundWeight += histogram[i];
            if (backgroundWeight == 0) continue;
            var foregroundWeight = total - backgroundWeight;
            if (foregroundWeight == 0) break;
            backgroundSum += (long)i * histogram[i];
            var backgroundMean = backgroundSum / (double)backgroundWeight;
            var foregroundMean = (sum - backgroundSum) / (double)foregroundWeight;
            var variance = (double)backgroundWeight * foregroundWeight * Math.Pow(backgroundMean - foregroundMean, 2);
            if (variance <= maximum) continue;
            maximum = variance;
            chosen = i;
        }
        return chosen;
    }

    private static BitmapSource Scale(BitmapSource source, double factor)
    {
        var transformed = new TransformedBitmap(source, new ScaleTransform(factor, factor));
        transformed.Freeze();
        return transformed;
    }

    private static BitmapSource Rotate(BitmapSource source, double angle)
    {
        var transformed = new TransformedBitmap(source, new RotateTransform(angle));
        transformed.Freeze();
        return transformed;
    }

    private static string NormalizeLanguage(string language)
    {
        var installed = new HashSet<string>(Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "tessdata"), "*.traineddata")
            .Select(path => Path.GetFileNameWithoutExtension(path)), StringComparer.OrdinalIgnoreCase);
        var chosen = (language ?? "eng").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(installed.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return chosen.Count > 0 ? string.Join('+', chosen) : "eng";
    }

    private static byte[] ToPngBytes(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource Freeze(BitmapSource source)
    {
        var clone = source.Clone();
        clone.Freeze();
        return clone;
    }
}
