namespace PLCSIM.UnitTest.Common.Diagnostics;

/// <summary>
/// Represents one coalesced diagnostic finding in a diagnostic snapshot.
/// </summary>
public sealed record DiagnosticEntry(
    DiagnosticFingerprint Fingerprint,
    Diagnostic Current,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int OccurrenceCount)
{
    public static DiagnosticEntry Create(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new DiagnosticEntry(
            DiagnosticFingerprint.FromDiagnostic(diagnostic),
            diagnostic,
            diagnostic.Timestamp,
            diagnostic.Timestamp,
            1);
    }

    public DiagnosticEntry ApplyOccurrence(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var fingerprint = DiagnosticFingerprint.FromDiagnostic(diagnostic);
        if (fingerprint != Fingerprint)
        {
            throw new InvalidOperationException("Diagnostic occurrence fingerprint does not match the coalesced entry.");
        }

        var current = Current;
        var lastSeenAt = LastSeenAt;
        if (diagnostic.Timestamp >= lastSeenAt)
        {
            current = diagnostic;
            lastSeenAt = diagnostic.Timestamp;
        }

        return this with
        {
            Current = current,
            LastSeenAt = lastSeenAt,
            OccurrenceCount = OccurrenceCount + 1
        };
    }
}
