using Microsoft.Win32;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using SnapAnchor.Services;
using SnapAnchor.Models;
using SnapAnchor.Controls;
using Forms = System.Windows.Forms;


namespace SnapAnchor.Windows;

public partial class CaptureOverlayWindow
{
    private async void Record_Click(object sender, RoutedEventArgs e) => await RunRecordingAsync();

    private async Task RunRecordingAsync()
    {
        if (_recordingRunning || !EnsureSelection()) return;
        _recordingRunning = true;
        _recordingPaused = false;
        RecordingPauseIcon.Visibility = Visibility.Visible;
        RecordingResumeIcon.Visibility = Visibility.Collapsed;
        RecordingPauseLabel.Text = L("Pause");
        RecordingPauseButton.ToolTip = "Pause recording (Space)";
        _recordingStopRequested = false;
        _recordingCancellation = new CancellationTokenSource();
        _settings.RecordingIncludeCursor = RecordingCursorBox.IsChecked ?? _settings.RecordingIncludeCursor;
        SettingsService.Save(_settings);
        var pixelRegion = SelectedPixelRegion();
        var virtualBounds = DisplayTopologyService.VirtualBoundsPixels();
        var screenRegion = new Rect(virtualBounds.Left + pixelRegion.X, virtualBounds.Top + pixelRegion.Y, pixelRegion.Width, pixelRegion.Height);
        _recordingTargetWindow = _settings.RecordingCaptureMode.Equals("Window", StringComparison.OrdinalIgnoreCase)
            ? ElementDetectionService.WindowHandleAt(new Point(screenRegion.X + screenRegion.Width / 2, screenRegion.Y + screenRegion.Height / 2), _overlayHandle)
            : IntPtr.Zero;

        EnterRecordingAppearance();
        try
        {
            var countdown = Math.Clamp(_settings.RecordingCountdownSeconds, 0, 10);
            for (var remaining = countdown; remaining > 0; remaining--)
            {
                RecordingCountdownText.Text = remaining.ToString();
                RecordingCountdownBadge.Visibility = Visibility.Visible;
                await Task.Delay(1000, _recordingCancellation.Token);
                if (_recordingStopRequested)
                {
                    RestoreCaptureAppearance();
                    return;
                }
            }
            RecordingCountdownBadge.Visibility = Visibility.Collapsed;
            _settings.RecordingIncludeCursor = RecordingCursorBox.IsChecked == true;
            RecordingTimerText.Text = "00:00";
            var result = _settings.RecordingFormat.Equals("MP4", StringComparison.OrdinalIgnoreCase)
                ? await CaptureMp4Async(screenRegion, _recordingCancellation.Token)
                : await ScreenRecordingService.CaptureGifAsync(
                    screenRegion,
                    _settings,
                    () => _recordingPaused,
                    () => _recordingStopRequested,
                    (elapsed, frames) => Dispatcher.BeginInvoke(() =>
                    {
                        RecordingTimerText.Text = elapsed.ToString(@"mm\:ss");
                        RecordingFrameText.Text = _recordingPaused ? L("Paused") : frames == 1 ? L("1 frame") : LocalizationService.Format("{0} frames", frames);
                    }),
                    _recordingCancellation.Token);
            if (_settings.RecordingFormat.Equals("MP4", StringComparison.OrdinalIgnoreCase))
            {
                var reviewed = await ReviewAndTrimAsync(result, _recordingCancellation.Token);
                if (reviewed is null)
                {
                    RestoreCaptureAppearance();
                    return;
                }
                result = reviewed;
            }
            RecordingTimerText.Text = L("Saving…");
            HistoryService.AddRecording(result.Preview, result.FilePath, result.Duration, result.FrameCount);
            System.Media.SystemSounds.Asterisk.Play();
            Close();
        }
        catch (OperationCanceledException)
        {
            if (IsVisible) RestoreCaptureAppearance();
        }
        catch (Exception ex)
        {
            RestoreCaptureAppearance();
            MessageBox.Show(this, ex.Message, L("Recording failed"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _advancedRecording = null;
            _recordingReviewRunning = false;
            _recordingRunning = false;
            _recordingPaused = false;
        }
    }

    private async Task<ScreenRecordingResult> CaptureMp4Async(Rect screenRegion, CancellationToken cancellationToken)
    {
        using var session = AdvancedRecordingSession.Start(screenRegion, _settings, _overlayHandle, _recordingTargetWindow);
        _advancedRecording = session;
        RecordingFrameText.Text = $"MP4 · {_settings.RecordingCaptureMode}";
        var maximum = TimeSpan.FromSeconds(Math.Clamp(_settings.RecordingMaxDurationSeconds, 5, 600));
        try
        {
            while (!_recordingStopRequested && session.Elapsed < maximum)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecordingTimerText.Text = session.Elapsed.ToString(@"mm\:ss");
                if (!_recordingPaused) RecordingFrameText.Text = $"MP4 · {_settings.RecordingCaptureMode} · {session.FrameCount}f";
                await Task.Delay(100, cancellationToken);
            }
            RecordingTimerText.Text = L("Saving…");
            var result = await session.StopAsync(cancellationToken);
            return new ScreenRecordingResult(result.FilePath, result.Preview, result.Duration, result.FrameCount);
        }
        catch (OperationCanceledException)
        {
            try { await session.StopAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    private void EnterRecordingAppearance()
    {
        ScreenImage.Visibility = Visibility.Collapsed;
        MaskLayer.Visibility = Visibility.Collapsed;
        SelectionMask.Visibility = Visibility.Collapsed;
        SelectionHandles.Visibility = Visibility.Collapsed;
        DetectionRect.Visibility = Visibility.Collapsed;
        CrossHorizontal.Visibility = Visibility.Collapsed;
        CrossVertical.Visibility = Visibility.Collapsed;
        ShortcutHints.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        ActionBar.Visibility = Visibility.Collapsed;
        CaptureInlineEditor.Visibility = Visibility.Collapsed;
        OcrPanel.Visibility = Visibility.Collapsed;
        Root.Background = Brushes.Transparent;
        Background = Brushes.Transparent;
        SelectionRect.Stroke = Brushes.Red;
        SelectionRect.Fill = Brushes.Transparent;
        RecordingCursorBox.IsChecked = _settings.RecordingIncludeCursor;
        RecordingBar.Visibility = Visibility.Visible;
        PositionRecordingControls();
        Cursor = System.Windows.Input.Cursors.Arrow;
    }

    private void RestoreCaptureAppearance()
    {
        ScreenImage.Visibility = Visibility.Visible;
        Root.Background = Brushes.Black;
        Background = Brushes.Black;
        RecordingBar.Visibility = Visibility.Collapsed;
        RecordingCountdownBadge.Visibility = Visibility.Collapsed;
        ShortcutHints.Visibility = CaptureHintsVisibility;
        SelectionRect.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_settings.CaptureBorderColor));
        Cursor = System.Windows.Input.Cursors.Cross;
        RenderSelection();
        CaptureInlineEditor.Visibility = _annotationMode ? Visibility.Visible : Visibility.Collapsed;
        ActionBar.Visibility = Visibility.Visible;
        PositionActionBar();
    }

    private void PositionRecordingControls()
    {
        var compact = ActualWidth < 520;
        RecordingFrameText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RecordingCursorBox.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RecordingBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = RecordingBar.DesiredSize.Width;
        var height = RecordingBar.DesiredSize.Height;
        var left = Math.Clamp(_selection.Right - width, 8, Math.Max(8, ActualWidth - width - 8));
        var top = _selection.Bottom + 10 + height <= ActualHeight ? _selection.Bottom + 10 : Math.Max(8, _selection.Top - height - 10);
        Canvas.SetLeft(RecordingBar, left);
        Canvas.SetTop(RecordingBar, top);
        Canvas.SetLeft(RecordingCountdownBadge, Math.Clamp(_selection.X + (_selection.Width - 82) / 2, 8, Math.Max(8, ActualWidth - 90)));
        Canvas.SetTop(RecordingCountdownBadge, Math.Clamp(_selection.Y + (_selection.Height - 82) / 2, 8, Math.Max(8, ActualHeight - 90)));
        RecordingReviewPanel.Width = Math.Min(620, Math.Max(300, ActualWidth - 20));
        RecordingReviewPanel.Height = Math.Min(470, Math.Max(260, ActualHeight - 20));
        if (!_recordingReviewPositioned)
        {
            Canvas.SetLeft(RecordingReviewPanel, Math.Max(10, (ActualWidth - RecordingReviewPanel.Width) / 2));
            Canvas.SetTop(RecordingReviewPanel, Math.Max(10, (ActualHeight - RecordingReviewPanel.Height) / 2));
            _recordingReviewPositioned = true;
        }
        else
        {
            Canvas.SetLeft(RecordingReviewPanel, Math.Clamp(Canvas.GetLeft(RecordingReviewPanel), 0, Math.Max(0, ActualWidth - RecordingReviewPanel.Width)));
            Canvas.SetTop(RecordingReviewPanel, Math.Clamp(Canvas.GetTop(RecordingReviewPanel), 0, Math.Max(0, ActualHeight - RecordingReviewPanel.Height)));
        }
    }

    private void RecordingReviewHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_recordingReviewRunning) return;
        _recordingReviewDragging = true;
        _recordingReviewDragStart = e.GetPosition(OverlayCanvas);
        _recordingReviewStart = new Point(Canvas.GetLeft(RecordingReviewPanel), Canvas.GetTop(RecordingReviewPanel));
        RecordingReviewHeader.CaptureMouse();
        e.Handled = true;
    }

