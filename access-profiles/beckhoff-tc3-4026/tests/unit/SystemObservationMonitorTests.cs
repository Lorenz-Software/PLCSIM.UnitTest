using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using TwinCAT.Ads;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class SystemObservationMonitorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task RestartWaitRequiresFreshRunAndChangedNonzeroCounter()
    {
        var monitor = Create();
        Publish(monitor, AdsState.Run, 4);
        var baseline = monitor.Latest;
        var wait = monitor.WaitForRunAsync(baseline, true, Deadline, default);
        Assert.False(wait.IsCompleted);
        Publish(monitor, AdsState.Run, 4);
        Publish(monitor, AdsState.Config, 5);
        Publish(monitor, AdsState.Run, 0);
        Assert.False(wait.IsCompleted);
        Publish(monitor, AdsState.Run, 6);
        var result = await wait;
        Assert.Equal(6, result.Value!.RestartCounter);
        Assert.True(result.Sequence > baseline.Sequence);
    }

    [Fact]
    public async Task CounterWrapIsAChangeNotAnIncreasingCounterRequirement()
    {
        var monitor = Create();
        Publish(monitor, AdsState.Run, 15);
        var baseline = monitor.Latest;
        Publish(monitor, AdsState.Run, 1);
        Assert.Equal(1, (await monitor.WaitForRunAsync(baseline, true, Deadline, default)).Value!.RestartCounter);
    }

    [Fact]
    public async Task ExternalRunWaitDoesNotInventRestartRequirement()
    {
        var monitor = Create();
        var baseline = monitor.Latest;
        Publish(monitor, AdsState.Run, 0);
        Assert.True((await monitor.WaitForRunAsync(baseline, false, Deadline, default)).IsRun);
    }

    [Fact]
    public async Task LostInvalidatesRunAndRejectsInflightRead()
    {
        var monitor = Create();
        Publish(monitor, AdsState.Run, 2);
        var baseline = monitor.Latest;
        monitor.Invalidate("Lost");
        Assert.False(monitor.TryPublish(baseline.Sequence, new(AdsState.Run, 3)));
        Assert.Null(monitor.Latest.Value);
        Assert.False(monitor.Latest.IsRun);
        var wait = monitor.WaitForRunAsync(baseline, false, Deadline, default);
        Assert.False(wait.IsCompleted);
        Publish(monitor, AdsState.Run, 3);
        Assert.True((await wait).IsRun);
    }

    [Fact]
    public async Task CancellationDoesNotStopMonitoringOrOtherWaiters()
    {
        var monitor = Create();
        using var cancellation = new CancellationTokenSource();
        var baseline = monitor.Latest;
        var cancelled = monitor.WaitForRunAsync(baseline, false, Deadline, cancellation.Token);
        var other = monitor.WaitForRunAsync(baseline, false, Deadline, default);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Publish(monitor, AdsState.Run, 2);
        Assert.True((await other).IsRun);
    }

    [Fact]
    public async Task TimeoutDoesNotTerminateConfigMonitoring()
    {
        var monitor = Create();
        Publish(monitor, AdsState.Config, 2);
        var baseline = monitor.Latest;
        await Assert.ThrowsAsync<TimeoutException>(() => monitor.WaitForRunAsync(baseline, false, TimeSpan.FromMilliseconds(20), default));
        Publish(monitor, AdsState.Config, 2);
        Publish(monitor, AdsState.Run, 3);
        Assert.True((await monitor.WaitForRunAsync(baseline, false, Deadline, default)).IsRun);
    }

    [Fact]
    public async Task StopReleasesWaitersAndRejectsLatePublication()
    {
        var monitor = Create();
        var baseline = monitor.Latest;
        var wait = monitor.WaitForRunAsync(baseline, false, Deadline, default);
        monitor.Stop();
        await Assert.ThrowsAsync<IOException>(() => wait);
        Assert.False(monitor.TryPublish(monitor.Latest.Sequence, new(AdsState.Run, 1)));
        monitor.Stop();
    }

    [Fact]
    public async Task ReplacedContextCannotReuseBaseline()
    {
        var old = Create();
        Publish(old, AdsState.Run, 4);
        var replacement = Create();
        Publish(replacement, AdsState.Run, 5);
        await Assert.ThrowsAsync<ArgumentException>(() => replacement.WaitForRunAsync(old.Latest, true, Deadline, default));
    }

    [Fact]
    public async Task RestartRequiresObservedBaseline()
    {
        var monitor = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.WaitForRunAsync(monitor.Latest, true, Deadline, default));
    }

    [Fact]
    public async Task MultipleWaitersObservePublicationWithoutSerializingEachOther()
    {
        var monitor = Create();
        var baseline = monitor.Latest;
        var waits = Enumerable.Range(0, 20).Select(_ => monitor.WaitForRunAsync(baseline, false, Deadline, default)).ToArray();
        Publish(monitor, AdsState.Run, 1);
        var results = await Task.WhenAll(waits);
        Assert.All(results, result => Assert.Equal(monitor.Latest, result));
    }

    private static SystemObservationMonitor Create() => new(TimeProvider.System);
    private static void Publish(SystemObservationMonitor monitor, AdsState state, int counter) =>
        Assert.True(monitor.TryPublish(monitor.Latest.Sequence, new(state, counter)));
}
