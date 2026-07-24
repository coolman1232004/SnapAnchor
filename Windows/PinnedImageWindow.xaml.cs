using Microsoft.Win32;
using SnapAnchor.Models;
using SnapAnchor.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SnapAnchor.Windows;

internal sealed record PinSnapshot(Guid Id, BitmapSource Image, string Group, bool IsVisible, bool IsOnCurrentDesktop, string SizeLabel)
{
    public string VisibilityLabel => !IsVisible ? "Hidden" : IsOnCurrentDesktop ? "Visible" : "Other desktop";
}

public partial class PinnedImageWindow : Window
{
    private static readonly HashSet<PinnedImageWindow> OpenPins = [];
    private static readonly HashSet<PinnedImageWindow> SelectedPins = [];
    private static bool _soloMode;
    public static event EventHandler? PinsChanged;

    private readonly Guid _pinId = Guid.NewGuid();
    private BitmapSource _baseSource;
    private BitmapSource _source;
    private readonly AppSettings _settings;
    private readonly bool _startEditing;
    private string _groupName;
    private string _backgroundMode;
    private string _inlineMode = "None";
    private string? _historyRecordId;
    private string _lastRecognitionFingerprint = string.Empty;
    private bool _grayscale;
    private bool _inverted;
    private bool _clickThrough;
    private bool _thumbnail;
    private bool _positionLocked;
    private bool _shaded;
    private bool _edgeSnapping = true;
    private double _preShadeHeight;
    private string _pinTitle = string.Empty;
    private readonly bool _longScrollable;
    private Rect _preThumbnailBounds;
    private double _normalHeight;
    private double _rotation;
    private double _flipX = 1;
    private double _flipY = 1;
    private readonly DispatcherTimer _zoomBadgeTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _textAutoScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(28) };
    private readonly DispatcherTimer _longScrollAnimationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _screenPixelBoundsTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private Rect _targetZoomBounds;
    private System.Drawing.Rectangle? _initialScreenPixelBounds;
    private int _screenPixelBoundsPasses;
    private int _screenPixelBoundsStablePasses;
    private bool _shadowEnabled;
    private string _sourceFilePath = string.Empty;
    private CancellationTokenSource? _selectableTextCancellation;
    private BitmapSource? _recognizedTextSource;
    private RecognitionResult? _selectableTextResult;
    private IReadOnlyList<RecognizedWord> _recognizedWords = Array.Empty<RecognizedWord>();
    private readonly Dictionary<Canvas, List<Rect>> _textHitRegions = [];
    private bool _textSelectable;
    private bool _selectingText;
    private int _textAutoScrollDirection;
    private double _targetLongScrollOffset;
    private int? _textSelectionAnchor;
    private int? _textSelectionEnd;
    private Point? _textSelectionToolbarPosition;
    private readonly bool _enableSelectableTextOnLoad;
    private PinnedAnnotationOverlayWindow? _annotationOverlay;

    public PinnedImageWindow(BitmapSource source, bool startEditing = false, string? groupName = null, string? backgroundMode = null,
        string? historyRecordId = null, bool applyTextSelectableDefault = true,
        System.Drawing.Rectangle? initialScreenPixelBounds = null)
    {
        InitializeComponent();
        _settings = SettingsService.Load();
        _baseSource = _source = source;
        _startEditing = startEditing;
        _groupName = groupName ?? _settings.CurrentPinGroup;
        _backgroundMode = backgroundMode ?? _settings.DefaultPinBackground;
        _pinTitle = "Pinned image";
        Title = $"SnapAnchor - {_pinTitle}";
        _historyRecordId = historyRecordId;
        _enableSelectableTextOnLoad = applyTextSelectableDefault && !startEditing && _settings.PinTextSelectableByDefault;
        _initialScreenPixelBounds = initialScreenPixelBounds;
        _longScrollable = source.PixelHeight > source.PixelWidth * 2.5;
        PinnedImage.Source = source;
        LongPinnedImage.Source = source;
        Opacity = Math.Clamp(_settings.PinDefaultOpacity / 100.0, 0.15, 1);
        _shadowEnabled = _settings.PinWindowShadow;
        ApplyShadow();
        ApplyBackground();
        SizeToImage(source);
        _targetZoomBounds = new Rect(Left, Top, Width, Height);
        _zoomBadgeTimer.Tick += (_, _) => { _zoomBadgeTimer.Stop(); ZoomBadge.Visibility = Visibility.Collapsed; };
        _refreshTimer.Tick += (_, _) => RefreshScreenshot(silent: true);
        _textAutoScrollTimer.Tick += TextAutoScrollTimer_Tick;
        _longScrollAnimationTimer.Tick += LongScrollAnimationTimer_Tick;
        _screenPixelBoundsTimer.Tick += ScreenPixelBoundsTimer_Tick;
        ShowImageSurface();
        SizeChanged += (_, _) =>
        {
            UpdateLongImageWidth();
            ApplyImageScalingMode(animating: false);
            if (_textSelectable) Dispatcher.BeginInvoke(UpdateSelectableTextLayout, DispatcherPriority.Loaded);
        };
        OpenPins.Add(this);
        SourceInitialized += (_, _) =>
        {
            ApplyCaptureAffinityToWindow();
            ApplyVirtualDesktopBinding();
            ApplyInitialScreenPixelBounds();
        };
        DpiChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(() => ApplyImageScalingMode(animating: false), DispatcherPriority.Render);
            if (_initialScreenPixelBounds is { } bounds)
                Dispatcher.BeginInvoke(() => BeginScreenPixelBoundsStabilization(bounds), DispatcherPriority.Render);
        };
        ContentRendered += (_, _) =>
        {
            if (_initialScreenPixelBounds is null) return;
            Dispatcher.BeginInvoke(() =>
            {
                ApplyInitialScreenPixelBounds();
                Dispatcher.BeginInvoke(() =>
                {
                    if (_initialScreenPixelBounds is { } bounds) BeginScreenPixelBoundsStabilization(bounds);
                }, DispatcherPriority.ContextIdle);
            }, DispatcherPriority.Render);
        };
        Closed += (_, _) =>
        {
            _annotationOverlay?.Close();
            _annotationOverlay = null;
            _zoomBadgeTimer.Stop();
            _refreshTimer.Stop();
            _textAutoScrollTimer.Stop();
            _longScrollAnimationTimer.Stop();
            _screenPixelBoundsTimer.Stop();
            _selectableTextCancellation?.Cancel();
            _selectableTextCancellation?.Dispose();
            OpenPins.Remove(this);
            SelectedPins.Remove(this);
            NotifyPinsChanged();
        };
        InlineEditor.Applied += ApplyEditedImage;
        InlineEditor.DocumentStored += PersistEditedDocument;
        InlineEditor.Cancelled += (_, _) => ExitInlineMode();
        InlineCrop.Applied += ApplyCroppedImage;
        InlineCrop.Cancelled += (_, _) => ExitInlineMode();
        InlineRecognition.RecognitionCompleted += Recognition_Completed;
        InlineRecognition.Cancelled += (_, _) => ExitInlineMode();
        Loaded += async (_, _) =>
        {
            AccessibilityService.Apply(this);
            if (TextSelectionToolbar.Visibility == Visibility.Visible)
                AccessibilityService.ApplyToolbar(TextSelectionToolbar);
            Focus();
            ApplyImageScalingMode(animating: false);
            if (!_groupName.Equals(_settings.CurrentPinGroup, StringComparison.OrdinalIgnoreCase)) Hide();
            if (_startEditing) BeginEditMode();
            else if (_textSelectable && _recognizedWords.Count > 0) RebuildSelectableTextLayers();
            else if (_enableSelectableTextOnLoad) await SetTextSelectableAsync(true);
            UpdatePinStateBadge();
            NotifyPinsChanged();
        };
    }

    internal static IReadOnlyList<PinSnapshot> Snapshots() => OpenPins
        .OrderBy(pin => pin._groupName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(pin => pin._pinId)
        .Select(pin => new PinSnapshot(
            pin._pinId,
            pin._source,
            pin._groupName,
            pin.IsVisible,
            VirtualDesktopService.IsOnCurrentDesktop(new WindowInteropHelper(pin).Handle),
            $"{Math.Round(pin.Width)} × {Math.Round(pin._inlineMode == "None" ? pin.Height : pin._normalHeight)}"))
        .ToList();

    public static void ActivatePin(Guid id)
    {
        var pin = OpenPins.FirstOrDefault(candidate => candidate._pinId == id);
        if (pin is null) return;
        SwitchGroup(pin._groupName);
        pin.Show();
        pin.WindowState = WindowState.Normal;
        pin.Activate();
        NotifyPinsChanged();
    }

    public static void ClosePin(Guid id) => OpenPins.FirstOrDefault(pin => pin._pinId == id)?.Close();

    public static void MovePin(Guid id, string group)
    {
        var pin = OpenPins.FirstOrDefault(candidate => candidate._pinId == id);
        if (pin is null) return;
        pin._groupName = group;
        pin.ApplyVirtualDesktopBinding();
        if (!group.Equals(SettingsService.Load().CurrentPinGroup, StringComparison.OrdinalIgnoreCase)) pin.Hide();
        SaveSession();
        NotifyPinsChanged();
    }

    public static void ReassignGroup(string oldGroup, string replacementGroup)
    {
        foreach (var pin in OpenPins.Where(pin => pin._groupName.Equals(oldGroup, StringComparison.OrdinalIgnoreCase)))
        {
            pin._groupName = replacementGroup;
            pin.ApplyVirtualDesktopBinding();
        }
        SwitchGroup(replacementGroup);
        SaveSession();
        NotifyPinsChanged();
    }

    public static void ApplyCaptureAffinity(bool exclude)
    {
        foreach (var pin in OpenPins.ToArray()) pin.ApplyCaptureAffinityToWindow(exclude);
    }

    public static void RestoreInteractions()
    {
        foreach (var pin in OpenPins.ToArray()) pin.SetClickThrough(false);
    }

    public static void ToggleAllVisibility()
    {
        var groupPins = OpenPins.Where(pin => pin._groupName.Equals(SettingsService.Load().CurrentPinGroup, StringComparison.OrdinalIgnoreCase)).ToList();
        var shouldHide = groupPins.Any(pin => pin.IsVisible);
        foreach (var pin in groupPins) { if (shouldHide) pin.Hide(); else pin.Show(); }
        NotifyPinsChanged();
    }

    public static void CloseAll()
    {
        foreach (var pin in OpenPins.ToArray()) pin.Close();
    }

    public static void CloseGroup(string groupName)
    {
        foreach (var pin in OpenPins.Where(pin => pin._groupName.Equals(groupName, StringComparison.OrdinalIgnoreCase)).ToArray())
            pin.Close();
    }

    public static void SwitchGroup(string groupName)
    {
        var settings = SettingsService.Load();
        if (!settings.PinGroups.Contains(groupName, StringComparer.OrdinalIgnoreCase)) return;
        settings.CurrentPinGroup = groupName;
        SettingsService.Save(settings);
        var targetDesktop = settings.PinGroupsFollowVirtualDesktops
            ? VirtualDesktopService.BoundDesktop(settings, groupName)
            : VirtualDesktopService.CurrentDesktopId();
        _soloMode = false;
        ClearSelection();
        foreach (var pin in OpenPins.ToArray())
        {
            if (pin._groupName.Equals(groupName, StringComparison.OrdinalIgnoreCase))
            {
                if (targetDesktop is { } desktopId) pin.MoveToDesktop(desktopId);
                pin.Show();
            }
            else pin.Hide();
        }
        NotifyPinsChanged();
    }

    public static void ToggleSolo()
    {
        if (SelectedPins.Count == 0) return;
        _soloMode = !_soloMode;
        var current = SettingsService.Load().CurrentPinGroup;
        foreach (var pin in OpenPins.ToArray())
        {
            var visible = pin._groupName.Equals(current, StringComparison.OrdinalIgnoreCase) && (!_soloMode || SelectedPins.Contains(pin));
            if (visible) pin.Show(); else pin.Hide();
        }
        NotifyPinsChanged();
    }

    public static void SaveSession() => _ = SaveSessionAsync();

    public static Task SaveSessionAsync()
    {
        var settings = SettingsService.Load();
        if (!settings.AutoBackup)
            return PinSessionService.QueueClear();

        var snapshots = OpenPins.OrderBy(pin => pin._pinId).Select(pin =>
        {
            var image = pin._source.IsFrozen ? pin._source : pin._source.Clone();
            if (!image.IsFrozen) image.Freeze();
            var handle = new WindowInteropHelper(pin).Handle;
            var physicalRect = default(NativeMethods.NativeRect);
            var hasPhysicalBounds = handle != IntPtr.Zero && NativeMethods.GetWindowRect(handle, out physicalRect);
            var screen = handle != IntPtr.Zero ? Forms.Screen.FromHandle(handle) : Forms.Screen.PrimaryScreen;
            var monitorBounds = handle != IntPtr.Zero
                ? DisplayTopologyService.MonitorBoundsForWindowPixels(handle)
                : DisplayTopologyService.VirtualBoundsPixels();
            return new PinSessionSnapshot(image, new PinSessionItem
            {
                Left = pin.Left,
                Top = pin.Top,
                Width = pin.Width,
                Height = pin._shaded ? Math.Max(60, pin._preShadeHeight) : pin._inlineMode == "None" ? pin.Height : pin._normalHeight,
                Opacity = pin.Opacity,
                Topmost = pin.Topmost,
                Group = pin._groupName,
                Background = pin._backgroundMode,
                DesktopId = VirtualDesktopService.GetDesktopId(new WindowInteropHelper(pin).Handle)?.ToString() ?? string.Empty,
                HistoryRecordId = pin._historyRecordId ?? string.Empty,
                TextSelectable = pin._textSelectable,
                OcrText = pin._selectableTextResult?.Text ?? string.Empty,
                OcrBarcodeText = pin._selectableTextResult?.BarcodeText ?? string.Empty,
                OcrBarcodeFormat = pin._selectableTextResult?.BarcodeFormat ?? string.Empty,
                OcrWords = pin._recognizedWords.ToList(),
                Title = pin._pinTitle,
                PositionLocked = pin._positionLocked,
                WindowShaded = pin._shaded,
                EdgeSnapping = pin._edgeSnapping,
                MonitorDeviceName = screen?.DeviceName ?? string.Empty,
                PhysicalLeft = hasPhysicalBounds ? physicalRect.Left : 0,
                PhysicalTop = hasPhysicalBounds ? physicalRect.Top : 0,
                PhysicalWidth = hasPhysicalBounds ? physicalRect.Width : 0,
                PhysicalHeight = hasPhysicalBounds ? physicalRect.Height : 0,
                MonitorLeft = monitorBounds.Left,
                MonitorTop = monitorBounds.Top,
                MonitorWidth = monitorBounds.Width,
                MonitorHeight = monitorBounds.Height,
                LongScrollOffset = pin._longScrollable ? pin.LongImageViewer.VerticalOffset : 0,
                IsVisible = pin.IsVisible,
                ClickThrough = pin._clickThrough
            });
        }).ToList();
        var fingerprint = string.Join("|", snapshots.Select(snapshot => FormattableString.Invariant(
            $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(snapshot.Image)}:{snapshot.Item.Left:R}:{snapshot.Item.Top:R}:{snapshot.Item.Width:R}:{snapshot.Item.Height:R}:{snapshot.Item.Opacity:R}:{snapshot.Item.Topmost}:{snapshot.Item.Group}:{snapshot.Item.Background}:{snapshot.Item.DesktopId}:{snapshot.Item.HistoryRecordId}:{snapshot.Item.TextSelectable}:{snapshot.Item.OcrWords.Count}:{snapshot.Item.Title}:{snapshot.Item.PositionLocked}:{snapshot.Item.WindowShaded}:{snapshot.Item.EdgeSnapping}:{snapshot.Item.MonitorDeviceName}:{snapshot.Item.PhysicalLeft}:{snapshot.Item.PhysicalTop}:{snapshot.Item.PhysicalWidth}:{snapshot.Item.PhysicalHeight}:{snapshot.Item.LongScrollOffset:R}:{snapshot.Item.IsVisible}:{snapshot.Item.ClickThrough}")));
        return PinSessionService.QueueSave(snapshots, fingerprint);
    }

    public static void RestoreSession()
    {
        if (OpenPins.Count > 0 || !SettingsService.Load().AutoBackup) return;
        foreach (var (image, item) in PinSessionService.Load())
        {
            var pin = new PinnedImageWindow(image, false, item.Group, item.Background, item.HistoryRecordId,
                applyTextSelectableDefault: false)
            {
                Opacity = item.Opacity,
                Topmost = item.Topmost
            };
            pin.RestoreSessionState(item);
            if (item.PhysicalWidth <= 0 || item.PhysicalHeight <= 0)
                pin.SetInitialBounds(new Rect(item.Left, item.Top, item.Width, item.Height));
            pin.Show();
            pin.RestorePhysicalPlacement(item);
            pin.Dispatcher.BeginInvoke(() =>
            {
                if (pin._longScrollable && item.LongScrollOffset > 0)
                    pin.LongImageViewer.ScrollToVerticalOffset(item.LongScrollOffset);
                if (item.ClickThrough) pin.SetClickThrough(true);
                if (!item.IsVisible) pin.Hide();
            }, DispatcherPriority.Loaded);
            var settings = SettingsService.Load();
            var desktop = settings.PinGroupsFollowVirtualDesktops
                ? VirtualDesktopService.BoundDesktop(settings, item.Group)
                : Guid.TryParse(item.DesktopId, out var restoredDesktop) ? restoredDesktop : null;
            if (desktop is { } desktopId) pin.MoveToDesktop(desktopId);
        }
    }

    private void RestorePhysicalPlacement(PinSessionItem item)
    {
        if (item.PhysicalWidth <= 0 || item.PhysicalHeight <= 0) return;
        var screens = Forms.Screen.AllScreens;
        var target = screens.FirstOrDefault(candidate =>
            candidate.DeviceName.Equals(item.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
        var desired = new System.Drawing.Rectangle(item.PhysicalLeft, item.PhysicalTop, item.PhysicalWidth, item.PhysicalHeight);
        if (target is null)
        {
            target = Forms.Screen.PrimaryScreen ?? screens.FirstOrDefault();
            if (target is null) return;
            var oldWidth = Math.Max(1, item.MonitorWidth);
            var oldHeight = Math.Max(1, item.MonitorHeight);
            var relativeX = (item.PhysicalLeft - item.MonitorLeft) / (double)oldWidth;
            var relativeY = (item.PhysicalTop - item.MonitorTop) / (double)oldHeight;
            desired.X = target.WorkingArea.Left + (int)Math.Round(relativeX * target.WorkingArea.Width);
            desired.Y = target.WorkingArea.Top + (int)Math.Round(relativeY * target.WorkingArea.Height);
        }
        SetScreenPixelBounds(ClampVisible(desired, target.WorkingArea));
    }

    private static System.Drawing.Rectangle ClampVisible(System.Drawing.Rectangle desired, System.Drawing.Rectangle workingArea)
    {
        var width = Math.Clamp(desired.Width, 80, Math.Max(80, workingArea.Width));
        var height = Math.Clamp(desired.Height, 60, Math.Max(60, workingArea.Height));
        var left = Math.Clamp(desired.Left, workingArea.Left - width + 48, workingArea.Right - 48);
        var top = Math.Clamp(desired.Top, workingArea.Top, workingArea.Bottom - 40);
        return new System.Drawing.Rectangle(left, top, width, height);
    }

    private void RestoreSelectableTextCache(PinSessionItem item)
    {
        if (item.OcrWords is not { Count: > 0 }) return;
        _recognizedTextSource = _source;
        _recognizedWords = item.OcrWords;
        _selectableTextResult = new RecognitionResult(
            item.OcrText,
            item.OcrBarcodeText,
            item.OcrBarcodeFormat,
            Words: _recognizedWords);
        _lastRecognitionFingerprint = $"{item.OcrText}\n{item.OcrBarcodeFormat}\n{item.OcrBarcodeText}";
        _textSelectable = item.TextSelectable;
    }

    private void RestoreSessionState(PinSessionItem item)
    {
        RestoreSelectableTextCache(item);
        _pinTitle = string.IsNullOrWhiteSpace(item.Title) ? "Pinned image" : item.Title.Trim();
        Title = $"SnapAnchor - {_pinTitle}";
        ShadeTitleText.Text = _pinTitle;
        _positionLocked = item.PositionLocked;
        _edgeSnapping = item.EdgeSnapping;
        if (item.WindowShaded) Dispatcher.BeginInvoke(() => SetShaded(true), DispatcherPriority.Loaded);
        UpdatePinStateBadge();
    }

    public void SetInitialBounds(Rect bounds)
    {
        StopZoomAnimation(false);
        Left = bounds.X; Top = bounds.Y;
        Width = Math.Max(80, bounds.Width); Height = Math.Max(60, bounds.Height);
        _normalHeight = Height;
        _targetZoomBounds = new Rect(Left, Top, Width, Height);
        ApplyImageScalingMode(animating: false);
    }

    public void SetScreenPixelBounds(System.Drawing.Rectangle bounds)
    {
        StopZoomAnimation(false);
        BeginScreenPixelBoundsStabilization(bounds);
    }

    private void ApplyInitialScreenPixelBounds()
    {
        if (_initialScreenPixelBounds is { } bounds) BeginScreenPixelBoundsStabilization(bounds);
    }

    private void BeginScreenPixelBoundsStabilization(System.Drawing.Rectangle bounds)
    {
        _initialScreenPixelBounds = bounds;
        _screenPixelBoundsPasses = 16;
        _screenPixelBoundsStablePasses = 0;
        ApplyScreenPixelBounds(bounds);
        _screenPixelBoundsTimer.Start();
    }

    private void StopScreenPixelBoundsStabilization()
    {
        _screenPixelBoundsTimer.Stop();
        _initialScreenPixelBounds = null;
    }

    private void ScreenPixelBoundsTimer_Tick(object? sender, EventArgs e)
    {
        if (_initialScreenPixelBounds is not { } bounds)
        {
            _screenPixelBoundsTimer.Stop();
            return;
        }

        ApplyScreenPixelBounds(bounds);
        var handle = new WindowInteropHelper(this).Handle;
        var stable = handle != IntPtr.Zero && NativeMethods.GetWindowRect(handle, out var actual) &&
            Math.Abs(actual.Left - bounds.Left) <= 1 && Math.Abs(actual.Top - bounds.Top) <= 1 &&
            Math.Abs(actual.Width - bounds.Width) <= 1 && Math.Abs(actual.Height - bounds.Height) <= 1;
        _screenPixelBoundsStablePasses = stable ? _screenPixelBoundsStablePasses + 1 : 0;
        _screenPixelBoundsPasses--;
        if (_screenPixelBoundsStablePasses < 5 && _screenPixelBoundsPasses > 0) return;

        _screenPixelBoundsTimer.Stop();
        _initialScreenPixelBounds = null;
        _normalHeight = Height;
        _targetZoomBounds = new Rect(Left, Top, Width, Height);
        UpdateLongImageWidth();
        ApplyImageScalingMode(animating: false);
    }

    private void ApplyScreenPixelBounds(System.Drawing.Rectangle bounds)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            bounds.Left,
            bounds.Top,
            Math.Max(1, bounds.Width),
            Math.Max(1, bounds.Height),
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        Dispatcher.BeginInvoke(() =>
        {
            _normalHeight = Height;
            _targetZoomBounds = new Rect(Left, Top, Width, Height);
            UpdateLongImageWidth();
        });
    }

    public void BeginEditMode()
    {
        if (_annotationOverlay is not null)
        {
            _annotationOverlay.Activate();
            return;
        }
        if (_inlineMode != "None") return;

        SetClickThrough(false);
        HideSelectableText();
        _inlineMode = "Edit";

        var editSource = _source;
        IReadOnlyList<AnnotationItem>? annotations = null;
        if (!string.IsNullOrWhiteSpace(_historyRecordId) && HistoryService.Find(_historyRecordId) is { } record && record.HasEditableAnnotations)
        {
            editSource = HistoryService.LoadBaseImage(record);
            annotations = HistoryService.LoadAnnotations(record);
        }

        // The original pin remains the only visible image. For an editable
        // document, show its base image here and render annotations in the
        // transparent overlay so existing marks are not drawn twice.
        PinnedImage.Source = editSource;
        LongPinnedImage.Source = editSource;

        var pinBounds = new Rect(Left, Top, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        var overlay = new PinnedAnnotationOverlayWindow(editSource, annotations, pinBounds)
        {
            Owner = this
        };
        _annotationOverlay = overlay;
        overlay.Applied += ApplyEditedImage;
        overlay.DocumentStored += PersistEditedDocument;
        overlay.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_annotationOverlay, overlay)) return;
            _annotationOverlay = null;
            _inlineMode = "None";
            PinnedImage.Source = _source;
            LongPinnedImage.Source = _source;
            ShowImageSurface();
            Focus();
        };
        overlay.Show();
    }

    internal Vector MoveFromAnnotationOverlay(Vector requestedDelta)
    {
        if (_positionLocked)
        {
            ShowPinStatus("Position locked");
            return default;
        }

        StopZoomAnimation(true);
        var before = new Point(Left, Top);
        Left += requestedDelta.X;
        Top += requestedDelta.Y;
        return new Vector(Left - before.X, Top - before.Y);
    }

    internal Vector CompleteAnnotationOverlayMove()
    {
        var before = new Point(Left, Top);
        if (!_positionLocked && _edgeSnapping) SnapToCurrentMonitorEdges();
        SaveSession();
        return new Vector(Left - before.X, Top - before.Y);
    }

    public void BeginCropMode()
    {
        if (_inlineMode != "None") return;
        BeginInlineMode("Crop", 56);
        InlineCrop.Visibility = Visibility.Visible;
        InlineCrop.LoadImage(_source);
        InlineCrop.Focus();
    }

    public void BeginRecognitionMode()
    {
        if (_inlineMode != "None") return;
        BeginInlineMode("Recognize", 176);
        InlineRecognition.Visibility = Visibility.Visible;
        InlineRecognition.LoadImage(_source);
        InlineRecognition.Focus();
    }

    private void BeginInlineMode(string mode, double toolbarHeight)
    {
        SetClickThrough(false);
        HideSelectableText();
        _inlineMode = mode;
        _normalHeight = Height;
        Height = Math.Min(SystemParameters.VirtualScreenHeight, Height + toolbarHeight);
        PinnedImage.Visibility = Visibility.Collapsed;
        LongImageViewer.Visibility = Visibility.Collapsed;
        ClickThroughBadge.Visibility = Visibility.Collapsed;
    }

    private void ApplyEditedImage(SnapAnchor.Controls.AnnotationAppliedEventArgs document)
    {
        SetBaseImage(document.FlattenedImage);
        PersistEditedDocument(document);
        ExitInlineMode();
    }

    private void PersistEditedDocument(SnapAnchor.Controls.AnnotationAppliedEventArgs document)
    {
        if (string.IsNullOrWhiteSpace(_historyRecordId))
            _historyRecordId = HistoryService.Add(document.BaseImage, sourceKind: "Edited").Id;
        HistoryService.SaveAnnotationDocument(_historyRecordId, document.BaseImage, document.FlattenedImage, document.Items);
        HistoryService.UpdateSourceKind(_historyRecordId, "Edited");
    }

    private void ApplyCroppedImage(BitmapSource image)
    {
        var displayWidth = Width;
        _normalHeight = Math.Max(60, displayWidth * image.PixelHeight / Math.Max(1, image.PixelWidth));
        SetBaseImage(image);
        _historyRecordId = HistoryService.Add(image, sourceKind: "Edited").Id;
        ExitInlineMode();
    }

    private void Recognition_Completed(object? sender, SnapAnchor.Controls.RecognitionCompletedEventArgs e)
    {
        PersistRecognitionResult(e.Result);
    }

    private void PersistRecognitionResult(RecognitionResult result)
    {
        if (!result.HasContent) return;
        var fingerprint = $"{result.Text}\n{result.BarcodeFormat}\n{result.BarcodeText}";
        if (fingerprint == _lastRecognitionFingerprint) return;
        _lastRecognitionFingerprint = fingerprint;
        if (string.IsNullOrWhiteSpace(_historyRecordId))
            _historyRecordId = HistoryService.SaveRecognition(_source, result.Text, result.BarcodeText, result.BarcodeFormat).Id;
        else
            HistoryService.UpdateRecognition(_historyRecordId, result.Text, result.BarcodeText, result.BarcodeFormat);
    }

    private void SetBaseImage(BitmapSource image)
    {
        InvalidateSelectableText();
        _baseSource = image;
        _grayscale = _inverted = false;
        ApplyFilters();
    }

    private void ExitInlineMode()
    {
        if (_inlineMode == "None") return;
        if (_inlineMode == "Recognize") InlineRecognition.CancelCurrent();
        InlineEditor.Visibility = Visibility.Collapsed;
        InlineCrop.Visibility = Visibility.Collapsed;
        InlineRecognition.Visibility = Visibility.Collapsed;
        ShowImageSurface();
        Height = Math.Max(60, _normalHeight);
        _inlineMode = "None";
        Focus();
    }

    private void SizeToImage(BitmapSource source)
    {
        var maxWidth = Math.Min(SystemParameters.WorkArea.Width * 0.68, _settings.PinMaxWindowSize);
        var maxHeight = Math.Min(SystemParameters.WorkArea.Height * 0.68, _settings.PinMaxWindowSize);
        var width = source.Width > 0 ? source.Width : source.PixelWidth;
        var height = source.Height > 0 ? source.Height : source.PixelHeight;
        if (_longScrollable)
        {
            Width = Math.Clamp(width, 320, maxWidth);
            Height = Math.Clamp(SystemParameters.WorkArea.Height * 0.72, 280, Math.Min(820, _settings.PinMaxWindowSize));
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
            _normalHeight = Height;
            UpdateLongImageWidth();
            return;
        }
        var scale = Math.Min(1, Math.Min(maxWidth / Math.Max(1, width), maxHeight / Math.Max(1, height)));
        Width = Math.Max(120, width * scale); Height = Math.Max(80, height * scale);
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
        _normalHeight = Height;
    }

    private void BeginTitleEdit()
    {
        SetClickThrough(false);
        TitleEditorBox.Text = _pinTitle;
        TitleEditor.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            TitleEditorBox.Focus();
            TitleEditorBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void TitleEditorBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitTitleEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            TitleEditor.Visibility = Visibility.Collapsed;
            Focus();
            e.Handled = true;
        }
    }

    private void TitleEditorBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TitleEditor.Visibility == Visibility.Visible) CommitTitleEdit();
    }

    private void CommitTitleEdit()
    {
        _pinTitle = string.IsNullOrWhiteSpace(TitleEditorBox.Text) ? "Pinned image" : TitleEditorBox.Text.Trim();
        Title = $"SnapAnchor - {_pinTitle}";
        ShadeTitleText.Text = _pinTitle;
        TitleEditor.Visibility = Visibility.Collapsed;
        UpdatePinStateBadge();
        SaveSession();
        Focus();
    }

    private void SetShaded(bool shaded)
    {
        if (_shaded == shaded) return;
        if (shaded)
        {
            _preShadeHeight = Math.Max(60, Height);
            _shaded = true;
            StopZoomAnimation(false);
            Height = 30;
        }
        else
        {
            _shaded = false;
            Height = Math.Max(60, _preShadeHeight);
        }
        ShowImageSurface();
        UpdatePinStateBadge();
        SaveSession();
    }

    private void UpdatePinStateBadge()
    {
        ShadeTitleText.Text = _pinTitle;
        var states = new List<string>();
        if (_positionLocked) states.Add("Locked");
        if (_textSelectable) states.Add("OCR text");
        PinStateText.Text = string.Join(" | ", states);
        PinStateBadge.Visibility = !_shaded && states.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClickThroughBadge.Visibility = !_shaded && _clickThrough ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReplaceFromClipboard()
    {
        if (CaptureService.GetClipboardVisual() is not { } image) return;
        _sourceFilePath = string.Empty;
        SetBaseImage(image);
    }

    private void ReplaceByFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        SetBaseImage(CaptureService.LoadImage(dialog.FileName));
        _sourceFilePath = dialog.FileName;
    }

    private IEnumerable<PinnedImageWindow> Targets() => SelectedPins.Contains(this) ? SelectedPins : [this];

    private void CopyTargetsAsFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SnapAnchor", "ClipboardFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var files = ExportTargets(directory, Targets().ToList(), forcePng: true);
        var dropList = new System.Collections.Specialized.StringCollection();
        dropList.AddRange(files.ToArray());
        Clipboard.SetFileDropList(dropList);
    }

    private void ExportSelectedImages()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            UseDescriptionForTitle = true,
            Description = "Choose a folder for the selected SnapAnchor images"
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        ExportTargets(dialog.SelectedPath, Targets().ToList(), forcePng: false);
        System.Media.SystemSounds.Asterisk.Play();
    }

    private static IReadOnlyList<string> ExportTargets(string directory, IReadOnlyList<PinnedImageWindow> pins, bool forcePng)
    {
        Directory.CreateDirectory(directory);
        var settings = SettingsService.Load();
        var extension = forcePng ? ".png" : CaptureService.ExtensionForFormat(settings.OutputFormat);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
        var paths = new List<string>(pins.Count);
        for (var index = 0; index < pins.Count; index++)
        {
            var path = Path.Combine(directory, $"SnapAnchor_{stamp}_{index + 1:D2}{extension}");
            CaptureService.SaveImage(pins[index]._source, path, settings.ImageQuality, settings);
            paths.Add(path);
        }
        return paths;
    }

    internal bool CanRefreshScreenshot => RefreshRecord() is not null;

    private CaptureRecord? RefreshRecord() =>
        string.IsNullOrWhiteSpace(_historyRecordId)
            ? null
            : HistoryService.Find(_historyRecordId) is { SourceRegion: not null } record ? record : null;

    private void SetAutoRefresh(TimeSpan? interval)
    {
        _refreshTimer.Stop();
        if (interval is null) return;
        _refreshTimer.Interval = interval.Value;
        _refreshTimer.Start();
        RefreshScreenshot();
    }

    private void RefreshScreenshot(bool silent = false)
    {
        try
        {
            if (RefreshRecord() is not { SourceRegion: { } region } record) return;
            var context = CaptureService.CaptureVirtualScreen();
            var freshBase = CaptureService.Crop(context, new Int32Rect(region.X, region.Y, region.Width, region.Height));
            var annotations = HistoryService.LoadAnnotations(record);
            BitmapSource refreshed = freshBase;
            if (annotations.Count > 0)
            {
                InlineEditor.LoadImage(freshBase, annotations);
                refreshed = InlineEditor.Flatten();
            }
            SetBaseImage(refreshed);
            HistoryService.SaveAnnotationDocument(record.Id, freshBase, refreshed, annotations);
            HistoryService.UpdateSourceKind(record.Id, "Refreshed");
            if (!silent) System.Media.SystemSounds.Asterisk.Play();
        }
        catch (Exception ex)
        {
            _refreshTimer.Stop();
            if (!silent)
                MessageBox.Show(ex.Message, L("Refresh screenshot"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SetSelected(bool selected)
    {
        if (selected) SelectedPins.Add(this); else SelectedPins.Remove(this);
        PinOutline.BorderBrush = selected ? Brushes.DeepSkyBlue : new SolidColorBrush(Color.FromArgb(68, 0, 0, 0));
        PinOutline.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
    }

    private static void ClearSelection()
    {
        foreach (var pin in SelectedPins.ToArray()) pin.SetSelected(false);
    }

    private void MoveTargetsToGroup(string group)
    {
        foreach (var pin in Targets())
        {
            pin._groupName = group;
            pin.ApplyVirtualDesktopBinding();
        }
        if (!group.Equals(SettingsService.Load().CurrentPinGroup, StringComparison.OrdinalIgnoreCase))
            foreach (var pin in Targets().ToArray()) pin.Hide();
        ClearSelection();
        SaveSession();
        NotifyPinsChanged();
    }

    private void ApplyCaptureAffinityToWindow() => ApplyCaptureAffinityToWindow(SettingsService.Load().ExcludeSnapAnchorFromCapture);

    private void ApplyCaptureAffinityToWindow(bool exclude)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            NativeMethods.SetWindowDisplayAffinity(handle, exclude ? NativeMethods.WdaExcludeFromCapture : NativeMethods.WdaNone);
    }

    private void ApplyVirtualDesktopBinding()
    {
        var settings = SettingsService.Load();
        if (!settings.PinGroupsFollowVirtualDesktops) return;
        if (VirtualDesktopService.BoundDesktop(settings, _groupName) is { } desktopId) MoveToDesktop(desktopId);
    }

    private void MoveToDesktop(Guid desktopId)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) VirtualDesktopService.MoveWindowToDesktop(handle, desktopId);
    }

    private static void NotifyPinsChanged() => PinsChanged?.Invoke(null, EventArgs.Empty);

    private void ToggleThumbnail()
    {
        if (!_thumbnail)
        {
            _preThumbnailBounds = new Rect(Left, Top, Width, Height);
            Width = Height = _settings.FastThumbnailSize;
            LongImageViewer.Visibility = Visibility.Collapsed;
            PinnedImage.Visibility = Visibility.Visible;
            PinnedImage.Stretch = Stretch.UniformToFill;
            _thumbnail = true;
        }
        else
        {
            SetInitialBounds(_preThumbnailBounds);
            PinnedImage.Stretch = Stretch.Fill;
            _thumbnail = false;
            ShowImageSurface();
        }
    }

    internal void SetThumbnailMode(bool enabled)
    {
        if (_thumbnail != enabled) ToggleThumbnail();
    }

    private void SetZoom(double scale)
    {
        ResizeToPhysicalPercent((int)Math.Round(scale * 100));
    }

    private void ApplyFilters()
    {
        InvalidateSelectableText();
        _source = ImageTransformService.Apply(_baseSource, _grayscale, _inverted);
        PinnedImage.Source = _source;
        LongPinnedImage.Source = _source;
    }

    private void ApplyBackground()
    {
        Frame.Background = _backgroundMode switch
        {
            "DarkChecker" => Checkerboard(Color.FromRgb(55, 65, 81), Color.FromRgb(31, 41, 55)),
            "LightChecker" => Checkerboard(Color.FromRgb(255, 255, 255), Color.FromRgb(226, 232, 240)),
            _ => Brushes.Transparent
        };
    }

    private static Brush Checkerboard(Color first, Color second)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(first), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(second), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(second), null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        return new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
    }

    private static MenuItem Item(string header, RoutedEventHandler action)
    {
        var item = new MenuItem { Header = L(header) };
        item.Click += action;
        return item;
    }

    private static string L(string value) => LocalizationService.Current(value);

    private static MenuItem CheckItem(string header, bool isChecked, RoutedEventHandler action)
    {
        var item = Item(header, action);
        item.IsCheckable = true; item.IsChecked = isChecked;
        return item;
    }

    private void SaveImage(object sender, RoutedEventArgs e)
        => SaveBitmap(_source, applyOutputEffects: true);

    private void SaveBitmap(BitmapSource source, bool applyOutputEffects)
    {
        var folder = Directory.Exists(_settings.QuickSaveFolder) ? _settings.QuickSaveFolder : Path.GetTempPath();
        var suggested = SettingsService.CreateOutputPath(folder, _settings.OutputFileName);
        var dialog = new SaveFileDialog
        {
            Filter = CaptureService.ImageSaveFilter,
            DefaultExt = CaptureService.ExtensionForFormat(_settings.OutputFormat),
            FilterIndex = CaptureService.FilterIndexForFormat(_settings.OutputFormat),
            AddExtension = true,
            InitialDirectory = Path.GetDirectoryName(suggested) ?? folder,
            FileName = Path.GetFileName(suggested)
        };
        if (dialog.ShowDialog(this) == true)
            CaptureService.SaveImage(source, dialog.FileName, _settings.ImageQuality, applyOutputEffects ? _settings : null);
    }

    private void PrintImage()
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        var availableWidth = Math.Max(1, dialog.PrintableAreaWidth);
        var availableHeight = Math.Max(1, dialog.PrintableAreaHeight);
        var scale = Math.Min(availableWidth / Math.Max(1, _source.Width), availableHeight / Math.Max(1, _source.Height));
        var image = new Image
        {
            Source = _source,
            Width = Math.Max(1, _source.Width * Math.Min(1, scale)),
            Height = Math.Max(1, _source.Height * Math.Min(1, scale)),
            Stretch = Stretch.Uniform
        };
        image.Measure(new Size(image.Width, image.Height));
        image.Arrange(new Rect(0, 0, image.Width, image.Height));
        dialog.PrintVisual(image, "SnapAnchor image");
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (TitleEditor.Visibility == Visibility.Visible) return;
        if (_inlineMode != "None")
        {
            if (e.Key == Key.Escape) ExitInlineMode();
            return;
        }
        if (_textSelectable && e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
            !string.IsNullOrWhiteSpace(SelectedRecognizedText()))
        {
            CopySelectedText();
            e.Handled = true;
            return;
        }
        if (_textSelectable && e.Key == Key.Escape &&
            (_textSelectionAnchor is not null || _textSelectionEnd is not null))
        {
            ClearRecognizedTextSelection();
            e.Handled = true;
            return;
        }
        if (AccessibilityService.TryHandleToolbarNavigation(e, PinToolbarHosts))
            return;

        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.D1: _rotation -= 90; ApplyTransform(); break;
            case Key.D2: _rotation += 90; ApplyTransform(); break;
            case Key.D3: _flipX *= -1; ApplyTransform(); break;
            case Key.D4: _flipY *= -1; ApplyTransform(); break;
            case Key.D5: _grayscale = !_grayscale; ApplyFilters(); break;
            case Key.Space: Topmost = !Topmost; break;
            case Key.Delete: foreach (var pin in Targets().ToArray()) pin.Close(); break;
            case Key.E: BeginEditMode(); break;
            case Key.C: BeginCropMode(); break;
            case Key.R: BeginRecognitionMode(); break;
            case Key.F5: RefreshScreenshot(); break;
            case Key.F2: BeginTitleEdit(); break;
            case Key.L:
                _positionLocked = !_positionLocked;
                UpdatePinStateBadge();
                SaveSession();
                break;
            case Key.Y: SetShaded(!_shaded); break;
            case Key.P when (Keyboard.Modifiers & ModifierKeys.Control) != 0: PrintImage(); break;
        }
    }

    private void SetClickThrough(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        if (enabled) style |= NativeMethods.WsExTransparent | NativeMethods.WsExLayered; else style &= ~NativeMethods.WsExTransparent;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(style));
        _clickThrough = enabled;
        UpdatePinStateBadge();
    }

    private IEnumerable<DependencyObject> PinToolbarHosts()
    {
        if (InlineEditor.Visibility == Visibility.Visible)
            yield return InlineEditor;
        if (TextSelectionToolbar.Visibility == Visibility.Visible)
            yield return TextSelectionToolbar;
    }
}
