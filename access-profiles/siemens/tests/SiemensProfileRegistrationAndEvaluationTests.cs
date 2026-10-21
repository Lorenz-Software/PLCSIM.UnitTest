using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.Fake;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.Fake;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens;

public sealed partial class SiemensAccessProfilePluginTests
{
    [Fact]
    public void Manifest_matches_siemens_profile_metadata_and_entry_point()
    {
        var manifestPath = GetSiemensProfileManifestPath();

        Assert.True(File.Exists(manifestPath), $"Expected Siemens profile manifest at '{manifestPath}'.");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        var profile = root.GetProperty("profile");
        var entryPoint = root.GetProperty("entryPoint");

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(SiemensAccessProfilePlugin.DefaultProfileId, profile.GetProperty("id").GetString());
        Assert.Equal(SiemensAccessProfilePlugin.DefaultProfileVersion, profile.GetProperty("version").GetString());
        Assert.Equal(SiemensAccessProfilePlugin.DefaultDisplayName, profile.GetProperty("displayName").GetString());
        Assert.Equal(SiemensAccessProfilePlugin.DefaultDescription, profile.GetProperty("description").GetString());
        Assert.Equal("2.0.0", profile.GetProperty("contractVersion").GetString());
        Assert.Equal("PLCSIM.UnitTest.AccessProfiles.Siemens.dll", entryPoint.GetProperty("assembly").GetString());
        Assert.Equal(typeof(SiemensAccessProfilePlugin).FullName, entryPoint.GetProperty("type").GetString());
    }

    [Fact]
    public void Generic_loader_discovers_siemens_builtin_profile_shell()
    {
        var plugin = new SiemensAccessProfilePlugin();
        var loader = new AccessProfilePluginLoader(TestCoreServiceContext.Create(), [plugin]);

        var discovery = loader.Discover(new AccessProfileDiscoveryOptions(string.Empty, IncludeBuiltInProfiles: true));
        var candidate = Assert.Single(discovery.Candidates);

        Assert.Empty(discovery.Diagnostics);
        Assert.Equal(AccessProfileCandidateState.Discovered, candidate.State);
        Assert.Equal(AccessProfileSourceKind.BuiltIn, candidate.SourceKind);
        Assert.Equal(SiemensAccessProfilePlugin.DefaultProfileId, candidate.Info.ProfileId);
        Assert.Equal(SiemensAccessProfilePlugin.DefaultProfileVersion, candidate.Info.Version);
    }

    [Fact]
    public async Task Registration_overlap_rejects_load_evaluation_and_disposal()
    {
        var profileRoot = CreateTempDirectory();
        var innerStore = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var store = new BlockingSiemensProfileRegistryStore(innerStore);
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateCompleteBindingPair(profileRoot);
            var registrationTask = plugin.RegisterAsync(CancellationToken.None);
            await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var rejectedLoad = await LoadApplicationManagedAsync(
                plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);
            var rejectedEvaluation = plugin.EvaluateProjectSource(@"D:\Projects\Sample.ap21");
            var disposeFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await plugin.DisposeAsync());

            Assert.False(rejectedLoad.IsSuccessful);
            Assert.Contains(
                rejectedLoad.Diagnostics,
                diagnostic => diagnostic.Code == "SiemensProfile.Load.OperationRejected");
            Assert.Equal(AccessProfileProjectSourceEvaluationState.Error, rejectedEvaluation.State);
            Assert.Contains("Registration", rejectedEvaluation.Message, StringComparison.Ordinal);
            Assert.Contains("Registration", disposeFailure.Message, StringComparison.Ordinal);

