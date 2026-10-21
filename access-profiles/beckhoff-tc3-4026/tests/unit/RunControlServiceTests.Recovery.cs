using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed partial class RunControlServiceTests
{
    [Fact]
    public async Task ShortRuntimeLossRebindsEvenWhenAvailableAgain()
    {
        await using var test = new Fixture { Strategy = ObservationStrategy.Subscription };
        await test.Prepare();
        var old = test.Access;
        var replacement = new FakeAccess { Value = Ready with { State = UnitTestLifecycleState.Completed } };
        test.Access = replacement;
        test.BeforeOpen = () => Assert.Equal(1, old.Disposals);
        Interlocked.Increment(ref test.BindingGeneration);
        old.Signal.Request();
        await Until(() => test.Service.CurrentObservation == replacement.Value);
        Assert.Equal(1, replacement.Registrations);
        Assert.True(replacement.Reads > 0);
        old.Signal.Request();
        Assert.Equal(replacement.Value, test.Service.CurrentObservation);
        Assert.Empty(old.Commands);
    }

    [Fact]
    public async Task RuntimeLossDuringReadDiscardsItsResult()
    {
        await using var test = new Fixture(TimeSpan.FromMilliseconds(5)) { Strategy = ObservationStrategy.Subscription };
        await test.Prepare();
        var old = test.Access;
        old.Read = _ =>
        {
            Interlocked.Increment(ref test.BindingGeneration);
            test.Access = new FakeAccess();
            return Task.FromResult(Ready with { State = UnitTestLifecycleState.Completed });
        };
        old.Signal.Request();
        await Until(() => old.Disposals == 1 && test.Access.Registrations == 1);
        Assert.All(test.Observations, value => Assert.Equal(Ready, value));
    }

    [Fact]
    public async Task CommandCannotUseBindingFromBeforeRuntimeLoss()
    {
        await using var test = new Fixture { Strategy = ObservationStrategy.Subscription };
        await test.Prepare();
        Interlocked.Increment(ref test.BindingGeneration);
        Assert.False((await test.Command(UnitTestRunCommand.Start)).IsSuccessful);
        Assert.Empty(test.Access.Commands);
    }
}
