using SnapAnchor.Controls;
using SnapAnchor.Models;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapAnchor.Windows;

/// <summary>
/// A transparent, virtual-desktop overlay that keeps the original pin visible
/// while hosting the same annotation toolbar used by region capture.
/// </summary>
internal sealed class PinnedAnnotationOverlayWindow : Window
{
    private readonly AnnotationEditorControl _editor;

    internal event Action<AnnotationAppliedEventArgs>? Applied;
    internal event Action<AnnotationAppliedEventArgs>? DocumentStored;

    internal PinnedAnnotationOverlayWindow(
        BitmapSource source,
        IEnumerable<AnnotationItem>? items,
        Rect pinScreenBounds)
    {
        Title = "SnapAnchor Pin toolbar";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _editor = new AnnotationEditorControl();
        Content = _editor;
        _editor.LoadImage(source, items);
        _editor.SetExternalBackgroundMode(true);

        var relativePinBounds = new Rect(
            pinScreenBounds.Left - Left,
            pinScreenBounds.Top - Top,
            pinScreenBounds.Width,
            pinScreenBounds.Height);
        _editor.ConfigureCaptureOverlay(
            relativePinBounds,
            new Size(Width, Height),
            relativePinBounds,
            showActions: true,
            showPrimaryToolbar: true,
            showCancelAction: true);

        _editor.Applied += document =>
        {
            Applied?.Invoke(document);
            Close();
        };
        _editor.DocumentStored += document => DocumentStored?.Invoke(document);
        _editor.Cancelled += (_, _) => Close();
        Loaded += (_, _) =>
        {
            Activate();
            _editor.Focus();
        };
    }
}
