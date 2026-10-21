using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Core.AccessProfiles;

namespace PLCSIM.UnitTest.App.Cli;

public sealed record CliApplicationOptions(
    string ProfileDiscoveryDirectory,
    string ProfileRegistryPath,
    string LogDirectory,
    LogLevel LogLevel,
    bool RawEventLoggingEnabled)
{
    public static CliApplicationOptions CreateDefault(
        LogLevel logLevel = LogLevel.Information,
        bool rawEventLoggingEnabled = false)
    {
        return new CliApplicationOptions(
            ResolvePath(
                "PLCSIM_UNITTEST_PROFILE_DISCOVERY_DIRECTORY",
                Path.Combine(AppContext.BaseDirectory, "profiles")),
            ResolvePath(
                "PLCSIM_UNITTEST_PROFILE_REGISTRY_PATH",
                ApplicationProfileRegistryOptions.DefaultRegistryPath),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PLCSIM.UnitTest",
                "logs"),
            logLevel,
            rawEventLoggingEnabled);
    }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileDiscoveryDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileRegistryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(LogDirectory);
    }

    private static string ResolvePath(string environmentVariable, string fallback)
    {
        var configuredPath = Environment.GetEnvironmentVariable(environmentVariable);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configuredPath) ? fallback : configuredPath);
    }
}
