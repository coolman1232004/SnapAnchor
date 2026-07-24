using Microsoft.Win32;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using SnapAnchor.Services;
using SnapAnchor.Models;
using SnapAnchor.Controls;
using Forms = System.Windows.Forms;

namespace SnapAnchor.Windows;

public enum CaptureCompletionMode
{
    Interactive,
    CopyOnSelection,
    RecordOnSelection,
    FullScreenDraw,
    ColorPicker
}

public partial class CaptureOverlayWindow : Window
{
    private enum RecordingReviewChoice { KeepFull, SaveTrimmed, Discard }

    internal bool IsDrawingInline => _completionMode == CaptureCompletionMode.FullScreenDraw && _annotationMode;
    internal Rect CurrentSelection => _selection;

    private readonly BitmapSource _screen;
    private readonly AppSettings _settings;
    private readonly CaptureCompletionMode _completionMode;
    private readonly CaptureOptions _options;
    private System.Windows.Point _start;
    private Rect _selection;
    private Rect _detectedSelection;
    private bool _dragging;
    private bool _resizing;
    private string _resizeHandle = string.Empty;
    private Point _resizeStart;
    private Rect _resizeStartRect;
    private IReadOnlyList<DetectedScreenRegion> _detectionCandidates = [];
    private int _detectionIndex;
    private bool _detectElements;
    private CancellationTokenSource? _ocrCancellation;
    private string? _ocrHistoryRecordId;
    private bool _ocrLayoutMode;
    private bool _ocrAreaSelectionMode;
    private bool _ocrAreaDragging;
    private Point _ocrAreaStart;
    private Rect _ocrAreaSelection;
    private bool _initialized;
    private bool _longCaptureRunning;
    private bool _recordingRunning;
    private bool _recordingPaused;
    private bool _recordingStopRequested;
    private CancellationTokenSource? _recordingCancellation;
    private AdvancedRecordingSession? _advancedRecording;
    private bool _recordingReviewRunning;
    private bool _recordingPreviewPlaying;
    private bool _recordingReviewDragging;
    private bool _recordingReviewPositioned;
    private Point _recordingReviewDragStart;
    private Point _recordingReviewStart;
    private TaskCompletionSource<RecordingReviewChoice>? _recordingReviewChoice;
    private IntPtr _recordingTargetWindow;
    private IntPtr _overlayHandle;
    private readonly Stopwatch _detectionClock = Stopwatch.StartNew();
    private Point _lastDetectionOverlayPoint = new(double.NaN, double.NaN);
    private Point _lastDetectionScreenPoint = new(double.NaN, double.NaN);
    private bool _annotationMode;
    private string? _annotationHistoryRecordId;
    private IReadOnlyList<AnnotationItem>? _annotationResizeItems;
    private Int32Rect _annotationResizePixelRegion;
    private readonly IReadOnlyList<CaptureRegion> _regionHistory;
    private int _regionHistoryIndex = -1;
    private readonly ColorMagnifierControl _colorMagnifier = new();
    private bool _colorMagnifierUseHex;
    private bool _colorPickerMode;

