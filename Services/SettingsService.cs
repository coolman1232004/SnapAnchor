using System.IO;
using System.Text.Json;

namespace SnapAnchor.Services;

public sealed class AppSettings
{
    public const string DefaultUpdateFeedUrl = "https://github.com/coolman1232004/SnapAnchor/releases/latest/download/release.json";
    internal const int CurrentSettingsSchemaVersion = 1;

    public int SettingsSchemaVersion { get; set; }
    public string CaptureHotkey { get; set; } = "PrintScreen";
    public string CaptureAndCopyHotkey { get; set; } = "CtrlF1";
    public string CustomCaptureHotkey { get; set; } = "ShiftF1";
    public string DrawingHotkey { get; set; } = "CtrlShiftD";
    public string PasteHotkey { get; set; } = "F3";
    public string TogglePinsHotkey { get; set; } = "ShiftF3";
    public bool RunOnStartup { get; set; }
    public bool RunAsAdministrator { get; set; }
    public bool AutoBackup { get; set; } = true;
    public bool KeepResponsive { get; set; } = true;
    public string UiLanguage { get; set; } = "English";
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool CheckUpdatesDaily { get; set; }
    public int CaptureBorderWidth { get; set; } = 2;
    public string CaptureBorderColor { get; set; } = "#3388FF";
    public string CaptureMaskColor { get; set; } = "#66000000";
    public bool ShowCrossLines { get; set; }
    public bool ShowCaptureSize { get; set; } = true;
    public bool? ShowElementDetection { get; set; } = true;
    /// <summary>
    /// When true, hover may refine from window outlines into smaller UI controls.
    /// Default false keeps capture smooth (window spotlight only); Tab still toggles for a session.
    /// </summary>
    public bool DetectUiElements { get; set; }
    public bool ShowCaptureHints { get; set; } = true;
    /// <summary>Legacy name kept for JSON compatibility; prefer <see cref="EnableColorMagnifier"/>.</summary>
    public bool ShowColorSampler { get; set; } = true;
    public bool EnableColorMagnifier { get; set; } = true;
    public string ColorMagnifierHotkey { get; set; } = "CtrlShiftC";
    public bool ColorMagnifierUseHex { get; set; }
    public bool HasSeenWelcomeTip { get; set; }
    public bool ExcludeSnapAnchorFromCapture { get; set; } = true;
    public List<string> CaptureExcludedProcesses { get; set; } = [];
    public List<string> HotkeyExcludedProcesses { get; set; } = [];
    public bool PinWindowShadow { get; set; } = true;
    public bool PinTextSelectableByDefault { get; set; }
    public int PinDefaultOpacity { get; set; } = 100;
    public int PinMaxWindowSize { get; set; } = 12000;
    public int FastThumbnailSize { get; set; } = 50;
    public List<string> PinGroups { get; set; } = ["Default"];
    public string CurrentPinGroup { get; set; } = "Default";
    public bool PinGroupsFollowVirtualDesktops { get; set; }
    public Dictionary<string, string> PinGroupDesktopBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string DefaultPinBackground { get; set; } = "Transparent";
    public string OutputFileName { get; set; } = "SnapAnchor_$yyyy-$MM-$dd_$HH-$mm-$ss.png";
    public string OutputFormat { get; set; } = "PNG";
    public int ImageQuality { get; set; } = 92;
    public int OutputBorderWidth { get; set; }
    public string OutputBorderColor { get; set; } = "#FF111827";
    public bool OutputIncludeShadow { get; set; }
    public int OutputShadowSize { get; set; } = 12;
    public string OutputShadowColor { get; set; } = "#66000000";
    public string QuickSaveFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public bool ShowSaveNotification { get; set; } = true;
    public bool AutoSave { get; set; }
    public string AutoSaveFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnapAnchor");
    public int HistoryLimit { get; set; } = 200;
    public string OcrLanguage { get; set; } = "eng+chi_sim+chi_tra";
    public bool OcrAutoCopy { get; set; }
    public bool OcrDetectOrientation { get; set; } = true;
    public bool CorrectHdrColors { get; set; } = true;
    /// <summary>
    /// Prefer DXGI Desktop Duplication for still captures (better for some full-screen
    /// DirectX apps). Falls back to GDI automatically when DXGI fails.
    /// </summary>
    public bool PreferDxgiCapture { get; set; } = true;
    public string UpdateFeedUrl { get; set; } = DefaultUpdateFeedUrl;
    public int ScrollCaptureMaxFrames { get; set; } = 12;
    public int ScrollCaptureDelayMs { get; set; } = 450;
    public int ScrollCaptureWheelClicks { get; set; } = 5;
    public bool AnnotationFillEnabled { get; set; }
    public int AnnotationFillOpacity { get; set; } = 20;
    public int AnnotationOpacity { get; set; } = 100;
    public bool AnnotationDashed { get; set; }
    public string AnnotationArrowHeads { get; set; } = "End";
    public string AnnotationStylePreset { get; set; } = "Outline";
    public string AnnotationFontFamily { get; set; } = "Calibri";
    public bool AnnotationTextBold { get; set; }
    public bool AnnotationTextItalic { get; set; }
    public bool AnnotationTextUnderline { get; set; }
    public bool AnnotationTextStrikethrough { get; set; }
    public string AnnotationTextAlignment { get; set; } = "Left";
    public bool AnnotationTextOutline { get; set; }
    public string ToolbarSizeMode { get; set; } = "Compact";
    public double AnnotationShapeSize { get; set; } = 3;
    public double AnnotationArrowSize { get; set; } = 3;
    public double AnnotationPencilSize { get; set; } = 3;
    public double AnnotationMarkerSize { get; set; } = 21;
    public double AnnotationBlurSize { get; set; } = 21;
    public double AnnotationTextSize { get; set; } = 2.75;
    public double AnnotationEraserSize { get; set; } = 21;
    public List<string> AnnotationToolbarOrder { get; set; } = AnnotationToolbarCatalog.DefaultOrder.ToList();
    public List<string> AnnotationToolbarEnabled { get; set; } = AnnotationToolbarCatalog.DefaultEnabled.ToList();
    public List<string> CaptureToolbarOrder { get; set; } = CaptureToolbarCatalog.DefaultOrder.ToList();
    public List<string> CaptureToolbarEnabled { get; set; } = CaptureToolbarCatalog.DefaultEnabled.ToList();
    public string RecordingFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SnapAnchor");
    public string RecordingFormat { get; set; } = "MP4";
    public string RecordingCaptureMode { get; set; } = "Region";
    public string RecordingQuality { get; set; } = "High";
    public int RecordingFrameRate { get; set; } = 8;
    public int RecordingCountdownSeconds { get; set; } = 3;
    public int RecordingMaxDurationSeconds { get; set; } = 60;
    public int RecordingMaxWidth { get; set; } = 960;
    public bool RecordingIncludeCursor { get; set; } = true;
    public bool RecordingHighlightClicks { get; set; } = true;
    public bool RecordingSystemAudio { get; set; } = true;
    public bool RecordingMicrophone { get; set; }
    public string RecordingInputDevice { get; set; } = string.Empty;
    public string RecordingOutputDevice { get; set; } = string.Empty;
    public string RecordingHotkey { get; set; } = "CtrlShiftR";
}

