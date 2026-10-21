namespace PLCSIM.UnitTest.Common.References;

/// <summary>
/// Resolution state for project or runtime target selection.
/// </summary>
public enum TargetResolutionState
{
    NotResolved,
    Resolved,
    NotAvailable,
    Ambiguous,
    Incompatible,
    InvalidConfiguration
}
