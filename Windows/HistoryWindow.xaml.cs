using SnapAnchor.Models;
using SnapAnchor.Services;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SnapAnchor.Windows;

public partial class HistoryWindow : Window
{
    public event EventHandler? RepeatLastRequested;
    private readonly HashSet<string> _contextPreviewIds = new(StringComparer.OrdinalIgnoreCase);

    public HistoryWindow()
    {
        InitializeComponent();
        DpiLayoutService.Attach(this);
        var settings = SettingsService.Load();
        LocalizationService.Apply(this, settings.UiLanguage);
        AccessibilityService.Apply(this);
        Loaded += (_, _) =>
        {
            Reload();
            Dispatcher.BeginInvoke(() =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }, System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void Reload()
    {
        var showDeleted = ShowRecycleBox.IsChecked == true;
        var records = HistoryService.List(includeDeleted: showDeleted)
            .Where(record => record.IsDeleted == showDeleted)
            // Favorites first, then newest — keeps power-user pins easy to re-open.
            .OrderByDescending(record => record.IsFavorite)
            .ThenByDescending(record => record.CreatedAt)
            .ToList();
        _contextPreviewIds.RemoveWhere(id => records.All(record => !record.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || !record.HasContext));
        var filtered = records.Where(MatchesFilters).ToList();
        HistoryList.ItemsSource = filtered.Select(record => new HistoryViewItem
        {
            Id = record.Id,
            Thumbnail = PreviewImage(record, 240),
            SizeLabel = record.IsRecording
                ? $"{record.Width} x {record.Height} - {record.MediaKind} {TimeSpan.FromMilliseconds(record.DurationMilliseconds):mm\\:ss}"
                : $"{record.Width} x {record.Height}",
            TimeLabel = record.CreatedAt.ToString("MMM d  HH:mm:ss"),
            SourceLabel = SourceLabel(record),
            RecognitionLabel = RecognitionLabel(record),
            EditLabel = L(record.IsRecording ? "Open" : "Edit"),
            ContextLabel = L(_contextPreviewIds.Contains(record.Id) ? "Show result" : "Show context"),
            ContextVisibility = record.HasContext ? Visibility.Visible : Visibility.Collapsed,
            Title = string.IsNullOrWhiteSpace(record.Title) ? SourceLabel(record) : record.Title,
            FavoriteGlyph = record.IsFavorite ? "★" : "☆",
            ActiveVisibility = record.IsDeleted ? Visibility.Collapsed : Visibility.Visible,
            DeletedVisibility = record.IsDeleted ? Visibility.Visible : Visibility.Collapsed
        }).ToList();
        SummaryText.Text = filtered.Count == records.Count
            ? records.Count == 1 ? L("1 saved item") : LocalizationService.Format("{0} saved items", records.Count)
            : LocalizationService.Format("{0} of {1} saved items", filtered.Count, records.Count);
    }

    private bool MatchesFilters(CaptureRecord record)
    {
        var source = SourceLabel(record);
        var sourceFilter = SelectedTag(SourceFilterBox);
        if (!sourceFilter.Equals("All", StringComparison.OrdinalIgnoreCase) && !source.Equals(sourceFilter, StringComparison.OrdinalIgnoreCase)) return false;

        var typeFilter = SelectedTag(TypeFilterBox);
        if (typeFilter switch
        {
            "Images" => record.IsRecording,
            "Recordings" => !record.IsRecording,
            "OCR" => string.IsNullOrWhiteSpace(record.RecognizedText) && string.IsNullOrWhiteSpace(record.BarcodeText),
            "Annotated" => !record.HasEditableAnnotations,
            "Context" => !record.HasContext,
            _ => false
        }) return false;

        var earliest = SelectedTag(DateFilterBox) switch
        {
            "Today" => DateTime.Today,
            "7" => DateTime.Now.AddDays(-7),
            "30" => DateTime.Now.AddDays(-30),
            _ => DateTime.MinValue
        };
        if (record.CreatedAt < earliest) return false;
        if (FavoriteOnlyBox.IsChecked == true && !record.IsFavorite) return false;

        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return true;
        var searchable = string.Join(' ', record.Id, record.Title, string.Join(' ', record.Tags), source, record.Width, record.Height,
            record.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), record.RecognizedText, record.BarcodeText, record.BarcodeFormat, record.MediaKind);
        return searchable.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string SelectedTag(ComboBox box) => box.SelectedValue as string ?? "All";

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) Reload();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Reload();
    }

    private BitmapSource PreviewImage(CaptureRecord record, int decodeWidth = 0)
        => _contextPreviewIds.Contains(record.Id)
            ? (decodeWidth > 0 ? HistoryService.LoadContextPreview(record, decodeWidth) : HistoryService.LoadContextImage(record)) ?? HistoryService.LoadImage(record, decodeWidth)
            : HistoryService.LoadImage(record, decodeWidth);

    private void ToggleContext_Click(object sender, RoutedEventArgs e)
    {
        if (RecordFrom(sender) is not { HasContext: true } record) return;
        if (!_contextPreviewIds.Add(record.Id)) _contextPreviewIds.Remove(record.Id);
        Reload();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (RecordFrom(sender) is not { } record) return;
        if (record.IsRecording && HistoryService.MediaPath(record) is { } mediaPath)
            Clipboard.SetFileDropList(new StringCollection { mediaPath });
        else
            Clipboard.SetImage(PreviewImage(record));
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (RecordFrom(sender) is not { } record) return;
        var showingContext = _contextPreviewIds.Contains(record.Id);
        new PinnedImageWindow(PreviewImage(record), historyRecordId: showingContext ? null : record.Id).Show();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (RecordFrom(sender) is not { } record) return;
        if (record.IsRecording && HistoryService.MediaPath(record) is { } mediaPath)
            Process.Start(new ProcessStartInfo(mediaPath) { UseShellExecute = true });
        else
            new PinnedImageWindow(HistoryService.LoadImage(record), startEditing: true, historyRecordId: record.Id).Show();
    }

