using Microsoft.Win32;
using SnapPin.Models;
using SnapPin.Services;
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


namespace SnapPin.Windows;

public partial class PinnedImageWindow
{
    private async Task SetTextSelectableAsync(bool enabled)
    {
        if (!enabled)
        {
            HideSelectableText();
            SaveSession();
            return;
        }

        SetClickThrough(false);
        _textSelectable = true;
        ClearRecognizedTextSelection();
        ShowImageSurface();
        if (ReferenceEquals(_recognizedTextSource, _source) && _recognizedWords.Count > 0)
        {
            RebuildSelectableTextLayers();
            SaveSession();
            return;
        }

        _selectableTextCancellation?.Cancel();
        _selectableTextCancellation?.Dispose();
        var cancellation = _selectableTextCancellation = new CancellationTokenSource();
        var source = _source;
        ShowPinStatus("Recognizing text locally…");
        try
        {
            var result = await RecognitionService.RecognizeAsync(source, _settings.OcrLanguage, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(source, _source)) return;
            _recognizedTextSource = source;
            _selectableTextResult = result;
            _recognizedWords = result.RecognizedWords;
            PersistRecognitionResult(result);
            if (_recognizedWords.Count == 0)
            {
                HideSelectableText();
                ShowPinStatus("No selectable text found");
                return;
            }
            RebuildSelectableTextLayers();
            SaveSession();
            ShowPinStatus($"{_recognizedWords.Count:N0} selectable words");
        }
        catch (OperationCanceledException)
        {
            if (_textSelectable) HideSelectableText();
        }
        catch (Exception ex)
        {
            HideSelectableText();
            ShowPinStatus($"Text recognition failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_selectableTextCancellation, cancellation))
            {
                _selectableTextCancellation.Dispose();
                _selectableTextCancellation = null;
            }
        }
    }

    private void ShowPinStatus(string text)
    {
        ZoomText.Text = text;
        ZoomBadge.Visibility = Visibility.Visible;
        _zoomBadgeTimer.Stop();
        _zoomBadgeTimer.Start();
    }

    private void HideSelectableText()
    {
        _textSelectable = false;
        _selectingText = false;
        StopTextAutoScroll();
        if (Mouse.Captured is Canvas captured && (captured == SelectableTextLayer || captured == LongSelectableTextLayer))
            captured.ReleaseMouseCapture();
        SelectableTextLayer.Visibility = Visibility.Collapsed;
        LongSelectableTextLayer.Visibility = Visibility.Collapsed;
        ClearRecognizedTextSelection();
        UpdatePinStateBadge();
    }

    private void InvalidateSelectableText()
    {
        _selectableTextCancellation?.Cancel();
        _selectableTextCancellation?.Dispose();
        _selectableTextCancellation = null;
        _recognizedTextSource = null;
        _selectableTextResult = null;
        _recognizedWords = Array.Empty<RecognizedWord>();
        SelectableTextLayer.Children.Clear();
        LongSelectableTextLayer.Children.Clear();
        _textHitRegions.Clear();
        HideSelectableText();
    }

    private void RebuildSelectableTextLayers()
    {
        RebuildSelectableTextLayer(SelectableTextLayer);
        RebuildSelectableTextLayer(LongSelectableTextLayer);
        ShowImageSurface();
        UpdatePinStateBadge();
        Dispatcher.BeginInvoke(UpdateSelectableTextLayout, DispatcherPriority.Loaded);
    }

    private void RebuildSelectableTextLayer(Canvas layer)
    {
        layer.Children.Clear();
        for (var index = 0; index < _recognizedWords.Count; index++)
        {
            layer.Children.Add(new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(2),
                IsHitTestVisible = false,
                Tag = index,
                ToolTip = _recognizedWords[index].Text
            });
        }
        UpdateSelectableTextLayer(layer);
    }

    private void UpdateSelectableTextLayout()
    {
        UpdateSelectableTextLayer(SelectableTextLayer);
        UpdateSelectableTextLayer(LongSelectableTextLayer);
        PositionTextSelectionToolbar();
    }

