using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Fake;

/// <summary>
/// Deterministic fake PLC runtime control service for shared-core tests.
/// </summary>
public sealed class FakePlcControlService : FakeAccessProfileServiceBase, IPlcControlService
{
    private TaskCompletionSource<OperationResult<PlcState>>? pendingStartCompletion;
    private TaskCompletionSource<OperationResult<PlcState>>? pendingStopCompletion;
    private CancellationTokenRegistration pendingStartCancellation;
    private CancellationTokenRegistration pendingStopCancellation;
    private readonly TimeProvider timeProvider;

    public FakePlcControlService()
        : this(TimeProvider.System)
    {
    }

    public FakePlcControlService(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
    }

    public RuntimeTargetRef DefaultRuntimeTarget { get; set; } = new(
        new RuntimeTargetId("fake-runtime-target"),
        "Fake PLC Runtime Target",
        "FakeRuntimeTarget");

    public PlcState CurrentState { get; private set; } = new(
        RuntimeInstanceState.Unavailable,
        PlcOperatingState.Unavailable,
        Array.Empty<RuntimeTargetRef>(),
        ReadyForTransfer: false,
        ReadyForExecution: false);

    public OperationResult<PreparedPlcRuntime>? PrepareRuntimeResult { get; set; }

    public Func<CancellationToken, Task<OperationResult<PreparedPlcRuntime>>>? PrepareRuntimeHandler { get; set; }

    public PlcState? RuntimeStateObservedDuringPreparation { get; set; }

    public PlcRuntimeDiscoveryRequest? LastDiscoverRuntimeTargetsRequest { get; private set; }

    public PlcRuntimePrepareRequest? LastPrepareRuntimeRequest { get; private set; }

    public PlcRuntimeReleaseRequest? LastReleaseRuntimeRequest { get; private set; }

    public OperationResult<RuntimeTargetDiscoveryResult>? DiscoverRuntimeTargetsResult { get; set; }

    public OperationResult<RuntimeTargetResolution>? ResolveRuntimeTargetResult { get; set; }

    public OperationResult<PlcState>? StartResult { get; set; }

    public OperationResult<PlcState>? StopResult { get; set; }

    public OperationResult<PlcRuntimeReleaseOutcome>? ReleaseRuntimeResult { get; set; }

    public bool SupportsRuntimeDestruction { get; set; } = true;

    public bool HoldNextStart { get; set; }

    public bool HoldNextStop { get; set; }

    public bool IsStartPending => pendingStartCompletion is not null;

    public bool IsStopPending => pendingStopCompletion is not null;

    public event EventHandler<PlcStateChangedEventArgs>? PlcStateChangedEventHandler;

    protected override AccessServiceKind GetServiceKind()
    {
        return AccessServiceKind.PlcControl;
    }

    public Task<OperationResult<RuntimeTargetDiscoveryResult>> DiscoverRuntimeTargetsAsync(
        PlcRuntimeDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastDiscoverRuntimeTargetsRequest = request;

        var result = DiscoverRuntimeTargetsResult ?? OperationResult<RuntimeTargetDiscoveryResult>.Success(
            new RuntimeTargetDiscoveryResult(
                [DefaultRuntimeTarget],
                TargetDiscoveryState.CandidatesAvailable,
                Array.Empty<Diagnostic>(),
                DefaultTarget: DefaultRuntimeTarget));

        return Task.FromResult(result);
    }

    public async Task<OperationResult<PreparedPlcRuntime>> PrepareRuntimeAsync(
        PlcRuntimePrepareRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastPrepareRuntimeRequest = request;
        SetState(ServiceState.Busy, "Preparing fake PLC runtime.");

        var result = PrepareRuntimeHandler is not null
            ? await PrepareRuntimeHandler(cancellationToken)
            : PrepareRuntimeResult ?? OperationResult<PreparedPlcRuntime>.Success(
                new PreparedPlcRuntime(
                    new PlcRuntimeContext(new RuntimeContextId("fake-runtime"), "Fake PLC Runtime", "FakeRuntime"),
                    request.RuntimeTarget ?? DefaultRuntimeTarget));
        ReportFailureDiagnostic(
            result,
            "Fake.Runtime.Prepare.Failed",
            DiagnosticPhase.RuntimeProvisioning);

        if (result.IsSuccessful)
        {
            var observedState = RuntimeStateObservedDuringPreparation;
            SetCurrentState(
                observedState?.InstanceState ?? RuntimeInstanceState.Available,
                observedState?.OperatingState ?? PlcOperatingState.Stopped,
                observedState?.ReadyForTransfer ?? true,
                observedState?.ReadyForExecution ?? false);
        }

        SetState(
            result.IsSuccessful
                ? ServiceState.Ready
                : result.State == OperationResultState.Cancelled
                    ? CurrentState.InstanceState == RuntimeInstanceState.Unavailable
                        ? ServiceState.NotPrepared
                        : ServiceState.Ready
                    : ServiceState.NeedsRevalidation,
            "Fake PLC runtime preparation finished.");
        return result;
    }

