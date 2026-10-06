using SnapAnchor.Models;
using System.IO;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace SnapAnchor.Services;

internal sealed record PinSessionSnapshot(BitmapSource Image, PinSessionItem Item);

internal static class PinSessionService
{
    private static readonly string Root = Environment.GetEnvironmentVariable("SNAPANCHOR_SESSION_ROOT") is { Length: > 0 } testRoot
        ? Path.GetFullPath(testRoot)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapAnchor", "PinSession");
    private static readonly object QueueSync = new();
    private static PendingWrite? _pending;
    private static Task _worker = Task.CompletedTask;
    private static bool _workerRunning;
    private static string _lastFingerprint = string.Empty;
    private static readonly ConditionalWeakTable<BitmapSource, CachedImage> ImageFiles = new();

    public static string? LastError { get; private set; }

    public static Task QueueSave(IReadOnlyList<PinSessionSnapshot> pins, string fingerprint)
        => Queue(new PendingWrite(pins, fingerprint, Clear: false));

    public static Task QueueClear() => Queue(new PendingWrite([], "__cleared__", Clear: true));

    private static Task Queue(PendingWrite write)
    {
        lock (QueueSync)
        {
            if (!_workerRunning && write.Fingerprint == _lastFingerprint && _pending is null) return _worker;
            _pending = write;
            if (!_workerRunning)
            {
                _workerRunning = true;
                _worker = Task.Run(DrainQueue);
            }
            return _worker;
        }
    }

