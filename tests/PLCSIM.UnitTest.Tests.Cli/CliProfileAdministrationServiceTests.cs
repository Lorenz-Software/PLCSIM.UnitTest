using PLCSIM.UnitTest.App.Cli.Commands;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Core.AccessProfiles;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Cli;

public sealed class CliProfileAdministrationServiceTests
{
    [Fact]
    public async Task List_async_reads_persisted_registry_without_ensuring_or_refreshing_it()
    {
        var registry = new RecordingRegistryStore(CreateRegistry(
            CreateProfile("zeta", "1.0.0"),
            CreateProfile("alpha", "2.0.0"),
            CreateProfile("alpha", "1.0.0")));
        var service = new CliProfileListService(registry);

        var profiles = await service.ListAsync(CancellationToken.None);

        Assert.False(registry.EnsureLoadedCalled);
        Assert.True(registry.LoadCalled);
        Assert.Collection(
            profiles,
            profile => Assert.Equal(("alpha", "1.0.0"), (profile.ProfileId, profile.ProfileVersion)),
            profile => Assert.Equal(("alpha", "2.0.0"), (profile.ProfileId, profile.ProfileVersion)),
            profile => Assert.Equal(("zeta", "1.0.0"), (profile.ProfileId, profile.ProfileVersion)));
    }

    [Fact]
    public async Task List_async_returns_successful_empty_snapshot()
    {
        var service = new CliProfileListService(
            new RecordingRegistryStore(CreateRegistry()));

        var profiles = await service.ListAsync(CancellationToken.None);

        Assert.Empty(profiles);
    }

    [Fact]
    public async Task Register_async_uses_existing_refresh_all_with_fixed_timeout()
    {
        var expected = new ApplicationProfileRegistrationRefreshAllResult(
            ApplicationProfileRegistrationRefreshStatus.Completed,
            CreateRegistry(CreateProfile("fake", "1.0.0")),
            [],
            "completed");
        var registrations = new RecordingRegistrationService(expected);
        var service = new CliProfileRegistrationService(registrations);

        var result = await service.RegisterAsync(CancellationToken.None);

        Assert.Same(expected, result);
        Assert.Equal(
            CliProfileRegistrationService.RegistrationTimeout,
            registrations.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(3), registrations.Timeout);
        Assert.Equal(1, registrations.RefreshAllCallCount);
        Assert.Equal(0, registrations.RefreshSingleCallCount);
    }

    [Fact]
    public async Task Register_async_propagates_caller_cancellation()
    {
        var registrations = new RecordingRegistrationService(
            new ApplicationProfileRegistrationRefreshAllResult(
                ApplicationProfileRegistrationRefreshStatus.Completed,
                CreateRegistry(),
                [],
                null));
        var service = new CliProfileRegistrationService(registrations);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RegisterAsync(cancellation.Token));

        Assert.Equal(0, registrations.RefreshAllCallCount);
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
        string profileVersion)
    {
        return new AccessProfileRegistryEntry(
            profileId,
            profileVersion,
            $"{profileId} profile",
            new AccessProfileRegistrySource(
                "DirectoryPackage",
                Path.Combine("profiles", profileId, profileVersion),
                "fingerprint"),
            new AccessProfileRegistryRegistration(
                AccessProfileRegistrationState.Registered,
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

        public bool EnsureLoadedCalled { get; private set; }

        public bool LoadCalled { get; private set; }

        public Task<ApplicationProfileRegistry> EnsureLoadedAsync(
            CancellationToken cancellationToken)
        {
            EnsureLoadedCalled = true;
            throw new InvalidOperationException(
                "list-profiles must not initialize or repair the registry.");
        }

        public Task<ApplicationProfileRegistry> LoadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCalled = true;
            return Task.FromResult(registry);
        }

        public Task SaveAsync(
            ApplicationProfileRegistry registry,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(
                "list-profiles must not write the registry.");
        }
    }

    private sealed class RecordingRegistrationService
        : IApplicationProfileRegistrationService
    {
        private readonly ApplicationProfileRegistrationRefreshAllResult result;

        public RecordingRegistrationService(
            ApplicationProfileRegistrationRefreshAllResult result)
        {
            this.result = result;
        }

        public int RefreshAllCallCount { get; private set; }

        public int RefreshSingleCallCount { get; private set; }

        public TimeSpan? Timeout { get; private set; }

        public Task<ApplicationProfileRegistrationCheckResult> CheckProfileRegistrationsAsync(
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ApplicationProfileRegistrationRefreshResult>
            RefreshProfileRegistrationAsync(
                AccessProfileCandidateKey profile,
                TimeSpan timeout,
                CancellationToken cancellationToken)
        {
            RefreshSingleCallCount++;
            throw new NotSupportedException();
        }

        public Task<ApplicationProfileRegistrationRefreshAllResult>
            RefreshAllProfileRegistrationsAsync(
                TimeSpan timeout,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshAllCallCount++;
            Timeout = timeout;
            return Task.FromResult(result);
        }
    }
}
