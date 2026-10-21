using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Analysis;

namespace PLCSIM.UnitTest.Core.Workflow;

/// <summary>
/// Owns shared-core timing and coarse state for one observed UnitTest run cycle.
/// </summary>
public sealed class TestRunMonitor : IAsyncDisposable
{
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TestRunMonitor> logger;
    private readonly IUnitTestActivityTraceSink? activityTraceSink;
    private readonly object syncRoot = new();
    private readonly Queue<PendingNotification> pendingNotifications = new();
    private bool notificationDrainActive;
    private TestRunMonitoringContext? context;
    private ITimer? timer;
    private TestRunLifecycleExpectation lifecycleExpectation = TestRunLifecycleExpectation.None;
    private DateTimeOffset? lifecycleExpectationStartedAt;
    private RunEpochId? observedEpochId;
    private RunEpochId? retiredEpochId;
    private UnitTestRunControlObservation? lastTracedLifecycleObservation;
    private EventStreamState? lastTracedEventStreamState;

    public TestRunMonitor(
        CoreServiceContext coreContext)
        : this(coreContext, activityTraceSink: null)
    {
    }

    public TestRunMonitor(
        CoreServiceContext coreContext,
        IUnitTestActivityTraceSink? activityTraceSink)
        : this(
            coreContext?.TimeProvider ?? throw new ArgumentNullException(nameof(coreContext)),
            coreContext.LoggerFactory.CreateLogger<TestRunMonitor>(),
            activityTraceSink)
    {
    }

    public TestRunMonitor(
        TimeProvider timeProvider,
        ILogger<TestRunMonitor>? logger = null,
        IUnitTestActivityTraceSink? activityTraceSink = null)
    {
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<TestRunMonitor>.Instance;
        this.activityTraceSink = activityTraceSink;
    }

    public event EventHandler<DiagnosticEventArgs>? DiagnosticEventHandler;

    public event EventHandler<TestRunMonitorUpdateEventArgs>? MonitorUpdateEventHandler;

    public TestRunMonitoringState Current { get; private set; } = TestRunMonitoringState.NotMonitoring;

    public DateTimeOffset? MonitoringStartedAt { get; private set; }

    public DateTimeOffset? RunStartedAt { get; private set; }

    public DateTimeOffset? LastLifecycleEvidenceAt { get; private set; }

    public DateTimeOffset? LastEventStreamEvidenceAt { get; private set; }

    public DateTimeOffset? CompletionDetectedAt { get; private set; }

    public TimeSpan? MonitoringElapsed => ElapsedSince(MonitoringStartedAt);

    public TimeSpan? RunElapsed => ElapsedSince(RunStartedAt);

    public TimeSpan? TimeSinceLastLifecycleEvidence => ElapsedSince(LastLifecycleEvidenceAt);

    public TimeSpan? TimeSinceLastEventStreamEvidence => ElapsedSince(LastEventStreamEvidenceAt);

    public OperationResult StartMonitoring(TestRunMonitoringContext monitoringContext)
    {
        if (Current != TestRunMonitoringState.NotMonitoring)
        {
            return OperationResult.Failure("Test run monitoring is already active.");
        }

        context = monitoringContext;
        MonitoringStartedAt = monitoringContext.ObservationStartedAt;
        RunStartedAt = null;
        LastLifecycleEvidenceAt = null;
        LastEventStreamEvidenceAt = null;
        CompletionDetectedAt = null;
        lifecycleExpectationStartedAt = null;
        observedEpochId = null;
        retiredEpochId = monitoringContext.RunEpochBaseline;
        Current = TestRunMonitoringState.WaitingForRun;
        timer = timeProvider.CreateTimer(
            _ => EvaluateTimeouts(),
            null,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(250));

        logger.LogInformation(
            "Test run monitoring started. SessionId={SessionId}",
            monitoringContext.SessionId.Value);

        return OperationResult.Success("Test run monitoring started.");
    }

