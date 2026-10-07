using System.Windows;
using System.IO;
using SnapAnchor.Services;
using SnapAnchor.Models;
using SnapAnchor.Windows;
using System.Windows.Threading;

namespace SnapAnchor;

public partial class App : System.Windows.Application
{
    private SingleInstanceService? _singleInstance;
    private DispatcherTimer? _dailyUpdateTimer;
    private bool _automaticUpdateCheckRunning;

    protected override void OnStartup(StartupEventArgs e)
    {
        BrandMigrationService.MigrateLocalData();
        var settings = SettingsService.Load();
        LocalizationService.Configure(settings.UiLanguage);
        LocalizationService.EnableAutomaticLocalization();
        ApplicationThemeService.Apply(settings.AppearanceMode);
        if (PortableUpdateRequest.IsUpdateCommand(e.Args))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!PortableUpdateRequest.TryParse(e.Args, out var request, out var updateError))
            {
                MessageBox.Show(updateError, LocalizationService.Current("Portable update failed"), MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
                return;
            }
            var updater = new PortableUpdateWindow(request);
            MainWindow = updater;
            updater.Show();
            return;
        }

        var updateNotice = PortableUpdateService.ExtractStartupNotice(e.Args, out var applicationArguments);
        var startupArguments = RestartService.PrepareStartupArguments(applicationArguments);
        var recoveredPreviousCrash = DiagnosticsService.Start();
        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticsService.Log("ui-error", args.Exception.Message, args.Exception);
            MessageBox.Show(LocalizationService.Format(
                    "SnapAnchor recovered from an unexpected error.\n\n{0}\n\nA diagnostic log was saved locally.", args.Exception.Message),
                LocalizationService.Current("SnapAnchor recovered"), MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagnosticsService.Log("fatal", "Unhandled application error", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticsService.Log("background-error", args.Exception.Message, args.Exception);
            args.SetObserved();
        };
        if (!AppCommand.TryParse(startupArguments, out var startupCommand, out var commandError))
        {
            MessageBox.Show(commandError, LocalizationService.Current("SnapAnchor command"), MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(2);
            return;
        }
        ToolbarThemeService.Apply(settings.ToolbarSizeMode);
        if (ElevationService.NeedsElevation(settings.RunAsAdministrator, ElevationService.IsCurrentProcessElevated()))
        {
            if (ElevationService.TryRelaunchElevated(startupArguments, out var elevationError))
            {
                Shutdown();
                return;
            }
            if (!string.IsNullOrWhiteSpace(elevationError))
                MessageBox.Show(elevationError, LocalizationService.Current("SnapAnchor administrator mode"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        var executablePath = Environment.ProcessPath ?? string.Empty;
        var developmentBuild = executablePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        _singleInstance = new SingleInstanceService(developmentBuild ? "SnapAnchor.Desktop.x64.Development" : "SnapAnchor.Desktop.x64");
        if (!_singleInstance.IsPrimaryInstance)
        {
            _singleInstance.SignalPrimaryInstance(startupCommand);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        _singleInstance.ActivationRequested += (_, _) => Dispatcher.BeginInvoke(window.ShowFromExternalActivation);
        _singleInstance.CommandReceived += (_, args) =>
        {
            if (args.Command.Kind != AppCommandKind.Activate)
                Dispatcher.BeginInvoke(() => window.ExecuteCommand(args.Command));
        };
        _singleInstance.StartListening();
        window.Show();
        UpdateService.CleanupOldArtifacts();
        if (updateNotice is not null)
            Dispatcher.BeginInvoke(() => ShowUpdateResult(window, updateNotice));
        StartAutomaticUpdateChecks(window, settings);
        if (recoveredPreviousCrash)
            Dispatcher.BeginInvoke(() => MessageBox.Show(window,
                LocalizationService.Current("SnapAnchor recovered after the previous unexpected close. Open pins were restored from the last verified backup.\n\nYou can review the local diagnostic log in Preferences > About."),
                LocalizationService.Current("SnapAnchor recovery"), MessageBoxButton.OK, MessageBoxImage.Information));
        if (startupCommand.Kind != AppCommandKind.Activate)
            Dispatcher.BeginInvoke(() => window.ExecuteCommand(startupCommand));
    }

    private void StartAutomaticUpdateChecks(Window owner, AppSettings startupSettings)
    {
        if (!string.IsNullOrWhiteSpace(startupSettings.UpdateFeedUrl) &&
            (startupSettings.CheckUpdatesOnStartup ||
             startupSettings.CheckUpdatesDaily && UpdateCheckScheduleService.IsDailyCheckDue(DateTimeOffset.UtcNow)))
        {
            Dispatcher.BeginInvoke(async () => await CheckForUpdatesAutomaticallyAsync(owner, startupSettings));
        }

        _dailyUpdateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(30)
        };
        _dailyUpdateTimer.Tick += async (_, _) =>
        {
            var currentSettings = SettingsService.Load();
            if (currentSettings.CheckUpdatesDaily && !string.IsNullOrWhiteSpace(currentSettings.UpdateFeedUrl) &&
                UpdateCheckScheduleService.IsDailyCheckDue(DateTimeOffset.UtcNow))
            {
                await CheckForUpdatesAutomaticallyAsync(owner, currentSettings);
            }
        };
        _dailyUpdateTimer.Start();
    }

    private async Task CheckForUpdatesAutomaticallyAsync(Window owner, AppSettings settings)
    {
        if (_automaticUpdateCheckRunning) return;
        _automaticUpdateCheckRunning = true;
        try
        {
            var update = await UpdateService.CheckAsync(settings.UpdateFeedUrl);
            if (update.UpdateAvailable && !string.IsNullOrWhiteSpace(update.DownloadUrl) && owner is MainWindow dashboard)
                dashboard.NotifyUpdateAvailable(update);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("automatic-update", ex.Message, ex);
        }
        finally
        {
            UpdateCheckScheduleService.MarkChecked(DateTimeOffset.UtcNow);
            _automaticUpdateCheckRunning = false;
        }
    }

    private static void ShowUpdateResult(Window owner, UpdateStartupNotice notice)
    {
        if (notice.Success)
        {
            MessageBox.Show(owner, notice.Message, LocalizationService.Current("SnapAnchor updated successfully"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var log = string.IsNullOrWhiteSpace(notice.LogPath) ? string.Empty :
            "\n\n" + LocalizationService.Format("Update log: {0}", notice.LogPath);
        MessageBox.Show(owner,
            LocalizationService.Format("SnapAnchor could not apply update {0}. The previous version was restored.\n\n{1}", notice.CurrentVersion, notice.Message) + log,
            LocalizationService.Current("Portable update failed"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _dailyUpdateTimer?.Stop();
        _singleInstance?.Dispose();
        DiagnosticsService.MarkCleanExit();
        base.OnExit(e);
    }
}
