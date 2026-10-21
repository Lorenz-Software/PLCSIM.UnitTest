using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8;

internal sealed class PlcsimAdvancedV8CommunicationSession : IPlcsimUnitTestCommunicationSession
{
    private readonly IPlcsimAdvancedV8WorkerSessionHandle workerSession;
    private readonly SemaphoreSlim closeLock = new(1, 1);
    private int closeCompleted;
    private int safeDefaultPublished;
    private int subscriptionsDetached;
    private PlcsimEventStreamMonitoringState? lastMonitoringState;

    public PlcsimAdvancedV8CommunicationSession(
        IPlcsimAdvancedV8WorkerSessionHandle workerSession,
        PlcsimCommunicationSessionInfo communication,
        string? observedRunControlContractVersion,
        string? observedRunControlFrameworkVersion,
        string? observedEventStreamContractVersion,
        string? observedEventStreamFrameworkVersion)
    {
        this.workerSession = workerSession ?? throw new ArgumentNullException(nameof(workerSession));
        Communication = communication ?? throw new ArgumentNullException(nameof(communication));
        ObservedRunControlContractVersion = observedRunControlContractVersion;
        ObservedRunControlFrameworkVersion = observedRunControlFrameworkVersion;
        ObservedEventStreamContractVersion = observedEventStreamContractVersion;
        ObservedEventStreamFrameworkVersion = observedEventStreamFrameworkVersion;
        Health = PlcsimCommunicationHealthState.Healthy;
        this.workerSession.EventBatchReceived += OnWorkerEventBatchReceived;
        this.workerSession.EventStreamMonitoringStateChanged += OnWorkerEventStreamMonitoringStateChanged;
        this.workerSession.RunControlObservationChanged += OnWorkerRunControlObservationChanged;
        this.workerSession.CommunicationHealthChanged += OnWorkerCommunicationHealthChanged;
        this.workerSession.EventStreamMonitoringFaulted += OnWorkerEventStreamMonitoringFaulted;
    }

    public PlcsimCommunicationSessionInfo Communication { get; }

    public string? ObservedRunControlContractVersion { get; }

    public string? ObservedRunControlFrameworkVersion { get; }

    public string? ObservedEventStreamContractVersion { get; }

    public string? ObservedEventStreamFrameworkVersion { get; }

    public PlcsimCommunicationHealthState Health { get; private set; }

    public event EventHandler<PlcsimCommunicationHealthChangedEventArgs>? HealthChanged;

    public event EventHandler<PlcsimRunControlObservationChangedEventArgs>? RunControlObservationChanged;

    public event EventHandler<PlcsimEventBatchReceivedEventArgs>? EventBatchReceived;

    public event EventHandler<PlcsimEventStreamMonitoringStateChangedEventArgs>? EventStreamMonitoringStateChanged;

    public Task<PlcsimRunControlValidationResult> ValidateRunControlAsync(
        PlcsimRunControlValidationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsSessionMismatch(request.CommunicationSessionId))
        {
            return Task.FromResult(new PlcsimRunControlValidationResult(
                PlcsimOperationStatus.Rejected,
                ContractValid: false,
                ObservedContractVersion: ObservedRunControlContractVersion,
                CreateSessionMismatchDiagnostics("RunControlValidation", request.CommunicationSessionId),
                Array.Empty<PlcsimEvidence>()));
        }

        if (!IsOperational)
        {
            return Task.FromResult(new PlcsimRunControlValidationResult(
                PlcsimOperationStatus.Unavailable,
                ContractValid: false,
                ObservedContractVersion: ObservedRunControlContractVersion,
                CreateClosedDiagnostics("RunControlValidation"),
                Array.Empty<PlcsimEvidence>()));
        }

        var contractValid = string.Equals(
            ObservedRunControlContractVersion,
            request.ExpectedContractVersion,
            StringComparison.Ordinal);