internal readonly record struct HotkeyDefinition(string Name, string DisplayName, int Modifiers, int VirtualKey);

internal static class HotkeyOptions
{
    public static readonly HotkeyDefinition[] All = Build();

    private static HotkeyDefinition[] Build()
    {
        var options = new List<HotkeyDefinition>
        {
            new("None", "Disabled", 0, 0),
            new("PrintScreen", "Print Screen", 0, 0x2C),
            new("ShiftPrintScreen", "Shift + Print Screen", NativeMethods.ModShift, 0x2C),
            new("CtrlPrintScreen", "Ctrl + Print Screen", NativeMethods.ModControl, 0x2C),
            new("AltPrintScreen", "Alt + Print Screen", NativeMethods.ModAlt, 0x2C)
        };
        var modifiers = new (string Name, string Display, int Value)[]
        {
            (string.Empty, string.Empty, 0),
            ("Ctrl", "Ctrl + ", NativeMethods.ModControl),
            ("Shift", "Shift + ", NativeMethods.ModShift),
            ("Alt", "Alt + ", NativeMethods.ModAlt),
            ("CtrlShift", "Ctrl + Shift + ", NativeMethods.ModControl | NativeMethods.ModShift),
            ("CtrlAlt", "Ctrl + Alt + ", NativeMethods.ModControl | NativeMethods.ModAlt),
            ("AltShift", "Alt + Shift + ", NativeMethods.ModAlt | NativeMethods.ModShift)
        };
        foreach (var (prefix, display, modifier) in modifiers)
        {
            for (var index = 1; index <= 12; index++)
                options.Add(new($"{prefix}F{index}", $"{display}F{index}", modifier, 0x6F + index));
            if (modifier == 0) continue;
            for (var key = 'A'; key <= 'Z'; key++)
                options.Add(new($"{prefix}{key}", $"{display}{key}", modifier, key));
            for (var key = '0'; key <= '9'; key++)
                options.Add(new($"{prefix}{key}", $"{display}{key}", modifier, key));
        }
        return options.ToArray();
    }

