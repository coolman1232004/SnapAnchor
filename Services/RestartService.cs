using System.Diagnostics;
using System.Windows;
using SnapAnchor.Windows;

namespace SnapAnchor.Services;

internal static class RestartService
{
    private const string RestartArgument = "--restart-after";

    internal static string[] PrepareStartupArguments(IReadOnlyList<string> arguments)
    {
        var remaining = new List<string>(arguments.Count);
        int? previousProcessId = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals(RestartArgument, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Count && int.TryParse(arguments[index + 1], out var processId))
            {
                previousProcessId = processId;
                index++;
                continue;
            }
            remaining.Add(arguments[index]);
        }

        if (previousProcessId is { } id)
        {
            try
            {
                using var previous = Process.GetProcessById(id);
                previous.WaitForExit(15000);
            }
            catch (ArgumentException)
            {
                // The previous process already exited.
            }
            catch (Exception ex)
            {
                DiagnosticsService.Log("restart-wait", ex.Message, ex);
            }
        }

        return remaining.ToArray();
    }

    internal static async Task RestartApplicationAsync()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;

        try
        {
            await PinnedImageWindow.SaveSessionAsync();
            var info = new ProcessStartInfo(executable) { UseShellExecute = true };
            info.ArgumentList.Add(RestartArgument);
            info.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(info);
            if (Application.Current.MainWindow is MainWindow mainWindow)
                mainWindow.PrepareForRestart();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("restart", ex.Message, ex);
            MessageBox.Show(LocalizationService.Current("SnapAnchor could not restart automatically. Please close and open it again."),
                LocalizationService.Current("Restart required"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