    public TestRunMonitorUpdate ApplyLifecycleUpdate(UnitTestRunControlObservation observation)
    {
        TestRunMonitorUpdate update;
        bool drainNotifications;
        lock (syncRoot)
        {
            update = ApplyLifecycleUpdateCore(observation);
            drainNotifications = TryBeginNotificationDrainCore();
        }

        DrainNotifications(drainNotifications);
        return update;
    }

    public TestRunMonitorUpdate ApplyEventStreamUpdate(TestRunEventStreamUpdate eventStreamUpdate)
    {
        TestRunMonitorUpdate update;
        bool drainNotifications;
        lock (syncRoot)
        {
            update = ApplyEventStreamUpdateCore(eventStreamUpdate);
            drainNotifications = TryBeginNotificationDrainCore();
        }

        DrainNotifications(drainNotifications);
        return update;
    }

    private TestRunMonitorUpdate ApplyLifecycleUpdateCore(UnitTestRunControlObservation observation)
    {
        TraceLifecycleObservationCore(observation);
        ReportContradictoryExecutionSignals(observation);

        if (context is null)
        {
            return TestRunMonitorUpdate.NoChange(Current);
        }

        LastLifecycleEvidenceAt = timeProvider.GetUtcNow();
        var previous = Current;

        if (lifecycleExpectation == TestRunLifecycleExpectation.ResetReady)
        {
            ResetObservedRunState();
            Current = TestRunMonitoringState.WaitingForRun;
            if (IsResetReadyReaction(observation))
            {
                CompleteLifecycleExpectation();
            }

            return QueueUpdate(Current == previous
                ? TestRunMonitorUpdateKind.EvidenceUpdated
                : TestRunMonitorUpdateKind.StateChanged);
        }

        if (observation.ExecutionStatus == UnitTestExecutionStatus.Reset
            && observedEpochId is not null)
        {
            ResetObservedRunState();
            Current = TestRunMonitoringState.WaitingForRun;
            return CreateInvalidatedUpdate("Observed RunEpoch was reset before normal completion.");
        }

        if (lifecycleExpectation == TestRunLifecycleExpectation.StartReaction
            && IsStartReaction(observation))
        {
            CompleteLifecycleExpectation();
        }
        else if (lifecycleExpectation == TestRunLifecycleExpectation.AbortReaction
            && IsAbortReaction(observation))
        {
            CompleteLifecycleExpectation();
        }

        return QueueUpdate(TestRunMonitorUpdateKind.EvidenceUpdated);
    }

