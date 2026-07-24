namespace SnapAnchor.Services;

internal readonly record struct AnnotationToolbarDefinition(string Key, string Label);

internal static class AnnotationToolbarCatalog
{
    internal static readonly AnnotationToolbarDefinition[] All =
    [
        new("Rectangle", "Rectangle"),
        new("Arrow", "Arrow"),
        new("Pencil", "Pencil"),
        new("Marker", "Marker"),
        new("Blur", "Blur brush"),
        new("Text", "Text"),
        new("Number", "Numbered marker"),
        new("Callout", "Callout"),
        new("Eraser", "Eraser brush"),
        new("Ellipse", "Ellipse"),
        new("Line", "Straight line"),
        new("Magnify", "Magnifier")
    ];

    internal static readonly string[] DefaultOrder =
    [
        "Rectangle", "Ellipse", "Arrow", "Line", "Pencil", "Marker", "Blur", "Text", "Number", "Callout", "Eraser", "Magnify"
    ];

    internal static readonly string[] DefaultEnabled =
    [
        "Rectangle", "Ellipse", "Arrow", "Line", "Pencil", "Marker", "Blur", "Text", "Number", "Eraser"
    ];

    internal static List<string> NormalizeOrder(IEnumerable<string>? order)
    {
        var known = All.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = (order ?? [])
            .Where(known.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.AddRange(All.Select(item => item.Key).Where(key => !result.Contains(key, StringComparer.OrdinalIgnoreCase)));
        return result;
    }

    internal static List<string> NormalizeEnabled(IEnumerable<string>? enabled)
    {
        var known = All.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = (enabled ?? [])
            .Where(known.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (result.Count == 0) result.Add("Rectangle");
        return result;
    }
}

internal static class CaptureToolbarCatalog
{
    internal static readonly AnnotationToolbarDefinition[] All =
    [
        new("Cancel", "Cancel capture"),
        new("Pin", "Pin"),
        new("Save", "Save as image"),
        new("Copy", "Copy"),
        new("PinThumbnail", "Pin as thumbnail"),
        new("QuickSave", "Quick save"),
        new("LongCapture", "Long scrolling capture"),
        new("Record", "Screen recording"),
        new("OCR", "OCR / QR recognition"),
        new("Recapture", "Capture another area")
    ];

    internal static readonly string[] DefaultOrder =
    [
        "Cancel", "Pin", "Save", "Copy", "PinThumbnail", "QuickSave", "LongCapture", "Record", "OCR", "Recapture"
    ];

    internal static readonly string[] DefaultEnabled =
    [
        "Cancel", "Pin", "Save", "Copy", "OCR", "LongCapture"
    ];

    internal static List<string> NormalizeOrder(IEnumerable<string>? order)
    {
        var known = All.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = (order ?? [])
            .Where(known.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.AddRange(All.Select(item => item.Key).Where(key => !result.Contains(key, StringComparer.OrdinalIgnoreCase)));
        return result;
    }

    internal static List<string> NormalizeEnabled(IEnumerable<string>? enabled)
    {
        var known = All.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = (enabled ?? [])
            .Where(known.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (result.Count == 0) result.Add("Copy");
        return result;
    }
}
