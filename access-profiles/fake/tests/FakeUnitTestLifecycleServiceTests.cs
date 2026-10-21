using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Fake;

public sealed class FakeUnitTestLifecycleServiceTests
{
    [Fact]
    public void Initial_observation_is_ready_idle()
    {
        var service = new FakeUnitTestLifecycleService();

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated,
            ready: true,
            running: false,
            completed: false,
            error: false);
    }

    [Fact]
    public async Task Start_accepts_command_and_publishes_running_observation()
    {
        var service = new FakeUnitTestLifecycleService();
        var observations = CaptureObservations(service);

        var result = await service.StartAsync(CreateContext(), CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.True(result.Value?.Accepted);
        Assert.Equal(UnitTestRunCommand.Start, result.Value?.Command);
        await WaitUntilAsync(() => service.CurrentObservation.Running);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Running,
            UnitTestResultState.NotEvaluated,
            ready: false,
            running: true,
            completed: false,
            error: false);
        Assert.Single(observations);
    }

    [Fact]
    public async Task Completed_run_is_explicit_evidence_after_start()
    {
        var service = new FakeUnitTestLifecycleService();
        await service.StartAsync(CreateContext(), CancellationToken.None);
        await WaitUntilAsync(() => service.CurrentObservation.Running);

        service.CompleteRun(UnitTestResultState.Failed);

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Failed,
            ready: false,
            running: false,
            completed: true,
            error: false);
    }

    [Fact]
    public async Task Abort_accepts_command_before_completed_abort_evidence()
    {
        var service = new FakeUnitTestLifecycleService();
        await service.StartAsync(CreateContext(), CancellationToken.None);
        await WaitUntilAsync(() => service.CurrentObservation.Running);

        var abort = await service.AbortAsync(CreateContext(), CancellationToken.None);

        Assert.True(abort.IsSuccessful);
        Assert.True(abort.Value?.Accepted);
        await WaitUntilAsync(() => service.CurrentObservation.State == UnitTestLifecycleState.Aborting);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Aborting,
            UnitTestResultState.NotEvaluated,
            ready: false,
            running: true,
            completed: false,
            error: false);

        service.CompleteAbort();

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Abort,
            ready: false,
            running: false,
            completed: true,
            error: false);
    }

    [Fact]
    public async Task Reset_accepts_command_before_ready_base_state_is_reached()
    {
        var service = new FakeUnitTestLifecycleService();
        await service.StartAsync(CreateContext(), CancellationToken.None);
        await WaitUntilAsync(() => service.CurrentObservation.Running);
        service.CompleteRun();

        var reset = await service.ResetAsync(CreateContext(), CancellationToken.None);

        Assert.True(reset.IsSuccessful);
        Assert.True(reset.Value?.Accepted);
        await WaitUntilAsync(() => service.CurrentObservation.State == UnitTestLifecycleState.Reset);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Reset,
            UnitTestResultState.NotEvaluated,
            ready: false,
            running: false,
            completed: false,
            error: false);

        service.CompleteReset();

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated,
            ready: true,
            running: false,
            completed: false,
            error: false);
    }

    [Fact]
    public async Task Rejected_command_does_not_change_current_observation()
    {
        var service = new FakeUnitTestLifecycleService
        {
            RejectNextCommand = true
        };

        var result = await service.StartAsync(CreateContext(), CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.False(result.Value?.Accepted);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated,
            ready: true,
            running: false,
            completed: false,
            error: false);
    }

    [Fact]
    public async Task Start_write_is_accepted_without_reaction_while_runcontrol_is_not_ready()
    {
        var service = new FakeUnitTestLifecycleService();
        await service.StartAsync(CreateContext(), CancellationToken.None);
        await WaitUntilAsync(() => service.CurrentObservation.Running);

        var secondStart = await service.StartAsync(CreateContext(), CancellationToken.None);
        await Task.Delay(25);

        Assert.True(secondStart.IsSuccessful);
        Assert.True(secondStart.Value?.Accepted);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Running,
            UnitTestResultState.NotEvaluated,
            ready: false,
            running: true,
            completed: false,
            error: false);
    }

    [Fact]
    public async Task Abort_write_is_accepted_without_reaction_when_no_run_is_active()
    {
        var service = new FakeUnitTestLifecycleService();

        var abort = await service.AbortAsync(CreateContext(), CancellationToken.None);
        await Task.Delay(25);

        Assert.True(abort.IsSuccessful);
        Assert.True(abort.Value?.Accepted);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated,
            ready: true,
            running: false,
            completed: false,
            error: false);
    }

    [Fact]
    public async Task Reset_is_accepted_after_completed_run_and_returns_ready_after_explicit_completion()
    {
        var service = new FakeUnitTestLifecycleService();
        await service.StartAsync(CreateContext(), CancellationToken.None);
        await WaitUntilAsync(() => service.CurrentObservation.Running);
        service.CompleteRun();

        var reset = await service.ResetAsync(CreateContext(), CancellationToken.None);

        Assert.True(reset.IsSuccessful);
        Assert.True(reset.Value?.Accepted);
        await WaitUntilAsync(() => service.CurrentObservation.State == UnitTestLifecycleState.Reset);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Reset,
            UnitTestResultState.NotEvaluated,
            ready: false,
            running: false,
            completed: false,
            error: false);

        service.CompleteReset();

        Assert.True((await service.StartAsync(CreateContext(), CancellationToken.None)).Value?.Accepted);
        await WaitUntilAsync(() => service.CurrentObservation.Running);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Running,
            UnitTestResultState.NotEvaluated,
            ready: false,
            running: true,
            completed: false,
            error: false);
    }

    [Fact]
    public async Task Auto_complete_run_uses_deterministic_fake_time()
    {
        var timeProvider = FakeTime.Create();
        var service = new FakeUnitTestLifecycleService(timeProvider)
        {
            AutoCompleteRunAfter = TimeSpan.FromSeconds(2)
        };

        await service.StartAsync(CreateContext(), CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => service.CurrentObservation.Running);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await YieldUntilIdleAsync();

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Running,
            UnitTestResultState.NotEvaluated,
            ready: false,
            running: true,
            completed: false,
            error: false);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => service.CurrentObservation.Completed);

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Passed,
            ready: false,
            running: false,
            completed: true,
            error: false);
    }

    [Fact]
    public async Task Auto_complete_reset_uses_deterministic_fake_time()
    {
        var timeProvider = FakeTime.Create();
        var service = new FakeUnitTestLifecycleService(timeProvider)
        {
            AutoCompleteResetAfter = TimeSpan.FromSeconds(2)
        };

        await service.StartAsync(CreateContext(), CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => service.CurrentObservation.Running);
        service.CompleteRun();
        await service.ResetAsync(CreateContext(), CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => service.CurrentObservation.State == UnitTestLifecycleState.Reset);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await YieldUntilIdleAsync();

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Reset,
            UnitTestResultState.NotEvaluated,
            ready: false,
            running: false,
            completed: false,
            error: false);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => service.CurrentObservation.Ready);

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated,
            ready: true,
            running: false,
            completed: false,
            error: false);
    }

    [Fact]
    public async Task Auto_complete_abort_uses_deterministic_fake_time()
    {
        var timeProvider = FakeTime.Create();
        var service = new FakeUnitTestLifecycleService(timeProvider)
        {
            AutoCompleteAbortAfter = TimeSpan.FromSeconds(2)
        };

        await service.StartAsync(CreateContext(), CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => service.CurrentObservation.Running);
        await service.AbortAsync(CreateContext(), CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => service.CurrentObservation.State == UnitTestLifecycleState.Aborting);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await YieldUntilIdleAsync();

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Aborting,
            UnitTestResultState.NotEvaluated,
            ready: false,
            running: true,
            completed: false,
            error: false);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => service.CurrentObservation.Completed);

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Abort,
            ready: false,
            running: false,
            completed: true,
            error: false);
    }

    [Fact]
    public async Task Reset_cancels_pending_auto_run_completion()
    {
        var timeProvider = FakeTime.Create();
        var service = new FakeUnitTestLifecycleService(timeProvider)
        {
            AutoCompleteRunAfter = TimeSpan.FromSeconds(2),
            AutoCompleteResetAfter = TimeSpan.FromSeconds(2)
        };

        await service.StartAsync(CreateContext(), CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => service.CurrentObservation.Running);
        await service.ResetAsync(CreateContext(), CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => service.CurrentObservation.State == UnitTestLifecycleState.Reset);
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => service.CurrentObservation.Ready);

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated,
            ready: true,
            running: false,
            completed: false,
            error: false);
    }

    [Fact]
    public async Task Held_command_stays_pending_until_explicitly_accepted()
    {
        var service = new FakeUnitTestLifecycleService
        {
            HoldNextCommand = true
        };

        var startTask = service.StartAsync(CreateContext(), CancellationToken.None);

        Assert.False(startTask.IsCompleted);
        Assert.Equal(UnitTestRunCommand.Start, service.PendingCommand);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated,
            ready: true,
            running: false,
            completed: false,
            error: false);

        service.CompletePendingCommandAccepted();
        var result = await startTask;

        Assert.True(result.IsSuccessful);
        Assert.True(result.Value?.Accepted);
        Assert.Null(service.PendingCommand);
        await WaitUntilAsync(() => service.CurrentObservation.Running);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Running,
            UnitTestResultState.NotEvaluated,
            ready: false,
            running: true,
            completed: false,
            error: false);
    }

    [Fact]
    public async Task Accepted_command_can_remain_without_lifecycle_reaction()
    {
        var service = new FakeUnitTestLifecycleService
        {
            CommandReactionDelay = null
        };

        var result = await service.StartAsync(CreateContext(), CancellationToken.None);
        await Task.Delay(25);

        Assert.True(result.IsSuccessful);
        Assert.True(result.Value?.Accepted);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated,
            ready: true,
            running: false,
            completed: false,
            error: false);
    }

    [Fact]
    public async Task Accepted_command_precedes_deterministic_lifecycle_reaction()
    {
        var timeProvider = FakeTime.Create();
        var service = new FakeUnitTestLifecycleService(timeProvider)
        {
            CommandReactionDelay = TimeSpan.FromSeconds(1)
        };

        var result = await service.StartAsync(CreateContext(), CancellationToken.None);

        Assert.True(result.Value?.Accepted);
        Assert.True(service.CurrentObservation.Ready);
        Assert.False(service.CurrentObservation.Running);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => service.CurrentObservation.Running);

        Assert.True(service.CurrentObservation.Running);
    }

    [Fact]
    public async Task Releasing_communication_cancels_a_pending_command_reaction()
    {
        var service = new FakeUnitTestLifecycleService
        {
            CommandReactionDelay = TimeSpan.FromMilliseconds(50)
        };

        var result = await service.StartAsync(CreateContext(), CancellationToken.None);
        await service.ReleaseCommunicationAsync(CancellationToken.None);
        await Task.Delay(75);

        Assert.True(result.Value?.Accepted);
        Assert.False(service.CurrentObservation.Running);
        Assert.Equal(UnitTestExecutionStatus.Unknown, service.CurrentObservation.ExecutionStatus);
    }

    [Fact]
    public async Task Held_command_times_out_when_acknowledgement_window_expires()
    {
        var timeProvider = FakeTime.Create();
        var service = new FakeUnitTestLifecycleService(timeProvider)
        {
            HoldNextCommand = true,
            CommandAcknowledgementTimeout = TimeSpan.FromSeconds(2)
        };

        var startTask = service.StartAsync(CreateContext(), CancellationToken.None);
        await YieldUntilIdleAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await YieldUntilIdleAsync();

        Assert.False(startTask.IsCompleted);
        Assert.Equal(UnitTestRunCommand.Start, service.PendingCommand);

        timeProvider.Advance(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => startTask.IsCompleted);
        var result = await startTask;

        Assert.Equal(OperationResultState.TimedOut, result.State);
        Assert.Null(result.Value);
        Assert.Null(service.PendingCommand);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated,
            ready: true,
            running: false,
            completed: false,
            error: false);
    }

    [Theory]
    [InlineData(UnitTestRunCommand.Start)]
    [InlineData(UnitTestRunCommand.Abort)]
    [InlineData(UnitTestRunCommand.Reset)]
    public async Task Held_command_acknowledgement_timeout_is_available_for_each_command(
        UnitTestRunCommand command)
    {
        var timeProvider = FakeTime.Create();
        var service = new FakeUnitTestLifecycleService(timeProvider)
        {
            CommandAcknowledgementTimeout = TimeSpan.FromSeconds(2)
        };

        if (command == UnitTestRunCommand.Abort)
        {
            await service.StartAsync(CreateContext(), CancellationToken.None);
        }

        service.HoldNextCommand = true;
        var commandTask = command switch
        {
            UnitTestRunCommand.Start => service.StartAsync(CreateContext(), CancellationToken.None),
            UnitTestRunCommand.Abort => service.AbortAsync(CreateContext(), CancellationToken.None),
            UnitTestRunCommand.Reset => service.ResetAsync(CreateContext(), CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
        };

        Assert.False(commandTask.IsCompleted);
        Assert.Equal(command, service.PendingCommand);

        timeProvider.Advance(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => commandTask.IsCompleted);
        var result = await commandTask;

        Assert.Equal(OperationResultState.TimedOut, result.State);
        Assert.Null(result.Value);
        Assert.Null(service.PendingCommand);
    }

    [Fact]
    public async Task Accepted_held_command_is_not_overwritten_by_later_acknowledgement_timeout()
    {
        var timeProvider = FakeTime.Create();
        var service = new FakeUnitTestLifecycleService(timeProvider)
        {
            HoldNextCommand = true,
            CommandAcknowledgementTimeout = TimeSpan.FromSeconds(2)
        };

        var startTask = service.StartAsync(CreateContext(), CancellationToken.None);
        service.CompletePendingCommandAccepted();
        var result = await startTask;

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        await YieldUntilIdleAsync();

        Assert.True(result.IsSuccessful);
        Assert.True(result.Value?.Accepted);
        Assert.Null(service.PendingCommand);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Running,
            UnitTestResultState.NotEvaluated,
            ready: false,
            running: true,
            completed: false,
            error: false);
    }

    [Fact]
    public async Task Held_command_can_be_rejected_without_changing_observation()
    {
        var service = new FakeUnitTestLifecycleService
        {
            HoldNextCommand = true
        };

        var resetTask = service.ResetAsync(CreateContext(), CancellationToken.None);

        Assert.False(resetTask.IsCompleted);
        service.CompletePendingCommandRejected();
        var result = await resetTask;

        Assert.True(result.IsSuccessful);
        Assert.False(result.Value?.Accepted);
        Assert.Null(service.PendingCommand);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated,
            ready: true,
            running: false,
            completed: false,
            error: false);
    }

    [Fact]
    public async Task Held_command_can_fail_without_command_result()
    {
        var service = new FakeUnitTestLifecycleService
        {
            HoldNextCommand = true
        };

        var abortTask = service.AbortAsync(CreateContext(), CancellationToken.None);

        Assert.False(abortTask.IsCompleted);
        service.FailPendingCommand("Abort command failed.");
        var result = await abortTask;

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Value);
        Assert.Null(service.PendingCommand);
    }

    [Fact]
    public void External_run_helpers_publish_observed_lifecycle_without_command()
    {
        var service = new FakeUnitTestLifecycleService();
        var observations = CaptureObservations(service);

        service.SimulateExternalRunStarted();
        service.SimulateExternalRunCompleted(UnitTestResultState.Failed);

        Assert.Equal(2, observations.Count);
        AssertObservation(
            observations[0],
            UnitTestLifecycleState.Running,
            UnitTestResultState.NotEvaluated,
            ready: false,
            running: true,
            completed: false,
            error: false);
        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Failed,
            ready: false,
            running: false,
            completed: true,
            error: false);
    }

    [Fact]
    public void External_abort_and_reset_helpers_publish_observed_lifecycle_without_command()
    {
        var service = new FakeUnitTestLifecycleService();

        service.SimulateExternalRunStarted();
        service.SimulateExternalAbortStarted();

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Aborting,
            UnitTestResultState.NotEvaluated,
            ready: false,
            running: true,
            completed: false,
            error: false);

        service.SimulateExternalAbortCompleted();

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Abort,
            ready: false,
            running: false,
            completed: true,
            error: false);

        service.SimulateExternalResetStarted();
        service.SimulateExternalResetCompleted();

        AssertObservation(
            service.CurrentObservation,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated,
            ready: true,
            running: false,
            completed: false,
            error: false);
    }

    [Fact]
    public void Explicit_observation_preserves_all_independent_execution_signals()
    {
        var service = new FakeUnitTestLifecycleService();
        var observation = new UnitTestRunControlObservation(
            UnitTestExecutionStatus.NotReady,
            UnitTestExecutionSignals.Uninitialized
                | UnitTestExecutionSignals.ConfigError
                | UnitTestExecutionSignals.Ready
                | UnitTestExecutionSignals.Running
                | UnitTestExecutionSignals.Completed
                | UnitTestExecutionSignals.Aborted
                | UnitTestExecutionSignals.Inactive
                | UnitTestExecutionSignals.Error,
            UnitTestLifecycleState.Uninitialized,
            UnitTestResultState.NotEvaluated);

        service.PublishObservation(observation);

        Assert.Equal(observation, service.CurrentObservation);
        Assert.True(service.CurrentObservation.Uninitialized);
        Assert.True(service.CurrentObservation.ConfigError);
        Assert.True(service.CurrentObservation.Ready);
        Assert.True(service.CurrentObservation.Running);
        Assert.True(service.CurrentObservation.Completed);
        Assert.True(service.CurrentObservation.Aborted);
        Assert.True(service.CurrentObservation.Inactive);
        Assert.True(service.CurrentObservation.Error);
    }

    [Fact]
    public void Config_error_signal_does_not_imply_general_error()
    {
        var service = new FakeUnitTestLifecycleService();

        service.PublishObservation(new UnitTestRunControlObservation(
            UnitTestExecutionStatus.NotReady,
            UnitTestExecutionSignals.ConfigError,
            UnitTestLifecycleState.Uninitialized,
            UnitTestResultState.NotEvaluated));

        Assert.True(service.CurrentObservation.ConfigError);
        Assert.False(service.CurrentObservation.Error);
    }

    [Fact]
    public async Task Accepted_command_result_does_not_require_or_replace_observation()
    {
        var service = new FakeUnitTestLifecycleService
        {
            NextCommandResult = OperationResult<TestRunCommandResult>.Success(
                new TestRunCommandResult(UnitTestRunCommand.Start, Accepted: true))
        };
        var initialObservation = service.CurrentObservation;

        var result = await service.StartAsync(CreateContext(), CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.True(result.Value!.Accepted);
        Assert.Equal(initialObservation, service.CurrentObservation);
    }

    [Fact]
    public async Task Release_clears_observation_and_cancels_pending_auto_transition()
    {
        var timeProvider = FakeTime.Create();
        var service = new FakeUnitTestLifecycleService(timeProvider)
        {
            AutoCompleteRunAfter = TimeSpan.FromSeconds(1)
        };
        await service.StartAsync(CreateContext(), CancellationToken.None);

        var result = await service.ReleaseCommunicationAsync(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        await YieldUntilIdleAsync();

        Assert.True(result.IsSuccessful);
        AssertSafeObservation(service.CurrentObservation);
    }

    [Fact]
    public async Task Communication_invalidation_clears_observation_to_safe_unknown_values()
    {
        var service = new FakeUnitTestLifecycleService();
        await service.StartAsync(CreateContext(), CancellationToken.None);

        service.SimulateLifecycleCommunicationInvalidated();

        AssertSafeObservation(service.CurrentObservation);
    }

    private static List<UnitTestRunControlObservation> CaptureObservations(FakeUnitTestLifecycleService service)
    {
        var observations = new List<UnitTestRunControlObservation>();
        service.RunControlObservationChanged += (_, args) => observations.Add(args.Observation);
        return observations;
    }

    private static UnitTestContext CreateContext()
    {
        var session = new CommunicationSession(new CommunicationSessionId("fake-session"), FakeTime.DefaultStart);
        var project = new PlcProjectContext(new ProjectContextId("fake-project"), "Fake PLC Project");
        var runtime = new PlcRuntimeContext(new RuntimeContextId("fake-runtime"), "Fake PLC Runtime");
        var projectTarget = new ProjectTargetRef(new ProjectTargetId("fake-project-target"), "Fake PLC", "CPU 1516");
        var runtimeTarget = new RuntimeTargetRef(new RuntimeTargetId("fake-runtime-target"), "Fake Runtime", "192.0.2.10");

        return new UnitTestContext(
            session,
            new ProjectExecutionContext(ProjectOwnershipMode.ApplicationManaged, project, projectTarget),
            runtime,
            runtimeTarget);
    }

    private static void AssertObservation(
        UnitTestRunControlObservation observation,
        UnitTestLifecycleState lifecycleState,
        UnitTestResultState result,
        bool ready,
        bool running,
        bool completed,
        bool error)
    {
        var expectedStatus = lifecycleState switch
        {
            UnitTestLifecycleState.Idle => UnitTestExecutionStatus.Idle,
            UnitTestLifecycleState.Running => UnitTestExecutionStatus.Running,
            UnitTestLifecycleState.Completed => UnitTestExecutionStatus.Completed,
            UnitTestLifecycleState.Aborting => UnitTestExecutionStatus.Aborting,
            UnitTestLifecycleState.Reset => UnitTestExecutionStatus.Reset,
            UnitTestLifecycleState.Error => UnitTestExecutionStatus.Error,
            _ => UnitTestExecutionStatus.Unknown
        };
        var expectedSignals = UnitTestExecutionSignals.None;
        if (ready)
        {
            expectedSignals |= UnitTestExecutionSignals.Ready;
        }

        if (running)
        {
            expectedSignals |= UnitTestExecutionSignals.Running;
        }

        if (completed)
        {
            expectedSignals |= UnitTestExecutionSignals.Completed;
        }

        if (result == UnitTestResultState.Abort)
        {
            expectedSignals |= UnitTestExecutionSignals.Aborted;
        }

        if (error)
        {
            expectedSignals |= UnitTestExecutionSignals.Error;
        }

        Assert.Equal(expectedStatus, observation.ExecutionStatus);
        Assert.Equal(expectedSignals, observation.ExecutionSignals);
        Assert.Equal(lifecycleState, observation.State);
        Assert.Equal(result, observation.Result);
        Assert.Equal(ready, observation.Ready);
        Assert.Equal(running, observation.Running);
        Assert.Equal(completed, observation.Completed);
        Assert.Equal(result == UnitTestResultState.Abort, observation.Aborted);
        Assert.Equal(error, observation.Error);
    }

    private static void AssertSafeObservation(UnitTestRunControlObservation observation)
    {
        Assert.Equal(UnitTestExecutionStatus.Unknown, observation.ExecutionStatus);
        Assert.Equal(UnitTestExecutionSignals.None, observation.ExecutionSignals);
        Assert.Equal(UnitTestLifecycleState.Unknown, observation.State);
        Assert.Equal(UnitTestResultState.NotEvaluated, observation.Result);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(1);
        }

        Assert.True(predicate());
    }

    private static async Task YieldUntilIdleAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Yield();
        }
    }
}
