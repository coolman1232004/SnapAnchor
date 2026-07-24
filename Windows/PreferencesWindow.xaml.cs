using System.Windows;
using System.IO;
using System.Windows.Controls;
using Forms = System.Windows.Forms;
using SnapAnchor.Services;

namespace SnapAnchor.Windows;

public partial class PreferencesWindow : Window
{
    private readonly AppSettings _settings;
    public event EventHandler? SettingsApplied;

    public PreferencesWindow(AppSettings settings)
    {
        InitializeComponent();
        DpiLayoutService.Attach(this);
        _settings = settings;
        CaptureHotkeyBox.ItemsSource = HotkeyOptions.All;
        CopyHotkeyBox.ItemsSource = HotkeyOptions.All;
        CustomHotkeyBox.ItemsSource = HotkeyOptions.All;
        DrawingHotkeyBox.ItemsSource = HotkeyOptions.All;
        PasteHotkeyBox.ItemsSource = HotkeyOptions.All;
        TogglePinsHotkeyBox.ItemsSource = HotkeyOptions.All;
        RecordingHotkeyBox.ItemsSource = HotkeyOptions.All;
        ColorMagnifierHotkeyBox.ItemsSource = HotkeyOptions.All;
        RecordingInputDeviceBox.ItemsSource = AdvancedRecordingSession.InputDevices();
        RecordingOutputDeviceBox.ItemsSource = AdvancedRecordingSession.OutputDevices();
        var runningApps = CaptureExclusionService.RunningApps();
        RunningAppsBox.ItemsSource = runningApps;
        HotkeyRunningAppsBox.ItemsSource = runningApps;
        if (RunningAppsBox.Items.Count > 0) RunningAppsBox.SelectedIndex = 0;
        if (HotkeyRunningAppsBox.Items.Count > 0) HotkeyRunningAppsBox.SelectedIndex = 0;
        LoadValues();
        LocalizationService.Apply(this, _settings.UiLanguage);
        AccessibilityService.Apply(this);
    }

