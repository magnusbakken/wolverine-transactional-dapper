namespace IntegrationTests.Infrastructure;

/// <summary>
/// Lightweight test-skip mechanism for xUnit 2.x, which does not have a
/// built-in dynamic-skip API. Tests that call Skip() will fail with a
/// recognizable message rather than silently pass with no assertions.
/// </summary>
public static class TestSkip
{
    /// <summary>
    /// Skip a test with the given reason by throwing an exception that makes
    /// it easy to find in the output. This causes the test to be marked FAILED
    /// with a clear "SKIPPED" message, which is more visible than a silent pass.
    ///
    /// To have tests truly "skipped" (yellow in test runners), set the
    /// WOLVERINE_DEMO_CONNECTION_STRING environment variable to a running
    /// PostgreSQL instance.
    /// </summary>
    public static void Because(string reason)
        => throw new InfrastructureNotAvailableException(reason);
}

public class InfrastructureNotAvailableException(string reason)
    : Exception($"[SKIPPED] Infrastructure not available: {reason}");
