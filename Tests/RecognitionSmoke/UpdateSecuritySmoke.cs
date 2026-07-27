using SnapAnchor.Services;
using System.IO;
using System.Security.Cryptography;

namespace SnapAnchor.RecognitionSmoke;

internal static class UpdateSecuritySmoke
{
    internal static void Run()
    {
        var hasOfficialFeed = Uri.TryCreate(
            AppSettings.DefaultUpdateFeedUrl,
            UriKind.Absolute,
            out var updateFeed);
        SmokeAssert.Require(
            new AppSettings().UpdateFeedUrl == AppSettings.DefaultUpdateFeedUrl &&
            hasOfficialFeed &&
            updateFeed!.Scheme == Uri.UriSchemeHttps &&
            updateFeed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase),
            "the update channel is the official GitHub HTTPS feed");

        var dailyCheckNow = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        SmokeAssert.Require(
            !new AppSettings().CheckUpdatesDaily &&
            UpdateCheckScheduleService.IsDailyCheckDue(null, dailyCheckNow, TimeZoneInfo.Utc) &&
            !UpdateCheckScheduleService.IsDailyCheckDue(dailyCheckNow.AddHours(-6), dailyCheckNow, TimeZoneInfo.Utc) &&
            UpdateCheckScheduleService.IsDailyCheckDue(dailyCheckNow.AddDays(-1), dailyCheckNow, TimeZoneInfo.Utc) &&
            !UpdateCheckScheduleService.IsDailyCheckDue(dailyCheckNow.AddHours(1), dailyCheckNow, TimeZoneInfo.Utc),
            "startup and once-per-day update schedules remain deterministic");

        var installedExecutable = Path.Combine(Path.GetTempPath(), "SnapAnchorInstalled", "SnapAnchor.exe");
        var portableExecutable = Path.Combine(Path.GetTempPath(), "SnapAnchorPortable", "SnapAnchor.exe");
        var portableUrl = UpdateService.ResolvePackageUrl(updateFeed!, null, "SnapAnchor-Portable-win-x64.zip");
        SmokeAssert.Require(
            !UpdateService.IsPortableLocation(installedExecutable, Path.GetDirectoryName(installedExecutable)) &&
            UpdateService.IsPortableLocation(portableExecutable, Path.GetDirectoryName(installedExecutable)) &&
            portableUrl.Equals(
                "https://github.com/coolman1232004/SnapAnchor/releases/latest/download/SnapAnchor-Portable-win-x64.zip",
                StringComparison.OrdinalIgnoreCase),
            "installed and portable update routing remains correct");
        SmokeAssert.Require(
            UpdateService.ResolvePackageUrl(updateFeed!, "http://example.com/update.exe", null) == string.Empty,
            "non-HTTPS update packages are rejected");

        var probe = Path.Combine(Path.GetTempPath(), $"snapanchor-update-hash-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(probe, [1, 2, 3, 4, 5]);
            var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(probe)));
            SmokeAssert.Require(
                UpdatePolicyService.IsValidSha256(expected) &&
                !UpdatePolicyService.IsValidSha256(string.Empty) &&
                !UpdatePolicyService.IsValidSha256(new string('Z', 64)) &&
                UpdatePolicyService.HashMatches(probe, expected) &&
                !UpdatePolicyService.HashMatches(probe, new string('0', 64)),
                "update checksums are mandatory and fail closed");
        }
        finally
        {
            File.Delete(probe);
        }

        SmokeAssert.Require(
            UpdatePolicyService.SupportsCurrentWindows(17_763, 22_631) &&
            !UpdatePolicyService.SupportsCurrentWindows(22_631, 17_763),
            "minimum Windows build requirements are enforced");

        Console.WriteLine("UPDATE SECURITY: HTTPS routing, mandatory hashes, compatibility and schedules verified");
    }
}
