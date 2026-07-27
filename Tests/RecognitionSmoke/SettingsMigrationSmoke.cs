using SnapAnchor.Services;

namespace SnapAnchor.RecognitionSmoke;

internal static class SettingsMigrationSmoke
{
    internal static void Run()
    {
        var repairedDetection = SettingsService.Normalize(new AppSettings
        {
            SettingsSchemaVersion = 0,
            ShowCaptureSize = false,
            ShowElementDetection = false,
            ShowColorSampler = false
        });
        SmokeAssert.Require(
            repairedDetection.ShowElementDetection == true,
            "schema 1 repairs the legacy detection preference");
        SmokeAssert.Require(
            repairedDetection.EnableColorMagnifier == false &&
            repairedDetection.ShowColorSampler == false,
            "schema 2 preserves a disabled legacy color sampler");

        var current = SettingsService.Normalize(new AppSettings
        {
            SettingsSchemaVersion = AppSettings.CurrentSettingsSchemaVersion,
            ShowElementDetection = false,
            ShowColorSampler = true,
            EnableColorMagnifier = false
        });
        SmokeAssert.Require(
            current.ShowElementDetection == false,
            "current detection choices remain explicit");
        SmokeAssert.Require(
            current.EnableColorMagnifier == false &&
            current.ShowColorSampler == false,
            "the current magnifier property remains authoritative");

        Console.WriteLine("SETTINGS MIGRATION: detection repair and legacy magnifier choice preservation verified");
    }
}
