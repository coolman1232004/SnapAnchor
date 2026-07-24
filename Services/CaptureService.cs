using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace SnapAnchor.Services;

internal static partial class CaptureService
{
    public const string ImageSaveFilter = "PNG image (*.png)|*.png|JPEG image (*.jpg;*.jpeg)|*.jpg;*.jpeg|WebP image (*.webp)|*.webp";

    public static string ExtensionForFormat(string? format) => format?.ToUpperInvariant() switch
    {
        "JPEG" => ".jpg",
        "WEBP" => ".webp",
        _ => ".png"
    };

    public static int FilterIndexForFormat(string? format) => format?.ToUpperInvariant() switch
    {
        "JPEG" => 2,
        "WEBP" => 3,
        _ => 1
    };

    public static BitmapSource CaptureVirtualScreen(bool includeCursor = false)
    {
        var bounds = DisplayTopologyService.VirtualBoundsPixels();
        return CaptureBounds(bounds, includeCursor);
    }

    public static BitmapSource CaptureScreenRect(Rect screenRect, bool includeCursor = false)
    {
        var bounds = new Drawing.Rectangle(
            (int)Math.Round(screenRect.X),
            (int)Math.Round(screenRect.Y),
            Math.Max(1, (int)Math.Round(screenRect.Width)),
            Math.Max(1, (int)Math.Round(screenRect.Height)));
        return CaptureBounds(bounds, includeCursor);
    }

    private static BitmapSource CaptureBounds(Drawing.Rectangle bounds, bool includeCursor)
    {
        var settings = SettingsService.Load();
        BitmapSource source;
        if (settings.PreferDxgiCapture &&
            DxgiCaptureService.TryCapture(bounds, includeCursor, out var dxgiImage) &&
            dxgiImage is not null)
        {
            source = dxgiImage;
        }
        else
        {
            source = CaptureBoundsGdi(bounds, includeCursor);
        }

        source = NormalizeDpi96(source);
        source = HdrColorService.CorrectIfNeeded(source, bounds, settings);
        return CaptureExclusionService.Apply(source, bounds, settings);
    }

