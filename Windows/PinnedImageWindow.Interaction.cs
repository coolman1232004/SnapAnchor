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
namespace SnapAnchor.Windows;

public partial class PinnedImageWindow
{
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_inlineMode != "None" || TitleEditor.Visibility == Visibility.Visible) return;
        StopScreenPixelBoundsStabilization();
        StopZoomAnimation(true);
        Focus();
        if (e.ClickCount == 2) { Close(); return; }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SetSelected(!SelectedPins.Contains(this));
            e.Handled = true;
            return;
        }

        if (!SelectedPins.Contains(this))
        {
            ClearSelection();
            SetSelected(true);
        }

        if (_positionLocked)
        {
            ShowPinStatus("Position locked");
            e.Handled = true;
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed) return;
        var targets = SelectedPins.Contains(this) ? SelectedPins.ToArray() : [this];
        var positions = targets.ToDictionary(pin => pin, pin => new Point(pin.Left, pin.Top));
        var start = new Point(Left, Top);
        DragMove();
        if (_edgeSnapping) SnapToCurrentMonitorEdges();
        var dx = Left - start.X; var dy = Top - start.Y;
        foreach (var pin in targets.Where(pin => pin != this))
        {
            pin.Left = positions[pin].X + dx;
            pin.Top = positions[pin].Y + dy;
        }
        SaveSession();
    }

    private void SnapToCurrentMonitorEdges()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out var rect)) return;
        var working = DisplayTopologyService.WorkingAreaForWindowPixels(handle);
        const int distance = 14;
        var x = rect.Left;
        var y = rect.Top;
        if (Math.Abs(rect.Left - working.Left) <= distance) x = working.Left;
        if (Math.Abs(rect.Top - working.Top) <= distance) y = working.Top;
        if (Math.Abs(rect.Right - working.Right) <= distance) x = working.Right - rect.Width;
        if (Math.Abs(rect.Bottom - working.Bottom) <= distance) y = working.Bottom - rect.Height;
        if (x != rect.Left || y != rect.Top)
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, x, y, rect.Width, rect.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_inlineMode != "None") return;
        HandleWheelZoom(e.Delta, Keyboard.Modifiers);
        e.Handled = true;
    }

    /// <summary>
    /// The pin annotation surface is a separate transparent top-level window,
    /// so wheel messages over the visible pin belong to that surface while the
    /// toolbar is open. Keep pin zoom/opacity available by forwarding those
    /// messages through this single implementation.
    /// </summary>
    internal void HandleAnnotationOverlayWheel(int delta, ModifierKeys modifiers)
        => HandleWheelZoom(delta, modifiers);

    private void HandleWheelZoom(int delta, ModifierKeys modifiers)
    {
        StopScreenPixelBoundsStabilization();
        var targets = SelectedPins.Contains(this) && SelectedPins.Count > 1 ? SelectedPins.ToArray() : [this];
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            foreach (var pin in targets) pin.Opacity = Math.Clamp(pin.Opacity + (delta > 0 ? 0.08 : -0.08), 0.15, 1);
            return;
        }
        foreach (var pin in targets) pin.ResizeByWheelStep(delta);
    }

    private void ResizeByWheelStep(int wheelDelta)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out var current)) return;
        var percent = PinScalingQuality.NextWheelZoomPercent(current.Width, _source.PixelWidth, wheelDelta);
        ResizeToPhysicalPercent(percent, current);
    }

    private void ResizeToPhysicalPercent(int percent, NativeMethods.NativeRect? knownBounds = null)
    {
        StopScreenPixelBoundsStabilization();
        StopZoomAnimation(false);

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        if (knownBounds is not { } current && !NativeMethods.GetWindowRect(handle, out current)) return;

        percent = Math.Clamp(percent, PinScalingQuality.MinimumZoomPercent, PinScalingQuality.MaximumZoomPercent);
        var target = PinScalingQuality.PhysicalSizeForPercent(_source.PixelWidth, _source.PixelHeight, percent);

        // Match Snow Shot's physical-pixel sizing. Anchor at the mouse when it
        // is over the image; otherwise preserve the window centre (for wheel
        // messages forwarded by an annotation toolbar outside the image).
        var relativeX = 0.5;
        var relativeY = 0.5;
        var anchorX = current.Left + current.Width / 2d;
        var anchorY = current.Top + current.Height / 2d;
        var cursor = new NativeMethods.CursorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.CursorInfo>() };
        if (NativeMethods.GetCursorInfo(ref cursor) &&
            cursor.ScreenPosition.X >= current.Left && cursor.ScreenPosition.X <= current.Right &&
            cursor.ScreenPosition.Y >= current.Top && cursor.ScreenPosition.Y <= current.Bottom)
        {
            anchorX = cursor.ScreenPosition.X;
            anchorY = cursor.ScreenPosition.Y;
            relativeX = (anchorX - current.Left) / Math.Max(1d, current.Width);
            relativeY = (anchorY - current.Top) / Math.Max(1d, current.Height);
        }

        var left = (int)Math.Round(anchorX - target.Width * relativeX);
        var top = (int)Math.Round(anchorY - target.Height * relativeY);
        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            left,
            top,
            target.Width,
            target.Height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);

        ApplyImageScalingMode(animating: false);
        ShowZoomPercent(percent);
        Dispatcher.BeginInvoke(() =>
        {
            _normalHeight = Height;
            _targetZoomBounds = new Rect(Left, Top, Width, Height);
            UpdateLongImageWidth();
            ApplyImageScalingMode(animating: false);
        }, DispatcherPriority.Render);
    }

    private void StopZoomAnimation(bool commitTarget)
    {
        // Kept as the common pre-move/pre-mode-change synchronization point.
        // Zoom is now committed atomically in physical pixels rather than
        // passing through soft, fractional animation frames.
        ApplyImageScalingMode(animating: false);
        _targetZoomBounds = new Rect(Left, Top, Width, Height);
    }

    private void ApplyImageScalingMode(bool animating)
    {
        var dpi = DpiLayoutService.WindowScale(this);
        var normalWidth = ActualWidth > 1 ? ActualWidth : Width;
        var longWidth = LongPinnedImage.Width > 1 ? LongPinnedImage.Width : normalWidth;
        RenderOptions.SetBitmapScalingMode(PinnedImage,
            PinScalingQuality.ModeFor(normalWidth, dpi.DpiScaleX, _source.PixelWidth, animating));
        RenderOptions.SetBitmapScalingMode(LongPinnedImage,
            PinScalingQuality.ModeFor(longWidth, dpi.DpiScaleX, _source.PixelWidth, animating));
    }

    private void ApplyShadow()
    {
        // Effects on Frame rasterize and soften the screenshot itself. Keep
        // the image layer untouched and apply decoration only to the outline.
        Frame.Effect = null;
        PinOutline.Effect = _shadowEnabled
            ? new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.32 }
            : null;
    }

    private void ShowZoomPercent(double width)
    {
        var dpi = DpiLayoutService.WindowScale(this);
        ShowZoomPercent((int)DpiLayoutService.PhysicalZoomPercent(width, dpi.DpiScaleX, _source.PixelWidth));
    }

    private void ShowZoomPercent(int percent)
    {
        ZoomText.Text = $"{percent}%";
        ZoomBadge.Visibility = Visibility.Visible;
        _zoomBadgeTimer.Stop();
        _zoomBadgeTimer.Start();
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // When the annotation toolbar overlay is open it owns pointer input over
        // the pin; that path calls ShowPinContextMenu() directly instead.
        if (_inlineMode != "None") return;
        ShowPinContextMenu();
        e.Handled = true;
    }

    /// <summary>
    /// Pin context menu shared by the bare pin window and the annotation
    /// toolbar overlay so "Show toolbar" mode still offers the same actions.
    /// </summary>
    internal void ShowPinContextMenu()
    {
        if (!SelectedPins.Contains(this)) { ClearSelection(); SetSelected(true); }
        var menu = new ContextMenu();

        var copySelectedText = Item("Copy selected text", (_, _) => CopySelectedText());
        copySelectedText.IsEnabled = !string.IsNullOrWhiteSpace(SelectedRecognizedText());
        menu.Items.Add(copySelectedText);
        menu.Items.Add(CheckItem("Text selectable (OCR)", _textSelectable,
            async (_, _) => await SetTextSelectablePreferenceAsync(!_textSelectable)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy image", (_, _) => Clipboard.SetImage(_source)));
        menu.Items.Add(Item("Copy unscaled image", (_, _) => Clipboard.SetImage(_baseSource)));
        menu.Items.Add(Item("Save image as…", (_, _) => SaveBitmap(_source, applyOutputEffects: true)));
        menu.Items.Add(Item("Save unscaled image as…", (_, _) => SaveBitmap(_baseSource, applyOutputEffects: false)));
        menu.Items.Add(new Separator());
        // When already editing, offer hide toolbar instead of opening another overlay.
        if (_inlineMode == "Edit")
            menu.Items.Add(Item("Hide toolbar", (_, _) => ExitInlineMode()));
        else
            menu.Items.Add(Item("Show toolbar", (_, _) => BeginEditMode()));
        menu.Items.Add(Item("Set title...    F2", (_, _) => BeginTitleEdit()));
        menu.Items.Add(CheckItem("Lock position    L", _positionLocked, (_, _) =>
        {
            _positionLocked = !_positionLocked;
            UpdatePinStateBadge();
            SaveSession();
        }));
        menu.Items.Add(CheckItem("Window shade    Y", _shaded, (_, _) => SetShaded(!_shaded)));
        menu.Items.Add(CheckItem("Snap to screen edges", _edgeSnapping, (_, _) =>
        {
            _edgeSnapping = !_edgeSnapping;
            UpdatePinStateBadge();
            SaveSession();
        }));
        menu.Items.Add(CheckItem("Shadow", _shadowEnabled, (_, _) => { _shadowEnabled = !_shadowEnabled; ApplyShadow(); }));

        var zoom = new MenuItem { Header = L("Zoom") };
        foreach (var percent in new[] { 25, 33, 50, 67, 75, 100, 125, 150, 200 })
            zoom.Items.Add(Item($"{percent}%", (_, _) => SetZoom(percent / 100.0)));
        menu.Items.Add(zoom);

        var processing = new MenuItem { Header = L("Image processing") };
        processing.Items.Add(Item("Rotate left", (_, _) => { _rotation -= 90; ApplyTransform(); }));
        processing.Items.Add(Item("Rotate right", (_, _) => { _rotation += 90; ApplyTransform(); }));
        processing.Items.Add(Item("Flip horizontally", (_, _) => { _flipX *= -1; ApplyTransform(); }));
        processing.Items.Add(Item("Flip vertically", (_, _) => { _flipY *= -1; ApplyTransform(); }));
        processing.Items.Add(new Separator());
        processing.Items.Add(CheckItem("Grayscale", _grayscale, (_, _) => { _grayscale = !_grayscale; ApplyFilters(); }));
        processing.Items.Add(CheckItem("Invert colors", _inverted, (_, _) => { _inverted = !_inverted; ApplyFilters(); }));
        processing.Items.Add(Item("Crop…", (_, _) => BeginCropMode()));
        menu.Items.Add(processing);

        menu.Items.Add(Item("Paste", (_, _) => ReplaceFromClipboard()));
        menu.Items.Add(Item("Replace by file…", (_, _) => ReplaceByFile()));

        var groups = new MenuItem { Header = L("Move to group") };
        foreach (var group in SettingsService.Load().PinGroups)
            groups.Items.Add(CheckItem(group, _groupName.Equals(group, StringComparison.OrdinalIgnoreCase), (_, _) => MoveTargetsToGroup(group)));
        menu.Items.Add(groups);
        if (!string.IsNullOrWhiteSpace(_sourceFilePath))
            menu.Items.Add(Item("View in folder", (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_sourceFilePath}\"") { UseShellExecute = true })));

        var tools = new MenuItem { Header = L("More tools") };
        tools.Items.Add(Item("Recognize text / QR…", (_, _) => BeginRecognitionMode()));
        tools.Items.Add(CheckItem("Fixed thumbnail", _thumbnail, (_, _) => ToggleThumbnail()));
        tools.Items.Add(CheckItem("Always on top", Topmost, (_, _) => { foreach (var pin in Targets()) pin.Topmost = !Topmost; }));
        tools.Items.Add(Item(_clickThrough ? "Make interactive" : "Enable click-through", (_, _) => SetClickThrough(!_clickThrough)));
        tools.Items.Add(CheckItem("Solo selected pins", _soloMode, (_, _) => ToggleSolo()));
        tools.Items.Add(Item("Copy image as file", (_, _) => CopyTargetsAsFiles()));
        if (Targets().Skip(1).Any()) tools.Items.Add(Item("Export selected images…", (_, _) => ExportSelectedImages()));
        tools.Items.Add(Item("Print…    Ctrl+P", (_, _) => PrintImage()));
        if (CanRefreshScreenshot)
        {
            tools.Items.Add(Item("Refresh screenshot    F5", (_, _) => RefreshScreenshot()));
            var automaticRefresh = new MenuItem { Header = L("Auto refresh") };
            automaticRefresh.Items.Add(CheckItem("Off", !_refreshTimer.IsEnabled, (_, _) => SetAutoRefresh(null)));
            foreach (var seconds in new[] { 1, 2, 5 })
                automaticRefresh.Items.Add(CheckItem(
                    LocalizationService.Format(seconds == 1 ? "Every {0} second" : "Every {0} seconds", seconds),
                    _refreshTimer.IsEnabled && Math.Abs(_refreshTimer.Interval.TotalSeconds - seconds) < 0.1,
                    (_, _) => SetAutoRefresh(TimeSpan.FromSeconds(seconds))));
            tools.Items.Add(automaticRefresh);
        }
        var backgrounds = new MenuItem { Header = L("Transparent background") };
        foreach (var mode in new[] { "Transparent", "Pseudo", "DarkChecker", "LightChecker" })
        {
            var label = mode switch { "Pseudo" => "Pseudo-transparent", "DarkChecker" => "Dark checkerboard", "LightChecker" => "Light checkerboard", _ => "Transparent" };
            backgrounds.Items.Add(CheckItem(label, _backgroundMode == mode, (_, _) => { _backgroundMode = mode; ApplyBackground(); }));
        }
        tools.Items.Add(backgrounds);
        tools.Items.Add(Item("Reset selected", (_, _) => { foreach (var pin in Targets()) pin.ResetAppearance(); }));
        menu.Items.Add(tools);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item(SelectedPins.Count > 1 ? "Close selected" : "Close", (_, _) => { foreach (var pin in Targets().ToArray()) pin.Close(); }));
        menu.Items.Add(Item("Destroy", (_, _) => { foreach (var pin in Targets().ToArray()) pin.Close(); }));
        var dimensions = new MenuItem { Header = $"{Math.Round(Width)} × {Math.Round(_inlineMode == "None" ? Height : _normalHeight)}" };
        foreach (var percent in new[] { 50, 100, 150, 200 })
            dimensions.Items.Add(Item(LocalizationService.Format("Zoom to {0}%", percent), (_, _) => SetZoom(percent / 100.0)));
        menu.Items.Add(dimensions);
        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

}
