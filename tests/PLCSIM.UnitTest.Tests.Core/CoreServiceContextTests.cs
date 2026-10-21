using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Testing.Core;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core;

public sealed class CoreServiceContextTests
{
    [Fact]
    public void Access_profile_context_preserves_application_owned_logging_options()
    {
        var logging = new OperationalLoggingOptions(
            Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "logs"),
            LogLevel.Debug,
            2_000_000,
            3,
            rawEventLoggingEnabled: true);
        var coreContext = TestCoreServiceContext.Create(logging: logging);

        var profileContext = coreContext.CreateAccessProfileContext("profile-root");

        Assert.Same(logging, coreContext.Logging);
        Assert.Same(logging, profileContext.Logging);
        Assert.StartsWith(
            Path.GetFullPath(coreContext.AccessProfileData.RootDirectory),
            profileContext.ProfileDataDirectory,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LogLevel.Debug, profileContext.Logging.MinimumLevel);
        Assert.True(profileContext.Logging.RawEventLoggingEnabled);
    }

    [Fact]
    public void Access_profile_context_uses_profile_specific_logger_factory_when_profile_id_is_supplied()
    {
        var applicationLoggerFactory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
        var profileLoggerFactory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
        var coreContext = TestCoreServiceContext.Create(
            loggerFactory: applicationLoggerFactory,
            accessProfileLoggerFactory: profileId =>
            {
                Assert.Equal("siemens", profileId);
                return profileLoggerFactory;
            });

        var profileContext = coreContext.CreateAccessProfileContext("profile-root", "siemens");

        Assert.Same(profileLoggerFactory, profileContext.LoggerFactory);
    }

    [Fact]
    public void Operational_logging_disables_raw_events_outside_debug_level()
    {
        var logging = new OperationalLoggingOptions(
            Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "logs"),
            LogLevel.Information,
            2_000_000,
            3,
            rawEventLoggingEnabled: true);

        Assert.False(logging.RawEventLoggingEnabled);
    }
}