    private static BitmapSource CaptureBoundsGdi(Drawing.Rectangle bounds, bool includeCursor)
    {
        using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height, DrawingImaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, Drawing.CopyPixelOperation.SourceCopy);
            if (includeCursor) DrawCursor(graphics, bounds);
        }

        var handle = bitmap.GetHbitmap();
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            return NormalizeDpi96(source);
        }
        finally
        {
            NativeMethods.DeleteObject(handle);
        }
    }

    internal static BitmapSource NormalizeDpi96(BitmapSource source)
    {
        if (Math.Abs(source.DpiX - 96) < 0.01 && Math.Abs(source.DpiY - 96) < 0.01)
        {
            if (!source.IsFrozen) source.Freeze();
            return source;
        }

        var stride = Math.Max(1, (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        var normalized = BitmapSource.Create(
            source.PixelWidth,
            source.PixelHeight,
            96,
            96,
            source.Format,
            source.Palette,
            pixels,
            stride);
        normalized.Freeze();
        return normalized;
    }

    private static void DrawCursor(Drawing.Graphics graphics, Drawing.Rectangle bounds)
    {
        var info = new NativeMethods.CursorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.CursorInfo>() };
        if (!NativeMethods.GetCursorInfo(ref info) || (info.Flags & NativeMethods.CursorShowing) == 0 || info.Cursor == IntPtr.Zero) return;
        var hotspotX = 0;
        var hotspotY = 0;
        if (NativeMethods.GetIconInfo(info.Cursor, out var iconInfo))
        {
            hotspotX = iconInfo.HotspotX;
            hotspotY = iconInfo.HotspotY;
            if (iconInfo.MaskBitmap != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.MaskBitmap);
            if (iconInfo.ColorBitmap != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.ColorBitmap);
        }
        var dc = graphics.GetHdc();
        try
        {
            NativeMethods.DrawIconEx(dc,
                info.ScreenPosition.X - bounds.Left - hotspotX,
                info.ScreenPosition.Y - bounds.Top - hotspotY,
                info.Cursor, 0, 0, 0, IntPtr.Zero, NativeMethods.DiNormal);
        }
        finally
        {
            graphics.ReleaseHdc(dc);
        }
    }

    public static BitmapSource Crop(BitmapSource source, Int32Rect region)
    {
        region.X = Math.Clamp(region.X, 0, Math.Max(0, source.PixelWidth - 1));
        region.Y = Math.Clamp(region.Y, 0, Math.Max(0, source.PixelHeight - 1));
        region.Width = Math.Clamp(region.Width, 1, source.PixelWidth - region.X);
        region.Height = Math.Clamp(region.Height, 1, source.PixelHeight - region.Y);
        var cropped = new CroppedBitmap(source, region);
        cropped.Freeze();
        return cropped;
    }

    public static BitmapSource? GetClipboardVisual()
        => GetClipboardVisuals().FirstOrDefault();

    public static IReadOnlyList<BitmapSource> GetClipboardVisuals()
    {
        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            image?.Freeze();
            return image is null ? [] : [image];
        }

        if (Clipboard.ContainsFileDropList())
        {
            return Clipboard.GetFileDropList().Cast<string>()
                .Where(IsSupportedImage)
                .Take(30)
                .Select(LoadImage)
                .ToList();
        }

        if (Clipboard.ContainsData(DataFormats.Html) && Clipboard.GetData(DataFormats.Html) is string html)
        {
            var plain = HtmlToPlainText(html);
            if (!string.IsNullOrWhiteSpace(plain)) return [RenderTextCard(plain)];
        }

        return Clipboard.ContainsText() ? [RenderTextCard(Clipboard.GetText())] : [];
    }

    private static string HtmlToPlainText(string html)
    {
        var fragment = Regex.Match(html, @"(?is)<!--StartFragment-->(.*?)<!--EndFragment-->");
        var body = fragment.Success ? fragment.Groups[1].Value : html;
        body = Regex.Replace(body, @"(?is)<(br|/p|/div|/li|/tr)\b[^>]*>", "\n");
        body = Regex.Replace(body, @"(?is)<[^>]+>", string.Empty);
        body = System.Net.WebUtility.HtmlDecode(body);
        return Regex.Replace(body, @"[ \t]+", " ").Trim();
    }

    public static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static void SaveImage(BitmapSource source, string path, int quality = 92, AppSettings? outputEffects = null)
    {
        if (outputEffects is not null) source = ApplyOutputEffects(source, outputEffects);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".png":
                SavePng(source, path);
                return;
            case ".jpg":
            case ".jpeg":
                var jpeg = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
                jpeg.Frames.Add(BitmapFrame.Create(source));
                using (var stream = File.Create(path)) jpeg.Save(stream);
                return;
            case ".webp":
                SaveWebP(source, path, quality);
                return;
            default:
                throw new NotSupportedException(LocalizationService.Format("Unsupported image format: {0}", extension));
        }
    }

    internal static BitmapSource ApplyOutputEffects(BitmapSource source, AppSettings settings)
    {
        var border = Math.Clamp(settings.OutputBorderWidth, 0, 32);
        var shadowSize = settings.OutputIncludeShadow ? Math.Clamp(settings.OutputShadowSize, 1, 64) : 0;
        if (border == 0 && shadowSize == 0) return source;

        var pad = shadowSize * 2;
        var width = source.PixelWidth + border * 2 + pad * 2;
        var height = source.PixelHeight + border * 2 + pad * 2;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            if (shadowSize > 0)
            {
                var shadow = ParseColor(settings.OutputShadowColor, Color.FromArgb(102, 0, 0, 0));
                var offset = Math.Max(1, shadowSize / 3d);
                for (var spread = shadowSize; spread >= 1; spread--)
                {
                    var alpha = (byte)Math.Clamp(shadow.A / Math.Max(3d, shadowSize * 0.7), 1, 80);
                    drawing.DrawRoundedRectangle(
                        new SolidColorBrush(Color.FromArgb(alpha, shadow.R, shadow.G, shadow.B)),
                        null,
                        new Rect(pad - spread + offset, pad - spread + offset,
                            source.PixelWidth + border * 2 + spread * 2,
                            source.PixelHeight + border * 2 + spread * 2),
                        Math.Min(6, spread),
                        Math.Min(6, spread));
                }
            }

            if (border > 0)
                drawing.DrawRectangle(
                    new SolidColorBrush(ParseColor(settings.OutputBorderColor, Color.FromRgb(17, 24, 39))),
                    null,
                    new Rect(pad, pad, source.PixelWidth + border * 2, source.PixelHeight + border * 2));
            drawing.DrawImage(source, new Rect(pad + border, pad + border, source.PixelWidth, source.PixelHeight));
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    private static Color ParseColor(string value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return fallback; }
    }

    private static void SaveWebP(BitmapSource source, string path, int quality)
    {
        using var pngStream = new MemoryStream();
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(source));
        png.Save(pngStream);
        pngStream.Position = 0;
        using var bitmap = SKBitmap.Decode(pngStream) ?? throw new InvalidOperationException(LocalizationService.Current("The image could not be prepared for WebP export."));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, Math.Clamp(quality, 1, 100))
            ?? throw new InvalidOperationException(LocalizationService.Current("WebP encoding failed."));
        using var output = File.Create(path);
        data.SaveTo(output);
    }

    private static bool IsSupportedImage(string path) =>
        new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" }
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static BitmapSource LoadImage(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource RenderTextCard(string input)
    {
        var text = input.Trim();
        if (text.Length > 3000) text = text[..3000] + "\n…";

        const double width = 540;
        var pixelsPerDip = 1.0;
        var visual = new DrawingVisual();
        var colorMatch = HexColorRegex().Match(text);
        var isColor = colorMatch.Success && colorMatch.Value.Length == text.Length;
        var background = isColor
            ? (SolidColorBrush)new BrushConverter().ConvertFromString(text)!
            : new SolidColorBrush(Color.FromRgb(24, 28, 35));

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            isColor ? 28 : 18,
            isColor && IsLight(background.Color) ? Brushes.Black : Brushes.White,
            pixelsPerDip)
        {
            MaxTextWidth = width - 64,
            MaxTextHeight = 720,
            Trimming = TextTrimming.CharacterEllipsis,
            LineHeight = isColor ? 38 : 26
        };

        var height = isColor ? 300 : Math.Clamp(formatted.Height + 64, 120, 760);
        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(background, null, new Rect(0, 0, width, height), 18, 18);
            var origin = isColor
                ? new Point((width - formatted.Width) / 2, (height - formatted.Height) / 2)
                : new Point(32, 30);
            context.DrawText(formatted, origin);
        }

        var bitmap = new RenderTargetBitmap((int)width, (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool IsLight(System.Windows.Media.Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) > 155;

    [GeneratedRegex("^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColorRegex();
}
