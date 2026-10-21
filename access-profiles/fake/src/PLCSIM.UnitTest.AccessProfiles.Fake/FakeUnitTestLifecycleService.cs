using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Fake;

/// <summary>
/// Deterministic fake UnitTest lifecycle service for shared-core tests.
/// </summary>
public sealed class FakeUnitTestLifecycleService : FakeAccessProfileServiceBase, IUnitTestLifecycleService
{
    private TaskCompletionSource<OperationResult<TestRunCommandResult>>? pendingCommandCompletion;
    private Action? pendingCommandAccept;
    private CancellationTokenRegistration pendingCommandCancellation;
    private ITimer? pendingCommandTimeoutTimer;
    private readonly TimeProvider timeProvider;
    private int pendingCommandGeneration;
    private int transitionGeneration;
    private FakeRunControlState state = FakeRunControlState.Idle;

    public FakeUnitTestLifecycleService(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        CurrentObservation = CreateObservation(FakeRunControlState.Idle, UnitTestResultState.NotEvaluated);
    }

    public UnitTestRunControlObservation CurrentObservation { get; private set; }

    protected override AccessServiceKind GetServiceKind()
    {
        return AccessServiceKind.UnitTestLifecycle;
    }

    public bool RejectNextCommand { get; set; }

    public bool HoldNextCommand { get; set; }

    public TimeSpan? CommandAcknowledgementTimeout { get; set; }

    public TimeSpan? CommandReactionDelay { get; set; } = TimeSpan.FromMilliseconds(1);

    public TimeSpan? AutoCompleteRunAfter { get; set; }

    public UnitTestResultState AutoCompleteRunResult { get; set; } = UnitTestResultState.Passed;

    public TimeSpan? AutoCompleteAbortAfter { get; set; }

    public TimeSpan? AutoCompleteResetAfter { get; set; }

    public UnitTestRunCommand? PendingCommand { get; private set; }

    public OperationResult<TestRunCommandResult>? NextCommandResult { get; set; }

    public int ReleaseCommunicationCount { get; private set; }

    public event EventHandler<UnitTestRunControlObservationChangedEventArgs>? RunControlObservationChanged;

    public Task<OperationResult<TestRunCommandResult>> StartAsync(
        UnitTestContext context,
        CancellationToken cancellationToken)
    {
        return ExecuteCommandAsync(UnitTestRunCommand.Start, () =>
        {
            PublishInternalState(FakeRunControlState.Running, UnitTestResultState.NotEvaluated);
            ScheduleRunCompletionIfConfigured();
        }, cancellationToken);
    }

    public Task<OperationResult<TestRunCommandResult>> AbortAsync(
        UnitTestContext context,
        CancellationToken cancellationToken)
    {
        return ExecuteCommandAsync(UnitTestRunCommand.Abort, () =>
        {
            PublishInternalState(FakeRunControlState.Aborting, UnitTestResultState.NotEvaluated);
            ScheduleAbortCompletionIfConfigured();
        }, cancellationToken);
    }

    public Task<OperationResult<TestRunCommandResult>> ResetAsync(
        UnitTestContext context,
        CancellationToken cancellationToken)
    {
        return ExecuteCommandAsync(UnitTestRunCommand.Reset, () =>
        {
            PublishInternalState(FakeRunControlState.Resetting, UnitTestResultState.NotEvaluated);
            ScheduleResetCompletionIfConfigured();
        }, cancellationToken);
    }

    public Task<OperationResult> ReleaseCommunicationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseCommunicationCount++;
        ClearObservation();
        SetState(ServiceState.NotPrepared, "Fake UnitTest lifecycle communication released.");
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
    {
        return ReleaseCommunicationAsync(cancellationToken);
    }

    public void SimulateLifecycleCommunicationInvalidated()
    {
        ClearObservation();
        SetState(ServiceState.Invalidated, "Fake UnitTest lifecycle communication was invalidated.");
    }

    public void SimulateLifecycleCommunicationRevalidationRequired()
    {
        SetState(ServiceState.NeedsRevalidation, "Fake UnitTest lifecycle communication requires revalidation.");
    }

    public void SimulateLifecycleCommunicationRecovered()
    {
        SetState(ServiceState.Ready, "Fake UnitTest lifecycle communication recovered.");
    }

