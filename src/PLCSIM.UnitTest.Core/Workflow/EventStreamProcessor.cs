using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Analysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace PLCSIM.UnitTest.Core.Workflow;

/// <summary>
/// Accepts UnitTest event-stream batches for the active communication session.
/// </summary>
public sealed class EventStreamProcessor
{
    private readonly object stateSync = new();
    private readonly Queue<Action> pendingNotifications = new();
    private bool notificationDrainActive;
    private bool acceptedEventsChanged;
    private PublishedState publishedState = new(null, Array.Empty<UnitTestEventEntry>());
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EventStreamProcessor> logger;
    private readonly IUnitTestActivityTraceSink? activityTraceSink;
    private readonly List<UnitTestEventEntry> acceptedEvents = [];
    private ActiveRunEpochState? currentRun;
    private EventStreamObservationContext? observationContext;
    private uint? highestAcceptedSequenceId;
    private bool runControlCompletionAnchorObserved;
    private bool bufferObservedEmptyForCurrentEpoch;
    private RunEpochTraceState? lastTracedRunEpoch;

    public EventStreamProcessor(
        CoreServiceContext coreContext)
        : this(coreContext, activityTraceSink: null)
    {
    }

    public EventStreamProcessor(
        CoreServiceContext coreContext,
        IUnitTestActivityTraceSink? activityTraceSink)
        : this(
            coreContext?.TimeProvider ?? throw new ArgumentNullException(nameof(coreContext)),
            coreContext.LoggerFactory.CreateLogger<EventStreamProcessor>(),
            activityTraceSink)
    {
    }

    public EventStreamProcessor(
        TimeProvider timeProvider,
        ILogger<EventStreamProcessor>? logger = null,
        IUnitTestActivityTraceSink? activityTraceSink = null)
    {
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<EventStreamProcessor>.Instance;
        this.activityTraceSink = activityTraceSink;
    }

    public IReadOnlyList<UnitTestEventEntry> AcceptedEvents => Volatile.Read(ref publishedState).AcceptedEvents;

    public UnitTestRunEpochSnapshot? Current => Volatile.Read(ref publishedState).CurrentRun;

    public event EventHandler<EventStreamProcessorUpdateEventArgs>? ProcessorUpdateEventHandler;

    public OperationResult StartObservation(EventStreamObservationContext context)
        => ApplyStateChange(() => StartObservationCore(context));

    private OperationResult StartObservationCore(EventStreamObservationContext context)
    {
        observationContext = context;
        ClearAcceptedEvents();
        highestAcceptedSequenceId = context.SequenceBaseline;
        QueueLog(LogLevel.Information,
            "Event-stream observation started. SessionId={SessionId}, SequenceBaseline={SequenceBaseline}",
            context.SessionId.Value,
            context.SequenceBaseline);
        PublishUpdate(
            EventStreamProcessorUpdateKind.ObservationStarted,
            EventProcessingState.Ignored,
            Array.Empty<UnitTestEventRef>(),
            Array.Empty<Diagnostic>());
        return OperationResult.Success("Event-stream observation started.");
    }

    public EventProcessingResult ProcessBatch(UnitTestEventBatch batch)
        => ApplyStateChange(() => ProcessBatchCore(batch));

