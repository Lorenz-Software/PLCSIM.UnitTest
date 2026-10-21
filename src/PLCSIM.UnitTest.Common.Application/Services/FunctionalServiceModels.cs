using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Common.Services;

/// <summary>
/// Opaque profile-specific request used to discover or prepare a PLC project context.
/// </summary>
public sealed record PlcProjectRequest(
    AccessProfileConfig Configuration);

/// <summary>
/// Opaque profile-specific request used to discover PLC runtime targets.
/// </summary>
public sealed record PlcRuntimeDiscoveryRequest(
    AccessProfileConfig Configuration);

/// <summary>
/// Opaque profile-specific request used to prepare a PLC runtime context.
/// </summary>
public sealed record PlcRuntimePrepareRequest(
    AccessProfileConfig Configuration,
    RuntimeProvisioningMode ProvisioningMode,
    RuntimeTargetRef? RuntimeTarget);

/// <summary>
/// Request used to replace technical access to an already prepared PLC runtime target.
/// </summary>
public sealed record PlcRuntimeReconnectRequest(
    AccessProfileConfig Configuration,
    RuntimeProvisioningMode ProvisioningMode,
    PlcRuntimeContext Runtime,
    RuntimeTargetRef RuntimeTarget);

/// <summary>
/// Application-owned request to release technical runtime access and retain or destroy the runtime resource.
/// </summary>
public sealed record PlcRuntimeReleaseRequest(
    PlcRuntimeContext Runtime,
    RuntimeTargetRef RuntimeTarget,
    RuntimeResourceReleaseMode ReleaseMode);

/// <summary>
/// Provider-reported outcome after a runtime release request was processed successfully.
/// </summary>
public enum PlcRuntimeReleaseOutcome
{
    Unspecified,
    ResourceKept,
    ResourceDestroyed,
    ResourcePartiallyDestroyed
}

/// <summary>
/// Logical project context used after project provisioning has been selected or completed.
/// </summary>
public sealed record ProjectExecutionContext(
    ProjectOwnershipMode OwnershipMode,
    PlcProjectContext? Project,
    ProjectTargetRef? ProjectTarget);

/// <summary>
/// Request used to validate and prepare UnitTest communication for a prepared runtime.
/// </summary>
public sealed record CommunicationPrepareRequest(
    AccessProfileConfig Configuration,
    ProjectExecutionContext ProjectContext,
    PlcRuntimeContext Runtime,
    RuntimeTargetRef RuntimeTarget,
    CommunicationTimeoutPolicy? Timeouts = null);

/// <summary>
/// Validated UnitTest communication session.
/// </summary>
public sealed record CommunicationSession(
    CommunicationSessionId SessionId,
    DateTimeOffset PreparedAt);

/// <summary>
/// Context handed to UnitTest lifecycle and event-stream services after communication preparation succeeded.
/// </summary>
public sealed record UnitTestContext(
    CommunicationSession Session,
    ProjectExecutionContext ProjectContext,
    PlcRuntimeContext Runtime,
    RuntimeTargetRef RuntimeTarget);

