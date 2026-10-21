using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Fake;

/// <summary>
/// Deterministic fake UnitTest event-stream service for shared-core tests.
/// </summary>
public class FakeUnitTestEventStreamService : FakeAccessProfileServiceBase, IUnitTestEventStreamService
{
    private readonly object pendingOperationGate = new();
    private TaskCompletionSource<OperationResult<EventStreamState>>? pendingStartMonitoringCompletion;
    private CancellationTokenRegistration pendingStartMonitoringCancellation;
    private TaskCompletionSource<OperationResult>? pendingReleaseCommunicationCompletion;
    private CancellationTokenRegistration pendingReleaseCommunicationCancellation;
    private uint nextSequenceId = 1;

    public EventStreamState CurrentMonitoringState { get; private set; } = new(
        new CommunicationSessionId("not-started"),
        IsMonitoring: false,
        EventBufferReadState.NotObserved,
        EventStreamBatchReleaseState.Idle,
        BufferObservedEmpty: false,
        LostEventCount: 0);

    protected override AccessServiceKind GetServiceKind()
    {
        return AccessServiceKind.UnitTestEventStream;
    }

    public bool HoldNextStartMonitoring { get; set; }

    public bool HoldNextReleaseCommunication { get; set; }

    public OperationResult<EventBufferResetCommandResult> EventBufferResetResult { get; set; } =
        OperationResult<EventBufferResetCommandResult>.Success(new EventBufferResetCommandResult(Accepted: true));

    public int EventBufferResetCount { get; private set; }

    public ushort InitialLostEventCount { get; set; }

    public bool IsStartMonitoringPending
    {
        get
        {
            lock (pendingOperationGate)
            {
                return pendingStartMonitoringCompletion is not null;
            }
        }
    }

    public bool IsReleaseCommunicationPending
    {
        get
        {
            lock (pendingOperationGate)
            {
                return pendingReleaseCommunicationCompletion is not null;
            }
        }
    }

    public int StopMonitoringCount { get; private set; }

    public int ReleaseCommunicationCount { get; private set; }

    public event EventHandler<EventBatchReceivedEventArgs>? EventBatchReceivedEventHandler;

    public event EventHandler<EventStreamStateChangedEventArgs>? MonitoringStateChangedEventHandler;

    public Task<OperationResult<EventStreamState>> StartMonitoringAsync(
        UnitTestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HoldNextStartMonitoring)
        {
            lock (pendingOperationGate)
            {
                if (pendingStartMonitoringCompletion is not null)
                {
                    return Task.FromResult(OperationResult<EventStreamState>.Failure(
                        "A fake UnitTest event-stream monitoring start operation is already pending."));
                }

                HoldNextStartMonitoring = false;
                var completion = new TaskCompletionSource<OperationResult<EventStreamState>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pendingStartMonitoringCompletion = completion;
                var registration = cancellationToken.Register(
                    () => CancelPendingStartMonitoring(completion, cancellationToken));
                if (ReferenceEquals(pendingStartMonitoringCompletion, completion))
                {
                    pendingStartMonitoringCancellation = registration;
                }
                else
                {
                    registration.Dispose();
                }

                return completion.Task;
            }
        }

        ApplyMonitoringState(new EventStreamState(
            context.Session.SessionId,
            IsMonitoring: true,
            InitialLostEventCount == 0 ? EventBufferReadState.Empty : EventBufferReadState.Valid,
            EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: true,
            LostEventCount: InitialLostEventCount));

