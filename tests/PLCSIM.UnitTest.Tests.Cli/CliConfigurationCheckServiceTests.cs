using PLCSIM.UnitTest.App.Cli.Commands;
using PLCSIM.UnitTest.App.Cli.Configuration;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Workflow;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Cli;

public sealed class CliConfigurationCheckServiceTests : IDisposable
{
    private readonly string temporaryDirectory;

    public CliConfigurationCheckServiceTests()
    {
        temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.CliConfigurationCheckServiceTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
    }

    [Fact]
    public async Task Check_async_accepts_common_configuration_for_registered_profile()
    {
        var path = WriteConfiguration("fake", "1.0.0");
        var profile = CreateProfile(
            "fake",
            "1.0.0",
            AccessProfileRegistrationState.Registered);
        var service = CreateService(profile);

        var result = await service.CheckAsync(path, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(CliConfigurationCheckState.Valid, result.State);
        Assert.Same(profile, result.Profile);
        Assert.NotNull(result.Setup);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Check_async_accepts_any_registered_version_when_version_is_omitted()
    {
        var path = WriteConfiguration("fake", null);
        var profile = CreateProfile(
            "fake",
            "2.0.0",
            AccessProfileRegistrationState.Registered);

        var result = await CreateService(profile).CheckAsync(path, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Same(profile, result.Profile);
    }

    [Fact]
    public async Task Check_async_rejects_invalid_common_configuration_before_registry_access()
    {
        var path = Path.Combine(temporaryDirectory, "invalid.config.json");
        File.WriteAllText(path, "{");
        var registry = new RecordingRegistryStore(CreateRegistry());
        var registrationService = new RecordingRegistrationService(
            ApplicationProfileRegistrationCheckStatus.Current);

        var result = await CreateService(registry, registrationService)
            .CheckAsync(path, CancellationToken.None);

        Assert.Equal(CliConfigurationCheckState.InvalidConfiguration, result.State);
        Assert.False(registry.WasLoaded);
        Assert.Equal(0, registrationService.CheckCallCount);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "Cli.Configuration.Json.Invalid");
    }

    [Theory]
    [InlineData(AccessProfileRegistrationState.Unavailable)]
    [InlineData(AccessProfileRegistrationState.Error)]
    public async Task Check_async_reports_profile_unavailable_for_non_registered_entry(
        AccessProfileRegistrationState registrationState)
    {
        var path = WriteConfiguration("fake", "1.0.0");
        var profile = CreateProfile("fake", "1.0.0", registrationState);

        var result = await CreateService(profile).CheckAsync(path, CancellationToken.None);

        Assert.Equal(CliConfigurationCheckState.ProfileUnavailable, result.State);
        Assert.Null(result.Profile);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "Cli.Configuration.AccessProfile.Unavailable");
    }

    [Fact]
    public async Task Check_async_reports_profile_unavailable_for_version_mismatch()
    {
        var path = WriteConfiguration("fake", "2.0.0");
        var profile = CreateProfile(
            "fake",
            "1.0.0",
            AccessProfileRegistrationState.Registered);

        var result = await CreateService(profile).CheckAsync(path, CancellationToken.None);

        Assert.Equal(CliConfigurationCheckState.ProfileUnavailable, result.State);
        Assert.Null(result.Profile);
    }

    [Fact]
    public async Task Check_async_does_not_load_or_evaluate_profile_plugin()
    {
        var path = WriteConfiguration("fake", "1.0.0");
        var registry = new RecordingRegistryStore(CreateRegistry(
            CreateProfile("fake", "1.0.0", AccessProfileRegistrationState.Registered)));

        var result = await CreateService(registry).CheckAsync(path, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.True(registry.WasLoaded);
    }

    [Theory]
    [InlineData(ApplicationProfileRegistrationCheckStatus.RefreshRequired)]
    [InlineData(ApplicationProfileRegistrationCheckStatus.NoProfilesDiscovered)]
    [InlineData(ApplicationProfileRegistrationCheckStatus.Failed)]
    public async Task Check_async_reports_profile_unavailable_when_registration_is_not_current(
        ApplicationProfileRegistrationCheckStatus status)
    {
        var path = WriteConfiguration("fake", "1.0.0");
        var registry = new RecordingRegistryStore(CreateRegistry(
            CreateProfile("fake", "1.0.0", AccessProfileRegistrationState.Registered)));
        var detailedDiagnostic = new Diagnostic(
            "Profile.Registry.Check.Detail",
            DiagnosticCategory.Configuration,
            Microsoft.Extensions.Logging.LogLevel.Error,
            DiagnosticSource.AccessProfileLoader,
            DiagnosticPhase.ProfileValidation,
            "Detailed registration check failure.",
            new DiagnosticCorrelation(),
            DateTimeOffset.UtcNow);
        var registrationService = new RecordingRegistrationService(
            status,
            [detailedDiagnostic]);

        var result = await CreateService(registry, registrationService)
            .CheckAsync(path, CancellationToken.None);

        Assert.Equal(CliConfigurationCheckState.ProfileUnavailable, result.State);
        Assert.Null(result.Profile);
        Assert.False(registry.WasLoaded);
        Assert.Equal(1, registrationService.CheckCallCount);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == detailedDiagnostic.Code);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "Cli.Configuration.AccessProfile.RegistrationNotCurrent"
            && diagnostic.Message.Contains("register-profiles", StringComparison.Ordinal));
        Assert.Equal(0, registrationService.RefreshAllCallCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private string WriteConfiguration(string profileId, string? profileVersion)
    {
        var version = profileVersion is null
            ? string.Empty
            : $", \"version\": \"{profileVersion}\"";
        var path = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.config.json");
        File.WriteAllText(
            path,
            $$"""
            {
              "version": 1,
              "project": { "source": "fake-project.ap" },
              "accessProfile": {
                "id": "{{profileId}}"{{version}},
                "values": { "opaque": true }
              }
            }
            """);
        return path;
    }

    private static CliConfigurationCheckService CreateService(
        params AccessProfileRegistryEntry[] profiles)
    {
        return CreateService(new RecordingRegistryStore(CreateRegistry(profiles)));
    }

    private static CliConfigurationCheckService CreateService(
        IApplicationProfileRegistryStore registry)
    {
        return CreateService(
            registry,
            new RecordingRegistrationService(ApplicationProfileRegistrationCheckStatus.Current));
    }

    private static CliConfigurationCheckService CreateService(
        IApplicationProfileRegistryStore registry,
        IApplicationProfileRegistrationService registrationService)
    {
        var timeProvider = TimeProvider.System;
        return new CliConfigurationCheckService(
            new CliConfigurationFileLoadService(
                new RunSetupValidator(timeProvider),
                timeProvider),
            registrationService,
            registry,
            timeProvider);
    }

    private static ApplicationProfileRegistry CreateRegistry(
        params AccessProfileRegistryEntry[] profiles)
    {
        var now = DateTimeOffset.UtcNow;
        return new ApplicationProfileRegistry(
            ApplicationProfileRegistry.CurrentSchemaVersion,
            now,
            now,
            profiles);
    }

    private static AccessProfileRegistryEntry CreateProfile(
        string profileId,
        string profileVersion,
        AccessProfileRegistrationState state)
    {
        return new AccessProfileRegistryEntry(
            profileId,
            profileVersion,
            $"{profileId} profile",
            new AccessProfileRegistrySource(
                "DirectoryPackage",
                Path.Combine("profiles", profileId),
                "fingerprint"),
            new AccessProfileRegistryRegistration(
                state,
                DateTimeOffset.UtcNow,
                null),
            AccessProfileCapabilities.Empty);
    }

    private sealed class RecordingRegistryStore : IApplicationProfileRegistryStore
    {
        private readonly ApplicationProfileRegistry registry;

        public RecordingRegistryStore(ApplicationProfileRegistry registry)
        {
            this.registry = registry;
        }

        public bool WasLoaded { get; private set; }

        public Task<ApplicationProfileRegistry> EnsureLoadedAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasLoaded = true;
            return Task.FromResult(registry);
        }

        public Task<ApplicationProfileRegistry> LoadAsync(
            CancellationToken cancellationToken)
        {
            return EnsureLoadedAsync(cancellationToken);
        }

        public Task SaveAsync(
            ApplicationProfileRegistry registry,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingRegistrationService : IApplicationProfileRegistrationService
    {
        private readonly ApplicationProfileRegistrationCheckStatus status;
        private readonly IReadOnlyList<Diagnostic> diagnostics;

        public RecordingRegistrationService(
            ApplicationProfileRegistrationCheckStatus status,
            IReadOnlyList<Diagnostic>? diagnostics = null)
        {
            this.status = status;
            this.diagnostics = diagnostics ?? [];
        }

        public int CheckCallCount { get; private set; }

        public int RefreshAllCallCount { get; private set; }

        public Task<ApplicationProfileRegistrationCheckResult> CheckProfileRegistrationsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckCallCount++;
            return Task.FromResult(new ApplicationProfileRegistrationCheckResult(
                status,
                $"Registration check returned {status}.",
                diagnostics));
        }

        public Task<ApplicationProfileRegistrationRefreshResult> RefreshProfileRegistrationAsync(
            AccessProfileCandidateKey profile,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Configuration checks must not refresh registrations.");
        }

        public Task<ApplicationProfileRegistrationRefreshAllResult> RefreshAllProfileRegistrationsAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            RefreshAllCallCount++;
            throw new InvalidOperationException("Configuration checks must not refresh registrations.");
        }
    }
}
