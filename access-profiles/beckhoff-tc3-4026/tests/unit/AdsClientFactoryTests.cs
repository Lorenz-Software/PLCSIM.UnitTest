using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using TwinCAT.Ads;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class AdsClientFactoryTests
{
    [Fact]
    public void SystemAndRegularClientsUseSeparateSettingsWithoutConnecting()
    {
        var factory = new AdsClientFactory(NullLoggerFactory.Instance);
        using var regular = factory.CreateClient();
        using var system = factory.CreateSystemClient();
        Assert.False(regular.IsConnected);
        Assert.False(system.IsConnected);
        Assert.Equal(AdsClientSettings.Default.Timeout, regular.Timeout);
        Assert.Equal(AdsClientSettings.FastWriteThrough.Timeout, system.Timeout);
        Assert.IsType<AdsLoggerFactory>(regular.LoggerFactory);
        Assert.Same(regular.LoggerFactory, system.LoggerFactory);
        system.Timeout = 3000;
        Assert.Equal(AdsClientSettings.Default.Timeout, regular.Timeout);
    }
}