    public CaptureOverlayWindow(BitmapSource screen, CaptureCompletionMode completionMode = CaptureCompletionMode.Interactive, CaptureOptions? options = null)
    {
        InitializeComponent();
        _screen = screen;
        _regionHistory = HistoryService.RecentRegions();
        _settings = SettingsService.Load();
        ToolbarThemeService.ApplyTo(Resources, _settings.ToolbarSizeMode);
        ApplyCaptureAnnotationToolbarConfiguration(_settings.AnnotationToolbarOrder, _settings.AnnotationToolbarEnabled);
        ApplyCaptureToolbarConfiguration(_settings.CaptureToolbarOrder, _settings.CaptureToolbarEnabled);
        _completionMode = completionMode;
        _options = options ?? new CaptureOptions();
        _colorPickerMode = completionMode == CaptureCompletionMode.ColorPicker;
        // Window-first by default for smooth multi-monitor hover; optional UI refine.
        _detectElements = _settings.DetectUiElements;
        _colorMagnifierUseHex = _settings.ColorMagnifierUseHex;
        _colorMagnifier.SetUseHex(_colorMagnifierUseHex);
        Panel.SetZIndex(_colorMagnifier, 3700);
        OverlayCanvas.Children.Add(_colorMagnifier);
        ScreenImage.Source = screen;
        CaptureInlineEditor.Applied += ApplyCaptureAnnotation;
        CaptureInlineEditor.DocumentStored += StoreCaptureAnnotation;
        CaptureInlineEditor.Cancelled += (_, _) => Close();
        SelectionRect.StrokeThickness = _settings.CaptureBorderWidth;
        var selectionColor = _settings.CaptureBorderColor.Equals("#63E6BE", StringComparison.OrdinalIgnoreCase) ? "#3388FF" : _settings.CaptureBorderColor;
        TrySetBrush(SelectionRect, System.Windows.Shapes.Shape.StrokeProperty, selectionColor);
        TrySetBrush(MaskLayer, System.Windows.Shapes.Shape.FillProperty, _settings.CaptureMaskColor);
        foreach (var mask in new[] { MaskTop, MaskLeft, MaskRight, MaskBottom })
            TrySetBrush(mask, System.Windows.Shapes.Shape.FillProperty, _settings.CaptureMaskColor);
        CrossHorizontal.Visibility = _settings.ShowCrossLines ? Visibility.Visible : Visibility.Collapsed;
        CrossVertical.Visibility = _settings.ShowCrossLines ? Visibility.Visible : Visibility.Collapsed;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        SourceInitialized += (_, _) => FitToVirtualScreenPixels();
        CaptureOcrLanguageBox.SelectedValue = _settings.OcrLanguage;
        if (CaptureOcrLanguageBox.SelectedIndex < 0) CaptureOcrLanguageBox.SelectedIndex = 3;
        OcrAutoCopyBox.IsChecked = _settings.OcrAutoCopy;
        ShortcutHints.Visibility = CaptureHintsVisibility;
        var detectionOn = _settings.ShowElementDetection != false;
        DetectionWheelHint.Visibility = detectionOn && _detectElements ? Visibility.Visible : Visibility.Collapsed;
        DetectionTabHint.Visibility = detectionOn ? Visibility.Visible : Visibility.Collapsed;
        DetectionModeText.Visibility = detectionOn ? Visibility.Visible : Visibility.Collapsed;
        if (detectionOn)
            DetectionModeText.Text = $"{L("Mode")}: {L(_detectElements ? "UI element" : "Window")}";
        Loaded += (_, _) =>
        {
            _overlayHandle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowDisplayAffinity(_overlayHandle, NativeMethods.WdaExcludeFromCapture);
            _initialized = true;
            // Window rectangles only (milliseconds). UI Automation elements are
            // loaded lazily per hovered window — never a full-desktop tree walk.
            if (_settings.ShowElementDetection != false && !_colorPickerMode)
            {
                try { ElementDetectionService.RebuildWindowCache(_overlayHandle); }
                catch { /* detection remains best-effort */ }
            }
            PositionFloatingPanels();
            Focus();
            if (_completionMode == CaptureCompletionMode.FullScreenDraw)
                Dispatcher.BeginInvoke(InitializeFullScreenDrawing);
            if (_colorPickerMode)
                Dispatcher.BeginInvoke(() =>
                {
                    ShortcutHints.Visibility = Visibility.Collapsed;
                    ActionBar.Visibility = Visibility.Collapsed;
                    ClearDetectionHighlight();
                    UpdateColorMagnifier(Mouse.GetPosition(OverlayCanvas));
                });
            else if (_settings.ShowElementDetection != false)
                Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    () => UpdateDetectedRegion(Mouse.GetPosition(OverlayCanvas)));
        };
        Closed += (_, _) =>
        {
            _ocrCancellation?.Cancel();
            _recordingCancellation?.Cancel();
        };
    }

    internal void ApplyCaptureToolbarConfiguration(IEnumerable<string>? order, IEnumerable<string>? enabled)
    {
        var normalizedOrder = CaptureToolbarCatalog.NormalizeOrder(order);
        var normalizedEnabled = CaptureToolbarCatalog.NormalizeEnabled(enabled).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var buttons = CaptureActionPanel.Children.OfType<Button>()
            .Where(button => button.Tag is string)
            .ToDictionary(button => (string)button.Tag, StringComparer.OrdinalIgnoreCase);
        foreach (var button in buttons.Values.ToList()) CaptureActionPanel.Children.Remove(button);
        foreach (var key in normalizedOrder)
        {
            if (!buttons.TryGetValue(key, out var button)) continue;
            button.Visibility = normalizedEnabled.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
            CaptureActionPanel.Children.Add(button);
        }
    }

    internal void ApplyCaptureAnnotationToolbarConfiguration(IEnumerable<string>? order, IEnumerable<string>? enabled)
    {
        var normalizedOrder = AnnotationToolbarCatalog.NormalizeOrder(order);
        var normalizedEnabled = AnnotationToolbarCatalog.NormalizeEnabled(enabled).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var buttons = CaptureAnnotationToolPanel.Children.OfType<Button>()
            .Where(button => button.Tag is string)
            .ToDictionary(button => (string)button.Tag, StringComparer.OrdinalIgnoreCase);
        foreach (var button in buttons.Values.ToList()) CaptureAnnotationToolPanel.Children.Remove(button);
        foreach (var key in normalizedOrder)
        {
            if (!buttons.TryGetValue(key, out var button)) continue;
            button.Visibility = normalizedEnabled.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
            CaptureAnnotationToolPanel.Children.Add(button);
        }
    }

    private Visibility CaptureHintsVisibility => _settings.ShowCaptureHints ? Visibility.Visible : Visibility.Collapsed;

    private void InitializeFullScreenDrawing()
    {
        if (!IsVisible || ActualWidth < 1 || ActualHeight < 1) return;
        _selection = new Rect(0, 0, ActualWidth, ActualHeight);
        RenderSelection();
        BeginInlineAnnotation();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_annotationMode) return;
        if (_ocrAreaSelectionMode)
        {
            BeginOcrAreaDrag(e.GetPosition(OverlayCanvas));
            e.Handled = true;
            return;
        }
        if (_colorPickerMode)
        {
            UpdateColorMagnifier(e.GetPosition(OverlayCanvas));
            _colorMagnifier.TryCopyColor();
            e.Handled = true;
            return;
        }
        if (_recordingRunning || ActionBar.IsMouseOver || OcrPanel.IsMouseOver || RecordingBar.IsMouseOver || RecordingReviewPanel.IsMouseOver || e.OriginalSource is System.Windows.Controls.Button) return;
        _ocrCancellation?.Cancel();
        OcrPanel.Visibility = Visibility.Collapsed;
        _dragging = true;
        _start = e.GetPosition(OverlayCanvas);
        _selection = new Rect(_start, _start);
        SelectionRect.Visibility = Visibility.Visible;
        SizeBadge.Visibility = _settings.ShowCaptureSize ? Visibility.Visible : Visibility.Collapsed;
        ActionBar.Visibility = Visibility.Collapsed;
        SelectionHandles.Visibility = Visibility.Collapsed;
        ClearDetectionHighlight();
        CaptureMouse();
        UpdateSelection(e.GetPosition(OverlayCanvas));
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var point = e.GetPosition(OverlayCanvas);
        if (_annotationMode)
        {
            if (_resizing) UpdateResize(point);
            return;
        }
        if (_ocrAreaSelectionMode)
        {
            if (_ocrAreaDragging) UpdateOcrAreaDrag(point);
            return;
        }
        if (_settings.ShowCrossLines)
        {
            CrossHorizontal.X1 = 0;
            CrossHorizontal.X2 = ActualWidth;
            CrossHorizontal.Y1 = CrossHorizontal.Y2 = point.Y;
            CrossVertical.Y1 = 0;
            CrossVertical.Y2 = ActualHeight;
            CrossVertical.X1 = CrossVertical.X2 = point.X;
        }
        UpdateColorMagnifier(point);
        if (_colorPickerMode)
            return;
        if (_resizing)
            UpdateResize(point);
        else if (_dragging)
            UpdateSelection(point);
        else if (_settings.ShowElementDetection != false && _selection.Width < 3 && _selection.Height < 3)
        {
            UpdateDetectedRegion(point);
        }
        else if (_selection.Width >= 3 || _settings.ShowElementDetection == false)
        {
            ClearDetectionHighlight();
        }
    }

    private bool ColorMagnifierEnabled =>
        _settings.EnableColorMagnifier || _colorPickerMode;

    private long _lastMagnifierTick;

    private void UpdateColorMagnifier(Point overlayPoint)
    {
        if (!ColorMagnifierEnabled || _annotationMode || _recordingRunning || _ocrAreaSelectionMode)
        {
            _colorMagnifier.Visibility = Visibility.Collapsed;
            return;
        }

        // Pixel sampling is expensive; keep it light so detection stays smooth.
        var now = Environment.TickCount64;
        var minInterval = _settings.ShowElementDetection != false ? 48 : 28;
        if (now - _lastMagnifierTick < minInterval && _colorMagnifier.Visibility == Visibility.Visible)
        {
            PlaceColorMagnifier(overlayPoint);
            return;
        }
        _lastMagnifierTick = now;

        _colorMagnifier.UpdateFromCapture(_screen, overlayPoint, ActualWidth, ActualHeight);
        _colorMagnifier.Visibility = Visibility.Visible;
        PlaceColorMagnifier(overlayPoint);
    }

    private void PlaceColorMagnifier(Point overlayPoint)
    {
        _colorMagnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _colorMagnifier.DesiredSize;
        var left = overlayPoint.X + 22;
        var top = overlayPoint.Y + 22;
        if (left + size.Width > ActualWidth - 8)
            left = overlayPoint.X - size.Width - 16;
        if (top + size.Height > ActualHeight - 8)
            top = overlayPoint.Y - size.Height - 16;
        left = Math.Clamp(left, 8, Math.Max(8, ActualWidth - size.Width - 8));
        top = Math.Clamp(top, 8, Math.Max(8, ActualHeight - size.Height - 8));
        Canvas.SetLeft(_colorMagnifier, left);
        Canvas.SetTop(_colorMagnifier, top);
    }

    private void PersistColorMagnifierFormat()
    {
        if (_settings.ColorMagnifierUseHex == _colorMagnifierUseHex) return;
        _settings.ColorMagnifierUseHex = _colorMagnifierUseHex;
        try { SettingsService.Save(_settings); } catch { /* non-fatal */ }
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_annotationMode)
        {
            if (_resizing)
            {
                _resizing = false;
                ReleaseMouseCapture();
                RefreshInlineAnnotationAfterResize();
                SetResizeToolbarsVisible(true);
                PositionActionBar();
                e.Handled = true;
            }
            return;
        }
        if (_ocrAreaSelectionMode)
        {
            FinishOcrAreaDrag(e.GetPosition(OverlayCanvas));
            e.Handled = true;
            return;
        }
        if (_resizing)
        {
            _resizing = false;
            ReleaseMouseCapture();
            RenderSelection();
            SetResizeToolbarsVisible(true);
            PositionActionBar();
            e.Handled = true;
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        UpdateSelection(e.GetPosition(OverlayCanvas));

        if (_selection.Width < 3 || _selection.Height < 3)
        {
            if (_detectedSelection.Width >= 3 && _detectedSelection.Height >= 3)
            {
                _selection = _detectedSelection;
                RenderSelection();
            }
            else
            {
                ResetSelection();
                return;
            }
        }

        if (_completionMode == CaptureCompletionMode.CopyOnSelection)
        {
            CompleteCopy();
            return;
        }
        if (_completionMode == CaptureCompletionMode.RecordOnSelection)
        {
            _ = RunRecordingAsync();
            return;
        }
        ClearDetectionHighlight();
        ActionBar.Visibility = Visibility.Visible;
        PositionActionBar();
    }

    private void UpdateSelection(System.Windows.Point current)
    {
        var width = Math.Abs(current.X - _start.X);
        var height = Math.Abs(current.Y - _start.Y);
        var fixedWidth = _options.FixedWidth > 0 ? _options.FixedWidth * ActualWidth / _screen.PixelWidth : 0;
        var fixedHeight = _options.FixedHeight > 0 ? _options.FixedHeight * ActualHeight / _screen.PixelHeight : 0;
        if (fixedWidth > 0) width = fixedWidth;
        if (fixedHeight > 0) height = fixedHeight;

        if (_options.AspectRatio > 0)
        {
            var displayRatio = _options.AspectRatio * ActualWidth / _screen.PixelWidth / (ActualHeight / _screen.PixelHeight);
            if (fixedWidth > 0 && fixedHeight <= 0)
                height = width / displayRatio;
            else if (fixedHeight > 0 && fixedWidth <= 0)
                width = height * displayRatio;
            else if (fixedWidth <= 0 || fixedHeight <= 0)
            {
                if (height <= 0 || width / height >= displayRatio) height = width / displayRatio;
                else width = height * displayRatio;
            }
        }

        var x = current.X >= _start.X ? _start.X : _start.X - width;
        var y = current.Y >= _start.Y ? _start.Y : _start.Y - height;
        x = Math.Clamp(x, 0, ActualWidth);
        y = Math.Clamp(y, 0, ActualHeight);
        width = Math.Min(width, ActualWidth - x);
        height = Math.Min(height, ActualHeight - y);
        _selection = new Rect(x, y, Math.Max(0, width), Math.Max(0, height));

        RenderSelection();
    }

    private void RenderSelection()
    {
        if (_selection.Width >= 3 && _selection.Height >= 3)
        {
            DetectionRect.Visibility = Visibility.Collapsed;
            _detectedSelection = Rect.Empty;
            _detectionCandidates = [];
        }
        Canvas.SetLeft(SelectionRect, _selection.X);
        Canvas.SetTop(SelectionRect, _selection.Y);
        SelectionRect.Width = _selection.Width;
        SelectionRect.Height = _selection.Height;
        SelectionRect.Visibility = Visibility.Visible;
        SelectionHandles.Visibility = Visibility.Visible;
        PositionSelectionHandles();
        RenderSelectionMask();

        var pixelRegion = SelectedPixelRegion();
        var virtualBounds = DisplayTopologyService.VirtualBoundsPixels();
        SizeText.Text = $"{virtualBounds.Left + pixelRegion.X}, {virtualBounds.Top + pixelRegion.Y}   {pixelRegion.Width} × {pixelRegion.Height} px";
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(SizeBadge, Math.Min(ActualWidth - 100, _selection.X));
        Canvas.SetTop(SizeBadge, Math.Max(4, _selection.Y - 36));
    }

    private void UpdateDetectedRegion(Point overlayPoint)
    {
        if (_settings.ShowElementDetection == false || _colorPickerMode || _annotationMode || _recordingRunning)
        {
            ClearDetectionHighlight();
            return;
        }

        if (_overlayHandle == IntPtr.Zero)
        {
            _overlayHandle = new WindowInteropHelper(this).Handle;
            if (_overlayHandle == IntPtr.Zero) return;
        }

        // Use the physical cursor position for hit-testing. Window/UI Automation
        // geometry is also physical-pixel based (GetWindowRect / BoundingRectangle).
        var screenPoint = NativeMethods.GetCursorPos(out var cursor)
            ? new Point(cursor.X, cursor.Y)
            : OverlayToScreen(overlayPoint);

        // Window-only mode is pure geometry — keep it very smooth.
        var minMove = _detectElements ? 2.0 : 3.0;
        var movedLittle = !double.IsNaN(_lastDetectionScreenPoint.X) &&
            Math.Abs(screenPoint.X - _lastDetectionScreenPoint.X) < minMove &&
            Math.Abs(screenPoint.Y - _lastDetectionScreenPoint.Y) < minMove;
        if (movedLittle && _detectionCandidates.Count > 0 && _detectionClock.ElapsedMilliseconds < (_detectElements ? 20 : 12))
            return;

        // Stay lit without re-layout while the pointer is still inside the hole.
        if (_detectionCandidates.Count > 0 &&
            !_detectedSelection.IsEmpty &&
            _detectedSelection.Contains(overlayPoint) &&
            _detectionClock.ElapsedMilliseconds < (_detectElements ? 45 : 100))
            return;

        _lastDetectionOverlayPoint = overlayPoint;
        _lastDetectionScreenPoint = screenPoint;
        _detectionClock.Restart();

        _detectionCandidates = ElementDetectionService.DetectHierarchy(screenPoint, _overlayHandle, _detectElements);
        if (_detectionCandidates.Count == 0)
        {
            ClearDetectionHighlight();
            return;
        }

        _detectionIndex = _detectElements ? _detectionCandidates.Count - 1 : 0;
        RenderDetectedCandidate();
    }

    private void RenderDetectedCandidate()
    {
        if (_detectionCandidates.Count == 0) return;
        _detectionIndex = Math.Clamp(_detectionIndex, 0, _detectionCandidates.Count - 1);
        var detected = _detectionCandidates[_detectionIndex];

        var topLeft = ScreenToOverlay(new Point(detected.Bounds.Left, detected.Bounds.Top));
        var bottomRight = ScreenToOverlay(new Point(detected.Bounds.Right, detected.Bounds.Bottom));
        var next = Rect.Intersect(
            new Rect(0, 0, ActualWidth, ActualHeight),
            new Rect(topLeft, bottomRight));
        if (next.IsEmpty || next.Width < 2 || next.Height < 2)
        {
            ClearDetectionHighlight();
            return;
        }

        // Skip mask rebuild when the lit region did not meaningfully change.
        var sameRegion = !_detectedSelection.IsEmpty &&
            Math.Abs(_detectedSelection.X - next.X) < 1 &&
            Math.Abs(_detectedSelection.Y - next.Y) < 1 &&
            Math.Abs(_detectedSelection.Width - next.Width) < 1 &&
            Math.Abs(_detectedSelection.Height - next.Height) < 1;
        _detectedSelection = next;
        if (!sameRegion)
        {
            RenderSpotlightMask(_detectedSelection);
            Canvas.SetLeft(DetectionRect, _detectedSelection.X);
            Canvas.SetTop(DetectionRect, _detectedSelection.Y);
            DetectionRect.Width = _detectedSelection.Width;
            DetectionRect.Height = _detectedSelection.Height;
            DetectionRect.Visibility = Visibility.Visible;
        }
        else if (DetectionRect.Visibility != Visibility.Visible)
        {
            DetectionRect.Visibility = Visibility.Visible;
        }

        DetectionModeText.Text = $"{L("Mode")}: {L(_detectElements ? "UI element" : "Window")}   •   {_detectionIndex + 1}/{_detectionCandidates.Count}   •   {detected.Name}";
    }

    private void ClearDetectionHighlight()
    {
        _detectedSelection = Rect.Empty;
        DetectionRect.Visibility = Visibility.Collapsed;
        if (_selection.Width < 3 && !_dragging && !_resizing && !_annotationMode && !_recordingRunning)
        {
            SelectionMask.Visibility = Visibility.Collapsed;
            MaskLayer.Visibility = Visibility.Visible;
        }
    }

    private void PositionActionBar()
    {
        var originalVisibility = ActionBar.Visibility;
        if (originalVisibility == Visibility.Collapsed) ActionBar.Visibility = Visibility.Hidden;
        ActionBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = ActionBar.DesiredSize.Width;
        var height = ActionBar.DesiredSize.Height;
        var placement = OverlayLayoutService.PlaceBelowAndKeepVisible(_selection, new Size(width, height), new Size(ActualWidth, ActualHeight));
        Canvas.SetLeft(ActionBar, placement.X);
        var reservedHeight = _annotationMode
            ? 90
            : OcrPanel.Visibility == Visibility.Visible ? OcrPanel.Height + 5 : 0;
        var maximumTop = Math.Max(8, ActualHeight - height - reservedHeight - 8);
        Canvas.SetTop(ActionBar, Math.Min(placement.Y, maximumTop));
        ActionBar.Visibility = originalVisibility;
        if (_annotationMode && CaptureInlineEditor.Visibility == Visibility.Visible)
        {
            var propertyTop = Canvas.GetTop(ActionBar) + Math.Max(1, height) + 2;
            CaptureInlineEditor.UpdateCaptureToolbarPosition(Canvas.GetLeft(ActionBar), propertyTop);
        }
    }

    private Rect ActionBarBounds()
    {
        ActionBar.UpdateLayout();
        var width = ActionBar.ActualWidth > 1 ? ActionBar.ActualWidth : ActionBar.DesiredSize.Width;
        var height = ActionBar.ActualHeight > 1 ? ActionBar.ActualHeight : ActionBar.DesiredSize.Height;
        return new Rect(Canvas.GetLeft(ActionBar), Canvas.GetTop(ActionBar), Math.Max(1, width), Math.Max(1, height));
    }

    private void PositionSelectionHandles()
    {
        SelectionHandles.Width = Math.Max(1, ActualWidth);
        SelectionHandles.Height = Math.Max(1, ActualHeight);
        const double radius = 6.5;
        var centerX = _selection.X + _selection.Width / 2;
        var centerY = _selection.Y + _selection.Height / 2;
        SetHandle(HandleNW, _selection.Left - radius, _selection.Top - radius);
        SetHandle(HandleN, centerX - radius, _selection.Top - radius);
        SetHandle(HandleNE, _selection.Right - radius, _selection.Top - radius);
        SetHandle(HandleE, _selection.Right - radius, centerY - radius);
        SetHandle(HandleSE, _selection.Right - radius, _selection.Bottom - radius);
        SetHandle(HandleS, centerX - radius, _selection.Bottom - radius);
        SetHandle(HandleSW, _selection.Left - radius, _selection.Bottom - radius);
        SetHandle(HandleW, _selection.Left - radius, centerY - radius);
    }

    private void RenderSelectionMask() => RenderSpotlightMask(_selection);

    /// <summary>
    /// Dim the full-screen capture everywhere except <paramref name="lit"/>,
    /// so the highlighted region appears at full captured brightness.
    /// </summary>
    private void RenderSpotlightMask(Rect lit)
    {
        MaskLayer.Visibility = Visibility.Collapsed;
        SelectionMask.Visibility = Visibility.Visible;
        SelectionMask.Width = Math.Max(1, ActualWidth);
        SelectionMask.Height = Math.Max(1, ActualHeight);
        var hole = Rect.Intersect(new Rect(0, 0, ActualWidth, ActualHeight), lit);
        if (hole.IsEmpty)
        {
            SetMask(MaskTop, 0, 0, ActualWidth, ActualHeight);
            SetMask(MaskBottom, 0, 0, 0, 0);
            SetMask(MaskLeft, 0, 0, 0, 0);
            SetMask(MaskRight, 0, 0, 0, 0);
            return;
        }

        SetMask(MaskTop, 0, 0, ActualWidth, hole.Top);
        SetMask(MaskBottom, 0, hole.Bottom, ActualWidth, Math.Max(0, ActualHeight - hole.Bottom));
        SetMask(MaskLeft, 0, hole.Top, hole.Left, hole.Height);
        SetMask(MaskRight, hole.Right, hole.Top, Math.Max(0, ActualWidth - hole.Right), hole.Height);
    }

    private static void SetMask(FrameworkElement mask, double left, double top, double width, double height)
    {
        Canvas.SetLeft(mask, left); Canvas.SetTop(mask, top);
        mask.Width = Math.Max(0, width); mask.Height = Math.Max(0, height);
    }

    private static void SetHandle(FrameworkElement handle, double left, double top)
    {
        Canvas.SetLeft(handle, left);
        Canvas.SetTop(handle, top);
    }

    private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string handle }) return;
        if (_annotationMode)
        {
            var document = CaptureInlineEditor.SnapshotDocument();
            _annotationResizeItems = document.Items.Select(item => item.Clone()).ToList();
            _annotationResizePixelRegion = SelectedPixelRegion();
        }
        _resizing = true;
        _resizeHandle = handle;
        _resizeStart = e.GetPosition(OverlayCanvas);
        _resizeStartRect = _selection;
        SetResizeToolbarsVisible(false);
        CaptureMouse();
        e.Handled = true;
    }

    private void SetResizeToolbarsVisible(bool visible)
    {
        ActionBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_annotationMode)
            CaptureInlineEditor.SetCapturePropertiesToolbarVisible(visible);
    }

    private void RefreshInlineAnnotationAfterResize()
    {
        if (!_annotationMode || _annotationResizeItems is null) return;
        var newPixelRegion = SelectedPixelRegion();
        var shiftedItems = ShiftAnnotationsForRecrop(_annotationResizeItems, _annotationResizePixelRegion, newPixelRegion);
        _annotationResizeItems = null;
        CaptureInlineEditor.LoadImage(SelectedImage(), shiftedItems);
        CaptureInlineEditor.UpdateCaptureOverlayBounds(_selection, new Size(ActualWidth, ActualHeight), _selection);
        SelectionRect.Fill = Brushes.Transparent;
        SelectionRect.Visibility = Visibility.Visible;
        SelectionHandles.Visibility = Visibility.Visible;
        RenderSelection();
        CaptureInlineEditor.Focus();
    }

    internal static IReadOnlyList<AnnotationItem> ShiftAnnotationsForRecrop(
        IEnumerable<AnnotationItem> items, Int32Rect oldPixelRegion, Int32Rect newPixelRegion)
    {
        var offsetX = oldPixelRegion.X - newPixelRegion.X;
        var offsetY = oldPixelRegion.Y - newPixelRegion.Y;
        return items.Select(item =>
        {
            var shifted = item.Clone();
            shifted.X += offsetX;
            shifted.Y += offsetY;
            shifted.Points = shifted.Points.Select(point => new Point(point.X + offsetX, point.Y + offsetY)).ToList();
            return shifted;
        }).ToList();
    }

    private void UpdateResize(Point point)
    {
        var dx = point.X - _resizeStart.X;
        var dy = point.Y - _resizeStart.Y;
        var left = _resizeStartRect.Left;
        var right = _resizeStartRect.Right;
        var top = _resizeStartRect.Top;
        var bottom = _resizeStartRect.Bottom;
        if (_resizeHandle.Contains('W')) left = Math.Clamp(left + dx, 0, right - 3);
        if (_resizeHandle.Contains('E')) right = Math.Clamp(right + dx, left + 3, ActualWidth);
        if (_resizeHandle.Contains('N')) top = Math.Clamp(top + dy, 0, bottom - 3);
        if (_resizeHandle.Contains('S')) bottom = Math.Clamp(bottom + dy, top + 3, ActualHeight);
        _selection = new Rect(left, top, right - left, bottom - top);
        RenderSelection();
    }

    private void PositionFloatingPanels()
    {
        ShortcutHints.Width = Math.Min(330, Math.Max(220, ActualWidth - 32));
        OcrPanel.Width = Math.Min(580, Math.Max(300, ActualWidth - 36));
        OcrPanel.Height = Math.Min(390, Math.Max(250, ActualHeight - 36));
        Canvas.SetLeft(ShortcutHints, 16);
        ShortcutHints.Measure(new Size(ShortcutHints.Width, double.PositiveInfinity));
        Canvas.SetTop(ShortcutHints, Math.Max(16, ActualHeight - ShortcutHints.DesiredSize.Height - 18));
        if (ActionBar.Visibility == Visibility.Visible && _selection.Width >= 3 && _selection.Height >= 3)
        {
            PositionActionBar();
            var placement = OverlayLayoutService.PlaceBelowAndKeepVisible(
                ActionBarBounds(), new Size(OcrPanel.Width, OcrPanel.Height), new Size(ActualWidth, ActualHeight), 5);
            Canvas.SetLeft(OcrPanel, placement.X);
            Canvas.SetTop(OcrPanel, placement.Y);
        }
        else
        {
            Canvas.SetLeft(OcrPanel, Math.Max(10, ActualWidth - OcrPanel.Width - 18));
            Canvas.SetTop(OcrPanel, Math.Max(10, (ActualHeight - OcrPanel.Height) / 2));
        }
        PositionSelectionHandles();
        if (_selection.Width >= 3 && _selection.Height >= 3) RenderSelectionMask();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PositionFloatingPanels();
        if (_recordingRunning || _recordingReviewRunning) PositionRecordingControls();
        if (_annotationMode)
        {
            CaptureInlineEditor.UpdateCaptureOverlayBounds(_selection, new Size(ActualWidth, ActualHeight), _selection);
        }
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_annotationMode) return;
        if (_settings.ShowElementDetection == false || _selection.Width >= 3 || _dragging || _resizing || _detectionCandidates.Count <= 1) return;
        _detectionIndex = Math.Clamp(_detectionIndex + (e.Delta < 0 ? 1 : -1), 0, _detectionCandidates.Count - 1);
        RenderDetectedCandidate();
        e.Handled = true;
    }

    private BitmapSource SelectedImage()
        => CaptureService.Crop(_screen, SelectedPixelRegion());

    private Int32Rect SelectedPixelRegion() => PixelRegion(_selection);

    private Int32Rect PixelRegion(Rect area)
    {
        var virtualBounds = DisplayTopologyService.VirtualBoundsPixels();
        var topLeft = OverlayToScreen(area.TopLeft);
        var bottomRight = OverlayToScreen(area.BottomRight);
        return CaptureCoordinateService.ToBitmapRegion(topLeft, bottomRight, virtualBounds, _screen.PixelWidth, _screen.PixelHeight);
    }

    private void FitToVirtualScreenPixels()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var bounds = DisplayTopologyService.VirtualBoundsPixels();
        if (handle != IntPtr.Zero)
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    /// <summary>
    /// Physical screen rectangle of this overlay HWND. Spanning multi-monitor
    /// overlays must not use WPF PointToScreen/PointFromScreen — those break
    /// when the window covers monitors with different DPI.
    /// </summary>
    private System.Drawing.Rectangle OverlayHwndScreenRect()
    {
        var handle = _overlayHandle != IntPtr.Zero
            ? _overlayHandle
            : new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && NativeMethods.GetWindowRect(handle, out var native) &&
            native.Width > 0 && native.Height > 0)
            return new System.Drawing.Rectangle(native.Left, native.Top, native.Width, native.Height);

        return DisplayTopologyService.VirtualBoundsPixels();
    }

    private Point OverlayToScreen(Point point)
    {
        var bounds = OverlayHwndScreenRect();
        var width = Math.Max(1.0, ActualWidth > 1 ? ActualWidth : bounds.Width);
        var height = Math.Max(1.0, ActualHeight > 1 ? ActualHeight : bounds.Height);
        return new Point(
            bounds.Left + point.X * bounds.Width / width,
            bounds.Top + point.Y * bounds.Height / height);
    }

    private Point ScreenToOverlay(Point point)
    {
        var bounds = OverlayHwndScreenRect();
        var width = Math.Max(1.0, ActualWidth > 1 ? ActualWidth : bounds.Width);
        var height = Math.Max(1.0, ActualHeight > 1 ? ActualHeight : bounds.Height);
        return new Point(
            (point.X - bounds.Left) * width / bounds.Width,
            (point.Y - bounds.Top) * height / bounds.Height);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        CompleteCopy();
    }

    private void QuickAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tool }) return;
        if (_annotationMode)
        {
            CaptureInlineEditor.ToggleConfiguredTool(tool);
            SetCaptureAnnotationActiveTool(
                CaptureInlineEditor.ActiveTool.Equals("None", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : CaptureInlineEditor.ActiveTool);
            CaptureInlineEditor.Focus();
            return;
        }
        BeginInlineAnnotation(tool);
    }

    private void Recapture_Click(object sender, RoutedEventArgs e)
    {
        _ocrCancellation?.Cancel();
        OcrPanel.Visibility = Visibility.Collapsed;
        _annotationHistoryRecordId = null;
        _ocrHistoryRecordId = null;
        if (_annotationMode)
        {
            _annotationMode = false;
            CaptureInlineEditor.Visibility = Visibility.Collapsed;
            SetCaptureAnnotationActiveTool(null);
            UpdateCaptureAnnotationHistoryButtons();
        }
        ResetSelection();
        Focus();
    }

    private void BeginInlineAnnotation(string? initialTool = null)
    {
        if (_annotationMode || !EnsureSelection()) return;
        // Preserve the user's existing toolbar anchor when annotation starts.
        // Every primary command already occupies a permanent slot, so opening
        // the property row must not alter the toolbar's size or position.
        PositionActionBar();
        var toolbarLeft = Canvas.GetLeft(ActionBar);
        var toolbarTop = Canvas.GetTop(ActionBar);
        _annotationMode = true;
        _ocrCancellation?.Cancel();
        OcrPanel.Visibility = Visibility.Collapsed;
        ActionBar.Visibility = Visibility.Visible;
        ActionBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var actionWidth = ActionBar.DesiredSize.Width;
        var actionHeight = ActionBar.DesiredSize.Height;
        Canvas.SetLeft(ActionBar, Math.Clamp(toolbarLeft, 8, Math.Max(8, ActualWidth - actionWidth - 8)));
        Canvas.SetTop(ActionBar, Math.Clamp(toolbarTop, 8, Math.Max(8, ActualHeight - actionHeight - 98)));
        var actionBounds = ActionBarBounds();
        SelectionRect.Fill = Brushes.Transparent;
        SelectionHandles.Visibility = Visibility.Visible;
        SelectionRect.Visibility = Visibility.Visible;
        DetectionRect.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        ShortcutHints.Visibility = Visibility.Collapsed;
        CrossHorizontal.Visibility = Visibility.Collapsed;
        CrossVertical.Visibility = Visibility.Collapsed;
        CaptureInlineEditor.Visibility = Visibility.Visible;
        Canvas.SetLeft(CaptureInlineEditor, 0);
        Canvas.SetTop(CaptureInlineEditor, 0);
        // Keep the hidden inline editor's tool set identical to the capture
        // annotation toolbar so ActivateConfiguredTool can enable tools that
        // are visible on the action bar even when they are off in Preferences.
        var captureToolOrder = CaptureAnnotationToolPanel.Children.OfType<Button>()
            .Select(button => button.Tag as string)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Cast<string>()
            .ToList();
        var captureToolEnabled = CaptureAnnotationToolPanel.Children.OfType<Button>()
            .Where(button => button.Visibility == Visibility.Visible && button.Tag is string)
            .Select(button => (string)button.Tag)
            .ToList();
        CaptureInlineEditor.ApplyToolbarConfiguration(captureToolOrder, captureToolEnabled);
        CaptureInlineEditor.LoadImage(SelectedImage());
        CaptureInlineEditor.ConfigureCaptureOverlay(_selection, new Size(ActualWidth, ActualHeight), _selection,
            showActions: false, toolbarLeft: actionBounds.Left, toolbarTop: actionBounds.Bottom + 2,
            showPrimaryToolbar: false, allowToolToggleOff: true);
        UpdateCaptureAnnotationHistoryButtons();
        RenderSelection();
        initialTool ??= captureToolEnabled.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(initialTool))
        {
            CaptureInlineEditor.ActivateConfiguredTool(initialTool);
            SetCaptureAnnotationActiveTool(initialTool);
        }
        CaptureInlineEditor.Focus();
    }

    private void SetCaptureAnnotationActiveTool(string? activeTool)
    {
        foreach (var button in CaptureAnnotationToolPanel.Children.OfType<Button>())
        {
            var isActive = activeTool is not null &&
                string.Equals(button.Tag as string, activeTool, StringComparison.OrdinalIgnoreCase);
            button.Background = isActive
                ? (TryFindResource("ToolbarActiveBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(204, 251, 241)))
                : Brushes.Transparent;
            button.Foreground = isActive
                ? (TryFindResource("ToolbarActiveInkBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(13, 148, 136)))
                : (TryFindResource("ToolbarInkBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(51, 65, 85)));
        }
    }

    private void AnnotationUndo_Click(object sender, RoutedEventArgs e)
    {
        CaptureInlineEditor.UndoForCapture();
        CaptureInlineEditor.Focus();
    }

    private void AnnotationRedo_Click(object sender, RoutedEventArgs e)
    {
        CaptureInlineEditor.RedoForCapture();
        CaptureInlineEditor.Focus();
    }

    private void UpdateCaptureAnnotationHistoryButtons()
    {
        // These slots never appear or disappear. They are muted while the user
        // is only choosing a capture, then become available as soon as an
        // annotation tool opens (a no-history click simply has no effect).
        CaptureUndoButton.IsEnabled = _annotationMode;
        CaptureRedoButton.IsEnabled = _annotationMode;
    }

    private void ExitInlineAnnotation()
    {
        if (!_annotationMode) return;
        if (_completionMode == CaptureCompletionMode.FullScreenDraw)
        {
            Close();
            return;
        }
        _annotationMode = false;
        CaptureInlineEditor.Visibility = Visibility.Collapsed;
        SetCaptureAnnotationActiveTool(null);
        UpdateCaptureAnnotationHistoryButtons();
        SelectionRect.Fill = new SolidColorBrush(Color.FromArgb(5, 255, 255, 255));
        SelectionRect.Visibility = Visibility.Visible;
        SelectionHandles.Visibility = Visibility.Visible;
        ShortcutHints.Visibility = CaptureHintsVisibility;
        CrossHorizontal.Visibility = _settings.ShowCrossLines ? Visibility.Visible : Visibility.Collapsed;
        CrossVertical.Visibility = _settings.ShowCrossLines ? Visibility.Visible : Visibility.Collapsed;
        ActionBar.Visibility = Visibility.Visible;
        RenderSelection();
        PositionActionBar();
        Focus();
    }

    private void StoreCaptureAnnotation(AnnotationAppliedEventArgs document)
    {
        if (string.IsNullOrWhiteSpace(_annotationHistoryRecordId))
            _annotationHistoryRecordId = HistoryService.Add(document.BaseImage, SelectedPixelRegion(), _screen, "Edited").Id;
        HistoryService.SaveAnnotationDocument(_annotationHistoryRecordId, document.BaseImage, document.FlattenedImage, document.Items);
    }

    private void ApplyCaptureAnnotation(AnnotationAppliedEventArgs document)
    {
        StoreCaptureAnnotation(document);
        if (_annotationHistoryRecordId is { } id) HistoryService.UpdateSourceKind(id, "Edited");
        HistoryService.RememberLastRegion(SelectedPixelRegion());
        AutoSave(document.FlattenedImage);
        Close();
    }

    private BitmapSource CurrentResultImage(out bool annotationStored)
    {
        annotationStored = false;
        if (!_annotationMode) return SelectedImage();
        var document = CaptureInlineEditor.SnapshotDocument();
        StoreCaptureAnnotation(document);
        annotationStored = true;
        return document.FlattenedImage;
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => PinSelection(asThumbnail: false);

    private void PinThumbnail_Click(object sender, RoutedEventArgs e) => PinSelection(asThumbnail: true);

    private void PinSelection(bool asThumbnail)
    {
        var image = CurrentResultImage(out var annotationStored);
        Clipboard.SetImage(image);
        string? historyRecordId;
        if (!annotationStored)
            historyRecordId = HistoryService.Add(image, SelectedPixelRegion(), _screen, "Pinned").Id;
        else
        {
            historyRecordId = _annotationHistoryRecordId;
            if (historyRecordId is { } annotatedId) HistoryService.UpdateSourceKind(annotatedId, "Pinned");
        }
        HistoryService.RememberLastRegion(SelectedPixelRegion());
        AutoSave(image);
        var pixelRegion = SelectedPixelRegion();
        var virtualBounds = DisplayTopologyService.VirtualBoundsPixels();
        var pinBounds = new System.Drawing.Rectangle(
            virtualBounds.Left + pixelRegion.X,
            virtualBounds.Top + pixelRegion.Y,
            pixelRegion.Width,
            pixelRegion.Height);
        Close();
        var pin = new PinnedImageWindow(image, historyRecordId: historyRecordId, initialScreenPixelBounds: pinBounds);
        pin.Show();
        if (asThumbnail) pin.SetThumbnailMode(true);
    }

    private void CycleRegionHistory(int direction)
    {
        if (_regionHistory.Count == 0) return;
        _regionHistoryIndex = _regionHistoryIndex < 0
            ? direction >= 0 ? 0 : _regionHistory.Count - 1
            : (_regionHistoryIndex + direction + _regionHistory.Count) % _regionHistory.Count;
        var region = _regionHistory[_regionHistoryIndex];
        var virtualBounds = DisplayTopologyService.VirtualBoundsPixels();
        var topLeft = ScreenToOverlay(new Point(virtualBounds.Left + region.X, virtualBounds.Top + region.Y));
        var bottomRight = ScreenToOverlay(new Point(virtualBounds.Left + region.X + region.Width, virtualBounds.Top + region.Y + region.Height));
        _selection = Rect.Intersect(new Rect(0, 0, ActualWidth, ActualHeight), new Rect(topLeft, bottomRight));
        if (_selection.Width < 3 || _selection.Height < 3) return;
        _annotationHistoryRecordId = null;
        _ocrHistoryRecordId = null;
        RenderSelection();
        PositionActionBar();
        ActionBar.Visibility = Visibility.Visible;
    }

    private async void Recognize_Click(object sender, RoutedEventArgs e) => await RunCaptureOcrAsync(_settings.OcrAutoCopy);

    private async void LongCapture_Click(object sender, RoutedEventArgs e) => await RunLongCaptureAsync();

    private Microsoft.Win32.SaveFileDialog CreateImageSaveDialog()
    {
        var suggested = SettingsService.CreateOutputPath(
            Directory.Exists(_settings.QuickSaveFolder) ? _settings.QuickSaveFolder : Path.GetTempPath(),
            _settings.OutputFileName);
        return new Microsoft.Win32.SaveFileDialog
        {
            Filter = CaptureService.ImageSaveFilter,
            DefaultExt = CaptureService.ExtensionForFormat(_settings.OutputFormat),
            FilterIndex = CaptureService.FilterIndexForFormat(_settings.OutputFormat),
            AddExtension = true,
            InitialDirectory = Path.GetDirectoryName(suggested) ?? _settings.QuickSaveFolder,
            FileName = Path.GetFileName(suggested)
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateImageSaveDialog();
        if (dialog.ShowDialog(this) != true) return;
        var image = CurrentResultImage(out var annotationStored);
        CaptureService.SaveImage(image, dialog.FileName, _settings.ImageQuality, _settings);
        if (!annotationStored) HistoryService.Add(image, SelectedPixelRegion(), _screen, "Saved");
        else if (_annotationHistoryRecordId is { } annotatedId) HistoryService.UpdateSourceKind(annotatedId, "Saved");
        HistoryService.RememberLastRegion(SelectedPixelRegion());
        Close();
    }

    private void QuickSave_Click(object sender, RoutedEventArgs e)
    {
        var image = CurrentResultImage(out var annotationStored);
        var path = SettingsService.CreateOutputPath(_settings.QuickSaveFolder, _settings.OutputFileName);
        CaptureService.SaveImage(image, path, _settings.ImageQuality, _settings);
        Clipboard.SetImage(image);
        if (!annotationStored) HistoryService.Add(image, SelectedPixelRegion(), _screen, "Saved");
        else if (_annotationHistoryRecordId is { } annotatedId) HistoryService.UpdateSourceKind(annotatedId, "Saved");
        HistoryService.RememberLastRegion(SelectedPixelRegion());
        if (_settings.ShowSaveNotification) System.Media.SystemSounds.Asterisk.Play();
        Close();
    }

    private void CompleteCopy()
    {
        var image = CurrentResultImage(out var annotationStored);
        Clipboard.SetImage(image);
        if (!annotationStored) HistoryService.Add(image, SelectedPixelRegion(), _screen, "Copied");
        else if (_annotationHistoryRecordId is { } annotatedId) HistoryService.UpdateSourceKind(annotatedId, "Copied");
        HistoryService.RememberLastRegion(SelectedPixelRegion());
        AutoSave(image);
        Close();
    }

    private void AutoSave(BitmapSource image)
    {
        if (!_settings.AutoSave || string.IsNullOrWhiteSpace(_settings.AutoSaveFolder)) return;
        CaptureService.SaveImage(image, SettingsService.CreateOutputPath(_settings.AutoSaveFolder, _settings.OutputFileName), _settings.ImageQuality, _settings);
    }

    private static void TrySetBrush(DependencyObject target, DependencyProperty property, string value)
    {
        try
        {
            if (new BrushConverter().ConvertFromString(value) is Brush brush) target.SetValue(property, brush);
        }
        catch
        {
            // Keep the built-in color if the custom value is invalid.
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_ocrAreaSelectionMode)
        {
            if (e.Key == Key.Escape) CancelOcrAreaSelection();
            e.Handled = true;
            return;
        }
        if (_annotationMode)
        {
            if (e.Key == Key.Escape) Close();
            e.Handled = true;
            return;
        }
        if (_recordingRunning)
        {
            if (_recordingReviewRunning)
            {
                if (e.Key == Key.Space) RecordingPreviewPlay_Click(this, new RoutedEventArgs());
                else if (e.Key == Key.Escape) DiscardRecording_Click(this, new RoutedEventArgs());
            }
            else if (e.Key == Key.Space) RecordingPause_Click(this, new RoutedEventArgs());
            else if (e.Key is Key.Escape or Key.R) RecordingStop_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (OcrPanel.Visibility == Visibility.Visible) CloseOcr_Click(this, new RoutedEventArgs()); else Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Tab)
        {
            if (_settings.ShowElementDetection == false)
            {
                ClearDetectionHighlight();
                e.Handled = true;
                return;
            }
            _detectElements = !_detectElements;
            DetectionWheelHint.Visibility = _detectElements ? Visibility.Visible : Visibility.Collapsed;
            _lastDetectionScreenPoint = new Point(double.NaN, double.NaN);
            _detectionClock.Reset();
            _detectionClock.Start();
            UpdateDetectedRegion(Mouse.GetPosition(OverlayCanvas));
            DetectionModeText.Text = $"{L("Mode")}: {L(_detectElements ? "UI element" : "Window")}";
            e.Handled = true;
            return;
        }
        if (e.Key is Key.OemComma or Key.OemPeriod)
        {
            CycleRegionHistory(e.Key == Key.OemComma ? 1 : -1);
            e.Handled = true;
            return;
        }
        if (e.Key is Key.W or Key.A or Key.S or Key.D)
        {
            var cursor = Forms.Cursor.Position;
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            if (e.Key == Key.W) cursor.Y -= step;
            if (e.Key == Key.S) cursor.Y += step;
            if (e.Key == Key.A) cursor.X -= step;
            if (e.Key == Key.D) cursor.X += step;
            NativeMethods.SetCursorPos(cursor.X, cursor.Y);
            e.Handled = true;
            return;
        }
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            if (ColorMagnifierEnabled && _colorMagnifier.Visibility == Visibility.Visible &&
                (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            {
                _colorMagnifier.ToggleFormat();
                _colorMagnifierUseHex = _colorMagnifier.UseHexFormat;
                PersistColorMagnifierFormat();
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.C)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                if (!_colorPickerMode)
                    _ = RunCaptureOcrAsync(true);
            }
            else if (ColorMagnifierEnabled && _colorMagnifier.Visibility == Visibility.Visible)
            {
                _colorMagnifier.TryCopyColor();
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.L)
        {
            _ = RunLongCaptureAsync();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.R)
        {
            _ = RunRecordingAsync();
            e.Handled = true;
            return;
        }
        if (_selection.Width >= 3 && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var pixelX = ActualWidth / Math.Max(1, _screen.PixelWidth);
            var pixelY = ActualHeight / Math.Max(1, _screen.PixelHeight);
            var multiplier = (Keyboard.Modifiers & ModifierKeys.Alt) != 0 ? 10 : 1;
            pixelX *= multiplier;
            pixelY *= multiplier;
            var dx = e.Key == Key.Left ? -pixelX : e.Key == Key.Right ? pixelX : 0;
            var dy = e.Key == Key.Up ? -pixelY : e.Key == Key.Down ? pixelY : 0;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                // Ctrl adjusts the top/left edge while preserving the opposite edge.
                if (dx != 0)
                {
                    var right = _selection.Right;
                    _selection.X = Math.Clamp(_selection.X + dx, 0, right - pixelX);
                    _selection.Width = right - _selection.X;
                }
                if (dy != 0)
                {
                    var bottom = _selection.Bottom;
                    _selection.Y = Math.Clamp(_selection.Y + dy, 0, bottom - pixelY);
                    _selection.Height = bottom - _selection.Y;
                }
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                _selection.Width = Math.Max(pixelX, Math.Min(ActualWidth - _selection.X, _selection.Width + dx));
                _selection.Height = Math.Max(pixelY, Math.Min(ActualHeight - _selection.Y, _selection.Height + dy));
            }
            else
            {
                _selection.X = Math.Clamp(_selection.X + dx, 0, Math.Max(0, ActualWidth - _selection.Width));
                _selection.Y = Math.Clamp(_selection.Y + dy, 0, Math.Max(0, ActualHeight - _selection.Height));
            }
            RenderSelection();
            PositionActionBar();
            e.Handled = true;
        }
        if (e.Key == Key.Enter && _selection.Width >= 3)
        {
            Clipboard.SetImage(SelectedImage());
            Close();
        }
    }

    private void ResetSelection()
    {
        _selection = Rect.Empty;
        _detectionCandidates = [];
        _lastDetectionScreenPoint = new Point(double.NaN, double.NaN);
        SelectionRect.Visibility = Visibility.Collapsed;
        SelectionHandles.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        ActionBar.Visibility = Visibility.Collapsed;
        ClearDetectionHighlight();
        if (!_colorPickerMode)
            _colorMagnifier.Visibility = Visibility.Collapsed;
    }

    private static string L(string value) => LocalizationService.Current(value);
}