    private TestRunMonitorUpdate ApplyEventStreamUpdateCore(TestRunEventStreamUpdate eventStreamUpdate)
    {
        TraceEventStreamStateCore(eventStreamUpdate.State);
        if (context is null)
        {
            return TestRunMonitorUpdate.NoChange(Current);
        }

        var eventStreamState = eventStreamUpdate.State;
        LastEventStreamEvidenceAt = timeProvider.GetUtcNow();

        if (eventStreamState.LastReadState is EventBufferReadState.ContractInvalid or EventBufferReadState.SessionInvalid)
        {
            Current = TestRunMonitoringState.Invalidated;
            return CreateInvalidatedUpdate($"Event stream read state invalidated monitoring: {eventStreamState.LastReadState}.");
        }

        var runEpoch = eventStreamUpdate.CurrentRun;
        if (runEpoch is null)
        {
            return QueueUpdate(TestRunMonitorUpdateKind.EvidenceUpdated);
        }

        if (retiredEpochId == runEpoch.EpochId)
        {
            return QueueUpdate(TestRunMonitorUpdateKind.EvidenceUpdated);
        }

        var newEpoch = observedEpochId != runEpoch.EpochId;
        // A delayed operation result must not replace this epoch's terminal observation.
        // Communication evidence above is still processed; a new epoch can start below.
        if (!newEpoch && IsTerminal(Current))
        {
            return QueueUpdate(TestRunMonitorUpdateKind.EvidenceUpdated);
        }

        if (newEpoch)
        {
            retiredEpochId = null;
            observedEpochId = runEpoch.EpochId;
            RunStartedAt = timeProvider.GetUtcNow();
            CompletionDetectedAt = null;
        }

        var previous = Current;
        switch (runEpoch.State)
        {
            case UnitTestRunState.Observing:
            case UnitTestRunState.Running:
                Current = TestRunMonitoringState.RunActive;
                break;
            case UnitTestRunState.Completing:
                CompletionDetectedAt ??= timeProvider.GetUtcNow();
                Current = TestRunMonitoringState.CompletionPending;
                break;
            case UnitTestRunState.Completed:
                CompletionDetectedAt ??= timeProvider.GetUtcNow();
                Current = runEpoch.Result == UnitTestResultState.Abort
                    ? TestRunMonitoringState.Aborted
                    : TestRunMonitoringState.Completed;
                return QueueUpdate(TestRunMonitorUpdateKind.TerminalStateReached);
            case UnitTestRunState.Aborted:
                CompletionDetectedAt ??= timeProvider.GetUtcNow();
                Current = TestRunMonitoringState.Aborted;
                return QueueUpdate(TestRunMonitorUpdateKind.TerminalStateReached);
            case UnitTestRunState.TimedOut:
                Current = TestRunMonitoringState.TimedOut;
                return QueueUpdate(TestRunMonitorUpdateKind.TimeoutDetected);
            case UnitTestRunState.Invalidated:
                Current = TestRunMonitoringState.Invalidated;
                return CreateInvalidatedUpdate("RunEpoch event evidence was invalidated.");
        }

        if (newEpoch)
        {
            return QueueUpdate(TestRunMonitorUpdateKind.EpochBoundaryDetected);
        }

        return QueueUpdate(Current == previous
            ? TestRunMonitorUpdateKind.EvidenceUpdated
            : TestRunMonitorUpdateKind.StateChanged);
    }

    public void BeginCommandLifecycleMonitoring(UnitTestRunCommand command)
    {
        lock (syncRoot)
        {
            lifecycleExpectation = ToLifecycleExpectation(command);
            lifecycleExpectationStartedAt = timeProvider.GetUtcNow();
            logger.LogInformation(
                "Test run monitor command lifecycle expectation started. Command={Command}, Expectation={Expectation}, State={State}",
                command,
                lifecycleExpectation,
                Current);
        }
    }

    internal void RecordLifecycleObservation(UnitTestRunControlObservation observation)
    {
        lock (syncRoot)
        {
            TraceLifecycleObservationCore(observation);
        }
    }

    public void CancelCommandLifecycleMonitoring(UnitTestRunCommand command)
    {
        lock (syncRoot)
        {
            var expected = ToLifecycleExpectation(command);

            if (lifecycleExpectation == expected)
            {
                lifecycleExpectation = TestRunLifecycleExpectation.None;
                lifecycleExpectationStartedAt = null;
                logger.LogInformation(
                    "Test run monitor command lifecycle expectation cancelled. Command={Command}, PreviousExpectation={Expectation}, State={State}",
                    command,
                    expected,
                    Current);
            }
        }
    }

    public Task StopMonitoringAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        context = null;
        timer?.Dispose();
        timer = null;
        lifecycleExpectation = TestRunLifecycleExpectation.None;
        lifecycleExpectationStartedAt = null;
        observedEpochId = null;
        retiredEpochId = null;
        Current = TestRunMonitoringState.NotMonitoring;
        MonitoringStartedAt = null;
        RunStartedAt = null;
        LastLifecycleEvidenceAt = null;
        LastEventStreamEvidenceAt = null;
        CompletionDetectedAt = null;