    private EventProcessingResult ProcessBatchCore(UnitTestEventBatch batch)
    {
        if (observationContext is null)
        {
            var diagnostic = CreateDiagnostic(
                "Workflow.EventStreamProcessor.NotObserving",
                LogLevel.Warning,
                "Event batch was ignored because event-stream observation is not active.");
            QueueLog(LogLevel.Warning,
                "Event-stream batch ignored because observation is not active. SessionId={SessionId}, EventCount={EventCount}",
                batch.SessionId.Value,
                batch.Events.Count);
            TraceBatchEventsOutsideEpoch(batch);
            return EventProcessingResult.Ignored([diagnostic]);
        }

        if (batch.SessionId != observationContext.SessionId)
        {
            var diagnostic = CreateDiagnostic(
                "Workflow.EventStreamProcessor.SessionMismatch",
                LogLevel.Error,
                "Event batch session does not match the active UnitTest communication session.");
            QueueLog(LogLevel.Warning,
                "Event-stream batch ignored because session does not match. ActiveSessionId={ActiveSessionId}, BatchSessionId={BatchSessionId}",
                observationContext.SessionId.Value,
                batch.SessionId.Value);
            TraceBatchEventsOutsideEpoch(batch);
            return EventProcessingResult.Ignored([diagnostic]);
        }

        var diagnostics = new List<Diagnostic>();
        var classification = ClassifyBatch(batch, diagnostics);
        if (classification == EventProcessingState.Incomplete)
        {
            currentRun?.MarkEvidenceIncomplete();
        }

        if (classification == EventProcessingState.Invalidated)
        {
            QueueLog(LogLevel.Warning,
                "Event-stream batch invalidated observation. SessionId={SessionId}, ReadState={ReadState}, EventCount={EventCount}",
                batch.SessionId.Value,
                batch.ReadState,
                batch.Events.Count);
            currentRun?.Complete(EventStreamCompletionReason.TargetInvalidated, timeProvider.GetUtcNow());
            TraceBatchEventsOutsideEpoch(batch);
            var invalidatedResult = new EventProcessingResult(
                classification,
                currentRun?.CreateSnapshot(),
                Array.Empty<UnitTestEventRef>(),
                diagnostics);
            PublishUpdate(
                EventStreamProcessorUpdateKind.BatchProcessed,
                invalidatedResult.State,
                invalidatedResult.AcceptedEvents,
                invalidatedResult.Diagnostics);
            return invalidatedResult;
        }

        var orderedEvents = batch.Events
            .Where(static entry => entry.SequenceId > 0)
            .OrderBy(static entry => entry.SequenceId)
            .ToArray();

        var acceptedRefs = new List<UnitTestEventRef>();
        var runEpochStarted = false;
        var completionAnchorObservedInBatch = false;
        if (currentRun is not null && !currentRun.IsTerminal && batch.BufferObservedEmpty)
        {
            bufferObservedEmptyForCurrentEpoch = true;
        }

        foreach (var entry in orderedEvents)
        {
            if (highestAcceptedSequenceId is { } highest && entry.SequenceId <= highest)
            {
                diagnostics.Add(CreateDiagnostic(
                    "Workflow.EventStreamProcessor.StaleEvent",
                    LogLevel.Warning,
                    $"Event sequence '{entry.SequenceId}' is stale for the active event-stream observation."));
                TracePlcEvent(batch.SessionId, entry, runEpochId: null);
                continue;
            }

            if (highestAcceptedSequenceId is { } previous && entry.SequenceId > previous + 1)
            {
                diagnostics.Add(CreateDiagnostic(
                    "Workflow.EventStreamProcessor.SequenceGap",
                    LogLevel.Warning,
                    $"Event sequence gap detected between '{previous}' and '{entry.SequenceId}'."));
            }

            highestAcceptedSequenceId = entry.SequenceId;

            if (IsRunControlStartAnchor(entry))
            {
                if (currentRun is not null && !currentRun.IsTerminal)
                {
                    currentRun.MarkEvidenceIncomplete();
                    currentRun.Complete(EventStreamCompletionReason.TargetInvalidated, entry.Timestamp);
                    diagnostics.Add(CreateDiagnostic(
                        "Workflow.EventStreamProcessor.OverlappingRunEpoch",
                        LogLevel.Error,
                        "A RunControl start anchor was observed before the active RunEpoch reached its completion anchor."));
                }

                StartNewEpochFromAnchor(batch.SessionId, entry);
                runEpochStarted = true;
            }

            if (CanAcceptIntoCurrentEpoch(entry))
            {
                acceptedEvents.Add(entry);
                acceptedEventsChanged = true;
                currentRun!.AppendEvent(entry);
                acceptedRefs.Add(new UnitTestEventRef(entry.SequenceId));
                TracePlcEvent(batch.SessionId, entry, currentRun.EpochId);
                if (IsFrameworkEvent(entry))
                {
                    diagnostics.Add(CreateFrameworkEventDiagnostic(entry, correlateWithCurrentEpoch: true));
                }

                if (IsRunControlCompletionAnchor(entry))
                {
                    runControlCompletionAnchorObserved = true;
                    completionAnchorObservedInBatch = true;
                }

                continue;
            }

            RouteOutOfEpochEvent(entry, diagnostics);
            TracePlcEvent(batch.SessionId, entry, runEpochId: null);
        }

        if (classification == EventProcessingState.Incomplete)
        {
            currentRun?.MarkEvidenceIncomplete();
        }

        if (batch.LostEventCount > 0)
        {
            currentRun?.RecordLostEvents(batch.LostEventCount, timeProvider.GetUtcNow());
        }

        if (runEpochStarted && batch.BufferObservedEmpty)
        {
            bufferObservedEmptyForCurrentEpoch = true;
        }

        var epochCompleted = TryCompleteCurrentEpochFromEvidence();
        if (epochCompleted)
        {
            classification = EventProcessingState.Completed;
        }

        QueueLog(LogLevel.Debug,
            "Event-stream batch processed. SessionId={SessionId}, State={State}, AcceptedEventCount={AcceptedEventCount}, TotalAcceptedEventCount={TotalAcceptedEventCount}, DiagnosticCount={DiagnosticCount}",
            batch.SessionId.Value,
            classification,
            acceptedRefs.Count,
            acceptedEvents.Count,
            diagnostics.Count);

        var processingState = classification == EventProcessingState.Accepted
            && acceptedRefs.Count == 0
            && diagnostics.Count == 0
            ? EventProcessingState.Ignored
            : classification;
        var processingResult = new EventProcessingResult(
            processingState,
            currentRun?.CreateSnapshot(),
            acceptedRefs,
            diagnostics)
        {
            RunEpochStarted = runEpochStarted,
            RunControlCompletionAnchorObserved = completionAnchorObservedInBatch
        };
        PublishUpdate(
            EventStreamProcessorUpdateKind.BatchProcessed,
            processingResult.State,
            processingResult.AcceptedEvents,
            processingResult.Diagnostics);
        return processingResult;
    }

    public EventProcessingResult ApplyMonitoringState(EventStreamState state)
        => ApplyStateChange(() => ApplyMonitoringStateCore(state));

    private EventProcessingResult ApplyMonitoringStateCore(EventStreamState state)
    {
        if (observationContext is null || state.SessionId != observationContext.SessionId)
        {
            return EventProcessingResult.Ignored(Array.Empty<Diagnostic>());
        }

        if (IsEventBufferEpochResetBoundary(state))
        {
            var previousSequenceId = highestAcceptedSequenceId;
            highestAcceptedSequenceId = 0;
            if (previousSequenceId is > 0)
            {
                if (currentRun is not null && !currentRun.IsTerminal)
                {
                    currentRun.MarkEvidenceIncomplete();
                    currentRun.Complete(EventStreamCompletionReason.Timeout, timeProvider.GetUtcNow());
                }

                QueueLog(LogLevel.Information,
                    "Event-buffer epoch reset observed. PreviousSequenceId={PreviousSequenceId}",
                    previousSequenceId);
                var resetResult = new EventProcessingResult(
                    currentRun is not null ? EventProcessingState.Incomplete : EventProcessingState.Ignored,
                    currentRun?.CreateSnapshot(),
                    Array.Empty<UnitTestEventRef>(),
                    Array.Empty<Diagnostic>());
                PublishUpdate(
                    EventStreamProcessorUpdateKind.MonitoringStateApplied,
                    resetResult.State,
                    resetResult.AcceptedEvents,
                    resetResult.Diagnostics);
                return resetResult;
            }
        }

        if (state.LostEventCount == 0 || currentRun is null)
        {
            return new EventProcessingResult(
                EventProcessingState.Ignored,
                currentRun?.CreateSnapshot(),
                Array.Empty<UnitTestEventRef>(),
                Array.Empty<Diagnostic>());
        }

        var diagnostics = new List<Diagnostic>();
        if (currentRun.RecordLostEvents(state.LostEventCount, timeProvider.GetUtcNow()))
        {
            diagnostics.Add(CreateDiagnostic(
                "Workflow.EventStreamProcessor.LostEvents",
                LogLevel.Warning,
                $"Event-stream producer reported '{state.LostEventCount}' lost event(s) for the active RunEpoch."));
        }

        var result = new EventProcessingResult(
            EventProcessingState.Incomplete,
            currentRun.CreateSnapshot(),
            Array.Empty<UnitTestEventRef>(),
            diagnostics);
        PublishUpdate(
            EventStreamProcessorUpdateKind.MonitoringStateApplied,
            result.State,
            result.AcceptedEvents,
            result.Diagnostics);
        return result;
    }