    public void PublishObservation(UnitTestRunControlObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Interlocked.Increment(ref transitionGeneration);
        SetObservation(observation);
        SetState(ServiceState.Ready, "Fake UnitTest lifecycle observation updated.");
    }

    public void SimulateExternalRunStarted()
    {
        PublishInternalState(FakeRunControlState.Running, UnitTestResultState.NotEvaluated);
    }

    public void SimulateExternalRunCompleted(UnitTestResultState result = UnitTestResultState.Passed)
    {
        CompleteRun(result);
    }

    public void SimulateExternalAbortStarted()
    {
        PublishInternalState(FakeRunControlState.Aborting, UnitTestResultState.NotEvaluated);
    }

    public void SimulateExternalAbortCompleted()
    {
        CompleteAbort();
    }

    public void SimulateExternalResetStarted()
    {
        PublishInternalState(FakeRunControlState.Resetting, UnitTestResultState.NotEvaluated);
    }

    public void SimulateExternalResetCompleted()
    {
        CompleteReset();
    }

    private void PublishInternalState(
        FakeRunControlState nextState,
        UnitTestResultState result)
    {
        state = nextState;
        Interlocked.Increment(ref transitionGeneration);
        SetObservation(CreateObservation(nextState, result));
        SetState(ServiceState.Ready, "Fake UnitTest lifecycle observation updated.");
    }

    public void CompleteRun(UnitTestResultState result = UnitTestResultState.Passed)
    {
        if (result is not (UnitTestResultState.Passed or UnitTestResultState.Failed or UnitTestResultState.Error))
        {
            throw new ArgumentOutOfRangeException(nameof(result), result, "Completed fake runs must use passed, failed, or error result evidence.");
        }

        PublishInternalState(FakeRunControlState.Completed, result);
    }

    public void CompleteAbort()
    {
        PublishInternalState(FakeRunControlState.Completed, UnitTestResultState.Abort);
    }

    public void CompleteReset()
    {
        PublishInternalState(FakeRunControlState.Idle, UnitTestResultState.NotEvaluated);
    }

    public void PublishNotReady()
    {
        PublishInternalState(FakeRunControlState.Resetting, UnitTestResultState.NotEvaluated);
    }

    public void CompletePendingCommandAccepted()
    {
        if (pendingCommandCompletion is null || PendingCommand is null || pendingCommandAccept is null)
        {
            throw new InvalidOperationException("No fake UnitTest lifecycle command is pending.");
        }

        var completion = pendingCommandCompletion;
        var command = PendingCommand.Value;
        var accept = pendingCommandAccept;
        ClearPendingCommand();

        ScheduleCommandReaction(command, accept);
        completion.SetResult(CreateAcceptedCommandResult(command));
    }

    public void CompletePendingCommandRejected()
    {
        if (pendingCommandCompletion is null || PendingCommand is null)
        {
            throw new InvalidOperationException("No fake UnitTest lifecycle command is pending.");
        }

        var completion = pendingCommandCompletion;
        var command = PendingCommand.Value;
        ClearPendingCommand();

        completion.SetResult(OperationResult<TestRunCommandResult>.Success(
            new TestRunCommandResult(command, Accepted: false)));
    }

    public void FailPendingCommand(string message)
    {
        if (pendingCommandCompletion is null)
        {
            throw new InvalidOperationException("No fake UnitTest lifecycle command is pending.");
        }

        var completion = pendingCommandCompletion;
        ClearPendingCommand();

        completion.SetResult(OperationResult<TestRunCommandResult>.Failure(message));
    }

