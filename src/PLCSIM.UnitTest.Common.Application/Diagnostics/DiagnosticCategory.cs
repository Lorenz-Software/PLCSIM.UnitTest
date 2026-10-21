namespace PLCSIM.UnitTest.Common.Diagnostics;

/// <summary>
/// Classifies the application-facing problem area of a diagnostic.
/// </summary>
public enum DiagnosticCategory
{
    Configuration,
    Environment,
    Workflow,
    Communication,
    Result,
    Output,
    Cleanup,
    Internal
}