    private static bool IsEventBufferEpochResetBoundary(EventStreamState state)
    {
        return state.IsMonitoring
            && state.LastReadState == EventBufferReadState.Empty
            && state.BufferObservedEmpty
            && state.ProducerSequenceId == 0
            && state.DeletedSequenceId == 0
            && state.LostEventCount == 0;
    }

    public UnitTestRunEpochSnapshot? CreateSnapshot()
    {
        return Current;
    }

    public EventProcessingResult CompleteObservation(
        EventStreamCompletionReason reason,
        DateTimeOffset completedAt)
    {
        StopObservation();
        return EventProcessingResult.Ignored(Array.Empty<Diagnostic>());
    }

    public EventProcessingResult CompleteCurrentEpoch(
        EventStreamCompletionReason reason,
        DateTimeOffset completedAt)
        => ApplyStateChange(() => CompleteCurrentEpochCore(reason, completedAt));

    private EventProcessingResult CompleteCurrentEpochCore(
        EventStreamCompletionReason reason,
        DateTimeOffset completedAt)
    {
        if (currentRun is null)
        {
            return EventProcessingResult.Ignored(Array.Empty<Diagnostic>());
        }

        if (currentRun.IsTerminal)
        {
            return new EventProcessingResult(
                EventProcessingState.Ignored,
                currentRun.CreateSnapshot(),
                Array.Empty<UnitTestEventRef>(),
                Array.Empty<Diagnostic>());
        }

        currentRun.Complete(reason, completedAt);
        QueueLog(LogLevel.Information,
            "UnitTest run epoch completed. EpochId={EpochId}, Reason={Reason}",
            currentRun.EpochId.Value,
            reason);
        var result = new EventProcessingResult(
            ToProcessingState(reason),
            currentRun.CreateSnapshot(),
            Array.Empty<UnitTestEventRef>(),
            Array.Empty<Diagnostic>());
        PublishUpdate(
            EventStreamProcessorUpdateKind.EpochCompleted,
            result.State,
            result.AcceptedEvents,
            result.Diagnostics);
        return result;
    }

    public void StopObservation()
        => ApplyStateChange(StopObservationCore);

    private void StopObservationCore()
    {
        observationContext = null;
        highestAcceptedSequenceId = null;
        runControlCompletionAnchorObserved = false;
        bufferObservedEmptyForCurrentEpoch = false;
        QueueLog(LogLevel.Information, "Event-stream observation stopped.");
        PublishUpdate(
            EventStreamProcessorUpdateKind.ObservationStopped,
            EventProcessingState.Ignored,
            Array.Empty<UnitTestEventRef>(),
            Array.Empty<Diagnostic>());
    }

    public void ClearCurrentEpoch()
        => ApplyStateChange(ClearCurrentEpochCore);

    private void ClearCurrentEpochCore()
    {
        ClearAcceptedEvents();
        currentRun = null;
        highestAcceptedSequenceId = observationContext?.SequenceBaseline;
        runControlCompletionAnchorObserved = false;
        bufferObservedEmptyForCurrentEpoch = false;
        lastTracedRunEpoch = null;
        QueueLog(LogLevel.Information, "Current UnitTest run epoch cleared.");
        PublishUpdate(
            EventStreamProcessorUpdateKind.EpochCleared,
            EventProcessingState.Ignored,
            Array.Empty<UnitTestEventRef>(),
            Array.Empty<Diagnostic>());
    }

    public void Reset()
        => ApplyStateChange(ResetCore);

    private void ResetCore()
    {
        observationContext = null;
        ClearAcceptedEvents();
        currentRun = null;
        highestAcceptedSequenceId = null;
        runControlCompletionAnchorObserved = false;
        bufferObservedEmptyForCurrentEpoch = false;
        lastTracedRunEpoch = null;
        QueueLog(LogLevel.Information, "Event-stream processor reset.");
        PublishUpdate(
            EventStreamProcessorUpdateKind.Reset,
            EventProcessingState.Ignored,
            Array.Empty<UnitTestEventRef>(),
            Array.Empty<Diagnostic>());
    }

    private void PublishUpdate(
        EventStreamProcessorUpdateKind kind,
        EventProcessingState state,
        IReadOnlyList<UnitTestEventRef> acceptedEventRefs,
        IReadOnlyList<Diagnostic> updateDiagnostics)
    {
        var update = new EventStreamProcessorUpdate(
            kind,
            state,
            currentRun?.CreateSnapshot(),
            acceptedEventRefs,
            updateDiagnostics);
        TraceRunEpoch(update.CurrentRun);
        pendingNotifications.Enqueue(() =>
            ProcessorUpdateEventHandler?.Invoke(this, new EventStreamProcessorUpdateEventArgs(update)));
    }

    private void ClearAcceptedEvents()
    {
        acceptedEventsChanged |= acceptedEvents.Count > 0;
        acceptedEvents.Clear();
    }

    private void ApplyStateChange(Action change)
    {
        ApplyStateChange(() =>
        {
            change();
            return true;
        });
    }

    private T ApplyStateChange<T>(Func<T> change)
    {
        T result;
        bool drainNotifications;
        lock (stateSync)
        {
            result = change();
            var events = acceptedEventsChanged
                ? Array.AsReadOnly(acceptedEvents.ToArray())
                : publishedState.AcceptedEvents;
            Volatile.Write(ref publishedState, new PublishedState(currentRun?.CreateSnapshot(), events));
            acceptedEventsChanged = false;
            drainNotifications = !notificationDrainActive && pendingNotifications.Count > 0;
            if (drainNotifications)
            {
                notificationDrainActive = true;
            }
        }

        DrainNotifications(drainNotifications);
        return result;
    }

