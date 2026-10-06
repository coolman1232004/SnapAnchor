using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SnapAnchor.Services;

/// <summary>Tracks each snapshot's baseline so separate windows only save their own changes.</summary>
internal sealed class SettingsStore(string path)
{
    private static readonly PropertyInfo[] Properties = typeof(AppSettings).GetProperties();
    private readonly object _sync = new();
    private readonly ConditionalWeakTable<AppSettings, Baseline> _baselines = new();
    private AppSettings? _current;

    public AppSettings Load()
    {
        lock (_sync)
        {
            var snapshot = Clone(Current);
            _baselines.Add(snapshot, new Baseline(Clone(snapshot)));
            return snapshot;
        }
    }

    public T Read<T>(Func<AppSettings, T> read)
    {
        lock (_sync) return read(Current);
    }

    public void Save(AppSettings snapshot)
    {
        lock (_sync)
        {
            var merged = Clone(Current);
            var incoming = Clone(snapshot);
            var tracked = _baselines.TryGetValue(snapshot, out var baseline);
            foreach (var property in Properties)
            {
                var value = property.GetValue(snapshot);
                if (!tracked || JsonSerializer.Serialize(value) != JsonSerializer.Serialize(property.GetValue(baseline!.Value)))
                    property.SetValue(merged, property.GetValue(incoming));
            }
            SettingsService.Normalize(merged);
            AtomicFileService.WriteJson(path, merged);
            _current = merged;
            // Refresh the caller too: its next save starts from the merged version.
            var refreshed = Clone(merged);
            foreach (var property in Properties) property.SetValue(snapshot, property.GetValue(refreshed));
            _baselines.Remove(snapshot);
            _baselines.Add(snapshot, new Baseline(Clone(snapshot)));
        }
    }

    private AppSettings Current => _current ??= SettingsService.Normalize(
        AtomicFileService.TryReadJson<AppSettings>(path, out var loaded) ? loaded! : new AppSettings());

    private static AppSettings Clone(AppSettings settings) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
    private sealed record Baseline(AppSettings Value);
}
