using SnapAnchor.Models;
using SnapAnchor.Windows;
using SnapAnchor.Services;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SnapAnchor.Controls;

public partial class HistoryView : UserControl
{
    public event EventHandler? RepeatLastRequested;
    private readonly HashSet<string> _contextPreviewIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HistoryThumbnailCache _thumbnails = new();
    private readonly System.Windows.Threading.DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _reloadCancellation;
    private int PageSize => _workspaceMode ? 6 : 60;
    private bool _workspaceMode;
    private bool _configuring;
    private bool _embedded;
    private string? _selectedId;
    private Window HostWindow => Window.GetWindow(this);
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(HistoryView), new PropertyMetadata(230d));
    public double CardWidth { get => (double)GetValue(CardWidthProperty); private set => SetValue(CardWidthProperty, value); }

    internal void ConfigureWorkspace(bool workspace)
    {
        _workspaceMode = workspace;
        _embedded = true;
        _configuring = true;
        SearchBox.Clear();
        FavoriteOnlyBox.IsChecked = false;
        ShowRecycleBox.IsChecked = false;
        foreach (var box in new[] { SourceFilterBox, TypeFilterBox, DateFilterBox }) box.SelectedValue = "All";
        _configuring = false;
        HeadingText.Visibility = Visibility.Collapsed;
        PaginationPanel.Visibility = workspace ? Visibility.Collapsed : Visibility.Visible;
        FilterToggle.IsChecked = false;
        if (IsLoaded) Reload();
    }
    internal void Refresh() { if (IsLoaded) BeginReload(false); }

    private void HistoryList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var available = Math.Max(160, e.NewSize.Width - 20);
        var columns = Math.Max(1, (int)((available + 14) / 238));
        CardWidth = Math.Max(140, Math.Floor(available / columns) - 14);
    }
    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = HistoryList.SelectedItem as HistoryViewItem;
        _selectedId = selected?.Id;
        SelectedActions.IsEnabled = selected is not null;
        SelectionText.Text = selected?.Title ?? L("Select a capture");
    }
    private void OpenLibraryMenu_Click(object sender, RoutedEventArgs e) => OpenMenu((Button)sender, null);
    private void OpenItemMenu_Click(object sender, RoutedEventArgs e) => OpenMenu((Button)sender, HistoryList.SelectedItem);
    private static void OpenMenu(Button button, object? item)
    {
        if (button.ContextMenu is not { } menu) return;
        menu.DataContext = item;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
    private int _page;
    private int _pageCount = 1;
    private readonly Dictionary<string, CaptureRecord> _records = new();

    public HistoryView()
    {
        ApplicationThemeService.EnsureResources(this);
        InitializeComponent();
        var settings = SettingsService.Load();
        LocalizationService.Apply(this, settings.UiLanguage);
        AccessibilityService.Apply(this);
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Reload(); };
        Unloaded += (_, _) => { _searchTimer.Stop(); _reloadCancellation?.Cancel(); };
        Loaded += (_, _) =>
        {
            Reload();
            if (_embedded) return;
            Dispatcher.BeginInvoke(() =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }, System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void Reload() => BeginReload(resetPage: true);

    private async void BeginReload(bool resetPage) => await ReloadAsync(resetPage);

    internal async Task ReloadAsync(bool resetPage)
    {
        _searchTimer.Stop();
        _reloadCancellation?.Cancel();
        var cancellation = _reloadCancellation = new CancellationTokenSource();
        if (resetPage) _page = 0;
        try
        {
            var showDeleted = ShowRecycleBox.IsChecked == true;
            var loaded = await Task.Run(() => HistoryService.List(includeDeleted: showDeleted), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            var records = loaded
                .Where(record => record.IsDeleted == showDeleted)
                // Favorites first, then newest — keeps power-user pins easy to re-open.
                .OrderByDescending(record => !_workspaceMode && record.IsFavorite)
                .ThenByDescending(record => record.CreatedAt)
                .ToList();
            _records.Clear();
            foreach (var record in records) _records[record.Id] = record;
            _contextPreviewIds.RemoveWhere(id => !_records.TryGetValue(id, out var record) || !record.HasContext);
            var filtered = records.Where(MatchesFilters).ToList();
            _pageCount = _workspaceMode ? 1 : Math.Max(1, (filtered.Count + PageSize - 1) / PageSize);
            _page = Math.Clamp(_page, 0, _pageCount - 1);
            var page = filtered.Skip(_page * PageSize).Take(PageSize).ToList();
            var view = page.Select(record => new HistoryViewItem
            {
                Id = record.Id,
                SizeLabel = record.IsRecording
                    ? $"{record.Width} x {record.Height} - {record.MediaKind} {TimeSpan.FromMilliseconds(record.DurationMilliseconds):mm\\:ss}"
                    : $"{record.Width} x {record.Height}",
                TimeLabel = record.CreatedAt.ToString("MMM d  HH:mm:ss"),
                SourceLabel = SourceLabel(record),
                RecognitionLabel = RecognitionLabel(record),
                CanRecognize = !record.IsRecording,
                EditLabel = L(record.IsRecording ? "Open" : "Edit"),
                ContextLabel = L(_contextPreviewIds.Contains(record.Id) ? "Show result" : "Show context"),
                ContextVisibility = record.HasContext ? Visibility.Visible : Visibility.Collapsed,
                Title = string.IsNullOrWhiteSpace(record.Title) ? SourceLabel(record) : record.Title,
                FavoriteGlyph = record.IsFavorite ? "★" : "",
                ActiveVisibility = record.IsDeleted ? Visibility.Collapsed : Visibility.Visible,
                DeletedVisibility = record.IsDeleted ? Visibility.Visible : Visibility.Collapsed
            }).ToList();
            var selectedId = _selectedId;
            HistoryList.ItemsSource = view;
            HistoryList.SelectedItem = view.FirstOrDefault(item => item.Id == selectedId) ?? view.FirstOrDefault();
            EmptyState.Visibility = view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PreviousPageButton.IsEnabled = _page > 0;
            NextPageButton.IsEnabled = _page + 1 < _pageCount;
            PageText.Text = $"{_page + 1} / {_pageCount}";
            SummaryText.Text = filtered.Count == records.Count
                ? records.Count == 1 ? L("1 saved item") : LocalizationService.Format("{0} saved items", records.Count)
                : LocalizationService.Format("{0} of {1} saved items", filtered.Count, records.Count);
            for (var index = 0; index < page.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var record = page[index];
                var context = _contextPreviewIds.Contains(record.Id);
                var image = await Task.Run(() => _thumbnails.Load(record, context), cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                view[index].Thumbnail = image;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (ReferenceEquals(_reloadCancellation, cancellation)) SummaryText.Text = ex.Message; }
        finally
        {
            if (ReferenceEquals(_reloadCancellation, cancellation)) _reloadCancellation = null;
            cancellation.Dispose();
        }
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e) { if (_page > 0) { _page--; BeginReload(false); } }
    private void NextPage_Click(object sender, RoutedEventArgs e) { if (_page + 1 < _pageCount) { _page++; BeginReload(false); } }

    private bool MatchesFilters(CaptureRecord record)
    {
        var source = SourceKind(record);
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
        if (IsLoaded && !_configuring) { _reloadCancellation?.Cancel(); _searchTimer.Stop(); _searchTimer.Start(); }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_configuring) Reload();
    }

    private BitmapSource PreviewImage(CaptureRecord record, int decodeWidth = 0)
        => _contextPreviewIds.Contains(record.Id)
            ? (decodeWidth > 0 ? HistoryService.LoadContextPreview(record, decodeWidth) : HistoryService.LoadContextImage(record)) ?? HistoryService.LoadImage(record, decodeWidth)
            : HistoryService.LoadImage(record, decodeWidth);

    private void ToggleContext_Click(object sender, RoutedEventArgs e)
    {
        if (RecordFrom(sender) is not { HasContext: true } record) return;
        if (!_contextPreviewIds.Add(record.Id)) _contextPreviewIds.Remove(record.Id);
        BeginReload(false);
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
        if (MessageBox.Show(HostWindow, L("Move this item to the recycle bin?"), L("SnapAnchor History"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
        var title = TextPromptWindow.Ask(HostWindow, L("Rename history item"), L("Name"), record.Title);
        if (title is null) return;
        var tagsText = TextPromptWindow.Ask(HostWindow, L("Organize history item"), L("Tags (comma separated)"), string.Join(", ", record.Tags));
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
        if (MessageBox.Show(HostWindow, L("Permanently delete this item and its files?"), L("SnapAnchor History"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        HistoryService.DeletePermanently(record.Id);
        Reload();
    }

    private void EmptyRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(HostWindow, L("Permanently delete every item in the recycle bin?"), L("SnapAnchor History"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
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

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(HistoryService.HistoryDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{HistoryService.HistoryDirectory}\"") { UseShellExecute = true });
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(HostWindow, L("Move every active item to the recycle bin?"), L("SnapAnchor History"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        HistoryService.Clear();
        _contextPreviewIds.Clear();
        Reload();
    }

    private CaptureRecord? RecordFrom(object sender)
    {
        var id = sender switch
        {
            FrameworkElement { Tag: string value } => value,
            _ => null
        };
        return id is not null && _records.TryGetValue(id, out var record) ? record : null;
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

    private static string SourceLabel(CaptureRecord record) => L(SourceKind(record));

    private static string SourceKind(CaptureRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.SourceKind)) return record.SourceKind;
        if (record.IsRecording) return "Recording";
        if (!string.IsNullOrWhiteSpace(record.RecognizedText) || !string.IsNullOrWhiteSpace(record.BarcodeText)) return "OCR";
        if (record.HasEditableAnnotations) return "Edited";
        return "Capture";
    }

    private static string L(string value) => LocalizationService.Current(value);

    private sealed class HistoryViewItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private BitmapSource? _thumbnail;
        public string Id { get; init; } = string.Empty;
        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
        }
        public string SizeLabel { get; init; } = string.Empty;
        public string TimeLabel { get; init; } = string.Empty;
        public string SourceLabel { get; init; } = string.Empty;
        public string RecognitionLabel { get; init; } = string.Empty;
        public bool CanRecognize { get; init; }
        public string EditLabel { get; init; } = "Edit";
        public string ContextLabel { get; init; } = "Show context";
        public Visibility ContextVisibility { get; init; }
        public string Title { get; init; } = string.Empty;
        public string FavoriteGlyph { get; init; } = "☆";
        public Visibility ActiveVisibility { get; init; } = Visibility.Visible;
        public Visibility DeletedVisibility { get; init; } = Visibility.Collapsed;
    }
}
