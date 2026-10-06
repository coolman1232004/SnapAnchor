using SnapAnchor.Controls;
using SnapAnchor.Models;
using SnapAnchor.Services;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapAnchor.RecognitionSmoke;

internal static class OptimizationSmoke
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"snapanchor-optimization-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var previousRoot = Environment.GetEnvironmentVariable("SNAPANCHOR_HISTORY_ROOT");
        try
        {
            TestSettings(root);
            TestSession(root);
            Environment.SetEnvironmentVariable("SNAPANCHOR_HISTORY_ROOT", Path.Combine(root, "history"));
            TestHistory();
            TestGif(root);
            Program.RunSta(() => { TestAnnotation(); return true; });
            Console.WriteLine("OPTIMIZATION REGRESSIONS: settings conflicts, corrupt PNG fallback, document transactions, bounded thumbnails, GIF timing and annotation reuse verified");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SNAPANCHOR_HISTORY_ROOT", previousRoot);
            Directory.Delete(root, true);
        }
    }

    internal static async Task RunRecordingAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"snapanchor-gif-live-{Guid.NewGuid():N}");
        var settings = new AppSettings { RecordingFolder = root, RecordingFrameRate = 8, RecordingMaxWidth = 320, RecordingIncludeCursor = false };
        var bounds = DisplayTopologyService.VirtualBoundsPixels();
        var state = new RecordingState();
        var caller = Environment.CurrentManagedThreadId;
        var worker = caller;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var capture = ScreenRecordingService.CaptureGifAsync(new Rect(bounds.Left, bounds.Top, 32, 32), settings,
                () => Volatile.Read(ref state.Paused), () => Volatile.Read(ref state.Stopped),
                (_, _) => Volatile.Write(ref worker, Environment.CurrentManagedThreadId), cancellation.Token);
            await Task.Delay(350);
            Volatile.Write(ref state.Paused, true);
            await Task.Delay(400);
            Volatile.Write(ref state.Paused, false);
            await Task.Delay(350);
            Volatile.Write(ref state.Stopped, true);
            var result = await capture.WaitAsync(TimeSpan.FromSeconds(10));
            elapsed.Stop();
            SmokeAssert.Require(result.FrameCount >= 2 && worker != caller, "capture and GIF encoding execute off the calling thread");
            SmokeAssert.Require(elapsed.Elapsed - result.Duration > TimeSpan.FromMilliseconds(150), "paused time is excluded from the recording duration");
            using var stream = File.OpenRead(result.FilePath);
            var frames = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames;
            var duration = frames.Sum(frame => Convert.ToInt32(((BitmapMetadata)frame.Metadata).GetQuery("/grctlext/Delay"))) * 10;
            SmokeAssert.Require(frames.Count == result.FrameCount && Math.Abs(duration - result.Duration.TotalMilliseconds) < 80,
                "stopping writes every captured frame with the actual active duration");
            Console.WriteLine($"LIVE GIF: {result.FrameCount} background frames, pause/stop and {duration} ms playback duration verified");
        }
        finally
        {
            cancellation.Cancel();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class RecordingState { internal bool Paused; internal bool Stopped; }

    private static BitmapSource Image(int width = 80, int height = 50, byte color = 90)
    {
        var bytes = Enumerable.Repeat(color, checked(width * height * 4)).ToArray();
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        image.Freeze();
        return image;
    }

    private static void TestSettings(string root)
    {
        var path = Path.Combine(root, "settings.json");
        var store = new SettingsStore(path);
        var preferences = store.Load();
        var editor = store.Load();
        preferences.UiLanguage = "繁體中文";
        preferences.CaptureExcludedProcesses.Add("example");
        store.Save(preferences);
        editor.AnnotationPencilSize = 9;
        store.Save(editor);
        var saved = new SettingsStore(path).Load();
        SmokeAssert.Require(saved.UiLanguage == "繁體中文" && saved.CaptureExcludedProcesses.Contains("example") && saved.AnnotationPencilSize == 9,
            "an old editor snapshot preserves newer preferences, including mutable lists");
        preferences.HistoryLimit = 77;
        store.Save(preferences);
        SmokeAssert.Require(store.Load().AnnotationPencilSize == 9 && store.Load().HistoryLimit == 77, "repeated saves keep their own baseline");
        var detached = store.Load();
        detached.CaptureExcludedProcesses.Clear();
        SmokeAssert.Require(store.Load().CaptureExcludedProcesses.Count == 1, "callers cannot mutate cached preferences without saving");
    }

    private static void TestSession(string root)
    {
        var session = Path.Combine(root, "session");
        var image = Image();
        PinSessionService.SaveCore(session, [new(image, new PinSessionItem { Group = "First" })]);
        PinSessionService.SaveCore(session, [new(image, new PinSessionItem { Group = "Second" })]);
        using var pointer = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "current.json")));
        var current = pointer.RootElement.GetProperty("Current").GetString()!;
        File.WriteAllText(Path.Combine(session, current, "pin_0000.png"), "invalid PNG");
        var recovered = PinSessionService.LoadCore(session);
        SmokeAssert.Require(recovered.Count == 1 && recovered[0].Item.Group == "First", "a corrupt current PNG falls back to the previous independent generation");
        PinSessionService.SaveCore(session, [new(image, new PinSessionItem { Group = "Third" })]);
        SmokeAssert.Require(PinSessionService.LoadCore(session)[0].Item.Group == "Third", "a changed cached PNG is regenerated rather than copied");
    }

    private static void TestHistory()
    {
        var original = Image();
        var record = HistoryService.Add(original);
        var item = new AnnotationItem { Kind = AnnotationKind.Rectangle, X = 5, Y = 5, Width = 20, Height = 10 };
        HistoryService.SaveAnnotationDocument(record.Id, original, Image(color: 110), [item], "Edited");
        var first = HistoryService.Find(record.Id)!;
        var index = Path.Combine(HistoryService.HistoryDirectory, "history.json");
        using (var locked = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failed = false;
            try { HistoryService.SaveAnnotationDocument(record.Id, Image(color: 140), Image(color: 160), [], "Refreshed"); }
            catch (IOException) { failed = true; }
            SmokeAssert.Require(failed, "an index commit failure is observable");
        }
        var afterFailure = HistoryService.Find(record.Id)!;
        SmokeAssert.Require(afterFailure.FileName == first.FileName && afterFailure.BaseFileName == first.BaseFileName &&
            HistoryService.LoadAnnotations(afterFailure).Count == 1, "a failed commit leaves the original document coherent");
        HistoryService.SaveAnnotationDocument(record.Id, Image(color: 170), Image(color: 180), [], "Refreshed");
        var second = HistoryService.Find(record.Id)!;
        SmokeAssert.Require(second.FileName != first.FileName && File.Exists(Path.Combine(HistoryService.HistoryDirectory, first.FileName)), "previous image revisions remain available for index recovery");
        File.WriteAllText(index, "{ corrupt");
        var recovered = HistoryService.Find(record.Id)!;
        SmokeAssert.Require(recovered.FileName == first.FileName && HistoryService.LoadBaseImage(recovered).PixelWidth == 80 &&
            HistoryService.LoadAnnotations(recovered).Count == 1, "index recovery restores matching base image, flattened image and layers");
        HistoryService.DeletePermanently(record.Id);
        SmokeAssert.Require(Directory.EnumerateFiles(HistoryService.HistoryDirectory).All(path => Path.GetFileName(path).StartsWith("history.json", StringComparison.Ordinal)),
            "permanent deletion removes original images and every immutable revision");

        var tall = HistoryService.Add(Image(30, 8000));
        var cache = new HistoryThumbnailCache();
        var thumbnail = cache.Load(tall, false)!;
        SmokeAssert.Require(thumbnail.IsFrozen && thumbnail.PixelWidth <= 240 && thumbnail.PixelHeight <= 180,
            "thumbnails bound both dimensions for long captures");
        SmokeAssert.Require(ReferenceEquals(thumbnail, cache.Load(tall, false)), "repeated searches reuse decoded thumbnails");
        var context = HistoryService.Add(original, new Int32Rect(5, 5, 20, 20), Image(320, 180));
        var backgroundThumbnail = Task.Run(() => cache.Load(context, true)).GetAwaiter().GetResult();
        SmokeAssert.Require(backgroundThumbnail?.IsFrozen == true, "context preview rendering works on the background thumbnail worker");
    }

    private static void TestGif(string root)
    {
        var path = Path.Combine(root, "timing.gif");
        var image = Image();
        ScreenRecordingService.SaveGif(Enumerable.Repeat(image, 32).ToArray(), path, TimeSpan.FromMilliseconds(125));
        using (var stream = File.OpenRead(path))
        {
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var delay = decoder.Frames.Sum(frame => Convert.ToInt32(((BitmapMetadata)frame.Metadata).GetQuery("/grctlext/Delay")));
            SmokeAssert.Require(delay == 400, "8 fps GIF delay rounding does not accumulate drift");
        }
        using (var writer = new StreamingGifWriter(path, 80, 50, TimeSpan.FromMilliseconds(125)))
        {
            writer.AppendFrame(image, TimeSpan.FromMilliseconds(40));
            writer.AppendFrame(image, TimeSpan.FromMilliseconds(210));
            writer.AppendFrame(image, TimeSpan.FromMilliseconds(120));
            writer.Complete();
        }
        using var variable = File.OpenRead(path);
        var frames = new GifBitmapDecoder(variable, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames;
        var delays = frames.Select(frame => Convert.ToInt32(((BitmapMetadata)frame.Metadata).GetQuery("/grctlext/Delay"))).ToArray();
        SmokeAssert.Require(delays.SequenceEqual(new[] { 4, 21, 12 }), "GIF frames preserve uneven capture durations");
    }

    private static void TestAnnotation()
    {
        var editor = new AnnotationEditorControl();
        var first = new AnnotationItem { Kind = AnnotationKind.Rectangle, Width = 15, Height = 10 };
        var second = new AnnotationItem { Kind = AnnotationKind.Ellipse, X = 30, Width = 15, Height = 10 };
        editor.LoadImage(Image(), [first, second]);
        var canvas = (Canvas)editor.FindName("AnnotationCanvas");
        var initial = canvas.Children.Cast<FrameworkElement>().First(element => Equals(element.Tag, first.Id));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var items = (List<AnnotationItem>)typeof(AnnotationEditorControl).GetField("_items", flags)!.GetValue(editor)!;
        items[1].X = 35;
        typeof(AnnotationEditorControl).GetMethod("RenderAnnotations", flags)!.Invoke(editor, null);
        SmokeAssert.Require(ReferenceEquals(initial, canvas.Children.Cast<FrameworkElement>().First(element => Equals(element.Tag, first.Id))),
            "changing one annotation reuses the other annotation's visual");
        var push = typeof(AnnotationEditorControl).GetMethod("PushUndo", flags)!;
        for (var i = 0; i < 140; i++) push.Invoke(editor, null);
        var undo = (Stack<List<AnnotationItem>>)typeof(AnnotationEditorControl).GetField("_undo", flags)!.GetValue(editor)!;
        SmokeAssert.Require(undo.Count == 100, "annotation undo history has a finite count bound");
    }
}
