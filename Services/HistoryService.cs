using SnapAnchor.Models;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapAnchor.Services;

internal static class HistoryService
{
    private static readonly object Sync = new();
    private static string Root => Environment.GetEnvironmentVariable("SNAPANCHOR_HISTORY_ROOT") is { Length: > 0 } testRoot
        ? Path.GetFullPath(testRoot)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapAnchor", "History");
    private static string IndexPath => Path.Combine(Root, "history.json");

    public static string HistoryDirectory => Root;

    private static BitmapSource Frozen(BitmapSource image)
    {
        if (image.IsFrozen) return image;
        var copy = image.Clone();
        copy.Freeze();
        return copy;
    }

    public static Task<CaptureRecord> AddAsync(BitmapSource image, Int32Rect? sourceRegion = null, BitmapSource? contextImage = null, string sourceKind = "Capture")
    {
        var frozen = Frozen(image);
        var context = contextImage is null ? null : Frozen(contextImage);
        return PersistenceQueue.Run(() => Add(frozen, sourceRegion, context, sourceKind));
    }

    public static Task<CaptureRecord> AddRecordingAsync(BitmapSource preview, string mediaPath, TimeSpan duration, int frameCount)
    {
        var frozen = Frozen(preview);
        return PersistenceQueue.Run(() => AddRecording(frozen, mediaPath, duration, frameCount));
    }

    public static Task SaveAnnotationDocumentAsync(string id, BitmapSource baseImage, BitmapSource flattenedImage, IReadOnlyList<AnnotationItem> items, string? sourceKind = null)
    {
        var source = Frozen(baseImage);
        var flattened = Frozen(flattenedImage);
        var snapshot = items.Select(item => item.Clone()).ToList();
        return PersistenceQueue.Run(() => SaveAnnotationDocument(id, source, flattened, snapshot, sourceKind));
    }

    public static Task<string> StoreDocumentAsync(string? id, BitmapSource baseImage, BitmapSource flattenedImage,
        IReadOnlyList<AnnotationItem> items, string sourceKind, Int32Rect? region = null, BitmapSource? contextImage = null)
    {
        var source = Frozen(baseImage);
        var flattened = Frozen(flattenedImage);
        var context = contextImage is null ? null : Frozen(contextImage);
        var snapshot = items.Select(item => item.Clone()).ToList();
        return PersistenceQueue.Run(() =>
        {
            var recordId = id;
            if (string.IsNullOrWhiteSpace(recordId)) recordId = Add(source, region, context, sourceKind).Id;
            SaveAnnotationDocument(recordId, source, flattened, snapshot, sourceKind);
            return recordId;
        });
    }

    public static Task<CaptureRecord> SaveRecognitionAsync(BitmapSource image, string text, string barcode, string format)
    {
        var frozen = Frozen(image);
        return PersistenceQueue.Run(() => SaveRecognition(frozen, text, barcode, format));
    }

