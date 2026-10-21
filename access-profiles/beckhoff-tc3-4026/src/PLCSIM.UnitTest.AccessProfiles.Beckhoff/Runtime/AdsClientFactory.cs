using Microsoft.Extensions.Logging;
using TwinCAT.Ads;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

// Creating a client does not connect it. Its caller owns connection and disposal.
internal sealed class AdsClientFactory(ILoggerFactory loggerFactory)
{
    private readonly ILoggerFactory vendorLogging = new AdsLoggerFactory(loggerFactory);

    public AdsClient CreateClient() => new(vendorLogging);

    public IRuntimeAdsSession CreateRuntimeSession(string netId, int port) =>
        new RuntimeAdsSession(netId, port, vendorLogging);

    // System polling owns retry timing; cached FailFast errors would hide CONFIG/RUN recovery.
    public AdsClient CreateSystemClient() => new(null, AdsClientSettings.FastWriteThrough, vendorLogging);
}
