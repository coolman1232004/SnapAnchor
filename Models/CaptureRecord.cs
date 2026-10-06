namespace SnapAnchor.Models;

internal sealed class CaptureRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string FileName { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string RecognizedText { get; set; } = string.Empty;
    public string BarcodeText { get; set; } = string.Empty;
    public string BarcodeFormat { get; set; } = string.Empty;
    public string BaseFileName { get; set; } = string.Empty;
    public string AnnotationFileName { get; set; } = string.Empty;
    public string MediaFileName { get; set; } = string.Empty;
    public string MediaKind { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public string ContextFileName { get; set; } = string.Empty;
    public int ContextWidth { get; set; }
    public int ContextHeight { get; set; }
    public CaptureRegion? SourceRegion { get; set; }
    public int DurationMilliseconds { get; set; }
    public int FrameCount { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public bool IsFavorite { get; set; }
    public DateTime? DeletedAt { get; set; }
    public bool HasEditableAnnotations => !string.IsNullOrWhiteSpace(AnnotationFileName);
    public bool IsRecording => !string.IsNullOrWhiteSpace(MediaFileName);
    public bool HasContext => !string.IsNullOrWhiteSpace(ContextFileName);
    public bool IsDeleted => DeletedAt is not null;
}

internal sealed class CaptureHistoryState
{
    public List<CaptureRecord> Records { get; set; } = [];
    public CaptureRegion? LastRegion { get; set; }
    public List<CaptureRegion> RegionHistory { get; set; } = [];
}

internal sealed class CaptureRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