    public static CaptureRecord Add(
        BitmapSource image,
        Int32Rect? sourceRegion = null,
        BitmapSource? contextImage = null,
        string sourceKind = "Capture")
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Root);
            var state = LoadState();
            var record = new CaptureRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.Now,
                Width = image.PixelWidth,
                Height = image.PixelHeight,
                SourceKind = sourceKind
            };
            record.FileName = $"{record.CreatedAt:yyyyMMdd_HHmmss_fff}_{record.Id[..6]}.png";
            CaptureService.SavePng(image, Path.Combine(Root, record.FileName));
            var contextIsDistinct = contextImage is not null && (sourceRegion is not Int32Rect contextRegion ||
                contextRegion.X != 0 || contextRegion.Y != 0 || contextRegion.Width != contextImage.PixelWidth || contextRegion.Height != contextImage.PixelHeight);
            if (contextImage is not null && contextIsDistinct)
            {
                record.ContextFileName = $"{Path.GetFileNameWithoutExtension(record.FileName)}_context.jpg";
                record.ContextWidth = contextImage.PixelWidth;
                record.ContextHeight = contextImage.PixelHeight;
                CaptureService.SaveImage(contextImage, Path.Combine(Root, record.ContextFileName), 82);
            }
            state.Records.Insert(0, record);

            if (sourceRegion is Int32Rect region && region.Width > 0 && region.Height > 0)
            {
                record.SourceRegion = new CaptureRegion { X = region.X, Y = region.Y, Width = region.Width, Height = region.Height };
                RememberRegion(state, region);
            }

            Trim(state, SettingsService.Load().HistoryLimit);
            SaveState(state);
            return record;
        }
    }

    public static IReadOnlyList<CaptureRecord> List(bool includeDeleted = false)
    {
        lock (Sync)
        {
            var state = LoadState();
            var changed = state.Records.RemoveAll(record => !File.Exists(PathFor(record))) > 0;
            foreach (var record in state.Records.Where(record => !string.IsNullOrWhiteSpace(record.ContextFileName)).ToList())
            {
                if (File.Exists(Path.Combine(Root, record.ContextFileName))) continue;
                record.ContextFileName = string.Empty;
                record.ContextWidth = 0;
                record.ContextHeight = 0;
                record.SourceRegion = null;
                changed = true;
            }
            changed |= PurgeExpiredDeleted(state, 30);
            if (changed) SaveState(state);
            return state.Records
                .Where(record => includeDeleted || !record.IsDeleted)
                .OrderByDescending(record => record.IsFavorite)
                .ThenByDescending(record => record.CreatedAt)
                .ToList();
        }
    }

    public static CaptureRegion? LastRegion()
    {
        lock (Sync)
        {
            var region = LoadState().LastRegion;
            return region is null ? null : new CaptureRegion { X = region.X, Y = region.Y, Width = region.Width, Height = region.Height };
        }
    }

    public static IReadOnlyList<CaptureRegion> RecentRegions()
    {
        lock (Sync)
        {
            var state = LoadState();
            var regions = state.RegionHistory.Count > 0
                ? state.RegionHistory
                : state.LastRegion is null ? [] : [state.LastRegion];
            return regions.Select(CloneRegion).ToList();
        }
    }

    public static void RememberLastRegion(Int32Rect region)
    {
        if (region.Width <= 0 || region.Height <= 0) return;
        lock (Sync)
        {
            var state = LoadState();
            RememberRegion(state, region);
            SaveState(state);
        }
    }

    private static void RememberRegion(CaptureHistoryState state, Int32Rect region)
    {
        var remembered = new CaptureRegion { X = region.X, Y = region.Y, Width = region.Width, Height = region.Height };
        state.LastRegion = CloneRegion(remembered);
        state.RegionHistory ??= [];
        state.RegionHistory.RemoveAll(existing => existing.X == remembered.X && existing.Y == remembered.Y && existing.Width == remembered.Width && existing.Height == remembered.Height);
        state.RegionHistory.Insert(0, remembered);
        if (state.RegionHistory.Count > 30) state.RegionHistory.RemoveRange(30, state.RegionHistory.Count - 30);
    }

    private static CaptureRegion CloneRegion(CaptureRegion region) =>
        new() { X = region.X, Y = region.Y, Width = region.Width, Height = region.Height };

    public static BitmapSource LoadImage(CaptureRecord record, int decodeWidth = 0, int decodeHeight = 0)
        => LoadImagePath(PathFor(record), decodeWidth, decodeHeight);

    public static BitmapSource LoadBaseImage(CaptureRecord record)
        => LoadImagePath(string.IsNullOrWhiteSpace(record.BaseFileName) ? PathFor(record) : Path.Combine(Root, record.BaseFileName), 0);

    public static BitmapSource? LoadContextImage(CaptureRecord record, int decodeWidth = 0, int decodeHeight = 0)
    {
        if (string.IsNullOrWhiteSpace(record.ContextFileName)) return null;
        var path = Path.Combine(Root, record.ContextFileName);
        return File.Exists(path) ? LoadImagePath(path, decodeWidth, decodeHeight) : null;
    }

    public static BitmapSource? LoadContextPreview(CaptureRecord record, int decodeWidth = 240, int decodeHeight = 0)
    {
        var context = LoadContextImage(record, decodeWidth, decodeHeight);
        if (context is null || record.SourceRegion is not { Width: > 0, Height: > 0 } region) return context;
        var sourceWidth = record.ContextWidth > 0 ? record.ContextWidth : context.PixelWidth;
        var sourceHeight = record.ContextHeight > 0 ? record.ContextHeight : context.PixelHeight;
        var scaleX = context.PixelWidth / Math.Max(1d, sourceWidth);
        var scaleY = context.PixelHeight / Math.Max(1d, sourceHeight);
        var selected = new Rect(region.X * scaleX, region.Y * scaleY, region.Width * scaleX, region.Height * scaleY);
        selected.Intersect(new Rect(0, 0, context.PixelWidth, context.PixelHeight));
        if (selected.IsEmpty) return context;

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(context, new Rect(0, 0, context.PixelWidth, context.PixelHeight));
            var fill = new SolidColorBrush(Color.FromArgb(30, 51, 136, 255));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(51, 136, 255)), Math.Max(2, context.PixelWidth / 240d * 2));
            drawing.DrawRectangle(fill, pen, selected);
        }
        var preview = new RenderTargetBitmap(context.PixelWidth, context.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        preview.Render(visual);
        preview.Freeze();
        return preview;
    }

    public static IReadOnlyList<AnnotationItem> LoadAnnotations(CaptureRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.AnnotationFileName)) return [];
        lock (Sync)
        {
            var path = Path.Combine(Root, record.AnnotationFileName);
            return AtomicFileService.TryReadJson<AnnotationDocument>(path, out var document)
                ? document!.Items
                : [];
        }
    }

    private static BitmapSource LoadImagePath(string path, int decodeWidth, int decodeHeight = 0)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        if (decodeHeight > 0) image.DecodePixelHeight = decodeHeight;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static CaptureRecord? Find(string id)
    {
        lock (Sync) return LoadState().Records.FirstOrDefault(record => record.Id == id);
    }

    public static void UpdateMetadata(string id, string? title = null, bool? favorite = null, IEnumerable<string>? tags = null)
    {
        lock (Sync)
        {
            var state = LoadState();
            var record = state.Records.FirstOrDefault(candidate => candidate.Id == id);
            if (record is null) return;
            if (title is not null) record.Title = title.Trim();
            if (favorite is not null) record.IsFavorite = favorite.Value;
            if (tags is not null)
                record.Tags = tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim())
                    .Distinct(StringComparer.CurrentCultureIgnoreCase).Take(12).ToList();
            SaveState(state);
        }
    }

    public static CaptureRecord SaveRecognition(BitmapSource image, string recognizedText, string barcodeText, string barcodeFormat)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Root);
            var state = LoadState();
            var record = new CaptureRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.Now,
                Width = image.PixelWidth,
                Height = image.PixelHeight,
                RecognizedText = recognizedText,
                BarcodeText = barcodeText,
                BarcodeFormat = barcodeFormat,
                SourceKind = "OCR"
            };
            record.FileName = $"{record.CreatedAt:yyyyMMdd_HHmmss_fff}_{record.Id[..6]}.png";
            CaptureService.SavePng(image, Path.Combine(Root, record.FileName));
            state.Records.Insert(0, record);
            Trim(state, SettingsService.Load().HistoryLimit);
            SaveState(state);
            return record;
        }
    }

    public static CaptureRecord AddRecording(BitmapSource preview, string mediaPath, TimeSpan duration, int frameCount)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Root);
            var state = LoadState();
            var record = new CaptureRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.Now,
                Width = preview.PixelWidth,
                Height = preview.PixelHeight,
                MediaKind = Path.GetExtension(mediaPath).TrimStart('.').ToUpperInvariant(),
                DurationMilliseconds = Math.Max(0, (int)Math.Round(duration.TotalMilliseconds)),
                FrameCount = Math.Max(1, frameCount),
                SourceKind = "Recording"
            };
            var stem = $"{record.CreatedAt:yyyyMMdd_HHmmss_fff}_{record.Id[..6]}";
            record.FileName = $"{stem}.png";
            record.MediaFileName = $"{stem}{Path.GetExtension(mediaPath).ToLowerInvariant()}";
            CaptureService.SavePng(preview, Path.Combine(Root, record.FileName));
            File.Copy(mediaPath, Path.Combine(Root, record.MediaFileName), true);
            state.Records.Insert(0, record);
            Trim(state, SettingsService.Load().HistoryLimit);
            SaveState(state);
            return record;
        }
    }

    public static string? MediaPath(CaptureRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.MediaFileName)) return null;
        var path = Path.Combine(Root, record.MediaFileName);
        return File.Exists(path) ? path : null;
    }

    public static void UpdateRecognition(string id, string recognizedText, string barcodeText, string barcodeFormat)
    {
        lock (Sync)
        {
            var state = LoadState();
            var record = state.Records.FirstOrDefault(candidate => candidate.Id == id);
            if (record is null) return;
            record.RecognizedText = recognizedText;
            record.BarcodeText = barcodeText;
            record.BarcodeFormat = barcodeFormat;
            record.SourceKind = "OCR";
            SaveState(state);
        }
    }

    public static void UpdateSourceKind(string id, string sourceKind)
    {
        if (string.IsNullOrWhiteSpace(sourceKind)) return;
        lock (Sync)
        {
            var state = LoadState();
            var record = state.Records.FirstOrDefault(candidate => candidate.Id == id);
            if (record is null) return;
            record.SourceKind = sourceKind;
            SaveState(state);
        }
    }

    public static void SaveAnnotationDocument(string id, BitmapSource baseImage, BitmapSource flattenedImage, IReadOnlyList<AnnotationItem> items, string? sourceKind = null)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Root);
            var state = LoadState();
            var record = state.Records.FirstOrDefault(candidate => candidate.Id == id);
            if (record is null) return;
            // Immutable files make the index the transaction's single commit point.
            // The old index and its .bak continue to refer to complete documents.
            var revision = $"{record.Id}_rev_{Guid.NewGuid():N}";
            if (string.IsNullOrWhiteSpace(record.OriginalFileName)) record.OriginalFileName = record.FileName;
            record.BaseFileName = revision + "_base.png";
            record.AnnotationFileName = revision + ".annotations.json";
            record.FileName = revision + ".png";
            record.Width = flattenedImage.PixelWidth;
            record.Height = flattenedImage.PixelHeight;
            CaptureService.SavePng(baseImage, Path.Combine(Root, record.BaseFileName));
            CaptureService.SavePng(flattenedImage, PathFor(record));
            var document = new AnnotationDocument { Items = items.Select(item => item.Clone()).ToList() };
            AtomicFileService.WriteJson(Path.Combine(Root, record.AnnotationFileName), document);
            if (sourceKind is not null) record.SourceKind = sourceKind;
            SaveState(state);
            CleanupRevisions(state, record.Id);
        }
    }

    private static void CleanupRevisions(CaptureHistoryState state, string id)
    {
        var protectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Keep(CaptureHistoryState index)
        {
            foreach (var record in index.Records)
                foreach (var name in new[] { record.FileName, record.BaseFileName, record.AnnotationFileName })
                    protectedFiles.Add(name);
        }
        Keep(state);
        if (AtomicFileService.TryReadJson<CaptureHistoryState>(AtomicFileService.BackupPath(IndexPath), out var backup)) Keep(backup!);
        foreach (var file in RevisionFiles(id))
        {
            var name = Path.GetFileName(file);
            if (protectedFiles.Contains(name) || (name.EndsWith(".bak", StringComparison.Ordinal) && protectedFiles.Contains(name[..^4]))) continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static void Delete(string id)
    {
        lock (Sync)
        {
            var state = LoadState();
            var record = state.Records.FirstOrDefault(candidate => candidate.Id == id);
            if (record is null) return;
            record.DeletedAt = DateTime.Now;
            record.IsFavorite = false;
            SaveState(state);
        }
    }

    public static void Restore(string id)
    {
        lock (Sync)
        {
            var state = LoadState();
            var record = state.Records.FirstOrDefault(candidate => candidate.Id == id);
            if (record is null) return;
            record.DeletedAt = null;
            SaveState(state);
        }
    }

    public static void DeletePermanently(string id)
    {
        lock (Sync)
        {
            var state = LoadState();
            var record = state.Records.FirstOrDefault(candidate => candidate.Id == id);
            if (record is null) return;
            DeleteFiles(record);
            state.Records.Remove(record);
            SaveState(state);
        }
    }

    public static void EmptyRecycleBin()
    {
        lock (Sync)
        {
            var state = LoadState();
            foreach (var record in state.Records.Where(record => record.IsDeleted).ToList())
            {
                DeleteFiles(record);
                state.Records.Remove(record);
            }
            SaveState(state);
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            var state = LoadState();
            foreach (var record in state.Records.Where(record => !record.IsDeleted))
            {
                record.DeletedAt = DateTime.Now;
                record.IsFavorite = false;
            }
            SaveState(state);
        }
    }

    public static void EnforceLimit(int limit)
    {
        lock (Sync)
        {
            var state = LoadState();
            Trim(state, limit);
            SaveState(state);
        }
    }

    private static CaptureHistoryState LoadState()
    {
        var state = AtomicFileService.TryReadJson<CaptureHistoryState>(IndexPath, out var loaded)
            ? loaded!
            : new CaptureHistoryState();
        state.RegionHistory ??= [];
        return state;
    }

    private static void SaveState(CaptureHistoryState state)
    {
        AtomicFileService.WriteJson(IndexPath, state);
    }

    private static void Trim(CaptureHistoryState state, int requestedLimit)
    {
        var limit = Math.Clamp(requestedLimit, 10, 5000);
        var active = state.Records.Where(record => !record.IsDeleted).OrderByDescending(record => record.CreatedAt).ToList();
        foreach (var record in active.Where(record => !record.IsFavorite).Skip(Math.Max(0, limit - active.Count(record => record.IsFavorite))).ToList())
        {
            DeleteFiles(record);
            state.Records.Remove(record);
        }
    }

    private static bool PurgeExpiredDeleted(CaptureHistoryState state, int days)
    {
        var threshold = DateTime.Now.AddDays(-Math.Max(1, days));
        var expired = state.Records.Where(record => record.DeletedAt is { } deleted && deleted < threshold).ToList();
        foreach (var record in expired)
        {
            DeleteFiles(record);
            state.Records.Remove(record);
        }
        return expired.Count > 0;
    }

    private static void DeleteFiles(CaptureRecord record)
    {
        foreach (var file in RevisionFiles(record.Id)) File.Delete(file);
        var originalStem = string.IsNullOrWhiteSpace(record.OriginalFileName) ? string.Empty : Path.GetFileNameWithoutExtension(record.OriginalFileName);
        foreach (var path in new[]
        {
            PathFor(record),
            string.IsNullOrWhiteSpace(record.OriginalFileName) ? string.Empty : Path.Combine(Root, record.OriginalFileName),
            originalStem.Length == 0 ? string.Empty : Path.Combine(Root, originalStem + "_base.png"),
            originalStem.Length == 0 ? string.Empty : Path.Combine(Root, originalStem + ".annotations.json"),
            originalStem.Length == 0 ? string.Empty : Path.Combine(Root, originalStem + ".annotations.json.bak"),
            string.IsNullOrWhiteSpace(record.BaseFileName) ? string.Empty : Path.Combine(Root, record.BaseFileName),
            string.IsNullOrWhiteSpace(record.AnnotationFileName) ? string.Empty : Path.Combine(Root, record.AnnotationFileName)
            ,string.IsNullOrWhiteSpace(record.AnnotationFileName) ? string.Empty : AtomicFileService.BackupPath(Path.Combine(Root, record.AnnotationFileName))
            ,string.IsNullOrWhiteSpace(record.MediaFileName) ? string.Empty : Path.Combine(Root, record.MediaFileName)
            ,string.IsNullOrWhiteSpace(record.ContextFileName) ? string.Empty : Path.Combine(Root, record.ContextFileName)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(path)) File.Delete(path);
    }

    private static string PathFor(CaptureRecord record) => Path.Combine(Root, record.FileName);

    private static IEnumerable<string> RevisionFiles(string id) => Guid.TryParseExact(id, "N", out _)
        ? Directory.EnumerateFiles(Root, $"{id}_rev_*") : [];
}