    private void UpdateSelectableTextLayer(Canvas layer)
    {
        if (_recognizedWords.Count == 0 || layer.Children.Count != _recognizedWords.Count) return;
        var width = layer.ActualWidth > 1 ? layer.ActualWidth : layer.Width;
        var height = layer.ActualHeight > 1 ? layer.ActualHeight : layer.Height;
        if (double.IsNaN(width) || width <= 1 || double.IsNaN(height) || height <= 1) return;
        var scale = Math.Min(width / Math.Max(1.0, _source.PixelWidth), height / Math.Max(1.0, _source.PixelHeight));
        var offsetX = (width - _source.PixelWidth * scale) / 2;
        var offsetY = (height - _source.PixelHeight * scale) / 2;
        var regions = new List<Rect>(_recognizedWords.Count);
        for (var index = 0; index < _recognizedWords.Count; index++)
        {
            var word = _recognizedWords[index];
            var rect = new Rect(
                offsetX + word.X * scale,
                offsetY + word.Y * scale,
                Math.Max(3, word.Width * scale),
                Math.Max(3, word.Height * scale));
            regions.Add(rect);
            if (layer.Children[index] is not Border visual) continue;
            visual.Width = rect.Width;
            visual.Height = rect.Height;
            Canvas.SetLeft(visual, rect.X);
            Canvas.SetTop(visual, rect.Y);
        }
        _textHitRegions[layer] = regions;
        UpdateRecognizedTextSelectionVisuals();
    }

