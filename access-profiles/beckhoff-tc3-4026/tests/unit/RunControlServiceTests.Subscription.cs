using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed partial class RunControlServiceTests
{
    [Theory]
    [InlineData(UnitTestRunCommand.Start)]
    [InlineData(UnitTestRunCommand.Abort)]
    [InlineData(UnitTestRunCommand.Reset)]
    public async Task SubscriptionCommandRequestsFreshReadAndPublishesUnchangedStatus(UnitTestRunCommand command)
    {
        await using var test = new Fixture { Strategy = ObservationStrategy.Subscription };
        await test.Prepare();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Access.Read = async token =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(token);
            return Ready;
        };
        Assert.True((await test.Command(command)).IsSuccessful);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(test.Observations);
        Assert.Equal(1, test.Access.Requests);
        Assert.Equal(command, Assert.Single(test.Access.Commands));
        finish.TrySetResult();
        await Until(() => test.Observations.Count == 2);
        Assert.All(test.Observations, value => Assert.Equal(Ready, value));
    }

    [Fact]
    public async Task LateNotificationCannotReplaceNewerReadWithHistoricalStatus()
    {
        await using var test = new Fixture { Strategy = ObservationStrategy.Subscription };
        await test.Prepare();
        var completed = Ready with { State = UnitTestLifecycleState.Completed, ExecutionStatus = UnitTestExecutionStatus.Completed };
        test.Access.Value = completed;
        test.Access.Signal.Request();
        await Until(() => test.Service.CurrentObservation == completed);
        var reads = test.Access.Reads;
        // A delayed Running callback is only a wakeup, never an input to publication.
        test.Access.Signal.Request();
        await Until(() => test.Access.Reads > reads);
        Assert.Equal(completed, test.Service.CurrentObservation);
        Assert.Equal(new[] { Ready, completed }, test.Observations.ToArray());
    }

    [Fact]
    public async Task MissingNotificationStillUsesBoundedControlRead()
    {
        await using var test = new Fixture { Strategy = ObservationStrategy.Subscription };
        await test.Prepare();
        test.Access.Value = Ready with { State = UnitTestLifecycleState.Running };
        await Until(() => test.Service.CurrentObservation.State == UnitTestLifecycleState.Running);
        Assert.Equal(TimeSpan.FromMilliseconds(100), test.Access.LastFallback);
        Assert.Equal(1, test.Access.Registrations);
        Assert.Empty(test.Access.Commands);
    }

    [Fact]
    public async Task NotificationDuringReadSchedulesAnotherFreshObservation()
    {
        await using var test = new Fixture { Strategy = ObservationStrategy.Subscription };
        await test.Prepare();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Access.Read = async token =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(token);
            return Ready;
        };
        test.Access.Signal.Request();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        test.Access.Signal.Request();
        var completed = Ready with { State = UnitTestLifecycleState.Completed };
        test.Access.Read = _ => Task.FromResult(completed);
        finish.TrySetResult();
        await Until(() => test.Service.CurrentObservation == completed);
    }

    [Fact]
    public async Task FailedCommandDoesNotRequestConfirmationOrReplay()
    {
        await using var test = new Fixture { Strategy = ObservationStrategy.Subscription };
        await test.Prepare();
        test.Access.OnWrite = () => test.Available = false;
        test.Access.WriteError = new IOException("Rejected write");
        Assert.False((await test.Command(UnitTestRunCommand.Reset)).IsSuccessful);
        Assert.Equal(0, test.Access.Requests);
        Assert.Equal(UnitTestRunCommand.Reset, Assert.Single(test.Access.Commands));
        Assert.Single(test.Observations);
    }

    [Fact]
    public async Task ReleaseCancelsSubscriptionWait()
    {
        await using var test = new Fixture { Strategy = ObservationStrategy.Subscription };
        await test.Prepare();
        await Until(() => test.Access.LastFallback != default);
        Assert.True((await test.Service.ReleaseCommunicationAsync(default).WaitAsync(TimeSpan.FromSeconds(3))).IsSuccessful);
        var count = test.Observations.Count;
        test.Access.Signal.Request();
        Assert.Equal(count, test.Observations.Count);
        Assert.Equal(UnitTestLifecycleState.Unknown, test.Service.CurrentObservation.State);
    }

    [Fact]
    public async Task RequestsCoalesceAndAreNotLostAcrossReadBoundary()
    {
        var signal = new RunControlObservationSignal();
        for (var i = 0; i < 100; i++) signal.Request();
        await signal.WaitAsync(TimeSpan.FromMinutes(1), TimeProvider.System, default).WaitAsync(TimeSpan.FromSeconds(3));
        signal.BeginRead();
        using var cancellation = new CancellationTokenSource();
        var wait = signal.WaitAsync(TimeSpan.FromMinutes(1), TimeProvider.System, cancellation.Token);
        Assert.False(wait.IsCompleted);
        signal.Request();
        await wait.WaitAsync(TimeSpan.FromSeconds(3));
        signal.BeginRead();
        var cancelled = signal.WaitAsync(TimeSpan.FromMinutes(1), TimeProvider.System, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    [Fact]
    public async Task FailedConfirmationRecoversWithoutRepeatingAcceptedCommand()
    {
        await using var test = new Fixture(TimeSpan.FromMilliseconds(5)) { Strategy = ObservationStrategy.Subscription };
        await test.Prepare();
        var old = test.Access;
        var replacement = new FakeAccess();
        old.OnWrite = () =>
        {
            old.ReadError = new IOException("Confirmation read failed");
            test.Access = replacement;
        };
        Assert.True((await test.Command(UnitTestRunCommand.Reset)).IsSuccessful);
        await Until(() => test.Observations.Count == 2);
        Assert.Equal(1, old.Disposals);
        Assert.Equal(UnitTestRunCommand.Reset, Assert.Single(old.Commands));
        Assert.Empty(replacement.Commands);
        Assert.Equal(1, replacement.Registrations);
        Assert.All(test.Observations, value => Assert.Equal(Ready, value));
    }

    [Fact]
    public async Task CancellationAfterAcceptedWriteDoesNotSuppressConfirmation()
    {
        await using var test = new Fixture { Strategy = ObservationStrategy.Subscription };
        await test.Prepare();
        using var cancellation = new CancellationTokenSource();
        test.Access.OnWrite = cancellation.Cancel;
        Assert.True((await test.Command(UnitTestRunCommand.Reset, cancellation.Token)).IsSuccessful);
        await Until(() => test.Observations.Count == 2);
        Assert.Single(test.Access.Commands);
        Assert.Equal(1, test.Access.Requests);
    }

    [Fact]
    public async Task ClosedGenerationDoesNotWakeReplacementSignal()
    {
        var old = new RunControlObservationSignal();
        old.Close();
        var current = new RunControlObservationSignal();
        using var cancellation = new CancellationTokenSource();
        var wait = current.WaitAsync(TimeSpan.FromMinutes(1), TimeProvider.System, cancellation.Token);
        old.Request();
        Assert.False(wait.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
