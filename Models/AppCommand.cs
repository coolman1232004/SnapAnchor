using System.Text.Json;
using SnapAnchor.Services;

namespace SnapAnchor.Models;

internal enum AppCommandKind
{
    Activate,
    Capture,
    CaptureCopy,
    PinClipboard,
    Record,
    Draw,
    History,
    Settings,
    Whiteboard,
    TransparentWhiteboard,
    ColorPicker,
    Exit
}

internal sealed record AppCommand(
    AppCommandKind Kind,
    int DelaySeconds = 0,
    int FixedWidth = 0,
    int FixedHeight = 0,
    double AspectRatio = 0,
    bool IncludeCursor = false)
{
    public static AppCommand Activate { get; } = new(AppCommandKind.Activate);

    public string Serialize() => JsonSerializer.Serialize(this);

    public static bool TryDeserialize(string? value, out AppCommand command)
    {
        command = Activate;
        if (string.Equals(value, "activate", StringComparison.Ordinal)) return true;
        try
        {
            command = JsonSerializer.Deserialize<AppCommand>(value ?? string.Empty) ?? Activate;
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static bool TryParse(IReadOnlyList<string> arguments, out AppCommand command, out string error)
    {
        command = Activate;
        error = string.Empty;
        if (arguments.Count == 0) return true;

        var kind = arguments[0].Trim().ToLowerInvariant() switch
        {
            "activate" or "show" => AppCommandKind.Activate,
            "capture" or "snip" => AppCommandKind.Capture,
            "capture-copy" or "snip-copy" => AppCommandKind.CaptureCopy,
            "pin" or "paste" => AppCommandKind.PinClipboard,
            "record" => AppCommandKind.Record,
            "draw" => AppCommandKind.Draw,
            "history" => AppCommandKind.History,
            "settings" or "preferences" => AppCommandKind.Settings,
            "whiteboard" => AppCommandKind.Whiteboard,
            "transparent-whiteboard" => AppCommandKind.TransparentWhiteboard,
            "color" or "colour" or "color-picker" or "colour-picker" or "picker" => AppCommandKind.ColorPicker,
            "exit" or "quit" => AppCommandKind.Exit,
            _ => (AppCommandKind)(-1)
        };
        if ((int)kind < 0)
        {
            error = LocalizationService.Format("Unknown SnapAnchor command: {0}", arguments[0]);
            return false;
        }

        var delay = 0;
        var width = 0;
        var height = 0;
        var ratio = 0d;
        var cursor = false;
        for (var index = 1; index < arguments.Count; index++)
        {
            switch (arguments[index].ToLowerInvariant())
            {
                case "--cursor": cursor = true; break;
                case "--delay" when index + 1 < arguments.Count && int.TryParse(arguments[++index], out var parsedDelay):
                    delay = Math.Clamp(parsedDelay, 0, 60); break;
                case "--size" when index + 1 < arguments.Count && TrySize(arguments[++index], out var parsedWidth, out var parsedHeight):
                    width = parsedWidth; height = parsedHeight; break;
                case "--ratio" when index + 1 < arguments.Count && TryRatio(arguments[++index], out var parsedRatio):
                    ratio = parsedRatio; break;
                default:
                    error = LocalizationService.Format("Invalid or incomplete option: {0}", arguments[index]);
                    return false;
            }
        }
        command = new AppCommand(kind, delay, width, height, ratio, cursor);
        return true;
    }

    private static bool TrySize(string value, out int width, out int height)
    {
        width = height = 0;
        var parts = value.Split(['x', 'X', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height) &&
               width is >= 1 and <= 50000 && height is >= 1 and <= 50000;
    }

    private static bool TryRatio(string value, out double ratio)
    {
        ratio = 0;
        var parts = value.Split([':', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && double.TryParse(parts[0], out var left) && double.TryParse(parts[1], out var right) && left > 0 && right > 0)
        {
            ratio = left / right;
            return true;
        }
        return double.TryParse(value, out ratio) && ratio > 0 && ratio <= 100;
    }
}

internal sealed class AppCommandEventArgs(AppCommand command) : EventArgs
{
    public AppCommand Command { get; } = command;
}
