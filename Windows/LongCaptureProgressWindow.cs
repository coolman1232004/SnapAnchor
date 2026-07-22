using SnapAnchor.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SnapAnchor.Windows;

internal sealed class LongCaptureProgressWindow : Window
{
    private readonly TextBlock _status;
    internal event EventHandler? StopRequested;

    internal LongCaptureProgressWindow()
    {
        Title = LocalizationService.Current("SnapAnchor long capture");
        Width = 310;
        Height = 58;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        _status = new TextBlock
        {
            Text = LocalizationService.Current("Preparing long capture..."),
            Foreground = new SolidColorBrush(Color.FromRgb(28, 25, 23)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var stop = new Button
        {
            Content = LocalizationService.Current("Stop"),
            Width = 62,
            Height = 30,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(10, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(231, 229, 228))
        };
        stop.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(stop, Dock.Right);
        row.Children.Add(stop);
        row.Children.Add(_status);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(248, 250, 249, 247)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(120, 113, 108)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 9, 8),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 2, Opacity = 0.25 },
            Child = row
        };
        SourceInitialized += (_, _) => NativeMethods.SetWindowDisplayAffinity(
            new WindowInteropHelper(this).Handle, NativeMethods.WdaExcludeFromCapture);
        Loaded += (_, _) =>
        {
            Left = SystemParameters.WorkArea.Right - ActualWidth - 18;
            Top = SystemParameters.WorkArea.Bottom - ActualHeight - 18;
        };
    }

    internal void Report(ScrollingCaptureProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Report(progress));
            return;
        }
        _status.Text = LocalizationService.Format("{0}  |  kept {1}, skipped {2}", progress.Message, progress.FrameCount, progress.RejectedFrames);
    }
}
