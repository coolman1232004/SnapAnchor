using SnapAnchor.Windows;
using System.Windows;

namespace SnapAnchor.Services;

internal static class UpdateWorkflowService
{
    internal static async Task<bool> CheckAndRunAsync(Window owner, string? feedUrl, bool automatic)
    {
        var update = await UpdateService.CheckAsync(feedUrl);
        if (!update.UpdateAvailable || string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            if (!automatic)
                MessageBox.Show(owner, update.Message, LocalizationService.Current("SnapAnchor update"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return await RunAvailableAsync(owner, update);
    }

    internal static async Task<bool> RunAvailableAsync(Window owner, UpdateCheckResult update)
    {
        PreparedUpdate? prepared;
        var pending = await UpdateService.TryLoadPendingAsync(update);
        if (pending is not null)
        {
            prepared = pending;
        }
        else
        {
            if (!UpdateAvailableWindow.Ask(owner, update)) return false;
            prepared = UpdateProgressWindow.Run(owner, update);
            if (prepared is null) return false;
        }

        if (UpdateReadyWindow.Ask(owner, prepared) != UpdateReadyDecision.ApplyNow)
        {
            UpdateService.SavePending(prepared);
            return false;
        }
        return UpdateService.LaunchPrepared(prepared);
    }
}