    private void LoadValues()
    {
        RunOnStartupBox.IsChecked = _settings.RunOnStartup;
        UiLanguageBox.SelectedValue = LocalizationService.Normalize(_settings.UiLanguage);
        CheckUpdatesOnStartupBox.IsChecked = _settings.CheckUpdatesOnStartup;
        CheckUpdatesDailyBox.IsChecked = _settings.CheckUpdatesDaily;
        RunAsAdministratorBox.IsChecked = _settings.RunAsAdministrator;
        AutoBackupBox.IsChecked = _settings.AutoBackup;
        KeepResponsiveBox.IsChecked = _settings.KeepResponsive;
        BorderWidthBox.Text = _settings.CaptureBorderWidth.ToString();
        BorderColorBox.Text = _settings.CaptureBorderColor;
        MaskColorBox.Text = _settings.CaptureMaskColor;
        CrossLinesBox.IsChecked = _settings.ShowCrossLines;
        ShowSizeBox.IsChecked = _settings.ShowCaptureSize;
        ShowElementDetectionBox.IsChecked = _settings.ShowElementDetection != false;
        ShowCaptureHintsBox.IsChecked = _settings.ShowCaptureHints;
        EnableColorMagnifierBox.IsChecked = _settings.EnableColorMagnifier;
        HdrColorCorrectionBox.IsChecked = _settings.CorrectHdrColors;
        ExcludeSnapAnchorBox.IsChecked = _settings.ExcludeSnapAnchorFromCapture;
        ExcludedAppsList.ItemsSource = _settings.CaptureExcludedProcesses.ToList();
        HotkeyExcludedAppsList.ItemsSource = _settings.HotkeyExcludedProcesses.ToList();
        ScrollFramesBox.Text = _settings.ScrollCaptureMaxFrames.ToString();
        ScrollDelayBox.Text = _settings.ScrollCaptureDelayMs.ToString();
        ScrollClicksBox.Text = _settings.ScrollCaptureWheelClicks.ToString();
        RecordingFormatBox.SelectedValue = _settings.RecordingFormat;
        RecordingModeBox.SelectedValue = _settings.RecordingCaptureMode;
        RecordingQualityBox.SelectedValue = _settings.RecordingQuality;
        RecordingFpsBox.Text = _settings.RecordingFrameRate.ToString();
        RecordingCountdownBox.Text = _settings.RecordingCountdownSeconds.ToString();
        RecordingDurationBox.Text = _settings.RecordingMaxDurationSeconds.ToString();
        RecordingWidthBox.Text = _settings.RecordingMaxWidth.ToString();
        RecordingCursorBox.IsChecked = _settings.RecordingIncludeCursor;
        RecordingClickBox.IsChecked = _settings.RecordingHighlightClicks;
        RecordingSystemAudioBox.IsChecked = _settings.RecordingSystemAudio;
        RecordingMicrophoneBox.IsChecked = _settings.RecordingMicrophone;
        RecordingInputDeviceBox.SelectedValue = _settings.RecordingInputDevice;
        RecordingOutputDeviceBox.SelectedValue = _settings.RecordingOutputDevice;
        RecordingFolderBox.Text = _settings.RecordingFolder;
        PinShadowBox.IsChecked = _settings.PinWindowShadow;
        PinTextSelectableDefaultBox.IsChecked = _settings.PinTextSelectableByDefault;
        PinOpacityBox.Text = _settings.PinDefaultOpacity.ToString();
        PinMaxSizeBox.Text = _settings.PinMaxWindowSize.ToString();
        ThumbnailSizeBox.Text = _settings.FastThumbnailSize.ToString();
        PinGroupsBox.Text = string.Join(", ", _settings.PinGroups);
        PinBackgroundBox.SelectedValue = _settings.DefaultPinBackground;
        DesktopGroupsBox.IsChecked = _settings.PinGroupsFollowVirtualDesktops;
        HistoryLimitBox.Text = _settings.HistoryLimit.ToString();
        FileNameBox.Text = _settings.OutputFileName;
        OutputFormatBox.SelectedValue = _settings.OutputFormat;
        if (OutputFormatBox.SelectedIndex < 0) OutputFormatBox.SelectedIndex = 0;
        ImageQualitySlider.Value = _settings.ImageQuality;
        ImageQualityText.Text = _settings.ImageQuality.ToString();
        OutputBorderWidthBox.Text = _settings.OutputBorderWidth.ToString();
        OutputBorderColorBox.Text = _settings.OutputBorderColor;
        OutputShadowBox.IsChecked = _settings.OutputIncludeShadow;
        OutputShadowSizeBox.Text = _settings.OutputShadowSize.ToString();
        OutputShadowColorBox.Text = _settings.OutputShadowColor;
        QuickFolderBox.Text = _settings.QuickSaveFolder;
        SaveNotificationBox.IsChecked = _settings.ShowSaveNotification;
        AutoSaveBox.IsChecked = _settings.AutoSave;
        AutoFolderBox.Text = _settings.AutoSaveFolder;
        CaptureHotkeyBox.SelectedValue = _settings.CaptureHotkey;
        CopyHotkeyBox.SelectedValue = _settings.CaptureAndCopyHotkey;
        CustomHotkeyBox.SelectedValue = _settings.CustomCaptureHotkey;
        DrawingHotkeyBox.SelectedValue = _settings.DrawingHotkey;
        PasteHotkeyBox.SelectedValue = _settings.PasteHotkey;
        TogglePinsHotkeyBox.SelectedValue = _settings.TogglePinsHotkey;
        RecordingHotkeyBox.SelectedValue = _settings.RecordingHotkey;
        ColorMagnifierHotkeyBox.SelectedValue = _settings.ColorMagnifierHotkey;
        if (ColorMagnifierHotkeyBox.SelectedIndex < 0) ColorMagnifierHotkeyBox.SelectedValue = "CtrlShiftC";
        OcrLanguageBox.SelectedValue = _settings.OcrLanguage;
        OcrOrientationBox.IsChecked = _settings.OcrDetectOrientation;
        if (OcrLanguageBox.SelectedIndex < 0) OcrLanguageBox.SelectedIndex = 3;
        ToolbarSizeBox.SelectedValue = ToolbarThemeService.Normalize(_settings.ToolbarSizeMode);
        VersionText.Text = LocalizationService.Format("Version {0}", DiagnosticsService.Version);
        EditionText.Text = LocalizationService.Format("Edition: {0}",
            LocalizationService.Current(UpdateService.IsPortableInstallation() ? "portable copy" : "installed copy"));
        PopulateCaptureToolbar(_settings.CaptureToolbarOrder, _settings.CaptureToolbarEnabled);
        PopulateToolbar(_settings.AnnotationToolbarOrder, _settings.AnnotationToolbarEnabled);
    }

