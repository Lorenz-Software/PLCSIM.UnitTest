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
    public async Task LoadAsync_returns_missing_registry_failure_before_service_composition()
    {
        var plugin = new SiemensAccessProfilePlugin();
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext("built-in:siemens@1.0.0"));

        var result = await LoadApplicationManagedAsync(plugin,
            CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
            CancellationToken.None);

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Session);
        Assert.Equal("Siemens profile registry is missing. Run profile registration.", result.Message);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Load.ProfileRegistryMissing");
    }

    [Fact]
    public async Task LoadAsync_uses_plcsim_only_registration_for_external_mode_and_rejects_managed_mode()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-advanced-v8",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(TestPlcsimBinding));

            var registration = await plugin.RegisterAsync(CancellationToken.None);
            var externalLoad = await plugin.LoadAsync(
                CreateSessionRequest(
                    CreateConfig(
                        SiemensAccessProfilePlugin.DefaultProfileId,
                        SiemensAccessProfilePlugin.DefaultProfileVersion,
                        projectSource: string.Empty),
                    ProjectOwnershipMode.ExternallyManaged),
                CancellationToken.None);

            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);
            Assert.True(
                externalLoad.IsSuccessful,
                string.Join(Environment.NewLine, externalLoad.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            var externalSession = Assert.IsType<SiemensAccessProfileSession>(externalLoad.Session);
            Assert.Null(externalSession.Bindings.TiaOpenness);
            await externalSession.DisposeAsync();

            var managedLoad = await LoadApplicationManagedAsync(
                plugin,
                CreateConfig(
                    SiemensAccessProfilePlugin.DefaultProfileId,
                    SiemensAccessProfilePlugin.DefaultProfileVersion,
                    projectSource: @"D:\Projects\Sample.ap21"),
                CancellationToken.None);

            Assert.False(managedLoad.IsSuccessful);
            Assert.Contains(
                managedLoad.Diagnostics,
                diagnostic => diagnostic.Code == "SiemensProfile.Load.SelectedBindingMissing");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_returns_siemens_session_after_valid_selected_evidence()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var loggerFactory = new SiemensTestLoggerFactory();
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create(loggerFactory: loggerFactory).CreateAccessProfileContext(profileRoot));

        try
        {
            CreateLoadableBindingPair(profileRoot);
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);

            var result = await LoadApplicationManagedAsync(plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.True(result.IsSuccessful);
            var session = Assert.IsType<SiemensAccessProfileSession>(result.Session);
            Assert.Empty(result.Diagnostics);
            Assert.Equal("Siemens access profile session composed.", result.Message);
            Assert.Equal(AccessHealthState.Healthy, session.Health.State);
            Assert.NotNull(session.Project);
            Assert.NotNull(session.PlcControl);
            Assert.NotNull(session.UnitTestLifecycle);
            Assert.NotNull(session.UnitTestEventStream);
            Assert.Contains(
                loggerFactory.Entries,
                entry => entry.Level == LogLevel.Information
                    && entry.Message.Contains("selected bindings loaded", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_selects_binding_pair_by_requested_project_source()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateMixedRealAndFakeBindingCandidates(profileRoot);

            var registration = await plugin.RegisterAsync(CancellationToken.None);
            var load = await LoadApplicationManagedAsync(plugin,
                CreateConfig(
                    SiemensAccessProfilePlugin.DefaultProfileId,
                    SiemensAccessProfilePlugin.DefaultProfileVersion,
                    """
                    {
                      "plcRef": { "id": "siemens-fake-plc" },
                      "runtimeRef": { "id": "fake-runtime", "instanceName": "Fake PLCSIM Runtime" }
                    }
                    """,
                    CreateSiemensFakeProject(profileRoot)),
                CancellationToken.None);

            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);
            Assert.True(
                load.IsSuccessful,
                string.Join(Environment.NewLine, load.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            var session = Assert.IsType<SiemensAccessProfileSession>(load.Session);
            Assert.Equal(typeof(FakeTiaOpennessBinding).FullName, session.Bindings.TiaOpenness!.GetType().FullName);
            Assert.Equal(typeof(FakePlcsimAdvancedBinding).FullName, session.Bindings.PlcSimAdvancedRuntime.GetType().FullName);
            Assert.Same(session.Bindings.PlcSimAdvancedRuntime, session.Bindings.PlcSimAdvancedCommunication);
            await session.DisposeAsync();
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_application_managed_mode_rejects_missing_project_source()
    {
        var profileRoot = CreateTempDirectory();
        var plugin = new SiemensAccessProfilePlugin();
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            var load = await LoadApplicationManagedAsync(
                plugin,
                CreateConfig(
                    SiemensAccessProfilePlugin.DefaultProfileId,
                    SiemensAccessProfilePlugin.DefaultProfileVersion,
                    projectSource: string.Empty),
                CancellationToken.None);

            Assert.False(load.IsSuccessful);
            Assert.Contains(load.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Config.ProjectSourceMissing");
        }
        finally
        {
            await plugin.DisposeAsync();
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_external_mode_without_project_source_loads_only_plcsim_binding()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateLoadableBindingPair(profileRoot);
            var registration = await plugin.RegisterAsync(CancellationToken.None);

            var load = await plugin.LoadAsync(
                CreateSessionRequest(
                    CreateConfig(
                        SiemensAccessProfilePlugin.DefaultProfileId,
                        SiemensAccessProfilePlugin.DefaultProfileVersion,
                        projectSource: string.Empty),
                    ProjectOwnershipMode.ExternallyManaged),
                CancellationToken.None);

            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);
            Assert.True(
                load.IsSuccessful,
                string.Join(Environment.NewLine, load.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            var session = Assert.IsType<SiemensAccessProfileSession>(load.Session);
            Assert.Null(session.Bindings.TiaOpenness);
            var projectService = Assert.IsType<SiemensExternalPlcProjectService>(session.Project);
            var unavailableProjectHandling = await projectService.DiscoverProjectTargetsAsync(
                CreateProjectRequest(string.Empty, AccessProfileConfigurationPayload.Empty),
                CancellationToken.None);
            Assert.True(unavailableProjectHandling.IsUnsuccessful);
            Assert.Contains(
                "externally managed",
                unavailableProjectHandling.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(typeof(TestPlcsimBinding).FullName, session.Bindings.PlcSimAdvancedRuntime.GetType().FullName);
            await session.DisposeAsync();
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Theory]
    [InlineData(ProjectOwnershipMode.ApplicationManaged)]
    [InlineData(ProjectOwnershipMode.ExternallyManaged)]
    public async Task Siemens_plugin_reloads_after_disposed_profile_session(
        ProjectOwnershipMode ownershipMode)
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateLoadableBindingPair(profileRoot);
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);

            for (var iteration = 0; iteration < 2; iteration++)
            {
                var configuration = CreateConfig(
                    SiemensAccessProfilePlugin.DefaultProfileId,
                    SiemensAccessProfilePlugin.DefaultProfileVersion,
                    projectSource: ownershipMode == ProjectOwnershipMode.ApplicationManaged
                        ? @"D:\Projects\Sample.ap21"
                        : string.Empty);
                var load = await plugin.LoadAsync(
                    CreateSessionRequest(configuration, ownershipMode),
                    CancellationToken.None);

                Assert.True(
                    load.IsSuccessful,
                    string.Join(Environment.NewLine, load.Diagnostics.Select(
                        diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
                await Assert.IsType<SiemensAccessProfileSession>(load.Session).DisposeAsync();
            }

            await plugin.DisposeAsync();
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_external_mode_without_project_source_rejects_ambiguous_plcsim_bindings()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-openness-v21",
                SiemensBindingKinds.TiaOpenness,
                typeof(TestTiaBinding));
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-advanced-v8-a",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(TestPlcsimBinding));
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-advanced-v8-b",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(TestPlcsimBinding));
            var registration = await plugin.RegisterAsync(CancellationToken.None);

            var load = await plugin.LoadAsync(
                CreateSessionRequest(
                    CreateConfig(
                        SiemensAccessProfilePlugin.DefaultProfileId,
                        SiemensAccessProfilePlugin.DefaultProfileVersion,
                        projectSource: string.Empty),
                    ProjectOwnershipMode.ExternallyManaged),
                CancellationToken.None);

            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);
            Assert.False(load.IsSuccessful);
            Assert.Contains("Multiple usable PLCSIM Advanced", load.Message, StringComparison.Ordinal);
            Assert.Contains(load.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Load.SelectedBindingMissing");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task Selected_binding_load_plan_uses_registered_selected_binding_evidence()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(context);

        try
        {
            CreateCompleteBindingPair(profileRoot);
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);
            var registry = store.Load(context);

            var plan = SiemensSelectedBindingLoadPlan.Create(registry);

            Assert.Equal(SiemensBindingKinds.TiaOpenness, plan.TiaOpenness!.BindingKind);
            Assert.Equal(SiemensBindingKinds.PlcSimAdvanced, plan.PlcSimAdvanced.BindingKind);
            Assert.EndsWith(
                "PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21.dll",
                plan.TiaOpenness!.EntryAssemblyPath,
                StringComparison.Ordinal);
            Assert.EndsWith(
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.dll",
                plan.PlcSimAdvanced.EntryAssemblyPath,
                StringComparison.Ordinal);
            Assert.Contains(profileRoot, plan.TiaOpenness!.PackageRootPath, StringComparison.Ordinal);
            Assert.Contains(profileRoot, plan.PlcSimAdvanced.PackageRootPath, StringComparison.Ordinal);
            Assert.Same(
                registry.BindingCandidates.Single(binding => binding.BindingKind == SiemensBindingKinds.TiaOpenness),
                plan.TiaOpenness!.Evidence);
            Assert.Same(
                registry.BindingCandidates.Single(binding => binding.BindingKind == SiemensBindingKinds.PlcSimAdvanced),
                plan.PlcSimAdvanced.Evidence);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task Selected_binding_selector_reports_ambiguous_project_source_candidates_at_load_time()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-openness-v21-a",
                SiemensBindingKinds.TiaOpenness,
                typeof(TestTiaBinding));
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-openness-v21-b",
                SiemensBindingKinds.TiaOpenness,
                typeof(TestTiaBinding));
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-advanced-v8",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(TestPlcsimBinding));
            var registration = await plugin.RegisterAsync(CancellationToken.None);

            var load = await LoadApplicationManagedAsync(plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);
            Assert.False(load.IsSuccessful);
            Assert.Null(load.Session);
            Assert.Contains(load.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Load.SelectedBindingMissing");
            Assert.Contains("Multiple usable TIA Openness", load.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task Selected_binding_loader_loads_selected_facade_instances()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(context);

        try
        {
            CreateLoadableBindingPair(profileRoot);
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);
            var registry = store.Load(context);
            var plan = SiemensSelectedBindingLoadPlan.Create(registry);
            var bindingContext = new SiemensBindingContext("siemens-test", context.Logging);
            var loader = new SiemensSelectedBindingLoader(
                context.TimeProvider,
                new SiemensTestLoggerFactory().CreateLogger("test"),
                bindingContext);

            var result = await loader.LoadAsync(plan, CancellationToken.None);

            Assert.True(result.IsSuccessful);
            Assert.Empty(result.Diagnostics);
            Assert.Equal(typeof(TestTiaBinding).FullName, result.Bindings!.TiaOpenness!.GetType().FullName);
            var initializedContext = result.Bindings.TiaOpenness.GetType()
                .GetProperty(nameof(TestTiaBinding.InitializationContext))!
                .GetValue(result.Bindings.TiaOpenness);
            Assert.Same(bindingContext, initializedContext);
            Assert.Equal(typeof(TestPlcsimBinding).FullName, result.Bindings.PlcSimAdvancedRuntime.GetType().FullName);
            Assert.Same(result.Bindings.PlcSimAdvancedRuntime, result.Bindings.PlcSimAdvancedCommunication);
            await result.Bindings.DisposeAsync();
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task Selected_binding_loader_honors_requested_cancellation_before_loading_packages()
    {
        var profileRoot = CreateTempDirectory();

        try
        {
            CreateLoadableBindingPair(profileRoot);
            var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
            var loader = new SiemensSelectedBindingLoader(
                context.TimeProvider,
                context.LoggerFactory.CreateLogger<SiemensSelectedBindingLoader>(),
                new SiemensBindingContext("siemens-test", context.Logging));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => loader.LoadAsync(
                    CreateSelectedBindingLoadPlan(profileRoot),
                    cancellation.Token));
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public void Selected_binding_loader_releases_collectible_package_contexts()
    {
        var profileRoot = CreateTempDirectory();

        try
        {
            var tiaPackage = CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-collectible",
                SiemensBindingKinds.TiaOpenness,
                typeof(TestTiaBinding));
            var plcsimPackage = CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-collectible",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(TestPlcsimBinding));

            var contexts = LoadAndDisposeSelectedBindingPackages(profileRoot);

            foreach (var context in contexts)
            {
                WaitForUnload(context);
                Assert.False(context.IsAlive);
            }

            File.WriteAllText(
                Path.Combine(tiaPackage, Path.GetFileName(typeof(TestTiaBinding).Assembly.Location)),
                "released");
            File.WriteAllText(
                Path.Combine(plcsimPackage, Path.GetFileName(typeof(TestPlcsimBinding).Assembly.Location)),
                "released");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task Selected_binding_loader_rolls_back_initialized_bindings_when_plcsim_initialization_fails()
    {
        var profileRoot = CreateTempDirectory();
        Environment.SetEnvironmentVariable("PLCSIM_UNITTEST_BINDING_DISPOSE_PATH", profileRoot);

        try
        {
            var tiaPackage = CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-disposable",
                SiemensBindingKinds.TiaOpenness,
                typeof(DisposableTestTiaBinding));
            var plcsimPackage = CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-initialize-failure",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(ThrowingInitializePlcsimBinding));
            var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
            var loader = new SiemensSelectedBindingLoader(
                context.TimeProvider,
                new SiemensTestLoggerFactory().CreateLogger("test"),
                new SiemensBindingContext("siemens-test", context.Logging));

            var result = await loader.LoadAsync(
                CreateSelectedBindingLoadPlan(profileRoot),
                CancellationToken.None);

            Assert.False(result.IsSuccessful);
            Assert.Null(result.Bindings);
            var diagnostic = Assert.Single(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "SiemensProfile.BindingLoad.EntryInitializationFailed");
            Assert.DoesNotContain(
                typeof(ThrowingInitializePlcsimBinding).FullName!,
                diagnostic.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                typeof(ThrowingInitializePlcsimBinding).FullName!,
                diagnostic.TechnicalDetail,
                StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(profileRoot, "tia-binding-disposed.marker")));
            Assert.True(File.Exists(Path.Combine(profileRoot, "plcsim-binding-disposed.marker")));
            Assert.Equal(
                new[] { "plcsim-binding-disposed.marker", "tia-binding-disposed.marker" },
                File.ReadAllLines(Path.Combine(profileRoot, "binding-dispose-order.log")));
            WriteAllTextAfterPackageUnload(
                Path.Combine(tiaPackage, Path.GetFileName(typeof(DisposableTestTiaBinding).Assembly.Location)),
                "released");
            WriteAllTextAfterPackageUnload(
                Path.Combine(plcsimPackage, Path.GetFileName(typeof(ThrowingInitializePlcsimBinding).Assembly.Location)),
                "released");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLCSIM_UNITTEST_BINDING_DISPOSE_PATH", null);
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task Selected_binding_loader_rolls_back_contract_mismatch_in_reverse_order()
    {
        var profileRoot = CreateTempDirectory();
        Environment.SetEnvironmentVariable("PLCSIM_UNITTEST_BINDING_DISPOSE_PATH", profileRoot);

        try
        {
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-disposable",
                SiemensBindingKinds.TiaOpenness,
                typeof(DisposableTestTiaBinding));
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-runtime-only",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(RuntimeOnlyPlcsimBinding));
            var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
            var loader = new SiemensSelectedBindingLoader(
                context.TimeProvider,
                new SiemensTestLoggerFactory().CreateLogger("test"),
                new SiemensBindingContext("siemens-test", context.Logging));

            var result = await loader.LoadAsync(
                CreateSelectedBindingLoadPlan(profileRoot),
                CancellationToken.None);

            Assert.False(result.IsSuccessful);
            Assert.Null(result.Bindings);
            var diagnostic = Assert.Single(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "SiemensProfile.BindingLoad.ContractViolation.PlcSimCommunication");
            Assert.DoesNotContain(
                typeof(RuntimeOnlyPlcsimBinding).FullName!,
                diagnostic.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                typeof(RuntimeOnlyPlcsimBinding).FullName!,
                diagnostic.TechnicalDetail,
                StringComparison.Ordinal);
            Assert.Equal(
                new[] { "plcsim-binding-disposed.marker", "tia-binding-disposed.marker" },
                File.ReadAllLines(Path.Combine(profileRoot, "binding-dispose-order.log")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLCSIM_UNITTEST_BINDING_DISPOSE_PATH", null);
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task Selected_bindings_dispose_each_package_exactly_once()
    {
        var profileRoot = CreateTempDirectory();
        Environment.SetEnvironmentVariable("PLCSIM_UNITTEST_BINDING_DISPOSE_PATH", profileRoot);

        try
        {
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-disposable",
                SiemensBindingKinds.TiaOpenness,
                typeof(DisposableTestTiaBinding));
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-disposable",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(DisposableTestPlcsimBinding));
            var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
            var loader = new SiemensSelectedBindingLoader(
                context.TimeProvider,
                new SiemensTestLoggerFactory().CreateLogger("test"),
                new SiemensBindingContext("siemens-test", context.Logging));
            var result = await loader.LoadAsync(
                CreateSelectedBindingLoadPlan(profileRoot),
                CancellationToken.None);

            Assert.True(result.IsSuccessful);
            await result.Bindings!.DisposeAsync();
            await result.Bindings.DisposeAsync();

            Assert.Equal(
                new[] { "plcsim-binding-disposed.marker", "tia-binding-disposed.marker" },
                File.ReadAllLines(Path.Combine(profileRoot, "binding-dispose-order.log")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLCSIM_UNITTEST_BINDING_DISPOSE_PATH", null);
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_returns_stale_diagnostic_when_selected_binding_assembly_changed_after_registration()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-loadable",
                SiemensBindingKinds.TiaOpenness,
                typeof(TestTiaBinding));
            var plcsimPackage = CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-corrupt",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(TestPlcsimBinding));
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);
            WriteAllTextAfterPackageUnload(
                Path.Combine(plcsimPackage, Path.GetFileName(typeof(TestPlcsimBinding).Assembly.Location)),
                "not a managed assembly");

            var result = await LoadApplicationManagedAsync(plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.False(result.IsSuccessful);
            Assert.Null(result.Session);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Load.SelectedBindingStale");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_returns_diagnostic_when_plcsim_binding_lacks_communication_contract()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-loadable",
                SiemensBindingKinds.TiaOpenness,
                typeof(TestTiaBinding));
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-runtime-only",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(RuntimeOnlyPlcsimBinding));
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);

            var result = await LoadApplicationManagedAsync(plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.False(result.IsSuccessful);
            Assert.Null(result.Session);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.BindingLoad.ContractViolation.PlcSimCommunication");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_accepts_ap21_with_registered_non_v8_plcsim_runtime()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "tia-loadable",
                SiemensBindingKinds.TiaOpenness,
                typeof(TestTiaBinding));
            CreateTestAssemblyBindingPackage(
                profileRoot,
                "plcsim-loadable-v9",
                SiemensBindingKinds.PlcSimAdvanced,
                typeof(TestPlcsimBinding),
                runtimeVersion: "9.0");
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);

            var result = await LoadApplicationManagedAsync(plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.True(
                result.IsSuccessful,
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            Assert.IsType<SiemensAccessProfileSession>(result.Session);
            await result.Session!.DisposeAsync();
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_composes_deployed_fake_binding_packages_through_normal_registration_path()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            CreateDeployedFakeBindingPackage(
                profileRoot,
                "tia-openness-fake",
                GetFakeTiaBindingManifestPath(),
                typeof(FakeTiaOpennessBinding).Assembly.Location);
            CreateDeployedFakeBindingPackage(
                profileRoot,
                "plcsim-advanced-fake",
                GetFakePlcsimBindingManifestPath(),
                typeof(FakePlcsimAdvancedBinding).Assembly.Location);

            var registration = await plugin.RegisterAsync(CancellationToken.None);
            var load = await LoadApplicationManagedAsync(plugin,
                new AccessProfileConfig(
                    SiemensAccessProfilePlugin.DefaultProfileId,
                    SiemensAccessProfilePlugin.DefaultProfileVersion,
                    new AccessProfileConfigurationPayload(
                        """
                        {
                          "plcRef": { "id": "siemens-fake-plc" },
                          "runtimeRef": { "id": "fake-runtime", "instanceName": "Fake PLCSIM Runtime" }
                        }
                        """),
                    CreateSiemensFakeProject(profileRoot)),
                CancellationToken.None);

            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);
            Assert.True(
                load.IsSuccessful,
                string.Join(Environment.NewLine, load.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            var session = Assert.IsType<SiemensAccessProfileSession>(load.Session);
            Assert.IsType<SiemensTiaPlcProjectService>(session.Project);
            Assert.IsType<SiemensPlcsimPlcControlService>(session.PlcControl);
            Assert.IsType<SiemensPlcsimUnitTestLifecycleService>(session.UnitTestLifecycle);
            Assert.IsType<SiemensPlcsimUnitTestEventStreamService>(session.UnitTestEventStream);
            await session.DisposeAsync();
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_fails_when_profile_registry_is_unavailable()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));

        try
        {
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Unavailable, registration.State);

            var result = await LoadApplicationManagedAsync(plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.False(result.IsSuccessful);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Load.ProfileRegistryUnavailable");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_fails_when_profile_registry_is_in_error_state()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
        plugin.Initialize(context);

        try
        {
            var errorResult = new AccessProfileRegistrationResult(
                AccessProfileRegistrationState.Error,
                AccessProfileCapabilities.Empty,
                "Registration failed.");
            await store.SaveAsync(
                context,
                SiemensProfileRegistry.FromRegistrationResult(
                    SiemensAccessProfilePlugin.CreateDefaultInfo(),
                    profileRoot,
                    errorResult,
                    FakeTime.DefaultStart),
                CancellationToken.None);

            var result = await LoadApplicationManagedAsync(plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.False(result.IsSuccessful);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Load.ProfileRegistryErrorState");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_fails_when_selected_binding_evidence_is_missing()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
        plugin.Initialize(context);

        try
        {
            await store.SaveAsync(context, CreateRegistry(profileRoot), CancellationToken.None);

            var result = await LoadApplicationManagedAsync(plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.False(result.IsSuccessful);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Load.SelectedBindingMissing");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_fails_when_selected_binding_package_fingerprint_is_stale()
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
            File.WriteAllText(Path.Combine(profileRoot, "lib", "bindings", "plcsim-advanced-v8", "extra.txt"), "changed");

            var result = await LoadApplicationManagedAsync(plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.False(result.IsSuccessful);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Load.SelectedBindingStale");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_fails_when_profile_registry_misses_required_collection()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
        plugin.Initialize(context);

        try
        {
            await store.SaveAsync(context, CreateRegistry(profileRoot), CancellationToken.None);
            var registryPath = store.GetRegistryPath(context);
            var json = await File.ReadAllTextAsync(registryPath);
            json = json.Replace(
                "\"bindingCandidates\": []",
                "\"bindingCandidates\": null",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(registryPath, json);

            var result = await LoadApplicationManagedAsync(
                plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.False(result.IsSuccessful);
            Assert.Null(result.Session);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "SiemensProfile.Load.ProfileRegistryCorrupt");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_fails_when_selected_binding_entry_point_evidence_differs_from_descriptor()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        var context = TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot);
        plugin.Initialize(context);

        try
        {
            CreateLoadableBindingPair(profileRoot);
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);

            var registry = await store.LoadAsync(context, CancellationToken.None);
            var candidates = registry.BindingCandidates
                .Select(candidate => string.Equals(
                    candidate.BindingKind,
                    SiemensBindingKinds.PlcSimAdvanced,
                    StringComparison.OrdinalIgnoreCase)
                    ? candidate with
                    {
                        EntryPoint = candidate.EntryPoint with
                        {
                            TypeName = typeof(TestTiaBinding).FullName!
                        }
                    }
                    : candidate)
                .ToArray();
            await store.SaveAsync(
                context,
                registry with { BindingCandidates = candidates },
                CancellationToken.None);

            var result = await LoadApplicationManagedAsync(
                plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.False(result.IsSuccessful);
            Assert.Null(result.Session);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "SiemensProfile.Load.SelectedBindingMismatch");
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task LoadAsync_logs_precondition_diagnostic_codes_for_loadability_failures()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var loggerFactory = new SiemensTestLoggerFactory();
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create(loggerFactory: loggerFactory).CreateAccessProfileContext(profileRoot));

        try
        {
            var result = await LoadApplicationManagedAsync(plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.False(result.IsSuccessful);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Load.ProfileRegistryMissing");
            Assert.Contains(
                loggerFactory.Entries,
                entry => entry.Level == LogLevel.Error
                    && entry.Message.Contains("ProfileRegistryMissing", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

}
