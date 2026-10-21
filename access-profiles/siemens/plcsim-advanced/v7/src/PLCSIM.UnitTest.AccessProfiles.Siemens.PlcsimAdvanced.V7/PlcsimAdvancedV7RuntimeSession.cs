using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7;

internal sealed class PlcsimAdvancedV7RuntimeSession : IPlcsimRuntimeSession
{
    private readonly IPlcsimAdvancedV7WorkerSessionHandle workerSession;
    private readonly Action unregisterSession;
    private PlcsimRuntimePrepareRequest? cancelledPrepareRequest;
    private PlcsimRuntimePrepareResult? cancelledPrepareCompletion;
    private int finalized;

    public PlcsimAdvancedV7RuntimeSession(
        IPlcsimAdvancedV7WorkerSessionHandle workerSession,
        PlcsimRuntimeReference? runtimeReference,
        DateTimeOffset openedAt,
        Action unregisterSession)
    {
        this.workerSession = workerSession ?? throw new ArgumentNullException(nameof(workerSession));
        this.unregisterSession = unregisterSession ?? throw new ArgumentNullException(nameof(unregisterSession));
        Runtime = new PlcsimRuntimeSessionInfo(
            workerSession.SessionId,
            runtimeReference,
            false,
            openedAt);
        Health = PlcsimRuntimeHealthState.Healthy;
        workerSession.RuntimeStateChanged += OnWorkerRuntimeStateChanged;
    }

    public PlcsimRuntimeSessionInfo Runtime { get; private set; }

    public PlcsimRuntimeHealthState Health { get; private set; }

    internal int? WorkerProcessId =>
        (workerSession as PlcsimAdvancedV7WorkerSessionHandle)?.WorkerProcessId;

    public event EventHandler<PlcsimRuntimeHealthChangedEventArgs>? HealthChanged;

    public event EventHandler<PlcsimRuntimeStateChangedEventArgs>? RuntimeStateChanged;

    public event EventHandler<PlcsimRuntimeProgressSnapshot>? ProgressChanged;

    public async Task<PlcsimRuntimeDiscoveryResult> DiscoverRuntimeTargetsAsync(
        PlcsimRuntimeDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        ReportProgress("DiscoverRuntimeTargets", "Started", "PLCSIM Advanced V7 runtime target discovery started.", null);
        var result = await workerSession.DiscoverRuntimeTargetsAsync(request, cancellationToken).ConfigureAwait(false);
        ReportProgress(
            "DiscoverRuntimeTargets",
            result.Succeeded ? "Completed" : "Failed",
            result.Succeeded
                ? "PLCSIM Advanced V7 runtime target discovery completed."
                : "PLCSIM Advanced V7 runtime target discovery failed.",
            result.Succeeded ? 100 : null);
        return result;
    }

    public async Task<PlcsimRuntimePrepareResult> PrepareRuntimeAsync(
        PlcsimRuntimePrepareRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cancelledPrepareCompletion is not null)
        {
            if (!IsSameRuntimePrepareRequest(cancelledPrepareRequest, request))
            {
                return new PlcsimRuntimePrepareResult(
                    PlcsimOperationStatus.Rejected,
                    cancelledPrepareCompletion.Runtime,
                    new[]
                    {
                        new PlcsimDiagnostic(
                            "PlcsimAdvanced.V7.RuntimePrepareRetryChanged",
                            PlcsimDiagnosticSeverity.Error,
                            "A cancelled PLCSIM Advanced runtime preparation already completed. Retry with the same runtime setup or reopen the runtime session before changing it.",
                            "PrepareRuntime",
                            "plcsim-advanced-v7-runtime-prepare")
                    },
                    cancelledPrepareCompletion.Evidence);
            }

            var retainedCompletion = cancelledPrepareCompletion;
            cancelledPrepareRequest = null;
            cancelledPrepareCompletion = null;
            ReportProgress(
                "PrepareRuntime",
                "Prepared",
                "PLCSIM Advanced V7 runtime preparation adopted the completion retained after cancellation.",
                100);
            return retainedCompletion with
            {
                Status = PlcsimOperationStatus.Succeeded,
                Diagnostics = Array.Empty<PlcsimDiagnostic>()
            };
        }

