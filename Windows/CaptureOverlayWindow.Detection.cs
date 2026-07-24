using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using SnapAnchor.Services;

namespace SnapAnchor.Windows;

public partial class CaptureOverlayWindow
{
    private void UpdateDetectedRegion(Point overlayPoint)
    {
        if (_settings.ShowElementDetection == false || _colorPickerMode || _annotationMode || _recordingRunning)
        {
            ClearDetectionHighlight();
            return;
        }

        if (_overlayHandle == IntPtr.Zero)
        {
            _overlayHandle = new WindowInteropHelper(this).Handle;
            if (_overlayHandle == IntPtr.Zero) return;
        }

        // Use the physical cursor position for hit-testing. Window/UI Automation
        // geometry is also physical-pixel based (GetWindowRect / BoundingRectangle).
        var screenPoint = NativeMethods.GetCursorPos(out var cursor)
            ? new Point(cursor.X, cursor.Y)
            : OverlayToScreen(overlayPoint);

        // Window-only mode is pure geometry — keep it very smooth.
        var minMove = _detectElements ? 2.0 : 3.0;
        var movedLittle = !double.IsNaN(_lastDetectionScreenPoint.X) &&
            Math.Abs(screenPoint.X - _lastDetectionScreenPoint.X) < minMove &&
            Math.Abs(screenPoint.Y - _lastDetectionScreenPoint.Y) < minMove;
        if (movedLittle && _detectionCandidates.Count > 0 && _detectionClock.ElapsedMilliseconds < (_detectElements ? 20 : 12))
            return;

        // Stay lit without re-layout while the pointer is still inside the hole.
        if (_detectionCandidates.Count > 0 &&
            !_detectedSelection.IsEmpty &&
            _detectedSelection.Contains(overlayPoint) &&
            _detectionClock.ElapsedMilliseconds < (_detectElements ? 45 : 100))
            return;

        _lastDetectionOverlayPoint = overlayPoint;
        _lastDetectionScreenPoint = screenPoint;
        _detectionClock.Restart();

        _detectionCandidates = ElementDetectionService.DetectHierarchy(screenPoint, _overlayHandle, _detectElements);
        if (_detectionCandidates.Count == 0)
        {
            ClearDetectionHighlight();
            return;
        }

        _detectionIndex = _detectElements ? _detectionCandidates.Count - 1 : 0;
        RenderDetectedCandidate();
    }

    private void RenderDetectedCandidate()
    {
        if (_detectionCandidates.Count == 0) return;
        _detectionIndex = Math.Clamp(_detectionIndex, 0, _detectionCandidates.Count - 1);
        var detected = _detectionCandidates[_detectionIndex];

        var topLeft = ScreenToOverlay(new Point(detected.Bounds.Left, detected.Bounds.Top));
        var bottomRight = ScreenToOverlay(new Point(detected.Bounds.Right, detected.Bounds.Bottom));
        var next = Rect.Intersect(
            new Rect(0, 0, ActualWidth, ActualHeight),
            new Rect(topLeft, bottomRight));
        if (next.IsEmpty || next.Width < 2 || next.Height < 2)
        {
            ClearDetectionHighlight();
            return;
        }

        // Skip mask rebuild when the lit region did not meaningfully change.
        var sameRegion = !_detectedSelection.IsEmpty &&
            Math.Abs(_detectedSelection.X - next.X) < 1 &&
            Math.Abs(_detectedSelection.Y - next.Y) < 1 &&
            Math.Abs(_detectedSelection.Width - next.Width) < 1 &&
            Math.Abs(_detectedSelection.Height - next.Height) < 1;
        _detectedSelection = next;
        if (!sameRegion)
        {
            RenderSpotlightMask(_detectedSelection);
            Canvas.SetLeft(DetectionRect, _detectedSelection.X);
            Canvas.SetTop(DetectionRect, _detectedSelection.Y);
            DetectionRect.Width = _detectedSelection.Width;
            DetectionRect.Height = _detectedSelection.Height;
            DetectionRect.Visibility = Visibility.Visible;
        }
        else if (DetectionRect.Visibility != Visibility.Visible)
        {
            DetectionRect.Visibility = Visibility.Visible;
        }

        DetectionModeText.Text = $"{L("Mode")}: {L(_detectElements ? "UI element" : "Window")}   •   {_detectionIndex + 1}/{_detectionCandidates.Count}   •   {detected.Name}";
    }

    private void ClearDetectionHighlight()
    {
        _detectedSelection = Rect.Empty;
        DetectionRect.Visibility = Visibility.Collapsed;
        if (_selection.Width < 3 && !_dragging && !_resizing && !_annotationMode && !_recordingRunning)
        {
            SelectionMask.Visibility = Visibility.Collapsed;
            MaskLayer.Visibility = Visibility.Visible;
        }
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_annotationMode) return;
        if (_settings.ShowElementDetection == false || _selection.Width >= 3 || _dragging || _resizing || _detectionCandidates.Count <= 1) return;
        _detectionIndex = Math.Clamp(_detectionIndex + (e.Delta < 0 ? 1 : -1), 0, _detectionCandidates.Count - 1);
        RenderDetectedCandidate();
        e.Handled = true;
    }
}