        return Task.FromResult(new PlcsimRunControlValidationResult(
            contractValid ? PlcsimOperationStatus.Succeeded : PlcsimOperationStatus.Rejected,
            contractValid,
            ObservedRunControlContractVersion,
            contractValid
                ? Array.Empty<PlcsimDiagnostic>()
                : new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V8.RunControlContractMismatch",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V8 RunControl contract version does not match the expected UnitTest communication contract version.",
                        "RunControlValidation",
                        Communication.RunControlRoot)
                },
            new[]
            {
                new PlcsimEvidence(
                    "plcsim-advanced-v8-runcontrol-validation",
                    "RunControlValidation",
                    Communication.RunControlRoot),
                new PlcsimEvidence(
                    "plcsim-advanced-v8-runcontrol-contract-version",
                    "RunControlContractVersion",
                    ObservedRunControlContractVersion ?? string.Empty),
                new PlcsimEvidence(
                    "plcsim-advanced-v8-runcontrol-framework-version",
                    "RunControlFrameworkVersion",
                    ObservedRunControlFrameworkVersion ?? string.Empty)
            }));
    }

    public Task<PlcsimRunControlCommandResult> RequestRunControlCommandAsync(
        PlcsimRunControlCommandRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsSessionMismatch(request.CommunicationSessionId))
        {
            return Task.FromResult(new PlcsimRunControlCommandResult(
                PlcsimOperationStatus.Rejected,
                request.Command,
                Accepted: false,
                CreateSessionMismatchDiagnostics("RunControlCommand", request.CommunicationSessionId),
                Array.Empty<PlcsimEvidence>()));
        }

        if (!IsOperational)
        {
            return Task.FromResult(new PlcsimRunControlCommandResult(
                PlcsimOperationStatus.Unavailable,
                request.Command,
                Accepted: false,
                CreateClosedDiagnostics("RunControlCommand"),
                Array.Empty<PlcsimEvidence>()));
        }

        return workerSession.RequestRunControlCommandAsync(request, Communication, cancellationToken);
    }

    public Task<PlcsimEventBufferResetResult> RequestEventBufferResetAsync(
        PlcsimEventBufferResetRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsSessionMismatch(request.CommunicationSessionId))
        {
            return Task.FromResult(new PlcsimEventBufferResetResult(
                PlcsimOperationStatus.Rejected,
                Accepted: false,
                CreateSessionMismatchDiagnostics("EventBufferReset", request.CommunicationSessionId),
                Array.Empty<PlcsimEvidence>()));
        }

        if (!IsOperational)
        {
            return Task.FromResult(new PlcsimEventBufferResetResult(
                PlcsimOperationStatus.Unavailable,
                Accepted: false,
                CreateClosedDiagnostics("EventBufferReset"),
                Array.Empty<PlcsimEvidence>()));
        }

        return workerSession.RequestEventBufferResetAsync(request, Communication, cancellationToken);
    }

    public Task<PlcsimRunControlObservationResult> ObserveRunControlAsync(
        PlcsimRunControlObservationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsSessionMismatch(request.CommunicationSessionId))
        {
            return Task.FromResult(new PlcsimRunControlObservationResult(
                PlcsimOperationStatus.Rejected,
                null,
                CreateSessionMismatchDiagnostics("ObserveRunControl", request.CommunicationSessionId),
                Array.Empty<PlcsimEvidence>()));
        }

        if (!IsOperational)
        {
            return Task.FromResult(new PlcsimRunControlObservationResult(
                PlcsimOperationStatus.Unavailable,
                null,
                CreateClosedDiagnostics("ObserveRunControl"),
                Array.Empty<PlcsimEvidence>()));
        }

        return PublishObservationWhenSucceededAsync(
            workerSession.ObserveRunControlAsync(request, Communication, cancellationToken));
    }

    public Task<PlcsimEventBufferValidationResult> ValidateEventBufferAsync(
        PlcsimEventBufferValidationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsSessionMismatch(request.CommunicationSessionId))
        {
            return Task.FromResult(new PlcsimEventBufferValidationResult(
                PlcsimOperationStatus.Rejected,
                ContractValid: false,
                ObservedContractVersion: ObservedEventStreamContractVersion,
                CreateUnavailableEventBufferObservation(),
                CreateSessionMismatchDiagnostics("EventBufferValidation", request.CommunicationSessionId),
                Array.Empty<PlcsimEvidence>()));
        }

        if (!IsOperational)
        {
            return Task.FromResult(new PlcsimEventBufferValidationResult(
                PlcsimOperationStatus.Unavailable,
                ContractValid: false,
                ObservedContractVersion: ObservedEventStreamContractVersion,
                CreateUnavailableEventBufferObservation(),
                CreateClosedDiagnostics("EventBufferValidation"),
                Array.Empty<PlcsimEvidence>()));
        }

        var contractValid = string.Equals(
            ObservedEventStreamContractVersion,
            request.ExpectedContractVersion,
            StringComparison.Ordinal);

        return Task.FromResult(new PlcsimEventBufferValidationResult(
            contractValid ? PlcsimOperationStatus.Succeeded : PlcsimOperationStatus.Rejected,
            contractValid,
            ObservedEventStreamContractVersion,
            new PlcsimEventBufferObservation(
                contractValid,
                contractValid ? EventBufferReadState.NotObserved : EventBufferReadState.ContractInvalid,
                contractValid ? EventStreamBatchReleaseState.Idle : EventStreamBatchReleaseState.Invalidated,
                BufferObservedEmpty: false,
                LostEventCount: 0),
            contractValid
                ? Array.Empty<PlcsimDiagnostic>()
                : new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V8.EventStreamContractMismatch",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V8 EventStream contract version does not match the expected UnitTest communication contract version.",
                        "EventBufferValidation",
                        Communication.EventBufferRoot)
                },
            new[]
            {
                new PlcsimEvidence(
                    "plcsim-advanced-v8-eventbuffer-validation",
                    "EventBufferValidation",
                    Communication.EventBufferRoot),
                new PlcsimEvidence(
                    "plcsim-advanced-v8-eventstream-contract-version",
                    "EventStreamContractVersion",
                    ObservedEventStreamContractVersion ?? string.Empty),
                new PlcsimEvidence(
                    "plcsim-advanced-v8-eventstream-framework-version",
                    "EventStreamFrameworkVersion",
                    ObservedEventStreamFrameworkVersion ?? string.Empty)
            }));
    }

    public Task<PlcsimEventStreamMonitoringResult> StartEventStreamMonitoringAsync(
        PlcsimEventStreamMonitoringRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsSessionMismatch(request.CommunicationSessionId))
        {
            return Task.FromResult(CreateSessionMismatchEventStreamMonitoringResult(
                request.CommunicationSessionId,
                "StartEventStreamMonitoring"));
        }

        if (!IsOperational)
        {
            return Task.FromResult(CreateClosedEventStreamMonitoringResult(request.CommunicationSessionId, "StartEventStreamMonitoring"));
        }

        return RetainMonitoringResultAsync(() => workerSession.StartEventStreamMonitoringAsync(request, Communication, cancellationToken));
    }

    public Task<PlcsimEventStreamMonitoringResult> StopEventStreamMonitoringAsync(
        PlcsimEventStreamStopRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsSessionMismatch(request.CommunicationSessionId))
        {
            return Task.FromResult(CreateSessionMismatchEventStreamMonitoringResult(
                request.CommunicationSessionId,
                "StopEventStreamMonitoring"));
        }

        if (!IsOperational)
        {
            return Task.FromResult(CreateClosedEventStreamMonitoringResult(request.CommunicationSessionId, "StopEventStreamMonitoring"));
        }

        return RetainMonitoringResultAsync(() => workerSession.StopEventStreamMonitoringAsync(request, Communication, cancellationToken));
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        await closeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref closeCompleted) != 0)
            {
                return;
            }

            if (IsOperational)
            {
                SetHealth(PlcsimCommunicationHealthState.Degraded, "PLCSIM Advanced V8 UnitTest communication session is closing.");
            }

            try
            {
                var close = await workerSession.CloseCommunicationAsync(Communication, cancellationToken).ConfigureAwait(false);
                if (!close.Succeeded)
                {
                    var diagnostic = close.Diagnostics.FirstOrDefault();
                    throw new InvalidOperationException(diagnostic?.Message ?? "PLCSIM Advanced V8 UnitTest communication session close failed.");
                }

                FinalizeLocalSession(
                    PlcsimCommunicationHealthState.Disposed,
                    "PLCSIM Advanced V8 UnitTest communication session was closed.");
            }
            catch
            {
                FinalizeLocalSession(
                    PlcsimCommunicationHealthState.Invalidated,
                    "PLCSIM Advanced V8 UnitTest communication session close failed.");
                workerSession.Terminate(
                    "PLCSIM Advanced V8 worker was terminated after UnitTest communication close failed.");
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref closeCompleted, 1);
            }
        }
        finally
        {
            closeLock.Release();
        }
    }

    private void OnWorkerEventBatchReceived(object? sender, PlcsimEventBatchReceivedEventArgs args)
    {
        if (!IsOperational
            || !string.Equals(args.Batch.SessionId.Value, Communication.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        EventBatchReceived?.Invoke(this, args);
    }

    private void OnWorkerEventStreamMonitoringStateChanged(
        object? sender,
        PlcsimEventStreamMonitoringStateChangedEventArgs args)
    {
        if (!CanReceiveWorkerFault
            || !string.Equals(args.State.CommunicationSessionId, Communication.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        Volatile.Write(ref lastMonitoringState, args.State);
        EventStreamMonitoringStateChanged?.Invoke(this, args);
    }

    private void OnWorkerRunControlObservationChanged(object? sender, PlcsimRunControlObservationChangedEventArgs args)
    {
        if (!IsOperational)
        {
            return;
        }

        RunControlObservationChanged?.Invoke(this, args);
    }

    private void OnWorkerEventStreamMonitoringFaulted(object? sender, PlcsimEventStreamMonitoringFaultedEventArgs args)
    {
        if (!CanReceiveWorkerFault
            || !string.Equals(args.CommunicationSessionId, Communication.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        FinalizeLocalSession(
            args.Status == PlcsimOperationStatus.Unavailable
                ? PlcsimCommunicationHealthState.Invalidated
                : PlcsimCommunicationHealthState.Faulted,
            $"{args.Diagnostic.Code}: {args.Diagnostic.Message}");
    }

    private void OnWorkerCommunicationHealthChanged(
        object? sender,
        PlcsimWorkerCommunicationHealthChangedEventArgs args)
    {
        if (!string.Equals(args.CommunicationSessionId, Communication.SessionId, StringComparison.Ordinal)
            || Health is PlcsimCommunicationHealthState.Invalidated
                or PlcsimCommunicationHealthState.Faulted
                or PlcsimCommunicationHealthState.Disposed)
        {
            return;
        }

        if (args.Health == PlcsimCommunicationHealthState.RevalidationRequired
            && Health == PlcsimCommunicationHealthState.Healthy)
        {
            SetHealth(args.Health, args.Message);
            return;
        }

        if (args.Health == PlcsimCommunicationHealthState.Healthy
            && Health == PlcsimCommunicationHealthState.RevalidationRequired)
        {
            SetHealth(args.Health, args.Message);
        }
    }

    private async Task<PlcsimRunControlObservationResult> PublishObservationWhenSucceededAsync(
        Task<PlcsimRunControlObservationResult> operation)
    {
        var result = await operation.ConfigureAwait(false);
        if (IsOperational && result.Succeeded && result.Observation is not null)
        {
            RunControlObservationChanged?.Invoke(this, new PlcsimRunControlObservationChangedEventArgs(result.Observation));
        }

        return result;
    }

    private bool IsSessionMismatch(string communicationSessionId)
    {
        return !string.Equals(communicationSessionId, Communication.SessionId, StringComparison.Ordinal);
    }

    private bool IsOperational => Health == PlcsimCommunicationHealthState.Healthy;

    private bool CanReceiveWorkerFault => Health is PlcsimCommunicationHealthState.Healthy
        or PlcsimCommunicationHealthState.RevalidationRequired;

    private void FinalizeLocalSession(PlcsimCommunicationHealthState health, string message)
    {
        SetHealth(health, message);
        PublishSafeDefaultRunControlObservation();
        DetachWorkerSubscriptions();
    }

    private void DetachWorkerSubscriptions()
    {
        if (Interlocked.Exchange(ref subscriptionsDetached, 1) != 0)
        {
            return;
        }

        workerSession.EventBatchReceived -= OnWorkerEventBatchReceived;
        workerSession.EventStreamMonitoringStateChanged -= OnWorkerEventStreamMonitoringStateChanged;
        workerSession.RunControlObservationChanged -= OnWorkerRunControlObservationChanged;
        workerSession.CommunicationHealthChanged -= OnWorkerCommunicationHealthChanged;
        workerSession.EventStreamMonitoringFaulted -= OnWorkerEventStreamMonitoringFaulted;
    }

    private void PublishSafeDefaultRunControlObservation()
    {
        if (Interlocked.Exchange(ref safeDefaultPublished, 1) != 0)
        {
            return;
        }

        RunControlObservationChanged?.Invoke(
            this,
            new PlcsimRunControlObservationChangedEventArgs(CreateSafeDefaultObservation()));
    }

    private static PlcsimRunControlObservation CreateSafeDefaultObservation()
    {
        return new PlcsimRunControlObservation(
            UnitTestExecutionStatus.Unknown,
            UnitTestExecutionSignals.None,
            UnitTestLifecycleState.Unknown,
            UnitTestResultState.NotEvaluated);
    }

    private async Task<PlcsimEventStreamMonitoringResult> RetainMonitoringResultAsync(
        Func<Task<PlcsimEventStreamMonitoringResult>> operation)
    {
        var previous = Volatile.Read(ref lastMonitoringState);
        var result = await operation().ConfigureAwait(false);
        if (string.Equals(result.State.CommunicationSessionId, Communication.SessionId, StringComparison.Ordinal))
        {
            Interlocked.CompareExchange(ref lastMonitoringState, result.State, previous);
        }

        return result;
    }

    private PlcsimEventBufferObservation CreateUnavailableEventBufferObservation()
    {
        var previous = Volatile.Read(ref lastMonitoringState);
        return new PlcsimEventBufferObservation(
            ContractValid: false,
            EventBufferReadState.SessionInvalid,
            previous?.BatchReleaseState ?? EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: previous?.BufferObservedEmpty ?? false,
            LostEventCount: previous?.LostEventCount ?? 0);
    }

    private PlcsimEventStreamMonitoringResult CreateClosedEventStreamMonitoringResult(
        string communicationSessionId,
        string phase)
    {
        var previous = Volatile.Read(ref lastMonitoringState)
            ?? new PlcsimEventStreamMonitoringState(communicationSessionId, false,
                EventBufferReadState.NotObserved, EventStreamBatchReleaseState.Idle, false, 0);
        return new PlcsimEventStreamMonitoringResult(
            PlcsimOperationStatus.Unavailable,
            previous with { IsMonitoring = false, LastReadState = EventBufferReadState.SessionInvalid },
            CreateClosedDiagnostics(phase),
            Array.Empty<PlcsimEvidence>());
    }

    private static PlcsimEventStreamMonitoringResult CreateSessionMismatchEventStreamMonitoringResult(
        string communicationSessionId,
        string phase)
    {
        return new PlcsimEventStreamMonitoringResult(
            PlcsimOperationStatus.Rejected,
            new PlcsimEventStreamMonitoringState(
                communicationSessionId,
                IsMonitoring: false,
                EventBufferReadState.SessionInvalid,
                EventStreamBatchReleaseState.Idle,
                BufferObservedEmpty: false,
                LostEventCount: 0),
            CreateSessionMismatchDiagnostics(phase, communicationSessionId),
            Array.Empty<PlcsimEvidence>());
    }

    private static IReadOnlyList<PlcsimDiagnostic> CreateSessionMismatchDiagnostics(
        string phase,
        string communicationSessionId)
    {
        return new[]
        {
            new PlcsimDiagnostic(
                "PlcsimAdvanced.V8.CommunicationSessionMismatch",
                PlcsimDiagnosticSeverity.Error,
                "The PLCSIM Advanced V8 communication session id does not match the prepared session.",
                phase,
                communicationSessionId)
        };
    }

    private static IReadOnlyList<PlcsimDiagnostic> CreateClosedDiagnostics(string phase)
    {
        return new[]
        {
            new PlcsimDiagnostic(
                "PlcsimAdvanced.V8.CommunicationSessionClosed",
                PlcsimDiagnosticSeverity.Warning,
                "PLCSIM Advanced V8 UnitTest communication session is already closed.",
                phase,
                "plcsim-advanced-v8-communication-session")
        };
    }

    private void SetHealth(PlcsimCommunicationHealthState health, string message)
    {
        if (Health == health)
        {
            return;
        }

        var previous = Health;
        Health = health;
        HealthChanged?.Invoke(this, new PlcsimCommunicationHealthChangedEventArgs(previous, Health, message));
    }
}
