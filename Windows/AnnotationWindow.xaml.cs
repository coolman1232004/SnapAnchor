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

internal sealed record AnnotationAppliedEventArgs(BitmapSource BaseImage, BitmapSource FlattenedImage, IReadOnlyList<AnnotationItem> Items);

internal sealed record AnnotationHandleTag(Guid ItemId, string Handle);

public partial class AnnotationEditorControl : UserControl
{
    private BitmapSource _source = null!;
    private BitmapSource? _blurredSource;
    private readonly AppSettings _settings;
    private List<AnnotationItem> _items = [];
    private readonly Stack<List<AnnotationItem>> _undo = [];
    private readonly Stack<List<AnnotationItem>> _redo = [];
    private readonly Dictionary<Guid, FrameworkElement> _renderedElements = [];
    private string _tool = "Select";
    private Guid? _activeId;
    private Guid? _selectedId;
    private Point _start;
    private Point _last;
    private bool _dragging;
    private TextBox? _textEditor;
    private Grid? _textEditorFrame;
    private bool _textBoxDragging;
    private Point _textBoxStart;
    private Guid? _editingTextId;
    private AnnotationKind _pendingTextKind = AnnotationKind.Text;
    private int _nextNumber = 1;
    private bool _fillEnabled;
    private bool _dashed;
    private bool _loadingStyle = true;
    private bool _renderPending;
    private string _transformMode = string.Empty;
    private Point _transformStart;
    private AnnotationItem? _transformOriginal;
    private bool _captureOverlayMode;
    private Rect _captureSurfaceBounds;
    private Rect _captureToolbarAnchor;
    private Size _captureViewport;
    private double? _captureToolbarLeft;
    private double? _captureToolbarTop;
    private bool _textBold;
    private bool _textItalic;
    private bool _textUnderline;
    private bool _textStrikethrough;
    private TextAlignment _textAlignment = TextAlignment.Left;
    private bool _textOutline;
    private FrameworkElement? _customToolCursor;
    private bool _customToolCursorCentered;
    private bool _surfacePointerInside;
    private bool _externalBackgroundMode;
    private bool _allowToolToggleOff;
    private bool _externalSurfaceDragging;
    private Point _externalSurfaceLast;
    private string _selectedColor = "#FFEF4444";
    private readonly Dictionary<string, double> _toolSizes = new(StringComparer.OrdinalIgnoreCase);

    internal event Action<AnnotationAppliedEventArgs>? Applied;
    internal event Action<AnnotationAppliedEventArgs>? DocumentStored;
    internal event Action<Vector>? ExternalSurfaceMoved;
    internal event Action? ExternalSurfaceMoveCompleted;
    public event EventHandler? Cancelled;

    internal string ActiveTool => _tool;

    public AnnotationEditorControl()
    {
        _settings = SettingsService.Load();
        ToolbarThemeService.ApplyTo(Resources, _settings.ToolbarSizeMode);
        _toolSizes["Shape"] = _settings.AnnotationShapeSize;
        _toolSizes["Arrow"] = _settings.AnnotationArrowSize;
        _toolSizes["Pencil"] = _settings.AnnotationPencilSize;
        _toolSizes["Marker"] = _settings.AnnotationMarkerSize;
        _toolSizes["Blur"] = _settings.AnnotationBlurSize;
        _toolSizes["Text"] = _settings.AnnotationTextSize;
        _toolSizes["Eraser"] = _settings.AnnotationEraserSize;
        InitializeComponent();
        ApplyToolbarConfiguration(_settings.AnnotationToolbarOrder, _settings.AnnotationToolbarEnabled);
        LoadStyleSettings();
        Loaded += (_, _) =>
        {
            AccessibilityService.ApplyToolbar(ToolbarHost);
            Focus();
        };
        SizeChanged += (_, _) => { if (_captureOverlayMode) LayoutCaptureOverlay(); };
    }

    internal void LoadImage(BitmapSource source, IEnumerable<AnnotationItem>? items = null)
    {
        _source = source;
        _blurredSource = null;
        _items = items?.Select(item => item.Clone()).ToList() ?? [];
        _undo.Clear();
        _redo.Clear();
        _selectedId = null;
        _activeId = null;
        _nextNumber = Math.Max(1, _items.Where(item => item.Kind == AnnotationKind.Number).Select(item => int.TryParse(item.Text, out var value) ? value + 1 : 1).DefaultIfEmpty(1).Max());
        BackgroundImage.Source = source;
        Surface.Width = Math.Max(1, source.PixelWidth);
        Surface.Height = Math.Max(1, source.PixelHeight);
        RenderAnnotations();
        if (FindToolButton(_tool) is not { Visibility: Visibility.Visible } currentButton && FirstVisibleToolButton() is { } firstButton)
            ActivateTool(firstButton.Tag as string ?? "Rectangle", firstButton);
        Focus();
    }

