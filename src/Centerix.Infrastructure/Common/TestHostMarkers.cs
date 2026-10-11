namespace Centerix.Infrastructure.Common;

/// <summary>
/// REV-012 (A.1): an environment literally named "Testing" is not, by itself, trusted to
/// unlock Capture email mode or skip database initialization — a real deployment could be
/// named "Testing" by an operator. Only <c>TestWebApplicationFactory</c>-derived test hosts
/// set this configuration key (via in-memory configuration), so the behaviour stays opt-in
/// to the test harness rather than opt-in to an environment name.
/// </summary>
public static class TestHostMarkers
{
    public const string AllowCaptureAndSkipDbInitKey = "Testing:AllowCaptureAndSkipDbInit";
}
