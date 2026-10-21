using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using Xunit;
using static PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit.FakeEventStreamAccess;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed partial class EventStreamServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportRebindRetainsWatermarkUnlessBootstrapShowsReset(bool reset)
    {
        await using var test = new Fixture();
        test.Settings = test.Settings with
        {
            Strategy = ObservationStrategy.Subscription,
            MonitoringInterval = TimeSpan.FromMilliseconds(5),
            DrainInterval = TimeSpan.FromMilliseconds(1)
        };
        var old = test.Access;
        var oldNotifications = new NotificationEventStreamAccess(old, TimeProvider.System);
        test.AccessOverride = oldNotifications;
        var replacement = new FakeEventStreamAccess { Value = reset ? Observation(0, 0) : Observation() };
        var newNotifications = new NotificationEventStreamAccess(replacement, TimeProvider.System);
        test.Service.EventBatchReceivedEventHandler += (_, args) =>
        {
            if (args.Batch.Events.Count == 0 || old.Writes.Count == 0) return;
            test.AccessOverride = newNotifications;
            old.MetadataError = new IOException("Transport interrupted after accepted write");
        };
        await test.Prepare();
        await test.Start();
        await Until(() => replacement.MetadataReads >= 3);
        await test.Service.StopMonitoringAsync(default);
        Assert.Equal(1, old.Disposals);
        Assert.Equal(reset ? 0u : 2u, newNotifications.AcceptedWatermark);
        Assert.Empty(replacement.Writes);
        Assert.Equal(0, replacement.Reads);
        Assert.Single(test.Batches.Where(batch => batch.Events.Count != 0));
    }

    [Fact]
    public async Task ShortRuntimeLossRebindsSubscriptionBeforeReading()
    {
        await using var test = new Fixture();
        test.Settings = test.Settings with { Strategy = ObservationStrategy.Subscription };
        test.Access.Value = Observation(0, 0);
        var old = test.Access;
        var oldNotifications = new NotificationEventStreamAccess(old, TimeProvider.System);
        test.AccessOverride = oldNotifications;
        await test.Prepare();
        var replacement = new FakeEventStreamAccess();
        replacement.BeforePrepare = _ => { Assert.Equal(1, old.Disposals); return Task.CompletedTask; };
        test.AccessOverride = new NotificationEventStreamAccess(replacement, TimeProvider.System);
        Interlocked.Increment(ref test.BindingGeneration);
        await test.Start();
        await Until(() => replacement.Writes.Count == 1);
        await test.Service.StopMonitoringAsync(default);
        Assert.Empty(old.Writes);
        Assert.Equal(1, replacement.Prepares);
        Assert.True(replacement.MetadataReads > 0);
        oldNotifications.Cache.Observe(1, Observation().Fifo, Observation().Status);
        Assert.Single(test.Batches);
    }

    [Fact]
    public async Task RuntimeLossDuringPayloadReadCannotReleaseOrPublishThatBatch()
    {
        await using var test = new Fixture();
        test.Settings = test.Settings with { Strategy = ObservationStrategy.Subscription };
        test.AccessOverride = new NotificationEventStreamAccess(test.Access, TimeProvider.System);
        test.Access.BeforeRead = _ =>
        {
            Interlocked.Increment(ref test.BindingGeneration);
            return Task.CompletedTask;
        };
        await test.Prepare();
        await test.Start();
        await Until(() => test.Access.Disposals == 1);
        await test.Service.StopMonitoringAsync(default);
        Assert.Empty(test.Access.Writes);
        Assert.Empty(test.Batches);
    }
}