    private void PopulateCaptureToolbar(IEnumerable<string>? order, IEnumerable<string>? enabled) =>
        PopulateToolbarList(CaptureToolbarToolsList, CaptureToolbarCatalog.NormalizeOrder(order),
            CaptureToolbarCatalog.NormalizeEnabled(enabled), CaptureToolbarCatalog.All);

    private void PopulateToolbar(IEnumerable<string>? order, IEnumerable<string>? enabled) =>
        PopulateToolbarList(ToolbarToolsList, AnnotationToolbarCatalog.NormalizeOrder(order),
            AnnotationToolbarCatalog.NormalizeEnabled(enabled), AnnotationToolbarCatalog.All);

    private static void PopulateToolbarList(ListBox target, IEnumerable<string> order, IEnumerable<string> enabled,
        IEnumerable<AnnotationToolbarDefinition> availableDefinitions)
    {
        var enabledTools = enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var definitions = availableDefinitions.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        target.Items.Clear();
        foreach (var key in order)
        {
            if (!definitions.TryGetValue(key, out var definition)) continue;
            var checkBox = new CheckBox
            {
                Content = definition.Label,
                IsChecked = enabledTools.Contains(key),
                Tag = key,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(2, 0, 0, 0)
            };
            var item = new ListBoxItem
            {
                Content = checkBox,
                Tag = key,
                Height = 30,
                Padding = new Thickness(7, 3, 7, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            checkBox.Click += (_, _) => item.IsSelected = true;
            target.Items.Add(item);
        }
        if (target.Items.Count > 0) target.SelectedIndex = 0;
    }

    private void MoveToolbarUp_Click(object sender, RoutedEventArgs e) => MoveToolbarItem(-1);
    private void MoveToolbarDown_Click(object sender, RoutedEventArgs e) => MoveToolbarItem(1);
    private void MoveCaptureToolbarUp_Click(object sender, RoutedEventArgs e) => MoveToolbarItem(CaptureToolbarToolsList, -1);
    private void MoveCaptureToolbarDown_Click(object sender, RoutedEventArgs e) => MoveToolbarItem(CaptureToolbarToolsList, 1);

    private void MoveToolbarItem(int offset) => MoveToolbarItem(ToolbarToolsList, offset);

    private static void MoveToolbarItem(ListBox list, int offset)
    {
        var index = list.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= list.Items.Count) return;
        var item = list.Items[index];
        list.Items.RemoveAt(index);
        list.Items.Insert(target, item);
        list.SelectedIndex = target;
        if (item is ListBoxItem row) row.Focus();
    }

    private void EnableAllToolbar_Click(object sender, RoutedEventArgs e)
        => EnableAllToolbarItems(ToolbarToolsList);

    private void EnableAllCaptureToolbar_Click(object sender, RoutedEventArgs e)
        => EnableAllToolbarItems(CaptureToolbarToolsList);

    private static void EnableAllToolbarItems(ListBox list)
    {
        foreach (var item in list.Items.OfType<ListBoxItem>())
            if (item.Content is CheckBox checkBox) checkBox.IsChecked = true;
    }

    private void ResetToolbar_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        PopulateToolbar(defaults.AnnotationToolbarOrder, defaults.AnnotationToolbarEnabled);
    }