    private void Recognize_Click(object sender, RoutedEventArgs e)
    {
        if (RecordFrom(sender) is not { } record || record.IsRecording) return;
        var pin = new PinnedImageWindow(HistoryService.LoadImage(record), historyRecordId: record.Id);
        pin.Show();
        pin.BeginRecognitionMode();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RecordFrom(sender) is not { } record) return;
        if (MessageBox.Show(this, L("Move this item to the recycle bin?"), L("SnapAnchor History"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        HistoryService.Delete(record.Id);
        Reload();
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (RecordFrom(sender) is not { } record) return;
        HistoryService.UpdateMetadata(record.Id, favorite: !record.IsFavorite);
        Reload();
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (RecordFrom(sender) is not { } record) return;
        var title = TextPromptWindow.Ask(this, L("Rename history item"), L("Name"), record.Title);
        if (title is null) return;
        var tagsText = TextPromptWindow.Ask(this, L("Organize history item"), L("Tags (comma separated)"), string.Join(", ", record.Tags));
        if (tagsText is null) return;
        var tags = tagsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        HistoryService.UpdateMetadata(record.Id, title: title, tags: tags);
        Reload();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (RecordFrom(sender) is not { } record) return;
        HistoryService.Restore(record.Id);
        Reload();
    }

    private void DeleteForever_Click(object sender, RoutedEventArgs e)
    {
        if (RecordFrom(sender) is not { } record) return;
        if (MessageBox.Show(this, L("Permanently delete this item and its files?"), L("SnapAnchor History"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        HistoryService.DeletePermanently(record.Id);
        Reload();
    }

    private void EmptyRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, L("Permanently delete every item in the recycle bin?"), L("SnapAnchor History"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        HistoryService.EmptyRecycleBin();
        Reload();
    }

    private void Thumbnail_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || RecordFrom(sender) is not { } record) return;
        if (record.IsRecording && HistoryService.MediaPath(record) is { } mediaPath)
            Process.Start(new ProcessStartInfo(mediaPath) { UseShellExecute = true });
        else
        {
            var showingContext = _contextPreviewIds.Contains(record.Id);
            new PinnedImageWindow(PreviewImage(record), historyRecordId: showingContext ? null : record.Id).Show();
        }
    }

    private void Repeat_Click(object sender, RoutedEventArgs e) => RepeatLastRequested?.Invoke(this, EventArgs.Empty);

    private void BackToDashboard_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow dashboard)
        {
            dashboard.Show();
            dashboard.WindowState = WindowState.Normal;
            dashboard.Activate();
        }
        Close();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(HistoryService.HistoryDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{HistoryService.HistoryDirectory}\"") { UseShellExecute = true });
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, L("Move every active item to the recycle bin?"), L("SnapAnchor History"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        HistoryService.Clear();
        _contextPreviewIds.Clear();
        Reload();
    }

    private static CaptureRecord? RecordFrom(object sender)
    {
        var id = sender switch
        {
            Button { Tag: string value } => value,
            Image { Tag: string value } => value,
            _ => null
        };
        return id is null ? null : HistoryService.Find(id);
    }

    private static string RecognitionLabel(CaptureRecord record)
    {
        var labels = new List<string>();
        if (!string.IsNullOrWhiteSpace(record.RecognizedText)) labels.Add(L("OCR text"));
        if (!string.IsNullOrWhiteSpace(record.BarcodeText)) labels.Add(string.IsNullOrWhiteSpace(record.BarcodeFormat) ? L("Code") : record.BarcodeFormat);
        if (record.HasEditableAnnotations) labels.Add(L("Editable layers"));
        if (record.IsRecording) labels.Add(LocalizationService.Format("{0} recording · {1} frames", record.MediaKind, record.FrameCount));
        return string.Join(" · ", labels);
    }

    private static string SourceLabel(CaptureRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.SourceKind)) return record.SourceKind;
        if (record.IsRecording) return L("Recording");
        if (!string.IsNullOrWhiteSpace(record.RecognizedText) || !string.IsNullOrWhiteSpace(record.BarcodeText)) return "OCR";
        if (record.HasEditableAnnotations) return L("Edited");
        return L("Capture");
    }

    private static string L(string value) => LocalizationService.Current(value);

    private sealed class HistoryViewItem
    {
        public string Id { get; init; } = string.Empty;
        public BitmapSource? Thumbnail { get; init; }
        public string SizeLabel { get; init; } = string.Empty;
        public string TimeLabel { get; init; } = string.Empty;
        public string SourceLabel { get; init; } = string.Empty;
        public string RecognitionLabel { get; init; } = string.Empty;
        public string EditLabel { get; init; } = "Edit";
        public string ContextLabel { get; init; } = "Show context";
        public Visibility ContextVisibility { get; init; }
        public string Title { get; init; } = string.Empty;
        public string FavoriteGlyph { get; init; } = "☆";
        public Visibility ActiveVisibility { get; init; } = Visibility.Visible;
        public Visibility DeletedVisibility { get; init; } = Visibility.Collapsed;
    }
}
