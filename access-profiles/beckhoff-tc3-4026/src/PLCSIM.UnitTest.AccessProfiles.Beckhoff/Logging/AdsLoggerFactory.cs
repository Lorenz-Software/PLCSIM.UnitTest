using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Logging;

// Non-owning view of the host factory, passed only to the ADS library.
internal sealed class AdsLoggerFactory(ILoggerFactory host) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new ProfileLogger(host.CreateLogger(categoryName), vendor: true);
    public void Dispose() { }
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException("The application owns log providers.");
}
