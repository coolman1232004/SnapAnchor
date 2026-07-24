using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Threading.Tasks;
using System.Windows.Threading;
using SnapAnchor.Services;
using SnapAnchor.Windows;
using SnapAnchor.Models;
using Forms = System.Windows.Forms;

namespace SnapAnchor;

public partial class MainWindow : Window
{
    private const int CaptureHotKeyId = 1001;
    private const int PinHotKeyId = 1002;
    private const int RestoreHotKeyId = 1003;
    private const int CaptureAndCopyHotKeyId = 1004;
    private const int TogglePinsHotKeyId = 1005;
    private const int CustomCaptureHotKeyId = 1006;
    private const int RecordingHotKeyId = 1007;
    private const int DrawingHotKeyId = 1008;
    private const int ColorMagnifierHotKeyId = 1009;

    private HwndSource? _source;
    private Forms.NotifyIcon? _trayIcon;
    private bool _allowClose;
    private bool _exitStarted;
    private bool _hotkeysEnabled = true;
    private readonly AppSettings _settings;
    private HotkeyDefinition _captureHotkey;
    private HotkeyDefinition _captureAndCopyHotkey;
    private HotkeyDefinition _pasteHotkey;
    private HotkeyDefinition _togglePinsHotkey;
    private HotkeyDefinition _customCaptureHotkey;
    private HotkeyDefinition _recordingHotkey;
    private HotkeyDefinition _drawingHotkey;
    private HotkeyDefinition _colorMagnifierHotkey;
    private readonly DispatcherTimer _sessionTimer;
    private readonly DispatcherTimer _desktopTimer;
    private bool _refreshingPinManager;
    private bool _hotkeyConflictBalloonShown;
    private Guid? _lastDesktopId;
    private UpdateCheckResult? _availableUpdate;

