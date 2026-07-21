using SnapPin.Services;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapPin.Windows;

internal sealed class PortableUpdateWindow : Window
{
    private readonly PortableUpdateRequest _request;
    private readonly TextBlock _status;
    private readonly TextBlock _detail;
    private readonly ProgressBar _progress;
    private bool _running = true;

    internal PortableUpdateWindow(PortableUpdateRequest request)
    {
        _request = request;
        Title = LocalizationService.Current("Applying portable update");
        Width = 520;
        Height = 240;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/SnapPin;component/Assets/SnapPin-icon-512.png"));
        Topmost = true;
        Background = Brushes.White;
        Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 29));
        DpiLayoutService.Attach(this);

        var panel = new StackPanel { Margin = new Thickness(30, 26, 30, 24) };
        _status = new TextBlock
        {
            Text = LocalizationService.Current("Waiting for SnapPin to close..."),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _detail = new TextBlock
        {
            Text = LocalizationService.Current("Do not close this window while the portable files are being replaced."),
            Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        };
        _progress = new ProgressBar { Height = 13, Minimum = 0, Maximum = 100 };
        panel.Children.Add(_status);
        panel.Children.Add(_detail);
        panel.Children.Add(_progress);
        Content = panel;
        Loaded += ApplyUpdate;
        Closing += (_, args) => { if (_running) args.Cancel = true; };
    }

    private async void ApplyUpdate(object sender, RoutedEventArgs e)
    {
        PortableUpdateResult result;
        try
        {
            var progress = new Progress<PortableUpdateProgress>(value =>
            {
                _status.Text = value.Status;
                _progress.Value = Math.Clamp(value.Fraction * 100, 0, 100);
            });
            result = await PortableUpdateService.ApplyAsync(_request, progress);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("portable-update-window", ex.Message, ex);
            _running = false;
            MessageBox.Show(this, ex.Message, LocalizationService.Current("Portable update failed"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown(1);
            return;
        }

        if (!result.Success && result.RetryAsAdministrator)
        {
            _status.Text = LocalizationService.Current("Administrator permission is required. Waiting for approval...");
            _detail.Text = LocalizationService.Current("SnapPin will retry the portable update with administrator permission.");
            _progress.IsIndeterminate = true;
            if (PortableUpdateService.TryRestartAsAdministrator(_request, out var elevationError))
            {
                await Task.Delay(250);
                _running = false;
                Application.Current.Shutdown(0);
                return;
            }
            _progress.IsIndeterminate = false;
            if (!string.IsNullOrWhiteSpace(elevationError))
                result = result with { Message = result.Message + Environment.NewLine + elevationError,
                    RetryAsAdministrator = false };
        }

        await Task.Delay(650);
        _running = false;
        try
        {
            if (PortableUpdateService.RestartTarget(_request, result) is null)
            {
                ShowUnrecoverableFailure(result);
                return;
            }
            Application.Current.Shutdown(result.Success ? 0 : 1);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("portable-update-restart", ex.Message, ex);
            ShowUnrecoverableFailure(result with { Message = result.Message + Environment.NewLine + ex.Message });
        }
    }

    private void ShowUnrecoverableFailure(PortableUpdateResult result)
    {
        _status.Text = LocalizationService.Current("Portable update failed");
        _detail.Text = result.Message;
        _progress.Foreground = Brushes.IndianRed;
        var panel = Content as StackPanel;
        if (panel is null) return;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var log = new Button { Content = LocalizationService.Current("Open update log"), MinWidth = 120, Height = 34, Margin = new Thickness(0, 0, 10, 0) };
        log.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result.LogPath}\"") { UseShellExecute = true });
        var close = new Button { Content = LocalizationService.Current("Close"), MinWidth = 88, Height = 34 };
        close.Click += (_, _) => Application.Current.Shutdown(1);
        buttons.Children.Add(log);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
    }
}