    private Task<OperationResult<TestRunCommandResult>> ExecuteCommandAsync(
        UnitTestRunCommand command,
        Action accept,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (NextCommandResult is { } nextCommandResult)
        {
            NextCommandResult = null;
            return Task.FromResult(nextCommandResult);
        }

        if (HoldNextCommand)
        {
            if (pendingCommandCompletion is not null)
            {
                return Task.FromResult(OperationResult<TestRunCommandResult>.Failure("A fake UnitTest lifecycle command is already pending."));
            }

            HoldNextCommand = false;
            PendingCommand = command;
            pendingCommandAccept = accept;
            pendingCommandCompletion = new TaskCompletionSource<OperationResult<TestRunCommandResult>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var generation = Interlocked.Increment(ref pendingCommandGeneration);
            pendingCommandCancellation = cancellationToken.Register(CancelPendingCommand);
            ScheduleCommandAcknowledgementTimeoutIfConfigured(command, generation);
            return pendingCommandCompletion.Task;
        }

        if (RejectNextCommand)
        {
            RejectNextCommand = false;
            return Task.FromResult(OperationResult<TestRunCommandResult>.Success(
                new TestRunCommandResult(command, Accepted: false)));
        }

        ScheduleCommandReaction(command, accept);
        return Task.FromResult(CreateAcceptedCommandResult(command));
    }

    private static OperationResult<TestRunCommandResult> CreateAcceptedCommandResult(UnitTestRunCommand command)
    {
        return OperationResult<TestRunCommandResult>.Success(
            new TestRunCommandResult(command, Accepted: true));
    }

    private void ScheduleCommandReaction(UnitTestRunCommand command, Action reaction)
    {
        if (CommandReactionDelay is not { } delay)
        {
            return;
        }

        var generation = Volatile.Read(ref transitionGeneration);
        _ = ApplyCommandReactionAfterDelayAsync(command, reaction, generation, delay);
    }

    private async Task ApplyCommandReactionAfterDelayAsync(
        UnitTestRunCommand command,
        Action reaction,
        int generation,
        TimeSpan delay)
    {
        await Task.Delay(delay, timeProvider);
        if (Volatile.Read(ref transitionGeneration) == generation && CanApplyCommandReaction(command))
        {
            reaction();
        }
    }

    private bool CanApplyCommandReaction(UnitTestRunCommand command)
    {
        return command switch
        {
            UnitTestRunCommand.Start => CurrentObservation.ExecutionStatus == UnitTestExecutionStatus.Idle
                && CurrentObservation.Ready,
            UnitTestRunCommand.Abort => CurrentObservation.ExecutionStatus is UnitTestExecutionStatus.Starting
                or UnitTestExecutionStatus.Running
                or UnitTestExecutionStatus.Completing
                or UnitTestExecutionStatus.Aborting,
            UnitTestRunCommand.Reset => true,
            _ => false
        };
    }

    private void ScheduleCommandAcknowledgementTimeoutIfConfigured(
        UnitTestRunCommand command,
        int generation)
    {
        if (CommandAcknowledgementTimeout is not { } timeout)
        {
            return;
        }

        pendingCommandTimeoutTimer = timeProvider.CreateTimer(
            _ => CompletePendingCommandTimedOut(command, generation, timeout),
            state: null,
            dueTime: timeout,
            period: Timeout.InfiniteTimeSpan);
    }

    private void CompletePendingCommandTimedOut(
        UnitTestRunCommand command,
        int generation,
        TimeSpan timeout)
    {
        if (Volatile.Read(ref pendingCommandGeneration) != generation
            || pendingCommandCompletion is null
            || PendingCommand != command)
        {
            return;
        }

        var completion = pendingCommandCompletion;
        ClearPendingCommand();
        completion.TrySetResult(OperationResult<TestRunCommandResult>.TimedOut(
            $"Fake UnitTest lifecycle command '{command}' acknowledgement timed out after {timeout}."));
    }

    private void ScheduleRunCompletionIfConfigured()
    {
        if (AutoCompleteRunAfter is not { } delay)
        {
            return;
        }

        var generation = Volatile.Read(ref transitionGeneration);
        _ = CompleteRunAfterDelayAsync(generation, delay);
    }

    private async Task CompleteRunAfterDelayAsync(
        int generation,
        TimeSpan delay)
    {
        await Task.Delay(delay, timeProvider);
        if (Volatile.Read(ref transitionGeneration) == generation && state == FakeRunControlState.Running)
        {
            CompleteRun(AutoCompleteRunResult);
        }
    }

    private void ScheduleResetCompletionIfConfigured()
    {
        if (AutoCompleteResetAfter is not { } delay)
        {
            return;
        }

        var generation = Volatile.Read(ref transitionGeneration);
        _ = CompleteResetAfterDelayAsync(generation, delay);
    }

