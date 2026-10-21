using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Diagnostics;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.AccessProfiles;

public sealed class ApplicationProfileRegistryTests
{
    [Fact]
    public void Validator_rejects_invalid_capability_tokens()
    {
        var validator = new AccessProfileCapabilityValidator();
        var result = new AccessProfileRegistrationResult(
            AccessProfileRegistrationState.Registered,
            new AccessProfileCapabilities(
                [new AccessApiCapability("Fake Api", "1.0.0", "1.0.0", CapabilityAvailability.Usable)],
                [],
                [],
                ["1.0.0"]),
            null);

        var validation = validator.Validate(result);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("Access API kind", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Registry_store_creates_missing_registry()
    {
        var path = CreateRegistryPath();
        var store = CreateStore(path);

        var registry = await store.EnsureLoadedAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(ApplicationProfileRegistry.CurrentSchemaVersion, registry.SchemaVersion);
        Assert.Empty(registry.Profiles);
    }

    [Fact]
    public void Registry_store_creates_missing_registry_without_pumping_captured_context()
    {
        var path = CreateRegistryPath();
        var store = CreateStore(path);
        Exception? failure = null;
        var completed = false;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                store.EnsureLoadedAsync(CancellationToken.None).GetAwaiter().GetResult();
                completed = true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true
        };

        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Registry creation did not complete without pumping the captured synchronization context.");
        Assert.Null(failure);
        Assert.True(completed);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Registry_store_quarantines_corrupt_registry_and_resets()
    {
        var path = CreateRegistryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{");
        var sink = new DiagnosticSink();
        var store = CreateStore(path, diagnosticSink: sink);

        var registry = await store.EnsureLoadedAsync(CancellationToken.None);

        Assert.Empty(registry.Profiles);
        Assert.True(File.Exists(path));
        Assert.Contains(
            Directory.GetFiles(Path.GetDirectoryName(path)!, "application-profile-registry.corrupt-*.json"),
            File.Exists);
        Assert.Contains(sink.Current.Entries, entry => entry.Current.Code == "Profile.Registry.Corrupt");
    }

    [Fact]
    public async Task Refresh_all_registers_builtin_fake_profile()
    {
        var service = CreateRegistrationService(
            [FakeAccessProfileFactory.CreatePlugin()],
            out var store);

        var result = await service.RefreshAllProfileRegistrationsAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
        var registry = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(ApplicationProfileRegistrationRefreshStatus.Completed, result.Status);
        var entry = Assert.Single(registry.Profiles);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, entry.ProfileId);
        Assert.Equal(AccessProfileRegistrationState.Registered, entry.Registration.State);
        Assert.Equal(FakeAccessProfilePlugin.DefaultAccessApiKind, Assert.Single(entry.Capabilities.AccessApis).Kind);
        Assert.Equal(FakeAccessProfilePlugin.DefaultRuntimeKind, Assert.Single(entry.Capabilities.Runtimes).Kind);
        Assert.Equal(FakeAccessProfilePlugin.DefaultUnitTestContractVersion, Assert.Single(entry.Capabilities.UnitTestContractVersions));
    }

    [Fact]
    public async Task Refresh_all_returns_timed_out_and_preserves_previous_registry()
    {
        var registryPath = CreateRegistryPath();
        var seedService = CreateRegistrationService(
            [FakeAccessProfileFactory.CreatePlugin()],
            out var store,
            registryPath: registryPath);
        await seedService.RefreshAllProfileRegistrationsAsync(
            TimeSpan.FromMinutes(3),
            CancellationToken.None);
        Assert.Single((await store.LoadAsync(CancellationToken.None)).Profiles);

        var timeProvider = FakeTime.Create();
        var registrationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakeAccessProfilePlugin(
            (_, creationContext) => FakeAccessProfileFactory.CreateServices(creationContext.TimeProvider),
            async (_, cancellationToken) =>
            {
                registrationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, cancellationToken);
                throw new InvalidOperationException("The held registration should only complete through cancellation.");
            });
        var service = CreateRegistrationService(
            [plugin],
            out store,
            registryPath: registryPath,
            timeProvider: timeProvider);

        var refreshTask = service.RefreshAllProfileRegistrationsAsync(
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        await registrationStarted.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var result = await refreshTask;

        Assert.Equal(ApplicationProfileRegistrationRefreshStatus.TimedOut, result.Status);
        Assert.Single(result.Registry.Profiles);
        Assert.Single((await store.LoadAsync(CancellationToken.None)).Profiles);
    }

    [Fact]
    public async Task Refresh_all_persists_unavailable_profile_without_error_status()
    {
        var plugin = new FakeAccessProfilePlugin(
            (_, creationContext) => FakeAccessProfileFactory.CreateServices(creationContext.TimeProvider),
            (_, _) => Task.FromResult(FakeAccessProfilePlugin.CreateUnavailableRegistrationResult()));
        var service = CreateRegistrationService([plugin], out var store);

        var result = await service.RefreshAllProfileRegistrationsAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
        var entry = Assert.Single((await store.LoadAsync(CancellationToken.None)).Profiles);

        Assert.Equal(ApplicationProfileRegistrationRefreshStatus.Completed, result.Status);
        Assert.Equal(AccessProfileRegistrationState.Unavailable, entry.Registration.State);
        Assert.Equal(CapabilityAvailability.Unavailable, Assert.Single(entry.Capabilities.Runtimes).Availability);
    }

    [Fact]
    public async Task Refresh_all_keeps_going_when_profile_reports_error()
    {
        var errorPlugin = new FakeAccessProfilePlugin(
            new AccessProfileInfo("broken", "Broken Fake", "1.0.0", "Broken fake profile."),
            (_, creationContext) => FakeAccessProfileFactory.CreateServices(creationContext.TimeProvider),
            (_, _) => Task.FromResult(FakeAccessProfilePlugin.CreateErrorRegistrationResult()));
        var service = CreateRegistrationService(
            [FakeAccessProfileFactory.CreatePlugin(), errorPlugin],
            out var store);

        var result = await service.RefreshAllProfileRegistrationsAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
        var registry = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(ApplicationProfileRegistrationRefreshStatus.CompletedWithErrors, result.Status);
        Assert.Equal(2, registry.Profiles.Count);
        Assert.Contains(registry.Profiles, entry => entry.ProfileId == "broken"
            && entry.Registration.State == AccessProfileRegistrationState.Error);
        Assert.Contains(registry.Profiles, entry => entry.ProfileId == FakeAccessProfilePlugin.DefaultProfileId
            && entry.Registration.State == AccessProfileRegistrationState.Registered);
    }

    [Fact]
    public async Task Refresh_profile_not_found_removes_existing_registry_entry()
    {
        var key = new AccessProfileCandidateKey(
            FakeAccessProfilePlugin.DefaultProfileId,
            FakeAccessProfilePlugin.DefaultProfileVersion,
            AccessProfileSourceKind.BuiltIn.ToString(),
            $"built-in:{FakeAccessProfilePlugin.DefaultProfileId}@{FakeAccessProfilePlugin.DefaultProfileVersion}");
        var registryPath = CreateRegistryPath();
        var service = CreateRegistrationService(
            [FakeAccessProfileFactory.CreatePlugin()],
            out var store,
            registryPath: registryPath);
        await service.RefreshAllProfileRegistrationsAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
        Assert.Single((await store.LoadAsync(CancellationToken.None)).Profiles);

        var removeService = CreateRegistrationService(
            [],
            out _,
            registryPath: registryPath,
            includeBuiltInProfiles: true);
        var result = await removeService.RefreshProfileRegistrationAsync(key, TimeSpan.FromMinutes(3), CancellationToken.None);

        Assert.Equal(ApplicationProfileRegistrationRefreshStatus.CompletedWithErrors, result.Status);
        Assert.Null(result.Entry);
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Profiles);
    }

    [Fact]
    public async Task Check_reports_current_without_registering_profile_again()
    {
        var registrationCount = 0;
        var plugin = new FakeAccessProfilePlugin(
            (_, creationContext) => FakeAccessProfileFactory.CreateServices(creationContext.TimeProvider),
            (_, _) =>
            {
                registrationCount++;
                return Task.FromResult(FakeAccessProfilePlugin.CreateDefaultRegistrationResult());
            });
        var service = CreateRegistrationService([plugin], out _);
        await service.RefreshAllProfileRegistrationsAsync(TimeSpan.FromMinutes(3), CancellationToken.None);

        var result = await service.CheckProfileRegistrationsAsync(CancellationToken.None);

        Assert.Equal(ApplicationProfileRegistrationCheckStatus.Current, result.Status);
        Assert.True(result.IsCurrent);
        Assert.Equal(1, registrationCount);
    }

    [Fact]
    public async Task Check_reports_refresh_required_for_missing_registration()
    {
        var service = CreateRegistrationService(
            [FakeAccessProfileFactory.CreatePlugin()],
            out _);

        var result = await service.CheckProfileRegistrationsAsync(CancellationToken.None);

        Assert.Equal(ApplicationProfileRegistrationCheckStatus.RefreshRequired, result.Status);
        Assert.False(result.IsCurrent);
    }

    [Fact]
    public async Task Check_reports_refresh_required_for_stale_fingerprint()
    {
        var service = CreateRegistrationService(
            [FakeAccessProfileFactory.CreatePlugin()],
            out var store);
        await service.RefreshAllProfileRegistrationsAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
        var registry = await store.LoadAsync(CancellationToken.None);
        var entry = Assert.Single(registry.Profiles);
        await store.SaveAsync(
            registry with
            {
                Profiles =
                [
                    entry with
                    {
                        Source = entry.Source with { Fingerprint = "sha256:stale" }
                    }
                ]
            },
            CancellationToken.None);

        var result = await service.CheckProfileRegistrationsAsync(CancellationToken.None);

        Assert.Equal(ApplicationProfileRegistrationCheckStatus.RefreshRequired, result.Status);
    }

    [Fact]
    public async Task Check_reports_refresh_required_when_binding_manifest_is_added()
    {
        var profileRoot = CreateDirectoryProfilePackage();
        var service = CreateRegistrationService(
            [],
            out var store,
            includeBuiltInProfiles: false,
            profileDiscoveryDirectory: Path.GetDirectoryName(profileRoot)!);
        await MarkDirectoryRegistrationCurrentAsync(service, store);

        var bindingDirectory = Path.Combine(profileRoot, "lib", "bindings", "binding-a");
        Directory.CreateDirectory(bindingDirectory);
        await File.WriteAllTextAsync(Path.Combine(bindingDirectory, "manifest.json"), "{\"binding\":\"a\"}");

        var result = await service.CheckProfileRegistrationsAsync(CancellationToken.None);

        Assert.Equal(ApplicationProfileRegistrationCheckStatus.RefreshRequired, result.Status);
    }

    [Fact]
    public async Task Check_reports_refresh_required_when_binding_manifest_is_changed()
    {
        var profileRoot = CreateDirectoryProfilePackage();
        var bindingManifest = CreateBindingManifest(profileRoot, "{\"binding\":\"a\",\"version\":1}");
        var service = CreateRegistrationService(
            [],
            out var store,
            includeBuiltInProfiles: false,
            profileDiscoveryDirectory: Path.GetDirectoryName(profileRoot)!);
        await MarkDirectoryRegistrationCurrentAsync(service, store);

        await File.WriteAllTextAsync(bindingManifest, "{\"binding\":\"a\",\"version\":2}");

        var result = await service.CheckProfileRegistrationsAsync(CancellationToken.None);

        Assert.Equal(ApplicationProfileRegistrationCheckStatus.RefreshRequired, result.Status);
    }

    [Fact]
    public async Task Check_reports_refresh_required_when_binding_manifest_is_removed()
    {
        var profileRoot = CreateDirectoryProfilePackage();
        var bindingManifest = CreateBindingManifest(profileRoot, "{\"binding\":\"a\"}");
        var service = CreateRegistrationService(
            [],
            out var store,
            includeBuiltInProfiles: false,
            profileDiscoveryDirectory: Path.GetDirectoryName(profileRoot)!);
        await MarkDirectoryRegistrationCurrentAsync(service, store);

        File.Delete(bindingManifest);

        var result = await service.CheckProfileRegistrationsAsync(CancellationToken.None);

        Assert.Equal(ApplicationProfileRegistrationCheckStatus.RefreshRequired, result.Status);
    }

    [Theory]
    [InlineData(AccessProfileRegistrationState.Unavailable)]
    [InlineData(AccessProfileRegistrationState.Error)]
    public async Task Check_reports_refresh_required_for_unusable_registration(
        AccessProfileRegistrationState registrationState)
    {
        var plugin = new FakeAccessProfilePlugin(
            (_, creationContext) => FakeAccessProfileFactory.CreateServices(creationContext.TimeProvider),
            (_, _) => Task.FromResult(registrationState == AccessProfileRegistrationState.Unavailable
                ? FakeAccessProfilePlugin.CreateUnavailableRegistrationResult()
                : FakeAccessProfilePlugin.CreateErrorRegistrationResult()));
        var service = CreateRegistrationService([plugin], out _);
        await service.RefreshAllProfileRegistrationsAsync(TimeSpan.FromMinutes(3), CancellationToken.None);

        var result = await service.CheckProfileRegistrationsAsync(CancellationToken.None);

        Assert.Equal(ApplicationProfileRegistrationCheckStatus.RefreshRequired, result.Status);
    }

    [Fact]
    public async Task Check_reports_refresh_required_for_orphaned_registry_entry()
    {
        var registryPath = CreateRegistryPath();
        var seedService = CreateRegistrationService(
            [FakeAccessProfileFactory.CreatePlugin()],
            out _,
            registryPath: registryPath);
        await seedService.RefreshAllProfileRegistrationsAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
        var service = CreateRegistrationService(
            [],
            out _,
            registryPath: registryPath);

        var result = await service.CheckProfileRegistrationsAsync(CancellationToken.None);

        Assert.Equal(ApplicationProfileRegistrationCheckStatus.RefreshRequired, result.Status);
    }

    [Fact]
    public async Task Check_reports_no_profiles_when_discovery_and_registry_are_empty()
    {
        var service = CreateRegistrationService([], out _);

        var result = await service.CheckProfileRegistrationsAsync(CancellationToken.None);

        Assert.Equal(ApplicationProfileRegistrationCheckStatus.NoProfilesDiscovered, result.Status);
    }

    [Fact]
    public async Task Check_reports_failed_for_invalid_discovered_package()
    {
        var discoveryDirectory = Path.Combine(
            Path.GetTempPath(),
            "plcsim-unittest-profile-discovery-tests",
            Guid.NewGuid().ToString("N"));
        var packageDirectory = Path.Combine(discoveryDirectory, "broken");
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "manifest.json"), "{");
        var service = CreateRegistrationService(
            [],
            out _,
            includeBuiltInProfiles: false,
            profileDiscoveryDirectory: discoveryDirectory);

