namespace PLCSIM.UnitTest.Common.Diagnostics;

/// <summary>
/// References supporting evidence without embedding large or implementation-specific payloads in a diagnostic.
/// </summary>
public sealed record DiagnosticEvidenceReference(
    string Kind,
    string Reference);
