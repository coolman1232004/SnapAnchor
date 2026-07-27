using Microsoft.Win32;
using SnapAnchor.Models;
using SnapAnchor.Services;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SnapAnchor.Controls;

public partial class AnnotationEditorControl
{
    private void PushUndo()
    {
        _undo.Push(Clone(_items));
        _redo.Clear();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        CommitTextEditor();
        if (_undo.Count == 0) return;
        _redo.Push(Clone(_items));
        _items = _undo.Pop();
        _selectedId = null;
        RenderAnnotations();
    }

    private void Redo()
    {
        CommitTextEditor();
        if (_redo.Count == 0) return;
        _undo.Push(Clone(_items));
        _items = _redo.Pop();
        _selectedId = null;
        RenderAnnotations();
    }

    public BitmapSource Flatten()
    {
        CommitTextEditor();
        var selected = _selectedId;
        var cursorVisibility = ToolCursorLayer.Visibility;
        var backgroundVisibility = BackgroundImage.Visibility;
        try
        {
            _selectedId = null;
            ToolCursorLayer.Visibility = Visibility.Collapsed;
            if (_externalBackgroundMode) BackgroundImage.Visibility = Visibility.Visible;
            RenderAnnotations();
            Surface.Measure(new Size(_source.PixelWidth, _source.PixelHeight));
            Surface.Arrange(new Rect(0, 0, _source.PixelWidth, _source.PixelHeight));
            Surface.UpdateLayout();
            var result = new RenderTargetBitmap(_source.PixelWidth, _source.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            result.Render(Surface);
            result.Freeze();
            return result;
        }
        finally
        {
            BackgroundImage.Visibility = backgroundVisibility;
            _selectedId = selected;
            ToolCursorLayer.Visibility = cursorVisibility;
            RenderAnnotations();
        }
    }

    internal AnnotationAppliedEventArgs SnapshotDocument()
    {
        var image = Flatten();
        return new AnnotationAppliedEventArgs(_source, image, Clone(_items));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var image = Flatten();
        Clipboard.SetImage(image);
        DocumentStored?.Invoke(new AnnotationAppliedEventArgs(_source, image, Clone(_items)));
    }

    private SaveFileDialog CreateImageSaveDialog()
    {
        var folder = Directory.Exists(_settings.QuickSaveFolder) ? _settings.QuickSaveFolder : Path.GetTempPath();
        var suggested = SettingsService.CreateOutputPath(folder, _settings.OutputFileName);
        return new SaveFileDialog
        {
            Filter = CaptureService.ImageSaveFilter,
            DefaultExt = CaptureService.ExtensionForFormat(_settings.OutputFormat),
            FilterIndex = CaptureService.FilterIndexForFormat(_settings.OutputFormat),
            AddExtension = true,
            InitialDirectory = Path.GetDirectoryName(suggested) ?? folder,
            FileName = Path.GetFileName(suggested)
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateImageSaveDialog();
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            var image = Flatten();
            CaptureService.SaveImage(image, dialog.FileName, _settings.ImageQuality, _settings);
            DocumentStored?.Invoke(new AnnotationAppliedEventArgs(_source, image, Clone(_items)));
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var image = Flatten();
        Clipboard.SetImage(image);
        Applied?.Invoke(new AnnotationAppliedEventArgs(_source, image, Clone(_items)));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_textEditor is not null && e.OriginalSource is System.Windows.Controls.TextBox) return;

        if (AccessibilityService.TryHandleToolbarNavigation(e, () => [ToolbarHost]))
            return;

        if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Undo(); e.Handled = true; }
        else if (e.Key == Key.Y && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Redo(); e.Handled = true; }
        else if (e.Key == Key.D && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { DuplicateSelected(); e.Handled = true; }
        else if (e.Key == Key.OemCloseBrackets && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { ReorderSelected(1); e.Handled = true; }
        else if (e.Key == Key.OemOpenBrackets && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { ReorderSelected(-1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && TrySelectToolFromKey(e.Key))
            e.Handled = true;
        else if (_selectedId is not null && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            MoveSelected(e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0,
                e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _selectedId is not null)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private bool TrySelectToolFromKey(Key key)
    {
        var tool = AnnotationToolbarCatalog.ToolForKey(key);
        if (tool is null) return false;
        var button = FindToolButton(tool);
        if (button is null || button.Visibility != Visibility.Visible || !button.IsEnabled) return false;
        ToggleConfiguredTool(tool);
        return true;
    }
}
