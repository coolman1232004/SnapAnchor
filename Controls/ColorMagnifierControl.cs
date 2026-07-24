using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SnapAnchor.Services;

namespace SnapAnchor.Controls;

/// <summary>
/// Pixel-perfect colour magnifier used during capture and standalone colour-picker mode.
/// Samples the frozen capture bitmap (same space as the overlay) to avoid DPI drift.
/// </summary>
internal sealed class ColorMagnifierControl : Border
{
    private const int SourceGrid = 15; // odd so one center pixel
    private const int PixelScale = 10; // 15 * 10 = 150 px lens
    private const int LensSize = SourceGrid * PixelScale;

    private readonly Image _lensImage;
    private readonly WriteableBitmap _lensBitmap;
    private readonly Border _swatch;
    private readonly TextBlock _coordText;
    private readonly TextBlock _colorText;
    private readonly TextBlock _hintText;
    private readonly TextBlock _statusText;

    private Color _color = Colors.White;
    private int _pixelX;
    private int _pixelY;
    private bool _useHex;
    private string _lastCopied = string.Empty;

    public ColorMagnifierControl()
    {
        Width = LensSize + 20;
        Background = new SolidColorBrush(Color.FromRgb(0x29, 0x25, 0x24));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        BorderThickness = new Thickness(2);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(8, 8, 8, 8);
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 10,
            ShadowDepth = 2,
            Opacity = 0.35
        };

