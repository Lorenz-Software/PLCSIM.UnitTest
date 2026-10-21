using Microsoft.Extensions.Logging;

using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal static class SiemensPlcsimOperationMapping
{
    public static LogLevel ToLogLevel(PlcsimDiagnosticSeverity severity)
    {
        return severity switch
        {
            PlcsimDiagnosticSeverity.Information => LogLevel.Information,
            PlcsimDiagnosticSeverity.Warning => LogLevel.Warning,
            _ => LogLevel.Error
        };
    }

    public static LogLevel ToLogLevel(PlcsimOperationStatus status)
    {
        return status switch
        {
            PlcsimOperationStatus.Succeeded => LogLevel.Information,
            PlcsimOperationStatus.SucceededWithWarnings => LogLevel.Warning,
            PlcsimOperationStatus.Cancelled => LogLevel.Warning,
            PlcsimOperationStatus.Rejected => LogLevel.Warning,
            PlcsimOperationStatus.Unavailable => LogLevel.Warning,
            _ => LogLevel.Error
        };
    }

    public static LogLevel ToLogLevel(OperationResultState state)
    {
        return state switch
        {
            OperationResultState.Succeeded => LogLevel.Information,
            OperationResultState.Cancelled => LogLevel.Warning,
            _ => LogLevel.Error
        };
    }

    public static Diagnostic ToDiagnostic(
        PlcsimDiagnostic diagnostic,
        DiagnosticPhase phase,
        DateTimeOffset timestamp)
    {
        return new Diagnostic(
            diagnostic.Code,
            DiagnosticCategory.Environment,
            ToLogLevel(diagnostic.Severity),
            DiagnosticSource.AccessProfile,
            phase,
            diagnostic.Message,
            new DiagnosticCorrelation(ServiceId: "Siemens.PlcsimAdvanced"),
            timestamp,
            TechnicalDetail: diagnostic.EvidenceId);
    }

    public static OperationResult ToOperationResult(
        PlcsimOperationStatus status,
        string failureMessage)
    {
        return status switch
        {
            PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings => OperationResult.Success(),
            PlcsimOperationStatus.Cancelled => OperationResult.Cancelled(failureMessage),
            PlcsimOperationStatus.TimedOut => OperationResult.TimedOut(failureMessage),
            _ => OperationResult.Failure(failureMessage)
        };
    }

    public static OperationResult<T> ToOperationResult<T>(
        PlcsimOperationStatus status,
        T? value,
        string failureMessage)
    {
        return status switch
        {
            PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings when value is not null => OperationResult<T>.Success(value),
            PlcsimOperationStatus.Cancelled => OperationResult<T>.Cancelled(failureMessage),
            PlcsimOperationStatus.TimedOut => OperationResult<T>.TimedOut(failureMessage),
            _ => OperationResult<T>.Failure(failureMessage)
        };
    }
}
