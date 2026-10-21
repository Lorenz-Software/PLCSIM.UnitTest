using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Diagnostics;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.AccessProfiles;

public sealed class ApplicationProfileSelectionServiceTests
{
    [Fact]
    public async Task Evaluate_project_source_reports_supported_and_unsupported_registered_profiles()
    {
        var service = await CreateRegisteredSelectionServiceAsync(
            [CreateIsolatedFakePlugin()]);

        var supported = await service.EvaluateProjectSourceAsync("sample.json", CancellationToken.None);
        var unsupported = await service.EvaluateProjectSourceAsync("sample.ap21", CancellationToken.None);

        var supportedEntry = Assert.Single(supported.Profiles);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, supportedEntry.Profile.ProfileId);
        Assert.Equal(AccessProfileProjectSourceEvaluationState.Supported, supportedEntry.Evaluation.State);
        var unsupportedEntry = Assert.Single(unsupported.Profiles);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, unsupportedEntry.Profile.ProfileId);
        Assert.Equal(AccessProfileProjectSourceEvaluationState.Unsupported, unsupportedEntry.Evaluation.State);
    }

    [Fact]
    public async Task Evaluate_project_source_ignores_unavailable_profiles()
    {
        var plugin = new FakeAccessProfilePlugin(
            FakeAccessProfilePlugin.CreateDefaultInfo(),
            (_, creationContext) => FakeAccessProfileFactory.CreateServices(creationContext.TimeProvider),
            (_, _) => Task.FromResult(FakeAccessProfilePlugin.CreateUnavailableRegistrationResult()),
            new FileFakeProfileRegistryStore(new FixedFakeProfileRegistryPathResolver(CreateTempDirectory())));
        var service = await CreateRegisteredSelectionServiceAsync([plugin]);

        var result = await service.EvaluateProjectSourceAsync("sample.json", CancellationToken.None);

        Assert.Empty(result.Profiles);
    }

    [Fact]
    public async Task Evaluate_project_source_reports_plugin_exception_as_error_evaluation_and_diagnostic()
    {
        var sink = new DiagnosticSink();
        var plugin = new ThrowingProjectSourceEvaluationPlugin();
        var service = await CreateRegisteredSelectionServiceAsync([plugin], diagnosticSink: sink);

        var result = await service.EvaluateProjectSourceAsync("sample.throw", CancellationToken.None);

        var entry = Assert.Single(result.Profiles);
        Assert.Equal("throwing", entry.Profile.ProfileId);
        Assert.Equal(AccessProfileProjectSourceEvaluationState.Error, entry.Evaluation.State);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "Profile.Selection.ProjectSourceEvaluationFailed");
        Assert.Contains(sink.Current.Entries, entry =>
            entry.Current.Code == "Profile.Selection.ProjectSourceEvaluationFailed");
    }

    private static async Task<ApplicationProfileSelectionService> CreateRegisteredSelectionServiceAsync(
        IReadOnlyList<IAccessProfilePlugin> plugins,
        DiagnosticSink? diagnosticSink = null)
    {
        var coreContext = TestCoreServiceContext.Create(
            timeProvider: FakeTime.Create(),
            diagnosticSink: diagnosticSink);
        var registryStore = new FileApplicationProfileRegistryStore(
            coreContext,
            new ApplicationProfileRegistryOptions(CreateRegistryPath()),
            new AccessProfileCapabilityValidator());
        var pluginLoader = new AccessProfilePluginLoader(coreContext, plugins);
        var discoveryOptions = new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true);
        var registrationService = new ApplicationProfileRegistrationService(
            pluginLoader,
            discoveryOptions,
            registryStore,
            new AccessProfileCapabilityValidator(),
            coreContext);

        await registrationService.RefreshAllProfileRegistrationsAsync(TimeSpan.FromMinutes(3), CancellationToken.None);

        return new ApplicationProfileSelectionService(
            pluginLoader,
            discoveryOptions,
            registryStore,
            coreContext);
    }

    private static string CreateRegistryPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "plcsim-unittest-profile-selection-tests",
            Guid.NewGuid().ToString("N"),
            ApplicationProfileRegistryOptions.RegistryFileName);
    }

    private static FakeAccessProfilePlugin CreateIsolatedFakePlugin()
    {
        return FakeAccessProfileFactory.CreatePlugin(
            profileRegistryStore: new FileFakeProfileRegistryStore(
                new FixedFakeProfileRegistryPathResolver(CreateTempDirectory())));
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "plcsim-unittest-profile-selection-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class ThrowingProjectSourceEvaluationPlugin : IAccessProfilePlugin
    {
        public AccessProfileInfo Info { get; } = new(
            "throwing",
            "Throwing Profile",
            "1.0.0",
            "Throws during project-source evaluation.");

        public void Initialize(AccessProfileContext context)
        {
        }

        public Task<AccessProfileRegistrationResult> RegisterAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new AccessProfileRegistrationResult(
                AccessProfileRegistrationState.Registered,
                new AccessProfileCapabilities(
                    [new AccessApiCapability("throwing-api", "1.0.0", "1.0.0", CapabilityAvailability.Usable)],
                    [new ProjectSourceCapability("throwing-project", ".throw", "1.0.0", CapabilityAvailability.Usable)],
                    [new RuntimeCapability("throwing-runtime", "1.0.0", RuntimeLocality.Local, CapabilityAvailability.Usable)],
                    ["1.0.0"]),
                null));
        }

        public AccessProfileValidationResult ValidateConfig(AccessProfileConfig config)
        {
            return new AccessProfileValidationResult(true, []);
        }

        public Task<PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult> LoadAsync(
            AccessProfileSessionRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                PLCSIM.UnitTest.Common.AccessProfiles.AccessProfileSessionLoadResult.Failure(
                    "Not used by this test."));
        }

        public AccessProfileProjectSourceEvaluation EvaluateProjectSource(string projectSource)
        {
            throw new InvalidOperationException("Project-source evaluation failed.");
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
