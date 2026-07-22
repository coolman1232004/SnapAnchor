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
    private async Task RunLongCaptureAsync()
    {
        if (_longCaptureRunning || !EnsureSelection()) return;
        _longCaptureRunning = true;
        _ocrCancellation?.Cancel();
        var pixelRegion = SelectedPixelRegion();
        var virtualBounds = DisplayTopologyService.VirtualBoundsPixels();
        var screenRegion = new Rect(
            virtualBounds.Left + pixelRegion.X,
            virtualBounds.Top + pixelRegion.Y,
            pixelRegion.Width,
            pixelRegion.Height);
        var centerX = (int)Math.Round(screenRegion.X + screenRegion.Width / 2);
        var centerY = (int)Math.Round(screenRegion.Y + screenRegion.Height / 2);
        var originalCursor = Forms.Cursor.Position;
        var scrollPoint = new NativeMethods.NativePoint { X = centerX, Y = centerY };
        var scrollTarget = IntPtr.Zero;
        LongCaptureProgressWindow? progressWindow = null;
        var stopRequested = false;

        try
        {
            Hide();
            await Task.Delay(220);
            NativeMethods.SetCursorPos(centerX, centerY);
            scrollTarget = NativeMethods.WindowFromPoint(scrollPoint);
            if (scrollTarget != IntPtr.Zero)
            {
                var root = NativeMethods.GetAncestor(scrollTarget, NativeMethods.GaRoot);
                NativeMethods.SetForegroundWindow(root == IntPtr.Zero ? scrollTarget : root);
            }
            await Task.Delay(140);
            progressWindow = new LongCaptureProgressWindow();
            progressWindow.StopRequested += (_, _) => stopRequested = true;
            progressWindow.Show();
            var progress = new Progress<ScrollingCaptureProgress>(item => progressWindow?.Report(item));
            var result = await ScrollingCaptureService.CaptureAsync(
                screenRegion,
                _settings,
                scrollTarget,
                scrollPoint,
                progress: progress,
                stopRequested: () => stopRequested);
            progressWindow.Close();
            progressWindow = null;
            NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            if (result.FrameCount <= 1 && !result.Cancelled)
                throw new InvalidOperationException(L("The selected content did not scroll. Place the pointer over a scrollable page and try again."));
            Close();
            Clipboard.SetImage(result.Image);
            HistoryService.Add(result.Image, sourceKind: "Scrolling");
            var pin = new PinnedImageWindow(result.Image);
            pin.Show();
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch (OperationCanceledException)
        {
            NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            Show(); Activate();
        }
        catch (Exception ex)
        {
            NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            Show(); Activate();
            MessageBox.Show(this, ex.Message, L("Long screenshot failed"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            progressWindow?.Close();
            _longCaptureRunning = false;
        }
    }

    private async Task RunCaptureOcrAsync(bool copyWhenDone)
    {
        if (!EnsureSelection()) return;
        _ocrCancellation?.Cancel();
        _ocrCancellation = new CancellationTokenSource();
        OcrPanel.Visibility = Visibility.Visible;
        CaptureOcrResultBox.Text = L("Recognizing locally…");
        CopyOcrButton.IsEnabled = false;
        RetryOcrButton.IsEnabled = false;
        ActionBar.Visibility = Visibility.Visible;
        PositionFloatingPanels();

        try
        {
            var ocrArea = _ocrAreaSelection.Width >= 3 && _ocrAreaSelection.Height >= 3
                ? _ocrAreaSelection
                : _selection;
            var pixelRegion = PixelRegion(ocrArea);
            var image = CaptureService.Crop(_screen, pixelRegion);
            var language = CaptureOcrLanguageBox.SelectedValue as string ?? "eng";
            var result = await RecognitionService.RecognizeAsync(image, language, _ocrCancellation.Token);
            var sections = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.Text)) sections.Add(result.Text.Trim());
            if (!string.IsNullOrWhiteSpace(result.BarcodeText))
                sections.Add($"[{(string.IsNullOrWhiteSpace(result.BarcodeFormat) ? "CODE" : result.BarcodeFormat)}]{Environment.NewLine}{result.BarcodeText.Trim()}");
            CaptureOcrResultBox.Text = sections.Count > 0 ? string.Join($"{Environment.NewLine}{Environment.NewLine}", sections) : L("No text or code was found.");
            CopyOcrButton.IsEnabled = sections.Count > 0;

            if (sections.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(_ocrHistoryRecordId))
                    _ocrHistoryRecordId = HistoryService.Add(image, pixelRegion, _screen, "OCR").Id;
                HistoryService.UpdateRecognition(_ocrHistoryRecordId, result.Text, result.BarcodeText, result.BarcodeFormat);
                if (copyWhenDone || OcrAutoCopyBox.IsChecked == true) Clipboard.SetText(CaptureOcrResultBox.Text);
            }
        }
        catch (OperationCanceledException)
        {
            CaptureOcrResultBox.Text = L("Recognition cancelled.");
        }
        catch (Exception ex)
        {
            while (ex.InnerException is not null) ex = ex.InnerException;
            CaptureOcrResultBox.Text = LocalizationService.Format("Recognition failed: {0}", ex.Message);
        }
        finally
        {
            RetryOcrButton.IsEnabled = true;
        }
    }

    private void SelectOcrArea_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSelection()) return;
        _ocrCancellation?.Cancel();
        _ocrAreaSelectionMode = true;
        _ocrAreaDragging = false;
        _ocrAreaSelection = Rect.Empty;
        OcrAreaRect.Visibility = Visibility.Collapsed;
        OcrAreaHint.Visibility = Visibility.Visible;
        SelectionHandles.Visibility = Visibility.Collapsed;
        PositionOcrAreaHint();
        Focus();
        Cursor = Cursors.Cross;
    }

    private void BeginOcrAreaDrag(Point point)
    {
        var clamped = ClampToSelection(point);
        _ocrAreaStart = clamped;
        _ocrAreaSelection = new Rect(clamped, clamped);
        _ocrAreaDragging = true;
        CaptureMouse();
        UpdateOcrAreaRect();
    }

    private void UpdateOcrAreaDrag(Point point)
    {
        var clamped = ClampToSelection(point);
        _ocrAreaSelection = OcrAreaWithin(_selection, _ocrAreaStart, clamped);
        UpdateOcrAreaRect();
    }

    private void FinishOcrAreaDrag(Point point)
    {
        if (!_ocrAreaDragging) return;
        UpdateOcrAreaDrag(point);
        _ocrAreaDragging = false;
        _ocrAreaSelectionMode = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        OcrAreaHint.Visibility = Visibility.Collapsed;
        if (_ocrAreaSelection.Width < 3 || _ocrAreaSelection.Height < 3)
        {
            _ocrAreaSelection = Rect.Empty;
            OcrAreaRect.Visibility = Visibility.Collapsed;
            CaptureOcrResultBox.Text = L("The recognition area was too small. Select an area and try again.");
            return;
        }
        _ocrHistoryRecordId = null;
        _ = RunCaptureOcrAsync(false);
    }

    internal static Rect OcrAreaWithin(Rect selection, Point start, Point end)
        => Rect.Intersect(new Rect(start, end), selection);

    private Point ClampToSelection(Point point)
        => new(Math.Clamp(point.X, _selection.Left, _selection.Right),
            Math.Clamp(point.Y, _selection.Top, _selection.Bottom));

    private void UpdateOcrAreaRect()
    {
        if (_ocrAreaSelection.IsEmpty) return;
        Canvas.SetLeft(OcrAreaRect, _ocrAreaSelection.Left);
        Canvas.SetTop(OcrAreaRect, _ocrAreaSelection.Top);
        OcrAreaRect.Width = _ocrAreaSelection.Width;
        OcrAreaRect.Height = _ocrAreaSelection.Height;
        OcrAreaRect.Visibility = Visibility.Visible;
    }

    private void PositionOcrAreaHint()
    {
        OcrAreaHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = OcrAreaHint.DesiredSize.Width;
        var height = OcrAreaHint.DesiredSize.Height;
        var left = Math.Clamp(_selection.Left + (_selection.Width - width) / 2, 8, Math.Max(8, ActualWidth - width - 8));
        var top = Math.Clamp(_selection.Top + 10, 8, Math.Max(8, ActualHeight - height - 8));
        Canvas.SetLeft(OcrAreaHint, left);
        Canvas.SetTop(OcrAreaHint, top);
    }

    private void CancelOcrAreaSelection()
    {
        _ocrAreaSelectionMode = false;
        _ocrAreaDragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        OcrAreaHint.Visibility = Visibility.Collapsed;
        if (_ocrAreaSelection.Width < 3 || _ocrAreaSelection.Height < 3)
        {
            _ocrAreaSelection = Rect.Empty;
            OcrAreaRect.Visibility = Visibility.Collapsed;
        }
        Focus();
    }

    private void RetryOcr_Click(object sender, RoutedEventArgs e)
    {
        _ocrHistoryRecordId = null;
        _ = RunCaptureOcrAsync(false);
    }

    private bool EnsureSelection()
    {
        if (_selection.Width >= 3 && _selection.Height >= 3) return true;
        if (_detectedSelection.Width < 3 || _detectedSelection.Height < 3) return false;
        _selection = _detectedSelection;
        RenderSelection();
        PositionActionBar();
        ActionBar.Visibility = Visibility.Visible;
        return true;
    }

    private void CloseOcr_Click(object sender, RoutedEventArgs e)
    {
        _ocrCancellation?.Cancel();
        CancelOcrAreaSelection();
        _ocrAreaSelection = Rect.Empty;
        OcrAreaRect.Visibility = Visibility.Collapsed;
        SelectionHandles.Visibility = _selection.Width >= 3 && _selection.Height >= 3 ? Visibility.Visible : Visibility.Collapsed;
        OcrPanel.Visibility = Visibility.Collapsed;
        Focus();
    }

    private void CopyOcr_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(CaptureOcrResultBox.Text)) Clipboard.SetText(CaptureOcrResultBox.Text);
    }

    private void OcrLayout_Click(object sender, RoutedEventArgs e)
    {
        _ocrLayoutMode = !_ocrLayoutMode;
        CaptureOcrResultBox.TextWrapping = _ocrLayoutMode ? TextWrapping.NoWrap : TextWrapping.Wrap;
        CaptureOcrResultBox.FontFamily = new FontFamily(_ocrLayoutMode ? "Consolas" : "Segoe UI");
        OcrLayoutButton.FontWeight = _ocrLayoutMode ? FontWeights.Bold : FontWeights.Normal;
    }

    private void CaptureOcrLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        _settings.OcrLanguage = CaptureOcrLanguageBox.SelectedValue as string ?? "eng";
        SettingsService.Save(_settings);
    }

    private void OcrAutoCopy_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _settings.OcrAutoCopy = OcrAutoCopyBox.IsChecked == true;
        SettingsService.Save(_settings);
    }

}
