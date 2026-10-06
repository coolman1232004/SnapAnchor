using Microsoft.Win32;
using SnapAnchor.Models;
using SnapAnchor.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Shapes = System.Windows.Shapes;


namespace SnapAnchor.Controls;

public partial class AnnotationEditorControl
{
    private System.Windows.Threading.DispatcherTimer? _styleSaveTimer;
    private void QueueStyleSave()
    {
        if (_styleSaveTimer is null)
        {
            _styleSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _styleSaveTimer.Tick += (_, _) => { _styleSaveTimer.Stop(); SettingsService.Save(_settings); };
            Unloaded += (_, _) =>
            {
                if (_styleSaveTimer.IsEnabled) { _styleSaveTimer.Stop(); SettingsService.Save(_settings); }
            };
        }
        _styleSaveTimer.Stop();
        _styleSaveTimer.Start();
    }
    private void LoadStyleSettings()
    {
        _fillEnabled = _settings.AnnotationFillEnabled;
        _dashed = _settings.AnnotationDashed;
        FillOpacityBox.SelectedValue = _settings.AnnotationFillOpacity.ToString();
        if (FillOpacityBox.SelectedIndex < 0) FillOpacityBox.SelectedValue = "20";
        ArrowHeadsBox.SelectedValue = _settings.AnnotationArrowHeads;
        if (ArrowHeadsBox.SelectedIndex < 0) ArrowHeadsBox.SelectedValue = "End";
        EffectShapeBox.SelectedValue = "Rectangle";
        ObjectOpacitySlider.Value = _settings.AnnotationOpacity;
        StylePresetBox.SelectedValue = _settings.AnnotationStylePreset;
        if (StylePresetBox.SelectedIndex < 0) StylePresetBox.SelectedValue = "Outline";
        _textBold = _settings.AnnotationTextBold;
        _textItalic = _settings.AnnotationTextItalic;
        _textUnderline = _settings.AnnotationTextUnderline;
        _textStrikethrough = _settings.AnnotationTextStrikethrough;
        _textAlignment = Enum.TryParse<TextAlignment>(_settings.AnnotationTextAlignment, out var alignment) ? alignment : TextAlignment.Left;
        _textOutline = _settings.AnnotationTextOutline;
        TextFontBox.SelectedValue = _settings.AnnotationFontFamily;
        if (TextFontBox.SelectedIndex < 0) TextFontBox.SelectedValue = "Segoe UI";
        TextSizeBox.SelectedValue = Math.Clamp((int)Math.Round(ThicknessSlider.Value * 4), 9, 72).ToString();
        if (TextSizeBox.SelectedIndex < 0) TextSizeBox.SelectedValue = "16";
        SetSelectedColor(_selectedColor);
        UpdateStyleButtons();
        UpdateToolSizeLabels();
        _loadingStyle = false;
    }

    private void ApplyCurrentStyle(AnnotationItem item)
    {
        item.Opacity = ObjectOpacitySlider.Value / 100.0;
        item.FillOpacity = _fillEnabled ? SelectedFillOpacity() : 0;
        item.Dashed = _dashed;
        item.ArrowHeads = ArrowHeadsBox.SelectedValue as string ?? "End";
        item.EffectShape = EffectShapeBox.SelectedValue as string ?? "Rectangle";
        if (item.Kind is AnnotationKind.Text or AnnotationKind.Callout)
        {
            item.FontFamily = TextFontBox.SelectedValue as string ?? "Segoe UI";
            item.Bold = _textBold;
            item.Italic = _textItalic;
            item.Underline = _textUnderline;
            item.Strikethrough = _textStrikethrough;
            item.TextAlignment = _textAlignment.ToString();
            item.Outline = _textOutline;
        }
    }

    private double SelectedFillOpacity() =>
        double.TryParse(FillOpacityBox.SelectedValue as string, out var value) ? Math.Clamp(value / 100.0, 0, 1) : 0.2;

    private void SyncStyleControls(AnnotationItem item)
    {
        _loadingStyle = true;
        SetSelectedColor(item.Color);
        ThicknessSlider.Value = item.Thickness;
        _fillEnabled = item.FillOpacity > 0;
        _dashed = item.Dashed;
        var fillPercent = new[] { 0, 20, 40, 65 }.OrderBy(value => Math.Abs(value - item.FillOpacity * 100)).First();
        FillOpacityBox.SelectedValue = fillPercent.ToString();
        ArrowHeadsBox.SelectedValue = item.ArrowHeads;
        EffectShapeBox.SelectedValue = item.EffectShape;
        ObjectOpacitySlider.Value = item.Opacity * 100;
        _textBold = item.Bold;
        _textItalic = item.Italic;
        _textUnderline = item.Underline;
        _textStrikethrough = item.Strikethrough;
        _textAlignment = Enum.TryParse<TextAlignment>(item.TextAlignment, out var alignment) ? alignment : TextAlignment.Left;
        _textOutline = item.Outline;
        TextFontBox.SelectedValue = item.FontFamily;
        TextSizeBox.SelectedValue = Math.Clamp((int)Math.Round(item.Thickness * 4), 9, 72).ToString();
        UpdateStyleButtons();
        UpdateToolSizeLabels();
        _loadingStyle = false;
    }

