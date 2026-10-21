using PLCSIM.UnitTest.App.Wpf.ViewModels;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Wpf;

public sealed class CancellableOperationControllerTests
{
    [Fact]
    public async Task Requested_cancellation_runs_recovery_when_operation_returns_structured_result()
    {
        using var controller = new CancellableOperationController();
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operationRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryCalled = false;

        var execution = controller.RunAsync(
            "Operation",
            async _ =>
            {
                operationStarted.TrySetResult();
                await operationRelease.Task;
            },
            () =>
            {
                recoveryCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        await operationStarted.Task;
        Assert.True(controller.RequestCancellation());
        operationRelease.TrySetResult();
        await execution;

        Assert.True(recoveryCalled);
        Assert.False(controller.IsActive);
        Assert.False(controller.IsCancellationRequested);
    }

    [Fact]
    public async Task Cancel_and_wait_completes_after_cancellation_recovery()
    {
        using var controller = new CancellableOperationController();
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operationRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = controller.RunAsync(
            "Operation",
            async cancellationToken =>
            {
                operationStarted.TrySetResult();
                await operationRelease.Task;
                cancellationToken.ThrowIfCancellationRequested();
            },
            () => recoveryRelease.Task,
            CancellationToken.None);

        await operationStarted.Task;
        var cancellation = controller.CancelAndWaitAsync();
        operationRelease.TrySetResult();

        Assert.False(cancellation.IsCompleted);
        recoveryRelease.TrySetResult();
        await cancellation;
        await execution;

        Assert.False(controller.IsActive);
    }
}
