using PLCSIM.UnitTest.Common.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.CommonContracts;

public sealed class TimeSourceContractTests
{
    [Fact]
    public void System_time_source_returns_utc_time()
    {
        var timeSource = new SystemTimeSource();

        var before = DateTimeOffset.UtcNow;
        var actual = timeSource.UtcNow;
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(TimeSpan.Zero, actual.Offset);
        Assert.InRange(actual, before, after);
    }
}
