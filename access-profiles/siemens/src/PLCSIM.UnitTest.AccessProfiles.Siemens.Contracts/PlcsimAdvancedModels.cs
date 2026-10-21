using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

public enum PlcsimOperationStatus
{
    Succeeded,
    SucceededWithWarnings,
    Unavailable,
    Rejected,
    Failed,
    Cancelled,
    TimedOut,
    Unclear
}

public enum PlcsimDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public enum PlcsimRuntimeHealthState
{
    Unknown,
    Healthy,
    Unavailable,
    Degraded,
    RevalidationRequired,
    Invalidated,
    Faulted,
    Disposed
}

public enum PlcsimCommunicationHealthState
{
    Unknown,
    Healthy,
    Unavailable,
    Degraded,
    RevalidationRequired,
    Invalidated,
    Faulted,
    Disposed
}

public enum PlcsimRuntimeControlState
{
    Unknown,
    Off,
    Booting,
    Stop,
    Startup,
    Run,
    Freeze,
    ShuttingDown,
    Hold,
    Invalid
}

public enum PlcsimRuntimeStateChangeKind
{
    OperatingStateChanged,
    RuntimeManagerLost,
    ConnectionLost,
    SessionDisposed
}

public enum PlcsimRuntimeCleanupPreference
{
    Keep,
    Release
}

public enum PlcsimRuntimeProvisioningMode
{
    UseExisting = 0,
    CreateOwned = 1
}

public enum PlcsimRuntimeCpuType
{
    S71500
}

public enum PlcsimCommunicationObservationStrategyKind
{
    Polling,
    PlcCycle
}

public sealed record PlcsimRuntimeSessionOpenRequest(
    PlcsimRuntimeApiExpectation ApiExpectation,
    PlcsimRuntimeReference? RuntimeReference,
    PlcsimRuntimeStoragePreference StoragePreference,
    PlcsimRuntimeCleanupPreference CleanupPreference,
    string? CorrelationId);

public sealed record PlcsimRuntimeApiExpectation(
    string ProductVersion,
    string AccessApiVersion,
    SiemensRuntimeLocality Locality);

public sealed record PlcsimRuntimeReference(
    string? Id,
    string? InstanceName);

public sealed record PlcsimRuntimeStoragePreference(
    string? StoragePath);

