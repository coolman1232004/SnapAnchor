namespace SnapAnchor.RecognitionSmoke;

internal static class SmokeAssert
{
    internal static void Require(bool condition, string requirement)
    {
        if (!condition)
            throw new InvalidOperationException($"Smoke requirement failed: {requirement}");
    }
}
