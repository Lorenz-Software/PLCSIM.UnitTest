using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using PLCSIM.UnitTest.Common.Logging;

namespace PLCSIM.UnitTest.Testing.Time
{
    internal static class FakeTime
    {
        public static DateTimeOffset DefaultStart { get; } =
            new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        public static FakeTimeProvider Create()
        {
            return new FakeTimeProvider(DefaultStart);
        }

        public static FakeTimeProvider Create(DateTimeOffset start)
        {
            return new FakeTimeProvider(start);
        }
    }
}

namespace PLCSIM.UnitTest.Testing.Core
{
    internal static class TestOperationalLogging
    {
        public static OperationalLoggingOptions Options { get; } = new(
            Path.GetTempPath(),
            LogLevel.Information,
            10 * 1024 * 1024,
            5);
    }
}