        SetState(ServiceState.Ready, "Fake UnitTest event-stream monitoring started.");
        return Task.FromResult(OperationResult<EventStreamState>.Success(CurrentMonitoringState));
    }

    public virtual Task<OperationResult<EventBufferResetCommandResult>> ResetEventBufferAsync(
        UnitTestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        EventBufferResetCount++;
        return Task.FromResult(EventBufferResetResult);
    }

    public Task<OperationResult> StopMonitoringAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopMonitoringCount++;
        ApplyMonitoringState(CurrentMonitoringState with { IsMonitoring = false });
        SetState(ServiceState.Ready, "Fake UnitTest event-stream monitoring stopped.");
        return Task.FromResult(OperationResult.Success());
    }

    public virtual Task<OperationResult> ReleaseCommunicationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseCommunicationCount++;
        if (HoldNextReleaseCommunication)
        {
            lock (pendingOperationGate)
            {
                if (pendingReleaseCommunicationCompletion is not null)
                {
                    return Task.FromResult(OperationResult.Failure(
                        "A fake UnitTest event-stream communication release operation is already pending."));
                }

                HoldNextReleaseCommunication = false;
                var completion = new TaskCompletionSource<OperationResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pendingReleaseCommunicationCompletion = completion;
                var registration = cancellationToken.Register(
                    () => CancelPendingReleaseCommunication(completion, cancellationToken));
                if (ReferenceEquals(pendingReleaseCommunicationCompletion, completion))
                {
                    pendingReleaseCommunicationCancellation = registration;
                }
                else
                {
                    registration.Dispose();
                }

                return completion.Task;
            }
        }

        ApplyMonitoringState(CurrentMonitoringState with
        {
            IsMonitoring = false,
            BatchReleaseState = EventStreamBatchReleaseState.Idle
        });
        SetState(ServiceState.NotPrepared, "Fake UnitTest event-stream communication released.");
        return Task.FromResult(OperationResult.Success());
    }

    public virtual Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
    {
        return ReleaseCommunicationAsync(cancellationToken);
    }

    public void SimulateEventStreamCommunicationInvalidated()
    {
        ApplyMonitoringState(CurrentMonitoringState with
        {
            IsMonitoring = false,
            LastReadState = EventBufferReadState.SessionInvalid
        });
        SetState(ServiceState.Invalidated, "Fake UnitTest event-stream communication was invalidated.");
    }

    public void PublishBatch(UnitTestEventBatch batch)
    {
        if (batch.Events.Count > 0)
        {
            nextSequenceId = Math.Max(
                nextSequenceId,
                batch.Events.Max(static entry => entry.SequenceId) + 1);
        }

        ApplyMonitoringState(new EventStreamState(
            batch.SessionId,
            CurrentMonitoringState.IsMonitoring,
            batch.ReadState,
            EventStreamBatchReleaseState.Completed,
            batch.BufferObservedEmpty,
            batch.LostEventCount,
            BufferFull: false,
            batch.ProducerSequenceId,
            batch.DeletedSequenceId));

        EventBatchReceivedEventHandler?.Invoke(this, new EventBatchReceivedEventArgs(batch));
    }

    public UnitTestEventBatch PublishRunControlCompletionAnchor(
        bool bufferObservedEmpty = true,
        ushort lostEventCount = 0)
    {
        var sequenceId = NextSequenceId();
        var batch = CreateBatch(
            [
                CreateRunControlCompletionEntry(sequenceId)
            ],
            producerSequenceId: sequenceId,
            deletedSequenceId: bufferObservedEmpty ? sequenceId : sequenceId - 1,
            lostEventCount,
            bufferObservedEmpty,
            bufferObservedEmpty ? EventBufferReadState.Empty : EventBufferReadState.Valid);

        PublishBatch(batch);
        return batch;
    }

    public UnitTestEventBatch PublishRunControlStarted()
    {
        var sequenceId = NextSequenceId();
        var batch = CreateBatch(
            [
                CreateRunControlStateTransitionEntry(
                    sequenceId,
                    UnitTestLifecycleState.Starting,
                    UnitTestResultState.NotEvaluated,
                    "Fake RunControl epoch started.")
            ],
            producerSequenceId: sequenceId,
            deletedSequenceId: sequenceId - 1,
            lostEventCount: 0,
            bufferObservedEmpty: false,
            EventBufferReadState.Valid);

        PublishBatch(batch);
        return batch;
    }

    public UnitTestEventBatch PublishRunControlCompleted(
        UnitTestResultState result = UnitTestResultState.Passed,
        bool bufferObservedEmpty = true)
    {
        var resultSequenceId = NextSequenceId();
        var completionSequenceId = NextSequenceId();
        var batch = CreateBatch(
            [
                CreateRunControlResultEntry(
                    resultSequenceId,
                    result,
                    $"Fake RunControl result is {result}."),
                CreateRunControlStateTransitionEntry(
                    completionSequenceId,
                    UnitTestLifecycleState.Completed,
                    UnitTestResultState.NotEvaluated,
                    "Fake RunControl completed.")
            ],
            producerSequenceId: completionSequenceId,
            deletedSequenceId: bufferObservedEmpty ? completionSequenceId : completionSequenceId - 1,
            lostEventCount: 0,
            bufferObservedEmpty,
            bufferObservedEmpty ? EventBufferReadState.Empty : EventBufferReadState.Valid);

        PublishBatch(batch);
        return batch;
    }

    public UnitTestEventBatch PublishRunControlAbortCompleted(bool bufferObservedEmpty = true)
    {
        return PublishRunControlCompleted(UnitTestResultState.Abort, bufferObservedEmpty);
    }

    public UnitTestEventBatch PublishRunControlCompletedWithoutResult(bool bufferObservedEmpty = true)
    {
        return PublishRunControlCompletionAnchor(bufferObservedEmpty);
    }

    public UnitTestEventBatch PublishRunControlResultWithoutCompletionAnchor(UnitTestResultState result = UnitTestResultState.Passed)
    {
        var sequenceId = NextSequenceId();
        var batch = CreateBatch(
            [
                CreateRunControlResultEntry(
                    sequenceId,
                    result,
                    $"Fake RunControl result without completion anchor is {result}.")
            ],
            producerSequenceId: sequenceId,
            deletedSequenceId: sequenceId,
            lostEventCount: 0,
            bufferObservedEmpty: true,
            EventBufferReadState.Empty);

        PublishBatch(batch);
        return batch;
    }

    public UnitTestEventBatch PublishRunControlResetObserved()
    {
        var sequenceId = NextSequenceId();
        var batch = CreateBatch(
            [
                CreateRunControlStateTransitionEntry(
                    sequenceId,
                    UnitTestLifecycleState.Reset,
                    UnitTestResultState.NotEvaluated,
                    "Fake RunControl reset observed.")
            ],
            producerSequenceId: sequenceId,
            deletedSequenceId: sequenceId - 1,
            lostEventCount: 0,
            bufferObservedEmpty: false,
            EventBufferReadState.Valid);

        PublishBatch(batch);
        return batch;
    }

    public UnitTestEventBatch PublishEmptyBuffer()
    {
        var lastSequenceId = nextSequenceId == 1 ? 0 : nextSequenceId - 1;
        var batch = CreateBatch(
            Array.Empty<UnitTestEventEntry>(),
            producerSequenceId: lastSequenceId,
            deletedSequenceId: lastSequenceId,
            lostEventCount: 0,
            bufferObservedEmpty: true,
            EventBufferReadState.Empty);

        PublishBatch(batch);
        return batch;
    }

    public UnitTestEventBatch PublishBatchWithoutCompletionAnchor(
        bool bufferObservedEmpty = false,
        ushort lostEventCount = 0)
    {
        var sequenceId = NextSequenceId();
        var batch = CreateBatch(
            [
                new UnitTestEventEntry(
                    sequenceId,
                    DateTimeOffset.UnixEpoch,
                    UnitTestOwnerScope.RunControl,
                    UnitTestEventType.Info,
                    LogLevel.Information,
                    "FakeRunControl",
                    string.Empty,
                    UnitTestLifecycleState.Running,
                    UnitTestResultState.NotEvaluated,
                    "Fake RunControl diagnostic.")
            ],
            producerSequenceId: sequenceId,
            deletedSequenceId: bufferObservedEmpty ? sequenceId : sequenceId - 1,
            lostEventCount,
            bufferObservedEmpty,
            bufferObservedEmpty ? EventBufferReadState.Empty : EventBufferReadState.Valid);

        PublishBatch(batch);
        return batch;
    }

    public UnitTestEventBatch PublishLostEventEvidence(ushort lostEventCount = 1)
    {
        var sequenceId = NextSequenceId();
        var batch = CreateBatch(
            [
                CreateRunControlCompletionEntry(sequenceId)
            ],
            producerSequenceId: sequenceId,
            deletedSequenceId: sequenceId,
            lostEventCount,
            bufferObservedEmpty: true,
            EventBufferReadState.Valid);

        PublishBatch(batch);
        return batch;
    }

    public void PublishEventBufferFull(uint producerSequenceId, uint deletedSequenceId)
    {
        ApplyMonitoringState(CurrentMonitoringState with
        {
            LastReadState = EventBufferReadState.Valid,
            BatchReleaseState = EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty = false,
            BufferFull = true,
            ProducerSequenceId = producerSequenceId,
            DeletedSequenceId = deletedSequenceId
        });
    }

    public void PublishEventBufferLostEvents(
        ushort lostEventCount,
        uint producerSequenceId,
        uint deletedSequenceId)
    {
        if (lostEventCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lostEventCount));
        }

        ApplyMonitoringState(CurrentMonitoringState with
        {
            LastReadState = EventBufferReadState.Valid,
            BatchReleaseState = EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty = false,
            LostEventCount = lostEventCount,
            BufferFull = true,
            ProducerSequenceId = producerSequenceId,
            DeletedSequenceId = deletedSequenceId
        });
    }

    public void PublishEventBufferRecovered()
    {
        nextSequenceId = 1;
        ApplyMonitoringState(CurrentMonitoringState with
        {
            LastReadState = EventBufferReadState.Empty,
            BatchReleaseState = EventStreamBatchReleaseState.Completed,
            BufferObservedEmpty = true,
            LostEventCount = 0,
            BufferFull = false,
            ProducerSequenceId = 0,
            DeletedSequenceId = 0
        });
    }

    public UnitTestEventBatch PublishSequenceGap()
    {
        _ = NextSequenceId();
        var sequenceId = NextSequenceId();
        var batch = CreateBatch(
            [
                new UnitTestEventEntry(
                    sequenceId,
                    DateTimeOffset.UnixEpoch,
                    UnitTestOwnerScope.RunControl,
                    UnitTestEventType.Info,
                    LogLevel.Information,
                    "FakeRunControl",
                    string.Empty,
                    UnitTestLifecycleState.Running,
                    UnitTestResultState.NotEvaluated,
                    "Fake event after a sequence gap.")
            ],
            producerSequenceId: sequenceId,
            deletedSequenceId: sequenceId - 1,
            lostEventCount: 0,
            bufferObservedEmpty: false,
            EventBufferReadState.Valid);

        PublishBatch(batch);
        return batch;
    }

    public UnitTestEventBatch PublishEventBufferEpochResetObserved()
    {
        var batch = CreateBatch(
            [
                new UnitTestEventEntry(
                    1,
                    DateTimeOffset.UnixEpoch,
                    UnitTestOwnerScope.RunControl,
                    UnitTestEventType.Info,
                    LogLevel.Information,
                    "FakeRunControl",
                    string.Empty,
                    UnitTestLifecycleState.Running,
                    UnitTestResultState.NotEvaluated,
                    "Fake event-buffer epoch reset observed.")
            ],
            producerSequenceId: 1,
            deletedSequenceId: 0,
            lostEventCount: 0,
            bufferObservedEmpty: false,
            EventBufferReadState.Valid);

        PublishBatch(batch);
        return batch;
    }

    public UnitTestEventBatch PublishRetryRequired()
    {
        return PublishReadState(EventBufferReadState.RetryRequired);
    }

    public UnitTestEventBatch PublishInconsistentReadState()
    {
        return PublishReadState(EventBufferReadState.Inconsistent);
    }

    public UnitTestEventBatch PublishSessionInvalidated()
    {
        return PublishReadState(EventBufferReadState.SessionInvalid);
    }

    public UnitTestEventBatch PublishContractInvalidated()
    {
        return PublishReadState(EventBufferReadState.ContractInvalid);
    }

    public UnitTestEventBatch PublishAggregation(
        UnitTestOwnerScope ownerScope = UnitTestOwnerScope.RunControl,
        string name = "FakeRunControl",
        uint total = 1,
        uint passed = 1,
        uint failed = 0,
        uint error = 0,
        uint runtime = 25)
    {
        var sequenceId = NextSequenceId();
        var batch = CreateBatch(
            [
                CreateAggregationEntry(
                    sequenceId,
                    ownerScope,
                    name,
                    $@"{{""total"":{total},""inactive"":0,""idle"":0,""running"":0,""completed"":{total},""passed"":{passed},""failed"":{failed},""error"":{error},""runtime"":{runtime},""lostEvents"":0}}")
            ],
            producerSequenceId: sequenceId,
            deletedSequenceId: sequenceId - 1,
            lostEventCount: 0,
            bufferObservedEmpty: false,
            EventBufferReadState.Valid);

        PublishBatch(batch);
        return batch;
    }

    public UnitTestEventBatch PublishInvalidAggregation(
        UnitTestOwnerScope ownerScope = UnitTestOwnerScope.RunControl,
        string name = "FakeRunControl")
    {
        var sequenceId = NextSequenceId();
        var batch = CreateBatch(
            [
                CreateAggregationEntry(
                    sequenceId,
                    ownerScope,
                    name,
                    "not-json")
            ],
            producerSequenceId: sequenceId,
            deletedSequenceId: sequenceId - 1,
            lostEventCount: 0,
            bufferObservedEmpty: false,
            EventBufferReadState.Valid);

        PublishBatch(batch);
        return batch;
    }

    public void CompletePendingStartMonitoring(UnitTestContext context)
    {
        var (completion, cancellation) = TakePendingStartMonitoring();

        ApplyMonitoringState(new EventStreamState(
            context.Session.SessionId,
            IsMonitoring: true,
            EventBufferReadState.Empty,
            EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: true,
            LostEventCount: 0));

        SetState(ServiceState.Ready, "Fake UnitTest event-stream monitoring started.");
        cancellation.Dispose();
        completion.SetResult(OperationResult<EventStreamState>.Success(CurrentMonitoringState));
    }

    public void CompletePendingStartMonitoring(OperationResult<EventStreamState> result)
    {
        var (completion, cancellation) = TakePendingStartMonitoring();

        if (result.Value is not null)
        {
            ApplyMonitoringState(result.Value);
        }

        cancellation.Dispose();
        completion.SetResult(result);
    }

    public void FailPendingStartMonitoring(string message)
    {
        CompletePendingStartMonitoring(OperationResult<EventStreamState>.Failure(message));
    }

    public void CompletePendingReleaseCommunication()
    {
        var (completion, cancellation) = TakePendingReleaseCommunication();

        ApplyMonitoringState(CurrentMonitoringState with
        {
            IsMonitoring = false,
            BatchReleaseState = EventStreamBatchReleaseState.Idle
        });
        SetState(ServiceState.NotPrepared, "Fake UnitTest event-stream communication released.");
        cancellation.Dispose();
        completion.SetResult(OperationResult.Success());
    }

    public void CompletePendingReleaseCommunication(OperationResult result)
    {
        var (completion, cancellation) = TakePendingReleaseCommunication();

        cancellation.Dispose();
        completion.SetResult(result);
    }

    public void FailPendingReleaseCommunication(string message)
    {
        CompletePendingReleaseCommunication(OperationResult.Failure(message));
    }

    public UnitTestEventBatch PublishOldEvent(uint sequenceId)
    {
        var batch = CreateBatch(
            [
                new UnitTestEventEntry(
                    sequenceId,
                    DateTimeOffset.UnixEpoch,
                    UnitTestOwnerScope.RunControl,
                    UnitTestEventType.Info,
                    LogLevel.Information,
                    "FakeRunControl",
                    string.Empty,
                    UnitTestLifecycleState.Running,
                    UnitTestResultState.NotEvaluated,
                    "Fake old RunControl event.")
            ],
            producerSequenceId: Math.Max(sequenceId, nextSequenceId == 1 ? 0 : nextSequenceId - 1),
            deletedSequenceId: sequenceId,
            lostEventCount: 0,
            bufferObservedEmpty: false,
            EventBufferReadState.Valid);

        PublishBatch(batch);
        return batch;
    }

    private UnitTestEventBatch PublishReadState(EventBufferReadState readState)
    {
        var lastSequenceId = nextSequenceId == 1 ? 0 : nextSequenceId - 1;
        var batch = CreateBatch(
            Array.Empty<UnitTestEventEntry>(),
            producerSequenceId: lastSequenceId,
            deletedSequenceId: 0,
            lostEventCount: 0,
            bufferObservedEmpty: false,
            readState);

        PublishBatch(batch);
        return batch;
    }

    private (
        TaskCompletionSource<OperationResult<EventStreamState>> Completion,
        CancellationTokenRegistration Cancellation) TakePendingStartMonitoring()
    {
        lock (pendingOperationGate)
        {
            var completion = pendingStartMonitoringCompletion
                ?? throw new InvalidOperationException("No fake UnitTest event-stream monitoring start operation is pending.");
            pendingStartMonitoringCompletion = null;
            var cancellation = pendingStartMonitoringCancellation;
            pendingStartMonitoringCancellation = default;
            return (completion, cancellation);
        }
    }

    private (
        TaskCompletionSource<OperationResult> Completion,
        CancellationTokenRegistration Cancellation) TakePendingReleaseCommunication()
    {
        lock (pendingOperationGate)
        {
            var completion = pendingReleaseCommunicationCompletion
                ?? throw new InvalidOperationException("No fake UnitTest event-stream communication release operation is pending.");
            pendingReleaseCommunicationCompletion = null;
            var cancellation = pendingReleaseCommunicationCancellation;
            pendingReleaseCommunicationCancellation = default;
            return (completion, cancellation);
        }
    }

    private void CancelPendingStartMonitoring(
        TaskCompletionSource<OperationResult<EventStreamState>> completion,
        CancellationToken cancellationToken)
    {
        CancellationTokenRegistration cancellation;
        lock (pendingOperationGate)
        {
            if (!ReferenceEquals(pendingStartMonitoringCompletion, completion))
            {
                return;
            }

            pendingStartMonitoringCompletion = null;
            cancellation = pendingStartMonitoringCancellation;
            pendingStartMonitoringCancellation = default;
        }

        cancellation.Dispose();
        completion.TrySetCanceled(cancellationToken);
    }

    private void CancelPendingReleaseCommunication(
        TaskCompletionSource<OperationResult> completion,
        CancellationToken cancellationToken)
    {
        CancellationTokenRegistration cancellation;
        lock (pendingOperationGate)
        {
            if (!ReferenceEquals(pendingReleaseCommunicationCompletion, completion))
            {
                return;
            }

            pendingReleaseCommunicationCompletion = null;
            cancellation = pendingReleaseCommunicationCancellation;
            pendingReleaseCommunicationCancellation = default;
        }

        cancellation.Dispose();
        completion.TrySetCanceled(cancellationToken);
    }

    internal uint ReserveSequenceIds(int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var firstSequenceId = nextSequenceId;
        nextSequenceId += (uint)count;
        return firstSequenceId;
    }

    private void ApplyMonitoringState(EventStreamState state)
    {
        var previousState = CurrentMonitoringState;
        CurrentMonitoringState = state;
        if (previousState != state)
        {
            MonitoringStateChangedEventHandler?.Invoke(
                this,
                new EventStreamStateChangedEventArgs(previousState, state));
        }
    }

    private uint NextSequenceId()
    {
        return ReserveSequenceIds(1);
    }

    private UnitTestEventBatch CreateBatch(
        IReadOnlyList<UnitTestEventEntry> events,
        uint producerSequenceId,
        uint deletedSequenceId,
        ushort lostEventCount,
        bool bufferObservedEmpty,
        EventBufferReadState readState)
    {
        return new UnitTestEventBatch(
            CurrentMonitoringState.SessionId,
            events,
            producerSequenceId,
            deletedSequenceId,
            lostEventCount,
            bufferObservedEmpty,
            readState);
    }

    private static UnitTestEventEntry CreateRunControlCompletionEntry(uint sequenceId)
    {
        return CreateRunControlStateTransitionEntry(
            sequenceId,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.NotEvaluated,
            "Fake RunControl completed.");
    }

    private static UnitTestEventEntry CreateRunControlStateTransitionEntry(
        uint sequenceId,
        UnitTestLifecycleState state,
        UnitTestResultState result,
        string message)
    {
        return new UnitTestEventEntry(
            sequenceId,
            DateTimeOffset.UnixEpoch,
            UnitTestOwnerScope.RunControl,
            UnitTestEventType.StateTransition,
            LogLevel.Information,
            "FakeRunControl",
            string.Empty,
            state,
            result,
            message);
    }

    private static UnitTestEventEntry CreateRunControlResultEntry(
        uint sequenceId,
        UnitTestResultState result,
        string message)
    {
        return new UnitTestEventEntry(
            sequenceId,
            DateTimeOffset.UnixEpoch,
            UnitTestOwnerScope.RunControl,
            UnitTestEventType.Result,
            LogLevel.Information,
            "FakeRunControl",
            string.Empty,
            UnitTestLifecycleState.Completed,
            result,
            message);
    }

    private static UnitTestEventEntry CreateAggregationEntry(
        uint sequenceId,
        UnitTestOwnerScope ownerScope,
        string name,
        string payload)
    {
        return new UnitTestEventEntry(
            sequenceId,
            DateTimeOffset.UnixEpoch,
            ownerScope,
            UnitTestEventType.Aggregation,
            LogLevel.Information,
            name,
            string.Empty,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.NotEvaluated,
            payload);
    }
}