        var result = await service.CheckProfileRegistrationsAsync(CancellationToken.None);

        Assert.Equal(ApplicationProfileRegistrationCheckStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "Profile.Discovery.Manifest.InvalidJson");
    }

    private static ApplicationProfileRegistrationService CreateRegistrationService(
        IReadOnlyList<IAccessProfilePlugin> plugins,
        out IApplicationProfileRegistryStore store,
        string? registryPath = null,
        bool includeBuiltInProfiles = true,
        DiagnosticSink? diagnosticSink = null,
        TimeProvider? timeProvider = null,
        string profileDiscoveryDirectory = "")
    {
        var coreContext = TestCoreServiceContext.Create(
            timeProvider: timeProvider ?? FakeTime.Create(),
            diagnosticSink: diagnosticSink);
        registryPath ??= CreateRegistryPath();
        store = new FileApplicationProfileRegistryStore(
            coreContext,
            new ApplicationProfileRegistryOptions(registryPath),
            new AccessProfileCapabilityValidator());
        return new ApplicationProfileRegistrationService(
            new AccessProfilePluginLoader(coreContext, plugins),
            new AccessProfileDiscoveryOptions(profileDiscoveryDirectory, includeBuiltInProfiles),
            store,
            new AccessProfileCapabilityValidator(),
            coreContext);
    }

    private static FileApplicationProfileRegistryStore CreateStore(
        string path,
        DiagnosticSink? diagnosticSink = null)
    {
        return new FileApplicationProfileRegistryStore(
            TestCoreServiceContext.Create(timeProvider: FakeTime.Create(), diagnosticSink: diagnosticSink),
            new ApplicationProfileRegistryOptions(path),
            new AccessProfileCapabilityValidator());
    }

    private static async Task MarkDirectoryRegistrationCurrentAsync(
        ApplicationProfileRegistrationService service,
        IApplicationProfileRegistryStore store)
    {
        await service.RefreshAllProfileRegistrationsAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
        var registry = await store.LoadAsync(CancellationToken.None);
        var entry = Assert.Single(registry.Profiles);
        await store.SaveAsync(
            registry with
            {
                Profiles =
                [
                    entry with
                    {
                        Registration = entry.Registration with
                        {
                            State = AccessProfileRegistrationState.Registered
                        }
                    }
                ]
            },
            CancellationToken.None);

        var current = await service.CheckProfileRegistrationsAsync(CancellationToken.None);
        Assert.Equal(ApplicationProfileRegistrationCheckStatus.Current, current.Status);
    }

    private static string CreateDirectoryProfilePackage()
    {
        var discoveryRoot = Path.Combine(
            Path.GetTempPath(),
            "plcsim-unittest-profile-registry-tests",
            Guid.NewGuid().ToString("N"));
        var profileRoot = Path.Combine(discoveryRoot, "directory-profile");
        Directory.CreateDirectory(profileRoot);

        var assemblyFileName = "Directory.Profile.dll";
        File.Copy(typeof(ApplicationProfileRegistryTests).Assembly.Location, Path.Combine(profileRoot, assemblyFileName));
        var fakeAssemblyPath = typeof(FakeAccessProfilePlugin).Assembly.Location;
        File.Copy(fakeAssemblyPath, Path.Combine(profileRoot, Path.GetFileName(fakeAssemblyPath)));
        File.WriteAllText(
            Path.Combine(profileRoot, "manifest.json"),
            $$"""
                {
                  "schemaVersion": 1,
                  "profile": {
                    "id": "directorytest",
                    "version": "1.0.0",
                    "displayName": "Directory Access Profile",
                    "contractVersion": "2.0.0"
                  },
                  "entryPoint": {
                    "assembly": "{{assemblyFileName}}",
                    "type": "{{typeof(DirectoryLoadTestAccessProfilePlugin).FullName}}"
                  }
                }
                """);
        return profileRoot;
    }

    private static string CreateBindingManifest(string profileRoot, string content)
    {
        var bindingDirectory = Path.Combine(profileRoot, "lib", "bindings", "binding-a");
        Directory.CreateDirectory(bindingDirectory);
        var manifestPath = Path.Combine(bindingDirectory, "manifest.json");
        File.WriteAllText(manifestPath, content);
        return manifestPath;
    }

    private static string CreateRegistryPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "plcsim-unittest-profile-registry-tests",
            Guid.NewGuid().ToString("N"),
            ApplicationProfileRegistryOptions.RegistryFileName);
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }

}
