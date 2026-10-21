using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.Analysis;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.Tests.Core.TestLogging;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

public sealed class TestRunMonitorTests
{
    [Fact]
    public void Start_monitoring_waits_for_event_stream_epoch()
    {
        var time = FakeTime.Create();
        var monitor = new TestRunMonitor(time);

        var result = monitor.StartMonitoring(CreateContext(time.GetUtcNow()));

        Assert.True(result.IsSuccessful);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, monitor.Current);
        Assert.Equal(FakeTime.DefaultStart, monitor.MonitoringStartedAt);
        Assert.Null(monitor.RunStartedAt);
    }

    [Fact]
    public void Lifecycle_observation_does_not_create_run_epoch()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);

        var update = monitor.ApplyLifecycleUpdate(RunningObservation());

        Assert.Equal(TestRunMonitorUpdateKind.EvidenceUpdated, update.Kind);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, monitor.Current);
        Assert.Null(monitor.RunStartedAt);
        Assert.Equal(FakeTime.DefaultStart, monitor.LastLifecycleEvidenceAt);
    }

    [Fact]
    public void Monitor_traces_distinct_run_control_and_event_stream_observations_once()
    {
        var time = FakeTime.Create();
        var sink = new RecordingUnitTestActivityTraceSink();
        var monitor = new TestRunMonitor(time, logger: null, sink);
        monitor.StartMonitoring(CreateContext(time.GetUtcNow()));
        var lifecycle = RunningObservation();
        var eventStream = CreateEventStreamUpdate(currentRun: null);

        monitor.ApplyLifecycleUpdate(lifecycle);
        monitor.ApplyLifecycleUpdate(lifecycle);
        monitor.ApplyEventStreamUpdate(eventStream);
        monitor.ApplyEventStreamUpdate(eventStream);

        Assert.Single(sink.Activities, activity => activity.Payload is RunControlActivityPayload);
        var stream = Assert.Single(sink.Activities, activity => activity.Payload is EventStreamActivityPayload);
        Assert.Equal(new CommunicationSessionId("session"), stream.Correlation.CommunicationSessionId);
        Assert.Equal(new EventStreamContextId("session"), stream.Correlation.EventStreamContextId);
    }

    [Fact]
    public async Task Monitor_callbacks_do_not_hold_the_state_lock_needed_by_commands()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var commandCompleted = new ManualResetEventSlim();
        monitor.MonitorUpdateEventHandler += (_, _) =>
        {
            callbackEntered.Set();
            releaseCallback.Wait(TimeSpan.FromSeconds(5));
        };

        var updateTask = Task.Run(() => monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(currentRun: null)));
        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));

        var commandTask = Task.Run(() =>
        {
            monitor.BeginCommandLifecycleMonitoring(UnitTestRunCommand.Start);
            commandCompleted.Set();
        });

        var commandCompletedWhileCallbackWasBlocked = commandCompleted.Wait(TimeSpan.FromSeconds(2));
        releaseCallback.Set();
        await Task.WhenAll(updateTask, commandTask);

        Assert.True(
            commandCompletedWhileCallbackWasBlocked,
            "The monitor callback retained the state lock and blocked command lifecycle monitoring.");
    }

    [Fact]
    public async Task Concurrent_monitor_updates_preserve_state_commit_order_when_publication_is_blocked()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        var publishedKinds = new List<TestRunMonitorUpdateKind>();
        using var firstCallbackEntered = new ManualResetEventSlim();
        using var releaseFirstCallback = new ManualResetEventSlim();
        monitor.MonitorUpdateEventHandler += (_, args) =>
        {
            lock (publishedKinds)
            {
                publishedKinds.Add(args.Update.Kind);
            }

            if (args.Update.Kind == TestRunMonitorUpdateKind.EvidenceUpdated)
            {
                firstCallbackEntered.Set();
                releaseFirstCallback.Wait(TimeSpan.FromSeconds(5));
            }
        };

        var firstUpdate = Task.Run(() => monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(currentRun: null)));
        Assert.True(firstCallbackEntered.Wait(TimeSpan.FromSeconds(5)));

        var secondUpdate = Task.Run(() => monitor.InvalidateMonitoring("Concurrent invalidation."));
        await secondUpdate;
        releaseFirstCallback.Set();
        await firstUpdate;

        Assert.Equal(
            [TestRunMonitorUpdateKind.EvidenceUpdated, TestRunMonitorUpdateKind.Invalidated],
            publishedKinds);
    }

    [Fact]
    public async Task Blocked_monitor_publication_does_not_retain_concurrent_update_callers()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackCount = 0;
        monitor.MonitorUpdateEventHandler += (_, _) =>
        {
            if (Interlocked.Increment(ref callbackCount) == 1)
            {
                callbackEntered.Set();
                releaseCallback.Wait(TimeSpan.FromSeconds(5));
            }
        };

        var publisher = Task.Run(() => monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(currentRun: null)));
        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));

        var concurrentUpdates = Enumerable.Range(0, 16)
            .Select(index => Task.Run(() =>
            {
                if (index % 2 == 0)
                {
                    monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(currentRun: null));
                }
                else
                {
                    monitor.ApplyLifecycleUpdate(RunningObservation());
                }
            }))
            .ToArray();

        var concurrentCompletion = Task.WhenAll(concurrentUpdates);
        var callersCompletedWhilePublicationWasBlocked = await Task.WhenAny(
                concurrentCompletion,
                Task.Delay(TimeSpan.FromSeconds(2)))
            == concurrentCompletion;
        releaseCallback.Set();
        await publisher;
        await concurrentCompletion;

        Assert.True(
            callersCompletedWhilePublicationWasBlocked,
            "Concurrent monitor updates accumulated behind a blocked external callback.");
    }

    [Fact]
    public void Event_stream_epoch_snapshot_detects_boundary_without_start_command()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        time.Advance(TimeSpan.FromSeconds(3));

        var update = monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Running, UnitTestResultState.NotEvaluated, EvidenceState.Partial)));

        Assert.Equal(TestRunMonitorUpdateKind.EpochBoundaryDetected, update.Kind);
        Assert.Equal(TestRunMonitoringState.RunActive, monitor.Current);
        Assert.Equal(FakeTime.DefaultStart.AddSeconds(3), monitor.LastEventStreamEvidenceAt);
        Assert.Equal(FakeTime.DefaultStart.AddSeconds(3), monitor.RunStartedAt);
    }

    [Fact]
    public void Epoch_timeout_uses_local_anchor_observation_time_instead_of_plc_timestamp()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        using var diagnostics = new DiagnosticCollector(monitor);

        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(CreateRun(
            UnitTestRunState.Running,
            UnitTestResultState.NotEvaluated,
            EvidenceState.Partial,
            createdAt: FakeTime.DefaultStart.AddHours(-1))));

        time.Advance(TimeSpan.FromMinutes(9));

        Assert.Equal(TestRunMonitoringState.RunActive, monitor.Current);
        Assert.Equal(FakeTime.DefaultStart, monitor.RunStartedAt);
        Assert.DoesNotContain(
            diagnostics.Entries,
            diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.ObservationTimeout");

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(TestRunMonitoringState.TimedOut, monitor.Current);
    }

    [Fact]
    public void Explicit_event_buffer_reset_uses_existing_observation_timeout_semantics()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        var updates = new List<TestRunMonitorUpdate>();
        using var diagnostics = new DiagnosticCollector(monitor);
        monitor.MonitorUpdateEventHandler += (_, args) => updates.Add(args.Update);
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Running, UnitTestResultState.NotEvaluated, EvidenceState.Partial)));
        updates.Clear();

        monitor.TimeoutActiveObservation(
            "Test run evidence was discarded by an explicit EventBuffer reset.");

        Assert.Equal(TestRunMonitoringState.TimedOut, monitor.Current);
        Assert.Single(updates, update => update.Kind == TestRunMonitorUpdateKind.TimeoutDetected);
        Assert.Contains(
            diagnostics.Entries,
            diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.ObservationTimeout"
                && diagnostic.Message.Contains("EventBuffer reset", StringComparison.Ordinal));
    }

    [Fact]
    public void Completion_anchor_places_monitor_in_completion_pending()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        var run = CreateRun(UnitTestRunState.Running, UnitTestResultState.Passed, EvidenceState.Partial);
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(run));

        var update = monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Completing, UnitTestResultState.Passed, EvidenceState.Partial)));

        Assert.Equal(TestRunMonitorUpdateKind.StateChanged, update.Kind);
        Assert.Equal(TestRunMonitoringState.CompletionPending, monitor.Current);
        Assert.Equal(FakeTime.DefaultStart, monitor.CompletionDetectedAt);
    }

    [Fact]
    public void Completed_epoch_snapshot_terminates_monitor()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Running, UnitTestResultState.NotEvaluated, EvidenceState.Partial)));

        var update = monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Completed, UnitTestResultState.Passed, EvidenceState.Complete),
            bufferObservedEmpty: true));

        Assert.Equal(TestRunMonitorUpdateKind.TerminalStateReached, update.Kind);
        Assert.Equal(TestRunMonitoringState.Completed, monitor.Current);
    }

    [Fact]
    public void Aborted_epoch_snapshot_terminates_monitor_as_aborted()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);

        var update = monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Aborted, UnitTestResultState.Abort, EvidenceState.Complete),
            bufferObservedEmpty: true));

        Assert.Equal(TestRunMonitorUpdateKind.TerminalStateReached, update.Kind);
        Assert.Equal(TestRunMonitoringState.Aborted, monitor.Current);
    }

    [Fact]
    public void Run_epoch_baseline_is_not_republished_as_terminal_after_monitoring_restart()
    {
        var time = FakeTime.Create();
        var monitor = new TestRunMonitor(time);
        monitor.StartMonitoring(CreateContext(time.GetUtcNow()) with
        {
            RunEpochBaseline = new RunEpochId("epoch-1")
        });

        var update = monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Completed, UnitTestResultState.Passed, EvidenceState.Complete, "epoch-1"),
            bufferObservedEmpty: true));

        Assert.Equal(TestRunMonitorUpdateKind.EvidenceUpdated, update.Kind);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, monitor.Current);
    }

    [Fact]
    public void New_epoch_after_run_epoch_baseline_is_detected_normally()
    {
        var time = FakeTime.Create();
        var monitor = new TestRunMonitor(time);
        monitor.StartMonitoring(CreateContext(time.GetUtcNow()) with
        {
            RunEpochBaseline = new RunEpochId("epoch-1")
        });
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Completed, UnitTestResultState.Passed, EvidenceState.Complete, "epoch-1"),
            bufferObservedEmpty: true));

        var update = monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Running, UnitTestResultState.NotEvaluated, EvidenceState.Partial, "epoch-2")));

        Assert.Equal(TestRunMonitorUpdateKind.EpochBoundaryDetected, update.Kind);
        Assert.Equal(TestRunMonitoringState.RunActive, monitor.Current);
    }

    [Theory]
    [InlineData(UnitTestRunState.Completed, TestRunMonitoringState.Completed)]
    [InlineData(UnitTestRunState.Aborted, TestRunMonitoringState.Aborted)]
    [InlineData(UnitTestRunState.TimedOut, TestRunMonitoringState.TimedOut)]
    [InlineData(UnitTestRunState.Invalidated, TestRunMonitoringState.Invalidated)]
    public void Late_snapshots_preserve_terminal_epoch_but_not_block_a_new_epoch(
        UnitTestRunState terminal, TestRunMonitoringState expected)
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Running, UnitTestResultState.NotEvaluated, EvidenceState.Partial)));
        if (terminal == UnitTestRunState.TimedOut)
            monitor.TimeoutActiveObservation("Test timeout.");
        else if (terminal == UnitTestRunState.Invalidated)
            monitor.InvalidateMonitoring("Test invalidation.");
        else
            monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
                CreateRun(terminal, terminal == UnitTestRunState.Aborted ? UnitTestResultState.Abort : UnitTestResultState.Passed,
                    EvidenceState.Complete)));

        Assert.Equal(expected, monitor.Current);
        var startedAt = monitor.RunStartedAt;
        var completedAt = monitor.CompletionDetectedAt;
        foreach (var state in new[] { UnitTestRunState.Observing, UnitTestRunState.Running,
                     UnitTestRunState.Completing, UnitTestRunState.Completed, UnitTestRunState.Aborted,
                     UnitTestRunState.TimedOut, UnitTestRunState.Invalidated })
        {
            time.Advance(TimeSpan.FromSeconds(1));
            var update = monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
                CreateRun(state, state == UnitTestRunState.Aborted ? UnitTestResultState.Abort : UnitTestResultState.Passed,
                    EvidenceState.Partial)));

            Assert.Equal(expected, monitor.Current);
            Assert.Equal(TestRunMonitorUpdateKind.EvidenceUpdated, update.Kind);
            Assert.Equal(time.GetUtcNow(), monitor.LastEventStreamEvidenceAt);
            Assert.Equal(startedAt, monitor.RunStartedAt);
            Assert.Equal(completedAt, monitor.CompletionDetectedAt);
        }

        var next = monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Running, UnitTestResultState.NotEvaluated, EvidenceState.Partial, "epoch-2")));
        Assert.Equal(TestRunMonitorUpdateKind.EpochBoundaryDetected, next.Kind);
        Assert.Equal(TestRunMonitoringState.RunActive, monitor.Current);
    }

    [Theory]
    [InlineData(EventBufferReadState.SessionInvalid)]
    [InlineData(EventBufferReadState.ContractInvalid)]
    public void Real_communication_invalidation_is_not_hidden_by_terminal_snapshot_guard(EventBufferReadState readState)
    {
        var monitor = CreateStartedMonitor(FakeTime.Create());
        var run = CreateRun(UnitTestRunState.Completed, UnitTestResultState.Passed, EvidenceState.Complete);
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(run));

        var update = monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(run, readState: readState));

        Assert.Equal(TestRunMonitoringState.Invalidated, monitor.Current);
        Assert.Equal(TestRunMonitorUpdateKind.Invalidated, update.Kind);
    }

    [Fact]
    public void Reset_after_timeout_retires_old_snapshots_and_allows_the_next_run()
    {
        var monitor = CreateStartedMonitor(FakeTime.Create());
        var old = CreateRun(UnitTestRunState.Running, UnitTestResultState.NotEvaluated, EvidenceState.Partial);
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(old));
        monitor.TimeoutActiveObservation("Test timeout.");
        monitor.BeginCommandLifecycleMonitoring(UnitTestRunCommand.Reset);
        monitor.ApplyLifecycleUpdate(new(UnitTestExecutionStatus.Idle, UnitTestExecutionSignals.Ready,
            UnitTestLifecycleState.Idle, UnitTestResultState.NotEvaluated));
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(old));
        Assert.Equal(TestRunMonitoringState.WaitingForRun, monitor.Current);

        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Running, UnitTestResultState.NotEvaluated, EvidenceState.Partial, "epoch-2")));
        Assert.Equal(TestRunMonitoringState.RunActive, monitor.Current);
    }

    [Fact]
    public void A_new_epoch_can_follow_a_terminal_epoch()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Completed, UnitTestResultState.Passed, EvidenceState.Complete, "epoch-1"),
            bufferObservedEmpty: true));

        var update = monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Running, UnitTestResultState.NotEvaluated, EvidenceState.Partial, "epoch-2")));

        Assert.Equal(TestRunMonitorUpdateKind.EpochBoundaryDetected, update.Kind);
        Assert.Equal(TestRunMonitoringState.RunActive, monitor.Current);
    }

    [Fact]
    public void Invalid_event_stream_state_invalidates_monitoring()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);

        var update = monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            currentRun: null,
            readState: EventBufferReadState.SessionInvalid));

        Assert.Equal(TestRunMonitorUpdateKind.Invalidated, update.Kind);
        Assert.Equal(TestRunMonitoringState.Invalidated, monitor.Current);
    }

    [Fact]
    public void Start_command_reaction_monitoring_is_independent_from_epoch_detection()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        monitor.BeginCommandLifecycleMonitoring(UnitTestRunCommand.Start);

        var update = monitor.ApplyLifecycleUpdate(new UnitTestRunControlObservation(
            UnitTestExecutionStatus.Running,
            UnitTestExecutionSignals.None,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated));

        Assert.Equal(TestRunMonitorUpdateKind.EvidenceUpdated, update.Kind);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, monitor.Current);
        Assert.Null(monitor.RunStartedAt);
    }

    [Fact]
    public void Command_reaction_timeout_warns_without_timing_out_run_monitor()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        using var diagnostics = new DiagnosticCollector(monitor);
        monitor.BeginCommandLifecycleMonitoring(UnitTestRunCommand.Start);

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(TestRunMonitoringState.WaitingForRun, monitor.Current);
        Assert.Contains(diagnostics.Entries, diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.CommandReactionTimeout");
    }

    [Fact]
    public void Detailed_lifecycle_state_alone_does_not_complete_command_reaction_monitoring()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        using var diagnostics = new DiagnosticCollector(monitor);
        monitor.BeginCommandLifecycleMonitoring(UnitTestRunCommand.Start);

        monitor.ApplyLifecycleUpdate(new UnitTestRunControlObservation(
            UnitTestExecutionStatus.Idle,
            UnitTestExecutionSignals.None,
            UnitTestLifecycleState.Running,
            UnitTestResultState.NotEvaluated));
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Contains(diagnostics.Entries, diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.CommandReactionTimeout");
    }

    [Fact]
    public void Abort_signal_completes_abort_command_reaction_monitoring()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        using var diagnostics = new DiagnosticCollector(monitor);
        monitor.BeginCommandLifecycleMonitoring(UnitTestRunCommand.Abort);

        monitor.ApplyLifecycleUpdate(new UnitTestRunControlObservation(
            UnitTestExecutionStatus.Unknown,
            UnitTestExecutionSignals.Aborted,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated));
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.DoesNotContain(diagnostics.Entries, diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.CommandReactionTimeout");
    }

    [Fact]
    public void Reset_ready_reaction_does_not_depend_on_detailed_lifecycle_state()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        using var diagnostics = new DiagnosticCollector(monitor);
        monitor.BeginCommandLifecycleMonitoring(UnitTestRunCommand.Reset);

        monitor.ApplyLifecycleUpdate(new UnitTestRunControlObservation(
            UnitTestExecutionStatus.Idle,
            UnitTestExecutionSignals.Ready,
            UnitTestLifecycleState.Error,
            UnitTestResultState.NotEvaluated));
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.DoesNotContain(diagnostics.Entries, diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.CommandReactionTimeout");
    }

    [Fact]
    public void Contradictory_execution_signals_are_diagnosed_without_changing_monitor_state()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        using var diagnostics = new DiagnosticCollector(monitor);

        var update = monitor.ApplyLifecycleUpdate(new UnitTestRunControlObservation(
            UnitTestExecutionStatus.Completed,
            UnitTestExecutionSignals.Running | UnitTestExecutionSignals.Completed,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Passed));

        Assert.Equal(TestRunMonitorUpdateKind.EvidenceUpdated, update.Kind);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, monitor.Current);
        Assert.Contains(diagnostics.Entries, diagnostic => diagnostic.Code == "Workflow.RunControlObservation.ContradictorySignals");
    }

    [Fact]
    public void Completed_aborted_projection_is_not_diagnosed_as_contradictory()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        using var diagnostics = new DiagnosticCollector(monitor);

        monitor.ApplyLifecycleUpdate(new UnitTestRunControlObservation(
            UnitTestExecutionStatus.Completed,
            UnitTestExecutionSignals.Completed | UnitTestExecutionSignals.Aborted,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Abort));

        Assert.DoesNotContain(diagnostics.Entries, diagnostic => diagnostic.Code == "Workflow.RunControlObservation.ContradictorySignals");
    }

    [Fact]
    public void Reset_monitoring_retires_observed_epoch_and_waits_for_ready_idle()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Completed, UnitTestResultState.Passed, EvidenceState.Complete),
            bufferObservedEmpty: true));
        monitor.BeginCommandLifecycleMonitoring(UnitTestRunCommand.Reset);

        var resetting = monitor.ApplyLifecycleUpdate(new UnitTestRunControlObservation(
            UnitTestExecutionStatus.Reset,
            UnitTestExecutionSignals.None,
            UnitTestLifecycleState.Reset,
            UnitTestResultState.NotEvaluated));
        var ready = monitor.ApplyLifecycleUpdate(new UnitTestRunControlObservation(
            UnitTestExecutionStatus.Idle,
            UnitTestExecutionSignals.Ready,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated));

        Assert.Equal(TestRunMonitorUpdateKind.StateChanged, resetting.Kind);
        Assert.Equal(TestRunMonitorUpdateKind.EvidenceUpdated, ready.Kind);
        Assert.Equal(TestRunMonitoringState.WaitingForRun, monitor.Current);
        Assert.Null(monitor.RunStartedAt);
    }

    [Fact]
    public void Active_epoch_observation_timeout_sets_timed_out_state()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        using var diagnostics = new DiagnosticCollector(monitor);
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Running, UnitTestResultState.NotEvaluated, EvidenceState.Partial)));

        time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(TestRunMonitoringState.TimedOut, monitor.Current);
        Assert.Contains(diagnostics.Entries, diagnostic => diagnostic.Code == "Workflow.TestRunMonitor.ObservationTimeout");
    }

    [Fact]
    public void Completion_pending_timeout_reports_missing_final_evidence()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        using var diagnostics = new DiagnosticCollector(monitor);
        var run = CreateRun(UnitTestRunState.Running, UnitTestResultState.Passed, EvidenceState.Partial);
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(run));
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Completing, UnitTestResultState.Passed, EvidenceState.Partial)));

        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(TestRunMonitoringState.TimedOut, monitor.Current);
        Assert.Contains(
            diagnostics.Entries,
            diagnostic => diagnostic.Message.Contains("Final event-stream evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void Monitor_logs_epoch_and_terminal_updates()
    {
        var time = FakeTime.Create();
        var logger = new ListLogger<TestRunMonitor>();
        var monitor = new TestRunMonitor(time, logger);
        monitor.StartMonitoring(CreateContext(time.GetUtcNow()));

        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Running, UnitTestResultState.NotEvaluated, EvidenceState.Partial)));
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Completed, UnitTestResultState.Passed, EvidenceState.Complete),
            bufferObservedEmpty: true));

        Assert.Contains(logger.Entries, entry => entry.Message.Contains(nameof(TestRunMonitorUpdateKind.EpochBoundaryDetected), StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains(nameof(TestRunMonitorUpdateKind.TerminalStateReached), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_monitoring_clears_state_and_timing()
    {
        var time = FakeTime.Create();
        var monitor = CreateStartedMonitor(time);
        monitor.ApplyEventStreamUpdate(CreateEventStreamUpdate(
            CreateRun(UnitTestRunState.Running, UnitTestResultState.NotEvaluated, EvidenceState.Partial)));

        await monitor.StopMonitoringAsync(CancellationToken.None);

        Assert.Equal(TestRunMonitoringState.NotMonitoring, monitor.Current);
        Assert.Null(monitor.MonitoringStartedAt);
        Assert.Null(monitor.RunStartedAt);
        Assert.Null(monitor.LastLifecycleEvidenceAt);
        Assert.Null(monitor.LastEventStreamEvidenceAt);
    }

    private static TestRunMonitor CreateStartedMonitor(TimeProvider time)
    {
        var monitor = new TestRunMonitor(time);
        monitor.StartMonitoring(CreateContext(time.GetUtcNow()));
        return monitor;
    }

    private static TestRunMonitoringContext CreateContext(DateTimeOffset startedAt)
    {
        return new TestRunMonitoringContext(
            new CommunicationSessionId("session"),
            RunConfigurationDefaults.CreateDefaultTimeoutPolicy() with
            {
                UnitTestCommandReaction = TimeSpan.FromMinutes(1),
                TestRun = TimeSpan.FromMinutes(10),
                FinalEvent = TimeSpan.FromSeconds(10)
            },
            EventStreamSequenceBaseline: null,
            RunEpochBaseline: null,
            startedAt);
    }

    private static TestRunEventStreamUpdate CreateEventStreamUpdate(
        UnitTestRunEpochSnapshot? currentRun,
        bool bufferObservedEmpty = false,
        EventBufferReadState readState = EventBufferReadState.Valid)
    {
        return new TestRunEventStreamUpdate(
            new EventStreamState(
                new CommunicationSessionId("session"),
                IsMonitoring: true,
                readState,
                EventStreamBatchReleaseState.Completed,
                bufferObservedEmpty,
                LostEventCount: 0),
            currentRun);
    }

    private static UnitTestRunEpochSnapshot CreateRun(
        UnitTestRunState state,
        UnitTestResultState result,
        EvidenceState evidenceState,
        string epochId = "epoch-1",
        DateTimeOffset? createdAt = null)
    {
        var timestamp = createdAt ?? FakeTime.DefaultStart;
        var root = new UnitTestRunNode(
            new UnitTestNodeId($"{epochId}:run-control"),
            "RunControl",
            "RunControl",
            UnitTestNodeKind.RunControl,
            state == UnitTestRunState.Completed ? UnitTestLifecycleState.Completed : UnitTestLifecycleState.Starting,
            result,
            evidenceState,
            Aggregation: null,
            CreationEvent: new UnitTestEventRef(1),
            CompletionEvent: state == UnitTestRunState.Completed ? new UnitTestEventRef(3) : null,
            ResultEvent: result == UnitTestResultState.NotEvaluated ? null : new UnitTestEventRef(2),
            AggregationEvent: null,
            timestamp,
            timestamp,
            Findings: Array.Empty<UnitTestFinding>(),
            Children: Array.Empty<UnitTestRunNode>());
        return new UnitTestRunEpochSnapshot(
            new RunEpochId(epochId),
            state,
            result,
            evidenceState,
            timestamp,
            timestamp,
            Array.Empty<UnitTestEventEntry>(),
            Array.Empty<UnitTestEventRef>(),
            root);
    }

    private static UnitTestRunControlObservation RunningObservation()
    {
        return new UnitTestRunControlObservation(
            UnitTestExecutionStatus.Running,
            UnitTestExecutionSignals.Running,
            UnitTestLifecycleState.Running,
            UnitTestResultState.NotEvaluated);
    }

    private sealed class DiagnosticCollector : IDisposable
    {
        private readonly TestRunMonitor monitor;

        public DiagnosticCollector(TestRunMonitor monitor)
        {
            this.monitor = monitor;
            monitor.DiagnosticEventHandler += OnDiagnostic;
        }

        public List<PLCSIM.UnitTest.Common.Diagnostics.Diagnostic> Entries { get; } = [];

        public void Dispose()
        {
            monitor.DiagnosticEventHandler -= OnDiagnostic;
        }

        private void OnDiagnostic(object? sender, PLCSIM.UnitTest.Common.Diagnostics.DiagnosticEventArgs args)
        {
            Entries.Add(args.Diagnostic);
        }
    }
}
