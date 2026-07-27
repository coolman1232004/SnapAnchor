using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SnapAnchor.Services;

internal enum UpdatePackageKind
{
    Installer,
    Portable
}

internal sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string Message,
    string DownloadUrl = "",
    string Version = "",
    string Sha256 = "",
    UpdatePackageKind PackageKind = UpdatePackageKind.Installer,
    long DownloadSize = 0,
    string ReleaseNotes = "",
    string ReleasePageUrl = "",
    int MinimumWindowsBuild = 0,
    bool IsSigned = false);

internal enum UpdateProgressStage
{
    Downloading,
    Verifying,
    Preparing
}

internal sealed record UpdateProgressInfo(
    UpdateProgressStage Stage,
    double Fraction,
    long BytesReceived = 0,
    long TotalBytes = 0,
    double BytesPerSecond = 0);

internal sealed record PreparedUpdate(UpdateCheckResult Update, string PackagePath, string StagingDirectory = "");

internal static class UpdateService
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SnapAnchor";
    private const string PendingFileName = "pending-update.json";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    internal static bool IsPortableInstallation()
    {
        var executable = Environment.ProcessPath;
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        var registered = key?.GetValue("InstallLocation") as string;
        return IsPortableLocation(executable, registered);
    }

    internal static bool IsPortableLocation(string? executable, string? registeredInstallDirectory)
    {
        if (string.IsNullOrWhiteSpace(executable)) return true;
        var currentDirectory = Path.GetDirectoryName(Path.GetFullPath(executable));
        if (string.IsNullOrWhiteSpace(currentDirectory) || string.IsNullOrWhiteSpace(registeredInstallDirectory)) return true;
        return !Path.GetFullPath(currentDirectory).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(registeredInstallDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<UpdateCheckResult> CheckAsync(string? feedUrl, CancellationToken cancellationToken = default)
    {
        var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(feedUrl))
            return new UpdateCheckResult(false, LocalizationService.Format("SnapAnchor {0} is installed. The official GitHub update source is unavailable.", current.ToString(3)));
        if (!Uri.TryCreate(feedUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new UpdateCheckResult(false, LocalizationService.Current("The official update source is not a valid HTTPS address."));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd($"SnapAnchor/{current.ToString(3)}");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions, cancellationToken);
        if (manifest is null || !Version.TryParse(manifest.Version, out var published))
            return new UpdateCheckResult(false, LocalizationService.Current("The GitHub release did not contain a valid version."));
        if (published <= current)
            return new UpdateCheckResult(false, LocalizationService.Format("SnapAnchor {0} is up to date.", current.ToString(3)));

        var portable = IsPortableInstallation();
        var packageKind = portable ? UpdatePackageKind.Portable : UpdatePackageKind.Installer;
        var downloadUrl = portable
            ? ResolvePackageUrl(uri, manifest.PortableDownloadUrl, manifest.PortableFile)
            : ResolvePackageUrl(uri, manifest.DownloadUrl, manifest.InstallerFile);
        var sha256 = portable ? manifest.PortableSha256 : manifest.InstallerSha256;
        var size = portable ? manifest.PortableSize : manifest.InstallerSize;
        if (!UpdatePolicyService.IsValidSha256(sha256))
            return new UpdateCheckResult(false,
                LocalizationService.Current("The GitHub release did not contain a valid SHA-256 checksum."));
        if (!UpdatePolicyService.SupportsCurrentWindows(manifest.MinimumWindowsBuild))
            return new UpdateCheckResult(false,
                LocalizationService.Format(
                    "SnapAnchor {0} requires Windows build {1} or later. This computer is running build {2}.",
                    published.ToString(3),
                    manifest.MinimumWindowsBuild,
                    UpdatePolicyService.CurrentWindowsBuild));
        var edition = LocalizationService.Current(portable ? "portable copy" : "installed copy");
        var notes = LocalizationService.CurrentLanguage == LocalizationService.English
            ? manifest.ReleaseNotes ?? string.Empty
            : LocalizationService.Current("See the GitHub release page for full release notes.");
        var message = $"{LocalizationService.Format("SnapAnchor {0} is available for this {1}.", published.ToString(3), edition)}\n\n{notes}".Trim();
        if (string.IsNullOrWhiteSpace(downloadUrl))
            message += "\n\n" + LocalizationService.Current("The required update package is not attached to this GitHub release.");
        return new UpdateCheckResult(true, message, downloadUrl, published.ToString(3), sha256 ?? string.Empty,
            packageKind, size, notes, ReleasePage(uri, published.ToString(3)),
            manifest.MinimumWindowsBuild, manifest.Signed);
    }

    internal static async Task<PreparedUpdate> PrepareAsync(UpdateCheckResult update,
        IProgress<UpdateProgressInfo>? progress = null, CancellationToken cancellationToken = default)
    {
        var pending = await TryLoadPendingAsync(update, cancellationToken);
        if (pending is not null) return pending;
        var package = await DownloadPackageAsync(update, progress, cancellationToken);
        if (update.PackageKind == UpdatePackageKind.Installer)
        {
            var preparedInstaller = new PreparedUpdate(update, package);
            SavePending(preparedInstaller);
            return preparedInstaller;
        }

        return await PreparePortablePackageAsync(update, package, progress, cancellationToken);
    }

    private static async Task<PreparedUpdate> PreparePortablePackageAsync(
        UpdateCheckResult update,
        string package,
        IProgress<UpdateProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new UpdateProgressInfo(UpdateProgressStage.Preparing, 0));
        var stagingDirectory = Path.Combine(UpdateRoot(), $"Portable-{update.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            await ExtractArchiveSafelyAsync(package, stagingDirectory, cancellationToken);
            var stagedExecutable = Path.Combine(stagingDirectory, "SnapAnchor.exe");
            if (!File.Exists(stagedExecutable))
                throw new InvalidDataException(LocalizationService.Current("The portable GitHub package did not contain SnapAnchor.exe."));
            var actualVersion = FileVersionInfo.GetVersionInfo(stagedExecutable).FileVersion ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(update.Version) && !actualVersion.StartsWith(update.Version + ".", StringComparison.Ordinal))
                throw new InvalidDataException(LocalizationService.Format("The downloaded SnapAnchor version ({0}) did not match the expected version ({1}).", actualVersion, update.Version));
            var prepared = new PreparedUpdate(update, package, stagingDirectory);
            SavePending(prepared);
            return prepared;
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    internal static async Task<string> DownloadPackageAsync(UpdateCheckResult update,
        IProgress<UpdateProgressInfo>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(LocalizationService.Current("The GitHub update package must use HTTPS."));
        var root = UpdateRoot();
        Directory.CreateDirectory(root);
        var version = string.IsNullOrWhiteSpace(update.Version) ? "update" : update.Version;
        var fileName = update.PackageKind == UpdatePackageKind.Portable
            ? $"SnapAnchor-Portable-{version}.zip"
            : $"SnapAnchor-Setup-{version}.exe";
        var finalPath = Path.Combine(root, fileName);
        var temporaryPath = finalPath + ".download";

        if (File.Exists(finalPath))
        {
            progress?.Report(new UpdateProgressInfo(UpdateProgressStage.Verifying, 1, new FileInfo(finalPath).Length, new FileInfo(finalPath).Length));
            if (await UpdatePolicyService.HashMatchesAsync(finalPath, update.Sha256, cancellationToken)) return finalPath;
            File.Delete(finalPath);
        }

        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? update.DownloadSize;
        var clock = Stopwatch.StartNew();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
        {
            var buffer = new byte[131072];
            long received = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                received += count;
                var fraction = total > 0 ? received / (double)total : 0;
                var speed = clock.Elapsed.TotalSeconds > 0 ? received / clock.Elapsed.TotalSeconds : 0;
                progress?.Report(new UpdateProgressInfo(UpdateProgressStage.Downloading, fraction, received, total, speed));
            }
        }

        progress?.Report(new UpdateProgressInfo(UpdateProgressStage.Verifying, 1, new FileInfo(temporaryPath).Length, total));
        if (!await UpdatePolicyService.HashMatchesAsync(temporaryPath, update.Sha256, cancellationToken))
        {
            File.Delete(temporaryPath);
            throw new InvalidDataException(LocalizationService.Current("The downloaded GitHub package failed its SHA-256 integrity check."));
        }
        File.Move(temporaryPath, finalPath, true);
        return finalPath;
    }

    internal static async Task<string> DownloadPackageAsync(UpdateCheckResult update, IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        IProgress<UpdateProgressInfo>? adapter = progress is null ? null :
            new Progress<UpdateProgressInfo>(value => progress.Report(value.Fraction));
        return await DownloadPackageAsync(update, adapter, cancellationToken);
    }

    internal static bool LaunchPrepared(PreparedUpdate prepared)
    {
        if (prepared.Update.PackageKind == UpdatePackageKind.Installer)
        {
            Process.Start(new ProcessStartInfo(prepared.PackagePath) { UseShellExecute = true });
            ClearPending();
            return true;
        }

        var targetDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(targetDirectory)?.TrimEnd(Path.DirectorySeparatorChar);
        if (targetDirectory.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(LocalizationService.Current("A portable update cannot replace files in a drive root."));
        var stagedExecutable = Path.Combine(prepared.StagingDirectory, "SnapAnchor.exe");
        if (!File.Exists(stagedExecutable))
            throw new InvalidDataException(LocalizationService.Current("The prepared portable update is no longer available. Download it again."));

        var backupDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SnapAnchor", "PortableRollback", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
        var info = new ProcessStartInfo(stagedExecutable)
        {
            UseShellExecute = true,
            WorkingDirectory = prepared.StagingDirectory
        };
        info.ArgumentList.Add(PortableUpdateRequest.Command);
        AddArgument(info, "--parent-pid", Environment.ProcessId.ToString());
        AddArgument(info, "--source", prepared.StagingDirectory);
        AddArgument(info, "--target", targetDirectory);
        AddArgument(info, "--backup", backupDirectory);
        AddArgument(info, "--expected-version", prepared.Update.Version);
        AddArgument(info, "--previous-version", currentVersion);
        if (!CanWriteDirectory(targetDirectory)) info.Verb = "runas";
        if (Process.Start(info) is null)
            throw new InvalidOperationException(LocalizationService.Current("Windows could not start the portable update helper."));
        ClearPending();
        return true;
    }

    internal static bool HasPendingUpdate(UpdateCheckResult update) =>
        TryReadPending(update, out _);

    internal static async Task<PreparedUpdate?> TryLoadPendingAsync(
        UpdateCheckResult update,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadPending(update, out var data)) return null;
        if (!await UpdatePolicyService.HashMatchesAsync(data.PackagePath, update.Sha256, cancellationToken))
        {
            ClearPending();
            return null;
        }

        if (update.PackageKind == UpdatePackageKind.Installer)
            return new PreparedUpdate(update, data.PackagePath);

        // The archive is verified above. Re-extract it before use rather than
        // trusting a user-writable staging directory left by an earlier run.
        TryDeleteDirectory(data.StagingDirectory);
        return await PreparePortablePackageAsync(update, data.PackagePath, progress: null, cancellationToken);
    }

    private static bool TryReadPending(UpdateCheckResult update, out PendingUpdateData data)
    {
        data = null!;
        try
        {
            var path = Path.Combine(UpdateRoot(), PendingFileName);
            if (!File.Exists(path)) return false;
            data = JsonSerializer.Deserialize<PendingUpdateData>(File.ReadAllText(path), JsonOptions)!;
            if (data is null || !data.Version.Equals(update.Version, StringComparison.OrdinalIgnoreCase) ||
                data.PackageKind != update.PackageKind || !IsUnderUpdateRoot(data.PackagePath) || !File.Exists(data.PackagePath))
                return false;
            if (update.PackageKind == UpdatePackageKind.Portable &&
                !IsUnderUpdateRoot(data.StagingDirectory))
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void SavePending(PreparedUpdate prepared)
    {
        Directory.CreateDirectory(UpdateRoot());
        var data = new PendingUpdateData(prepared.Update.Version, prepared.Update.PackageKind,
            prepared.PackagePath, prepared.StagingDirectory, DateTime.UtcNow);
        AtomicFileService.WriteJson(Path.Combine(UpdateRoot(), PendingFileName), data);
    }

    internal static void ClearPending()
    {
        var path = Path.Combine(UpdateRoot(), PendingFileName);
        try { File.Delete(path); } catch { }
        try { File.Delete(AtomicFileService.BackupPath(path)); } catch { }
    }

    internal static void CleanupOldArtifacts()
    {
        try
        {
            var root = UpdateRoot();
            if (Directory.Exists(root))
                foreach (var directory in Directory.EnumerateDirectories(root, "Portable-*"))
                    if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddDays(-7)) TryDeleteDirectory(directory);

            var rollbackRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapAnchor", "PortableRollback");
            if (Directory.Exists(rollbackRoot))
                foreach (var directory in Directory.EnumerateDirectories(rollbackRoot).OrderByDescending(Directory.GetCreationTimeUtc).Skip(2))
                    TryDeleteDirectory(directory);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Log("update-cleanup", ex.Message, ex);
        }
    }

    internal static string ResolvePackageUrl(Uri feedUri, string? explicitUrl, string? packageFile)
    {
        if (Uri.TryCreate(explicitUrl, UriKind.Absolute, out var absolute) && absolute.Scheme == Uri.UriSchemeHttps)
            return absolute.AbsoluteUri;
        if (string.IsNullOrWhiteSpace(packageFile)) return string.Empty;
        var resolved = new Uri(feedUri, packageFile.Trim());
        return resolved.Scheme == Uri.UriSchemeHttps ? resolved.AbsoluteUri : string.Empty;
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return LocalizationService.Current("Unknown size");
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private static void AddArgument(ProcessStartInfo info, string name, string value)
    {
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(value);
    }

    private static bool CanWriteDirectory(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".snapanchor-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static async Task ExtractArchiveSafelyAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(LocalizationService.Current("The portable update contained an unsafe path."));
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var source = entry.Open();
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
            await source.CopyToAsync(output, cancellationToken);
        }
    }

    private static string ReleasePage(Uri feed, string version)
    {
        var marker = "/releases/";
        var index = feed.AbsoluteUri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index > 0 ? feed.AbsoluteUri[..(index + marker.Length)] + "tag/v" + version : "https://github.com/coolman1232004/SnapAnchor/releases";
    }

    private static bool IsUnderUpdateRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var root = Path.GetFullPath(UpdateRoot()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    internal static string UpdateRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapAnchor", "Updates");

    private sealed record PendingUpdateData(string Version, UpdatePackageKind PackageKind,
        string PackagePath, string StagingDirectory, DateTime PreparedUtc);

    private sealed class UpdateManifest
    {
        public string Version { get; set; } = string.Empty;
        public string? DownloadUrl { get; set; }
        public string? PortableDownloadUrl { get; set; }
        public string? InstallerFile { get; set; }
        public string? PortableFile { get; set; }
        public string? ReleaseNotes { get; set; }
        public string? InstallerSha256 { get; set; }
        public string? PortableSha256 { get; set; }
        public long InstallerSize { get; set; }
        public long PortableSize { get; set; }
        public int MinimumWindowsBuild { get; set; }
        public bool Signed { get; set; }
    }
}