    private void StyleControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStyle) return;
        if (sender == ColorBox && ColorBox.SelectedValue is string color)
        {
            _selectedColor = color;
            SelectedColorFill.Background = BrushFrom(color);
        }
        _toolSizes[ToolSizeKey(_tool)] = ThicknessSlider.Value;
        UpdateToolSizeLabels();
        if (sender == ThicknessSlider && _tool is "Eraser" or "Blur") UpdateSurfaceCursor(_tool);
        ApplyStyleToSelected();
        SaveStyleSettings();
    }

    private void FillToggle_Click(object sender, RoutedEventArgs e)
    {
        _fillEnabled = !_fillEnabled;
        UpdateStyleButtons();
        ApplyStyleToSelected();
        SaveStyleSettings();
    }

    private void DashToggle_Click(object sender, RoutedEventArgs e)
    {
        _dashed = !_dashed;
        UpdateStyleButtons();
        ApplyStyleToSelected();
        SaveStyleSettings();
    }

    private void StylePreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStyle) return;
        _loadingStyle = true;
        var preset = StylePresetBox.SelectedValue as string ?? "Outline";
        switch (preset)
        {
            case "Filled": _fillEnabled = true; _dashed = false; FillOpacityBox.SelectedValue = "40"; ObjectOpacitySlider.Value = 100; break;
            case "Dashed": _fillEnabled = false; _dashed = true; ObjectOpacitySlider.Value = 100; break;
            case "Emphasis": _fillEnabled = true; _dashed = false; FillOpacityBox.SelectedValue = "20"; ObjectOpacitySlider.Value = 100; ThicknessSlider.Value = Math.Max(6, ThicknessSlider.Value); break;
            default: _fillEnabled = false; _dashed = false; ObjectOpacitySlider.Value = 100; break;
        }
        UpdateStyleButtons();
        _loadingStyle = false;
        ApplyStyleToSelected();
        SaveStyleSettings();
    }

    private void ApplyStyleToSelected()
    {
        if (_selectedId is not Guid id || _items.FirstOrDefault(item => item.Id == id) is not { } selected) return;
        PushUndo();
        selected.Color = SelectedColor();
        selected.Thickness = ThicknessSlider.Value;
        ApplyCurrentStyle(selected);
        RenderAnnotations();
    }

    private void UpdateStyleButtons()
    {
        FillToggleButton.Background = _fillEnabled ? new SolidColorBrush(Color.FromRgb(214, 231, 255)) : Brushes.Transparent;
        DashToggleButton.Background = _dashed ? new SolidColorBrush(Color.FromRgb(214, 231, 255)) : Brushes.Transparent;
        FillOpacityBox.Visibility = FillToggleButton.Visibility == Visibility.Visible && _fillEnabled ? Visibility.Visible : Visibility.Collapsed;
        TextBoldButton.Background = _textBold ? new SolidColorBrush(Color.FromRgb(214, 231, 255)) : Brushes.Transparent;
        TextItalicButton.Background = _textItalic ? new SolidColorBrush(Color.FromRgb(214, 231, 255)) : Brushes.Transparent;
        TextUnderlineButton.Background = _textUnderline ? new SolidColorBrush(Color.FromRgb(214, 231, 255)) : Brushes.Transparent;
        TextStrikeButton.Background = _textStrikethrough ? new SolidColorBrush(Color.FromRgb(214, 231, 255)) : Brushes.Transparent;
        TextAlignButton.Content = _textAlignment switch { TextAlignment.Center => "≡", TextAlignment.Right => "☷", _ => "☰" };
        TextAlignButton.Background = _textAlignment == TextAlignment.Left ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(214, 231, 255));
        TextOutlineButton.Background = _textOutline ? new SolidColorBrush(Color.FromRgb(214, 231, 255)) : Brushes.Transparent;
        SelectedColorFill.Background = BrushFrom(SelectedColor());
    }

    private void SaveStyleSettings()
    {
        _settings.AnnotationFillEnabled = _fillEnabled;
        _settings.AnnotationFillOpacity = (int)Math.Round(SelectedFillOpacity() * 100);
        _settings.AnnotationOpacity = (int)Math.Round(ObjectOpacitySlider.Value);
        _settings.AnnotationDashed = _dashed;
        _settings.AnnotationArrowHeads = ArrowHeadsBox.SelectedValue as string ?? "End";
        _settings.AnnotationStylePreset = StylePresetBox.SelectedValue as string ?? "Outline";
        _settings.AnnotationFontFamily = TextFontBox.SelectedValue as string ?? "Segoe UI";
        _settings.AnnotationTextBold = _textBold;
        _settings.AnnotationTextItalic = _textItalic;
        _settings.AnnotationTextUnderline = _textUnderline;
        _settings.AnnotationTextStrikethrough = _textStrikethrough;
        _settings.AnnotationTextAlignment = _textAlignment.ToString();
        _settings.AnnotationTextOutline = _textOutline;
        RememberCurrentToolSize();
        _settings.AnnotationShapeSize = _toolSizes["Shape"];
        _settings.AnnotationArrowSize = _toolSizes["Arrow"];
        _settings.AnnotationPencilSize = _toolSizes["Pencil"];
        _settings.AnnotationMarkerSize = _toolSizes["Marker"];
        _settings.AnnotationBlurSize = _toolSizes["Blur"];
        _settings.AnnotationTextSize = _toolSizes["Text"];
        _settings.AnnotationEraserSize = _toolSizes["Eraser"];
        QueueStyleSave();
    }

    private static Brush BrushFrom(string color)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(color)!; }
        catch { return Brushes.Red; }
    }

    private static TextDecorationCollection TextDecorationsFor(bool underline, bool strikethrough)
    {
        var decorations = new TextDecorationCollection();
        if (underline)
            foreach (var decoration in TextDecorations.Underline) decorations.Add(decoration);
        if (strikethrough)
            foreach (var decoration in TextDecorations.Strikethrough) decorations.Add(decoration);
        return decorations;
    }

}
