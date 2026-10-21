using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

internal sealed class SiemensPlcsimUnitTestEventStreamService : SiemensAccessProfileServiceBase, IUnitTestEventStreamService
{
    private static readonly TimeSpan DefaultEventBufferResetCommandTimeout = TimeSpan.FromSeconds(3);
    private readonly ILogger<SiemensPlcsimUnitTestEventStreamService> logger;
    private readonly SiemensPlcsimCommunicationSessionOwner sessionOwner;
    private IPlcsimUnitTestCommunicationSession? session;
    private bool eventBufferRecoveryActive;
    private bool eventBufferRecoveryReported;

    public SiemensPlcsimUnitTestEventStreamService(
        TimeProvider timeProvider,
        ILogger<SiemensPlcsimUnitTestEventStreamService>? logger = null)
        : this(timeProvider, logger, new SiemensPlcsimCommunicationSessionOwner())
    {
    }

    internal SiemensPlcsimUnitTestEventStreamService(
        TimeProvider timeProvider,
        ILogger<SiemensPlcsimUnitTestEventStreamService>? logger,
        SiemensPlcsimCommunicationSessionOwner sessionOwner)
        : base(timeProvider)
    {
        this.logger = logger ?? NullLogger<SiemensPlcsimUnitTestEventStreamService>.Instance;
        this.sessionOwner = sessionOwner ?? throw new ArgumentNullException(nameof(sessionOwner));
    }

    public event EventHandler<EventBatchReceivedEventArgs>? EventBatchReceivedEventHandler;

    public event EventHandler<EventStreamStateChangedEventArgs>? MonitoringStateChangedEventHandler;

    public EventStreamState CurrentMonitoringState { get; private set; } = new(
        new CommunicationSessionId("not-started"),
        IsMonitoring: false,
        EventBufferReadState.NotObserved,
        EventStreamBatchReleaseState.Idle,
        BufferObservedEmpty: false,
        LostEventCount: 0);

    public void BindCommunicationSession(IPlcsimUnitTestCommunicationSession communicationSession)
    {
        ArgumentNullException.ThrowIfNull(communicationSession);
        if (session is not null && !ReferenceEquals(session, communicationSession))
        {
            throw new InvalidOperationException(
                "A different Siemens PLCSIM UnitTest communication session is already bound to the event-stream adapter.");
        }

        if (ReferenceEquals(session, communicationSession))
        {
            return;
        }

        sessionOwner.Attach(SiemensPlcsimCommunicationAdapterKind.EventStream, communicationSession);
        eventBufferRecoveryActive = false;
        eventBufferRecoveryReported = false;
        session = communicationSession;
        session.HealthChanged += OnSessionHealthChanged;
        session.EventBatchReceived += OnEventBatchReceived;
        session.EventStreamMonitoringStateChanged += OnEventStreamMonitoringStateChanged;
        SetHealth(MapHealth(session.Health, "Siemens PLCSIM UnitTest event-stream communication session bound."));
        logger.LogInformation(
            "Siemens PLCSIM UnitTest event-stream communication session bound. RuntimeSessionId={RuntimeSessionId}, CommunicationSessionId={CommunicationSessionId}, EventBufferRoot={EventBufferRoot}",
            session.Communication.RuntimeSessionId,
            session.Communication.SessionId,
            session.Communication.EventBufferRoot);
    }