    private static async Task DrainQueue()
    {
        while (true)
        {
            // Collapse bursts of position/opacity changes; awaiting the worker also flushes shutdown saves.
            await Task.Delay(150).ConfigureAwait(false);
            PendingWrite? write;
            lock (QueueSync)
            {
                write = _pending;
                _pending = null;
                if (write is null)
                {
                    _workerRunning = false;
                    return;
                }
            }

            try
            {
                lock (QueueSync) { if (write.Fingerprint == _lastFingerprint) continue; }
                if (write.Clear) ClearCore(Root);
                else SaveCore(Root, write.Pins);
                lock (QueueSync) _lastFingerprint = write.Fingerprint;
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }
    }

    internal static void SaveCore(string root, IReadOnlyList<PinSessionSnapshot> pins)
    {
        Directory.CreateDirectory(root);
        var pointerPath = Path.Combine(root, "current.json");
        AtomicFileService.TryReadJson<SessionPointer>(pointerPath, out var previousPointer);
        var generationName = $"gen_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
        var generationPath = Path.Combine(root, generationName);
        Directory.CreateDirectory(generationPath);
        var committed = false;

        try
        {
            var items = new List<PinSessionItem>(pins.Count);
            for (var index = 0; index < pins.Count; index++)
            {
                var snapshot = pins[index];
                snapshot.Item.FileName = $"pin_{index:D4}.png";
                var destination = Path.Combine(generationPath, snapshot.Item.FileName);
                if (ImageFiles.TryGetValue(snapshot.Image, out var cached) && cached.IsUnchanged())
                    File.Copy(cached.Path, destination);
                else
                {
                    CaptureService.SavePng(snapshot.Image, destination);
                    // Decode each new image once, rather than every unchanged pin on every save.
                    _ = LoadBitmap(destination);
                }
                items.Add(snapshot.Item);
            }
            AtomicFileService.WriteJson(Path.Combine(generationPath, "session.json"), items);
            if (!AtomicFileService.TryReadJson<List<PinSessionItem>>(Path.Combine(generationPath, "session.json"), out var verified) || verified?.Count != items.Count)
                throw new InvalidDataException("The new pin-session backup could not be verified.");

            var pointer = new SessionPointer(generationName, previousPointer?.Current);
            AtomicFileService.WriteJson(pointerPath, pointer);
            committed = true;
            for (var index = 0; index < pins.Count; index++)
            {
                ImageFiles.Remove(pins[index].Image);
                ImageFiles.Add(pins[index].Image, new CachedImage(Path.Combine(generationPath, items[index].FileName)));
            }
            CleanupOldGenerations(root, pointer);
        }
        catch
        {
            if (!committed) TryDeleteDirectory(generationPath);
            throw;
        }
    }

    public static IReadOnlyList<(BitmapSource Image, PinSessionItem Item)> Load() => LoadCore(Root);

    internal static IReadOnlyList<(BitmapSource Image, PinSessionItem Item)> LoadCore(string root)
    {
        try
        {
            var pointerPath = Path.Combine(root, "current.json");
            if (AtomicFileService.TryReadJson<SessionPointer>(pointerPath, out var pointer) && pointer is not null)
            {
                foreach (var generation in new[] { pointer.Current, pointer.Previous }.Where(name => !string.IsNullOrWhiteSpace(name)))
                    if (LoadGeneration(root, generation!) is { } loaded) return loaded;
            }

            if (Directory.Exists(root))
            {
                foreach (var directory in Directory.EnumerateDirectories(root, "gen_*").OrderByDescending(Directory.GetCreationTimeUtc))
                    if (LoadGeneration(root, Path.GetFileName(directory)) is { } recovered) return recovered;
            }

            return LoadLegacy(root);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<(BitmapSource Image, PinSessionItem Item)>? LoadGeneration(string root, string generationName)
    {
        try
        {
            if (!IsSafeGenerationName(generationName)) return null;
            var generationPath = Path.Combine(root, generationName);
            var indexPath = Path.Combine(generationPath, "session.json");
            if (!AtomicFileService.TryReadJson<List<PinSessionItem>>(indexPath, out var items) || items is null) return null;
            var result = new List<(BitmapSource, PinSessionItem)>(items.Count);
            foreach (var item in items)
            {
                var path = SafePinPath(generationPath, item.FileName);
                if (path is null || !File.Exists(path)) return null;
                result.Add((LoadBitmap(path), item));
            }
            return result;
        }
        // A bad PNG invalidates this generation only; LoadCore can still recover the previous one.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or FormatException)
        { return null; }
    }

    private static IReadOnlyList<(BitmapSource Image, PinSessionItem Item)> LoadLegacy(string root)
    {
        var indexPath = Path.Combine(root, "session.json");
        if (!AtomicFileService.TryReadJson<List<PinSessionItem>>(indexPath, out var items) || items is null) return [];
        var result = new List<(BitmapSource, PinSessionItem)>();
        foreach (var item in items)
        {
            var path = SafePinPath(root, item.FileName);
            if (path is null || !File.Exists(path)) continue;
            try { result.Add((LoadBitmap(path), item)); }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FormatException or System.Runtime.InteropServices.COMException) { }
        }
        return result;
    }

    private static BitmapSource LoadBitmap(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string? SafePinPath(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName) return null;
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static bool IsSafeGenerationName(string name)
        => name.StartsWith("gen_", StringComparison.Ordinal) && Path.GetFileName(name) == name;

    private static void CleanupOldGenerations(string root, SessionPointer pointer)
    {
        var keep = new HashSet<string>(new[] { pointer.Current, pointer.Previous }.Where(name => !string.IsNullOrWhiteSpace(name))!, StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root, "gen_*"))
            if (!keep.Contains(Path.GetFileName(directory))) TryDeleteDirectory(directory);
        foreach (var legacy in Directory.EnumerateFiles(root, "pin_*.png"))
            try { File.Delete(legacy); } catch (IOException) { }
        var legacyIndex = Path.Combine(root, "session.json");
        try { if (File.Exists(legacyIndex)) File.Delete(legacyIndex); } catch (IOException) { }
    }

    internal static void ClearCore(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record PendingWrite(IReadOnlyList<PinSessionSnapshot> Pins, string Fingerprint, bool Clear);
    private sealed record SessionPointer(string Current, string? Previous);
    private sealed class CachedImage(string path)
    {
        public string Path { get; } = path;
        private readonly long _length = new FileInfo(path).Length;
        private readonly DateTime _written = File.GetLastWriteTimeUtc(path);
        public bool IsUnchanged() => File.Exists(Path) && new FileInfo(Path).Length == _length && File.GetLastWriteTimeUtc(Path) == _written;
    }
}
