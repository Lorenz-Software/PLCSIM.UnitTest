namespace PLCSIM.UnitTest.Common.Diagnostics;

/// <summary>
/// Identifies the workflow or operation phase a diagnostic belongs to.
/// </summary>
public enum DiagnosticPhase
{
    Configuration,
    ProfileValidation,
    ProjectPreparation,
    RuntimeProvisioning,
    ProjectTransfer,
    RuntimeStartup,
    CommunicationValidation,
    RunStart,
    ExecutionMonitoring,
    CompletionStabilization,
    ResultInterpretation,
    OutputCoordination,
    Cleanup,
    ApplicationShutdown
}
