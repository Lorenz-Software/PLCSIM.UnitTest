using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.App.Cli;
using PLCSIM.UnitTest.App.Cli.Commands;
using PLCSIM.UnitTest.App.Cli.Configuration;
using PLCSIM.UnitTest.Common.Artifacts;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Core;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Workflow;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Cli;

public sealed class CliApplicationHostTests : IDisposable
{
    private readonly string temporaryDirectory;

    public CliApplicationHostTests()
    {
        temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.CliApplicationHostTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
    }

    [Fact]
    public async Task Create_composes_cli_services_without_concrete_profile_references()
    {
        await using var provider = CliApplicationHost.Create(CreateOptions());

        Assert.NotNull(provider.GetRequiredService<CoreServiceContext>());
        Assert.NotNull(provider.GetRequiredService<AccessProfilePluginLoader>());
        Assert.NotNull(provider.GetRequiredService<AccessProfileSessionService>());
        Assert.NotNull(provider.GetRequiredService<IApplicationProfileRegistryStore>());
        Assert.NotNull(provider.GetRequiredService<IApplicationProfileRegistrationService>());
        Assert.NotNull(provider.GetRequiredService<IApplicationProfileSelectionService>());
        Assert.NotNull(provider.GetRequiredService<ICliConfigurationFileLoadService>());
        Assert.NotNull(provider.GetRequiredService<ICliConfigurationCheckService>());
        Assert.NotNull(provider.GetRequiredService<ICliProfileListService>());
        Assert.NotNull(provider.GetRequiredService<ICliProfileRegistrationService>());
        Assert.NotNull(provider.GetRequiredService<RunWorkflowService>());
        Assert.NotNull(provider.GetRequiredService<RunSessionReleaseService>());
        Assert.NotEmpty(provider.GetServices<IArtifactExporter>());
    }

    [Fact]
    public async Task Create_uses_configured_paths_and_logging_policy()
    {
        var options = CreateOptions(LogLevel.Debug, rawEventLoggingEnabled: true);
        await using var provider = CliApplicationHost.Create(options);

        var discovery = provider.GetRequiredService<AccessProfileDiscoveryOptions>();
        var registry = provider.GetRequiredService<ApplicationProfileRegistryOptions>();
        var logging = provider.GetRequiredService<OperationalLoggingOptions>();

        Assert.Equal(Path.GetFullPath(options.ProfileDiscoveryDirectory), discovery.ProfileDiscoveryDirectory);
        Assert.False(discovery.IncludeBuiltInProfiles);
        Assert.Equal(Path.GetFullPath(options.ProfileRegistryPath), registry.RegistryPath);
        Assert.Equal(Path.GetFullPath(options.LogDirectory), logging.LogDirectory);
        Assert.Equal(LogLevel.Debug, logging.MinimumLevel);
        Assert.True(logging.RawEventLoggingEnabled);
    }

    [Fact]
    public async Task Create_does_not_create_the_profile_discovery_directory()
    {
        var options = CreateOptions();

        await using var provider = CliApplicationHost.Create(options);

        Assert.False(Directory.Exists(options.ProfileDiscoveryDirectory));
    }

    [Fact]
    public async Task Registry_store_can_initialize_an_empty_cli_registry()
    {
        await using var provider = CliApplicationHost.Create(CreateOptions());

        var registry = await provider
            .GetRequiredService<IApplicationProfileRegistryStore>()
            .EnsureLoadedAsync(CancellationToken.None);

        Assert.Empty(registry.Profiles);
        Assert.True(File.Exists(CreateOptions().ProfileRegistryPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private CliApplicationOptions CreateOptions(
        LogLevel logLevel = LogLevel.Information,
        bool rawEventLoggingEnabled = false)
    {
        return new CliApplicationOptions(
            Path.Combine(temporaryDirectory, "profiles"),
            Path.Combine(temporaryDirectory, "registry", "application-profile-registry.json"),
            Path.Combine(temporaryDirectory, "logs"),
            logLevel,
            rawEventLoggingEnabled);
    }
}