    private void RecordingReviewHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_recordingReviewDragging) return;
        var point = e.GetPosition(OverlayCanvas);
        var left = _recordingReviewStart.X + point.X - _recordingReviewDragStart.X;
        var top = _recordingReviewStart.Y + point.Y - _recordingReviewDragStart.Y;
        Canvas.SetLeft(RecordingReviewPanel, Math.Clamp(left, 0, Math.Max(0, ActualWidth - RecordingReviewPanel.ActualWidth)));
        Canvas.SetTop(RecordingReviewPanel, Math.Clamp(top, 0, Math.Max(0, ActualHeight - RecordingReviewPanel.ActualHeight)));
        e.Handled = true;
    }

    private void RecordingReviewHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_recordingReviewDragging) return;
        _recordingReviewDragging = false;
        RecordingReviewHeader.ReleaseMouseCapture();
        e.Handled = true;
    }

    private async Task<ScreenRecordingResult?> ReviewAndTrimAsync(ScreenRecordingResult result, CancellationToken cancellationToken)
    {
        if (result.Duration < TimeSpan.FromSeconds(1)) return result;
        _recordingReviewRunning = true;
        RecordingBar.Visibility = Visibility.Collapsed;
        RecordingCountdownBadge.Visibility = Visibility.Collapsed;
        SelectionRect.Visibility = Visibility.Collapsed;
        TrimStartSlider.Maximum = result.Duration.TotalSeconds;
        TrimEndSlider.Maximum = result.Duration.TotalSeconds;
        TrimStartSlider.Value = 0;
        TrimEndSlider.Value = result.Duration.TotalSeconds;
        TrimDurationText.Text = result.Duration.ToString(@"mm\:ss");
        RecordingSavePathText.Text = LocalizationService.Format("Will save to: {0}", result.FilePath);
        RecordingSavePathText.ToolTip = result.FilePath;
        _recordingPreviewPlaying = false;
        ReviewPlayIcon.Visibility = Visibility.Visible;
        ReviewPauseIcon.Visibility = Visibility.Collapsed;
        ReviewPlayLabel.Text = L("Play");
        _recordingReviewPositioned = false;
        RecordingPreviewMedia.Source = new Uri(result.FilePath);
        RecordingReviewPanel.Visibility = Visibility.Visible;
        PositionRecordingControls();
        UpdateTrimLabels();
        _recordingReviewChoice = new TaskCompletionSource<RecordingReviewChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var choice = await _recordingReviewChoice.Task.WaitAsync(cancellationToken);
        RecordingPreviewMedia.Stop();
        RecordingPreviewMedia.Source = null;
        RecordingPreviewMedia.Close();
        RecordingReviewPanel.Visibility = Visibility.Collapsed;
        _recordingReviewDragging = false;
        _recordingReviewRunning = false;
        if (choice == RecordingReviewChoice.Discard)
        {
            for (var attempt = 0; attempt < 5 && File.Exists(result.FilePath); attempt++)
            {
                try { File.Delete(result.FilePath); }
                catch (IOException) when (attempt < 4) { await Task.Delay(120, cancellationToken); }
            }
            if (File.Exists(result.FilePath))
            {
                DiagnosticsService.Log("recording-discard", $"Could not delete discarded recording: {result.FilePath}");
                MessageBox.Show(this, LocalizationService.Format(
                        "The recording was not added to history, but Windows kept the temporary file open:\n\n{0}", result.FilePath),
                    L("Recording discarded"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return null;
        }
        if (choice == RecordingReviewChoice.KeepFull) return result;
        var start = TimeSpan.FromSeconds(TrimStartSlider.Value);
        var end = TimeSpan.FromSeconds(TrimEndSlider.Value);
        var endTrim = result.Duration - end;
        RecordingTimerText.Text = L("Trimming…");
        var path = await MediaTrimService.TrimMp4Async(result.FilePath, start, endTrim, cancellationToken);
        return result with { FilePath = path, Duration = end - start };
    }

    private void TrimSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || TrimStartText is null) return;
        if (TrimEndSlider.Value < TrimStartSlider.Value + 0.25)
        {
            if (ReferenceEquals(sender, TrimStartSlider)) TrimEndSlider.Value = Math.Min(TrimEndSlider.Maximum, TrimStartSlider.Value + 0.25);
            else TrimStartSlider.Value = Math.Max(0, TrimEndSlider.Value - 0.25);
        }
        UpdateTrimLabels();
    }

    private void UpdateTrimLabels()
    {
        TrimStartText.Text = TimeSpan.FromSeconds(TrimStartSlider.Value).ToString(@"mm\:ss");
        TrimEndText.Text = TimeSpan.FromSeconds(TrimEndSlider.Value).ToString(@"mm\:ss");
    }

    private void RecordingPreviewPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingPreviewPlaying) RecordingPreviewMedia.Pause();
        else
        {
            RecordingPreviewMedia.Position = TimeSpan.FromSeconds(TrimStartSlider.Value);
            RecordingPreviewMedia.Play();
        }
        _recordingPreviewPlaying = !_recordingPreviewPlaying;
        ReviewPlayIcon.Visibility = _recordingPreviewPlaying ? Visibility.Collapsed : Visibility.Visible;
        ReviewPauseIcon.Visibility = _recordingPreviewPlaying ? Visibility.Visible : Visibility.Collapsed;
        ReviewPlayLabel.Text = L(_recordingPreviewPlaying ? "Pause" : "Play");
    }

    private void RecordingPreviewMedia_MediaEnded(object sender, RoutedEventArgs e)
    {
        _recordingPreviewPlaying = false;
        RecordingPreviewMedia.Stop();
        RecordingPreviewMedia.Position = TimeSpan.FromSeconds(TrimStartSlider.Value);
        ReviewPlayIcon.Visibility = Visibility.Visible;
        ReviewPauseIcon.Visibility = Visibility.Collapsed;
        ReviewPlayLabel.Text = L("Play");
    }

    private void KeepFullRecording_Click(object sender, RoutedEventArgs e) =>
        _recordingReviewChoice?.TrySetResult(RecordingReviewChoice.KeepFull);

    private void SaveTrimmedRecording_Click(object sender, RoutedEventArgs e) =>
        _recordingReviewChoice?.TrySetResult(RecordingReviewChoice.SaveTrimmed);

    private void DiscardRecording_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, L("Discard this recording permanently?"), L("Discard recording"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _recordingReviewChoice?.TrySetResult(RecordingReviewChoice.Discard);
    }

    private void RecordingPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_recordingRunning) return;
        _recordingPaused = !_recordingPaused;
        if (_advancedRecording is not null)
        {
            if (_recordingPaused) _advancedRecording.Pause(); else _advancedRecording.Resume();
        }
        RecordingPauseIcon.Visibility = _recordingPaused ? Visibility.Collapsed : Visibility.Visible;
        RecordingResumeIcon.Visibility = _recordingPaused ? Visibility.Visible : Visibility.Collapsed;
        RecordingPauseLabel.Text = L(_recordingPaused ? "Resume" : "Pause");
        RecordingPauseButton.ToolTip = _recordingPaused ? "Resume recording (Space)" : "Pause recording (Space)";
        RecordingFrameText.Text = L(_recordingPaused ? "Paused" : "Resuming…");
    }

    private void RecordingStop_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingRunning) _recordingStopRequested = true;
    }

}
