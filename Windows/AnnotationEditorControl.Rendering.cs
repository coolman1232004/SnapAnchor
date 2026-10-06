using Microsoft.Win32;
using SnapAnchor.Models;
using SnapAnchor.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Shapes = System.Windows.Shapes;


namespace SnapAnchor.Controls;

public partial class AnnotationEditorControl
{
    private readonly Dictionary<Guid, AnnotationItem> _renderedItems = new();
    private BitmapSource? _renderedSource;

    private void RenderAnnotations()
    {
        _renderPending = false;
        if (_textEditor is not null) return;
        if (!ReferenceEquals(_renderedSource, _source)) { _renderedItems.Clear(); _renderedSource = _source; }
        var existing = _renderedElements.Values.ToHashSet();
        foreach (var child in AnnotationCanvas.Children.OfType<FrameworkElement>().Where(child => !existing.Contains(child)).ToList())
            AnnotationCanvas.Children.Remove(child);
        var ids = _items.Select(item => item.Id).ToHashSet();
        foreach (var removedId in _renderedElements.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            AnnotationCanvas.Children.Remove(_renderedElements[removedId]);
            _renderedElements.Remove(removedId);
            _renderedItems.Remove(removedId);
        }
        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            if (_renderedItems.TryGetValue(item.Id, out var previous) && item.VisuallyEquals(previous) && _renderedElements.TryGetValue(item.Id, out var unchanged))
            {
                Panel.SetZIndex(unchanged, index);
                continue;
            }
            if (_renderedElements.Remove(item.Id, out var old)) AnnotationCanvas.Children.Remove(old);
            var element = CreateElement(item);
            if (element is null) continue;
            Panel.SetZIndex(element, index);
            AnnotationCanvas.Children.Add(element);
            _renderedElements[item.Id] = element;
            _renderedItems[item.Id] = item.Clone();
        }