        _lensBitmap = new WriteableBitmap(SourceGrid, SourceGrid, 96, 96, PixelFormats.Bgra32, null);
        _lensImage = new Image
        {
            Width = LensSize,
            Height = LensSize,
            Stretch = Stretch.Fill,
            Source = _lensBitmap,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(_lensImage, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_lensImage, EdgeMode.Aliased);

        var lensFrame = new Border
        {
            Width = LensSize,
            Height = LensSize,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            Background = Brushes.Black,
            Child = new Grid
            {
                Children =
                {
                    _lensImage,
                    // Crosshair
                    new Line { X1 = LensSize / 2.0, Y1 = 0, X2 = LensSize / 2.0, Y2 = LensSize, Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xEF, 0x44, 0x44)), StrokeThickness = 1, IsHitTestVisible = false },
                    new Line { X1 = 0, Y1 = LensSize / 2.0, X2 = LensSize, Y2 = LensSize / 2.0, Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xEF, 0x44, 0x44)), StrokeThickness = 1, IsHitTestVisible = false },
                    new Rectangle
                    {
                        Width = PixelScale,
                        Height = PixelScale,
                        Stroke = new SolidColorBrush(Color.FromRgb(0xFA, 0xF9, 0xF7)),
                        StrokeThickness = 1,
                        Fill = Brushes.Transparent,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false
                    }
                }
            }
        };

        _swatch = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(3),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0xE5, 0xE4)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        _coordText = MakeInfoText();
        _colorText = MakeInfoText();
        _colorText.FontWeight = FontWeights.SemiBold;
        _hintText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA2, 0x9E)),
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = LocalizationService.Current("Press C to copy colour") + "\n" +
                   LocalizationService.Current("Press Shift to switch RGB / HEX")
        };
        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC)),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };

        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        colorRow.Children.Add(_swatch);
        colorRow.Children.Add(_colorText);

        var root = new StackPanel();
        root.Children.Add(lensFrame);
        root.Children.Add(_coordText);
        root.Children.Add(colorRow);
        root.Children.Add(_hintText);
        root.Children.Add(_statusText);
        Child = root;
    }

    public Color CurrentColor => _color;
    public bool UseHexFormat => _useHex;
    public string CurrentColorText => FormatColor(_color, _useHex);

    public void SetUseHex(bool useHex)
    {
        _useHex = useHex;
        RefreshLabels();
    }

    public void ToggleFormat()
    {
        _useHex = !_useHex;
        RefreshLabels();
        FlashStatus(_useHex
            ? LocalizationService.Current("HEX format")
            : LocalizationService.Current("RGB format"));
    }

    public bool TryCopyColor()
    {
        var text = CurrentColorText;
        try
        {
            Clipboard.SetText(text);
            _lastCopied = text;
            FlashStatus(LocalizationService.Format("Copied {0}", text));
            return true;
        }
        catch
        {
            FlashStatus(LocalizationService.Current("Could not copy colour"));
            return false;
        }
    }

    /// <summary>
    /// Updates the lens from the frozen capture bitmap at the overlay pointer position.
    /// </summary>
    public void UpdateFromCapture(BitmapSource screen, Point overlayPoint, double overlayWidth, double overlayHeight)
    {
        if (screen.PixelWidth < 1 || screen.PixelHeight < 1 || overlayWidth < 1 || overlayHeight < 1)
            return;

        var pixelX = (int)Math.Floor(overlayPoint.X * screen.PixelWidth / overlayWidth);
        var pixelY = (int)Math.Floor(overlayPoint.Y * screen.PixelHeight / overlayHeight);
        pixelX = Math.Clamp(pixelX, 0, screen.PixelWidth - 1);
        pixelY = Math.Clamp(pixelY, 0, screen.PixelHeight - 1);
        _pixelX = pixelX;
        _pixelY = pixelY;

        var half = SourceGrid / 2;
        var srcX = Math.Clamp(pixelX - half, 0, Math.Max(0, screen.PixelWidth - SourceGrid));
        var srcY = Math.Clamp(pixelY - half, 0, Math.Max(0, screen.PixelHeight - SourceGrid));
        var width = Math.Min(SourceGrid, screen.PixelWidth - srcX);
        var height = Math.Min(SourceGrid, screen.PixelHeight - srcY);
        if (width < 1 || height < 1) return;

        try
        {
            var cropped = new CroppedBitmap(screen, new Int32Rect(srcX, srcY, width, height));
            var stride = width * 4;
            var pixels = new byte[stride * height];
            cropped.CopyPixels(pixels, stride, 0);

            // Pad into a full SourceGrid×SourceGrid buffer when near edges.
            var full = new byte[SourceGrid * SourceGrid * 4];
            for (var y = 0; y < SourceGrid; y++)
            {
                for (var x = 0; x < SourceGrid; x++)
                {
                    var sx = Math.Clamp(x, 0, width - 1);
                    var sy = Math.Clamp(y, 0, height - 1);
                    var src = (sy * stride) + sx * 4;
                    var dst = (y * SourceGrid + x) * 4;
                    if (x < width && y < height)
                    {
                        full[dst] = pixels[src];
                        full[dst + 1] = pixels[src + 1];
                        full[dst + 2] = pixels[src + 2];
                        full[dst + 3] = pixels[src + 3];
                    }
                    else
                    {
                        full[dst] = full[dst + 1] = full[dst + 2] = 0;
                        full[dst + 3] = 255;
                    }
                }
            }

            _lensBitmap.WritePixels(new Int32Rect(0, 0, SourceGrid, SourceGrid), full, SourceGrid * 4, 0);

            // Center sample colour (BGRA).
            var centerIndex = ((pixelY - srcY) * stride) + (pixelX - srcX) * 4;
            if (centerIndex >= 0 && centerIndex + 3 < pixels.Length)
            {
                _color = Color.FromArgb(pixels[centerIndex + 3], pixels[centerIndex + 2], pixels[centerIndex + 1], pixels[centerIndex]);
            }
            else
            {
                SampleSinglePixel(screen, pixelX, pixelY);
            }

            RefreshLabels();
        }
        catch
        {
            SampleSinglePixel(screen, pixelX, pixelY);
            RefreshLabels();
        }
    }

    private void SampleSinglePixel(BitmapSource screen, int pixelX, int pixelY)
    {
        try
        {
            var cropped = new CroppedBitmap(screen, new Int32Rect(pixelX, pixelY, 1, 1));
            var buf = new byte[4];
            cropped.CopyPixels(buf, 4, 0);
            _color = Color.FromArgb(buf[3], buf[2], buf[1], buf[0]);
        }
        catch
        {
            _color = Colors.Black;
        }
    }

    private void RefreshLabels()
    {
        _coordText.Text = LocalizationService.Format("X {0}   Y {1}", _pixelX, _pixelY);
        _colorText.Text = CurrentColorText;
        _swatch.Background = new SolidColorBrush(_color);
    }

    private void FlashStatus(string message)
    {
        _statusText.Text = message;
        _statusText.Visibility = Visibility.Visible;
    }

    public void ClearStatus() => _statusText.Visibility = Visibility.Collapsed;

    private static TextBlock MakeInfoText() => new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(0xFA, 0xF9, 0xF7)),
        FontSize = 12,
        Margin = new Thickness(0, 6, 0, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    internal static string FormatColor(Color color, bool hex) =>
        hex
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : string.Format(CultureInfo.InvariantCulture, "{0}, {1}, {2}", color.R, color.G, color.B);
}
