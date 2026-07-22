using SnapAnchor.Controls;
using SnapAnchor.Services;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace SnapAnchor.Windows;

public partial class WhiteboardWindow : Window
{
    private readonly bool _transparent;

    public WhiteboardWindow(bool transparent)
    {
        InitializeComponent();
        _transparent = transparent;
        Title = transparent ? "SnapAnchor Transparent Whiteboard" : "SnapAnchor Whiteboard";
        var bounds = DisplayTopologyService.VirtualBoundsPixels();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Editor.LoadImage(CreateBackground(bounds.Width, bounds.Height, transparent));
        Editor.Applied += Editor_Applied;
        Editor.Cancelled += (_, _) => Close();
        SourceInitialized += (_, _) => FitToVirtualScreenPixels();
        Loaded += (_, _) =>
        {
            Editor.ConfigureCaptureOverlay(
                new Rect(0, 0, ActualWidth, ActualHeight),
                new Size(ActualWidth, ActualHeight),
                new Rect(0, Math.Max(0, ActualHeight - 1), ActualWidth, 1),
                showActions: true);
            Editor.Focus();
        };
    }

    internal bool IsTransparentWhiteboard => _transparent;

    private void Editor_Applied(AnnotationAppliedEventArgs args)
    {
        Clipboard.SetImage(args.FlattenedImage);
        HistoryService.Add(args.FlattenedImage, new Int32Rect(0, 0, args.FlattenedImage.PixelWidth, args.FlattenedImage.PixelHeight), args.BaseImage, "Whiteboard");
        Close();
    }

    private void FitToVirtualScreenPixels()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var bounds = DisplayTopologyService.VirtualBoundsPixels();
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        }
    }

    private static BitmapSource CreateBackground(int width, int height, bool transparent)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var pixels = new byte[checked(width * height * 4)];
        if (!transparent) Array.Fill(pixels, (byte)255);
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }
}
