using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Diagnostics;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal static class SiemensDiagnostics
{
    public static Diagnostic ConfigurationError(
        string code,
        string message,
        string? details = null,
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
            timestamp ?? DateTimeOffset.UnixEpoch,
            TechnicalDetail: details);
    }

    public static Diagnostic ConfigurationWarning(
        string code,
        string message,
        string? details = null,
        DateTimeOffset? timestamp = null)
    {
        return new Diagnostic(
            code,
            DiagnosticCategory.Configuration,
            LogLevel.Warning,
            DiagnosticSource.AccessProfile,
            DiagnosticPhase.Configuration,
            message,
            new DiagnosticCorrelation(),
            timestamp ?? DateTimeOffset.UnixEpoch,
            TechnicalDetail: details);
    }
}