    private void SelectableText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_textSelectable || sender is not Canvas layer) return;
        var index = HitRecognizedWord(layer, e.GetPosition(layer), false);
        if (index is null)
        {
            if (_textSelectionAnchor is not null || _textSelectionEnd is not null)
            {
                ClearRecognizedTextSelection();
                e.Handled = true;
            }
            return;
        }
        _textSelectionAnchor = _textSelectionEnd = index;
        if (e.ClickCount >= 2)
        {
            _selectingText = false;
            UpdateRecognizedTextSelectionVisuals();
            PositionTextSelectionToolbar(reanchor: _textSelectionToolbarPosition is null);
            e.Handled = true;
            return;
        }
        _selectingText = true;
        _textSelectionToolbarPosition = null;
        TextSelectionToolbar.Visibility = Visibility.Collapsed;
        layer.CaptureMouse();
        UpdateRecognizedTextSelectionVisuals();
        e.Handled = true;
    }

    private void SelectableText_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_selectingText || sender is not Canvas layer || e.LeftButton != MouseButtonState.Pressed) return;
        UpdateTextAutoScroll(layer);
        var index = HitRecognizedWord(layer, e.GetPosition(layer), true);
        if (index is null || index == _textSelectionEnd) return;
        _textSelectionEnd = index;
        UpdateRecognizedTextSelectionVisuals();
        e.Handled = true;
    }

    private void SelectableText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selectingText || sender is not Canvas layer) return;
        var index = HitRecognizedWord(layer, e.GetPosition(layer), true);
        if (index is not null) _textSelectionEnd = index;
        _selectingText = false;
        StopTextAutoScroll();
        if (Mouse.Captured == layer) layer.ReleaseMouseCapture();
        UpdateRecognizedTextSelectionVisuals();
        PositionTextSelectionToolbar(reanchor: true);
        e.Handled = true;
    }

    private void UpdateTextAutoScroll(Canvas layer)
    {
        if (layer != LongSelectableTextLayer || LongImageViewer.Visibility != Visibility.Visible)
        {
            StopTextAutoScroll();
            return;
        }
        var point = Mouse.GetPosition(LongImageViewer);
        _textAutoScrollDirection = point.Y < 30 ? -1 : point.Y > LongImageViewer.ViewportHeight - 30 ? 1 : 0;
        if (_textAutoScrollDirection == 0) _textAutoScrollTimer.Stop();
        else if (!_textAutoScrollTimer.IsEnabled) _textAutoScrollTimer.Start();
    }

    private void TextAutoScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_selectingText || _textAutoScrollDirection == 0 || LongImageViewer.Visibility != Visibility.Visible)
        {
            StopTextAutoScroll();
            return;
        }
        LongImageViewer.ScrollToVerticalOffset(LongImageViewer.VerticalOffset + _textAutoScrollDirection * 18);
        var index = HitRecognizedWord(LongSelectableTextLayer, Mouse.GetPosition(LongSelectableTextLayer), true);
        if (index is not null) _textSelectionEnd = index;
        UpdateRecognizedTextSelectionVisuals();
    }

    private void StopTextAutoScroll()
    {
        _textAutoScrollDirection = 0;
        _textAutoScrollTimer.Stop();
    }

    private int? HitRecognizedWord(Canvas layer, Point point, bool allowNearest)
    {
        if (!_textHitRegions.TryGetValue(layer, out var regions)) return null;
        for (var index = 0; index < regions.Count; index++)
            if (regions[index].Contains(point)) return index;
        if (!allowNearest || regions.Count == 0) return null;
        var nearest = regions
            .Select((rect, index) => new { index, distance = DistanceToRect(point, rect) })
            .OrderBy(item => item.distance)
            .First();
        return nearest.distance <= 32 ? nearest.index : null;
    }

    private static double DistanceToRect(Point point, Rect rect)
    {
        var dx = point.X < rect.Left ? rect.Left - point.X : point.X > rect.Right ? point.X - rect.Right : 0;
        var dy = point.Y < rect.Top ? rect.Top - point.Y : point.Y > rect.Bottom ? point.Y - rect.Bottom : 0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void UpdateRecognizedTextSelectionVisuals()
    {
        var start = Math.Min(_textSelectionAnchor ?? -1, _textSelectionEnd ?? -1);
        var end = Math.Max(_textSelectionAnchor ?? -1, _textSelectionEnd ?? -1);
        foreach (var layer in new[] { SelectableTextLayer, LongSelectableTextLayer })
        foreach (var visual in layer.Children.OfType<Border>())
        {
            var selected = visual.Tag is int index && index >= start && index <= end;
            visual.Background = selected ? new SolidColorBrush(Color.FromArgb(92, 51, 136, 255)) : Brushes.Transparent;
            visual.BorderBrush = selected ? new SolidColorBrush(Color.FromArgb(210, 51, 136, 255)) : Brushes.Transparent;
            visual.BorderThickness = selected ? new Thickness(0.8) : new Thickness(0);
        }
    }

    private void PositionTextSelectionToolbar(bool reanchor = false)
    {
        if (_textSelectionAnchor is null || _textSelectionEnd is null || !_textSelectable)
        {
            TextSelectionToolbar.Visibility = Visibility.Collapsed;
            return;
        }
        var layer = LongSelectableTextLayer.Visibility == Visibility.Visible ? LongSelectableTextLayer : SelectableTextLayer;
        if (!_textHitRegions.TryGetValue(layer, out var regions) || regions.Count == 0 || Frame.Child is not Grid root) return;
        var start = Math.Clamp(Math.Min(_textSelectionAnchor.Value, _textSelectionEnd.Value), 0, regions.Count - 1);
        var end = Math.Clamp(Math.Max(_textSelectionAnchor.Value, _textSelectionEnd.Value), 0, regions.Count - 1);
        var bounds = regions[start];
        for (var index = start + 1; index <= end; index++) bounds.Union(regions[index]);
        TextSelectionToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarSize = TextSelectionToolbar.DesiredSize;
        var below = default(Point);
        var above = default(Point);
        if (reanchor || _textSelectionToolbarPosition is null)
        {
            below = layer.TranslatePoint(new Point(bounds.Left, bounds.Bottom + 6), root);
            above = layer.TranslatePoint(new Point(bounds.Left, bounds.Top - toolbarSize.Height - 6), root);
        }
        _textSelectionToolbarPosition = StableTextToolbarPosition(_textSelectionToolbarPosition, below, above,
            toolbarSize, new Size(root.ActualWidth, root.ActualHeight), reanchor);
        TextSelectionToolbar.Margin = new Thickness(_textSelectionToolbarPosition.Value.X,
            _textSelectionToolbarPosition.Value.Y, 0, 0);
        TextSelectionToolbar.Visibility = Visibility.Visible;
    }

    internal static Point StableTextToolbarPosition(Point? current, Point below, Point above, Size toolbarSize,
        Size viewport, bool reanchor)
    {
        var maximumLeft = Math.Max(4, viewport.Width - toolbarSize.Width - 4);
        var maximumTop = Math.Max(4, viewport.Height - toolbarSize.Height - 4);
        var position = current;
        if (reanchor || position is null)
        {
            var anchorLeft = Math.Clamp(below.X, 4, maximumLeft);
            var anchorTop = below.Y + toolbarSize.Height <= viewport.Height - 4 ? below.Y : Math.Max(4, above.Y);
            position = new Point(anchorLeft, anchorTop);
        }
        return new Point(Math.Clamp(position.Value.X, 4, maximumLeft), Math.Clamp(position.Value.Y, 4, maximumTop));
    }

    private void ClearRecognizedTextSelection()
    {
        StopTextAutoScroll();
        _textSelectionAnchor = _textSelectionEnd = null;
        _textSelectionToolbarPosition = null;
        TextSelectionToolbar.Visibility = Visibility.Collapsed;
        UpdateRecognizedTextSelectionVisuals();
    }

    private string SelectedRecognizedText() => _textSelectionAnchor is int start && _textSelectionEnd is int end
        ? ComposeRecognizedText(_recognizedWords, start, end)
        : string.Empty;

    internal static string ComposeRecognizedText(IReadOnlyList<RecognizedWord> words, int start, int end)
    {
        if (words.Count == 0) return string.Empty;
        var first = Math.Clamp(Math.Min(start, end), 0, words.Count - 1);
        var last = Math.Clamp(Math.Max(start, end), first, words.Count - 1);
        return string.Join(Environment.NewLine, words.Skip(first).Take(last - first + 1)
            .GroupBy(word => word.LineIndex)
            .OrderBy(line => line.Key)
            .Select(ComposeRecognizedLine));
    }

    private static string ComposeRecognizedLine(IEnumerable<RecognizedWord> source)
    {
        var line = source.OrderBy(word => word.X).ToList();
        if (line.Count == 0) return string.Empty;
        var characterWidths = line
            .Where(word => word.Text.Length > 0)
            .Select(word => word.Width / (double)Math.Max(1, word.Text.Length))
            .OrderBy(value => value)
            .ToList();
        var typicalCharacterWidth = characterWidths.Count == 0 ? 5 : characterWidths[characterWidths.Count / 2];
        typicalCharacterWidth = Math.Max(3, typicalCharacterWidth);
        var result = new System.Text.StringBuilder(line[0].Text);
        for (var index = 1; index < line.Count; index++)
        {
            var previous = line[index - 1];
            var gap = line[index].X - (previous.X + previous.Width);
            result.Append(gap >= typicalCharacterWidth * 3.5 ? '\t' : ' ');
            result.Append(line[index].Text);
        }
        return result.ToString();
    }

    private void CopySelectedText()
    {
        CopyRecognizedText(SelectedRecognizedText());
    }

    private void CopySelectedText_Click(object sender, RoutedEventArgs e) => CopySelectedText();

    private void CopyAllRecognizedText_Click(object sender, RoutedEventArgs e)
    {
        CopyRecognizedText(_selectableTextResult?.Text?.Trim());
    }

    private void CancelTextSelection_Click(object sender, RoutedEventArgs e)
    {
        ClearRecognizedTextSelection();
        Focus();
        e.Handled = true;
    }

    private void CopyRecognizedText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Clipboard.SetText(text);
        ClearRecognizedTextSelection();
        ShowPinStatus("Copied");
    }

}
