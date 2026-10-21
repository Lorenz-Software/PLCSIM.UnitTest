using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Core;
using PLCSIM.UnitTest.Core.AccessProfiles;

namespace PLCSIM.UnitTest.Testing.AccessProfiles;

public sealed record AccessProfileContractConformanceCase(
    string ProfileDiscoveryRoot,
    string ProfileId,
    string ProfileVersion,
    string ProjectSource,
    AccessProfileConfigurationPayload Configuration,
    ProjectOwnershipMode ProjectOwnershipMode,
    RuntimeProvisioningMode RuntimeProvisioningMode);

public sealed record AccessProfileContractConformanceCheck(
    string Name,
    bool Passed,
    string? Detail = null);

public sealed record AccessProfileContractConformanceReport(
    IReadOnlyList<AccessProfileContractConformanceCheck> Checks)
{
    public bool Passed => Checks.All(check => check.Passed);

    public IReadOnlyList<AccessProfileContractConformanceCheck> Failures =>
        Checks.Where(check => !check.Passed).ToArray();
}

public static class AccessProfileContractConformanceHarness
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    public static async Task<AccessProfileContractConformanceReport> RunAsync(
        AccessProfileContractConformanceCase testCase,
        CoreServiceContext coreContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(coreContext);

        var checks = new List<AccessProfileContractConformanceCheck>();
        var discoveryOptions = new AccessProfileDiscoveryOptions(
            testCase.ProfileDiscoveryRoot,
            IncludeBuiltInProfiles: false);
        var loader = new AccessProfilePluginLoader(coreContext, []);
        var discovery = loader.Discover(discoveryOptions);
        var candidate = discovery.Candidates.SingleOrDefault(candidate =>
            candidate.Info.ProfileId == testCase.ProfileId
            && candidate.Info.Version == testCase.ProfileVersion);

        Add(
            checks,
            "package.discovery",
            candidate is not null
            && candidate.State == AccessProfileCandidateState.Discovered
            && candidate.SourceKind == AccessProfileSourceKind.DirectoryPackage,
            candidate is null
                ? "The expected package candidate was not discovered."
                : $"Candidate state was {candidate.State} from {candidate.SourceKind}.");
        Add(
            checks,
            "package.discovery.diagnostics",
            discovery.Diagnostics.Count == 0,
            JoinDiagnosticCodes(discovery.Diagnostics));
        Add(
            checks,
            "package.configuration-documentation",
            candidate is not null
            && File.Exists(Path.Combine(candidate.SourcePath, "docs", "configuration.md")),
            "The directory package does not contain docs/configuration.md.");

        if (candidate is null)
        {
            return new AccessProfileContractConformanceReport(checks);
        }

        var registryPath = Path.Combine(
            Path.GetTempPath(),
            "plcsim-unittest-profile-conformance",
            Guid.NewGuid().ToString("N"),
            ApplicationProfileRegistryOptions.RegistryFileName);
        var registryStore = new FileApplicationProfileRegistryStore(
            coreContext,
            new ApplicationProfileRegistryOptions(registryPath),
            new AccessProfileCapabilityValidator());
        var registrationService = new ApplicationProfileRegistrationService(
            loader,
            discoveryOptions,
            registryStore,
            new AccessProfileCapabilityValidator(),
            coreContext);
        var registration = await registrationService.RefreshProfileRegistrationAsync(
            new AccessProfileCandidateKey(
                candidate.Info.ProfileId,
                candidate.Info.Version,
                candidate.SourceKind.ToString(),
                candidate.SourcePath),
            OperationTimeout,
            cancellationToken).ConfigureAwait(false);

        Add(
            checks,
            "package.registration",
            registration.Status == ApplicationProfileRegistrationRefreshStatus.Completed
            && registration.Entry?.Registration.State == AccessProfileRegistrationState.Registered,
            registration.Message);
        Add(
            checks,
            "package.capabilities",
            registration.Entry?.Capabilities is not null
            && registration.Entry.Capabilities != AccessProfileCapabilities.Empty,
            "Registration did not publish a non-empty capability projection.");

        await using var sessionService = new AccessProfileSessionService(
            loader,
            discoveryOptions,
            coreContext);
        var load = await sessionService.LoadAsync(
            new AccessProfileSessionRequest(
                testCase.ProfileId,
                testCase.ProfileVersion,
                testCase.ProjectSource,
                testCase.ProjectOwnershipMode),
            OperationTimeout,
            cancellationToken).ConfigureAwait(false);

        Add(
            checks,
            "session.load",
            load.State == AccessProfileLoadState.Loaded && load.Session is not null,
            $"Load state was {load.State}; diagnostics: {JoinDiagnosticCodes(load.Diagnostics)}");
        if (load.Session is null)
        {
            return new AccessProfileContractConformanceReport(checks);
        }

        var session = load.Session;
        var profile = session.Profile;
        Add(
            checks,
            "session.metadata",
            profile.Info.ProfileId == testCase.ProfileId
            && profile.Info.Version == testCase.ProfileVersion,
            $"Loaded {profile.Info.ProfileId}@{profile.Info.Version}.");
        Add(checks, "session.health", profile.Health is not null);
        Add(checks, "session.configuration.service", profile.Configuration is not null);
        Add(checks, "session.project.service", profile.Project is not null);
        Add(checks, "session.plc-control.service", profile.PlcControl is not null);
        Add(checks, "session.lifecycle.service", profile.UnitTestLifecycle is not null);
        Add(checks, "session.event-stream.service", profile.UnitTestEventStream is not null);

        var configuration = profile.Configuration
            ?? throw new InvalidOperationException("The loaded profile returned no configuration service.");
        var loadedConfiguration = configuration.LoadConfig(testCase.Configuration.Json);
        Add(
            checks,
            "configuration.load",
            loadedConfiguration.IsSuccessful && loadedConfiguration.Value is not null,
            JoinDiagnosticCodes(loadedConfiguration.Diagnostics));
        var serializedConfiguration = configuration.SerializeConfig();
        Add(
            checks,
            "configuration.serialize",
            serializedConfiguration.IsSuccessful
            && !string.IsNullOrWhiteSpace(serializedConfiguration.Value),
            JoinDiagnosticCodes(serializedConfiguration.Diagnostics));
        var fields = configuration.GetFields(testCase.RuntimeProvisioningMode);
        Add(
            checks,
            "configuration.projection",
            fields.IsSuccessful && fields.Value is not null,
            JoinDiagnosticCodes(fields.Diagnostics));
        var validation = configuration.ValidateConfig(testCase.RuntimeProvisioningMode);
        Add(
            checks,
            "configuration.validation",
            validation.IsValid,
            JoinDiagnosticCodes(validation.Diagnostics));

        await sessionService.UnloadAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
        Add(checks, "session.unload", sessionService.CurrentSession is null);
        await sessionService.UnloadAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
        Add(checks, "session.unload.idempotent", sessionService.CurrentSession is null);

        return new AccessProfileContractConformanceReport(checks);
    }

    private static void Add(
        ICollection<AccessProfileContractConformanceCheck> checks,
        string name,
        bool passed,
        string? detail = null)
    {
        checks.Add(new AccessProfileContractConformanceCheck(name, passed, passed ? null : detail));
    }

    private static string JoinDiagnosticCodes(
        IEnumerable<PLCSIM.UnitTest.Common.Diagnostics.Diagnostic> diagnostics)
    {
        return string.Join(", ", diagnostics.Select(diagnostic => diagnostic.Code));
    }
}
