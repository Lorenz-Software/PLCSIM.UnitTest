namespace PLCSIM.UnitTest.Common.Diagnostics;

/// <summary>
/// Identifies the layer or component that produced a diagnostic.
/// </summary>
public enum DiagnosticSource
{
    AccessProfileLoader,
    AccessProfileRegistry,
    AccessProfile,
    PlcProjectService,
    PlcControlService,
    UnitTestLifecycleService,
    UnitTestEventStreamService,
    SharedWorkflow,
    RunObservation,
    ResultInterpretation,
    OutputCoordinator,
    WpfApplication,
    CliApplication
}