    public async Task<OperationResult<EventStreamState>> StartMonitoringAsync(
        UnitTestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activeSession = session;
        if (activeSession is null || !sessionOwner.IsUsable(activeSession))
        {
            logger.LogWarning("Siemens PLCSIM event-stream monitoring start failed because UnitTest communication is not usable.");
            return OperationResult<EventStreamState>.Failure("Siemens PLCSIM UnitTest communication is not usable.");
        }

        logger.LogInformation(
            "Siemens PLCSIM event-stream monitoring start requested. CommunicationSessionId={CommunicationSessionId}",
            activeSession.Communication.SessionId);
        var validation = await activeSession.ValidateEventBufferAsync(
            new PlcsimEventBufferValidationRequest(
                activeSession.Communication.SessionId,
                UnitTestContractVersions.EventStreamModel,
                CorrelationId: null),
            cancellationToken).ConfigureAwait(false);
        if (!sessionOwner.IsUsable(activeSession))
        {
            return OperationResult<EventStreamState>.Failure(
                "Siemens PLCSIM UnitTest communication became terminal during EventBuffer validation.");
        }

        PublishDiagnostics(validation.Diagnostics, DiagnosticPhase.CommunicationValidation);
        ApplyMonitoringState(MapValidation(validation));
        if (!validation.Succeeded || !validation.ContractValid)
        {
            LogPlcsimDiagnostics("EventBufferValidation", validation.Status, validation.Diagnostics);
            ApplyOperationHealth(
                validation.Status,
                "Siemens.PlcsimAdvanced.EventStream.ValidationFailed",
                $"Siemens PLCSIM EventBuffer validation returned {validation.Status}.");
            LogOperationCompleted(
                "EventBufferValidation",
                validation.Status,
                $"Siemens PLCSIM EventBuffer validation completed. ContractValid={validation.ContractValid}, ReadState={CurrentMonitoringState.LastReadState}, BatchReleaseState={CurrentMonitoringState.BatchReleaseState}");
            return SiemensPlcsimOperationMapping.ToOperationResult<EventStreamState>(
                validation.Status,
                CurrentMonitoringState,
                $"Siemens PLCSIM EventBuffer validation returned {validation.Status}.");
        }

        var result = await activeSession.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(activeSession.Communication.SessionId, CorrelationId: null),
            cancellationToken).ConfigureAwait(false);
        if (!sessionOwner.IsUsable(activeSession))
        {
            return OperationResult<EventStreamState>.Failure(
                "Siemens PLCSIM UnitTest communication became terminal while starting event-stream monitoring.");
        }

        PublishDiagnostics(result.Diagnostics, DiagnosticPhase.ExecutionMonitoring);
        var state = ApplyMonitoringState(MapMonitoringState(result.State));
        if (!result.Succeeded)
        {
            LogPlcsimDiagnostics("EventStreamMonitoringStart", result.Status, result.Diagnostics);
            ApplyOperationHealth(
                result.Status,
                "Siemens.PlcsimAdvanced.EventStream.MonitoringStartFailed",
                $"Siemens PLCSIM event-stream monitoring start returned {result.Status}.");
        }