    public MainWindow()
    {
        InitializeComponent();
        DpiLayoutService.Attach(this);
        _settings = SettingsService.Load();
        CaptureHotkeyCombo.ItemsSource = HotkeyOptions.All;
        RefreshDefinitions();
        RefreshDashboard();
        LocalizationService.Apply(this, _settings.UiLanguage);
        AccessibilityService.Apply(this);
        SourceInitialized += MainWindow_SourceInitialized;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && _settings.KeepResponsive) Hide();
        };
        Closing += MainWindow_Closing;
        CreateTrayIcon();
        PinnedImageWindow.PinsChanged += PinnedImageWindow_PinsChanged;
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _sessionTimer.Tick += (_, _) => _ = PinnedImageWindow.SaveSessionAsync();
        _sessionTimer.Start();
        _desktopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _desktopTimer.Tick += (_, _) => CheckVirtualDesktop();
        _desktopTimer.Start();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowDisplayAffinity(handle, _settings.ExcludeSnapAnchorFromCapture ? NativeMethods.WdaExcludeFromCapture : NativeMethods.WdaNone);
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WndProc);
        UpdateHotkeyStatus(RegisterAllHotkeys());
        Dispatcher.BeginInvoke(PinnedImageWindow.RestoreSession);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotKey) return IntPtr.Zero;
        handled = true;
        var hotkeyId = wParam.ToInt32();
        if (hotkeyId != RestoreHotKeyId && HotkeyExclusionService.IsForegroundExcluded(_settings.HotkeyExcludedProcesses))
            return IntPtr.Zero;
        switch (hotkeyId)
        {
            case CaptureHotKeyId: StartCapture(); break;
            case PinHotKeyId: PinClipboard(); break;
            case RestoreHotKeyId: PinnedImageWindow.RestoreInteractions(); break;
            case CaptureAndCopyHotKeyId: StartCapture(CaptureCompletionMode.CopyOnSelection); break;
            case TogglePinsHotKeyId: PinnedImageWindow.ToggleAllVisibility(); break;
            case CustomCaptureHotKeyId: OpenCustomCapture(); break;
            case RecordingHotKeyId: StartCapture(CaptureCompletionMode.RecordOnSelection); break;
            case DrawingHotKeyId: StartCapture(CaptureCompletionMode.FullScreenDraw); break;
            case ColorMagnifierHotKeyId: StartCapture(CaptureCompletionMode.ColorPicker); break;
        }
        return IntPtr.Zero;
    }

    private bool RegisterAllHotkeys()
    {
        UnregisterAllHotkeys();
        if (!_hotkeysEnabled) return true;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return true;

        var success = Register(handle, CaptureHotKeyId, _captureHotkey);
        success &= Register(handle, CaptureAndCopyHotKeyId, _captureAndCopyHotkey);
        success &= Register(handle, PinHotKeyId, _pasteHotkey);
        success &= Register(handle, TogglePinsHotKeyId, _togglePinsHotkey);
        success &= Register(handle, CustomCaptureHotKeyId, _customCaptureHotkey);
        success &= Register(handle, RecordingHotKeyId, _recordingHotkey);
        success &= Register(handle, DrawingHotKeyId, _drawingHotkey);
        success &= Register(handle, ColorMagnifierHotKeyId, _colorMagnifierHotkey);
        success &= NativeMethods.RegisterHotKey(handle, RestoreHotKeyId,
            NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModNoRepeat, 0x50);
        return success;
    }

    private static bool Register(IntPtr handle, int id, HotkeyDefinition definition) =>
        definition.VirtualKey == 0 || NativeMethods.RegisterHotKey(handle, id,
            definition.Modifiers | NativeMethods.ModNoRepeat, definition.VirtualKey);

    private void UnregisterAllHotkeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        foreach (var id in new[] { CaptureHotKeyId, PinHotKeyId, RestoreHotKeyId, CaptureAndCopyHotKeyId, TogglePinsHotKeyId, CustomCaptureHotKeyId, RecordingHotKeyId, DrawingHotKeyId, ColorMagnifierHotKeyId })
            NativeMethods.UnregisterHotKey(handle, id);
    }

    private void UpdateHotkeyStatus(bool success)
    {
        StatusDot.Fill = success ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.Orange;
        StatusText.Text = success
            ? $"{L("Capture")}: {_captureHotkey.DisplayName}  ·  {L("Pin")}: {_pasteHotkey.DisplayName}"
            : L("One or more selected shortcuts are already used by Windows or another app");
        if (success)
        {
            _hotkeyConflictBalloonShown = false;
            return;
        }
        if (_hotkeyConflictBalloonShown || _trayIcon is null) return;
        _hotkeyConflictBalloonShown = true;
        _trayIcon.ShowBalloonTip(
            9000,
            L("Shortcut conflict"),
            L("One or more SnapAnchor shortcuts could not be registered because Windows or another app already uses them. Open Preferences → Hotkeys to pick free keys."),
            Forms.ToolTipIcon.Warning);
    }

    private void RefreshDefinitions()
    {
        _captureHotkey = HotkeyOptions.Get(_settings.CaptureHotkey);
        _captureAndCopyHotkey = HotkeyOptions.Get(_settings.CaptureAndCopyHotkey);
        _pasteHotkey = HotkeyOptions.Get(_settings.PasteHotkey);
        _togglePinsHotkey = HotkeyOptions.Get(_settings.TogglePinsHotkey);
        _customCaptureHotkey = HotkeyOptions.Get(_settings.CustomCaptureHotkey);
        _recordingHotkey = HotkeyOptions.Get(_settings.RecordingHotkey);
        _drawingHotkey = HotkeyOptions.Get(_settings.DrawingHotkey);
        _colorMagnifierHotkey = HotkeyOptions.Get(_settings.ColorMagnifierHotkey);
    }

    private void RefreshDashboard()
    {
        CaptureHotkeyCombo.SelectedValue = _captureHotkey.Name;
        CaptureButton.Content = $"{L("Capture region")}  ·  {_captureHotkey.DisplayName}";
        DrawScreenButton.Content = $"{L("Draw")}  ·  {_drawingHotkey.DisplayName}";
        RefreshPinManager();
    }

    private void PinnedImageWindow_PinsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshPinManager);

    private void RefreshPinManager()
    {
        _refreshingPinManager = true;
        try
        {
            var persisted = SettingsService.Load();
            _settings.PinGroups = persisted.PinGroups;
            _settings.CurrentPinGroup = persisted.CurrentPinGroup;
            _settings.PinGroupsFollowVirtualDesktops = persisted.PinGroupsFollowVirtualDesktops;
            _settings.PinGroupDesktopBindings = persisted.PinGroupDesktopBindings;
            PinGroupsCombo.ItemsSource = null;
            PinGroupsCombo.ItemsSource = _settings.PinGroups.ToList();
            PinGroupsCombo.SelectedItem = _settings.CurrentPinGroup;
            var pins = PinnedImageWindow.Snapshots()
                .Where(pin => pin.Group.Equals(_settings.CurrentPinGroup, StringComparison.OrdinalIgnoreCase))
                .ToList();
            PinManagerList.ItemsSource = pins;
            PinManagerStatus.Text = pins.Count == 1 ? L("1 pin") : LocalizationService.Format("{0} pins", pins.Count);
            var currentDesktop = VirtualDesktopService.CurrentDesktopId();
            var boundDesktop = VirtualDesktopService.BoundDesktop(_settings, _settings.CurrentPinGroup);
            PinDesktopStatus.Text = !VirtualDesktopService.IsSupported
                ? L("Virtual desktops unavailable")
                : boundDesktop is null
                    ? LocalizationService.Format("{0} · not bound", VirtualDesktopService.ShortLabel(currentDesktop))
                    : boundDesktop == currentDesktop
                        ? LocalizationService.Format("{0} · bound here", VirtualDesktopService.ShortLabel(boundDesktop))
                        : LocalizationService.Format("Bound to {0}", VirtualDesktopService.ShortLabel(boundDesktop));
        }
        finally
        {
            _refreshingPinManager = false;
        }
    }

    private void PinGroup_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshingPinManager || PinGroupsCombo.SelectedItem is not string group) return;
        _settings.CurrentPinGroup = group;
        SettingsService.Save(_settings);
        PinnedImageWindow.SwitchGroup(group);
        RefreshPinManager();
        RefreshTrayMenu();
    }

    private void AddPinGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = NewPinGroupBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(group)) return;
        if (!_settings.PinGroups.Contains(group, StringComparer.OrdinalIgnoreCase)) _settings.PinGroups.Add(group);
        _settings.CurrentPinGroup = _settings.PinGroups.First(name => name.Equals(group, StringComparison.OrdinalIgnoreCase));
        SettingsService.Save(_settings);
        NewPinGroupBox.Clear();
        PinnedImageWindow.SwitchGroup(_settings.CurrentPinGroup);
        RefreshPinManager();
        RefreshTrayMenu();
    }

    private void DeletePinGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.PinGroups.Count <= 1 || PinGroupsCombo.SelectedItem is not string group) return;
        var replacement = _settings.PinGroups.First(name => !name.Equals(group, StringComparison.OrdinalIgnoreCase));
        _settings.PinGroups.RemoveAll(name => name.Equals(group, StringComparison.OrdinalIgnoreCase));
        _settings.PinGroupDesktopBindings.Remove(group);
        _settings.CurrentPinGroup = replacement;
        SettingsService.Save(_settings);
        PinnedImageWindow.ReassignGroup(group, replacement);
        RefreshPinManager();
        RefreshTrayMenu();
    }

    private void ToggleManagedPins_Click(object sender, RoutedEventArgs e) => PinnedImageWindow.ToggleAllVisibility();

    private void CloseManagedPins_Click(object sender, RoutedEventArgs e)
    {
        if (PinnedImageWindow.Snapshots().Any(pin => pin.Group.Equals(_settings.CurrentPinGroup, StringComparison.OrdinalIgnoreCase)))
            PinnedImageWindow.CloseGroup(_settings.CurrentPinGroup);
    }

    private void ActivateManagedPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: Guid id }) PinnedImageWindow.ActivatePin(id);
    }

    private void CloseManagedPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: Guid id }) PinnedImageWindow.ClosePin(id);
    }

    private void BindPinGroupDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (VirtualDesktopService.CurrentDesktopId() is not { } desktopId) return;
        foreach (var key in _settings.PinGroupDesktopBindings
                     .Where(pair => Guid.TryParse(pair.Value, out var bound) && bound == desktopId)
                     .Select(pair => pair.Key)
                     .ToList())
            _settings.PinGroupDesktopBindings.Remove(key);
        _settings.PinGroupDesktopBindings[_settings.CurrentPinGroup] = desktopId.ToString();
        _settings.PinGroupsFollowVirtualDesktops = true;
        SettingsService.Save(_settings);
        _lastDesktopId = null;
        PinnedImageWindow.SwitchGroup(_settings.CurrentPinGroup);
        CheckVirtualDesktop(force: true);
        RefreshPinManager();
    }

    private void ClearPinGroupDesktop_Click(object sender, RoutedEventArgs e)
    {
        _settings.PinGroupDesktopBindings.Remove(_settings.CurrentPinGroup);
        SettingsService.Save(_settings);
        RefreshPinManager();
    }

    private void CheckVirtualDesktop(bool force = false)
    {
        if (VirtualDesktopService.CurrentDesktopId() is not { } desktopId) return;
        var changed = _lastDesktopId != desktopId;
        _lastDesktopId = desktopId;
        if (!_settings.PinGroupsFollowVirtualDesktops || (!force && !changed)) return;
        var group = _settings.PinGroupDesktopBindings.FirstOrDefault(pair =>
            Guid.TryParse(pair.Value, out var boundDesktop) && boundDesktop == desktopId).Key;
        if (string.IsNullOrWhiteSpace(group) || group.Equals(_settings.CurrentPinGroup, StringComparison.OrdinalIgnoreCase))
        {
            RefreshPinManager();
            return;
        }
        _settings.CurrentPinGroup = group;
        SettingsService.Save(_settings);
        PinnedImageWindow.SwitchGroup(group);
        RefreshPinManager();
        RefreshTrayMenu();
    }

    private void SaveHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (CaptureHotkeyCombo.SelectedValue is not string selectedName) return;
        _settings.CaptureHotkey = selectedName;
        SettingsService.Save(_settings);
        ApplySettings();
    }

    private void Preferences_Click(object sender, RoutedEventArgs e) => OpenPreferences();
    private void History_Click(object sender, RoutedEventArgs e) => OpenHistory();
    private void CaptureButton_Click(object sender, RoutedEventArgs e) => StartCapture();
    private void DrawScreenButton_Click(object sender, RoutedEventArgs e) => StartCapture(CaptureCompletionMode.FullScreenDraw);
    private void PinButton_Click(object sender, RoutedEventArgs e) => PinClipboard();

    private void OpenPreferences()
    {
        MoveDashboardToCurrentDesktop();
        var dialog = new PreferencesWindow(_settings) { Owner = this };
        dialog.SettingsApplied += (_, _) => ApplySettings();
        dialog.ShowDialog();
    }

    private async Task CheckForUpdatesAsync()
    {
        ShowDashboard();
        try
        {
            if (await UpdateWorkflowService.CheckAndRunAsync(this, _settings.UpdateFeedUrl, automatic: false))
                Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("manual-update", ex.Message, ex);
            MessageBox.Show(this, LocalizationService.Format("GitHub update check failed: {0}", ex.Message), L("SnapAnchor update"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenHistory()
    {
        MoveDashboardToCurrentDesktop();
        var window = new HistoryWindow { Owner = this };
        window.RepeatLastRequested += (_, _) => RepeatLastRegion();
        window.Show();
    }

    private void RepeatLastRegion()
    {
        try
        {
            var region = HistoryService.LastRegion();
            if (region is null)
            {
                ShowError("No previous region", "Capture a region first, then repeat-last-region will become available.");
                return;
            }
            var fullScreen = CaptureService.CaptureVirtualScreen();
            var rect = new Int32Rect(region.X, region.Y, region.Width, region.Height);
            var image = CaptureService.Crop(fullScreen, rect);
            Clipboard.SetImage(image);
            HistoryService.Add(image, rect, fullScreen, "Copied");
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch (Exception ex)
        {
            ShowError("Repeat capture failed", ex.Message);
        }
    }

    private void ApplySettings()
    {
        RefreshDefinitions();
        RefreshDashboard();
        RefreshTrayMenu();
        UpdateHotkeyStatus(RegisterAllHotkeys());
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            NativeMethods.SetWindowDisplayAffinity(handle, _settings.ExcludeSnapAnchorFromCapture ? NativeMethods.WdaExcludeFromCapture : NativeMethods.WdaNone);
        PinnedImageWindow.ApplyCaptureAffinity(_settings.ExcludeSnapAnchorFromCapture);
        _lastDesktopId = null;
        CheckVirtualDesktop(force: true);
    }

    internal void ExecuteCommand(AppCommand command)
    {
        var options = new CaptureOptions
        {
            DelaySeconds = command.DelaySeconds,
            FixedWidth = command.FixedWidth,
            FixedHeight = command.FixedHeight,
            AspectRatio = command.AspectRatio,
            IncludeCursor = command.IncludeCursor
        };
        switch (command.Kind)
        {
            case AppCommandKind.Activate: ShowFromExternalActivation(); break;
            case AppCommandKind.Capture: _ = BeginCustomCaptureAsync(options); break;
            case AppCommandKind.CaptureCopy: _ = BeginCommandCaptureAsync(CaptureCompletionMode.CopyOnSelection, options); break;
            case AppCommandKind.PinClipboard: PinClipboard(); break;
            case AppCommandKind.Record: _ = BeginCommandCaptureAsync(CaptureCompletionMode.RecordOnSelection, options); break;
            case AppCommandKind.Draw: _ = BeginCommandCaptureAsync(CaptureCompletionMode.FullScreenDraw, options); break;
            case AppCommandKind.History: OpenHistory(); break;
            case AppCommandKind.Settings: OpenPreferences(); break;
            case AppCommandKind.Whiteboard: OpenWhiteboard(transparent: false); break;
            case AppCommandKind.TransparentWhiteboard: OpenWhiteboard(transparent: true); break;
            case AppCommandKind.ColorPicker: _ = BeginCommandCaptureAsync(CaptureCompletionMode.ColorPicker, options); break;
            case AppCommandKind.Exit: ExitApplication(); break;
        }
    }

    private async Task BeginCommandCaptureAsync(CaptureCompletionMode mode, CaptureOptions options)
    {
        if (IsVisible) Hide();
        if (options.DelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(options.DelaySeconds));
        StartCapture(mode, options);
    }

    private void OpenWhiteboard(bool transparent)
    {
        if (IsVisible) Hide();
        var whiteboard = new WhiteboardWindow(transparent);
        whiteboard.Show();
        whiteboard.Activate();
    }

    private void StartCapture(CaptureCompletionMode completionMode = CaptureCompletionMode.Interactive, CaptureOptions? options = null)
    {
        try
        {
            if (IsVisible) Hide();
            var overlay = new CaptureOverlayWindow(CaptureService.CaptureVirtualScreen(options?.IncludeCursor == true), completionMode, options);
            overlay.Show();
            overlay.Activate();
        }
        catch (Exception ex)
        {
            ShowError("Screen capture failed", ex.Message);
        }
    }

    private void OpenCustomCapture()
    {
        var dialog = new CustomCaptureWindow { Owner = IsVisible ? this : null };
        if (dialog.ShowDialog() != true) return;
        _ = BeginCustomCaptureAsync(dialog.Options);
    }

    private async Task BeginCustomCaptureAsync(CaptureOptions options)
    {
        if (IsVisible) Hide();
        if (options.DelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(options.DelaySeconds));
        StartCapture(CaptureCompletionMode.Interactive, options);
    }

    private void CaptureActiveWindow()
    {
        try
        {
            if (IsVisible) Hide();
            var detected = ElementDetectionService.TopExternalWindow();
            if (detected is null)
            {
                ShowError("No active window", "SnapAnchor could not find another visible window to capture.");
                return;
            }
            var virtualBounds = DisplayTopologyService.VirtualBoundsPixels();
            var region = new Int32Rect(
                (int)Math.Round(detected.Bounds.X - virtualBounds.Left),
                (int)Math.Round(detected.Bounds.Y - virtualBounds.Top),
                Math.Max(1, (int)Math.Round(detected.Bounds.Width)),
                Math.Max(1, (int)Math.Round(detected.Bounds.Height)));
            var fullScreen = CaptureService.CaptureVirtualScreen();
            var image = CaptureService.Crop(fullScreen, region);
            Clipboard.SetImage(image);
            var record = HistoryService.Add(image, region, fullScreen, "Window");
            new PinnedImageWindow(image, startEditing: true, historyRecordId: record.Id).Show();
        }
        catch (Exception ex)
        {
            ShowError("Active window capture failed", ex.Message);
        }
    }

    private void PinClipboard()
    {
        try
        {
            var images = CaptureService.GetClipboardVisuals();
            if (images.Count == 0)
            {
                ShowError("Nothing to pin", "Copy one or more images, image files, formatted text, plain text, or a hex color first.");
                return;
            }
            foreach (var image in images) new PinnedImageWindow(image).Show();
        }
        catch (Exception ex)
        {
            ShowError("Clipboard could not be pinned", ex.Message);
        }
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "SnapAnchor",
            Visible = true,
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        RefreshTrayMenu();
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowDashboard);
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(RunAvailableUpdate);
        MaybeShowWelcomeTip();
    }

    private void MaybeShowWelcomeTip()
    {
        if (_settings.HasSeenWelcomeTip || _trayIcon is null) return;
        _settings.HasSeenWelcomeTip = true;
        SettingsService.Save(_settings);
        _trayIcon.ShowBalloonTip(
            12000,
            L("Welcome to SnapAnchor"),
            LocalizationService.Format(
                "Capture region: {0}. Pin clipboard: {1}. Record: {2}. Colour picker: {3}. Right-click this icon for every action.",
                _captureHotkey.DisplayName,
                _pasteHotkey.DisplayName,
                _recordingHotkey.DisplayName,
                _colorMagnifierHotkey.DisplayName),
            Forms.ToolTipIcon.Info);
    }

    private void CaptureFullScreen()
    {
        try
        {
            if (IsVisible) Hide();
            var fullScreen = CaptureService.CaptureVirtualScreen();
            var region = new Int32Rect(0, 0, fullScreen.PixelWidth, fullScreen.PixelHeight);
            Clipboard.SetImage(fullScreen);
            var record = HistoryService.Add(fullScreen, region, fullScreen, "Full screen");
            new PinnedImageWindow(fullScreen, startEditing: true, historyRecordId: record.Id).Show();
        }
        catch (Exception ex)
        {
            ShowError("Screen capture failed", ex.Message);
        }
    }

    private void RefreshTrayMenu()
    {
        if (_trayIcon?.ContextMenuStrip is not { } menu) return;
        menu.Items.Clear();
        if (_availableUpdate is not null)
        {
            var ready = UpdateService.TryLoadPending(_availableUpdate, out _);
            var label = ready
                ? LocalizationService.Format("Restart and update to SnapAnchor {0}...", _availableUpdate.Version)
                : LocalizationService.Format("Download SnapAnchor {0} update...", _availableUpdate.Version);
            var updateItem = new Forms.ToolStripMenuItem(label) { Font = new Font(menu.Font, System.Drawing.FontStyle.Bold) };
            updateItem.Click += (_, _) => Dispatcher.Invoke(RunAvailableUpdate);
            menu.Items.Add(updateItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
        }
        // Capture — matches README quick-start actions
        menu.Items.Add($"{L("Capture region")} ({_captureHotkey.DisplayName})", null, (_, _) => Dispatcher.Invoke(() => StartCapture()));
        menu.Items.Add($"{L("Capture and copy")} ({_captureAndCopyHotkey.DisplayName})", null, (_, _) => Dispatcher.Invoke(() => StartCapture(CaptureCompletionMode.CopyOnSelection)));
        menu.Items.Add(L("Capture active window"), null, (_, _) => Dispatcher.Invoke(CaptureActiveWindow));
        menu.Items.Add(L("Capture full screen"), null, (_, _) => Dispatcher.Invoke(CaptureFullScreen));
        menu.Items.Add($"{L("Custom capture")} ({_customCaptureHotkey.DisplayName})", null, (_, _) => Dispatcher.Invoke(OpenCustomCapture));
        menu.Items.Add(L("Repeat last region"), null, (_, _) => Dispatcher.Invoke(RepeatLastRegion));
        menu.Items.Add(new Forms.ToolStripSeparator());
        // Annotate / record / colour
        menu.Items.Add($"{L("Draw on screen")} ({_drawingHotkey.DisplayName})", null, (_, _) => Dispatcher.Invoke(() => StartCapture(CaptureCompletionMode.FullScreenDraw)));
        menu.Items.Add($"{L("Record region")} ({_recordingHotkey.DisplayName})", null, (_, _) => Dispatcher.Invoke(() => StartCapture(CaptureCompletionMode.RecordOnSelection)));
        menu.Items.Add($"{L("Colour picker")} ({_colorMagnifierHotkey.DisplayName})", null, (_, _) => Dispatcher.Invoke(() => StartCapture(CaptureCompletionMode.ColorPicker)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        // Pins
        menu.Items.Add($"{L("Pin clipboard")} ({_pasteHotkey.DisplayName})", null, (_, _) => Dispatcher.Invoke(PinClipboard));
        menu.Items.Add($"{L("Hide/show all pins")} ({_togglePinsHotkey.DisplayName})", null, (_, _) => Dispatcher.Invoke(PinnedImageWindow.ToggleAllVisibility));
        var images = new Forms.ToolStripMenuItem(L("Pins"));
        images.DropDownItems.Add(L("Hide/show all"), null, (_, _) => Dispatcher.Invoke(PinnedImageWindow.ToggleAllVisibility));
        images.DropDownItems.Add(L("Restore click interaction"), null, (_, _) => Dispatcher.Invoke(PinnedImageWindow.RestoreInteractions));
        images.DropDownItems.Add(L("Close all"), null, (_, _) => Dispatcher.Invoke(PinnedImageWindow.CloseAll));
        var groups = new Forms.ToolStripMenuItem(L("Switch group"));
        var currentGroup = SettingsService.Load().CurrentPinGroup;
        foreach (var groupName in SettingsService.Load().PinGroups)
        {
            var item = new Forms.ToolStripMenuItem(groupName) { Checked = groupName.Equals(currentGroup, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => Dispatcher.Invoke(() => PinnedImageWindow.SwitchGroup(groupName));
            groups.DropDownItems.Add(item);
        }
        images.DropDownItems.Add(groups);
        images.DropDownItems.Add(L("Solo selected"), null, (_, _) => Dispatcher.Invoke(PinnedImageWindow.ToggleSolo));
        menu.Items.Add(images);
        menu.Items.Add(new Forms.ToolStripSeparator());
        // Tools
        menu.Items.Add(L("Whiteboard"), null, (_, _) => Dispatcher.Invoke(() => OpenWhiteboard(false)));
        menu.Items.Add(L("Transparent whiteboard"), null, (_, _) => Dispatcher.Invoke(() => OpenWhiteboard(true)));
        menu.Items.Add(L("Capture history…"), null, (_, _) => Dispatcher.Invoke(OpenHistory));
        menu.Items.Add(L("Check for updates…"), null, (_, _) => Dispatcher.Invoke(() => _ = CheckForUpdatesAsync()));
        menu.Items.Add(L("Preferences…"), null, (_, _) => Dispatcher.Invoke(OpenPreferences));
        menu.Items.Add(L("Open SnapAnchor"), null, (_, _) => Dispatcher.Invoke(ShowDashboard));
        var disableItem = new Forms.ToolStripMenuItem(L("Disable hotkeys")) { Checked = !_hotkeysEnabled, CheckOnClick = true };
        disableItem.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            _hotkeysEnabled = !disableItem.Checked;
            UpdateHotkeyStatus(RegisterAllHotkeys());
        });
        menu.Items.Add(disableItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L("Exit"), null, (_, _) => Dispatcher.Invoke(ExitApplication));
    }

    internal void NotifyUpdateAvailable(UpdateCheckResult update)
    {
        _availableUpdate = update;
        RefreshTrayMenu();
        if (_trayIcon is null) return;
        var ready = UpdateService.TryLoadPending(update, out _);
        _trayIcon.ShowBalloonTip(7000,
            L(ready ? "SnapAnchor update ready" : "SnapAnchor update available"),
            ready
                ? LocalizationService.Format("SnapAnchor {0} is downloaded and ready. Click to choose when to restart.", update.Version)
                : LocalizationService.Format("SnapAnchor {0} is available. Click to review and download it.", update.Version),
            Forms.ToolTipIcon.Info);
    }

    private void RunAvailableUpdate()
    {
        if (_availableUpdate is null) return;
        ShowDashboard();
        try
        {
            if (UpdateWorkflowService.RunAvailable(this, _availableUpdate)) Application.Current.Shutdown();
            else RefreshTrayMenu();
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("update", ex.Message, ex);
            MessageBox.Show(this, LocalizationService.Format("Update check failed: {0}", ex.Message), L("SnapAnchor update"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ShowDashboard()
    {
        MoveDashboardToCurrentDesktop();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    internal void ShowFromExternalActivation() => ShowDashboard();

    private void MoveDashboardToCurrentDesktop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var desktop = _lastDesktopId ?? VirtualDesktopService.CurrentDesktopId();
        if (handle != IntPtr.Zero && desktop is { } desktopId)
            VirtualDesktopService.MoveWindowToDesktop(handle, desktopId);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_settings.KeepResponsive)
        {
            Hide();
            return;
        }
        Dispatcher.BeginInvoke(ExitApplication);
    }

    internal void DisposeLayoutPreview()
    {
        _sessionTimer.Stop();
        _desktopTimer.Stop();
        PinnedImageWindow.PinsChanged -= PinnedImageWindow_PinsChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    internal void PrepareForRestart()
    {
        _allowClose = true;
        _exitStarted = true;
        _sessionTimer.Stop();
        _desktopTimer.Stop();
        PinnedImageWindow.PinsChanged -= PinnedImageWindow_PinsChanged;
        UnregisterAllHotkeys();
        _source?.RemoveHook(WndProc);
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private async void ExitApplication()
    {
        if (_exitStarted) return;
        _exitStarted = true;
        _allowClose = true;
        _sessionTimer.Stop();
        _desktopTimer.Stop();
        PinnedImageWindow.PinsChanged -= PinnedImageWindow_PinsChanged;
        await PinnedImageWindow.SaveSessionAsync();
        UnregisterAllHotkeys();
        _source?.RemoveHook(WndProc);
        _trayIcon?.Dispose();
        Close();
    }

    private static void ShowError(string title, string message) =>
        System.Windows.MessageBox.Show(L(message), L(title), MessageBoxButton.OK, MessageBoxImage.Information);

    private static string L(string value) => LocalizationService.Current(value);
}
