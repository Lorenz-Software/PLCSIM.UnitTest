using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed partial class EventStreamServiceTests
{
    [Fact]
    public async Task ResetCannotOvertakeSubscriptionCaptureAndPublication()
    {
        await using var test = new Fixture();
        test.Settings = test.Settings with { Strategy = ObservationStrategy.Subscription };
        test.AccessOverride = new NotificationEventStreamAccess(test.Access, TimeProvider.System);
        var entered = Signal();
        var finish = Signal();
        test.Access.BeforeRead = async token =>
        {
            entered.TrySetResult();
            await finish.Task.WaitAsync(token);
        };
        test.Access.BeforeReset = _ =>
        {
            Assert.Single(test.Batches);
            Assert.Single(test.Access.Writes);
            return Task.CompletedTask;
        };
        await test.Prepare();
        await test.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var reset = test.Service.ResetEventBufferAsync(test.Context, default);
        try { Assert.False(reset.IsCompleted); Assert.Equal(0, test.Access.Resets); }
        finally { finish.TrySetResult(); }
        Assert.True((await reset.WaitAsync(TimeSpan.FromSeconds(3))).IsSuccessful);
        Assert.Equal(1, test.Access.Resets);
        await test.Service.StopMonitoringAsync(default);
    }
}