public sealed record PlcsimRuntimeSessionOpenResult(
    PlcsimOperationStatus Status,
    IPlcsimRuntimeSession? Session,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimRuntimeSessionInfo(
    string SessionId,
    PlcsimRuntimeReference? RuntimeReference,
    bool IsOwned,
    DateTimeOffset OpenedAt);

public sealed record PlcsimRuntimeDiscoveryRequest(
    string? CorrelationId);

public sealed record PlcsimRuntimeTargetCandidate(
    PlcsimRuntimeReference Reference,
    string ProductVersion,
    string AccessApiVersion,
    SiemensRuntimeLocality Locality,
    PlcsimRuntimeControlState State,
    bool IsUsable,
    bool IsOwned);

public sealed record PlcsimRuntimeStateChange(
    string RuntimeSessionId,
    PlcsimRuntimeReference? RuntimeReference,
    PlcsimRuntimeStateChangeKind Kind,
    PlcsimRuntimeControlState? PreviousState,
    PlcsimRuntimeControlState CurrentState,
    DateTimeOffset ObservedAt,
    string Message,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence);

public sealed record PlcsimRuntimeDiscoveryResult(
    PlcsimOperationStatus Status,
    IReadOnlyList<PlcsimRuntimeTargetCandidate> Candidates,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimRuntimePrepareRequest(
    PlcsimRuntimeReference? RuntimeReference,
    PlcsimRuntimeStoragePreference StoragePreference,
    PlcsimRuntimeProvisioningMode ProvisioningMode,
    PlcsimRuntimeCpuType CpuType,
    TimeSpan Timeout,
    string? CorrelationId);

public sealed record PlcsimRuntimePrepareResult(
    PlcsimOperationStatus Status,
    PlcsimRuntimeTargetCandidate? Runtime,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimRuntimeObservationRequest(
    string? CorrelationId);

public sealed record PlcsimRuntimeRevalidationRequest(
    PlcsimRuntimeReference ExpectedRuntime,
    TimeSpan Timeout,
    string? CorrelationId);

public sealed record PlcsimRuntimeObservationResult(
    PlcsimOperationStatus Status,
    PlcsimRuntimeTargetCandidate? Runtime,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimRuntimeControlRequest(
    string SessionId,
    TimeSpan Timeout,
    string? CorrelationId);

public sealed record PlcsimRuntimeControlResult(
    PlcsimOperationStatus Status,
    PlcsimRuntimeControlState State,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimRuntimeReleaseRequest(
    string SessionId,
    PlcsimRuntimeCleanupPreference CleanupPreference,
    TimeSpan Timeout,
    string? CorrelationId);

public sealed record PlcsimRuntimeReleaseResult(
    PlcsimOperationStatus Status,
    bool Released,
    PlcsimRuntimeCleanupPreference CleanupPreference,
    bool StorageCleaned,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimCommunicationPrepareRequest(
    string RuntimeSessionId,
    string RunControlRoot,
    string EventBufferRoot,
    string ExpectedRunControlContractVersion,
    string ExpectedEventStreamContractVersion,
    PlcsimCommunicationWorkerOptions WorkerOptions,
    TimeSpan PrepareTimeout,
    TimeSpan RevalidationTimeout,
    string? CorrelationId);

public sealed record PlcsimCommunicationWorkerOptions(
    PlcsimCommunicationObservationStrategyKind ObservationStrategy,
    TimeSpan? MonitoringInterval)
{
    public TimeSpan? DrainInterval { get; init; }

    public int? MaxEventsPerBatch { get; init; }

    [Obsolete("Use MonitoringInterval.")]
    public TimeSpan? PollInterval => MonitoringInterval;
}

public sealed record PlcsimCommunicationPrepareResult(
    PlcsimOperationStatus Status,
    IPlcsimUnitTestCommunicationSession? Session,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimCommunicationSessionInfo(
    string SessionId,
    string RuntimeSessionId,
    string RunControlRoot,
    string EventBufferRoot,
    DateTimeOffset PreparedAt);

public sealed record PlcsimCommunicationCloseResult(
    PlcsimOperationStatus Status,
    string CommunicationSessionId,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimRunControlValidationRequest(
    string CommunicationSessionId,
    string ExpectedContractVersion,
    string? CorrelationId);

public sealed record PlcsimRunControlValidationResult(
    PlcsimOperationStatus Status,
    bool ContractValid,
    string? ObservedContractVersion,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimRunControlCommandRequest(
    string CommunicationSessionId,
    UnitTestRunCommand Command,
    TimeSpan Timeout,
    string? CorrelationId);

public sealed record PlcsimRunControlCommandResult(
    PlcsimOperationStatus Status,
    UnitTestRunCommand Command,
    bool Accepted,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimEventBufferResetRequest(
    string CommunicationSessionId,
    TimeSpan Timeout,
    string? CorrelationId);

public sealed record PlcsimEventBufferResetResult(
    PlcsimOperationStatus Status,
    bool Accepted,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimRunControlObservationRequest(
    string CommunicationSessionId,
    string? CorrelationId);

public sealed record PlcsimRunControlObservationResult(
    PlcsimOperationStatus Status,
    PlcsimRunControlObservation? Observation,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimRunControlObservation(
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

public sealed record PlcsimEventBufferValidationRequest(
    string CommunicationSessionId,
    string ExpectedContractVersion,
    string? CorrelationId);

public sealed record PlcsimEventBufferValidationResult(
    PlcsimOperationStatus Status,
    bool ContractValid,
    string? ObservedContractVersion,
    PlcsimEventBufferObservation Observation,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimEventStreamMonitoringRequest(
    string CommunicationSessionId,
    string? CorrelationId);

public sealed record PlcsimEventStreamStopRequest(
    string CommunicationSessionId,
    string? CorrelationId);

public sealed record PlcsimEventStreamMonitoringResult(
    PlcsimOperationStatus Status,
    PlcsimEventStreamMonitoringState State,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

public sealed record PlcsimEventStreamMonitoringState(
    string CommunicationSessionId,
    bool IsMonitoring,
    EventBufferReadState LastReadState,
    EventStreamBatchReleaseState BatchReleaseState,
    bool BufferObservedEmpty,
    ushort LostEventCount,
    bool BufferFull = false,
    uint ProducerSequenceId = 0,
    uint DeletedSequenceId = 0);

public sealed record PlcsimEventBufferObservation(
    bool ContractValid,
    EventBufferReadState ReadState,
    EventStreamBatchReleaseState BatchReleaseState,
    bool BufferObservedEmpty,
    ushort LostEventCount);

public sealed record PlcsimEventBatchReceivedEventArgs(
    UnitTestEventBatch Batch);

public sealed record PlcsimRuntimeProgressSnapshot(
    string Operation,
    string Phase,
    string Message,
    int? Percent);

public sealed record PlcsimDiagnostic(
    string Code,
    PlcsimDiagnosticSeverity Severity,
    string Message,
    string Phase,
    string? EvidenceId);

public sealed record PlcsimEvidence(
    string Id,
    string Kind,
    string Summary);
