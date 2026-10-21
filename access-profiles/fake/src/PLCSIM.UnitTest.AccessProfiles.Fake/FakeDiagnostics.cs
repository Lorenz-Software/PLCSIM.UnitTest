using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.AccessProfiles.Fake;

/// <summary>
/// Diagnostic factory helpers used by fake test infrastructure.
/// </summary>
public static class FakeDiagnostics
{
    public static Diagnostic ConfigurationError(
        string code,
        string message,
        DateTimeOffset? timestamp = null)
    {
        return new Diagnostic(
            code,
            DiagnosticCategory.Configuration,
            LogLevel.Error,
            DiagnosticSource.AccessProfile,
            DiagnosticPhase.Configuration,
            message,
            new DiagnosticCorrelation(),
            timestamp ?? DateTimeOffset.UnixEpoch);
    }

    public static Diagnostic OperationError(
        string code,
        DiagnosticCategory category,
        DiagnosticPhase phase,
        string message,
        DateTimeOffset? timestamp = null)
    {
        return new Diagnostic(
            code,
            category,
            LogLevel.Error,
            DiagnosticSource.AccessProfile,
            phase,
            message,
            new DiagnosticCorrelation(),
            timestamp ?? DateTimeOffset.UnixEpoch);
    }
}
