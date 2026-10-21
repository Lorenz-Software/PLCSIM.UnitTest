using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Core.Workflow;

public sealed record WorkflowActionPolicy(
    TimeSpan Timeout);

public sealed record WorkflowActionResult(
    WorkflowAction Action,
    OperationResult Result,
    RunWorkflowPublication Publication);

public sealed record UnitTestWorkflowActionResult(
    WorkflowAction Action,
    OperationResult<TestRunCommandResult> Result,
    RunWorkflowPublication Publication);

public sealed record EventBufferResetWorkflowActionResult(
    WorkflowAction Action,
    OperationResult<EventBufferResetCommandResult> Result,
    RunWorkflowPublication Publication);

public sealed record SafetyAuthorizationRequirementWorkflowActionResult(
    WorkflowAction Action,
    OperationResult<SafetyProgramAuthorizationRequirementResult> Result,
    RunWorkflowPublication Publication);

public sealed record SafetyAuthorizationWorkflowActionResult(
    WorkflowAction Action,
    OperationResult<SafetyProgramAuthorizationResult> Result,
    RunWorkflowPublication Publication);

public sealed record RunWorkflowPublicationEventArgs(
    RunWorkflowPublication Publication);

public sealed record RunWorkflowPublication(
    long Sequence,
    RunWorkflowState State,
    ActiveOperation? ActiveOperation,
    WorkflowStatusSnapshot Status,
    UnitTestRunEpochSnapshot? CurrentRun,
    TestRunMonitoringState MonitorState,
    UnitTestRunControlObservation? LifecycleObservation,
    EventStreamState? EventStreamState,
    ProjectTargetDiscoveryResult? ProjectTargets,
    RuntimeTargetDiscoveryResult? RuntimeTargets,
    IReadOnlyList<ActionGateState> ActionGates,
    DiagnosticSnapshot DiagnosticSnapshot)
{
    public IReadOnlyList<Diagnostic> Diagnostics => DiagnosticSnapshot.Entries
        .Select(static entry => entry.Current)
        .ToArray();
}

public sealed record WorkflowStatusSnapshot(
    RunWorkflowState State,
    ActiveOperation? ActiveOperation,
    bool OperationInProgress,
    bool HasAccessProfile,
    string? AccessProfileId,
    string? AccessProfileVersion,
    ProjectOwnershipMode ProjectOwnershipMode,
    bool HasRunSetup,
    RuntimeProvisioningMode? RuntimeProvisioningMode,
    bool HasProjectTarget,
    bool HasRuntimeTarget,
    ProjectSetupState ProjectSetupState,
    bool ProjectAccessActive,
    bool RuntimePrepared,
    PlcState PlcState,
    bool UnitTestCommunicationPrepared,
    bool UnitTestCommunicationContextActive,
    bool MonitoringActive,
    UnitTestRunControlObservation? LifecycleObservation,
    EventStreamState? EventStreamState,
    TestRunMonitoringState MonitorState,
    UnitTestRunEpochSnapshot? CurrentRun,
    AccessHealthSnapshot AccessHealth,
    bool IsExportable)
{
    public bool ProjectPrepared => ProjectSetupState is ProjectSetupState.ManagedProjectPrepared
        or ProjectSetupState.ManagedTransferCompleted;

    public bool ProjectTransferred => ProjectSetupState == ProjectSetupState.ManagedTransferCompleted;

    public bool ProjectSetupSatisfied => ProjectSetupState is ProjectSetupState.ManagedTransferCompleted
        or ProjectSetupState.ExternalManagementAccepted;

    public bool PlcRunning => PlcState.OperatingState == PlcOperatingState.Running;
}

public enum ProjectSetupState
{
    NotEstablished,
    ManagedProjectPrepared,
    ManagedTransferCompleted,
    ExternalManagementAccepted
}

public sealed record ActiveOperation(
    string OperationId,
    WorkflowAction Action,
    string Phase,
    DateTimeOffset StartedAt,
    OperationProgress? Progress);

public sealed record ActionGateState(
    WorkflowAction Action,
    ActionGateAvailability Availability,
    IReadOnlyList<ActionGateReason> Reasons)
{
    public bool IsAvailable => Availability == ActionGateAvailability.Available;
}

public enum ActionGateAvailability
{
    Available,
    Pending,
    Blocked,
    Unsafe,
    NotApplicable
}

public sealed record ActionGateReason(
    ActionGateReasonCode Code,
    string Message);

public enum RunWorkflowState
{
    NotConfigured,
    AccessProfileLoaded,
    DiscoveryReady,
    Configured,
    PreparingProject,
    RuntimePrepared,
    ProjectTransferred,
    PlcStopped,
    PlcRunning,
    UnitTestCommunicationPrepared,
    Monitoring,
    RunActive,
    RunCompleted,
    CleaningUp,
    Error
}

public enum WorkflowAction
{
    SelectConfiguration,
    LoadAccessProfile,
    UnloadAccessProfile,
    DiscoverProjectTargets,
    DiscoverRuntimeTargets,
    PrepareProject,
    PrepareRuntime,
    ReconnectRuntime,
    DiscoverSafetyProgramAuthorizationRequirement,
    AuthorizeSafetyProgram,
    TransferProject,
    StartPlc,
    StopPlc,
    PrepareUnitTestCommunication,
    StartMonitoring,
    StopMonitoring,
    StartUnitTest,
    AbortUnitTest,
    ResetUnitTest,
    ResetEventBuffer,
    ReleaseUnitTestCommunication,
    ReleaseRuntime,
    ReleaseProject,
    Cleanup
}

public enum ActionGateReasonCode
{
    NotConfigured,
    AccessProfileNotLoaded,
    ProjectTargetNotSelected,
    RuntimeTargetNotSelected,
    OperationInProgress,
    ProjectNotPrepared,
    ProjectSetupNotSatisfied,
    RuntimeNotPrepared,
    RuntimeInstanceNotAvailable,
    ProjectNotTransferred,
    PlcNotRunning,
    PlcNotStopped,
    UnitTestCommunicationNotPrepared,
    UnitTestCommunicationStillPrepared,
    EventStreamNotMonitoring,
    UnitTestNotReady,
    EventBufferHasLostEvents,
    RunAlreadyActive,
    NoRunAvailable,
    RuntimeStillPrepared,
    AccessProfileUnhealthy,
    ProjectAccessUnhealthy,
    PlcControlAccessUnhealthy,
    UnitTestLifecycleAccessUnhealthy,
    UnitTestEventStreamAccessUnhealthy,
    AccessRevalidationRequired,
    ErrorState
}
