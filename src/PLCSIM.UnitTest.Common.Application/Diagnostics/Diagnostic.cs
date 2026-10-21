using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.Common.Diagnostics;

/// <summary>
/// Describes a structured application-facing diagnostic emitted by a layer or component.
/// </summary>
public sealed record Diagnostic(
    string Code,
    DiagnosticCategory Category,
    LogLevel Level,
    DiagnosticSource Source,
    DiagnosticPhase Phase,
    string Message,
    DiagnosticCorrelation Correlation,
    DateTimeOffset Timestamp,
    string? Id = null,
    string? TechnicalDetail = null,
    DiagnosticEvidenceReference? EvidenceReference = null);
