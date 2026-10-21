using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V6;

internal sealed class StubPlcsimAdvancedV6EnvironmentProbe : IPlcsimAdvancedV6EnvironmentProbe
{
    private readonly PlcsimAdvancedV6EnvironmentProbeResult result;

    public StubPlcsimAdvancedV6EnvironmentProbe(PlcsimAdvancedV6EnvironmentProbeResult result)
    {
        this.result = result;
    }

    public Task<PlcsimAdvancedV6EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }
}

internal sealed class StubPlcsimAdvancedV6WorkerSessionHost : IPlcsimAdvancedV6WorkerSessionHost
{
    private readonly PlcsimAdvancedV6WorkerSessionHostOpenResult result;

    public StubPlcsimAdvancedV6WorkerSessionHost(PlcsimAdvancedV6WorkerSessionHostOpenResult result)
    {
        this.result = result;
    }

    public SiemensBindingContext? BindingContext { get; private set; }

    public int OpenCount { get; private set; }

    public Task<PlcsimAdvancedV6WorkerSessionHostOpenResult> OpenAsync(
        string runtimeApiDirectory,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenCount++;
        BindingContext = bindingContext;
        return Task.FromResult(result);
    }
}

internal sealed class StubPlcsimAdvancedV6WorkerSessionHandle : IPlcsimAdvancedV6WorkerSessionHandle
{
    private readonly PlcsimRuntimeDiscoveryResult discoveryResult;

    public StubPlcsimAdvancedV6WorkerSessionHandle(
        string sessionId,
        PlcsimRuntimeDiscoveryResult? discoveryResult = null)
    {
        SessionId = sessionId;
        this.discoveryResult = discoveryResult ?? new PlcsimRuntimeDiscoveryResult(
            PlcsimOperationStatus.Succeeded,
            Array.Empty<PlcsimRuntimeTargetCandidate>(),
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>());
    }

    public string SessionId { get; }

    public int CloseCommunicationCount { get; private set; }

    public int CloseCount { get; private set; }

    public int TerminateCount { get; private set; }

    public int RevalidateRuntimeCount { get; private set; }

    public PlcsimRuntimeRevalidationRequest? LastRevalidationRequest { get; private set; }

    public bool PreparedRuntimeIsOwned { get; init; }

    public Exception? CloseException { get; init; }

    public PlcsimCommunicationCloseResult? CommunicationCloseResult { get; init; }

    public PlcsimCommunicationPrepareResult? CommunicationPrepareResult { get; init; }

    private EventHandler<PlcsimRuntimeStateChangedEventArgs>? runtimeStateChanged;
    private EventHandler<PlcsimEventBatchReceivedEventArgs>? eventBatchReceived;
    private EventHandler<PlcsimEventStreamMonitoringStateChangedEventArgs>? eventStreamMonitoringStateChanged;
    private EventHandler<PlcsimRunControlObservationChangedEventArgs>? runControlObservationChanged;
    private EventHandler<PlcsimWorkerCommunicationHealthChangedEventArgs>? communicationHealthChanged;
    private EventHandler<PlcsimEventStreamMonitoringFaultedEventArgs>? eventStreamMonitoringFaulted;

    public int RuntimeStateChangedSubscriberCount => runtimeStateChanged?.GetInvocationList().Length ?? 0;

    public int EventBatchReceivedSubscriberCount => eventBatchReceived?.GetInvocationList().Length ?? 0;

    public int EventStreamMonitoringStateChangedSubscriberCount =>
        eventStreamMonitoringStateChanged?.GetInvocationList().Length ?? 0;

    public int RunControlObservationChangedSubscriberCount => runControlObservationChanged?.GetInvocationList().Length ?? 0;

    public int CommunicationHealthChangedSubscriberCount => communicationHealthChanged?.GetInvocationList().Length ?? 0;

    public int EventStreamMonitoringFaultedSubscriberCount => eventStreamMonitoringFaulted?.GetInvocationList().Length ?? 0;

    public event EventHandler<PlcsimRuntimeStateChangedEventArgs>? RuntimeStateChanged
    {
        add => runtimeStateChanged += value;
        remove => runtimeStateChanged -= value;
    }

    public event EventHandler<PlcsimEventBatchReceivedEventArgs>? EventBatchReceived
    {
        add => eventBatchReceived += value;
        remove => eventBatchReceived -= value;
    }

    public event EventHandler<PlcsimEventStreamMonitoringStateChangedEventArgs>? EventStreamMonitoringStateChanged
    {
        add => eventStreamMonitoringStateChanged += value;
        remove => eventStreamMonitoringStateChanged -= value;
    }

    public event EventHandler<PlcsimRunControlObservationChangedEventArgs>? RunControlObservationChanged
    {
        add => runControlObservationChanged += value;
        remove => runControlObservationChanged -= value;
    }

    public event EventHandler<PlcsimWorkerCommunicationHealthChangedEventArgs>? CommunicationHealthChanged
    {
        add => communicationHealthChanged += value;
        remove => communicationHealthChanged -= value;
    }