    private async Task CompleteResetAfterDelayAsync(
        int generation,
        TimeSpan delay)
    {
        await Task.Delay(delay, timeProvider);
        if (Volatile.Read(ref transitionGeneration) == generation && state == FakeRunControlState.Resetting)
        {
            CompleteReset();
        }
    }

    private void ScheduleAbortCompletionIfConfigured()
    {
        if (AutoCompleteAbortAfter is not { } delay)
        {
            return;
        }

        var generation = Volatile.Read(ref transitionGeneration);
        _ = CompleteAbortAfterDelayAsync(generation, delay);
    }

    private async Task CompleteAbortAfterDelayAsync(
        int generation,
        TimeSpan delay)
    {
        await Task.Delay(delay, timeProvider);
        if (Volatile.Read(ref transitionGeneration) == generation && state == FakeRunControlState.Aborting)
        {
            CompleteAbort();
        }
    }

    private void SetObservation(UnitTestRunControlObservation observation)
    {
        CurrentObservation = observation;
        RunControlObservationChanged?.Invoke(
            this,
            new UnitTestRunControlObservationChangedEventArgs(observation));
    }

    private void ClearObservation()
    {
        state = FakeRunControlState.Idle;
        Interlocked.Increment(ref transitionGeneration);
        SetObservation(CreateSafeObservation());
    }

    private static UnitTestRunControlObservation CreateSafeObservation()
    {
        return new UnitTestRunControlObservation(
            UnitTestExecutionStatus.Unknown,
            UnitTestExecutionSignals.None,
            UnitTestLifecycleState.Unknown,
            UnitTestResultState.NotEvaluated);
    }

    private static UnitTestRunControlObservation CreateObservation(
        FakeRunControlState state,
        UnitTestResultState result)
    {
        var observation = state switch
        {
            FakeRunControlState.Idle => new UnitTestRunControlObservation(
                UnitTestExecutionStatus.Idle,
                UnitTestExecutionSignals.Ready,
                UnitTestLifecycleState.Idle,
                result),
            FakeRunControlState.Running => new UnitTestRunControlObservation(
                UnitTestExecutionStatus.Running,
                UnitTestExecutionSignals.Running,
                UnitTestLifecycleState.Running,
                result),
            FakeRunControlState.Completed => new UnitTestRunControlObservation(
                UnitTestExecutionStatus.Completed,
                UnitTestExecutionSignals.Completed,
                UnitTestLifecycleState.Completed,
                result),
            FakeRunControlState.Aborting => new UnitTestRunControlObservation(
                UnitTestExecutionStatus.Aborting,
                UnitTestExecutionSignals.Running,
                UnitTestLifecycleState.Aborting,
                result),
            FakeRunControlState.Resetting => new UnitTestRunControlObservation(
                UnitTestExecutionStatus.Reset,
                UnitTestExecutionSignals.None,
                UnitTestLifecycleState.Reset,
                result),
            _ => CreateSafeObservation()
        };

        var resultSignals = result switch
        {
            UnitTestResultState.Abort => UnitTestExecutionSignals.Aborted,
            UnitTestResultState.Error => UnitTestExecutionSignals.Error,
            _ => UnitTestExecutionSignals.None
        };

        return observation with
        {
            ExecutionSignals = observation.ExecutionSignals | resultSignals
        };
    }

    private void ClearPendingCommand()
    {
        pendingCommandCompletion = null;
        pendingCommandAccept = null;
        PendingCommand = null;
        pendingCommandTimeoutTimer?.Dispose();
        pendingCommandTimeoutTimer = null;
        pendingCommandCancellation.Dispose();
        pendingCommandCancellation = default;
    }

    private void CancelPendingCommand()
    {
        var completion = pendingCommandCompletion;
        pendingCommandCompletion = null;
        pendingCommandAccept = null;
        PendingCommand = null;
        pendingCommandTimeoutTimer?.Dispose();
        pendingCommandTimeoutTimer = null;
        pendingCommandCancellation = default;
        completion?.TrySetCanceled();
    }

    private enum FakeRunControlState
    {
        Idle,
        Running,
        Completed,
        Aborting,
        Resetting
    }
}
