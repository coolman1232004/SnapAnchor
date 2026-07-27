using SnapAnchor.Services;

namespace SnapAnchor.RecognitionSmoke;

internal static class ElementDetectionSmoke
{
    internal static void Run()
    {
        const long loaded = 10_000;
        SmokeAssert.Require(
            !ElementDetectionService.IsCacheExpired(
                loaded,
                loaded + ElementDetectionService.WindowCacheLifetimeMs - 1,
                ElementDetectionService.WindowCacheLifetimeMs),
            "window detection cache remains stable within its lifetime");
        SmokeAssert.Require(
            ElementDetectionService.IsCacheExpired(
                loaded,
                loaded + ElementDetectionService.WindowCacheLifetimeMs,
                ElementDetectionService.WindowCacheLifetimeMs),
            "window detection cache refreshes at its lifetime boundary");
        SmokeAssert.Require(
            ElementDetectionService.IsCacheExpired(
                loaded,
                loaded + ElementDetectionService.ElementCacheLifetimeMs,
                ElementDetectionService.ElementCacheLifetimeMs),
            "UI-element snapshots expire and can be reloaded");

        Console.WriteLine("DETECTION CACHE: bounded window and UI-element refresh lifetimes verified");
    }
}
