namespace PLCSIM.UnitTest.Common.Diagnostics;

/// <summary>
/// Read-only application-facing view of the current coalesced diagnostics.
/// </summary>
public sealed record DiagnosticSnapshot(
    IReadOnlyList<DiagnosticEntry> Entries,
    DateTimeOffset UpdatedAt)
{
    public static DiagnosticSnapshot Empty(DateTimeOffset updatedAt)
    {
        return new DiagnosticSnapshot(Array.Empty<DiagnosticEntry>(), updatedAt);
    }
}
