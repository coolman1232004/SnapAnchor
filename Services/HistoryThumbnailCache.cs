using SnapAnchor.Models;
using System.IO;
using System.Windows.Media.Imaging;

namespace SnapAnchor.Services;

internal sealed class HistoryThumbnailCache
{
    private const int Capacity = 96;
    private readonly Dictionary<string, BitmapSource> _images = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly object _sync = new();

    internal BitmapSource? Load(CaptureRecord record, bool context, int maxWidth = 240, int maxHeight = 180)
    {
        var name = context ? record.ContextFileName : record.FileName;
        var path = Path.Combine(HistoryService.HistoryDirectory, name);
        var key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{maxWidth}|{maxHeight}|{record.SourceRegion?.X}|{record.SourceRegion?.Y}|{record.SourceRegion?.Width}|{record.SourceRegion?.Height}";
        lock (_sync)
        {
            if (_images.TryGetValue(key, out var cached))
            {
                _order.Remove(key);
                _order.AddLast(key);
                return cached;
            }
        }
        var width = context ? record.ContextWidth : record.Width;
        var height = context ? record.ContextHeight : record.Height;
        var scale = Math.Min(1, Math.Min(maxWidth / Math.Max(1d, width), maxHeight / Math.Max(1d, height)));
        var decodeWidth = Math.Max(1, (int)Math.Floor(width * scale));
        var decodeHeight = Math.Max(1, (int)Math.Floor(height * scale));
        BitmapSource? image;
        try { image = context ? HistoryService.LoadContextPreview(record, decodeWidth, decodeHeight) : HistoryService.LoadImage(record, decodeWidth, decodeHeight); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or System.Runtime.InteropServices.COMException) { return null; }
        if (image is null) return null;
        lock (_sync)
        {
            if (_images.ContainsKey(key)) return _images[key];
            _images.Add(key, image);
            _order.AddLast(key);
            while (_order.Count > Capacity)
            {
                _images.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
        }
        return image;
    }
}
