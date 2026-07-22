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
        StopScreenPixelBoundsStabilization();
        var targets = SelectedPins.Contains(this) && SelectedPins.Count > 1 ? SelectedPins.ToArray() : [this];
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            foreach (var pin in targets) pin.Opacity = Math.Clamp(pin.Opacity + (e.Delta > 0 ? 0.08 : -0.08), 0.15, 1);
            e.Handled = true;
            return;
        }
        var factor = Math.Pow(1.045, e.Delta / 120.0);
        foreach (var pin in targets) pin.QueueSmoothResize(factor);
        e.Handled = true;
    }

    private void QueueSmoothResize(double factor)
    {
        var basis = _zoomAnimating ? _targetZoomBounds : new Rect(Left, Top, Width, Height);
        QueueSmoothResizeTo(
            Math.Clamp(basis.Width * factor, 60, SystemParameters.VirtualScreenWidth * 1.5),
            Math.Clamp(basis.Height * factor, 40, SystemParameters.VirtualScreenHeight * 1.5),
            basis.X + basis.Width / 2,
            basis.Y + basis.Height / 2);
    }

    private void QueueSmoothResizeTo(double width, double height, double centerX, double centerY)
    {
        _targetZoomBounds = new Rect(centerX - width / 2, centerY - height / 2, width, height);
        if (!_longScrollable) PinnedImage.CacheMode = _zoomBitmapCache;
        RenderOptions.SetBitmapScalingMode(PinnedImage, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(LongPinnedImage, BitmapScalingMode.NearestNeighbor);
        _zoomAnimating = true;
        _zoomAnimationTimer.Start();
        ShowZoomPercent(width);
    }

    private void ZoomAnimation_Tick(object? sender, EventArgs e)
    {
        const double blend = 0.42;
        Left += (_targetZoomBounds.Left - Left) * blend;
        Top += (_targetZoomBounds.Top - Top) * blend;
        Width += (_targetZoomBounds.Width - Width) * blend;
        Height += (_targetZoomBounds.Height - Height) * blend;
        _normalHeight = Height;
        if (Math.Abs(Left - _targetZoomBounds.Left) < 0.25 &&
            Math.Abs(Top - _targetZoomBounds.Top) < 0.25 &&
            Math.Abs(Width - _targetZoomBounds.Width) < 0.25 &&
            Math.Abs(Height - _targetZoomBounds.Height) < 0.25)
            StopZoomAnimation(true);
    }

    private void StopZoomAnimation(bool commitTarget)
    {
        if (commitTarget && _zoomAnimating && !_targetZoomBounds.IsEmpty)
        {
            Left = _targetZoomBounds.Left;
            Top = _targetZoomBounds.Top;
            Width = _targetZoomBounds.Width;
            Height = _targetZoomBounds.Height;
            _normalHeight = Height;
        }
        _zoomAnimating = false;
        _zoomAnimationTimer.Stop();
        RenderOptions.SetBitmapScalingMode(PinnedImage, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(LongPinnedImage, BitmapScalingMode.NearestNeighbor);
        PinnedImage.CacheMode = null;
        _targetZoomBounds = new Rect(Left, Top, Width, Height);
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
        ZoomText.Text = $"{DpiLayoutService.PhysicalZoomPercent(width, dpi.DpiScaleX, _source.PixelWidth)}%";
        ZoomBadge.Visibility = Visibility.Visible;
        _zoomBadgeTimer.Stop();
        _zoomBadgeTimer.Start();
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_inlineMode != "None") return;
        if (!SelectedPins.Contains(this)) { ClearSelection(); SetSelected(true); }
        var menu = new ContextMenu();

        var copySelectedText = Item("Copy selected text", (_, _) => CopySelectedText());
        copySelectedText.IsEnabled = !string.IsNullOrWhiteSpace(SelectedRecognizedText());
        menu.Items.Add(copySelectedText);
        menu.Items.Add(CheckItem("Text selectable (OCR)", _textSelectable, async (_, _) => await SetTextSelectableAsync(!_textSelectable)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy image", (_, _) => Clipboard.SetImage(_source)));
        menu.Items.Add(Item("Copy unscaled image", (_, _) => Clipboard.SetImage(_baseSource)));
        menu.Items.Add(Item("Save image as…", (_, _) => SaveBitmap(_source, applyOutputEffects: true)));
        menu.Items.Add(Item("Save unscaled image as…", (_, _) => SaveBitmap(_baseSource, applyOutputEffects: false)));
        menu.Items.Add(new Separator());
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
        menu.IsOpen = true;
        e.Handled = true;
    }

}