    private void ResetCaptureToolbar_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        PopulateCaptureToolbar(defaults.CaptureToolbarOrder, defaults.CaptureToolbarEnabled);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var previousLanguage = LocalizationService.Normalize(_settings.UiLanguage);
        _settings.RunOnStartup = RunOnStartupBox.IsChecked == true;
        _settings.UiLanguage = LocalizationService.Normalize(UiLanguageBox.SelectedValue as string);
        _settings.CheckUpdatesOnStartup = CheckUpdatesOnStartupBox.IsChecked == true;
        _settings.CheckUpdatesDaily = CheckUpdatesDailyBox.IsChecked == true;
        _settings.RunAsAdministrator = RunAsAdministratorBox.IsChecked == true;
        _settings.AutoBackup = AutoBackupBox.IsChecked == true;
        _settings.KeepResponsive = KeepResponsiveBox.IsChecked == true;
        _settings.CaptureBorderWidth = Parse(BorderWidthBox.Text, 1, 12, 2);
        _settings.CaptureBorderColor = BorderColorBox.Text.Trim();
        _settings.CaptureMaskColor = MaskColorBox.Text.Trim();
        _settings.ShowCrossLines = CrossLinesBox.IsChecked == true;
        _settings.ShowCaptureSize = ShowSizeBox.IsChecked == true;
        _settings.ShowElementDetection = ShowElementDetectionBox.IsChecked == true;
        _settings.ShowCaptureHints = ShowCaptureHintsBox.IsChecked == true;
        _settings.EnableColorMagnifier = EnableColorMagnifierBox.IsChecked == true;
        _settings.ShowColorSampler = _settings.EnableColorMagnifier;
        _settings.CorrectHdrColors = HdrColorCorrectionBox.IsChecked == true;
        _settings.ExcludeSnapAnchorFromCapture = ExcludeSnapAnchorBox.IsChecked == true;
        _settings.CaptureExcludedProcesses = ExcludedAppsList.Items.Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.HotkeyExcludedProcesses = HotkeyExclusionService.Normalize(HotkeyExcludedAppsList.Items.Cast<string>());
        _settings.ScrollCaptureMaxFrames = Parse(ScrollFramesBox.Text, 2, 60, 12);
        _settings.ScrollCaptureDelayMs = Parse(ScrollDelayBox.Text, 150, 3000, 450);
        _settings.ScrollCaptureWheelClicks = Parse(ScrollClicksBox.Text, 1, 20, 5);
        _settings.RecordingFormat = RecordingFormatBox.SelectedValue as string ?? "MP4";
        _settings.RecordingCaptureMode = RecordingModeBox.SelectedValue as string ?? "Region";
        _settings.RecordingQuality = RecordingQualityBox.SelectedValue as string ?? "High";
        _settings.RecordingFrameRate = Parse(RecordingFpsBox.Text, 2, 20, 8);
        _settings.RecordingCountdownSeconds = Parse(RecordingCountdownBox.Text, 0, 10, 3);
        _settings.RecordingMaxDurationSeconds = Parse(RecordingDurationBox.Text, 5, 600, 60);
        _settings.RecordingMaxWidth = Parse(RecordingWidthBox.Text, 320, 1920, 960);
        _settings.RecordingIncludeCursor = RecordingCursorBox.IsChecked == true;
        _settings.RecordingHighlightClicks = RecordingClickBox.IsChecked == true;
        _settings.RecordingSystemAudio = RecordingSystemAudioBox.IsChecked == true;
        _settings.RecordingMicrophone = RecordingMicrophoneBox.IsChecked == true;
        _settings.RecordingInputDevice = RecordingInputDeviceBox.SelectedValue as string ?? string.Empty;
        _settings.RecordingOutputDevice = RecordingOutputDeviceBox.SelectedValue as string ?? string.Empty;
        _settings.RecordingFolder = string.IsNullOrWhiteSpace(RecordingFolderBox.Text) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SnapAnchor") : RecordingFolderBox.Text.Trim();
        _settings.PinWindowShadow = PinShadowBox.IsChecked == true;
        _settings.PinTextSelectableByDefault = PinTextSelectableDefaultBox.IsChecked == true;
        _settings.PinDefaultOpacity = Parse(PinOpacityBox.Text, 15, 100, 100);
        _settings.PinMaxWindowSize = Parse(PinMaxSizeBox.Text, 500, 50000, 12000);
        _settings.FastThumbnailSize = Parse(ThumbnailSizeBox.Text, 20, 500, 50);
        _settings.PinGroups = PinGroupsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (_settings.PinGroups.Count == 0) _settings.PinGroups.Add("Default");
        if (!_settings.PinGroups.Contains(_settings.CurrentPinGroup, StringComparer.OrdinalIgnoreCase))
            _settings.CurrentPinGroup = _settings.PinGroups[0];
        _settings.DefaultPinBackground = PinBackgroundBox.SelectedValue as string ?? "Transparent";
        _settings.PinGroupsFollowVirtualDesktops = DesktopGroupsBox.IsChecked == true;
        _settings.HistoryLimit = Parse(HistoryLimitBox.Text, 10, 5000, 200);
        _settings.OutputFormat = OutputFormatBox.SelectedValue as string ?? "PNG";
        _settings.ImageQuality = Math.Clamp((int)Math.Round(ImageQualitySlider.Value), 30, 100);
        _settings.OutputBorderWidth = Parse(OutputBorderWidthBox.Text, 0, 32, 0);
        _settings.OutputBorderColor = OutputBorderColorBox.Text.Trim();
        _settings.OutputIncludeShadow = OutputShadowBox.IsChecked == true;
        _settings.OutputShadowSize = Parse(OutputShadowSizeBox.Text, 1, 64, 12);
        _settings.OutputShadowColor = OutputShadowColorBox.Text.Trim();
        var imageExtension = _settings.OutputFormat switch { "JPEG" => ".jpg", "WEBP" => ".webp", _ => ".png" };
        _settings.OutputFileName = Path.ChangeExtension(
            string.IsNullOrWhiteSpace(FileNameBox.Text) ? "SnapAnchor_$yyyy-MM-dd_HH-mm-ss" : FileNameBox.Text.Trim(),
            imageExtension);
        _settings.QuickSaveFolder = QuickFolderBox.Text.Trim();
        _settings.ShowSaveNotification = SaveNotificationBox.IsChecked == true;
        _settings.AutoSave = AutoSaveBox.IsChecked == true;
        _settings.AutoSaveFolder = AutoFolderBox.Text.Trim();
        _settings.CaptureHotkey = Selected(CaptureHotkeyBox, "F1");
        _settings.CaptureAndCopyHotkey = Selected(CopyHotkeyBox, "CtrlF1");
        _settings.CustomCaptureHotkey = Selected(CustomHotkeyBox, "ShiftF1");
        _settings.DrawingHotkey = Selected(DrawingHotkeyBox, "CtrlShiftD");
        _settings.PasteHotkey = Selected(PasteHotkeyBox, "F3");
        _settings.TogglePinsHotkey = Selected(TogglePinsHotkeyBox, "ShiftF3");
        _settings.RecordingHotkey = Selected(RecordingHotkeyBox, "CtrlShiftR");
        _settings.ColorMagnifierHotkey = Selected(ColorMagnifierHotkeyBox, "CtrlShiftC");
        _settings.OcrLanguage = OcrLanguageBox.SelectedValue as string ?? "eng+chi_sim+chi_tra";
        _settings.OcrDetectOrientation = OcrOrientationBox.IsChecked == true;
        _settings.ToolbarSizeMode = ToolbarThemeService.Normalize(ToolbarSizeBox.SelectedValue as string);
        _settings.UpdateFeedUrl = AppSettings.DefaultUpdateFeedUrl;
        _settings.AnnotationToolbarOrder = AnnotationToolbarCatalog.NormalizeOrder(
            ToolbarToolsList.Items.OfType<ListBoxItem>().Select(item => item.Tag as string ?? string.Empty));
        _settings.AnnotationToolbarEnabled = AnnotationToolbarCatalog.NormalizeEnabled(
            ToolbarToolsList.Items.OfType<ListBoxItem>()
                .Where(item => item.Content is CheckBox { IsChecked: true })
                .Select(item => item.Tag as string ?? string.Empty));
        _settings.CaptureToolbarOrder = CaptureToolbarCatalog.NormalizeOrder(
            CaptureToolbarToolsList.Items.OfType<ListBoxItem>().Select(item => item.Tag as string ?? string.Empty));
        _settings.CaptureToolbarEnabled = CaptureToolbarCatalog.NormalizeEnabled(
            CaptureToolbarToolsList.Items.OfType<ListBoxItem>()
                .Where(item => item.Content is CheckBox { IsChecked: true })
                .Select(item => item.Tag as string ?? string.Empty));
        SettingsService.Save(_settings);
        ToolbarThemeService.Apply(_settings.ToolbarSizeMode);
        HistoryService.EnforceLimit(_settings.HistoryLimit);
        SettingsApplied?.Invoke(this, EventArgs.Empty);
        var restartRequested = false;
        if (!previousLanguage.Equals(_settings.UiLanguage, StringComparison.Ordinal))
            restartRequested = LanguageRestartWindow.Ask(this, _settings.UiLanguage);
        DialogResult = true;
        if (restartRequested) Dispatcher.BeginInvoke(RestartService.RestartApplication);
    }

    private void BrowseQuick_Click(object sender, RoutedEventArgs e) => BrowseInto(QuickFolderBox);

    private void ImageQualitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageQualityText is not null) ImageQualityText.Text = Math.Round(e.NewValue).ToString();
    }

    private void BrowseAuto_Click(object sender, RoutedEventArgs e) => BrowseInto(AutoFolderBox);
    private void BrowseRecording_Click(object sender, RoutedEventArgs e) => BrowseInto(RecordingFolderBox);

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e) => DiagnosticsService.OpenFolder();

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(DiagnosticsService.Summary());
        MessageBox.Show(this, LocalizationService.Current("The diagnostic summary was copied."), LocalizationService.Current("SnapAnchor diagnostics"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CopyVersion_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(DiagnosticsService.ProductSummary(UpdateService.IsPortableInstallation()));
        MessageBox.Show(this, LocalizationService.Current("Version information copied."), "SnapAnchor",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await UpdateWorkflowService.CheckAndRunAsync(this, AppSettings.DefaultUpdateFeedUrl, automatic: false))
                Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("update", ex.Message, ex);
            MessageBox.Show(this, LocalizationService.Format("Update check failed: {0}", ex.Message), LocalizationService.Current("SnapAnchor update"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            UpdateCheckScheduleService.MarkChecked(DateTimeOffset.UtcNow);
        }
    }

    private void GitHubLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void AddExcludedApp_Click(object sender, RoutedEventArgs e)
    {
        if (RunningAppsBox.SelectedValue is not string processName || string.IsNullOrWhiteSpace(processName)) return;
        var names = ExcludedAppsList.Items.Cast<string>().Append(processName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ExcludedAppsList.ItemsSource = names;
        ExcludedAppsList.SelectedItem = processName;
    }

    private void RemoveExcludedApp_Click(object sender, RoutedEventArgs e)
    {
        if (ExcludedAppsList.SelectedItem is not string selected) return;
        ExcludedAppsList.ItemsSource = ExcludedAppsList.Items.Cast<string>()
            .Where(name => !name.Equals(selected, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void AddHotkeyExcludedApp_Click(object sender, RoutedEventArgs e)
    {
        if (HotkeyRunningAppsBox.SelectedValue is not string processName || string.IsNullOrWhiteSpace(processName)) return;
        var names = HotkeyExclusionService.Normalize(HotkeyExcludedAppsList.Items.Cast<string>().Append(processName));
        HotkeyExcludedAppsList.ItemsSource = names;
        HotkeyExcludedAppsList.SelectedItem = processName;
    }

    private void RemoveHotkeyExcludedApp_Click(object sender, RoutedEventArgs e)
    {
        if (HotkeyExcludedAppsList.SelectedItem is not string selected) return;
        HotkeyExcludedAppsList.ItemsSource = HotkeyExcludedAppsList.Items.Cast<string>()
            .Where(name => !name.Equals(selected, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static void BrowseInto(System.Windows.Controls.TextBox target)
    {
        using var dialog = new Forms.FolderBrowserDialog { InitialDirectory = target.Text, UseDescriptionForTitle = true, Description = LocalizationService.Current("Choose a SnapAnchor output folder") };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) target.Text = dialog.SelectedPath;
    }

    private static int Parse(string text, int minimum, int maximum, int fallback) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static string Selected(System.Windows.Controls.ComboBox box, string fallback) => box.SelectedValue as string ?? fallback;
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