/// <summary>
/// Result of a PLC project build/check operation.
/// </summary>
public sealed record PlcBuildResult(
    bool Succeeded,
    bool HasWarnings,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Authorization requirement reported for the Safety program of a selected project target.
/// </summary>
public enum SafetyProgramAuthorizationRequirement
{
    NotRequired,
    PasswordRequired
}

/// <summary>
/// Result of determining the Safety-program authorization requirement.
/// </summary>
public sealed record SafetyProgramAuthorizationRequirementResult(
    SafetyProgramAuthorizationRequirement Requirement);

/// <summary>
/// Result of submitting a transient Safety-program credential.
/// </summary>
public sealed record SafetyProgramAuthorizationResult(
    bool Authorized);

/// <summary>
/// Result of preparing a PLC runtime, including the effective runtime target.
/// </summary>
public sealed record PreparedPlcRuntime(
    PlcRuntimeContext Runtime,
    RuntimeTargetRef RuntimeTarget);

/// <summary>
/// Result of transferring a prepared PLC project target to a runtime target.
/// </summary>
public sealed record PlcTransferResult(
    PlcProjectContext Project,
    ProjectTargetRef ProjectTarget,
    RuntimeTargetRef RuntimeTarget,
    DateTimeOffset CompletedAt);

/// <summary>
/// Application-facing runtime-instance and PLC operating-state projection.
/// </summary>
public sealed record PlcState(
    RuntimeInstanceState InstanceState,
    PlcOperatingState OperatingState,
    IReadOnlyList<RuntimeTargetRef> RuntimeTargets,
    bool ReadyForTransfer,
    bool ReadyForExecution);

/// <summary>
/// Application-facing UnitTest RunControl observation.
/// </summary>
public sealed record UnitTestRunControlObservation(
    UnitTestExecutionStatus ExecutionStatus,
    UnitTestExecutionSignals ExecutionSignals,
    UnitTestLifecycleState State,
    UnitTestResultState Result)
{
    public bool Uninitialized => HasSignal(UnitTestExecutionSignals.Uninitialized);

    public bool ConfigError => HasSignal(UnitTestExecutionSignals.ConfigError);

    public bool Ready => HasSignal(UnitTestExecutionSignals.Ready);

    public bool Running => HasSignal(UnitTestExecutionSignals.Running);

    public bool Completed => HasSignal(UnitTestExecutionSignals.Completed);

    public bool Aborted => HasSignal(UnitTestExecutionSignals.Aborted);

    public bool Inactive => HasSignal(UnitTestExecutionSignals.Inactive);

    public bool Error => HasSignal(UnitTestExecutionSignals.Error);

    private bool HasSignal(UnitTestExecutionSignals signal) =>
        (ExecutionSignals & signal) == signal;
}

/// <summary>
/// Candidate target discovery result for project-side PLC targets.
/// </summary>
public sealed record ProjectTargetDiscoveryResult(
    IReadOnlyList<ProjectTargetRef> Candidates,
    TargetDiscoveryState State,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Candidate target discovery result for runtime-side PLC targets.
/// </summary>
public sealed record RuntimeTargetDiscoveryResult(
    IReadOnlyList<RuntimeTargetRef> Candidates,
    TargetDiscoveryState State,
    IReadOnlyList<Diagnostic> Diagnostics,
    RuntimeTargetRef? DefaultTarget = null);

/// <summary>
/// Candidate and selected target state for a PLC project.
/// </summary>
public sealed record ProjectTargetResolution(
    IReadOnlyList<ProjectTargetRef> Candidates,
    ProjectTargetRef? SelectedTarget,
    TargetResolutionState State);

/// <summary>
/// Candidate and selected target state for a PLC runtime.
/// </summary>
public sealed record RuntimeTargetResolution(
    IReadOnlyList<RuntimeTargetRef> Candidates,
    RuntimeTargetRef? SelectedTarget,
    TargetResolutionState State);

/// <summary>
/// Coarse result state for target discovery before final target selection exists.
/// </summary>
public enum TargetDiscoveryState
{
    NotAvailable,
    NoCandidates,
    CandidatesAvailable,
    Failed
}

/// <summary>
/// Outcome of a UnitTest RunControl command.
/// </summary>
public sealed record TestRunCommandResult(
    UnitTestRunCommand Command,
    bool Accepted);

/// <summary>
/// Outcome of an EventBuffer reset command dispatch.
/// </summary>
public sealed record EventBufferResetCommandResult(
    bool Accepted);

/// <summary>
/// Current state of UnitTest event-stream monitoring and buffer handover.
/// </summary>
public sealed record EventStreamState(
    CommunicationSessionId SessionId,
    bool IsMonitoring,
    EventBufferReadState LastReadState,
    EventStreamBatchReleaseState BatchReleaseState,
    bool BufferObservedEmpty,
    ushort LostEventCount,
    bool BufferFull = false,
    uint ProducerSequenceId = 0,
    uint DeletedSequenceId = 0);

/// <summary>
/// Event arguments for changes to the detailed UnitTest event-stream monitoring state.
/// </summary>
public sealed record EventStreamStateChangedEventArgs(
    EventStreamState PreviousState,
    EventStreamState CurrentState);

/// <summary>
/// Event arguments for PLC project state changes.
/// </summary>
public sealed record PlcProjectStateChangedEventArgs(
    PlcProjectContext Project);

/// <summary>
/// Event arguments for runtime-instance or PLC operating-state changes.
/// </summary>
public sealed record PlcStateChangedEventArgs(
    PlcState State);

/// <summary>
/// Event arguments for UnitTest RunControl observation changes.
/// </summary>
public sealed record UnitTestRunControlObservationChangedEventArgs(
    UnitTestRunControlObservation Observation);