            store.ReleaseSave();
            var registration = await registrationTask;
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);
            await plugin.DisposeAsync();
        }
        finally
        {
            store.ReleaseSave();
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task Profile_registry_store_uses_profile_data_directory_and_ignores_legacy_cache()
    {
        var profileRoot = CreateTempDirectory();
        var profileDataDirectory = CreateTempDirectory();
        var legacyRegistryPath = Path.Combine(profileRoot, "cache", "profile-registry.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyRegistryPath)!);
        File.WriteAllText(legacyRegistryPath, "{");
        var store = new FileSiemensProfileRegistryStore();
        var context = TestCoreServiceContext.Create(FakeTime.Create()).CreateAccessProfileContext(
            profileRoot,
            profileDataDirectory: profileDataDirectory);
        var registry = CreateRegistry(profileRoot);

        try
        {
            await store.SaveAsync(context, registry, CancellationToken.None);
            var loaded = await store.LoadAsync(context, CancellationToken.None);

            Assert.Equal(
                Path.Combine(profileDataDirectory, "profile-registry.json"),
                store.GetRegistryPath(context));
            Assert.True(File.Exists(Path.Combine(profileDataDirectory, "profile-registry.json")));
            Assert.Equal("{", File.ReadAllText(legacyRegistryPath));
            Assert.Equal(SiemensProfileRegistry.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(SiemensAccessProfilePlugin.DefaultProfileId, loaded.ProfileId);
            Assert.Equal(SiemensAccessProfilePlugin.DefaultProfileVersion, loaded.ProfileVersion);
            Assert.Equal(AccessProfileRegistrationState.Registered, loaded.State);
            Assert.Single(loaded.ProjectSources);
            Assert.Single(loaded.Runtimes);
            Assert.Equal(UnitTestContractVersions.EventStreamModel, Assert.Single(loaded.UnitTestContractVersions));
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
            DeleteTemporaryDirectory(profileDataDirectory);
        }
    }

    [Fact]
    public void Profile_registry_store_rejects_unsupported_schema_version()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
        var registryPath = Path.Combine(profileRoot, "profile-registry.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
            File.WriteAllText(
                registryPath,
                """
                {
                  "schemaVersion": "9.9",
                  "profileId": "siemens",
                  "profileVersion": "1.0.0",
                  "profileRootPath": "test",
                  "registeredAt": "2026-01-01T00:00:00+00:00",
                  "state": "Registered",
                  "accessApis": [],
                  "projectSources": [],
                  "runtimes": [],
                  "unitTestContractVersions": [],
                  "bindingCandidates": []
                }
                """);

            var ex = Assert.Throws<InvalidDataException>(() => store.Load(context));
            Assert.Contains("Unsupported Siemens profile registry schema version", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task RegisterAsync_writes_unavailable_registry_when_no_binding_packages_exist()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var timeProvider = FakeTime.Create();
        var plugin = CreatePluginWithStore(store);
        var context = TestCoreServiceContext.Create(timeProvider).CreateAccessProfileContext(profileRoot);
        plugin.Initialize(context);

        try
        {
            var result = await plugin.RegisterAsync(CancellationToken.None);
            var registry = store.Load(context);

            Assert.Equal(AccessProfileRegistrationState.Unavailable, result.State);
            Assert.Equal(AccessProfileRegistrationState.Unavailable, registry.State);
            Assert.Equal(SiemensAccessProfilePlugin.DefaultProfileId, registry.ProfileId);
            Assert.Equal(SiemensAccessProfilePlugin.DefaultProfileVersion, registry.ProfileVersion);
            Assert.Equal(profileRoot, registry.ProfileRootPath);
            Assert.Equal(timeProvider.GetUtcNow(), registry.RegisteredAt);
            Assert.Empty(registry.BindingCandidates);
            Assert.Empty(registry.AccessApis);
            Assert.Empty(registry.ProjectSources);
            Assert.Empty(registry.Runtimes);
            Assert.Empty(registry.UnitTestContractVersions);
            Assert.Equal(result.Message, registry.Message);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task RegisterAsync_maps_complete_binding_pair_to_registered_capabilities()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        var context = TestCoreServiceContext.Create(FakeTime.Create()).CreateAccessProfileContext(profileRoot);
        plugin.Initialize(context);

        try
        {
            CreateBindingPackage(
                profileRoot,
                "tia-openness-v21",
                SiemensBindingKinds.TiaOpenness,
                "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.dll");
            CreateBindingPackage(
                profileRoot,
                "plcsim-advanced-v8",
                SiemensBindingKinds.PlcSimAdvanced,
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.dll");

            var result = await plugin.RegisterAsync(CancellationToken.None);
            var registry = store.Load(context);

            Assert.Equal(AccessProfileRegistrationState.Registered, result.State);
            Assert.Equal(AccessProfileRegistrationState.Registered, registry.State);
            Assert.Equal(2, result.Capabilities.AccessApis.Count);
            Assert.Contains(result.Capabilities.AccessApis, capability => capability.Kind == SiemensBindingKinds.TiaOpenness);
            Assert.Contains(result.Capabilities.AccessApis, capability => capability.Kind == SiemensBindingKinds.PlcSimAdvanced);
            Assert.Single(result.Capabilities.ProjectSources, capability => capability.Extension == ".ap21");
            Assert.Single(result.Capabilities.Runtimes, capability => capability.Kind == SiemensBindingKinds.PlcSimAdvanced);
            Assert.Equal(UnitTestContractVersions.EventStreamModel, Assert.Single(result.Capabilities.UnitTestContractVersions));
            Assert.Equal(2, registry.BindingCandidates.Count);
            Assert.Contains(registry.BindingCandidates, binding => binding.BindingKind == SiemensBindingKinds.TiaOpenness);
            Assert.Contains(registry.BindingCandidates, binding => binding.BindingKind == SiemensBindingKinds.PlcSimAdvanced);
            Assert.Equal(result.Capabilities.AccessApis, registry.AccessApis);
            Assert.Equal(result.Capabilities.ProjectSources, registry.ProjectSources);
            Assert.Equal(result.Capabilities.Runtimes, registry.Runtimes);
            Assert.Equal(result.Capabilities.UnitTestContractVersions, registry.UnitTestContractVersions);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task RegisterAsync_registers_external_project_path_when_tia_binding_readiness_is_unavailable()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        var context = TestCoreServiceContext.Create(FakeTime.Create()).CreateAccessProfileContext(profileRoot);
        plugin.Initialize(context);
        try
        {
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-openness-v21",
                SiemensBindingKinds.TiaOpenness,
                typeof(UnavailableTestTiaBinding));
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-advanced-v8",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(TestPlcsimBinding));

            var result = await plugin.RegisterAsync(CancellationToken.None);
            var registry = store.Load(context);

            Assert.Equal(AccessProfileRegistrationState.Registered, result.State);
            Assert.Equal(AccessProfileRegistrationState.Registered, registry.State);
            Assert.Contains("externally managed", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(registry.BindingCandidates, binding => binding.BindingKind == SiemensBindingKinds.PlcSimAdvanced);
            Assert.Contains(
                result.Capabilities.AccessApis,
                capability => capability.Kind == SiemensBindingKinds.TiaOpenness
                    && capability.Availability == CapabilityAvailability.Unavailable);
            Assert.Contains(
                result.Capabilities.ProjectSources,
                capability => capability.Extension == ".ap21"
                    && capability.Availability == CapabilityAvailability.Unavailable);
            Assert.Contains(
                result.Capabilities.Runtimes,
                capability => capability.Kind == SiemensBindingKinds.PlcSimAdvanced
                    && capability.Availability == CapabilityAvailability.Usable);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task RegisterAsync_writes_unavailable_registry_for_partial_binding_chain()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
        plugin.Initialize(context);

        try
        {
            CreateBindingPackage(
                profileRoot,
                "tia-openness-v21",
                SiemensBindingKinds.TiaOpenness,
                "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.dll");

            var result = await plugin.RegisterAsync(CancellationToken.None);
            var registry = store.Load(context);

            Assert.Equal(AccessProfileRegistrationState.Unavailable, result.State);
            Assert.Equal(AccessProfileRegistrationState.Unavailable, registry.State);
            Assert.Single(registry.AccessApis);
            Assert.Single(registry.ProjectSources);
            Assert.Single(registry.Runtimes);
            Assert.Single(registry.BindingCandidates, binding => binding.BindingKind == SiemensBindingKinds.TiaOpenness);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task RegisterAsync_writes_error_registry_for_invalid_binding_package()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
        plugin.Initialize(context);

        try
        {
            CreateBindingPackage(
                profileRoot,
                "missing-entry",
                SiemensBindingKinds.PlcSimAdvanced,
                "Missing.Binding.dll",
                createAssembly: false);

            var result = await plugin.RegisterAsync(CancellationToken.None);
            var registry = store.Load(context);

            Assert.Equal(AccessProfileRegistrationState.Error, result.State);
            Assert.Equal(AccessProfileRegistrationState.Error, registry.State);
            Assert.Empty(registry.BindingCandidates);
            Assert.Empty(registry.AccessApis);
            Assert.Contains("errors", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task RegisterAsync_persists_multiple_usable_binding_candidates()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
        plugin.Initialize(context);

        try
        {
            CreateBindingPackage(
                profileRoot,
                "tia-openness-v21-a",
                SiemensBindingKinds.TiaOpenness,
                "TiaA.dll");
            CreateBindingPackage(
                profileRoot,
                "tia-openness-v21-b",
                SiemensBindingKinds.TiaOpenness,
                "TiaB.dll");
            CreateBindingPackage(
                profileRoot,
                "plcsim-advanced-v8",
                SiemensBindingKinds.PlcSimAdvanced,
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.dll");

            var result = await plugin.RegisterAsync(CancellationToken.None);
            var registry = store.Load(context);

            Assert.Equal(AccessProfileRegistrationState.Registered, result.State);
            Assert.Equal(AccessProfileRegistrationState.Registered, registry.State);
            Assert.Equal(3, registry.BindingCandidates.Count);
            Assert.Equal(2, registry.BindingCandidates.Count(binding => binding.BindingKind == SiemensBindingKinds.TiaOpenness));
            Assert.Single(registry.BindingCandidates, binding => binding.BindingKind == SiemensBindingKinds.PlcSimAdvanced);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task RegisterAsync_writes_error_registry_when_binding_readiness_fails()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var loggerFactory = new SiemensTestLoggerFactory();
        var plugin = CreatePluginWithStore(store);
        var context = TestCoreServiceContext.Create(loggerFactory: loggerFactory).CreateAccessProfileContext(profileRoot);
        plugin.Initialize(context);
        try
        {
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-openness-v21",
                SiemensBindingKinds.TiaOpenness,
                typeof(ErrorTestTiaBinding));
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-advanced-v8",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(TestPlcsimBinding));

            var result = await plugin.RegisterAsync(CancellationToken.None);
            var registry = store.Load(context);

            Assert.Equal(AccessProfileRegistrationState.Error, result.State);
            Assert.Equal(AccessProfileRegistrationState.Error, registry.State);
            Assert.Empty(registry.BindingCandidates);
            Assert.Contains(
                result.Capabilities.AccessApis,
                capability => capability.Kind == SiemensBindingKinds.TiaOpenness
                    && capability.Availability == CapabilityAvailability.Error);
            Assert.Contains(
                loggerFactory.Entries,
                entry => entry.Level == LogLevel.Error
                    && entry.Message.Contains("TiaOpenness.Environment.TestError", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task EvaluateProjectSource_returns_supported_from_registered_profile_registry()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateCompleteBindingPair(profileRoot);
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);

            var result = plugin.EvaluateProjectSource(@"D:\Projects\Sample.AP21");

            Assert.Equal(AccessProfileProjectSourceEvaluationState.Supported, result.State);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task EvaluateProjectSource_requires_matching_runtime_candidate()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-openness-fake-project",
                SiemensBindingKinds.TiaOpenness,
                typeof(TestTiaBinding),
                runtimeVersion: "fake",
                projectSourceKind: SiemensProjectSourceKinds.SiemensFakeProject,
                projectSourceExtension: ".apf",
                projectSourceVersion: "fake");
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-advanced-real-only",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(TestPlcsimBinding),
                runtimeVersion: "8.0",
                projectSourceKind: SiemensProjectSourceKinds.TiaProject,
                projectSourceExtension: ".ap21",
                projectSourceVersion: "21");
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);

            var result = plugin.EvaluateProjectSource(@"D:\Projects\siemens-fake-project.apf");

            Assert.Equal(AccessProfileProjectSourceEvaluationState.Unsupported, result.State);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task EvaluateProjectSource_returns_unsupported_for_non_matching_extension()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateCompleteBindingPair(profileRoot);
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);

            var result = plugin.EvaluateProjectSource(@"D:\Projects\Sample.json");

            Assert.Equal(AccessProfileProjectSourceEvaluationState.Unsupported, result.State);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task EvaluateProjectSource_returns_supported_for_binding_published_zap21_archive()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateBindingPackage(
                profileRoot,
                "tia-openness-v21",
                SiemensBindingKinds.TiaOpenness,
                "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.dll",
                projectSourceExtension: ".zap21");
            CreateBindingPackage(
                profileRoot,
                "plcsim-advanced-v8",
                SiemensBindingKinds.PlcSimAdvanced,
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.dll");

            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);
            Assert.Contains(
                registration.Capabilities.ProjectSources,
                source => source.Kind == SiemensProjectSourceKinds.TiaProject
                    && source.Extension == ".zap21"
                    && source.Version == "21");

            var result = plugin.EvaluateProjectSource(@"D:\Projects\Sample.zap21");

            Assert.Equal(AccessProfileProjectSourceEvaluationState.Supported, result.State);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task EvaluateProjectSource_returns_unsupported_when_profile_registry_is_unavailable()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Unavailable, registration.State);

            var result = plugin.EvaluateProjectSource(@"D:\Projects\Sample.ap21");

            Assert.Equal(AccessProfileProjectSourceEvaluationState.Unsupported, result.State);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public void EvaluateProjectSource_returns_error_when_profile_registry_is_missing()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            var result = plugin.EvaluateProjectSource(@"D:\Projects\Sample.ap21");

            Assert.Equal(AccessProfileProjectSourceEvaluationState.Error, result.State);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public void EvaluateProjectSource_returns_error_when_profile_registry_is_corrupt()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
        var registryPath = Path.Combine(profileRoot, "profile-registry.json");
        plugin.Initialize(context);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
            File.WriteAllText(registryPath, "{");

            var result = plugin.EvaluateProjectSource(@"D:\Projects\Sample.ap21");

            Assert.Equal(AccessProfileProjectSourceEvaluationState.Error, result.State);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task EvaluateProjectSource_returns_error_when_profile_registry_identity_mismatches()
    {
        var profileRoot = CreateTempDirectory();
        var otherProfileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
        plugin.Initialize(context);

        try
        {
            var registry = CreateRegistry(otherProfileRoot);
            await store.SaveAsync(context, registry, CancellationToken.None);

            var result = plugin.EvaluateProjectSource(@"D:\Projects\Sample.ap21");

            Assert.Equal(AccessProfileProjectSourceEvaluationState.Error, result.State);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
            DeleteTemporaryDirectory(otherProfileRoot);
        }
    }

    [Fact]
    public async Task Application_profile_registration_and_selection_filter_mixed_siemens_project_sources()
    {
        var profilesRoot = CreateTempDirectory();
        var registryPath = Path.Combine(profilesRoot, ApplicationProfileRegistryOptions.RegistryFileName);
        var coreContext = TestCoreServiceContext.Create(FakeTime.Create());
        var pluginLoader = new AccessProfilePluginLoader(coreContext, []);
        var discoveryOptions = new AccessProfileDiscoveryOptions(profilesRoot, IncludeBuiltInProfiles: false);
        var applicationRegistryStore = new FileApplicationProfileRegistryStore(
            coreContext,
            new ApplicationProfileRegistryOptions(registryPath),
            new AccessProfileCapabilityValidator());
        var registrationService = new ApplicationProfileRegistrationService(
            pluginLoader,
            discoveryOptions,
            applicationRegistryStore,
            new AccessProfileCapabilityValidator(),
            coreContext);
        var selectionService = new ApplicationProfileSelectionService(
            pluginLoader,
            discoveryOptions,
            applicationRegistryStore,
            coreContext);

        try
        {
            var profileRoot = CreateSiemensProfileDirectoryPackage(profilesRoot);
            CreateMixedRealAndFakeBindingCandidates(profileRoot);
            var fakeProjectPath = CreateSiemensFakeProject(profileRoot);

            var registration = await registrationService.RefreshAllProfileRegistrationsAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
            var registryEntry = Assert.Single(registration.Registry.Profiles);
            var fakeSelection = await selectionService.EvaluateProjectSourceAsync(fakeProjectPath, CancellationToken.None);
            var tiaSelection = await selectionService.EvaluateProjectSourceAsync(@"D:\Projects\Sample.ap21", CancellationToken.None);

            Assert.Equal(ApplicationProfileRegistrationRefreshStatus.Completed, registration.Status);
            Assert.Equal(AccessProfileRegistrationState.Registered, registryEntry.Registration.State);
            Assert.Contains(registryEntry.Capabilities.ProjectSources, source => source.Extension == ".apf");
            Assert.Contains(registryEntry.Capabilities.ProjectSources, source => source.Extension == ".ap21");
            Assert.Equal(
                AccessProfileProjectSourceEvaluationState.Supported,
                Assert.Single(fakeSelection.Profiles).Evaluation.State);
            Assert.Equal(
                AccessProfileProjectSourceEvaluationState.Supported,
                Assert.Single(tiaSelection.Profiles).Evaluation.State);
        }
        finally
        {
            DeleteTemporaryDirectory(profilesRoot);
        }
    }

    [Fact]
    public async Task RegisterAsync_returns_diagnostic_when_binding_entry_type_violates_technology_contract()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var loggerFactory = new SiemensTestLoggerFactory();
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create(loggerFactory: loggerFactory).CreateAccessProfileContext(profileRoot));

        try
        {
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-invalid",
                SiemensBindingKinds.TiaOpenness,
                typeof(InvalidSiemensBinding));
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-loadable",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(TestPlcsimBinding));

            var result = await plugin.RegisterAsync(CancellationToken.None);

            Assert.Equal(AccessProfileRegistrationState.Error, result.State);
            Assert.Contains(
                loggerFactory.Entries,
                entry => entry.Level == LogLevel.Error
                    && entry.Message.Contains("SiemensProfile.BindingReadiness.EntryTypeInvalid", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task RegisterAsync_logs_discovery_diagnostic_codes_for_real_registration_errors()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var loggerFactory = new SiemensTestLoggerFactory();
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create(loggerFactory: loggerFactory).CreateAccessProfileContext(profileRoot));

        try
        {
            CreateBindingPackage(
                profileRoot,
                "missing-entry",
                SiemensBindingKinds.PlcSimAdvanced,
                "Missing.Binding.dll",
                createAssembly: false);

            var result = await plugin.RegisterAsync(CancellationToken.None);

            Assert.Equal(AccessProfileRegistrationState.Error, result.State);
            Assert.Contains(
                loggerFactory.Entries,
                entry => entry.Level == LogLevel.Error
                    && entry.Message.Contains("SiemensProfile.BindingDiscovery.EntryAssemblyMissing", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public void Binding_discovery_returns_empty_result_when_bindings_directory_is_missing()
    {
        var profileRoot = CreateTempDirectory();
        var discovery = new SiemensBindingPackageDiscovery();

        try
        {
            var result = discovery.Discover(profileRoot);

            Assert.Empty(result.Candidates);
            Assert.Empty(result.Diagnostics);
            Assert.Equal(Path.Combine(profileRoot, "lib", "bindings"), result.BindingsRootPath);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public void Binding_discovery_discovers_valid_binding_descriptors()
    {
        var profileRoot = CreateTempDirectory();
        var discovery = new SiemensBindingPackageDiscovery();

        try
        {
            var tiaPackage = CreateBindingPackage(
                profileRoot,
                "tia-openness-v21",
                SiemensBindingKinds.TiaOpenness,
                "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.dll");
            var plcsimPackage = CreateBindingPackage(
                profileRoot,
                "plcsim-advanced-v8",
                SiemensBindingKinds.PlcSimAdvanced,
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.dll");

            var result = discovery.Discover(profileRoot);

            Assert.Empty(result.Diagnostics);
            Assert.Equal(
                new[] { SiemensBindingKinds.PlcSimAdvanced, SiemensBindingKinds.TiaOpenness },
                result.Candidates.Select(candidate => candidate.Descriptor.Info.BindingKind).ToArray());
            Assert.All(result.Candidates, candidate =>
            {
                Assert.Equal(SiemensBindingPackageCandidateState.Discovered, candidate.State);
                Assert.True(File.Exists(candidate.EntryAssemblyPath));
                Assert.False(string.IsNullOrWhiteSpace(candidate.DescriptorFingerprint));
                Assert.False(string.IsNullOrWhiteSpace(candidate.PackageFingerprint));
                Assert.Empty(candidate.Diagnostics);
            });
            Assert.Contains(result.Candidates, candidate => candidate.PackageRootPath == plcsimPackage);
            Assert.Contains(result.Candidates, candidate => candidate.PackageRootPath == tiaPackage);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public void Binding_discovery_reports_invalid_descriptor_json_without_candidate()
    {
        var profileRoot = CreateTempDirectory();
        var packageRoot = Path.Combine(profileRoot, "lib", "bindings", "broken");
        Directory.CreateDirectory(packageRoot);
        File.WriteAllText(Path.Combine(packageRoot, SiemensBindingPackageDescriptorContract.FileName), "{");
        var discovery = new SiemensBindingPackageDiscovery();

        try
        {
            var result = discovery.Discover(profileRoot);

            Assert.Empty(result.Candidates);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.BindingDiscovery.DescriptorInvalidJson");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public void Binding_discovery_rejects_entry_assembly_outside_package()
    {
        var profileRoot = CreateTempDirectory();
        var packageRoot = CreateBindingPackage(
            profileRoot,
            "escaping",
            SiemensBindingKinds.TiaOpenness,
            "..\\outside.dll",
            createAssembly: false);
        var discovery = new SiemensBindingPackageDiscovery();

        try
        {
            var result = discovery.Discover(profileRoot);

            var candidate = Assert.Single(result.Candidates);
            Assert.Equal(packageRoot, candidate.PackageRootPath);
            Assert.Equal(SiemensBindingPackageCandidateState.MetadataInvalid, candidate.State);
            Assert.Null(candidate.EntryAssemblyPath);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.BindingDiscovery.EntryAssemblyPathInvalid");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public void Binding_discovery_marks_missing_entry_assembly_as_load_failed()
    {
        var profileRoot = CreateTempDirectory();
        var packageRoot = CreateBindingPackage(
            profileRoot,
            "missing-entry",
            SiemensBindingKinds.PlcSimAdvanced,
            "Missing.Binding.dll",
            createAssembly: false);
        var discovery = new SiemensBindingPackageDiscovery();

        try
        {
            var result = discovery.Discover(profileRoot);

            var candidate = Assert.Single(result.Candidates);
            Assert.Equal(packageRoot, candidate.PackageRootPath);
            Assert.Equal(SiemensBindingPackageCandidateState.LoadFailed, candidate.State);
            Assert.EndsWith("Missing.Binding.dll", candidate.EntryAssemblyPath, StringComparison.Ordinal);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.BindingDiscovery.EntryAssemblyMissing");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public void Binding_discovery_rejects_invalid_declaration_entries()
    {
        var profileRoot = CreateTempDirectory();
        var packageRoot = Path.Combine(profileRoot, "lib", "bindings", "invalid-declaration");
        Directory.CreateDirectory(packageRoot);
        File.WriteAllText(Path.Combine(packageRoot, "Binding.dll"), string.Empty);
        File.WriteAllText(
            Path.Combine(packageRoot, SiemensBindingPackageDescriptorContract.FileName),
            """
            {
              "schemaVersion": 1,
              "info": {
                "bindingKind": "tia-openness",
                "bindingVersion": "1.0.0",
                "displayName": "Invalid declaration"
              },
              "entryPoint": {
                "assembly": "Binding.dll",
                "typeName": "Sample.Binding"
              },
              "declaration": {
                "accessApis": [
                  { "kind": "", "productVersion": "21", "accessApiVersion": "21.0" }
                ],
                "projectSources": [],
                "runtimes": [],
                "unitTestCommunicationContractVersions": [],
                "unitTestEventStreamContractVersions": []
              }
            }
            """);
        var discovery = new SiemensBindingPackageDiscovery();

        try
        {
            var result = discovery.Discover(profileRoot);

            var candidate = Assert.Single(result.Candidates);
            Assert.Equal(SiemensBindingPackageCandidateState.MetadataInvalid, candidate.State);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.BindingDiscovery.AccessApiDeclarationInvalid");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

}