    internal void SetExternalBackgroundMode(bool enabled)
    {
        _externalBackgroundMode = enabled;
        BackgroundImage.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        // A fully transparent layered window can be skipped by Win32 hit testing.
        // Keep one imperceptible alpha value over the external pin so drawing
        // gestures reach this editor while the original pinned image remains
        // the only visible image.
        var inputSurface = enabled
            ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0))
            : Brushes.Transparent;
        SurfaceHost.Background = inputSurface;
        AnnotationCanvas.Background = inputSurface;
    }

    internal void ApplyToolbarConfiguration(IEnumerable<string>? order, IEnumerable<string>? enabled)
    {
        var normalizedOrder = AnnotationToolbarCatalog.NormalizeOrder(order);
        var normalizedEnabled = AnnotationToolbarCatalog.NormalizeEnabled(enabled).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var buttons = ToolPanel.Children.OfType<Button>()
            .Where(button => button.Tag is string)
            .ToDictionary(button => (string)button.Tag, StringComparer.OrdinalIgnoreCase);

        foreach (var button in buttons.Values.ToList()) ToolPanel.Children.Remove(button);
        foreach (var key in normalizedOrder)
        {
            if (!buttons.TryGetValue(key, out var button)) continue;
            button.Visibility = normalizedEnabled.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
            ToolPanel.Children.Add(button);
        }
    }

    internal void ActivateConfiguredTool(string tool)
    {
        if (FindToolButton(tool) is { Visibility: Visibility.Visible } button)
            ActivateTool(tool, button);
    }

    internal void ToggleConfiguredTool(string tool)
    {
        if (FindToolButton(tool) is not { Visibility: Visibility.Visible } button) return;
        if (_allowToolToggleOff && _tool.Equals(tool, StringComparison.OrdinalIgnoreCase))
            DeactivateTool();
        else
            ActivateTool(tool, button);
    }

    private Button? FindToolButton(string tool) => ToolPanel.Children.OfType<Button>()
        .FirstOrDefault(button => button.Tag is string tag && tag.Equals(tool, StringComparison.OrdinalIgnoreCase));

    private Button? FirstVisibleToolButton() => ToolPanel.Children.OfType<Button>()
        .FirstOrDefault(button => button.Visibility == Visibility.Visible && button.Tag is string);

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        CommitTextEditor();
        if (sender is not Button { Tag: string tool }) return;
        ToggleConfiguredTool(tool);
    }

    private void DeactivateTool()
    {
        RememberCurrentToolSize();
        foreach (var button in ToolPanel.Children.OfType<Button>())
        {
            button.Background = Brushes.Transparent;
            button.Foreground = TryFindResource("ToolbarInkBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(51, 65, 85));
        }
        _tool = "None";
        _selectedId = null;
        _activeId = null;
        StatusText.Text = L("Drag the pinned image to move it, or choose a drawing tool.");
        UpdateSurfaceCursor(_tool);
        UpdateToolOptions(_tool);
        RenderAnnotations();
    }

    private void PaletteColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color }) return;
        SetSelectedColor(color);
        ApplyTextEditorStyle();
        ApplyStyleToSelected();
        SaveStyleSettings();
    }

    private void SelectedColorPreview_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        SetSelectedColor($"#FF{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        ApplyTextEditorStyle();
        ApplyStyleToSelected();
        SaveStyleSettings();
    }

    private void SetSelectedColor(string color)
    {
        _selectedColor = color;
        var wasLoading = _loadingStyle;
        _loadingStyle = true;
        ColorBox.SelectedValue = color;
        _loadingStyle = wasLoading;
        SelectedColorFill.Background = BrushFrom(color);
    }

    private void SizeCycle_Click(object sender, RoutedEventArgs e)
    {
        double[] sizes = _tool is "Eraser" or "Blur"
            ? [8, 12, 18, 24, 32, 48, 64, 96]
            : [1, 2, 4, 6, 10, 16, 21, 24];
        var index = Array.FindIndex(sizes, size => size > ThicknessSlider.Value + 0.1);
        ThicknessSlider.Value = index < 0 ? sizes[0] : sizes[index];
        UpdateToolSizeLabels();
    }

    private void UpdateToolSizeLabels()
    {
        var label = Math.Round(ThicknessSlider.Value).ToString();
        ShapeSizeButton.Content = ThicknessSlider.Value <= 4 ? "•" : label;
        ArrowSizeButton.Content = ThicknessSlider.Value <= 4 ? "•" : label;
        PencilSizeButton.Content = ThicknessSlider.Value <= 4 ? "•" : label;
        MarkerSizeButton.Content = label;
        BlurSizeButton.Content = label;
        EraserSizeButton.Content = label;
        BasicSizeButton.Content = label;
    }

    private void TextStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender == TextBoldButton) _textBold = !_textBold;
        else if (sender == TextItalicButton) _textItalic = !_textItalic;
        else if (sender == TextUnderlineButton) _textUnderline = !_textUnderline;
        else if (sender == TextStrikeButton) _textStrikethrough = !_textStrikethrough;
        else if (sender == TextAlignButton) _textAlignment = _textAlignment switch
        {
            TextAlignment.Left => TextAlignment.Center,
            TextAlignment.Center => TextAlignment.Right,
            _ => TextAlignment.Left
        };
        else if (sender == TextOutlineButton) _textOutline = !_textOutline;
        UpdateStyleButtons();
        ApplyTextEditorStyle();
        ApplyStyleToSelected();
        SaveStyleSettings();
    }

    private void TextFont_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStyle) return;
        ApplyTextEditorStyle();
        ApplyStyleToSelected();
        SaveStyleSettings();
    }

    private void TextSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStyle || !double.TryParse(TextSizeBox.SelectedValue as string, out var value)) return;
        ThicknessSlider.Value = Math.Clamp(value / 4.0, 1, 24);
        ApplyTextEditorStyle();
        UpdateToolSizeLabels();
    }

    private void ActivateTool(string tool, Button activeButton)
    {
        RememberCurrentToolSize();
        foreach (var button in ToolPanel.Children.OfType<Button>())
        {
            button.Background = Brushes.Transparent;
            button.Foreground = TryFindResource("ToolbarInkBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(51, 65, 85));
        }
        activeButton.Background = TryFindResource("ToolbarActiveBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(204, 251, 241));
        activeButton.Foreground = TryFindResource("ToolbarActiveInkBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(13, 148, 136));
        _tool = tool;
        var wasLoading = _loadingStyle;
        _loadingStyle = true;
        ThicknessSlider.Maximum = tool is "Eraser" or "Blur" ? 96 : 24;
        ThicknessSlider.Value = Math.Clamp(_toolSizes.GetValueOrDefault(ToolSizeKey(tool), 4), 1, ThicknessSlider.Maximum);
        if (tool is "Text" or "Callout")
        {
            TextSizeBox.SelectedValue = Math.Clamp((int)Math.Round(ThicknessSlider.Value * 4), 9, 72).ToString();
            if (TextSizeBox.SelectedIndex < 0) TextSizeBox.SelectedValue = "16";
        }
        _loadingStyle = wasLoading;
        _selectedId = null;
        StatusText.Text = tool switch
        {
            "Select" => L("Click an annotation to select it, then drag to move it."),
            "Text" => L("Click to place text. Enter adds a line; Ctrl+Enter finishes."),
            "Callout" => L("Click the image, type the callout, then press Ctrl+Enter."),
            "Number" => L("Click the image to add the next numbered marker."),
            "Eraser" => L("Drag the eraser over only the pixels you want to remove."),
            "Blur" => L("Drag to paint blur directly where the brush passes."),
            _ => LocalizationService.Format("Drag on the image to add {0}.", L(tool))
        };
        UpdateSurfaceCursor(tool);
        UpdateToolOptions(tool);
        UpdateToolSizeLabels();
        RenderAnnotations();
    }

    private void UpdateSurfaceCursor(string tool)
    {
        ToolCursorLayer.Children.Clear();
        _customToolCursor = null;
        _customToolCursorCentered = false;
        Surface.Cursor = tool switch
        {
            "None" => _externalBackgroundMode ? Cursors.SizeAll : Cursors.Arrow,
            "Select" => Cursors.Arrow,
            "Text" => Cursors.IBeam,
            "Pencil" or "Marker" => Cursors.Pen,
            "Eraser" or "Blur" => Cursors.None,
            _ => Cursors.Cross
        };

        if (tool == "Blur")
        {
            var size = Math.Max(7, ThicknessSlider.Value);
            var brushPreview = new Shapes.Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Color.FromArgb(44, 71, 84, 103)),
                Stroke = Brushes.White,
                StrokeThickness = Math.Max(1, 1.5 / Math.Max(0.25, size / 21)),
                IsHitTestVisible = false,
                Visibility = _surfacePointerInside ? Visibility.Visible : Visibility.Collapsed
            };
            _customToolCursor = brushPreview;
            _customToolCursorCentered = true;
            ToolCursorLayer.Children.Add(brushPreview);
            return;
        }

        if (tool != "Eraser") return;
        var eraserSize = Math.Max(8, ThicknessSlider.Value);
        var eraser = new Border
        {
            Width = eraserSize,
            Height = eraserSize,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(38, 248, 113, 113)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(2),
            Visibility = _surfacePointerInside ? Visibility.Visible : Visibility.Collapsed
        };
        eraser.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 1, ShadowDepth = 0, Opacity = 0.9 };
        _customToolCursor = eraser;
        _customToolCursorCentered = true;
        ToolCursorLayer.Children.Add(eraser);
    }

    private void Surface_MouseEnter(object sender, MouseEventArgs e)
    {
        _surfacePointerInside = true;
        if (_customToolCursor is not null) _customToolCursor.Visibility = Visibility.Visible;
    }

    private void Surface_MouseLeave(object sender, MouseEventArgs e)
    {
        _surfacePointerInside = false;
        if (_customToolCursor is not null) _customToolCursor.Visibility = Visibility.Collapsed;
    }

    private void PositionCustomToolCursor(Point point, bool visible)
    {
        if (_customToolCursor is null) return;
        _customToolCursor.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        var width = _customToolCursor.Width;
        var height = _customToolCursor.Height;
        var left = _customToolCursorCentered ? point.X - width / 2 : point.X + 5;
        var top = _customToolCursorCentered ? point.Y - height / 2 : point.Y - 20;
        Canvas.SetLeft(_customToolCursor, Math.Clamp(left, 0, Math.Max(0, Surface.Width - width)));
        Canvas.SetTop(_customToolCursor, Math.Clamp(top, 0, Math.Max(0, Surface.Height - height)));
    }

    private static string ToolSizeKey(string tool) => tool switch
    {
        "Rectangle" or "Ellipse" or "Number" or "Callout" or "Magnify" => "Shape",
        "Arrow" or "Line" => "Arrow",
        _ => tool
    };

    private void RememberCurrentToolSize()
    {
        var key = ToolSizeKey(_tool);
        if (_toolSizes.ContainsKey(key)) _toolSizes[key] = ThicknessSlider.Value;
    }

    private void UpdateToolOptions(string tool)
    {
        ShapeOptionsPanel.Visibility = tool is "Rectangle" or "Ellipse" ? Visibility.Visible : Visibility.Collapsed;
        ArrowOptionsPanel.Visibility = tool is "Arrow" or "Line" ? Visibility.Visible : Visibility.Collapsed;
        PencilOptionsPanel.Visibility = tool == "Pencil" ? Visibility.Visible : Visibility.Collapsed;
        MarkerOptionsPanel.Visibility = tool == "Marker" ? Visibility.Visible : Visibility.Collapsed;
        BlurOptionsPanel.Visibility = tool == "Blur" ? Visibility.Visible : Visibility.Collapsed;
        BasicOptionsPanel.Visibility = tool is "Number" or "Magnify" ? Visibility.Visible : Visibility.Collapsed;
        TextOptionsPanel.Visibility = tool is "Text" or "Callout" ? Visibility.Visible : Visibility.Collapsed;
        EraserOptionsPanel.Visibility = tool == "Eraser" ? Visibility.Visible : Visibility.Collapsed;
        ColorPalettePanel.Visibility = tool is "Rectangle" or "Ellipse" or "Arrow" or "Line" or "Pencil" or "Marker" or "Text" or "Callout" or "Number" or "Magnify"
            ? Visibility.Visible : Visibility.Collapsed;
        PropertiesToolbarFrame.Visibility = tool is "Select" or "None" ? Visibility.Collapsed : Visibility.Visible;
        UpdateStyleButtons();
        if (_captureOverlayMode) Dispatcher.BeginInvoke(LayoutCaptureOverlay);
    }

    internal void ConfigureCaptureOverlay(Rect surfaceBounds, Size viewport, Rect? toolbarAnchor = null, bool showActions = false,
        double? toolbarLeft = null, double? toolbarTop = null, bool showPrimaryToolbar = true, bool showCancelAction = false,
        bool startWithNoTool = false, bool allowToolToggleOff = false)
    {
        _captureOverlayMode = true;
        _captureSurfaceBounds = surfaceBounds;
        _captureToolbarAnchor = toolbarAnchor ?? surfaceBounds;
        _captureViewport = viewport;
        _captureToolbarLeft = toolbarLeft;
        _captureToolbarTop = toolbarTop;
        Width = Math.Max(1, viewport.Width);
        Height = Math.Max(1, viewport.Height);
        ActionSeparator.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        CopyActionButton.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        SaveActionButton.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        ApplyActionButton.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        CancelActionButton.Visibility = showCancelAction ? Visibility.Visible : Visibility.Collapsed;
        PrimaryToolbarFrame.Visibility = showPrimaryToolbar ? Visibility.Visible : Visibility.Collapsed;
        _allowToolToggleOff = allowToolToggleOff;
        Grid.SetRowSpan(SurfaceHost, 2);
        SurfaceHost.BorderThickness = new Thickness(0);
        SurfaceHost.HorizontalAlignment = HorizontalAlignment.Left;
        SurfaceHost.VerticalAlignment = VerticalAlignment.Top;
        if (startWithNoTool)
            DeactivateTool();
        else if (FirstVisibleToolButton() is { } firstButton)
            ActivateTool(firstButton.Tag as string ?? "Rectangle", firstButton);
        Dispatcher.BeginInvoke(LayoutCaptureOverlay);
    }

    internal void UpdateCaptureToolbarPosition(double left, double top)
    {
        if (!_captureOverlayMode) return;
        _captureToolbarLeft = left;
        _captureToolbarTop = top;
        Dispatcher.BeginInvoke(LayoutCaptureOverlay);
    }

    internal void SetCapturePropertiesToolbarVisible(bool visible)
    {
        if (!_captureOverlayMode) return;
        PropertiesToolbarFrame.Visibility = visible && _tool is not ("Select" or "None")
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (visible) Dispatcher.BeginInvoke(LayoutCaptureOverlay);
    }

    internal void UndoForCapture() => Undo();
    internal void RedoForCapture() => Redo();

    internal void UpdateCaptureOverlayBounds(Rect surfaceBounds, Size viewport, Rect? toolbarAnchor = null,
        bool resetToolbarPosition = false)
    {
        if (!_captureOverlayMode) return;
        _captureSurfaceBounds = surfaceBounds;
        if (toolbarAnchor is { } anchor) _captureToolbarAnchor = anchor;
        if (resetToolbarPosition)
        {
            _captureToolbarLeft = null;
            _captureToolbarTop = null;
        }
        _captureViewport = viewport;
        Width = Math.Max(1, viewport.Width);
        Height = Math.Max(1, viewport.Height);
        Dispatcher.BeginInvoke(LayoutCaptureOverlay);
    }

    internal void TranslateCaptureOverlay(Vector delta)
    {
        if (!_captureOverlayMode || delta.LengthSquared < 0.0001) return;
        _captureSurfaceBounds.Offset(delta.X, delta.Y);
        _captureToolbarAnchor.Offset(delta.X, delta.Y);
        if (_captureToolbarLeft is { } left) _captureToolbarLeft = left + delta.X;
        if (_captureToolbarTop is { } top) _captureToolbarTop = top + delta.Y;
        Dispatcher.BeginInvoke(LayoutCaptureOverlay);
    }

    private void LayoutCaptureOverlay()
    {
        if (!_captureOverlayMode || _captureViewport.Width <= 1 || _captureViewport.Height <= 1) return;
        var bounds = Rect.Intersect(new Rect(new Point(), _captureViewport), _captureSurfaceBounds);
        if (bounds.IsEmpty) return;
        SurfaceHost.Width = Math.Max(1, bounds.Width);
        SurfaceHost.Height = Math.Max(1, bounds.Height);
        SurfaceHost.Margin = new Thickness(bounds.X, bounds.Y, 0, 0);

        var availableWidth = Math.Max(240, _captureViewport.Width - 16);
        // Keep every annotation command in the primary row. The row below is
        // reserved exclusively for properties of the selected tool.
        ToolbarHost.Width = double.NaN;
        ToolbarHost.Measure(new Size(availableWidth, double.PositiveInfinity));
        var toolbarWidth = Math.Min(availableWidth, Math.Ceiling(ToolbarHost.DesiredSize.Width));
        ToolbarHost.Width = toolbarWidth;
        ToolbarHost.HorizontalAlignment = HorizontalAlignment.Left;
        ToolbarHost.Margin = new Thickness(0);
        ToolbarHost.RenderTransform = Transform.Identity;
        ToolbarHost.Measure(new Size(toolbarWidth, double.PositiveInfinity));
        UpdateLayout();
        var toolbarHeight = Math.Max(1, ToolbarHost.ActualHeight);
        var anchor = _captureToolbarAnchor.IsEmpty ? bounds : _captureToolbarAnchor;
        var placement = OverlayLayoutService.PlaceBelowAndKeepVisible(anchor, new Size(toolbarWidth, toolbarHeight), _captureViewport, 5);
        var maximumLeft = Math.Max(5, _captureViewport.Width - toolbarWidth - 5);
        var stableLeft = Math.Clamp(_captureToolbarLeft ?? placement.X, 5, maximumLeft);
        var maximumTop = Math.Max(5, _captureViewport.Height - toolbarHeight - 5);
        var stableTop = Math.Clamp(_captureToolbarTop ?? placement.Y, 5, maximumTop);
        _captureToolbarLeft = stableLeft;
        _captureToolbarTop = stableTop;
        ToolbarHost.Margin = new Thickness(0);
        UpdateLayout();
        var naturalPosition = ToolbarHost.TranslatePoint(new Point(0, 0), this);
        ToolbarHost.RenderTransform = new TranslateTransform(
            stableLeft - naturalPosition.X,
            stableTop - naturalPosition.Y);
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_textEditor is not null)
        {
            if (_textEditorFrame is not null && e.OriginalSource is DependencyObject source &&
                (ReferenceEquals(source, _textEditorFrame) || _textEditorFrame.IsAncestorOf(source)))
                return;
            CommitTextEditor();
        }

        if (_externalBackgroundMode && _tool == "None")
        {
            _externalSurfaceDragging = true;
            _externalSurfaceLast = e.GetPosition(this);
            Surface.CaptureMouse();
            e.Handled = true;
            return;
        }

        var point = Clamp(e.GetPosition(AnnotationCanvas));
        _start = _last = point;

        // Paint-style transform handles stay usable while the drawing tool
        // remains active; a separate selection tool is not required.
        if (FindHandle(e.OriginalSource as DependencyObject) is { } activeHandle)
        {
            var target = _items.FirstOrDefault(item => item.Id == activeHandle.ItemId);
            if (target is null) return;
            PushUndo();
            _selectedId = _activeId = target.Id;
            _transformMode = activeHandle.Handle;
            _transformStart = point;
            _transformOriginal = target.Clone();
            _dragging = true;
            Surface.CaptureMouse();
            e.Handled = true;
            return;
        }

        // Drawing tools must still be able to paint inside earlier shapes
        // (e.g. blur/pencil inside a rectangle). Handles always win; filled
        // interiors of shapes do not block new strokes. Full quick-select of
        // existing objects stays available from the Select tool.
        if (_tool is "Select" or "None")
        {
            if (TryBeginExistingItemInteraction(e.OriginalSource as DependencyObject, point, e.ClickCount, force: true))
            {
                e.Handled = true;
                return;
            }
        }
        else if (!IsBrushTool(_tool) &&
                 TryBeginExistingItemInteraction(e.OriginalSource as DependencyObject, point, e.ClickCount, force: false))
        {
            e.Handled = true;
            return;
        }

        if (_tool == "Select")
        {
            _selectedId = FindItemId(e.OriginalSource as DependencyObject);
            if (e.ClickCount == 2 && _selectedId is Guid editId && _items.FirstOrDefault(item => item.Id == editId) is { Kind: AnnotationKind.Text or AnnotationKind.Callout } editableText)
            {
                PushUndo();
                _pendingTextKind = editableText.Kind;
                BeginTextEditor(new Point(editableText.X, editableText.Y), editableText);
                e.Handled = true;
                return;
            }
            if (_selectedId is not null)
            {
                PushUndo();
                _activeId = _selectedId;
                _dragging = true;
                Surface.CaptureMouse();
                if (_items.FirstOrDefault(item => item.Id == _selectedId) is { } selected) SyncStyleControls(selected);
            }
            RenderAnnotations();
            return;
        }

        if (_tool is "Eraser" or "Blur")
        {
            AddBrushStroke(point);
            _dragging = true;
            Surface.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool is "Text" or "Callout")
        {
            _pendingTextKind = _tool == "Callout" ? AnnotationKind.Callout : AnnotationKind.Text;
            StartTextEditorDrag(point);
            e.Handled = true;
            return;
        }

        if (_tool == "Number")
        {
            PushUndo();
            var marker = new AnnotationItem
            {
                Kind = AnnotationKind.Number,
                X = Math.Clamp(point.X - 23, 0, Math.Max(0, Surface.Width - 46)),
                Y = Math.Clamp(point.Y - 23, 0, Math.Max(0, Surface.Height - 46)),
                Width = 46,
                Height = 46,
                Text = (_nextNumber++).ToString(),
                Color = SelectedColor(),
                Thickness = ThicknessSlider.Value
            };
            ApplyCurrentStyle(marker);
            marker.FillOpacity = Math.Max(marker.FillOpacity, 1);
            _items.Add(marker);
            _selectedId = marker.Id;
            RenderAnnotations();
            e.Handled = true;
            return;
        }

        if (!Enum.TryParse<AnnotationKind>(_tool, out var kind)) return;
        PushUndo();
        var item = new AnnotationItem
        {
            Kind = kind,
            Color = SelectedColor(),
            Thickness = ThicknessSlider.Value,
            X = point.X,
            Y = point.Y,
            Points = kind is AnnotationKind.Line or AnnotationKind.Arrow or AnnotationKind.Pencil or AnnotationKind.Marker
                ? new List<Point> { point, point }
                : new List<Point>()
        };
        ApplyCurrentStyle(item);
        _items.Add(item);
        _activeId = item.Id;
        _selectedId = item.Id;
        _dragging = true;
        Surface.CaptureMouse();
        RenderAnnotations();
        e.Handled = true;
    }

    private bool TryBeginExistingItemInteraction(DependencyObject? source, Point point, int clickCount, bool force)
    {
        if (FindItemId(source) is not Guid itemId ||
            _items.FirstOrDefault(item => item.Id == itemId) is not { } selected)
            return false;

        // Filled shapes (rectangle/ellipse/magnify) occupy a large interior.
        // Only the border (or Select tool) should claim that hit; the interior
        // must remain free for pencil, blur, nested shapes, and text.
        if (!force && IsInteriorOnlyHit(selected, point))
            return false;

        _selectedId = itemId;
        SyncStyleControls(selected);
        if (clickCount >= 2 && selected.Kind is AnnotationKind.Text or AnnotationKind.Callout)
        {
            PushUndo();
            _pendingTextKind = selected.Kind;
            BeginTextEditor(new Point(selected.X, selected.Y), selected);
            return true;
        }

        PushUndo();
        _activeId = itemId;
        _transformMode = "Move";
        _transformStart = point;
        _transformOriginal = selected.Clone();
        _last = point;
        _dragging = true;
        Surface.CaptureMouse();
        RenderAnnotations();
        return true;
    }

    private static bool IsBrushTool(string tool) =>
        tool is "Pencil" or "Marker" or "Blur" or "Eraser";

    private static bool IsInteriorOnlyHit(AnnotationItem item, Point point)
    {
        if (item.Kind is not (AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Magnify or AnnotationKind.Mosaic))
            return false;

        var bounds = Bounds(item);
        var margin = Math.Max(10, item.Thickness + 6);
        if (bounds.Width <= margin * 2 || bounds.Height <= margin * 2)
            return false;

        var inner = new Rect(
            bounds.X + margin,
            bounds.Y + margin,
            bounds.Width - margin * 2,
            bounds.Height - margin * 2);

        if (!inner.Contains(point)) return false;

        if (item.Kind == AnnotationKind.Ellipse ||
            (item.Kind == AnnotationKind.Magnify && string.Equals(item.EffectShape, "Ellipse", StringComparison.OrdinalIgnoreCase)))
        {
            var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            var nx = (point.X - center.X) / Math.Max(1, bounds.Width / 2 - margin);
            var ny = (point.Y - center.Y) / Math.Max(1, bounds.Height / 2 - margin);
            return nx * nx + ny * ny <= 1;
        }

        return true;
    }

    private void Surface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var id = FindItemId(e.OriginalSource as DependencyObject);
        if (id is null) return;
        _selectedId = id;
        RenderAnnotations();
        var menu = new ContextMenu();
        AddAnnotationMenuItem(menu, "Bring to front", () => ReorderSelected(int.MaxValue));
        AddAnnotationMenuItem(menu, "Bring forward", () => ReorderSelected(1));
        AddAnnotationMenuItem(menu, "Send backward", () => ReorderSelected(-1));
        AddAnnotationMenuItem(menu, "Send to back", () => ReorderSelected(int.MinValue));
        menu.Items.Add(new Separator());
        AddAnnotationMenuItem(menu, "Duplicate", DuplicateSelected);
        AddAnnotationMenuItem(menu, "Delete", DeleteSelected);
        menu.PlacementTarget = Surface;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static void AddAnnotationMenuItem(ContextMenu menu, string label, Action action)
    {
        var item = new MenuItem { Header = L(label) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static string L(string value) => LocalizationService.Current(value);

    private void DeleteSelected()
    {
        if (_selectedId is not Guid id) return;
        PushUndo();
        _items.RemoveAll(item => item.Id == id);
        _selectedId = null;
        RenderAnnotations();
    }

    private void MoveSelected(double dx, double dy)
    {
        if (_selectedId is not Guid id || _items.FirstOrDefault(item => item.Id == id) is not { } item) return;
        PushUndo();
        item.X += dx;
        item.Y += dy;
        if (item.Points.Count > 0)
            item.Points = item.Points.Select(point => new Point(point.X + dx, point.Y + dy)).ToList();
        RenderAnnotations();
    }

    private void ReorderSelected(int delta)
    {
        if (_selectedId is not Guid id) return;
        var index = _items.FindIndex(item => item.Id == id);
        if (index < 0) return;
        var next = delta == int.MaxValue ? _items.Count - 1 : delta == int.MinValue ? 0 : Math.Clamp(index + delta, 0, _items.Count - 1);
        if (next == index) return;
        PushUndo();
        var item = _items[index];
        _items.RemoveAt(index);
        _items.Insert(next, item);
        RenderAnnotations();
    }

    private void DuplicateSelected()
    {
        if (_selectedId is not Guid id || _items.FirstOrDefault(item => item.Id == id) is not { } source) return;
        PushUndo();
        var copy = new AnnotationItem
        {
            Kind = source.Kind, Points = source.Points.Select(point => new Point(point.X + 8, point.Y + 8)).ToList(),
            X = source.X + 8, Y = source.Y + 8, Width = source.Width, Height = source.Height,
            Color = source.Color, Thickness = source.Thickness, Text = source.Text, Opacity = source.Opacity,
            FillOpacity = source.FillOpacity, Dashed = source.Dashed, ArrowHeads = source.ArrowHeads,
            EffectShape = source.EffectShape, Rotation = source.Rotation, FontFamily = source.FontFamily,
            Bold = source.Bold, Italic = source.Italic, Underline = source.Underline,
            Strikethrough = source.Strikethrough, TextAlignment = source.TextAlignment, Outline = source.Outline
        };
        _items.Add(copy);
        _selectedId = copy.Id;
        RenderAnnotations();
    }

    private AnnotationItem AddBrushStroke(Point point)
    {
        PushUndo();
        var brushStroke = new AnnotationItem
        {
            Kind = _tool == "Eraser" ? AnnotationKind.Eraser : AnnotationKind.Blur,
            Thickness = ThicknessSlider.Value,
            Points = [point]
        };
        ApplyCurrentStyle(brushStroke);
        _items.Add(brushStroke);
        _activeId = brushStroke.Id;
        _selectedId = null;
        RenderAnnotations();
        return brushStroke;
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_externalSurfaceDragging)
        {
            var current = e.GetPosition(this);
            var delta = current - _externalSurfaceLast;
            _externalSurfaceLast = current;
            if (delta.LengthSquared >= 0.0001) ExternalSurfaceMoved?.Invoke(delta);
            e.Handled = true;
            return;
        }

        var pointer = Clamp(e.GetPosition(AnnotationCanvas));
        if (_textBoxDragging)
        {
            UpdateTextEditorDrag(pointer);
            e.Handled = true;
            return;
        }
        var pointerSource = e.OriginalSource as DependencyObject;
        PositionCustomToolCursor(pointer,
            FindHandle(pointerSource) is null && FindItemId(pointerSource) is null);
        if (!_dragging || _activeId is null) return;
        var point = pointer;
        var item = _items.FirstOrDefault(candidate => candidate.Id == _activeId);
        if (item is null) return;

        if (_transformMode == "Move")
        {
            MoveItem(item, point.X - _last.X, point.Y - _last.Y);
            _last = point;
        }
        else if (!string.IsNullOrWhiteSpace(_transformMode) && _transformOriginal is not null)
        {
            TransformItem(item, point);
        }
        else if (_tool == "Select")
        {
            MoveItem(item, point.X - _last.X, point.Y - _last.Y);
        }
        else if (item.Kind is AnnotationKind.Eraser || item.Kind == AnnotationKind.Blur && item.Points.Count > 0)
        {
            if (Distance(_last, point) < Math.Max(1.25, item.Thickness * 0.08)) return;
            item.Points.Add(point);
            _last = point;
            UpdateLiveBrushVisual(item);
            return;
        }
        else if (item.Kind is AnnotationKind.Pencil or AnnotationKind.Marker)
        {
            if (Distance(_last, point) < 1.5) return;
            item.Points.Add(point);
            _last = point;
            RequestRender();
            return;
        }
        else if (item.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            if (item.Points.Count < 2) item.Points.Add(point); else item.Points[1] = point;
        }
        else
        {
            item.X = Math.Min(_start.X, point.X);
            item.Y = Math.Min(_start.Y, point.Y);
            item.Width = Math.Abs(point.X - _start.X);
            item.Height = Math.Abs(point.Y - _start.Y);
        }

        _last = point;
        RequestRender();
    }

    private void UpdateLiveBrushVisual(AnnotationItem item)
    {
        if (item.Kind is AnnotationKind.Eraser or AnnotationKind.Blur &&
            _renderedElements.TryGetValue(item.Id, out var element) && element is Shapes.Path path)
            path.Data = BrushStrokeGeometry(item);
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_externalSurfaceDragging)
        {
            _externalSurfaceDragging = false;
            Surface.ReleaseMouseCapture();
            ExternalSurfaceMoveCompleted?.Invoke();
            e.Handled = true;
            return;
        }

        if (_textBoxDragging)
        {
            FinishTextEditorDrag();
            e.Handled = true;
            return;
        }
        if (!_dragging) return;
        var completedId = _activeId;
        _dragging = false;
        _activeId = null;
        _transformMode = string.Empty;
        _transformOriginal = null;
        Surface.ReleaseMouseCapture();
        _items.RemoveAll(item => !IsStrokeItem(item) &&
            item.Kind is not (AnnotationKind.Text or AnnotationKind.Number or AnnotationKind.Callout) &&
            (item.Width < 2 || item.Height < 2));
        _selectedId = completedId is Guid id && _items.FirstOrDefault(item => item.Id == id) is { Kind: not (AnnotationKind.Eraser or AnnotationKind.Blur) }
            ? id
            : null;
        RenderAnnotations();
    }

    private static bool IsStrokeItem(AnnotationItem item) =>
        item.Kind is AnnotationKind.Pencil or AnnotationKind.Marker or AnnotationKind.Line or AnnotationKind.Arrow or AnnotationKind.Eraser ||
        item.Kind == AnnotationKind.Blur && item.Points.Count > 0;

    private Guid? FindItemId(DependencyObject? source)
    {
        var current = source;
        while (current is not null && current != AnnotationCanvas)
        {
            if (current is FrameworkElement { Tag: Guid id }) return id;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private AnnotationHandleTag? FindHandle(DependencyObject? source)
    {
        var current = source;
        while (current is not null && current != AnnotationCanvas)
        {
            if (current is FrameworkElement { Tag: AnnotationHandleTag handle }) return handle;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static Rect Bounds(AnnotationItem item)
    {
        if (item.Points.Count > 0)
        {
            var minX = item.Points.Min(point => point.X);
            var minY = item.Points.Min(point => point.Y);
            var maxX = item.Points.Max(point => point.X);
            var maxY = item.Points.Max(point => point.Y);
            return new Rect(minX, minY, Math.Max(2, maxX - minX), Math.Max(2, maxY - minY));
        }
        return new Rect(item.X, item.Y, Math.Max(2, item.Width), Math.Max(2, item.Height));
    }

    private void MoveItem(AnnotationItem item, double dx, double dy)
    {
        if (item.Points.Count > 0)
            item.Points = item.Points.Select(point => Clamp(new Point(point.X + dx, point.Y + dy))).ToList();
        else
        {
            item.X = Math.Clamp(item.X + dx, 0, Math.Max(0, Surface.Width - item.Width));
            item.Y = Math.Clamp(item.Y + dy, 0, Math.Max(0, Surface.Height - item.Height));
        }
    }

    private void TransformItem(AnnotationItem item, Point point)
    {
        var original = _transformOriginal;
        if (original is null) return;
        if (item.Kind == AnnotationKind.Arrow && original.Points.Count >= 2)
        {
            TransformArrow(item, original, point);
            return;
        }

        var originalBounds = Bounds(original);
        if (_transformMode == "Rotate")
        {
            var center = new Point(originalBounds.X + originalBounds.Width / 2, originalBounds.Y + originalBounds.Height / 2);
            var startAngle = Math.Atan2(_transformStart.Y - center.Y, _transformStart.X - center.X);
            var currentAngle = Math.Atan2(point.Y - center.Y, point.X - center.X);
            item.Rotation = original.Rotation + (currentAngle - startAngle) * 180 / Math.PI;
            return;
        }

        var dx = point.X - _transformStart.X;
        var dy = point.Y - _transformStart.Y;
        var left = originalBounds.Left;
        var right = originalBounds.Right;
        var top = originalBounds.Top;
        var bottom = originalBounds.Bottom;
        if (_transformMode.Contains('W')) left = Math.Min(right - 12, Math.Max(0, left + dx));
        if (_transformMode.Contains('E')) right = Math.Max(left + 12, Math.Min(Surface.Width, right + dx));
        if (_transformMode.Contains('N')) top = Math.Min(bottom - 12, Math.Max(0, top + dy));
        if (_transformMode.Contains('S')) bottom = Math.Max(top + 12, Math.Min(Surface.Height, bottom + dy));
        var resized = new Rect(left, top, right - left, bottom - top);

        if (original.Points.Count > 0)
        {
            var scaleX = resized.Width / Math.Max(1, originalBounds.Width);
            var scaleY = resized.Height / Math.Max(1, originalBounds.Height);
            item.Points = original.Points.Select(source => new Point(
                resized.X + (source.X - originalBounds.X) * scaleX,
                resized.Y + (source.Y - originalBounds.Y) * scaleY)).ToList();
        }
        else
        {
            item.X = resized.X; item.Y = resized.Y; item.Width = resized.Width; item.Height = resized.Height;
        }
    }

    private void TransformArrow(AnnotationItem item, AnnotationItem original, Point point)
    {
        if (_transformMode == "ArrowStart")
        {
            item.Points[0] = point;
            return;
        }

        if (_transformMode == "ArrowEnd")
        {
            item.Points[^1] = point;
            return;
        }

        if (_transformMode != "ArrowMove") return;
        var dx = point.X - _transformStart.X;
        var dy = point.Y - _transformStart.Y;
        var minX = original.Points.Min(candidate => candidate.X);
        var maxX = original.Points.Max(candidate => candidate.X);
        var minY = original.Points.Min(candidate => candidate.Y);
        var maxY = original.Points.Max(candidate => candidate.Y);
        dx = Math.Clamp(dx, -minX, Surface.Width - maxX);
        dy = Math.Clamp(dy, -minY, Surface.Height - maxY);
        item.Points = original.Points
            .Select(candidate => new Point(candidate.X + dx, candidate.Y + dy))
            .ToList();
    }

    private Point Clamp(Point point) => new(
        Math.Clamp(point.X, 0, Surface.Width),
        Math.Clamp(point.Y, 0, Surface.Height));

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private string SelectedColor() => _selectedColor;

    private static List<AnnotationItem> Clone(IEnumerable<AnnotationItem> items) => items.Select(item => item.Clone()).ToList();

    private void PushUndo()
    {
        _undo.Push(Clone(_items));
        _redo.Clear();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        CommitTextEditor();
        if (_undo.Count == 0) return;
        _redo.Push(Clone(_items));
        _items = _undo.Pop();
        _selectedId = null;
        RenderAnnotations();
    }

    private void Redo()
    {
        CommitTextEditor();
        if (_redo.Count == 0) return;
        _undo.Push(Clone(_items));
        _items = _redo.Pop();
        _selectedId = null;
        RenderAnnotations();
    }

    public BitmapSource Flatten()
    {
        CommitTextEditor();
        var selected = _selectedId;
        var cursorVisibility = ToolCursorLayer.Visibility;
        var backgroundVisibility = BackgroundImage.Visibility;
        try
        {
            _selectedId = null;
            ToolCursorLayer.Visibility = Visibility.Collapsed;
            if (_externalBackgroundMode) BackgroundImage.Visibility = Visibility.Visible;
            RenderAnnotations();
            Surface.Measure(new Size(_source.PixelWidth, _source.PixelHeight));
            Surface.Arrange(new Rect(0, 0, _source.PixelWidth, _source.PixelHeight));
            Surface.UpdateLayout();
            var result = new RenderTargetBitmap(_source.PixelWidth, _source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            result.Render(Surface);
            result.Freeze();
            return result;
        }
        finally
        {
            BackgroundImage.Visibility = backgroundVisibility;
            _selectedId = selected;
            ToolCursorLayer.Visibility = cursorVisibility;
            RenderAnnotations();
        }
    }

    internal AnnotationAppliedEventArgs SnapshotDocument()
    {
        var image = Flatten();
        return new AnnotationAppliedEventArgs(_source, image, Clone(_items));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var image = Flatten();
        Clipboard.SetImage(image);
        DocumentStored?.Invoke(new AnnotationAppliedEventArgs(_source, image, Clone(_items)));
    }

    private SaveFileDialog CreateImageSaveDialog()
    {
        var folder = Directory.Exists(_settings.QuickSaveFolder) ? _settings.QuickSaveFolder : Path.GetTempPath();
        var suggested = SettingsService.CreateOutputPath(folder, _settings.OutputFileName);
        return new SaveFileDialog
        {
            Filter = CaptureService.ImageSaveFilter,
            DefaultExt = CaptureService.ExtensionForFormat(_settings.OutputFormat),
            FilterIndex = CaptureService.FilterIndexForFormat(_settings.OutputFormat),
            AddExtension = true,
            InitialDirectory = Path.GetDirectoryName(suggested) ?? folder,
            FileName = Path.GetFileName(suggested)
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateImageSaveDialog();
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            var image = Flatten();
            CaptureService.SaveImage(image, dialog.FileName, _settings.ImageQuality, _settings);
            DocumentStored?.Invoke(new AnnotationAppliedEventArgs(_source, image, Clone(_items)));
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var image = Flatten();
        Clipboard.SetImage(image);
        Applied?.Invoke(new AnnotationAppliedEventArgs(_source, image, Clone(_items)));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_textEditor is not null && e.OriginalSource is TextBox) return;

        if (e.Key == Key.F6)
        {
            AccessibilityService.ApplyToolbar(ToolbarHost);
            if (AccessibilityService.FocusFirstToolbarButton(ToolbarHost))
            {
                e.Handled = true;
                return;
            }
        }
        if (Keyboard.FocusedElement is Button &&
            (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == ModifierKeys.None)
        {
            var direction = e.Key is Key.Left or Key.Up ? -1 : 1;
            if (AccessibilityService.MoveToolbarFocus(ToolbarHost, direction))
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Undo(); e.Handled = true; }
        else if (e.Key == Key.Y && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Redo(); e.Handled = true; }
        else if (e.Key == Key.D && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { DuplicateSelected(); e.Handled = true; }
        else if (e.Key == Key.OemCloseBrackets && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { ReorderSelected(1); e.Handled = true; }
        else if (e.Key == Key.OemOpenBrackets && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { ReorderSelected(-1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && TrySelectToolFromKey(e.Key))
            e.Handled = true;
        else if (_selectedId is not null && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            MoveSelected(e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0,
                e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _selectedId is Guid id)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private bool TrySelectToolFromKey(Key key)
    {
        var tool = key switch
        {
            Key.R => "Rectangle",
            Key.O => "Ellipse",
            Key.A => "Arrow",
            Key.L => "Line",
            Key.P => "Pencil",
            Key.M => "Marker",
            Key.B => "Blur",
            Key.T => "Text",
            Key.N => "Number",
            Key.W => "Callout",
            Key.E => "Eraser",
            Key.G => "Magnify",
            _ => null
        };
        if (tool is null) return false;
        var button = FindToolButton(tool);
        if (button is null || button.Visibility != Visibility.Visible || !button.IsEnabled) return false;
        ToggleConfiguredTool(tool);
        return true;
    }
}