    // Serialize delivery in commit order without keeping readers or writers behind callbacks.
    private void DrainNotifications(bool drainNotifications)
    {
        if (!drainNotifications)
        {
            return;
        }

        ExceptionDispatchInfo? firstFailure = null;
        while (true)
        {
            Action notification;
            lock (stateSync)
            {
                if (pendingNotifications.Count == 0)
                {
                    notificationDrainActive = false;
                    break;
                }

                notification = pendingNotifications.Dequeue();
            }

            try
            {
                notification();
            }
            catch (Exception exception)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        firstFailure?.Throw();
    }

    private void QueueLog(LogLevel level, string message, params object?[] arguments)
    {
        pendingNotifications.Enqueue(() => logger.Log(level, message, arguments));
    }

    private void QueueActivity(UnitTestActivityPayload payload, UnitTestActivityCorrelation correlation)
    {
        if (activityTraceSink is not null)
        {
            pendingNotifications.Enqueue(() => activityTraceSink.TryRecord(payload, correlation));
        }
    }

    private sealed record PublishedState(
        UnitTestRunEpochSnapshot? CurrentRun,
        IReadOnlyList<UnitTestEventEntry> AcceptedEvents);

    private void TraceBatchEventsOutsideEpoch(UnitTestEventBatch batch)
    {
        foreach (var entry in batch.Events
                     .Where(static entry => entry.SequenceId > 0)
                     .OrderBy(static entry => entry.SequenceId))
        {
            TracePlcEvent(batch.SessionId, entry, runEpochId: null);
        }
    }

    private void TracePlcEvent(
        CommunicationSessionId communicationSessionId,
        UnitTestEventEntry entry,
        RunEpochId? runEpochId)
    {
        QueueActivity(
            new PlcEventActivityPayload(entry),
            new UnitTestActivityCorrelation(
                CommunicationSessionId: communicationSessionId,
                RunEpochId: runEpochId));
    }

    private void TraceRunEpoch(UnitTestRunEpochSnapshot? snapshot)
    {
        if (snapshot is null || activityTraceSink is null)
        {
            return;
        }

        var terminal = IsTerminalRunState(snapshot.State);
        UnitTestRunEpochActivityKind activity;
        if (lastTracedRunEpoch?.EpochId != snapshot.EpochId)
        {
            activity = UnitTestRunEpochActivityKind.Started;
        }
        else if (terminal && lastTracedRunEpoch?.Terminal != true)
        {
            activity = UnitTestRunEpochActivityKind.Ended;
        }
        else if (lastTracedRunEpoch is { } previous
            && previous.State == snapshot.State
            && previous.Result == snapshot.Result
            && previous.EvidenceState == snapshot.EvidenceState
            && previous.EventCount == snapshot.EventStream.Count
            && previous.LostEventCount == snapshot.LostEventCount
            && previous.LastUpdatedAt == snapshot.LastUpdatedAt)
        {
            return;
        }
        else
        {
            activity = UnitTestRunEpochActivityKind.Updated;
        }

        lastTracedRunEpoch = new RunEpochTraceState(
            snapshot.EpochId,
            snapshot.State,
            snapshot.Result,
            snapshot.EvidenceState,
            snapshot.EventStream.Count,
            snapshot.LostEventCount,
            snapshot.LastUpdatedAt,
            terminal);
        QueueActivity(
            new RunEpochActivityPayload(activity, snapshot),
            new UnitTestActivityCorrelation(RunEpochId: snapshot.EpochId));
    }

    private static bool IsTerminalRunState(UnitTestRunState state)
    {
        return state is UnitTestRunState.Completed
            or UnitTestRunState.Aborted
            or UnitTestRunState.TimedOut
            or UnitTestRunState.Invalidated;
    }

    private EventProcessingState ClassifyBatch(
        UnitTestEventBatch batch,
        List<Diagnostic> diagnostics)
    {
        if (batch.ReadState is EventBufferReadState.ContractInvalid or EventBufferReadState.SessionInvalid)
        {
            diagnostics.Add(CreateDiagnostic(
                "Workflow.EventStreamProcessor.ReadStateInvalidated",
                LogLevel.Error,
                $"Event-stream read state invalidated observation: {batch.ReadState}."));
            return EventProcessingState.Invalidated;
        }

        if (batch.ReadState == EventBufferReadState.Inconsistent)
        {
            diagnostics.Add(CreateDiagnostic(
                "Workflow.EventStreamProcessor.ReadStateInconsistent",
                LogLevel.Warning,
                "Event-stream batch was classified as inconsistent by the access profile."));
            return EventProcessingState.Ambiguous;
        }

        if (batch.ReadState == EventBufferReadState.RetryRequired)
        {
            diagnostics.Add(CreateDiagnostic(
                "Workflow.EventStreamProcessor.RetryRequired",
                LogLevel.Warning,
                "Event-stream batch requires a later retry before it can be treated as complete evidence."));
            return EventProcessingState.Incomplete;
        }

        if (batch.LostEventCount > 0)
        {
            diagnostics.Add(CreateDiagnostic(
                "Workflow.EventStreamProcessor.LostEvents",
                LogLevel.Warning,
                $"Event-stream producer reported '{batch.LostEventCount}' lost event(s)."));
        }

        if (batch.Events.Count == 0)
        {
            // An empty read can precede newly observed producer activity; it does not prove event loss.
            if (batch.ReadState == EventBufferReadState.Empty && batch.LostEventCount == 0)
            {
                return EventProcessingState.Empty;
            }

            return batch.BufferObservedEmpty
                ? EventProcessingState.Empty
                : EventProcessingState.Incomplete;
        }

        if (batch.Events.Any(static entry => entry.SequenceId == 0))
        {
            diagnostics.Add(CreateDiagnostic(
                "Workflow.EventStreamProcessor.EmptyEntryIgnored",
                LogLevel.Warning,
                "Event batch contained entry data with SequenceId 0. Such entries are not produced UnitTest events."));
        }

        return batch.LostEventCount > 0
            ? EventProcessingState.Incomplete
            : EventProcessingState.Accepted;
    }

    private Diagnostic CreateDiagnostic(
        string code,
        LogLevel level,
        string message)
    {
        return new Diagnostic(
            code,
            DiagnosticCategory.Workflow,
            level,
            DiagnosticSource.RunObservation,
            DiagnosticPhase.ExecutionMonitoring,
            message,
            new DiagnosticCorrelation(),
            timeProvider.GetUtcNow());
    }

    private Diagnostic CreateFrameworkEventDiagnostic(
        UnitTestEventEntry entry,
        bool correlateWithCurrentEpoch)
    {
        return new Diagnostic(
            "Workflow.EventStreamProcessor.FrameworkEvent",
            DiagnosticCategory.Workflow,
            entry.Severity,
            DiagnosticSource.RunObservation,
            DiagnosticPhase.ExecutionMonitoring,
            entry.Message,
            new DiagnosticCorrelation(UnitTestRunEpochId: correlateWithCurrentEpoch ? currentRun?.EpochId : null),
            entry.Timestamp,
            TechnicalDetail: $"Framework event '{entry.Name}' reported result '{entry.Result}' with owner scope '{entry.OwnerScope}' and event type '{entry.EventType}'.",
            EvidenceReference: new DiagnosticEvidenceReference("unit-test-event", entry.SequenceId.ToString()));
    }

    private static bool IsFrameworkEvent(UnitTestEventEntry entry)
    {
        return entry.OwnerScope == UnitTestOwnerScope.Framework
            || entry.EventType == UnitTestEventType.Framework;
    }

    private void StartNewEpochFromAnchor(
        CommunicationSessionId sessionId,
        UnitTestEventEntry startAnchor)
    {
        ClearAcceptedEvents();
        runControlCompletionAnchorObserved = false;
        bufferObservedEmptyForCurrentEpoch = false;
        currentRun = new ActiveRunEpochState(
            new RunEpochId(Guid.NewGuid().ToString("N")),
            startAnchor.Timestamp);
        QueueLog(LogLevel.Information,
            "UnitTest run epoch started from RunControl event-stream anchor. EpochId={EpochId}, SessionId={SessionId}, StartSequenceId={StartSequenceId}",
            currentRun.EpochId.Value,
            sessionId.Value,
            startAnchor.SequenceId);
    }

    private bool CanAcceptIntoCurrentEpoch(UnitTestEventEntry entry)
    {
        if (currentRun is null || currentRun.IsTerminal)
        {
            return false;
        }

        return !runControlCompletionAnchorObserved || IsRunControlCompletionAnchor(entry);
    }

    private bool TryCompleteCurrentEpochFromEvidence()
    {
        if (currentRun is null
            || currentRun.IsTerminal
            || !runControlCompletionAnchorObserved
            || !bufferObservedEmptyForCurrentEpoch)
        {
            return false;
        }

        var reason = currentRun.Result == UnitTestResultState.Abort
            ? EventStreamCompletionReason.AbortObserved
            : EventStreamCompletionReason.CompletionAnchorAndBufferEmpty;
        currentRun.Complete(reason, timeProvider.GetUtcNow());
        QueueLog(LogLevel.Information,
            "UnitTest run epoch completed from independent completion-anchor and empty-buffer evidence. EpochId={EpochId}, Reason={Reason}",
            currentRun.EpochId.Value,
            reason);
        return true;
    }

    private void RouteOutOfEpochEvent(
        UnitTestEventEntry entry,
        List<Diagnostic> diagnostics)
    {
        if (IsFrameworkEvent(entry))
        {
            diagnostics.Add(CreateFrameworkEventDiagnostic(entry, correlateWithCurrentEpoch: false));
            return;
        }

        if (RequiresRunEpoch(entry))
        {
            diagnostics.Add(CreateDiagnostic(
                "Workflow.EventStreamProcessor.RunEvidenceOutsideEpoch",
                LogLevel.Warning,
                $"Run-owned event '{entry.SequenceId}' ({entry.OwnerScope}/{entry.EventType}/{entry.State}) was observed outside a RunEpoch."));
            return;
        }

        QueueLog(LogLevel.Debug,
            "UnitTest event observed outside a RunEpoch and not added to run evidence. SequenceId={SequenceId}, OwnerScope={OwnerScope}, EventType={EventType}, State={State}",
            entry.SequenceId,
            entry.OwnerScope,
            entry.EventType,
            entry.State);
    }

    private static bool RequiresRunEpoch(UnitTestEventEntry entry)
    {
        if (IsRunControlCompletionAnchor(entry))
        {
            return true;
        }

        if (entry.OwnerScope == UnitTestOwnerScope.RunControl)
        {
            return entry.EventType is UnitTestEventType.Result or UnitTestEventType.Aggregation;
        }

        if (entry.OwnerScope is not (UnitTestOwnerScope.TestSuite
            or UnitTestOwnerScope.TestCase
            or UnitTestOwnerScope.Assertion))
        {
            return false;
        }

        if (entry.EventType is UnitTestEventType.Result or UnitTestEventType.Aggregation)
        {
            return true;
        }

        return entry.EventType == UnitTestEventType.StateTransition
            && entry.State is not (UnitTestLifecycleState.Uninitialized
                or UnitTestLifecycleState.Reset
                or UnitTestLifecycleState.Idle);
    }

    private static bool IsRunControlStartAnchor(UnitTestEventEntry entry)
    {
        return entry.OwnerScope == UnitTestOwnerScope.RunControl
            && entry.EventType == UnitTestEventType.StateTransition
            && entry.State == UnitTestLifecycleState.Starting;
    }

    private static bool IsRunControlCompletionAnchor(UnitTestEventEntry entry)
    {
        return entry.OwnerScope == UnitTestOwnerScope.RunControl
            && entry.EventType == UnitTestEventType.StateTransition
            && entry.State == UnitTestLifecycleState.Completed;
    }

    private static EventProcessingState ToProcessingState(EventStreamCompletionReason reason)
    {
        return reason switch
        {
            EventStreamCompletionReason.CompletionAnchorAndBufferEmpty => EventProcessingState.Completed,
            EventStreamCompletionReason.AbortObserved => EventProcessingState.Completed,
            EventStreamCompletionReason.ResetObserved => EventProcessingState.Invalidated,
            EventStreamCompletionReason.Timeout => EventProcessingState.Incomplete,
            EventStreamCompletionReason.TargetInvalidated => EventProcessingState.Invalidated,
            EventStreamCompletionReason.Cleanup => EventProcessingState.Invalidated,
            _ => EventProcessingState.Invalidated
        };
    }

    private sealed record RunEpochTraceState(
        RunEpochId EpochId,
        UnitTestRunState State,
        UnitTestResultState Result,
        EvidenceState EvidenceState,
        int EventCount,
        ushort LostEventCount,
        DateTimeOffset? LastUpdatedAt,
        bool Terminal);
}

public sealed record EventStreamObservationContext(
    CommunicationSessionId SessionId,
    uint? SequenceBaseline,
    DateTimeOffset StartedAt);

public sealed record EventProcessingResult(
    EventProcessingState State,
    UnitTestRunEpochSnapshot? CurrentRun,
    IReadOnlyList<UnitTestEventRef> AcceptedEvents,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool RunEpochStarted { get; init; }

    public bool RunControlCompletionAnchorObserved { get; init; }

    public static EventProcessingResult Ignored(IReadOnlyList<Diagnostic> diagnostics)
    {
        return new EventProcessingResult(
            EventProcessingState.Ignored,
            CurrentRun: null,
            Array.Empty<UnitTestEventRef>(),
            diagnostics);
    }
}

public sealed record EventStreamProcessorUpdate(
    EventStreamProcessorUpdateKind Kind,
    EventProcessingState State,
    UnitTestRunEpochSnapshot? CurrentRun,
    IReadOnlyList<UnitTestEventRef> AcceptedEvents,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record EventStreamProcessorUpdateEventArgs(
    EventStreamProcessorUpdate Update);

public enum EventStreamProcessorUpdateKind
{
    ObservationStarted,
    ObservationStopped,
    EpochStarted,
    EpochCleared,
    EpochCompleted,
    BatchProcessed,
    MonitoringStateApplied,
    BatchIgnored,
    Reset
}

public enum EventProcessingState
{
    Ignored,
    Empty,
    Accepted,
    Completed,
    Incomplete,
    Ambiguous,
    Invalidated
}

public enum EventStreamCompletionReason
{
    CompletionAnchorAndBufferEmpty,
    AbortObserved,
    ResetObserved,
    Timeout,
    TargetInvalidated,
    Cleanup
}

internal sealed class ActiveRunEpochState
{
    // Only accessed by EventStreamProcessor while its state lock is held.
    private UnitTestRunEpochSnapshot? snapshot;
    private readonly List<UnitTestEventEntry> eventStream = [];
    private readonly List<UnitTestEventRef> frameworkEvents = [];
    private readonly RunNodeBuilder rootNode;
    private readonly RunEpochId epochId;
    private readonly DateTimeOffset createdAt;
    private UnitTestRunState state = UnitTestRunState.Observing;
    private UnitTestResultState result = UnitTestResultState.NotEvaluated;
    private EvidenceState evidenceState = EvidenceState.None;
    private DateTimeOffset? lastUpdatedAt;
    private ushort lostEventCount;

    public ActiveRunEpochState(RunEpochId epochId, DateTimeOffset createdAt)
    {
        this.epochId = epochId;
        this.createdAt = createdAt;
        lastUpdatedAt = createdAt;
        rootNode = new RunNodeBuilder(
            new UnitTestNodeId($"{epochId.Value}:run-control"),
            "RunControl",
            UnitTestNodeKind.RunControl,
            createdAt);
    }

    public RunEpochId EpochId => epochId;

    public UnitTestResultState Result => result;

    public bool IsTerminal => state is UnitTestRunState.Completed
        or UnitTestRunState.Aborted
        or UnitTestRunState.TimedOut
        or UnitTestRunState.Invalidated;

    public void AppendEvent(UnitTestEventEntry entry)
    {
        snapshot = null;
        eventStream.Add(entry);
        if (entry.OwnerScope == UnitTestOwnerScope.Framework || entry.EventType == UnitTestEventType.Framework)
        {
            frameworkEvents.Add(new UnitTestEventRef(entry.SequenceId));
        }

        ApplySemanticEvent(entry);
        lastUpdatedAt = entry.Timestamp;
        if (entry.OwnerScope == UnitTestOwnerScope.RunControl
            && entry.EventType == UnitTestEventType.StateTransition
            && entry.State == UnitTestLifecycleState.Completed)
        {
            state = UnitTestRunState.Completing;
        }
        else if (state == UnitTestRunState.Observing && entry.EventType != UnitTestEventType.Trigger)
        {
            state = UnitTestRunState.Running;
        }

        if (evidenceState == EvidenceState.None)
        {
            evidenceState = EvidenceState.Partial;
        }
    }

    public void Complete(EventStreamCompletionReason reason, DateTimeOffset completedAt)
    {
        snapshot = null;
        state = reason switch
        {
            EventStreamCompletionReason.CompletionAnchorAndBufferEmpty => UnitTestRunState.Completed,
            EventStreamCompletionReason.AbortObserved => UnitTestRunState.Aborted,
            EventStreamCompletionReason.Timeout => UnitTestRunState.TimedOut,
            _ => UnitTestRunState.Invalidated
        };
        result = reason == EventStreamCompletionReason.AbortObserved
            ? UnitTestResultState.Abort
            : result;
        evidenceState = state is UnitTestRunState.Completed or UnitTestRunState.Aborted
            ? (evidenceState == EvidenceState.Incomplete
                || rootNode.HasIncompleteRequiredEvidence(
                    requireRunControlCompletion: reason == EventStreamCompletionReason.CompletionAnchorAndBufferEmpty)
                ? EvidenceState.Incomplete
                : EvidenceState.Complete)
            : EvidenceState.Incomplete;
        lastUpdatedAt = completedAt;
    }

    public void MarkEvidenceIncomplete()
    {
        if (evidenceState != EvidenceState.Incomplete)
        {
            snapshot = null;
        }
        evidenceState = EvidenceState.Incomplete;
    }

    public bool RecordLostEvents(ushort observedLostEventCount, DateTimeOffset observedAt)
    {
        if (observedLostEventCount <= lostEventCount)
        {
            return false;
        }

        snapshot = null;
        lostEventCount = observedLostEventCount;
        evidenceState = EvidenceState.Incomplete;
        lastUpdatedAt = observedAt;
        return true;
    }

    public UnitTestRunEpochSnapshot CreateSnapshot()
    {
        if (snapshot is not null)
        {
            return snapshot;
        }

        var eventCopy = eventStream.ToArray();
        var frameworkCopy = frameworkEvents.ToArray();
        snapshot = new UnitTestRunEpochSnapshot(
            epochId,
            state,
            result,
            evidenceState,
            createdAt,
            lastUpdatedAt,
            Array.AsReadOnly(eventCopy),
            Array.AsReadOnly(frameworkCopy),
            rootNode.ToSnapshot(result, evidenceState),
            lostEventCount);
        return snapshot;
    }

    private void ApplySemanticEvent(UnitTestEventEntry entry)
    {
        if (entry.EventType == UnitTestEventType.Info)
        {
            return;
        }

        if (entry.OwnerScope == UnitTestOwnerScope.Framework || entry.EventType == UnitTestEventType.Framework)
        {
            rootNode.AddFinding(new UnitTestFinding(
                UnitTestFindingKind.Framework,
                entry.Result,
                entry.Name,
                entry.Message,
                new UnitTestEventRef(entry.SequenceId)));
            return;
        }

        var node = ResolveNode(entry);
        if (node is null)
        {
            return;
        }

        node.Touch(entry);
        switch (entry.EventType)
        {
            case UnitTestEventType.StateTransition:
                node.LifecycleState = entry.State;
                if (entry.State == UnitTestLifecycleState.Completed)
                {
                    node.CompletionEvent = new UnitTestEventRef(entry.SequenceId);
                }

                break;
            case UnitTestEventType.Result:
                node.Result = entry.Result;
                node.ResultEvent = new UnitTestEventRef(entry.SequenceId);
                if (entry.OwnerScope == UnitTestOwnerScope.Assertion)
                {
                    ApplyAssertionFinding(node, entry);
                }
                else
                {
                    node.PlcReportedResult = entry.Result;
                    if (entry.Result is UnitTestResultState.Failed or UnitTestResultState.Error or UnitTestResultState.Abort)
                    {
                        node.AddFinding(new UnitTestFinding(
                            UnitTestFindingKind.OwnerResult,
                            entry.Result,
                            entry.Name,
                            entry.Message,
                            new UnitTestEventRef(entry.SequenceId)));
                    }
                }

                break;
            case UnitTestEventType.Aggregation:
                if (TryParseAggregation(entry.Message, out var aggregation))
                {
                    node.Aggregation = aggregation;
                    node.AggregationEvent = new UnitTestEventRef(entry.SequenceId);
                }
                else
                {
                    node.AddFinding(new UnitTestFinding(
                        UnitTestFindingKind.Consistency,
                        UnitTestResultState.Error,
                        entry.Name,
                        "Aggregation payload could not be parsed.",
                        new UnitTestEventRef(entry.SequenceId)));
                }

                break;
            case UnitTestEventType.Trigger:
                break;
        }

        PropagateResults();
    }

    private RunNodeBuilder? ResolveNode(UnitTestEventEntry entry)
    {
        return entry.OwnerScope switch
        {
            UnitTestOwnerScope.RunControl => rootNode,
            UnitTestOwnerScope.TestSuite => GetOrCreateSuite(entry),
            UnitTestOwnerScope.TestCase => GetOrCreateTestCase(entry),
            UnitTestOwnerScope.Assertion
                => GetOrCreateTestCase(entry),
            _ => null
        };
    }

    private RunNodeBuilder GetOrCreateSuite(UnitTestEventEntry entry)
    {
        var path = SplitName(entry.Name);
        var suiteName = path.Length > 1
            ? path[1]
            : path.Length == 1
                ? path[0]
                : entry.Name;
        return rootNode.GetOrCreateChild(
            UnitTestNodeKind.TestSuite,
            suiteName,
            CreateNodeId(UnitTestNodeKind.TestSuite, suiteName),
            entry.Timestamp);
    }

    private RunNodeBuilder GetOrCreateTestCase(UnitTestEventEntry entry)
    {
        var path = SplitName(entry.Name);
        var usesContractHierarchy = entry.OwnerScope == UnitTestOwnerScope.Assertion
            ? path.Length > 3
            : path.Length > 2;
        var suiteName = usesContractHierarchy
            ? path[1]
            : path.Length > 1
                ? path[0]
                : "Default";
        var caseName = entry.OwnerScope == UnitTestOwnerScope.Assertion
            ? (usesContractHierarchy ? path[2] : path.Length > 1 ? path[^2] : suiteName)
            : (usesContractHierarchy ? path[2] : path.Length > 0 ? path[^1] : entry.Name);

        var suite = rootNode.GetOrCreateChild(
            UnitTestNodeKind.TestSuite,
            suiteName,
            CreateNodeId(UnitTestNodeKind.TestSuite, suiteName),
            entry.Timestamp);
        return suite.GetOrCreateChild(
            UnitTestNodeKind.TestCase,
            caseName,
            CreateNodeId(UnitTestNodeKind.TestCase, $"{suiteName}.{caseName}"),
            entry.Timestamp);
    }

    private void ApplyAssertionFinding(RunNodeBuilder node, UnitTestEventEntry entry)
    {
        var resultState = entry.Result == UnitTestResultState.NotEvaluated
            ? UnitTestResultState.Unknown
            : entry.Result;
        if (resultState is UnitTestResultState.Failed or UnitTestResultState.Error)
        {
            node.AddFinding(new UnitTestFinding(
                UnitTestFindingKind.Assertion,
                resultState,
                entry.Name,
                entry.Message,
                new UnitTestEventRef(entry.SequenceId)));
            node.Result = resultState == UnitTestResultState.Error
                ? UnitTestResultState.Error
                : UnitTestResultState.Failed;
        }
    }

    private void PropagateResults()
    {
        rootNode.PropagateResultFromChildren();
        result = rootNode.Result;
    }

    private UnitTestNodeId CreateNodeId(UnitTestNodeKind kind, string name)
    {
        var normalized = new string(name.Select(static c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "unnamed";
        }

        return new UnitTestNodeId($"{epochId.Value}:{kind}:{normalized}".ToLowerInvariant());
    }

    private static string[] SplitName(string name)
    {
        return name
            .Split(['.', '/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool TryParseAggregation(string payload, out UnitTestAggregation? aggregation)
    {
        aggregation = null;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            aggregation = new UnitTestAggregation(
                GetRequiredUInt(root, "total"),
                GetOptionalUInt(root, "inactive"),
                GetOptionalUInt(root, "idle"),
                GetOptionalUInt(root, "running"),
                GetOptionalUInt(root, "completed"),
                GetRequiredUInt(root, "passed"),
                GetRequiredUInt(root, "failed"),
                GetRequiredUInt(root, "error"),
                GetOptionalUInt(root, "runtime"),
                GetOptionalUInt(root, "lostEvents"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static uint GetRequiredUInt(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.TryGetUInt32(out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Aggregation property '{name}' is missing or invalid.");
    }

    private static uint? GetOptionalUInt(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.TryGetUInt32(out var parsed)
            ? parsed
            : null;
    }
}

internal sealed class RunNodeBuilder
{
    private readonly List<RunNodeBuilder> children = [];
    private readonly List<UnitTestFinding> findings = [];

    public RunNodeBuilder(
        UnitTestNodeId id,
        string name,
        UnitTestNodeKind kind,
        DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        Kind = kind;
        CreatedAt = createdAt;
        LastUpdatedAt = createdAt;
    }

    public UnitTestNodeId Id { get; }
    public string Name { get; }
    public UnitTestNodeKind Kind { get; }
    public UnitTestLifecycleState LifecycleState { get; set; } = UnitTestLifecycleState.Unknown;
    public UnitTestResultState Result { get; set; } = UnitTestResultState.NotEvaluated;
    public UnitTestResultState? PlcReportedResult { get; set; }
    public UnitTestAggregation? Aggregation { get; set; }
    public UnitTestEventRef? CompletionEvent { get; set; }
    public UnitTestEventRef? ResultEvent { get; set; }
    public UnitTestEventRef? AggregationEvent { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastUpdatedAt { get; private set; }
    public UnitTestEventRef? CreationEvent { get; private set; }

    public RunNodeBuilder GetOrCreateChild(
        UnitTestNodeKind kind,
        string name,
        UnitTestNodeId id,
        DateTimeOffset createdAt)
    {
        var existing = children.FirstOrDefault(child => child.Kind == kind && child.Name == name);
        if (existing is not null)
        {
            return existing;
        }

        var child = new RunNodeBuilder(id, name, kind, createdAt);
        children.Add(child);
        return child;
    }

    public void Touch(UnitTestEventEntry entry)
    {
        CreationEvent ??= new UnitTestEventRef(entry.SequenceId);
        LastUpdatedAt = entry.Timestamp;
    }

    public void AddFinding(UnitTestFinding finding)
    {
        findings.Add(finding);
    }

    public void PropagateResultFromChildren()
    {
        foreach (var child in children)
        {
            child.PropagateResultFromChildren();
        }

        if (Result == UnitTestResultState.Error
            || findings.Any(static finding => finding.Result == UnitTestResultState.Error
                && finding.Kind is UnitTestFindingKind.Assertion or UnitTestFindingKind.OwnerResult)
            || children.Any(static child => child.Result == UnitTestResultState.Error))
        {
            Result = UnitTestResultState.Error;
            return;
        }

        // Abort belongs to this owner's explicit PLC outcome, not child severity.
        if (PlcReportedResult == UnitTestResultState.Abort)
        {
            Result = UnitTestResultState.Abort;
            return;
        }

        if (Result == UnitTestResultState.Failed || children.Any(static child => child.Result == UnitTestResultState.Failed))
        {
            Result = UnitTestResultState.Failed;
            return;
        }

        if (Result == UnitTestResultState.NotEvaluated
            && children.Count > 0
            && children.All(static child => child.Result == UnitTestResultState.Passed))
        {
            Result = UnitTestResultState.Passed;
        }
    }

    public bool HasIncompleteRequiredEvidence(bool requireRunControlCompletion)
    {
        var isIncomplete = Kind == UnitTestNodeKind.RunControl
            ? CreationEvent is not null
                && (ResultEvent is null || (requireRunControlCompletion && CompletionEvent is null))
            : CompletionEvent is not null && ResultEvent is null;
        if (isIncomplete)
        {
            return true;
        }

        return children.Any(static child => child.HasIncompleteRequiredEvidence(requireRunControlCompletion: false));
    }

    public UnitTestRunNode ToSnapshot(
        UnitTestResultState rootResult,
        EvidenceState epochEvidenceState)
    {
        var evidenceState = DetermineEvidenceState(epochEvidenceState);
        return new UnitTestRunNode(
            Id,
            Name,
            Name,
            Kind,
            LifecycleState,
            Kind == UnitTestNodeKind.RunControl ? rootResult : Result,
            evidenceState,
            Aggregation,
            CreationEvent,
            CompletionEvent,
            ResultEvent,
            AggregationEvent,
            CreatedAt,
            LastUpdatedAt,
            Array.AsReadOnly(findings.ToArray()),
            Array.AsReadOnly(children.Select(child => child.ToSnapshot(child.Result, epochEvidenceState)).ToArray()))
        {
            PlcReportedResult = PlcReportedResult
        };
    }

    private EvidenceState DetermineEvidenceState(EvidenceState epochEvidenceState)
    {
        if (epochEvidenceState is EvidenceState.Incomplete or EvidenceState.Ambiguous or EvidenceState.Conflicting)
        {
            return epochEvidenceState;
        }

        if (findings.Any(static finding => finding.Kind == UnitTestFindingKind.Consistency))
        {
            return EvidenceState.Conflicting;
        }

        if (CreationEvent is null)
        {
            return EvidenceState.None;
        }

        return epochEvidenceState == EvidenceState.Complete
            ? EvidenceState.Complete
            : EvidenceState.Partial;
    }
}
