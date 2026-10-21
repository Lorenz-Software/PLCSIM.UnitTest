namespace PLCSIM.UnitTest.Common.Configuration;

/// <summary>
/// First-target defaults for run configuration and output policy.
/// </summary>
public static class RunConfigurationDefaults
{
    public const int CurrentVersion = 2;
    public const string DefaultOutputDirectory = "artifacts";
    public const bool DefaultTestResultOutput = true;

    public static TimeSpan DefaultRegistrationRefreshTimeout { get; } = TimeSpan.FromMinutes(3);
    public static TimeSpan DefaultProfileLoadTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan DefaultProfileUnloadTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan DefaultProjectDiscoveryTimeout { get; } = TimeSpan.FromMinutes(3);
    public static TimeSpan DefaultRuntimeDiscoveryTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan DefaultProjectPrepareTimeout { get; } = TimeSpan.FromMinutes(1);
    public static TimeSpan DefaultBuildTransferTimeout { get; } = TimeSpan.FromMinutes(10);
    public static TimeSpan DefaultProjectReleaseTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan DefaultRuntimePrepareTimeout { get; } = TimeSpan.FromMinutes(1);
    public static TimeSpan DefaultRuntimeReleaseTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan DefaultPlcCommandTimeout { get; } = TimeSpan.FromSeconds(10);
    public static TimeSpan DefaultPlcStartReactionTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan DefaultPlcStopReactionTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan DefaultUnitTestCommandTimeout { get; } = TimeSpan.FromSeconds(3);
    public static TimeSpan DefaultUnitTestCommandReactionTimeout { get; } = TimeSpan.FromSeconds(10);
    public static TimeSpan DefaultTestRunTimeout { get; } = TimeSpan.FromMinutes(2);
    public static TimeSpan DefaultFinalEventTimeout { get; } = TimeSpan.FromSeconds(10);
    public static TimeSpan DefaultCommunicationPrepareTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan DefaultCommunicationRevalidationTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan DefaultMonitoringTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan DefaultCleanupTimeout { get; } = TimeSpan.FromMinutes(2);

    public static RunOperationTimeoutPolicy CreateDefaultTimeoutPolicy()
    {
        return new RunOperationTimeoutPolicy(
            DefaultProjectPrepareTimeout,
            DefaultBuildTransferTimeout,
            DefaultRuntimePrepareTimeout,
            DefaultPlcCommandTimeout,
            DefaultPlcStartReactionTimeout,
            DefaultPlcStopReactionTimeout,
            DefaultCommunicationPrepareTimeout,
            DefaultCommunicationRevalidationTimeout,
            DefaultUnitTestCommandTimeout,
            DefaultUnitTestCommandReactionTimeout,
            DefaultTestRunTimeout,
            DefaultFinalEventTimeout);
    }

    public static ApplicationOperationTimeoutPolicy CreateDefaultApplicationOperationTimeoutPolicy()
    {
        return new ApplicationOperationTimeoutPolicy(
            DefaultRegistrationRefreshTimeout,
            DefaultProfileLoadTimeout,
            DefaultProfileUnloadTimeout,
            DefaultProjectDiscoveryTimeout,
            DefaultRuntimeDiscoveryTimeout,
            DefaultProjectReleaseTimeout,
            DefaultRuntimeReleaseTimeout,
            DefaultMonitoringTimeout,
            DefaultCleanupTimeout);
    }

    public static OutputPolicy CreateDefaultOutputPolicy()
    {
        return new OutputPolicy(DefaultOutputDirectory, DefaultTestResultOutput);
    }

    public static CommunicationTimeoutPolicy CreateDefaultCommunicationTimeoutPolicy()
    {
        return new CommunicationTimeoutPolicy(
            DefaultCommunicationPrepareTimeout,
            DefaultCommunicationRevalidationTimeout);
    }
}
