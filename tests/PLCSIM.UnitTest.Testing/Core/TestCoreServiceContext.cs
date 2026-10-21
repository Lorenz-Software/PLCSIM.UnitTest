using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Core;
using PLCSIM.UnitTest.Core.Diagnostics;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Core.AccessProfiles;

namespace PLCSIM.UnitTest.Testing.Core;

public static class TestCoreServiceContext
{
    public static CoreServiceContext Create(
        TimeProvider? timeProvider = null,
        DiagnosticSink? diagnosticSink = null,
        ILoggerFactory? loggerFactory = null,
        OperationalLoggingOptions? logging = null,
        string? accessProfileDataRootDirectory = null,
        Func<string, ILoggerFactory>? accessProfileLoggerFactory = null)
    {
        return new CoreServiceContext(
            loggerFactory ?? NullLoggerFactory.Instance,
            timeProvider ?? TimeProvider.System,
            new DiagnosticServices(diagnosticSink ?? new DiagnosticSink()),
            logging ?? new OperationalLoggingOptions(
                Path.GetTempPath(),
                LogLevel.Information,
                10 * 1024 * 1024,
                5),
            new AccessProfileDataOptions(accessProfileDataRootDirectory ?? Path.Combine(
                Path.GetTempPath(),
                "PLCSIM.UnitTest",
                "tests",
                "profile-data")),
            accessProfileLoggerFactory);
    }
}
