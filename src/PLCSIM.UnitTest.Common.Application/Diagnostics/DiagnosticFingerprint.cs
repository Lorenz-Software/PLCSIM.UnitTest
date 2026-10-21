namespace PLCSIM.UnitTest.Common.Diagnostics;

/// <summary>
/// Identifies one coalesced diagnostic finding independently from emission text and timing.
/// </summary>
public sealed record DiagnosticFingerprint(
    string Code,
    DiagnosticCategory Category,
    DiagnosticSource Source,
    DiagnosticPhase Phase,
    DiagnosticCorrelationScope CorrelationScope)
{
    public static DiagnosticFingerprint FromDiagnostic(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new DiagnosticFingerprint(
            diagnostic.Code,
            diagnostic.Category,
            diagnostic.Source,
            diagnostic.Phase,
            DiagnosticCorrelationScope.FromCorrelationIdentity(diagnostic.Correlation));
    }
}