    public static HotkeyDefinition Get(string? name)
    {
        var match = All.FirstOrDefault(option => option.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(match.Name) ? All[1] : match;
    }
}

internal static class SettingsService
{
    private static readonly object Sync = new();
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapAnchor");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        lock (Sync)
        {
            return AtomicFileService.TryReadJson<AppSettings>(SettingsPath, out var settings)
                ? Normalize(settings!)
                : new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Sync)
        {
            AtomicFileService.WriteJson(SettingsPath, Normalize(settings));
        }
        ApplyStartup(settings.RunOnStartup);
    }

    internal static AppSettings Normalize(AppSettings settings)
    {
        // Updates always come from SnapAnchor's official GitHub release channel.
        // Older custom or empty feed values are migrated to the trusted source.
        settings.UpdateFeedUrl = AppSettings.DefaultUpdateFeedUrl;
        var legacyName = string.Concat("Snap", "Pin");
        if (!string.IsNullOrWhiteSpace(settings.OutputFileName) &&
            settings.OutputFileName.StartsWith(legacyName + "_", StringComparison.OrdinalIgnoreCase))
            settings.OutputFileName = "SnapAnchor_" + settings.OutputFileName[(legacyName.Length + 1)..];
        var legacyPictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), legacyName);
        if (PathsEqual(settings.AutoSaveFolder, legacyPictures))
            settings.AutoSaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnapAnchor");
        var legacyVideos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), legacyName);
        if (PathsEqual(settings.RecordingFolder, legacyVideos))
            settings.RecordingFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SnapAnchor");

        // The first dedicated detection preference was accidentally migrated
        // from the unrelated size/dimension checkbox. Repair that migration
        // once, then preserve an explicit detection choice from this version on.
        if (settings.SettingsSchemaVersion < AppSettings.CurrentSettingsSchemaVersion)
        {
            settings.ShowElementDetection = true;
            settings.SettingsSchemaVersion = AppSettings.CurrentSettingsSchemaVersion;
        }
        settings.ShowElementDetection ??= true;
        // The original text tool stored its default as Segoe UI at 16 px. Move
        // that untouched legacy default to the Paint-like Calibri 11 preset,
        // while preserving any font or size the user actually customized.
        if (string.Equals(settings.AnnotationFontFamily, "Segoe UI", StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(settings.AnnotationTextSize - 4) < 0.001)
        {
            settings.AnnotationFontFamily = "Calibri";
            settings.AnnotationTextSize = 2.75;
        }
        settings.PinGroups ??= [];
        settings.PinGroupDesktopBindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        settings.CaptureExcludedProcesses ??= [];
        settings.HotkeyExcludedProcesses ??= [];
        var previousAnnotationEnabled = settings.AnnotationToolbarEnabled ?? [];
        var previousAnnotationSet = previousAnnotationEnabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var historicalAnnotationOrders = new[]
        {
            new[] { "Rectangle", "Ellipse", "Arrow", "Line", "Pencil", "Marker", "Blur", "Text", "Eraser", "Magnify" },
            new[] { "Rectangle", "Arrow", "Pencil", "Marker", "Blur", "Text", "Eraser", "Ellipse", "Line", "Magnify" },
            new[] { "Rectangle", "Arrow", "Pencil", "Marker", "Blur", "Text", "Number", "Callout", "Eraser", "Ellipse", "Line", "Magnify" }
        };
        var historicalAnnotationEnabled = new[]
        {
            new[] { "Rectangle", "Ellipse", "Arrow", "Line", "Pencil", "Marker", "Blur", "Text", "Eraser", "Magnify" },
            new[] { "Rectangle", "Arrow", "Pencil", "Marker", "Blur", "Text", "Eraser" },
            new[] { "Rectangle", "Arrow", "Pencil", "Marker", "Blur", "Text", "Number", "Eraser" }
        };
        var currentOrder = settings.AnnotationToolbarOrder ?? [];
        var migrateAnnotationDefault =
            historicalAnnotationOrders.Any(order => currentOrder.SequenceEqual(order, StringComparer.OrdinalIgnoreCase)) &&
            historicalAnnotationEnabled.Any(enabled => previousAnnotationSet.SetEquals(enabled));
        settings.AnnotationToolbarOrder = migrateAnnotationDefault
            ? AnnotationToolbarCatalog.DefaultOrder.ToList()
            : AnnotationToolbarCatalog.NormalizeOrder(settings.AnnotationToolbarOrder);
        settings.AnnotationToolbarEnabled = migrateAnnotationDefault
            ? AnnotationToolbarCatalog.DefaultEnabled.ToList()
            : AnnotationToolbarCatalog.NormalizeEnabled(previousAnnotationEnabled);

        var previousCaptureOrder = new[] { "Copy", "Pin", "PinThumbnail", "Save", "QuickSave", "LongCapture", "Record", "OCR", "Recapture", "Cancel" };
        var previousCaptureEnabled = settings.CaptureToolbarEnabled ?? [];
        var previousCaptureSet = previousCaptureEnabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var historicalCaptureOrders = new[]
        {
            previousCaptureOrder,
            CaptureToolbarCatalog.DefaultOrder
        };
        var historicalCaptureEnabled = new[]
        {
            previousCaptureOrder,
            new[] { "Cancel", "Pin", "Save", "Copy" }
        };
        var currentCaptureOrder = settings.CaptureToolbarOrder ?? [];
        var migrateCaptureDefault =
            historicalCaptureOrders.Any(order => currentCaptureOrder.SequenceEqual(order, StringComparer.OrdinalIgnoreCase)) &&
            historicalCaptureEnabled.Any(enabled => previousCaptureSet.SetEquals(enabled));
        settings.CaptureToolbarOrder = migrateCaptureDefault
            ? CaptureToolbarCatalog.DefaultOrder.ToList()
            : CaptureToolbarCatalog.NormalizeOrder(settings.CaptureToolbarOrder);
        settings.CaptureToolbarEnabled = migrateCaptureDefault
            ? CaptureToolbarCatalog.DefaultEnabled.ToList()
            : CaptureToolbarCatalog.NormalizeEnabled(previousCaptureEnabled);
        settings.ToolbarSizeMode = ToolbarThemeService.Normalize(settings.ToolbarSizeMode);
        settings.UiLanguage = LocalizationService.Normalize(settings.UiLanguage);
        if (string.Equals(settings.CaptureBorderColor, "#63E6BE", StringComparison.OrdinalIgnoreCase))
            settings.CaptureBorderColor = "#3388FF";
        settings.PinGroups = settings.PinGroups
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (settings.PinGroups.Count == 0) settings.PinGroups.Add("Default");
        if (!settings.PinGroups.Contains(settings.CurrentPinGroup, StringComparer.OrdinalIgnoreCase))
            settings.CurrentPinGroup = settings.PinGroups[0];
        settings.PinGroupDesktopBindings = settings.PinGroupDesktopBindings
            .Where(pair => settings.PinGroups.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) && Guid.TryParse(pair.Value, out _))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
        settings.CaptureExcludedProcesses = settings.CaptureExcludedProcesses
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.HotkeyExcludedProcesses = HotkeyExclusionService.Normalize(settings.HotkeyExcludedProcesses);
        var normalizedOutputFormat = settings.OutputFormat?.ToUpperInvariant();
        settings.OutputFormat = normalizedOutputFormat is "JPEG" or "WEBP" ? normalizedOutputFormat : "PNG";
        settings.ImageQuality = Math.Clamp(settings.ImageQuality, 30, 100);
        settings.OutputBorderWidth = Math.Clamp(settings.OutputBorderWidth, 0, 32);
        settings.OutputShadowSize = Math.Clamp(settings.OutputShadowSize, 1, 64);
        var outputExtension = settings.OutputFormat switch { "JPEG" => ".jpg", "WEBP" => ".webp", _ => ".png" };
        var outputStem = string.IsNullOrWhiteSpace(settings.OutputFileName)
            ? "SnapAnchor_$yyyy-$MM-$dd_$HH-$mm-$ss"
            : NormalizeOutputFileNameTemplate(settings.OutputFileName);
        settings.OutputFileName = Path.ChangeExtension(outputStem, outputExtension);
        // Magnifier is the product feature; keep the legacy JSON flag mirrored.
        settings.ShowColorSampler = settings.EnableColorMagnifier;
        if (string.IsNullOrWhiteSpace(settings.ColorMagnifierHotkey))
            settings.ColorMagnifierHotkey = "CtrlShiftC";
        return settings;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // A malformed user-edited path must not prevent settings migration.
            return false;
        }
    }

    public static string CreateOutputPath(string folder, string template)
    {
        var extension = Path.GetExtension(template).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp")) extension = ".png";
        return CreateOutputPath(folder, template, extension);
    }

    public static string CreateOutputPath(string folder, string template, string extension)
    {
        var now = DateTime.Now;
        var fileName = ExpandOutputFileName(template, now);
        extension = extension.StartsWith('.') ? extension : $".{extension}";
        fileName = Path.ChangeExtension(fileName, extension);
        foreach (var invalid in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(invalid, '_');
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) return path;
        return Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)}_{now:fff}{extension}");
    }

    /// <summary>
    /// Expands output-name tokens so Save dialogs show a real filename such as
    /// SnapAnchor_2026-07-24_15-04-22.png instead of leaving $yyyy/$MM placeholders.
    /// </summary>
    public static string ExpandOutputFileName(string? template, DateTime? timestamp = null)
    {
        var now = timestamp ?? DateTime.Now;
        var fileName = string.IsNullOrWhiteSpace(template)
            ? "SnapAnchor_$yyyy-$MM-$dd_$HH-$mm-$ss.png"
            : template.Trim();

        // Legacy default used $yyyy-MM-dd without $ before each token, so only
        // the year expanded and the dialog showed SnapAnchor_2026-MM-dd_HH-mm-ss.png.
        fileName = NormalizeOutputFileNameTemplate(fileName);

        // Longer tokens first so $yyyy wins over any shorter prefix.
        fileName = fileName
            .Replace("$yyyy", now.ToString("yyyy"), StringComparison.Ordinal)
            .Replace("$MM", now.ToString("MM"), StringComparison.Ordinal)
            .Replace("$dd", now.ToString("dd"), StringComparison.Ordinal)
            .Replace("$HH", now.ToString("HH"), StringComparison.Ordinal)
            .Replace("$mm", now.ToString("mm"), StringComparison.Ordinal)
            .Replace("$ss", now.ToString("ss"), StringComparison.Ordinal)
            .Replace("$fff", now.ToString("fff"), StringComparison.Ordinal);

        return fileName;
    }

    internal static string NormalizeOutputFileNameTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return "SnapAnchor_$yyyy-$MM-$dd_$HH-$mm-$ss.png";

        // Fix the historical default and close variants that omit $ before MM/dd/HH/mm/ss.
        return template
            .Replace("SnapAnchor_$yyyy-MM-dd_HH-mm-ss", "SnapAnchor_$yyyy-$MM-$dd_$HH-$mm-$ss", StringComparison.OrdinalIgnoreCase)
            .Replace("$yyyy-MM-dd_HH-mm-ss", "$yyyy-$MM-$dd_$HH-$mm-$ss", StringComparison.Ordinal)
            .Replace("$yyyy-MM-dd", "$yyyy-$MM-$dd", StringComparison.Ordinal)
            .Replace("$HH-mm-ss", "$HH-$mm-$ss", StringComparison.Ordinal);
    }

    private static void ApplyStartup(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled)
            key?.SetValue("SnapAnchor", $"\"{Environment.ProcessPath}\"");
        else
            key?.DeleteValue("SnapAnchor", false);
    }
}