    public Task<OperationResult<RuntimeTargetResolution>> ResolveRuntimeTargetAsync(
        PlcRuntimeContext runtime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = ResolveRuntimeTargetResult ?? OperationResult<RuntimeTargetResolution>.Success(
            new RuntimeTargetResolution([DefaultRuntimeTarget], DefaultRuntimeTarget, TargetResolutionState.Resolved));

        return Task.FromResult(result);
    }

    public Task<OperationResult<PlcState>> StartAsync(
        PlcRuntimeContext runtime,
        RuntimeTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HoldNextStart)
        {
            if (pendingStartCompletion is not null)
            {
                return Task.FromResult(OperationResult<PlcState>.Failure("A fake PLC start operation is already pending."));
            }

            HoldNextStart = false;
            pendingStartCompletion = new TaskCompletionSource<OperationResult<PlcState>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pendingStartCancellation = cancellationToken.Register(CancelPendingStart);
            return pendingStartCompletion.Task;
        }

        if (StartResult is { } result)
        {
            ReportFailureDiagnostic(
                result,
                "Fake.Runtime.Start.Failed",
                DiagnosticPhase.RuntimeStartup);
            return Task.FromResult(result);
        }

        SetCurrentState(RuntimeInstanceState.Available, PlcOperatingState.Running, readyForTransfer: true, readyForExecution: true);
        return Task.FromResult(OperationResult<PlcState>.Success(CurrentState));
    }

    public Task<OperationResult<PlcState>> StopAsync(
        PlcRuntimeContext runtime,
        RuntimeTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HoldNextStop)
        {
            if (pendingStopCompletion is not null)
            {
                return Task.FromResult(OperationResult<PlcState>.Failure("A fake PLC stop operation is already pending."));
            }

            HoldNextStop = false;
            pendingStopCompletion = new TaskCompletionSource<OperationResult<PlcState>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pendingStopCancellation = cancellationToken.Register(CancelPendingStop);
            return pendingStopCompletion.Task;
        }

        if (StopResult is { } result)
        {
            ReportFailureDiagnostic(
                result,
                "Fake.Runtime.Stop.Failed",
                DiagnosticPhase.RuntimeStartup);
            return Task.FromResult(result);
        }

        SetCurrentState(RuntimeInstanceState.Available, PlcOperatingState.Stopped, readyForTransfer: true, readyForExecution: false);
        return Task.FromResult(OperationResult<PlcState>.Success(CurrentState));
    }

    public Task<OperationResult<PlcRuntimeReleaseOutcome>> ReleaseRuntimeAsync(
        PlcRuntimeReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        LastReleaseRuntimeRequest = request;
        SetCurrentState(RuntimeInstanceState.Unavailable, PlcOperatingState.Unavailable, readyForTransfer: false, readyForExecution: false);
        SetState(ServiceState.NotPrepared, "Fake PLC control service released.");

        if (ReleaseRuntimeResult is { } result)
        {
            return Task.FromResult(result);
        }

        if (request.ReleaseMode == RuntimeResourceReleaseMode.KeepResource)
        {
            return Task.FromResult(OperationResult<PlcRuntimeReleaseOutcome>.Success(
                PlcRuntimeReleaseOutcome.ResourceKept));
        }

        if (!SupportsRuntimeDestruction)
        {
            return Task.FromResult(OperationResult<PlcRuntimeReleaseOutcome>.NotSupported(
                PlcRuntimeReleaseOutcome.ResourceKept,
                "Fake PLC runtime destruction is not supported."));
        }

        return Task.FromResult(OperationResult<PlcRuntimeReleaseOutcome>.Success(
            PlcRuntimeReleaseOutcome.ResourceDestroyed));
    }

    public Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetCurrentState(RuntimeInstanceState.Unavailable, PlcOperatingState.Unavailable, readyForTransfer: false, readyForExecution: false);
        SetState(ServiceState.NotPrepared, "Fake PLC control service cleaned up.");
        return Task.FromResult(OperationResult.Success());
    }

    public void SimulateExternalState(
        RuntimeInstanceState instanceState,
        PlcOperatingState operatingState,
        bool readyForTransfer,
        bool readyForExecution)
    {
        SetCurrentState(instanceState, operatingState, readyForTransfer, readyForExecution);
    }

    public void SimulateExternalPlcRunning()
    {
        SetState(ServiceState.Ready, "Fake PLC was externally observed as running.");
        SetCurrentState(RuntimeInstanceState.Available, PlcOperatingState.Running, readyForTransfer: true, readyForExecution: true);
    }

    public void SimulateExternalPlcStopped()
    {
        SetState(ServiceState.Ready, "Fake PLC was externally observed as stopped.");
        SetCurrentState(RuntimeInstanceState.Available, PlcOperatingState.Stopped, readyForTransfer: true, readyForExecution: false);
    }

    public void SimulateRuntimeContextLost()
    {
        SetState(ServiceState.Invalidated, "Fake runtime context was externally lost.");
        SetCurrentState(RuntimeInstanceState.Unavailable, PlcOperatingState.Unavailable, readyForTransfer: false, readyForExecution: false);
    }

    public void SimulatePlcControlUnavailable()
    {
        SetState(ServiceState.Invalidated, "Fake PLC control service is externally unavailable.");
        SetCurrentState(RuntimeInstanceState.Unavailable, PlcOperatingState.Unavailable, readyForTransfer: false, readyForExecution: false);
    }

    public void RepublishCurrentState()
    {
        SetState(ServiceState.Ready, "Fake PLC runtime access reconnected.");
        PlcStateChangedEventHandler?.Invoke(this, new PlcStateChangedEventArgs(CurrentState));
    }

    public void CompletePendingStartAsRunning()
    {
        var completion = pendingStartCompletion
            ?? throw new InvalidOperationException("No fake PLC start operation is pending.");
        ClearPendingStart();

        SetCurrentState(RuntimeInstanceState.Available, PlcOperatingState.Running, readyForTransfer: true, readyForExecution: true);
        completion.TrySetResult(OperationResult<PlcState>.Success(CurrentState));
    }

    public void CompletePendingStart(OperationResult<PlcState> result)
    {
        var completion = pendingStartCompletion
            ?? throw new InvalidOperationException("No fake PLC start operation is pending.");
        ClearPendingStart();

        ReportFailureDiagnostic(
            result,
            "Fake.Runtime.Start.Failed",
            DiagnosticPhase.RuntimeStartup);
        completion.TrySetResult(result);
    }

    public void FailPendingStart(string message)
    {
        CompletePendingStart(OperationResult<PlcState>.Failure(message));
    }

    public void CompletePendingStopAsStopped()
    {
        var completion = pendingStopCompletion
            ?? throw new InvalidOperationException("No fake PLC stop operation is pending.");
        ClearPendingStop();

        SetCurrentState(RuntimeInstanceState.Available, PlcOperatingState.Stopped, readyForTransfer: true, readyForExecution: false);
        completion.TrySetResult(OperationResult<PlcState>.Success(CurrentState));
    }

    public void CompletePendingStop(OperationResult<PlcState> result)
    {
        var completion = pendingStopCompletion
            ?? throw new InvalidOperationException("No fake PLC stop operation is pending.");
        ClearPendingStop();

        ReportFailureDiagnostic(
            result,
            "Fake.Runtime.Stop.Failed",
            DiagnosticPhase.RuntimeStartup);
        completion.TrySetResult(result);
    }

    public void FailPendingStop(string message)
    {
        CompletePendingStop(OperationResult<PlcState>.Failure(message));
    }

    private void SetCurrentState(
        RuntimeInstanceState instanceState,
        PlcOperatingState operatingState,
        bool readyForTransfer,
        bool readyForExecution)
    {
        CurrentState = new PlcState(
            instanceState,
            operatingState,
            instanceState == RuntimeInstanceState.Unavailable ? Array.Empty<RuntimeTargetRef>() : [DefaultRuntimeTarget],
            readyForTransfer,
            readyForExecution);

        PlcStateChangedEventHandler?.Invoke(this, new PlcStateChangedEventArgs(CurrentState));
    }

    private void CancelPendingStart()
    {
        var completion = pendingStartCompletion;
        pendingStartCompletion = null;
        pendingStartCancellation = default;
        completion?.TrySetCanceled();
    }

    private void CancelPendingStop()
    {
        var completion = pendingStopCompletion;
        pendingStopCompletion = null;
        pendingStopCancellation = default;
        completion?.TrySetCanceled();
    }

    private void ClearPendingStart()
    {
        pendingStartCompletion = null;
        pendingStartCancellation.Dispose();
        pendingStartCancellation = default;
    }

    private void ClearPendingStop()
    {
        pendingStopCompletion = null;
        pendingStopCancellation.Dispose();
        pendingStopCancellation = default;
    }

    private void ReportFailureDiagnostic<T>(
        OperationResult<T> result,
        string code,
        DiagnosticPhase phase)
    {
        if (result.IsSuccessful)
        {
            return;
        }

        ReportDiagnostic(FakeDiagnostics.OperationError(
            code,
            DiagnosticCategory.Workflow,
            phase,
            result.Message ?? "Fake PLC runtime operation failed.",
            timeProvider.GetUtcNow()));
    }
}