        if (!_dragging && _selectedId is Guid id && _items.FirstOrDefault(item => item.Id == id) is { } selected)
        {
            var bounds = Bounds(selected);
            AddSelectionHandles(selected, bounds);
        }
        var rendered = _renderedElements.Values.ToHashSet();
        foreach (var child in AnnotationCanvas.Children.OfType<FrameworkElement>().Where(child => !rendered.Contains(child)))
            Panel.SetZIndex(child, _items.Count);
    }

    private void RequestRender()
    {
        if (_renderPending) return;
        _renderPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(RenderAnnotations));
    }

    private void AddSelectionHandles(AnnotationItem item, Rect bounds)
    {
        if (item.Kind == AnnotationKind.Arrow && item.Points.Count >= 2)
        {
            AddArrowSelectionHandles(item);
            return;
        }

        var centerX = bounds.X + bounds.Width / 2;
        var centerY = bounds.Y + bounds.Height / 2;
        AddHandle(item.Id, "NW", bounds.Left, bounds.Top, Cursors.SizeNWSE);
        AddHandle(item.Id, "N", centerX, bounds.Top, Cursors.SizeNS);
        AddHandle(item.Id, "NE", bounds.Right, bounds.Top, Cursors.SizeNESW);
        AddHandle(item.Id, "E", bounds.Right, centerY, Cursors.SizeWE);
        AddHandle(item.Id, "SE", bounds.Right, bounds.Bottom, Cursors.SizeNWSE);
        AddHandle(item.Id, "S", centerX, bounds.Bottom, Cursors.SizeNS);
        AddHandle(item.Id, "SW", bounds.Left, bounds.Bottom, Cursors.SizeNESW);
        AddHandle(item.Id, "W", bounds.Left, centerY, Cursors.SizeWE);
    }

    private void AddHandle(Guid itemId, string handle, double x, double y, Cursor cursor)
    {
        const double size = 7;
        var grip = new Shapes.Rectangle
        {
            Width = size,
            Height = size,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
            StrokeThickness = 1,
            Cursor = cursor,
            Tag = new AnnotationHandleTag(itemId, handle),
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(grip, x - size / 2);
        Canvas.SetTop(grip, y - size / 2);
        Panel.SetZIndex(grip, 1000);
        AnnotationCanvas.Children.Add(grip);
    }

    private void AddArrowSelectionHandles(AnnotationItem item)
    {
        var start = item.Points[0];
        var end = item.Points[^1];
        var midpoint = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        var endpointCursor = ArrowEndpointCursor(start, end);
        AddArrowHandle(item.Id, "ArrowStart", start, endpointCursor, filled: false);
        AddArrowHandle(item.Id, "ArrowMove", midpoint, Cursors.SizeAll, filled: true);
        AddArrowHandle(item.Id, "ArrowEnd", end, endpointCursor, filled: false);
    }

    private static Cursor ArrowEndpointCursor(Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var absoluteX = Math.Abs(dx);
        var absoluteY = Math.Abs(dy);
        if (absoluteX >= absoluteY * 2) return Cursors.SizeWE;
        if (absoluteY >= absoluteX * 2) return Cursors.SizeNS;
        return dx * dy >= 0 ? Cursors.SizeNWSE : Cursors.SizeNESW;
    }

    private void AddArrowHandle(Guid itemId, string handle, Point point, Cursor cursor, bool filled)
    {
        const double size = 9;
        var accent = BrushFrom(_settings.CaptureBorderColor);
        var grip = new Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Fill = filled ? accent : Brushes.White,
            Stroke = accent,
            StrokeThickness = 1.5,
            Cursor = cursor,
            Tag = new AnnotationHandleTag(itemId, handle),
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(grip, point.X - size / 2);
        Canvas.SetTop(grip, point.Y - size / 2);
        Panel.SetZIndex(grip, 1000);
        AnnotationCanvas.Children.Add(grip);
    }

    private FrameworkElement? CreateElement(AnnotationItem item)
    {
        var brush = BrushFrom(item.Color);
        FrameworkElement element;
        switch (item.Kind)
        {
            case AnnotationKind.Rectangle:
                element = new Shapes.Rectangle { Stroke = brush, StrokeThickness = item.Thickness, Fill = FillBrush(item), StrokeDashArray = DashArray(item) };
                Place(element, item);
                break;
            case AnnotationKind.Ellipse:
                element = new Shapes.Ellipse { Stroke = brush, StrokeThickness = item.Thickness, Fill = FillBrush(item), StrokeDashArray = DashArray(item) };
                Place(element, item);
                break;
            case AnnotationKind.Line:
                element = StrokePolyline(item, brush, false);
                break;
            case AnnotationKind.Arrow:
                element = StrokePolyline(item, brush, true);
                break;
            case AnnotationKind.Pencil:
                element = StrokePolyline(item, brush, false);
                break;
            case AnnotationKind.Marker:
                element = StrokePolyline(item, brush, false);
                element.Opacity = 0.38;
                break;
            case AnnotationKind.Text:
                var textBlock = new TextBlock
                {
                    Text = item.Text,
                    Foreground = brush,
                    FontSize = Math.Max(9, item.Thickness * 4),
                    FontFamily = new FontFamily(item.FontFamily),
                    FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal,
                    TextDecorations = TextDecorationsFor(item.Underline, item.Strikethrough),
                    TextAlignment = Enum.TryParse<TextAlignment>(item.TextAlignment, out var alignment) ? alignment : TextAlignment.Left,
                    TextWrapping = TextWrapping.Wrap,
                    Width = Math.Max(40, item.Width),
                    Height = Math.Max(24, item.Height),
                    Padding = new Thickness(5, 3, 5, 3),
                    Background = Brushes.Transparent
                };
                if (item.Outline)
                    textBlock.Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 1.5, ShadowDepth = 0, Opacity = 1 };
                element = textBlock;
                element.Tag = item.Id;
                Canvas.SetLeft(element, item.X);
                Canvas.SetTop(element, item.Y);
                break;
            case AnnotationKind.Number:
                element = new Border
                {
                    CornerRadius = new CornerRadius(Math.Max(4, item.Width / 2)),
                    Background = brush,
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(Math.Max(1, item.Thickness / 2)),
                    Child = new TextBlock { Text = item.Text, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = Math.Max(15, item.Height * 0.46), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                Place(element, item);
                break;
            case AnnotationKind.Callout:
                element = CalloutElement(item, brush);
                Place(element, item);
                break;
            case AnnotationKind.Mosaic:
                element = EffectElement(item, pixelate: true);
                break;
            case AnnotationKind.Blur:
                element = item.Points.Count > 0 ? BlurBrushElement(item) : EffectElement(item, pixelate: false);
                break;
            case AnnotationKind.Magnify:
                element = MagnifyElement(item);
                break;
            case AnnotationKind.Eraser:
                element = EraserBrushElement(item);
                break;
            default:
                return null;
        }
        element.Tag = item.Id;
        element.Opacity = Math.Clamp(item.Kind == AnnotationKind.Marker ? item.Opacity * 0.38 : item.Opacity, 0.12, 1);
        if (Math.Abs(item.Rotation) > 0.01)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = new RotateTransform(item.Rotation);
        }
        element.Cursor = Cursors.SizeAll;
        return element;
    }

    private FrameworkElement BlurBrushElement(AnnotationItem item)
    {
        return BrushImagePath(EnsureBlurredSource(), item, BitmapScalingMode.HighQuality);
    }

    private FrameworkElement EraserBrushElement(AnnotationItem item)
    {
        return BrushImagePath(_source, item, BitmapScalingMode.NearestNeighbor);
    }

    private FrameworkElement BrushImagePath(BitmapSource source, AnnotationItem item, BitmapScalingMode scalingMode)
    {
        var surfaceBounds = new Rect(0, 0, Math.Max(1, _source.PixelWidth), Math.Max(1, _source.PixelHeight));
        var sourceBrush = new ImageBrush(source)
        {
            Stretch = Stretch.Fill,
            Viewbox = surfaceBounds,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = surfaceBounds,
            ViewportUnits = BrushMappingMode.Absolute,
            TileMode = TileMode.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };
        var path = new Shapes.Path
        {
            Data = BrushStrokeGeometry(item),
            Fill = Brushes.Transparent,
            Stroke = sourceBrush,
            StrokeThickness = Math.Max(3, item.Thickness),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
            Tag = item.Id
        };
        RenderOptions.SetBitmapScalingMode(path, scalingMode);
        return path;
    }

    private BitmapSource EnsureBlurredSource()
    {
        if (_blurredSource is not null) return _blurredSource;
        var width = Math.Max(1, _source.PixelWidth);
        var height = Math.Max(1, _source.PixelHeight);
        var image = new Image
        {
            Source = _source,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            Effect = new BlurEffect
            {
                Radius = 12,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Quality
            }
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.Measure(new Size(width, height));
        image.Arrange(new Rect(0, 0, width, height));
        image.UpdateLayout();
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(image);
        result.Freeze();
        _blurredSource = result;
        return result;
    }

    private static Geometry BrushStrokeGeometry(AnnotationItem item)
    {
        var points = item.Points.Count > 0 ? item.Points : [new Point(item.X, item.Y)];
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            if (points.Count == 1)
                context.LineTo(new Point(points[0].X + 0.01, points[0].Y), isStroked: true, isSmoothJoin: true);
            else
                context.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }

    private FrameworkElement StrokePolyline(AnnotationItem item, Brush brush, bool arrow)
    {
        var points = item.Points.Count > 0 ? item.Points.ToList() : [new Point(item.X, item.Y)];
        if (arrow && points.Count >= 2) return ArrowElement(item, brush, points[0], points[^1]);
        var line = new Shapes.Polyline
        {
            Points = new PointCollection(points),
            Stroke = brush,
            StrokeThickness = item.Kind == AnnotationKind.Marker ? Math.Max(8, item.Thickness) : item.Thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = Brushes.Transparent,
            StrokeDashArray = DashArray(item),
            Tag = item.Id
        };
        return line;
    }

    private FrameworkElement ArrowElement(AnnotationItem item, Brush brush, Point start, Point end)
    {
        var geometry = new GeometryGroup();
        geometry.Children.Add(new LineGeometry(start, end));
        if (item.ArrowHeads is "End" or "Both") AddArrowHead(geometry, start, end, item.Thickness);
        if (item.ArrowHeads == "Both") AddArrowHead(geometry, end, start, item.Thickness);
        return new Shapes.Path
        {
            Data = geometry,
            Stroke = brush,
            StrokeThickness = item.Thickness,
            StrokeDashArray = DashArray(item),
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = Brushes.Transparent,
            Tag = item.Id
        };
    }

    private static void AddArrowHead(GeometryGroup geometry, Point start, Point end, double thickness)
    {
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var length = Math.Max(12, thickness * 4);
        var left = new Point(end.X - length * Math.Cos(angle - Math.PI / 6), end.Y - length * Math.Sin(angle - Math.PI / 6));
        var right = new Point(end.X - length * Math.Cos(angle + Math.PI / 6), end.Y - length * Math.Sin(angle + Math.PI / 6));
        geometry.Children.Add(new LineGeometry(end, left));
        geometry.Children.Add(new LineGeometry(end, right));
    }

    private FrameworkElement CalloutElement(AnnotationItem item, Brush brush)
    {
        var grid = new Grid();
        var bubble = new Border
        {
            Margin = new Thickness(0, 0, 0, 14),
            CornerRadius = new CornerRadius(10),
            Background = item.FillOpacity > 0 ? FillBrush(item) : new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
            BorderBrush = brush,
            BorderThickness = new Thickness(item.Thickness),
            Child = new TextBlock
            {
                Text = item.Text,
                Foreground = brush,
                FontSize = Math.Max(9, item.Thickness * 4),
                FontFamily = new FontFamily(item.FontFamily),
                FontWeight = item.Bold ? FontWeights.Bold : FontWeights.SemiBold,
                FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal,
                TextDecorations = TextDecorationsFor(item.Underline, item.Strikethrough),
                TextAlignment = Enum.TryParse<TextAlignment>(item.TextAlignment, out var alignment) ? alignment : TextAlignment.Left,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(10, 7, 10, 7)
            }
        };
        var tail = new Shapes.Polygon
        {
            Points = new PointCollection([new Point(24, Math.Max(12, item.Height - 17)), new Point(24, item.Height), new Point(50, Math.Max(12, item.Height - 17))]),
            Fill = bubble.Background,
            Stroke = brush,
            StrokeThickness = item.Thickness,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        grid.Children.Add(tail);
        grid.Children.Add(bubble);
        return grid;
    }

    private static DoubleCollection? DashArray(AnnotationItem item) => item.Dashed ? new DoubleCollection([4, 3]) : null;

    private static Brush FillBrush(AnnotationItem item)
    {
        if (item.FillOpacity <= 0) return Brushes.Transparent;
        if (BrushFrom(item.Color) is not SolidColorBrush color) return Brushes.Transparent;
        return new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(item.FillOpacity, 0, 1)), color.Color.R, color.Color.G, color.Color.B));
    }

    private FrameworkElement EffectElement(AnnotationItem item, bool pixelate)
    {
        var rect = Bounds(item);
        var cropRect = new Int32Rect(
            Math.Clamp((int)rect.X, 0, Math.Max(0, _source.PixelWidth - 1)),
            Math.Clamp((int)rect.Y, 0, Math.Max(0, _source.PixelHeight - 1)),
            Math.Clamp((int)Math.Max(1, rect.Width), 1, Math.Max(1, _source.PixelWidth - Math.Clamp((int)rect.X, 0, Math.Max(0, _source.PixelWidth - 1)))),
            Math.Clamp((int)Math.Max(1, rect.Height), 1, Math.Max(1, _source.PixelHeight - Math.Clamp((int)rect.Y, 0, Math.Max(0, _source.PixelHeight - 1)))));
        var crop = new CroppedBitmap(_source, cropRect);
        crop.Freeze();
        var image = new Image
        {
            Source = pixelate ? Pixelate(crop) : crop,
            Stretch = Stretch.Fill,
            Effect = pixelate ? null : new BlurEffect { Radius = Math.Max(8, item.Thickness * 2) },
            Tag = item.Id
        };
        if (pixelate) RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var border = new Border { Child = image, ClipToBounds = true, Tag = item.Id };
        ApplyEffectClip(border, item);
        Place(border, item);
        return border;
    }

    private FrameworkElement MagnifyElement(AnnotationItem item)
    {
        var rect = Bounds(item);
        const double zoom = 2.5;
        var cropWidth = Math.Max(1, rect.Width / zoom);
        var cropHeight = Math.Max(1, rect.Height / zoom);
        var x = rect.X + (rect.Width - cropWidth) / 2;
        var y = rect.Y + (rect.Height - cropHeight) / 2;
        var ix = Math.Clamp((int)x, 0, Math.Max(0, _source.PixelWidth - 1));
        var iy = Math.Clamp((int)y, 0, Math.Max(0, _source.PixelHeight - 1));
        var cropRect = new Int32Rect(
            ix, iy,
            Math.Clamp((int)Math.Max(1, cropWidth), 1, Math.Max(1, _source.PixelWidth - ix)),
            Math.Clamp((int)Math.Max(1, cropHeight), 1, Math.Max(1, _source.PixelHeight - iy)));
        var crop = new CroppedBitmap(_source, cropRect);
        crop.Freeze();
        var image = new Image { Source = crop, Stretch = Stretch.Fill, Tag = item.Id };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var border = new Border
        {
            Child = image,
            ClipToBounds = true,
            BorderBrush = BrushFrom(item.Color),
            BorderThickness = new Thickness(Math.Max(1, item.Thickness / 2)),
            Tag = item.Id
        };
        ApplyEffectClip(border, item);
        Place(border, item);
        return border;
    }

    private static void ApplyEffectClip(FrameworkElement element, AnnotationItem item)
    {
        if (item.EffectShape == "Ellipse")
            element.Clip = new EllipseGeometry(new Rect(0, 0, Math.Max(1, item.Width), Math.Max(1, item.Height)));
    }

    private static BitmapSource Pixelate(BitmapSource source)
    {
        var width = Math.Max(1, source.PixelWidth / 14);
        var height = Math.Max(1, source.PixelHeight / 14);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawImage(source, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Place(FrameworkElement element, AnnotationItem item)
    {
        element.Width = Math.Max(1, item.Width);
        element.Height = Math.Max(1, item.Height);
        Canvas.SetLeft(element, item.X);
        Canvas.SetTop(element, item.Y);
    }

}
