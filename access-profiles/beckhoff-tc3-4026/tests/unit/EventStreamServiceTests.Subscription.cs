using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using Xunit;
using static PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit.FakeEventStreamAccess;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed partial class EventStreamServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscriptionUsesDrainFallbackOrIdleVerificationNotMonitoringInterval(bool pending)
    {
        var clock = new DelayClock();
        await using var test = new Fixture(clock);
        test.Settings = test.Settings with
        {
            Strategy = ObservationStrategy.Subscription,
            MonitoringInterval = TimeSpan.FromMilliseconds(10),
            DrainInterval = TimeSpan.FromMilliseconds(1)
        };
        test.Access.Value = pending ? Observation() : Observation(0, 0);
        var access = new NotificationEventStreamAccess(test.Access, clock);
        test.AccessOverride = access;
        var entered = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        access.Wait = async (fallback, token) =>
        {
            entered.TrySetResult(fallback);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        await test.Prepare();
        await test.Start();
        Assert.Equal(TimeSpan.FromMilliseconds(pending ? 1 : 100), await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(clock.Timers);
        Assert.Single(test.Batches);
        Assert.True((await test.Service.StopMonitoringAsync(default)).IsSuccessful);
    }

    [Fact]
    public async Task CacheWaitWakesForUsableProgressWithoutPollingDelay()
    {
        var clock = new DelayClock();
        var cache = new EventStreamNotificationCache(1, clock);
        cache.Confirm(Observation(0, 0), cache.BeginRead());
        var wait = cache.WaitAsync(TimeSpan.FromMilliseconds(100), default);
        _ = await clock.Next();
        Assert.False(wait.IsCompleted);
        var available = Observation();
        cache.Observe(1, available.Fifo, available.Status);
        await wait.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(available, cache.Candidate());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task CacheWaitIsBoundedWhenNotificationIsMissing(int milliseconds)
    {
        var clock = new DelayClock();
        var cache = new EventStreamNotificationCache(1, clock);
        cache.Confirm(Observation(0, 0), cache.BeginRead());
        var wait = cache.WaitAsync(TimeSpan.FromMilliseconds(milliseconds), default);
        var timer = await clock.Next();
        Assert.InRange(timer.Due.TotalMilliseconds, 0, milliseconds);
        Assert.False(wait.IsCompleted);
        timer.Fire();
        await wait.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task AcceptedWatermarkSuppressesBacklogWakeUntilDeletionProgress()
    {
        var clock = new DelayClock();
        var cache = new EventStreamNotificationCache(1, clock);
        cache.Confirm(Observation(), cache.BeginRead());
        cache.Accepted(3);
        var wait = cache.WaitAsync(TimeSpan.FromMinutes(1), default);
        _ = await clock.Next();
        Assert.False(wait.IsCompleted);
        var progress = Observation(3, 6, 3);
        cache.Observe(1, progress.Fifo, progress.Status);
        await wait.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheWaitCanBeClosedOrCancelled(bool cancel)
    {
        var clock = new DelayClock();
        var cache = new EventStreamNotificationCache(1, clock);
        cache.Confirm(Observation(0, 0), cache.BeginRead());
        using var source = new CancellationTokenSource();
        var wait = cache.WaitAsync(TimeSpan.FromMinutes(1), source.Token);
        _ = await clock.Next();
        if (cancel)
        {
            source.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        }
        else
        {
            cache.Close();
            await wait.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task SubscriptionWriteAcceptanceSurvivesConcurrentStop()
    {
        await using var test = new Fixture();
        test.Settings = test.Settings with { Strategy = ObservationStrategy.Subscription };
        test.AccessOverride = new NotificationEventStreamAccess(test.Access, TimeProvider.System);
        var entered = Signal();
        var finish = Signal();
        test.Access.BeforeWrite = async (_, token) =>
        {
            Assert.False(token.CanBeCanceled);
            entered.TrySetResult();
            await finish.Task.WaitAsync(TimeSpan.FromSeconds(3));
        };
        await test.Prepare();
        await test.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop = test.Service.StopMonitoringAsync(default);
        try { Assert.False(stop.IsCompleted); }
        finally { finish.TrySetResult(); }
        Assert.True((await stop.WaitAsync(TimeSpan.FromSeconds(3))).IsSuccessful);
        Assert.Single(test.Batches);
        Assert.Single(test.Access.Writes);
    }
}
