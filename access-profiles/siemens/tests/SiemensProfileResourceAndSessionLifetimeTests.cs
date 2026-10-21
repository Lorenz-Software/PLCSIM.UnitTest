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
    public async Task Initialize_rejects_second_context_instead_of_mixing_plugin_state()
    {
        var plugin = new SiemensAccessProfilePlugin();
        var firstContext = TestCoreServiceContext.Create().CreateAccessProfileContext("built-in:siemens@1.0.0");
        var secondContext = TestCoreServiceContext.Create().CreateAccessProfileContext("built-in:siemens-other@1.0.0");
        plugin.Initialize(firstContext);

        var exception = Assert.Throws<InvalidOperationException>(() => plugin.Initialize(secondContext));

        Assert.Contains("already been initialized", exception.Message, StringComparison.Ordinal);
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task Active_session_rejects_registration_load_and_plugin_disposal_until_session_is_disposed()
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

            var load = await LoadApplicationManagedAsync(
                plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);
            var session = Assert.IsType<SiemensAccessProfileSession>(load.Session);

            var rejectedRegistration = await plugin.RegisterAsync(CancellationToken.None);
            var rejectedLoad = await LoadApplicationManagedAsync(
                plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);
            var disposeFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await plugin.DisposeAsync());

            Assert.Equal(AccessProfileRegistrationState.Error, rejectedRegistration.State);
            Assert.Contains("active profile session", rejectedRegistration.Message, StringComparison.Ordinal);
            Assert.False(rejectedLoad.IsSuccessful);
            Assert.Contains(
                rejectedLoad.Diagnostics,
                diagnostic => diagnostic.Code == "SiemensProfile.Load.OperationRejected");
            Assert.Contains("profile session is active", disposeFailure.Message, StringComparison.Ordinal);

            await session.DisposeAsync();

            var registrationAfterSession = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registrationAfterSession.State);
            await plugin.DisposeAsync();
        }
        finally
        {
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task Resource_scope_cleans_owned_resources_in_reverse_order_and_reports_failures()
    {
        var order = new List<string>();
        var scope = new SiemensProfileResourceScope(TimeProvider.System, new SiemensTestLoggerFactory().CreateLogger("test"));
        scope.OwnOptionalDisposable("first", new RecordingAsyncDisposable("first", order));
        scope.OwnOptionalDisposable("second", new RecordingDisposable("second", order));
        scope.OwnOptionalDisposable("broken", new ThrowingAsyncDisposable("broken", order));

        var result = await scope.CleanupAsync(CancellationToken.None);

        Assert.False(result.IsSuccessful);
        Assert.Equal(new[] { "broken", "second", "first" }, order);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("SiemensProfile.ResourceCleanupFailed", diagnostic.Code);
        Assert.Contains("broken", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resource_scope_continues_transactional_cleanup_after_cancellation()
    {
        var order = new List<string>();
        var scope = new SiemensProfileResourceScope(TimeProvider.System, new SiemensTestLoggerFactory().CreateLogger("test"));
        scope.OwnOptionalDisposable("first", new RecordingAsyncDisposable("first", order));
        scope.OwnOptionalDisposable("second", new RecordingDisposable("second", order));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await scope.CleanupAsync(cancellation.Token);

        Assert.True(result.IsSuccessful);
        Assert.Equal(new[] { "second", "first" }, order);
    }

    [Fact]
    public async Task Resource_scope_release_ownership_skips_cleanup()
    {
        var order = new List<string>();
        var scope = new SiemensProfileResourceScope(TimeProvider.System, new SiemensTestLoggerFactory().CreateLogger("test"));
        scope.OwnOptionalDisposable("first", new RecordingAsyncDisposable("first", order));

        scope.ReleaseOwnership();
        var result = await scope.CleanupAsync(CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Empty(order);
    }

    [Fact]
    public async Task Siemens_session_dispose_cleans_loaded_bindings()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));
        DisposableTestTiaBinding.DisposeCount = 0;
        DisposableTestPlcsimBinding.DisposeCount = 0;
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
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);

            var result = await LoadApplicationManagedAsync(plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.True(result.IsSuccessful);
            Assert.Equal(0, DisposableTestTiaBinding.DisposeCount);
            Assert.Equal(0, DisposableTestPlcsimBinding.DisposeCount);

            await result.Session!.DisposeAsync();

            Assert.True(File.Exists(Path.Combine(profileRoot, "tia-binding-disposed.marker")));
            Assert.True(File.Exists(Path.Combine(profileRoot, "plcsim-binding-disposed.marker")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLCSIM_UNITTEST_BINDING_DISPOSE_PATH", null);
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task Session_composition_failure_rolls_back_selected_bindings_and_releases_plugin_ownership()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        var loggerFactory = new ThrowingCategoryLoggerFactory(typeof(SiemensTiaPlcProjectService).FullName!);
        plugin.Initialize(TestCoreServiceContext.Create(loggerFactory: loggerFactory).CreateAccessProfileContext(profileRoot));
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
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registration.State);
            DeleteBindingDisposeEvidence(profileRoot);

            var load = await LoadApplicationManagedAsync(
                plugin,
                CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
                CancellationToken.None);

            Assert.False(load.IsSuccessful);
            Assert.Null(load.Session);
            Assert.Contains(
                load.Diagnostics,
                diagnostic => diagnostic.Code == "SiemensProfile.Load.SessionCompositionFailed");
            Assert.Equal(
                new[] { "plcsim-binding-disposed.marker", "tia-binding-disposed.marker" },
                File.ReadAllLines(Path.Combine(profileRoot, "binding-dispose-order.log")));

            var registrationAfterFailure = await plugin.RegisterAsync(CancellationToken.None);
            Assert.Equal(AccessProfileRegistrationState.Registered, registrationAfterFailure.State);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLCSIM_UNITTEST_BINDING_DISPOSE_PATH", null);
            DeleteTemporaryDirectory(profileRoot);
        }
    }

    [Fact]
    public async Task Siemens_external_session_disposes_only_loaded_plcsim_binding()
    {
        var profileRoot = CreateTempDirectory();
        var store = new FileSiemensProfileRegistryStore(new FixedSiemensProfileRegistryPathResolver(profileRoot));
        var plugin = CreatePluginWithStore(store);
        plugin.Initialize(TestCoreServiceContext.Create().CreateAccessProfileContext(profileRoot));
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
            var registration = await plugin.RegisterAsync(CancellationToken.None);
            File.Delete(Path.Combine(profileRoot, "tia-binding-disposed.marker"));
            File.Delete(Path.Combine(profileRoot, "plcsim-binding-disposed.marker"));

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

            await session.DisposeAsync();

            Assert.False(File.Exists(Path.Combine(profileRoot, "tia-binding-disposed.marker")));
            Assert.True(File.Exists(Path.Combine(profileRoot, "plcsim-binding-disposed.marker")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLCSIM_UNITTEST_BINDING_DISPOSE_PATH", null);
            DeleteTemporaryDirectory(profileRoot);
        }
    }

}
