using SnapAnchor.Models;
using SnapAnchor.Services;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace SnapAnchor.Controls;

internal sealed class RecognitionCompletedEventArgs(RecognitionResult result) : EventArgs
{
    internal RecognitionResult Result { get; } = result;
}

public partial class RecognitionControl : UserControl
{
    private BitmapSource? _image;
    private RecognitionResult? _result;
    private CancellationTokenSource? _cancellation;
    private bool _loaded;

    internal event EventHandler<RecognitionCompletedEventArgs>? RecognitionCompleted;
    internal event EventHandler? Cancelled;

    public RecognitionControl()
    {
        InitializeComponent();
        LanguageBox.SelectedValue = SettingsService.Load().OcrLanguage;
        if (LanguageBox.SelectedIndex < 0) LanguageBox.SelectedIndex = 3;
        Unloaded += (_, _) => _cancellation?.Cancel();
    }

    public async Task LoadImageAsync(BitmapSource image)
    {
        _image = image;
        PreviewImage.Source = image;
        _loaded = true;
        await ScanAsync();
    }

    internal void CancelCurrent() => _cancellation?.Cancel();

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (!_loaded || _image is null) return;
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        CopyButton.IsEnabled = SearchButton.IsEnabled = OpenButton.IsEnabled = false;
        StatusText.Text = L("Recognizing locally…");
        ResultBox.Text = string.Empty;

        try
        {
            var language = LanguageBox.SelectedValue as string ?? "eng";
            _result = await RecognitionService.RecognizeAsync(_image, language, _cancellation.Token);
            ResultBox.Text = FormatResult(_result);
            var content = _result.HasContent;
            CopyButton.IsEnabled = content;
            SearchButton.IsEnabled = !string.IsNullOrWhiteSpace(_result.Text);
            OpenButton.IsEnabled = TryGetWebUri(_result, out _);
            StatusText.Text = content
                ? BuildStatus(_result)
                : string.IsNullOrWhiteSpace(_result.ErrorMessage) ? L("No text or code was found.") : _result.ErrorMessage;
            RecognitionCompleted?.Invoke(this, new RecognitionCompletedEventArgs(_result));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L("Recognition cancelled.");
        }
        catch (Exception ex)
        {
            StatusText.Text = LocalizationService.Format("Recognition failed: {0}", DeepestMessage(ex));
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private static string FormatResult(RecognitionResult result)
    {
        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.Text)) sections.Add(result.Text.Trim());
        if (!string.IsNullOrWhiteSpace(result.BarcodeText))
            sections.Add($"[{(string.IsNullOrWhiteSpace(result.BarcodeFormat) ? "CODE" : result.BarcodeFormat)}]{Environment.NewLine}{result.BarcodeText.Trim()}");
        return string.Join($"{Environment.NewLine}{Environment.NewLine}", sections);
    }

    private static string BuildStatus(RecognitionResult result)
    {
        var pieces = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.Text)) pieces.Add(LocalizationService.Format("{0} text characters", result.Text.Length.ToString("N0")));
        if (!string.IsNullOrWhiteSpace(result.BarcodeText)) pieces.Add(string.IsNullOrWhiteSpace(result.BarcodeFormat) ? L("code found") : LocalizationService.Format("{0} found", result.BarcodeFormat));
        pieces.Add(L("saved with capture history"));
        return string.Join(" • ", pieces);
    }

    private static string DeepestMessage(Exception exception)
    {
        while (exception.InnerException is not null) exception = exception.InnerException;
        return exception.Message;
    }

    private static string L(string value) => LocalizationService.Current(value);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ResultBox.Text)) Clipboard.SetText(ResultBox.Text);
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        var text = _result?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        var query = text.Length > 500 ? text[..500] : text;
        Process.Start(new ProcessStartInfo($"https://www.bing.com/search?q={Uri.EscapeDataString(query)}") { UseShellExecute = true });
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetWebUri(_result, out var uri))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static bool TryGetWebUri(RecognitionResult? result, out Uri uri)
    {
        uri = null!;
        var candidates = new[] { result?.BarcodeText, result?.Text };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var firstLine = candidate.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (Uri.TryCreate(firstLine, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
            {
                uri = parsed;
                return true;
            }
        }
        return false;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CancelCurrent();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        var settings = SettingsService.Load();
        settings.OcrLanguage = LanguageBox.SelectedValue as string ?? "eng";
        SettingsService.Save(settings);
    }
}
