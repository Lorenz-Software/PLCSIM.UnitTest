using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Artifacts;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.App.Wpf.Dialogs;
using PLCSIM.UnitTest.App.Wpf.Services;
using PLCSIM.UnitTest.App.Wpf.ViewModels;
using PLCSIM.UnitTest.Core;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Analysis;
using PLCSIM.UnitTest.Core.Diagnostics;
using PLCSIM.UnitTest.Core.Output;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.Exporters.JUnit;

namespace PLCSIM.UnitTest.App.Wpf;

internal static class WpfServiceRegistration
{
    private const long OperationalLogFileSizeLimitBytes = 10 * 1024 * 1024;
    private const int OperationalLogRetainedFileCount = 5;
    private const string ProfileDiscoveryDirectoryEnvironmentVariable = "PLCSIM_UNITTEST_PROFILE_DISCOVERY_DIRECTORY";
    private const string ProfileRegistryPathEnvironmentVariable = "PLCSIM_UNITTEST_PROFILE_REGISTRY_PATH";
    public static IServiceCollection AddWpfApplicationServices(
        this IServiceCollection services,
        IWpfApplicationSettingsService settingsService)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(settingsService);

        services.AddSingleton<AccessProfileDiscoveryOptions>(_ =>
            CreateProfileDiscoveryOptions(
                Environment.GetEnvironmentVariable(ProfileDiscoveryDirectoryEnvironmentVariable)));
        services.AddSingleton<DiagnosticSink>();
        services.AddSingleton<DiagnosticServices>(provider => new DiagnosticServices(
            provider.GetRequiredService<DiagnosticSink>()));
        services.AddSingleton<IDiagnosticWriter>(provider =>
            provider.GetRequiredService<DiagnosticServices>().Writer);
        services.AddSingleton(new OperationalLoggingOptions(
            settingsService.Current.ResolvedLogDirectory,
            settingsService.Current.AccessProfileLogLevel,
            OperationalLogFileSizeLimitBytes,
            OperationalLogRetainedFileCount,
            settingsService.Current.RawEventLoggingEnabled));
        services.AddSingleton<WpfAccessProfileLoggerFactory>();
        services.AddSingleton(new AccessProfileDataOptions(
            AccessProfileDataOptions.DefaultRootDirectory));
        services.AddSingleton<CoreServiceContext>(provider => new CoreServiceContext(
            provider.GetRequiredService<ILoggerFactory>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<DiagnosticServices>(),
            provider.GetRequiredService<OperationalLoggingOptions>(),
            provider.GetRequiredService<AccessProfileDataOptions>(),
            provider.GetRequiredService<WpfAccessProfileLoggerFactory>().GetLoggerFactory));
        services.AddSingleton(provider => new AccessProfilePluginLoader(
            provider.GetRequiredService<CoreServiceContext>(),
            Enumerable.Empty<IAccessProfilePlugin>()));
        services.AddSingleton<AccessProfileSessionService>();
        services.AddSingleton<AccessProfileCapabilityValidator>();
        services.AddSingleton(_ =>
        {
            var configuredPath = Environment.GetEnvironmentVariable(ProfileRegistryPathEnvironmentVariable);
            return new ApplicationProfileRegistryOptions(
                string.IsNullOrWhiteSpace(configuredPath)
                    ? ApplicationProfileRegistryOptions.DefaultRegistryPath
                    : System.IO.Path.GetFullPath(configuredPath));
        });
        services.AddSingleton<IApplicationProfileRegistryStore, FileApplicationProfileRegistryStore>();
        services.AddSingleton<IApplicationProfileRegistrationService, ApplicationProfileRegistrationService>();
        services.AddSingleton<IApplicationProfileSelectionService, ApplicationProfileSelectionService>();
        services.AddSingleton<RunSetupValidator>();
        services.AddSingleton<IArtifactExporter>(provider => new JUnitArtifactExporter(
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<EvidenceAnalysisPackageZipWriter>();
        services.AddSingleton<EvidenceAnalysisService>();
        services.AddSingleton<RunWorkflowService>();
        services.AddSingleton<RunSessionReleaseService>();
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<IWpfApplicationSettingsService>();
            var artifactExports = new ArtifactExportService(
                provider.GetRequiredService<CoreServiceContext>(),
                provider.GetServices<IArtifactExporter>(),
                settings.Current.ToArtifactExportOptions());
            settings.SettingsChanged += (_, _) => artifactExports.UpdateOptions(settings.Current.ToArtifactExportOptions());
            provider.GetRequiredService<RunWorkflowService>().PublicationEventHandler += artifactExports.OnWorkflowPublication;
            return artifactExports;
        });

        services.AddSingleton<IConfirmationDialogService, MessageBoxConfirmationDialogService>();
        services.AddSingleton<ISafetyProgramPasswordDialogService, SafetyProgramPasswordDialogService>();
        services.AddSingleton<IConfigurationFileLoadService, ConfigurationFileLoadService>();
        services.AddSingleton<IRunConfigurationFileDialogService, RunConfigurationFileDialogService>();
        services.AddSingleton<IProjectSourceFileDialogService, ProjectSourceFileDialogService>();
        services.AddSingleton<IAnalysisReportFileDialogService, AnalysisReportFileDialogService>();
        services.AddSingleton<ISettingsPathDialogService, SettingsPathDialogService>();
        services.AddSingleton<ISettingsDialogService, SettingsDialogService>();
        services.AddSingleton<IHelpContentOpenService, ShellHelpContentOpenService>();
        services.AddSingleton<IClipboardCopyService, ClipboardCopyService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<MainWindow>();

        return services;
    }

    internal static AccessProfileDiscoveryOptions CreateProfileDiscoveryOptions(string? configuredDirectory)
    {
        var profileDiscoveryDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? System.IO.Path.Combine(AppContext.BaseDirectory, "profiles")
            : System.IO.Path.GetFullPath(configuredDirectory);
        return new AccessProfileDiscoveryOptions(
            profileDiscoveryDirectory,
            IncludeBuiltInProfiles: false);
    }
}
