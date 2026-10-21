using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.App.Cli.Commands;
using PLCSIM.UnitTest.App.Cli.Configuration;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Artifacts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Core;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Analysis;
using PLCSIM.UnitTest.Core.Diagnostics;
using PLCSIM.UnitTest.Core.Output;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.Exporters.JUnit;

namespace PLCSIM.UnitTest.App.Cli;

public static class CliServiceRegistration
{
    private const long OperationalLogFileSizeLimitBytes = 10 * 1024 * 1024;
    private const int OperationalLogRetainedFileCount = 5;

    public static IServiceCollection AddCliApplicationServices(
        this IServiceCollection services,
        CliApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        Directory.CreateDirectory(options.LogDirectory);

        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new AccessProfileDiscoveryOptions(
            Path.GetFullPath(options.ProfileDiscoveryDirectory),
            IncludeBuiltInProfiles: false));
        services.AddSingleton(new ApplicationProfileRegistryOptions(
            Path.GetFullPath(options.ProfileRegistryPath)));

        services.AddSingleton<DiagnosticSink>();
        services.AddSingleton<DiagnosticServices>(provider => new DiagnosticServices(
            provider.GetRequiredService<DiagnosticSink>()));
        services.AddSingleton<IDiagnosticWriter>(provider =>
            provider.GetRequiredService<DiagnosticServices>().Writer);
        services.AddSingleton(new OperationalLoggingOptions(
            Path.GetFullPath(options.LogDirectory),
            options.LogLevel,
            OperationalLogFileSizeLimitBytes,
            OperationalLogRetainedFileCount,
            options.RawEventLoggingEnabled));
        services.AddSingleton<CliAccessProfileLoggerFactory>();
        services.AddSingleton(new AccessProfileDataOptions(
            AccessProfileDataOptions.DefaultRootDirectory));
        services.AddSingleton<CoreServiceContext>(provider => new CoreServiceContext(
            provider.GetRequiredService<ILoggerFactory>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<DiagnosticServices>(),
            provider.GetRequiredService<OperationalLoggingOptions>(),
            provider.GetRequiredService<AccessProfileDataOptions>(),
            provider.GetRequiredService<CliAccessProfileLoggerFactory>().GetLoggerFactory));

        services.AddSingleton(provider => new AccessProfilePluginLoader(
            provider.GetRequiredService<CoreServiceContext>(),
            Enumerable.Empty<IAccessProfilePlugin>()));
        services.AddSingleton<AccessProfileSessionService>();
        services.AddSingleton<AccessProfileCapabilityValidator>();
        services.AddSingleton<IApplicationProfileRegistryStore, FileApplicationProfileRegistryStore>();
        services.AddSingleton<IApplicationProfileRegistrationService, ApplicationProfileRegistrationService>();
        services.AddSingleton<IApplicationProfileSelectionService, ApplicationProfileSelectionService>();

        services.AddSingleton<RunSetupValidator>();
        services.AddSingleton<ICliConfigurationFileLoadService, CliConfigurationFileLoadService>();
        services.AddSingleton<ICliConfigurationCheckService, CliConfigurationCheckService>();
        services.AddSingleton<ICliProfileListService, CliProfileListService>();
        services.AddSingleton<ICliProfileRegistrationService, CliProfileRegistrationService>();
        services.AddSingleton<ICliFullRunService, CliFullRunService>();
        services.AddSingleton<IArtifactExporter>(provider => new JUnitArtifactExporter(
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new ArtifactExportService(
            provider.GetRequiredService<CoreServiceContext>(),
            provider.GetServices<IArtifactExporter>()));
        services.AddSingleton<EvidenceAnalysisService>();
        services.AddSingleton<RunWorkflowService>();
        services.AddSingleton<RunSessionReleaseService>();

        return services;
    }
}
