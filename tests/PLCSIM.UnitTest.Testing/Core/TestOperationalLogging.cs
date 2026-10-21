using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Logging;

namespace PLCSIM.UnitTest.Testing.Core;

public static class TestOperationalLogging
{
    public static OperationalLoggingOptions Options { get; } = new(
        Path.GetTempPath(),
        LogLevel.Information,
        10 * 1024 * 1024,
        5);
}