        logger.LogInformation("Test run monitoring stopped.");
        return Task.CompletedTask;
    }

    public TestRunMonitorUpdate InvalidateMonitoring(string message)
    {
        TestRunMonitorUpdate update;
        bool drainNotifications;
        lock (syncRoot)
        {
            if (context is null || IsTerminal(Current))
            {
                return TestRunMonitorUpdate.NoChange(Current);
            }

            Current = TestRunMonitoringState.Invalidated;
            update = CreateInvalidatedUpdate(message);
            drainNotifications = TryBeginNotificationDrainCore();
        }

        DrainNotifications(drainNotifications);
        return update;
    }

    public void TimeoutActiveObservation(string message)
    {
        bool drainNotifications;
        lock (syncRoot)
        {
            if (context is null
                || Current is not (TestRunMonitoringState.WaitingForRun
                    or TestRunMonitoringState.RunActive
                    or TestRunMonitoringState.CompletionPending))
            {
                return;
            }

            ApplyObservationTimeout(message);
            drainNotifications = TryBeginNotificationDrainCore();
        }

        DrainNotifications(drainNotifications);
    }

    public ValueTask DisposeAsync()
    {
        timer?.Dispose();
        timer = null;
        return ValueTask.CompletedTask;
    }

    private void EvaluateTimeouts()
    {
        bool drainNotifications;
        lock (syncRoot)
        {
            if (context is null || IsTerminal(Current))
            {
                return;
            }

            var now = timeProvider.GetUtcNow();
            if (lifecycleExpectation != TestRunLifecycleExpectation.None
                && lifecycleExpectationStartedAt is { } expectationStartedAt
                && now - expectationStartedAt >= context.TimeoutPolicy.UnitTestCommandReaction)
            {
                ApplyCommandReactionTimeout(lifecycleExpectation);
            }
            else if (Current == TestRunMonitoringState.RunActive
                && context.TimeoutPolicy.TestRun is { } runTimeout
                && RunStartedAt is { } runStartedAt
                && now - runStartedAt >= runTimeout)
            {
                ApplyObservationTimeout("Test run completion observation was not received before timeout.");
            }
            else if (Current == TestRunMonitoringState.CompletionPending
                && context.TimeoutPolicy.FinalEvent is { } finalEventTimeout
                && CompletionDetectedAt is { } completionDetectedAt
                && now - completionDetectedAt >= finalEventTimeout)
            {
                ApplyObservationTimeout("Final event-stream evidence observation was not received before timeout.");
            }

            drainNotifications = TryBeginNotificationDrainCore();
        }

        DrainNotifications(drainNotifications);
    }

    private void ApplyCommandReactionTimeout(TestRunLifecycleExpectation expectation)
    {
        CompleteLifecycleExpectation();
        logger.LogWarning(
            "UnitTest command reaction observation timed out. Expectation={Expectation}, MonitorState={State}",
            expectation,
            Current);
        var diagnostic = CreateDiagnostic(
            "Workflow.TestRunMonitor.CommandReactionTimeout",
            LogLevel.Warning,
            $"Expected RunControl lifecycle reaction '{expectation}' was not observed before timeout.");
        var update = new TestRunMonitorUpdate(
            TestRunMonitorUpdateKind.EvidenceUpdated,
            Current,
            Diagnostics: [diagnostic]);
        QueueNotificationCore(diagnostic, update);
    }

    private void ApplyObservationTimeout(string message)
    {
        Current = TestRunMonitoringState.TimedOut;
        lifecycleExpectation = TestRunLifecycleExpectation.None;
        lifecycleExpectationStartedAt = null;
        logger.LogWarning(
            "Test run observation timed out. State={State}, Message={Message}",
            Current,
            message);
        var diagnostic = CreateDiagnostic("Workflow.TestRunMonitor.ObservationTimeout", LogLevel.Error, message);
        var update = new TestRunMonitorUpdate(
            TestRunMonitorUpdateKind.TimeoutDetected,
            Current,
            Diagnostics: [diagnostic]);
        QueueNotificationCore(diagnostic, update);
    }

    private void ResetObservedRunState()
    {
        RunStartedAt = null;
        CompletionDetectedAt = null;
        retiredEpochId = observedEpochId;
        observedEpochId = null;
    }

    private TestRunMonitorUpdate CreateUpdate(TestRunMonitorUpdateKind kind)
    {
        return new TestRunMonitorUpdate(
            kind,
            Current,
            Diagnostics: Array.Empty<Diagnostic>());
    }

    private TestRunMonitorUpdate QueueUpdate(TestRunMonitorUpdateKind kind)
    {
        var update = CreateUpdate(kind);
        logger.Log(
            kind == TestRunMonitorUpdateKind.EvidenceUpdated ? LogLevel.Debug : LogLevel.Information,
            "Test run monitor update. Kind={Kind}, State={State}, LifecycleExpectation={LifecycleExpectation}",
            kind,
            Current,
            lifecycleExpectation);
        QueueNotificationCore(diagnostic: null, update);
        return update;
    }

    private void QueueNotificationCore(Diagnostic? diagnostic, TestRunMonitorUpdate? update)
    {
        pendingNotifications.Enqueue(new PendingNotification(diagnostic, update));
    }

    private bool TryBeginNotificationDrainCore()
    {
        if (notificationDrainActive || pendingNotifications.Count == 0)
        {
            return false;
        }

        notificationDrainActive = true;
        return true;
    }

    private void DrainNotifications(bool drainNotifications)
    {
        if (!drainNotifications)
        {
            return;
        }

        try
        {
            while (true)
            {
                PendingNotification notification;
                lock (syncRoot)
                {
                    if (pendingNotifications.Count == 0)
                    {
                        notificationDrainActive = false;
                        return;
                    }

                    notification = pendingNotifications.Dequeue();
                }

                if (notification.Diagnostic is not null)
                {
                    DiagnosticEventHandler?.Invoke(this, new DiagnosticEventArgs(notification.Diagnostic));
                }

                if (notification.Update is not null)
                {
                    MonitorUpdateEventHandler?.Invoke(this, new TestRunMonitorUpdateEventArgs(notification.Update));
                }
            }
        }
        catch
        {
            lock (syncRoot)
            {
                notificationDrainActive = false;
            }

            throw;
        }
    }

    private void TraceLifecycleObservationCore(UnitTestRunControlObservation observation)
    {
        if (activityTraceSink is null || lastTracedLifecycleObservation == observation)
        {
            return;
        }

        lastTracedLifecycleObservation = observation;
        activityTraceSink.TryRecord(new RunControlActivityPayload(observation));
    }

    private void TraceEventStreamStateCore(EventStreamState state)
    {
        if (activityTraceSink is null || lastTracedEventStreamState == state)
        {
            return;
        }

        lastTracedEventStreamState = state;
        activityTraceSink.TryRecord(
            new EventStreamActivityPayload(state),
            new UnitTestActivityCorrelation(
                CommunicationSessionId: state.SessionId,
                EventStreamContextId: new EventStreamContextId(state.SessionId.Value)));
    }

    private TestRunMonitorUpdate CreateInvalidatedUpdate(string message)
    {
        logger.LogWarning(
            "Test run monitor invalidated. State={State}, Message={Message}",
            Current,
            message);
        var diagnostic = CreateDiagnostic("Workflow.TestRunMonitor.Invalidated", LogLevel.Error, message);
        var update = new TestRunMonitorUpdate(
            TestRunMonitorUpdateKind.Invalidated,
            Current,
            Diagnostics: [diagnostic]);
        QueueNotificationCore(diagnostic, update);
        return update;
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

    private static bool IsTerminal(TestRunMonitoringState state)
    {
        return state is TestRunMonitoringState.Completed
            or TestRunMonitoringState.Aborted
            or TestRunMonitoringState.TimedOut
            or TestRunMonitoringState.Invalidated;
    }

    private TimeSpan? ElapsedSince(DateTimeOffset? timestamp)
    {
        return timestamp is null
            ? null
            : timeProvider.GetUtcNow() - timestamp.Value;
    }

    private void CompleteLifecycleExpectation()
    {
        lifecycleExpectation = TestRunLifecycleExpectation.None;
        lifecycleExpectationStartedAt = null;
    }

    private static TestRunLifecycleExpectation ToLifecycleExpectation(UnitTestRunCommand command)
    {
        return command switch
        {
            UnitTestRunCommand.Start => TestRunLifecycleExpectation.StartReaction,
            UnitTestRunCommand.Abort => TestRunLifecycleExpectation.AbortReaction,
            UnitTestRunCommand.Reset => TestRunLifecycleExpectation.ResetReady,
            _ => TestRunLifecycleExpectation.None
        };
    }

    private static bool IsStartReaction(UnitTestRunControlObservation observation)
    {
        return observation.ExecutionStatus is UnitTestExecutionStatus.Starting
                or UnitTestExecutionStatus.Running
                or UnitTestExecutionStatus.Completing
                or UnitTestExecutionStatus.Completed
            || observation.Running
            || observation.Completed;
    }

    private static bool IsAbortReaction(UnitTestRunControlObservation observation)
    {
        return observation.ExecutionStatus is UnitTestExecutionStatus.Aborting
                or UnitTestExecutionStatus.Aborted
                or UnitTestExecutionStatus.Completed
            || observation.Aborted
            || observation.Completed;
    }

    private static bool IsResetReadyReaction(UnitTestRunControlObservation observation)
    {
        return observation.Ready
            && !observation.Running
            && observation.ExecutionStatus == UnitTestExecutionStatus.Idle;
    }

    private void ReportContradictoryExecutionSignals(UnitTestRunControlObservation observation)
    {
        var contradictions = new List<string>();
        if (observation.Running && observation.Completed)
        {
            contradictions.Add("Running and Completed are both set");
        }

        if (observation.Running && observation.Inactive)
        {
            contradictions.Add("Running and Inactive are both set");
        }

        if (contradictions.Count == 0)
        {
            return;
        }

        var message =
            $"RunControl observation contains contradictory execution signals: {string.Join("; ", contradictions)}. " +
            $"ExecutionStatus={observation.ExecutionStatus}, Signals={observation.ExecutionSignals}, " +
            $"State={observation.State}, Result={observation.Result}.";
        logger.LogWarning("{Message}", message);
        var diagnostic = CreateDiagnostic(
            "Workflow.RunControlObservation.ContradictorySignals",
            LogLevel.Warning,
            message);
        QueueNotificationCore(diagnostic, update: null);
    }

    private sealed record PendingNotification(
        Diagnostic? Diagnostic,
        TestRunMonitorUpdate? Update);

}

