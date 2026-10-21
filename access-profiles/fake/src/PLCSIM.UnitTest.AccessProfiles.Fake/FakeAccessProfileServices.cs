using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Services;

namespace PLCSIM.UnitTest.AccessProfiles.Fake;

/// <summary>
/// Bundles the four functional services required by a loaded fake access profile.
/// </summary>
public sealed record FakeAccessProfileServices(
    IPlcProjectService Project,
    IPlcControlService PlcControl,
    IUnitTestLifecycleService UnitTestLifecycle,
    IUnitTestEventStreamService UnitTestEventStream)
{
    private readonly object prepareUnitTestCommunicationGate = new();
    private TaskCompletionSource<OperationResult<CommunicationSession>>? pendingPrepareUnitTestCommunicationCompletion;
    private CancellationTokenRegistration pendingPrepareUnitTestCommunicationCancellation;

    public OperationResult<CommunicationSession>? PrepareUnitTestCommunicationResult { get; init; }

    public OperationResult<PreparedPlcRuntime>? ReconnectRuntimeResult { get; init; }

    public bool HoldNextPrepareUnitTestCommunication { get; set; }

    public bool IsPrepareUnitTestCommunicationPending
    {
        get
        {
            lock (prepareUnitTestCommunicationGate)
            {
                return pendingPrepareUnitTestCommunicationCompletion is not null;
            }
        }
    }

    internal Task<OperationResult<CommunicationSession>> HoldPrepareUnitTestCommunicationAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (prepareUnitTestCommunicationGate)
        {
            if (pendingPrepareUnitTestCommunicationCompletion is not null)
            {
                return Task.FromResult(OperationResult<CommunicationSession>.Failure(
                    "A fake UnitTest communication preparation operation is already pending."));
            }

            HoldNextPrepareUnitTestCommunication = false;
            var completion = new TaskCompletionSource<OperationResult<CommunicationSession>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pendingPrepareUnitTestCommunicationCompletion = completion;
            var registration = cancellationToken.Register(
                () => CancelPendingPrepareUnitTestCommunication(completion, cancellationToken));
            if (ReferenceEquals(pendingPrepareUnitTestCommunicationCompletion, completion))
            {
                pendingPrepareUnitTestCommunicationCancellation = registration;
            }
            else
            {
                registration.Dispose();
            }

            return completion.Task;
        }
    }

    public void CompletePendingPrepareUnitTestCommunication(OperationResult<CommunicationSession> result)
    {
        TaskCompletionSource<OperationResult<CommunicationSession>> completion;
        CancellationTokenRegistration cancellation;
        lock (prepareUnitTestCommunicationGate)
        {
            completion = pendingPrepareUnitTestCommunicationCompletion
                ?? throw new InvalidOperationException("No fake UnitTest communication preparation operation is pending.");
            pendingPrepareUnitTestCommunicationCompletion = null;
            cancellation = pendingPrepareUnitTestCommunicationCancellation;
            pendingPrepareUnitTestCommunicationCancellation = default;
        }

        cancellation.Dispose();
        completion.SetResult(result);
    }

    public void FailPendingPrepareUnitTestCommunication(string message)
    {
        CompletePendingPrepareUnitTestCommunication(OperationResult<CommunicationSession>.Failure(message));
    }

    private void CancelPendingPrepareUnitTestCommunication(
        TaskCompletionSource<OperationResult<CommunicationSession>> completion,
        CancellationToken cancellationToken)
    {
        CancellationTokenRegistration cancellation;
        lock (prepareUnitTestCommunicationGate)
        {
            if (!ReferenceEquals(pendingPrepareUnitTestCommunicationCompletion, completion))
            {
                return;
            }

            pendingPrepareUnitTestCommunicationCompletion = null;
            cancellation = pendingPrepareUnitTestCommunicationCancellation;
            pendingPrepareUnitTestCommunicationCancellation = default;
        }

        cancellation.Dispose();
        completion.TrySetCanceled(cancellationToken);
    }
}