        ReportProgress("PrepareRuntime", "Started", "PLCSIM Advanced V7 runtime preparation started.", null);
        var result = await workerSession.PrepareRuntimeAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Runtime is not null)
        {
            Runtime = Runtime with
            {
                RuntimeReference = result.Runtime.Reference,
                IsOwned = result.Runtime.IsOwned
            };
        }

        if (result.Status == PlcsimOperationStatus.Cancelled && result.Runtime is not null)
        {
            cancelledPrepareRequest = request;
            cancelledPrepareCompletion = result;
        }

        ReportProgress(
            "PrepareRuntime",
            result.Succeeded
                ? "Prepared"
                : result.Status == PlcsimOperationStatus.Cancelled
                    ? "Cancelled"
                    : "Failed",
            result.Succeeded
                ? "PLCSIM Advanced V7 runtime preparation completed."
                : result.Status == PlcsimOperationStatus.Cancelled
                    ? "PLCSIM Advanced V7 runtime preparation was cancelled after retaining any completed runtime ownership."
                : "PLCSIM Advanced V7 runtime preparation failed.",
            result.Succeeded ? 100 : null);
        return result;
    }

    public async Task<PlcsimRuntimeObservationResult> ObserveRuntimeAsync(
        PlcsimRuntimeObservationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress("ObserveRuntime", "Started", "PLCSIM Advanced V7 runtime observation started.", null);
        var result = await workerSession.ObserveRuntimeAsync(request, cancellationToken).ConfigureAwait(false);
        ReportProgress(
            "ObserveRuntime",
            result.Succeeded ? "Observed" : "Failed",
            result.Succeeded
                ? "PLCSIM Advanced V7 runtime observation completed."
                : "PLCSIM Advanced V7 runtime observation failed.",
            result.Succeeded ? 100 : null);
        return result;
    }

    public async Task<PlcsimRuntimeObservationResult> RevalidateRuntimeAsync(
        PlcsimRuntimeRevalidationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress("RevalidateRuntime", "Started", "PLCSIM Advanced V7 runtime revalidation started.", null);
        var result = await workerSession.RevalidateRuntimeAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Runtime is not null)
        {
            Runtime = Runtime with
            {
                RuntimeReference = result.Runtime.Reference,
                IsOwned = result.Runtime.IsOwned
            };
        }

        if (!result.Succeeded)
        {
            SetHealth(
                PlcsimRuntimeHealthState.Invalidated,
                result.Diagnostics.FirstOrDefault()?.Message
                    ?? "PLCSIM Advanced V7 runtime revalidation failed.");
        }

        ReportProgress(
            "RevalidateRuntime",
            result.Succeeded ? "Revalidated" : "Failed",
            result.Succeeded
                ? "PLCSIM Advanced V7 runtime revalidation completed."
                : "PLCSIM Advanced V7 runtime revalidation failed.",
            result.Succeeded ? 100 : null);
        return result;
    }

    public async Task<PlcsimRuntimeControlResult> StartRuntimeAsync(
        PlcsimRuntimeControlRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress("StartRuntime", "Started", "PLCSIM Advanced V7 runtime start started.", null);
        var result = await workerSession.StartRuntimeAsync(request, cancellationToken).ConfigureAwait(false);
        ReportProgress(
            "StartRuntime",
            result.Succeeded ? "Accepted" : "Failed",
            result.Succeeded
                ? "PLCSIM Advanced V7 runtime start command accepted."
                : "PLCSIM Advanced V7 runtime start failed.",
            result.Succeeded ? 100 : null);
        return result;
    }

    public async Task<PlcsimRuntimeControlResult> StopRuntimeAsync(
        PlcsimRuntimeControlRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress("StopRuntime", "Started", "PLCSIM Advanced V7 runtime stop started.", null);
        var result = await workerSession.StopRuntimeAsync(request, cancellationToken).ConfigureAwait(false);
        ReportProgress(
            "StopRuntime",
            result.Succeeded ? "Accepted" : "Failed",
            result.Succeeded
                ? "PLCSIM Advanced V7 runtime stop command accepted."
                : "PLCSIM Advanced V7 runtime stop failed.",
            result.Succeeded ? 100 : null);
        return result;
    }

    public Task<PlcsimRuntimeReleaseResult> ReleaseRuntimeAsync(
        PlcsimRuntimeReleaseRequest request,
        CancellationToken cancellationToken)
    {
        return ReleaseRuntimeCoreAsync(request, cancellationToken);
    }

    private async Task<PlcsimRuntimeReleaseResult> ReleaseRuntimeCoreAsync(
        PlcsimRuntimeReleaseRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress("ReleaseRuntime", "Started", "PLCSIM Advanced V7 runtime release started.", null);
        var result = await workerSession.ReleaseRuntimeAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            FinalizeSession(
                PlcsimRuntimeHealthState.Disposed,
                "PLCSIM Advanced V7 runtime session was released.");
        }

        ReportProgress(
            "ReleaseRuntime",
            result.Succeeded ? "Released" : "Failed",
            result.Succeeded
                ? "PLCSIM Advanced V7 runtime release completed."
                : "PLCSIM Advanced V7 runtime release failed.",
            result.Succeeded ? 100 : null);
        return result;
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        ReportProgress("CloseRuntimeSession", "Started", "PLCSIM Advanced V7 runtime session close started.", null);
        var closed = false;
        try
        {
            await workerSession.CloseAsync(cancellationToken).ConfigureAwait(false);
            closed = true;
            ReportProgress("CloseRuntimeSession", "Completed", "PLCSIM Advanced V7 runtime session close completed.", 100);
        }
        finally
        {
            FinalizeSession(
                closed ? PlcsimRuntimeHealthState.Disposed : PlcsimRuntimeHealthState.Invalidated,
                closed
                    ? "PLCSIM Advanced V7 runtime session was closed."
                    : "PLCSIM Advanced V7 runtime session close did not complete.");
        }
    }

    private void OnWorkerRuntimeStateChanged(object? sender, PlcsimRuntimeStateChangedEventArgs args)
    {
        if (args.Change.Kind is PlcsimRuntimeStateChangeKind.RuntimeManagerLost or PlcsimRuntimeStateChangeKind.ConnectionLost)
        {
            FinalizeSession(PlcsimRuntimeHealthState.Invalidated, args.Change.Message);
        }
        else if (args.Change.Kind == PlcsimRuntimeStateChangeKind.SessionDisposed)
        {
            FinalizeSession(PlcsimRuntimeHealthState.Disposed, args.Change.Message);
        }

        RuntimeStateChanged?.Invoke(this, args);
    }

    private void SetHealth(PlcsimRuntimeHealthState health, string message)
    {
        if (Health == health)
        {
            return;
        }

        var previous = Health;
        Health = health;
        HealthChanged?.Invoke(this, new PlcsimRuntimeHealthChangedEventArgs(previous, Health, message));
    }

    private void FinalizeSession(PlcsimRuntimeHealthState health, string message)
    {
        if (Interlocked.Exchange(ref finalized, 1) != 0)
        {
            return;
        }

        workerSession.RuntimeStateChanged -= OnWorkerRuntimeStateChanged;
        unregisterSession();
        SetHealth(health, message);
    }

    private void ReportProgress(string operation, string phase, string message, int? percent)
    {
        ProgressChanged?.Invoke(this, new PlcsimRuntimeProgressSnapshot(operation, phase, message, percent));
    }

    private static bool IsSameRuntimePrepareRequest(
        PlcsimRuntimePrepareRequest? previous,
        PlcsimRuntimePrepareRequest current)
    {
        return previous is not null
            && Equals(previous.RuntimeReference, current.RuntimeReference)
            && Equals(previous.StoragePreference, current.StoragePreference)
            && previous.ProvisioningMode == current.ProvisioningMode
            && previous.CpuType == current.CpuType;
    }

}