    public event EventHandler<PlcsimEventStreamMonitoringFaultedEventArgs>? EventStreamMonitoringFaulted
    {
        add => eventStreamMonitoringFaulted += value;
        remove => eventStreamMonitoringFaulted -= value;
    }

    public void RaiseRuntimeStateChanged(PlcsimRuntimeStateChangeKind kind)
    {
        runtimeStateChanged?.Invoke(
            this,
            new PlcsimRuntimeStateChangedEventArgs(
                new PlcsimRuntimeStateChange(
                    SessionId,
                    new PlcsimRuntimeReference("runtime-id", "PlcUnderTest"),
                    kind,
                    PlcsimRuntimeControlState.Stop,
                    PlcsimRuntimeControlState.Unknown,
                    DateTimeOffset.UtcNow,
                    kind.ToString(),
                    Array.Empty<PlcsimDiagnostic>(),
                    Array.Empty<PlcsimEvidence>())));
    }

    public void RaiseEventBatchReceived(UnitTestEventBatch batch)
    {
        eventBatchReceived?.Invoke(this, new PlcsimEventBatchReceivedEventArgs(batch));
    }

    public void RaiseEventStreamMonitoringStateChanged(PlcsimEventStreamMonitoringState state)
    {
        eventStreamMonitoringStateChanged?.Invoke(
            this,
            new PlcsimEventStreamMonitoringStateChangedEventArgs(state));
    }

    public void RaiseRunControlObservationChanged(PlcsimRunControlObservation observation)
    {
        runControlObservationChanged?.Invoke(this, new PlcsimRunControlObservationChangedEventArgs(observation));
    }

    public void RaiseCommunicationHealthChanged(
        string communicationSessionId,
        PlcsimCommunicationHealthState health,
        string message)
    {
        communicationHealthChanged?.Invoke(
            this,
            new PlcsimWorkerCommunicationHealthChangedEventArgs(
                communicationSessionId,
                health,
                message));
    }

    public void RaiseEventStreamMonitoringFaulted(
        string communicationSessionId,
        PlcsimOperationStatus status,
        string message)
    {
        eventStreamMonitoringFaulted?.Invoke(
            this,
            new PlcsimEventStreamMonitoringFaultedEventArgs(
                communicationSessionId,
                status,
                new PlcsimDiagnostic(
                    "Stub.EventStreamMonitoringFaulted",
                    PlcsimDiagnosticSeverity.Error,
                    message,
                    "EventStreamMonitoring",
                    "stub-eventstream-monitoring")));
    }

    public Task<PlcsimRuntimeDiscoveryResult> DiscoverRuntimeTargetsAsync(
        PlcsimRuntimeDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(discoveryResult);
    }

