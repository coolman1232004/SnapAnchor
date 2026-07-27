using System.IO;
using System.Security.Cryptography;

namespace SnapAnchor.Services;

/// <summary>
/// Pure update-policy helpers. Keeping these decisions outside the windows and
/// downloader makes the trust boundary independently testable.
/// </summary>
internal static class UpdatePolicyService
{
    internal const int Sha256HexLength = 64;

    internal static int CurrentWindowsBuild =>
        OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : int.MaxValue;

    internal static bool IsValidSha256(string? value) =>
        value is { Length: Sha256HexLength } &&
        value.All(character => Uri.IsHexDigit(character));

    internal static bool SupportsCurrentWindows(int minimumWindowsBuild, int? currentBuild = null) =>
        minimumWindowsBuild <= 0 || (currentBuild ?? CurrentWindowsBuild) >= minimumWindowsBuild;

    internal static bool HashMatches(string path, string expected)
    {
        if (!IsValidSha256(expected) || !File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<bool> HashMatchesAsync(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        if (!IsValidSha256(expected) || !File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