        LogOperationCompleted(
            "EventStreamMonitoringStart",
            result.Status,
            $"Siemens PLCSIM event-stream monitoring start completed. IsMonitoring={state.IsMonitoring}, ReadState={state.LastReadState}, BatchReleaseState={state.BatchReleaseState}, LostEventCount={state.LostEventCount}, DiagnosticCount={result.Diagnostics.Count}");
        return SiemensPlcsimOperationMapping.ToOperationResult(result.Status, state, $"Siemens PLCSIM event-stream monitoring start returned {result.Status}.");
    }

    public async Task<OperationResult<EventBufferResetCommandResult>> ResetEventBufferAsync(
        UnitTestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activeSession = session;
        if (activeSession is null || !sessionOwner.IsUsable(activeSession))
        {
            logger.LogWarning("Siemens PLCSIM EventBuffer reset failed because UnitTest communication is not usable.");
            return OperationResult<EventBufferResetCommandResult>.Failure(
                "Siemens PLCSIM UnitTest communication is not usable.");
        }

        logger.LogInformation(
            "Siemens PLCSIM EventBuffer reset write started. CommunicationSessionId={CommunicationSessionId}",
            activeSession.Communication.SessionId);
        var result = await activeSession.RequestEventBufferResetAsync(
            new PlcsimEventBufferResetRequest(
                activeSession.Communication.SessionId,
                DefaultEventBufferResetCommandTimeout,
                CorrelationId: null),
            cancellationToken).ConfigureAwait(false);
        if (!sessionOwner.IsUsable(activeSession))
        {
            return OperationResult<EventBufferResetCommandResult>.Failure(
                "Siemens PLCSIM UnitTest communication became terminal while writing the EventBuffer reset command.");
        }

        PublishDiagnostics(result.Diagnostics, DiagnosticPhase.ExecutionMonitoring);
        var commandResult = new EventBufferResetCommandResult(result.Accepted);
        var message = $"Siemens PLCSIM EventBuffer reset write returned {result.Status}.";
        if (IsResetNotSupported(result.Diagnostics))
        {
            LogPlcsimDiagnostics("EventBufferReset", result.Status, result.Diagnostics);
            LogOperationCompleted(
                "EventBufferReset",
                result.Status,
                $"Siemens PLCSIM EventBuffer reset is not supported. Accepted={result.Accepted}, DiagnosticCount={result.Diagnostics.Count}");
            return OperationResult<EventBufferResetCommandResult>.NotSupported(commandResult, message);
        }

        if (!result.Succeeded || !result.Accepted)
        {
            LogPlcsimDiagnostics("EventBufferReset", result.Status, result.Diagnostics);
            ApplyOperationHealth(
                result.Status,
                "Siemens.PlcsimAdvanced.EventStream.ResetFailed",
                message);
        }

        LogOperationCompleted(
            "EventBufferReset",
            result.Status,
            $"Siemens PLCSIM EventBuffer reset write completed. Accepted={result.Accepted}, DiagnosticCount={result.Diagnostics.Count}");
        return SiemensPlcsimOperationMapping.ToOperationResult(result.Status, commandResult, message);
    }

    public async Task<OperationResult> StopMonitoringAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activeSession = session;
        if (activeSession is null)
        {
            logger.LogInformation("Siemens PLCSIM event-stream monitoring stop ignored because UnitTest communication is not prepared.");
            return OperationResult.Success("Siemens PLCSIM UnitTest event-stream monitoring is not prepared.");
        }

        if (!sessionOwner.IsUsable(activeSession))
        {
            ApplyMonitoringState(CreateTerminalMonitoringState());
            logger.LogInformation("Siemens PLCSIM event-stream monitoring stop skipped because UnitTest communication is terminal.");
            return OperationResult.Success("Siemens PLCSIM UnitTest communication is already terminal.");
        }

        logger.LogInformation(
            "Siemens PLCSIM event-stream monitoring stop requested. CommunicationSessionId={CommunicationSessionId}",
            activeSession.Communication.SessionId);
        var result = await activeSession.StopEventStreamMonitoringAsync(
            new PlcsimEventStreamStopRequest(activeSession.Communication.SessionId, CorrelationId: null),
            cancellationToken).ConfigureAwait(false);
        if (!sessionOwner.IsUsable(activeSession))
        {
            ApplyMonitoringState(CreateTerminalMonitoringState());
            return OperationResult.Success("Siemens PLCSIM UnitTest communication became terminal while stopping event-stream monitoring.");
        }

        PublishDiagnostics(result.Diagnostics, DiagnosticPhase.ExecutionMonitoring);
        ApplyMonitoringState(MapMonitoringState(result.State));
        if (!result.Succeeded)
        {
            LogPlcsimDiagnostics("EventStreamMonitoringStop", result.Status, result.Diagnostics);
            ApplyOperationHealth(
                result.Status,
                "Siemens.PlcsimAdvanced.EventStream.MonitoringStopFailed",
                $"Siemens PLCSIM event-stream monitoring stop returned {result.Status}.");
        }

        LogOperationCompleted(
            "EventStreamMonitoringStop",
            result.Status,
            $"Siemens PLCSIM event-stream monitoring stop completed. IsMonitoring={CurrentMonitoringState.IsMonitoring}, ReadState={CurrentMonitoringState.LastReadState}, BatchReleaseState={CurrentMonitoringState.BatchReleaseState}, LostEventCount={CurrentMonitoringState.LostEventCount}, DiagnosticCount={result.Diagnostics.Count}");
        return SiemensPlcsimOperationMapping.ToOperationResult(result.Status, $"Siemens PLCSIM event-stream monitoring stop returned {result.Status}.");
    }

    public async Task<OperationResult> ReleaseCommunicationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Siemens PLCSIM UnitTest event-stream communication release started.");
        OperationResult stop = OperationResult.Success();
        if (session is not null && CurrentMonitoringState.IsMonitoring)
        {
            stop = await StopMonitoringAsync(cancellationToken).ConfigureAwait(false);
        }

        var close = await DetachSessionAsync(cancellationToken).ConfigureAwait(false);
        ApplyMonitoringState(new EventStreamState(
            new CommunicationSessionId("not-started"),
            IsMonitoring: false,
            EventBufferReadState.NotObserved,
            EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: false,
            LostEventCount: 0));
        if (close.IsSuccessful)
        {
            SetHealth(AccessHealth.NotEstablished(TimeProvider.GetUtcNow()));
        }
        LogOperationCompleted(
            "ReleaseCommunication",
            close.State,
            "Siemens PLCSIM UnitTest event-stream communication release completed.");
        return stop.IsUnsuccessful
            ? stop
            : close.IsSuccessful
            ? OperationResult.Success("Siemens PLCSIM UnitTest event-stream communication released.")
            : close;
    }

    public async Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Siemens PLCSIM UnitTest event-stream service cleanup started.");
        var close = await DetachSessionAsync(cancellationToken).ConfigureAwait(false);
        ApplyMonitoringState(new EventStreamState(
            new CommunicationSessionId("not-started"),
            IsMonitoring: false,
            EventBufferReadState.NotObserved,
            EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: false,
            LostEventCount: 0));
        SetDisposed("Siemens PLCSIM UnitTest event-stream service disposed.");
        LogOperationCompleted(
            "Cleanup",
            close.State,
            "Siemens PLCSIM UnitTest event-stream service cleanup completed.");
        return close.IsSuccessful
            ? OperationResult.Success("Siemens PLCSIM UnitTest event-stream service cleaned up.")
            : close;
    }

    protected override AccessServiceKind GetServiceKind() => AccessServiceKind.UnitTestEventStream;

    private EventStreamState ApplyMonitoringState(EventStreamState state)
    {
        var previousState = CurrentMonitoringState;
        CurrentMonitoringState = state;
        if (previousState != state)
        {
            PublishMonitoringStateDiagnostics(previousState, state);
            MonitoringStateChangedEventHandler?.Invoke(
                this,
                new EventStreamStateChangedEventArgs(previousState, state));
        }

        return state;
    }

    private EventStreamState MapValidation(PlcsimEventBufferValidationResult result)
    {
        if (session is not null
            && string.Equals(CurrentMonitoringState.SessionId.Value, session.Communication.SessionId, StringComparison.Ordinal)
            && result.Observation.ReadState is EventBufferReadState.NotObserved or EventBufferReadState.ContractInvalid)
        {
            return CurrentMonitoringState with
            {
                IsMonitoring = false,
                LastReadState = result.Observation.ContractValid
                    ? CurrentMonitoringState.LastReadState
                    : EventBufferReadState.ContractInvalid
            };
        }

        return new EventStreamState(
            new CommunicationSessionId(result.Observation.ContractValid && session is not null ? session.Communication.SessionId : CurrentMonitoringState.SessionId.Value),
            IsMonitoring: false,
            result.Observation.ReadState,
            result.Observation.BatchReleaseState,
            result.Observation.BufferObservedEmpty,
            result.Observation.LostEventCount);
    }

    private static EventStreamState MapMonitoringState(PlcsimEventStreamMonitoringState state)
    {
        return new EventStreamState(
            new CommunicationSessionId(state.CommunicationSessionId),
            state.IsMonitoring,
            state.LastReadState,
            state.BatchReleaseState,
            state.BufferObservedEmpty,
            state.LostEventCount,
            state.BufferFull,
            state.ProducerSequenceId,
            state.DeletedSequenceId);
    }

    private async Task<OperationResult> DetachSessionAsync(CancellationToken cancellationToken)
    {
        var detachedSession = session;
        if (detachedSession is null)
        {
            return OperationResult.Success();
        }

        UnwireSession(detachedSession);
        if (ReferenceEquals(session, detachedSession))
        {
            session = null;
        }

        eventBufferRecoveryActive = false;
        eventBufferRecoveryReported = false;

        var close = await sessionOwner.DetachAsync(
            SiemensPlcsimCommunicationAdapterKind.EventStream,
            detachedSession,
            cancellationToken).ConfigureAwait(false);
        if (close.IsUnsuccessful)
        {
            var message = close.Message ?? "Siemens PLCSIM UnitTest event-stream communication close failed.";
            ReportDiagnostic(CreateServiceDiagnostic(
                "Siemens.PlcsimAdvanced.EventStream.CommunicationCloseFailed",
                LogLevel.Error,
                DiagnosticPhase.Cleanup,
                message));
            ApplyOperationHealth(
                PlcsimOperationStatus.Failed,
                "Siemens.PlcsimAdvanced.EventStream.CommunicationCloseFailed",
                message);
        }

        return close;
    }

    private void UnwireSession(IPlcsimUnitTestCommunicationSession communicationSession)
    {
        communicationSession.HealthChanged -= OnSessionHealthChanged;
        communicationSession.EventBatchReceived -= OnEventBatchReceived;
        communicationSession.EventStreamMonitoringStateChanged -= OnEventStreamMonitoringStateChanged;
    }

    private void OnEventBatchReceived(object? sender, PlcsimEventBatchReceivedEventArgs args)
    {
        if (sender is not IPlcsimUnitTestCommunicationSession communicationSession
            || !ReferenceEquals(communicationSession, session)
            || !sessionOwner.IsUsable(communicationSession)
            || !string.Equals(args.Batch.SessionId.Value, CurrentMonitoringState.SessionId.Value, StringComparison.Ordinal))
        {
            return;
        }

        var batch = args.Batch;
        ApplyMonitoringState(CurrentMonitoringState with
        {
            LastReadState = batch.ReadState,
            BufferObservedEmpty = batch.BufferObservedEmpty,
            LostEventCount = batch.LostEventCount,
            ProducerSequenceId = batch.ProducerSequenceId,
            DeletedSequenceId = batch.DeletedSequenceId
        });
        logger.LogDebug(
            "Siemens PLCSIM event-stream batch received. CommunicationSessionId={CommunicationSessionId}, EventCount={EventCount}, ProducerSequenceId={ProducerSequenceId}, DeletedSequenceId={DeletedSequenceId}, ReadState={ReadState}, LostEventCount={LostEventCount}",
            batch.SessionId.Value,
            batch.Events.Count,
            batch.ProducerSequenceId,
            batch.DeletedSequenceId,
            batch.ReadState,
            batch.LostEventCount);
        EventBatchReceivedEventHandler?.Invoke(this, new EventBatchReceivedEventArgs(batch));
    }

    private void OnEventStreamMonitoringStateChanged(
        object? sender,
        PlcsimEventStreamMonitoringStateChangedEventArgs args)
    {
        if (sender is not IPlcsimUnitTestCommunicationSession communicationSession
            || !ReferenceEquals(communicationSession, session)
            || !sessionOwner.IsUsable(communicationSession)
            || !string.Equals(
                args.State.CommunicationSessionId,
                communicationSession.Communication.SessionId,
                StringComparison.Ordinal))
        {
            return;
        }

        ApplyMonitoringState(MapMonitoringState(args.State));
    }

    private void PublishMonitoringStateDiagnostics(
        EventStreamState previous,
        EventStreamState current)
    {
        if (!previous.BufferFull && current.BufferFull)
        {
            ReportMonitoringDiagnostic(
                "Siemens.Plcsim.EventStream.BufferFull",
                LogLevel.Warning,
                $"PLCSIM Advanced UnitTest EventBuffer became full at producer sequence {current.ProducerSequenceId}.");
        }

        if (current.LostEventCount > previous.LostEventCount)
        {
            ReportMonitoringDiagnostic(
                "Siemens.Plcsim.EventStream.EventsLost",
                LogLevel.Error,
                $"PLCSIM Advanced UnitTest EventBuffer lost-event count increased from {previous.LostEventCount} to {current.LostEventCount}.");
        }

        if (current.BufferFull || current.LostEventCount > 0)
        {
            eventBufferRecoveryActive = true;
        }

        if (eventBufferRecoveryActive
            && !eventBufferRecoveryReported
            && current.IsMonitoring
            && current.LastReadState == EventBufferReadState.Valid
            && current.BatchReleaseState == EventStreamBatchReleaseState.Completed)
        {
            eventBufferRecoveryReported = true;
            ReportMonitoringDiagnostic(
                "Siemens.Plcsim.EventStream.RecoveryStarted",
                LogLevel.Warning,
                $"PLCSIM Advanced UnitTest EventBuffer recovery started at producer sequence {current.ProducerSequenceId}.");
        }

        if (eventBufferRecoveryActive && IsRecoveredMonitoringState(current))
        {
            eventBufferRecoveryActive = false;
            eventBufferRecoveryReported = false;
            ReportMonitoringDiagnostic(
                "Siemens.Plcsim.EventStream.Recovered",
                LogLevel.Information,
                "PLCSIM Advanced UnitTest EventBuffer recovered and monitoring is active.");
        }
    }

    private static bool IsRecoveredMonitoringState(EventStreamState state)
    {
        return state.IsMonitoring
            && !state.BufferFull
            && state.LostEventCount == 0
            && state.LastReadState is EventBufferReadState.Valid or EventBufferReadState.Empty
            && state.BatchReleaseState is EventStreamBatchReleaseState.Completed or EventStreamBatchReleaseState.Idle;
    }

    private void ReportMonitoringDiagnostic(string code, LogLevel level, string message)
    {
        logger.Log(
            level,
            "Siemens PLCSIM EventBuffer state transition. Code={Code}, Message={Message}",
            code,
            message);
        ReportDiagnostic(CreateServiceDiagnostic(
            code,
            level,
            DiagnosticPhase.ExecutionMonitoring,
            message));
    }

    private void OnSessionHealthChanged(object? sender, PlcsimCommunicationHealthChangedEventArgs args)
    {
        if (sender is not IPlcsimUnitTestCommunicationSession communicationSession
            || !ReferenceEquals(communicationSession, session))
        {
            return;
        }

        if (IsTerminal(args.Current))
        {
            sessionOwner.MarkTerminal(communicationSession);
            ApplyMonitoringState(CreateTerminalMonitoringState());
        }
        else if (!sessionOwner.IsUsable(communicationSession))
        {
            return;
        }

        var health = MapHealth(args.Current, args.Message);
        SetHealth(health);
        logger.Log(
            GetLogLevel(health.State),
            "Siemens PLCSIM event-stream communication health changed. Previous={Previous}, Current={Current}, HealthState={HealthState}, Problem={Problem}, ReasonCode={ReasonCode}, Message={Message}",
            args.Previous,
            args.Current,
            health.State,
            health.Problem,
            health.ReasonCode,
            health.Message);
        if (health.State is AccessHealthState.Invalidated or AccessHealthState.RevalidationRequired)
        {
            ReportDiagnostic(CreateServiceDiagnostic(
                health.ReasonCode ?? "Siemens.Plcsim.EventStream.HealthChanged",
                health.State == AccessHealthState.Invalidated ? LogLevel.Error : LogLevel.Warning,
                DiagnosticPhase.ExecutionMonitoring,
                health.Message ?? args.Message));
        }
    }

    private EventStreamState CreateTerminalMonitoringState()
    {
        return CurrentMonitoringState with
        {
            IsMonitoring = false,
            LastReadState = EventBufferReadState.SessionInvalid
        };
    }

    private static bool IsTerminal(PlcsimCommunicationHealthState state)
    {
        return state is PlcsimCommunicationHealthState.Invalidated
            or PlcsimCommunicationHealthState.Faulted
            or PlcsimCommunicationHealthState.Disposed;
    }

    private AccessHealth MapHealth(
        PlcsimCommunicationHealthState state,
        string message)
    {
        var now = TimeProvider.GetUtcNow();
        return state switch
        {
            PlcsimCommunicationHealthState.Healthy => AccessHealth.Healthy(now),
            PlcsimCommunicationHealthState.Degraded => new AccessHealth(
                AccessHealthState.RevalidationRequired,
                AccessHealthProblem.RevalidationRequired,
                "Siemens.Plcsim.EventStream.Degraded",
                message,
                now),
            PlcsimCommunicationHealthState.RevalidationRequired => new AccessHealth(
                AccessHealthState.RevalidationRequired,
                AccessHealthProblem.RevalidationRequired,
                "Siemens.Plcsim.EventStream.RevalidationRequired",
                message,
                now),
            PlcsimCommunicationHealthState.Invalidated => new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.ContextLost,
                "Siemens.Plcsim.EventStream.Invalidated",
                message,
                now),
            PlcsimCommunicationHealthState.Faulted => new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.InternalFailure,
                "Siemens.Plcsim.EventStream.Faulted",
                message,
                now),
            PlcsimCommunicationHealthState.Disposed => AccessHealth.Disposed(now),
            _ => AccessHealth.NotEstablished(now)
        };
    }

    private IReadOnlyList<Diagnostic> MapDiagnostics(
        IReadOnlyList<PlcsimDiagnostic> diagnostics,
        DiagnosticPhase phase)
    {
        return diagnostics.Select(diagnostic => MapDiagnostic(diagnostic, phase)).ToArray();
    }

    private static bool IsResetNotSupported(IReadOnlyList<PlcsimDiagnostic> diagnostics)
    {
        return diagnostics.Any(diagnostic =>
            diagnostic.Code.EndsWith(".NotSupported", StringComparison.Ordinal));
    }

    private void PublishDiagnostics(
        IReadOnlyList<PlcsimDiagnostic> diagnostics,
        DiagnosticPhase phase)
    {
        foreach (var diagnostic in MapDiagnostics(diagnostics, phase))
        {
            ReportDiagnostic(diagnostic);
        }
    }

    private Diagnostic MapDiagnostic(
        PlcsimDiagnostic diagnostic,
        DiagnosticPhase phase)
    {
        return SiemensPlcsimOperationMapping.ToDiagnostic(
            diagnostic,
            phase,
            TimeProvider.GetUtcNow());
    }

    private Diagnostic CreateServiceDiagnostic(
        string code,
        LogLevel level,
        DiagnosticPhase phase,
        string message)
    {
        return new Diagnostic(
            code,
            DiagnosticCategory.Environment,
            level,
            DiagnosticSource.AccessProfile,
            phase,
            message,
            new DiagnosticCorrelation(ServiceId: "Siemens.PlcsimAdvanced"),
            TimeProvider.GetUtcNow());
    }

    private void ApplyOperationHealth(
        PlcsimOperationStatus status,
        string code,
        string message)
    {
        if (status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings)
        {
            return;
        }

        var now = TimeProvider.GetUtcNow();
        var health = status switch
        {
            PlcsimOperationStatus.Unavailable => new AccessHealth(
                AccessHealthState.Invalidated,
                AccessHealthProblem.ContextLost,
                code,
                message,
                now),
            PlcsimOperationStatus.Failed
                or PlcsimOperationStatus.TimedOut
                or PlcsimOperationStatus.Unclear => new AccessHealth(
                    AccessHealthState.RevalidationRequired,
                    AccessHealthProblem.RevalidationRequired,
                    code,
                    message,
                    now),
            _ => Health
        };

        SetHealth(health);
    }

    private void LogOperationCompleted(
        string operation,
        OperationResultState state,
        string message)
    {
        logger.Log(
            SiemensPlcsimOperationMapping.ToLogLevel(state),
            "{Message} Operation={Operation}, State={State}",
            message,
            operation,
            state);
    }

    private void LogOperationCompleted(
        string operation,
        PlcsimOperationStatus status,
        string message)
    {
        logger.Log(
            SiemensPlcsimOperationMapping.ToLogLevel(status),
            "{Message} Operation={Operation}, Status={Status}",
            message,
            operation,
            status);
    }

    private void LogPlcsimDiagnostics(
        string operation,
        PlcsimOperationStatus status,
        IReadOnlyList<PlcsimDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            logger.Log(
                SiemensPlcsimOperationMapping.ToLogLevel(diagnostic.Severity),
                "Siemens PLCSIM UnitTest event-stream communication diagnostic. Operation={Operation}, Status={Status}, Code={Code}, Severity={Severity}, EvidenceId={EvidenceId}, Message={DiagnosticMessage}",
                operation,
                status,
                diagnostic.Code,
                diagnostic.Severity,
                diagnostic.EvidenceId,
                diagnostic.Message);
        }
    }

    private static LogLevel GetLogLevel(AccessHealthState state)
    {
        return state switch
        {
            AccessHealthState.Healthy => LogLevel.Information,
            AccessHealthState.NotEstablished => LogLevel.Debug,
            AccessHealthState.Disposed => LogLevel.Debug,
            AccessHealthState.RevalidationRequired => LogLevel.Warning,
            _ => LogLevel.Error
        };
    }

}