    public Task<PlcsimRuntimePrepareResult> PrepareRuntimeAsync(
        PlcsimRuntimePrepareRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlcsimRuntimePrepareResult(
            PlcsimOperationStatus.Succeeded,
            new PlcsimRuntimeTargetCandidate(
                request.RuntimeReference ?? new PlcsimRuntimeReference("runtime-id", "PlcUnderTest"),
                "6.0",
                "6.0",
                SiemensRuntimeLocality.Local,
                PlcsimRuntimeControlState.Stop,
                true,
                PreparedRuntimeIsOwned),
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public Task<PlcsimRuntimeObservationResult> ObserveRuntimeAsync(
        PlcsimRuntimeObservationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlcsimRuntimeObservationResult(
            PlcsimOperationStatus.Succeeded,
            new PlcsimRuntimeTargetCandidate(
                new PlcsimRuntimeReference("runtime-id", "PlcUnderTest"),
                "6.0",
                "6.0",
                SiemensRuntimeLocality.Local,
                PlcsimRuntimeControlState.Stop,
                true,
                false),
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public Task<PlcsimRuntimeObservationResult> RevalidateRuntimeAsync(
        PlcsimRuntimeRevalidationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RevalidateRuntimeCount++;
        LastRevalidationRequest = request;
        return Task.FromResult(new PlcsimRuntimeObservationResult(
            PlcsimOperationStatus.Succeeded,
            new PlcsimRuntimeTargetCandidate(
                request.ExpectedRuntime,
                "6.0",
                "6.0",
                SiemensRuntimeLocality.Local,
                PlcsimRuntimeControlState.Stop,
                true,
                PreparedRuntimeIsOwned),
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public Task<PlcsimRuntimeControlResult> StartRuntimeAsync(
        PlcsimRuntimeControlRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlcsimRuntimeControlResult(
            PlcsimOperationStatus.Succeeded,
            PlcsimRuntimeControlState.Run,
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public Task<PlcsimRuntimeControlResult> StopRuntimeAsync(
        PlcsimRuntimeControlRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlcsimRuntimeControlResult(
            PlcsimOperationStatus.Succeeded,
            PlcsimRuntimeControlState.Stop,
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public Task<PlcsimRuntimeReleaseResult> ReleaseRuntimeAsync(
        PlcsimRuntimeReleaseRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlcsimRuntimeReleaseResult(
            PlcsimOperationStatus.Succeeded,
            true,
            request.CleanupPreference,
            request.CleanupPreference == PlcsimRuntimeCleanupPreference.Release,
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public Task<PlcsimCommunicationPrepareResult> PrepareCommunicationAsync(
        PlcsimCommunicationPrepareRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CommunicationPrepareResult ?? new PlcsimCommunicationPrepareResult(
            PlcsimOperationStatus.Succeeded,
            new StubPlcsimCommunicationSession(new PlcsimCommunicationSessionInfo(
                request.RuntimeSessionId + "-communication",
                request.RuntimeSessionId,
                request.RunControlRoot,
                request.EventBufferRoot,
                DateTimeOffset.UtcNow)),
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public Task<PlcsimCommunicationCloseResult> CloseCommunicationAsync(
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseCommunicationCount++;
        return Task.FromResult(CommunicationCloseResult ?? new PlcsimCommunicationCloseResult(
            PlcsimOperationStatus.Succeeded,
            communication.SessionId,
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public Task<PlcsimRunControlObservationResult> ObserveRunControlAsync(
        PlcsimRunControlObservationRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlcsimRunControlObservationResult(
            PlcsimOperationStatus.Succeeded,
            new PlcsimRunControlObservation(
                UnitTestExecutionStatus.Completed,
                UnitTestExecutionSignals.Completed,
                UnitTestLifecycleState.Completed,
                UnitTestResultState.NotEvaluated),
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public Task<PlcsimRunControlCommandResult> RequestRunControlCommandAsync(
        PlcsimRunControlCommandRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlcsimRunControlCommandResult(
            PlcsimOperationStatus.Succeeded,
            request.Command,
            true,
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public Task<PlcsimEventBufferResetResult> RequestEventBufferResetAsync(
        PlcsimEventBufferResetRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlcsimEventBufferResetResult(
            PlcsimOperationStatus.Succeeded,
            Accepted: true,
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public Task<PlcsimEventStreamMonitoringResult> StartEventStreamMonitoringAsync(
        PlcsimEventStreamMonitoringRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlcsimEventStreamMonitoringResult(
            PlcsimOperationStatus.Succeeded,
            new PlcsimEventStreamMonitoringState(
                request.CommunicationSessionId,
                IsMonitoring: true,
                EventBufferReadState.Empty,
                EventStreamBatchReleaseState.Idle,
                BufferObservedEmpty: true,
                LostEventCount: 0),
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public Task<PlcsimEventStreamMonitoringResult> StopEventStreamMonitoringAsync(
        PlcsimEventStreamStopRequest request,
        PlcsimCommunicationSessionInfo communication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlcsimEventStreamMonitoringResult(
            PlcsimOperationStatus.Succeeded,
            new PlcsimEventStreamMonitoringState(
                request.CommunicationSessionId,
                IsMonitoring: false,
                EventBufferReadState.Empty,
                EventStreamBatchReleaseState.Idle,
                BufferObservedEmpty: true,
                LostEventCount: 0),
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public void Terminate(string message)
    {
        TerminateCount++;
        RaiseRuntimeStateChanged(PlcsimRuntimeStateChangeKind.ConnectionLost);
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseCount++;
        if (CloseException is not null)
        {
            throw CloseException;
        }

        return Task.CompletedTask;
    }
}

internal sealed class StubPlcsimCommunicationSession : IPlcsimUnitTestCommunicationSession
{
    public StubPlcsimCommunicationSession(PlcsimCommunicationSessionInfo communication)
    {
        Communication = communication;
    }

    public PlcsimCommunicationSessionInfo Communication { get; }

    public PlcsimCommunicationHealthState Health => PlcsimCommunicationHealthState.Healthy;

#pragma warning disable CS0067 // Contract events are intentionally not raised by this stub.
    public event EventHandler<PlcsimCommunicationHealthChangedEventArgs>? HealthChanged;

    public event EventHandler<PlcsimRunControlObservationChangedEventArgs>? RunControlObservationChanged;

    public event EventHandler<PlcsimEventBatchReceivedEventArgs>? EventBatchReceived;

    public event EventHandler<PlcsimEventStreamMonitoringStateChangedEventArgs>? EventStreamMonitoringStateChanged;
#pragma warning restore CS0067

    public Task<PlcsimRunControlValidationResult> ValidateRunControlAsync(
        PlcsimRunControlValidationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlcsimRunControlValidationResult(
            PlcsimOperationStatus.Succeeded,
            true,
            request.ExpectedContractVersion,
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>()));
    }

    public Task<PlcsimRunControlCommandResult> RequestRunControlCommandAsync(
        PlcsimRunControlCommandRequest request,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<PlcsimEventBufferResetResult> RequestEventBufferResetAsync(
        PlcsimEventBufferResetRequest request,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<PlcsimRunControlObservationResult> ObserveRunControlAsync(
        PlcsimRunControlObservationRequest request,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<PlcsimEventBufferValidationResult> ValidateEventBufferAsync(
        PlcsimEventBufferValidationRequest request,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<PlcsimEventStreamMonitoringResult> StartEventStreamMonitoringAsync(
        PlcsimEventStreamMonitoringRequest request,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<PlcsimEventStreamMonitoringResult> StopEventStreamMonitoringAsync(
        PlcsimEventStreamStopRequest request,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