public sealed record TestRunMonitoringContext(
    CommunicationSessionId SessionId,
    RunOperationTimeoutPolicy TimeoutPolicy,
    uint? EventStreamSequenceBaseline,
    RunEpochId? RunEpochBaseline,
    DateTimeOffset ObservationStartedAt);

public sealed record TestRunMonitorUpdate(
    TestRunMonitorUpdateKind Kind,
    TestRunMonitoringState State,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public static TestRunMonitorUpdate NoChange(TestRunMonitoringState state)
    {
        return new TestRunMonitorUpdate(
            TestRunMonitorUpdateKind.NoChange,
            state,
            Diagnostics: Array.Empty<Diagnostic>());
    }
}

public sealed record TestRunMonitorUpdateEventArgs(
    TestRunMonitorUpdate Update);

public sealed record TestRunEventStreamUpdate(
    EventStreamState State,
    UnitTestRunEpochSnapshot? CurrentRun);

public enum TestRunMonitoringState
{
    NotMonitoring,
    WaitingForRun,
    RunActive,
    CompletionPending,
    Completed,
    Aborted,
    TimedOut,
    Invalidated
}

public enum TestRunMonitorUpdateKind
{
    NoChange,
    MonitoringStarted,
    EpochBoundaryDetected,
    StateChanged,
    EvidenceUpdated,
    TimeoutDetected,
    TerminalStateReached,
    MonitoringStopped,
    Invalidated
}

public enum TestRunLifecycleExpectation
{
    None,
    StartReaction,
    AbortReaction,
    ResetReady
}
