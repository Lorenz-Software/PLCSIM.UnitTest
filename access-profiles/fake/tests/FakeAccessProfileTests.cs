using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Fake;

public sealed class FakeAccessProfileTests
{
    [Fact]
    public async Task Fake_profile_plugin_creates_profile_with_all_functional_services()
    {
        var timeProvider = FakeTime.Create();
        var plugin = FakeAccessProfileFactory.CreatePlugin(timeProvider);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();

        plugin.Initialize(new AccessProfileContext(
            NullLoggerFactory.Instance,
            timeProvider,
            CreateTempDirectory(),
            CreateTempDirectory(),
            TestOperationalLogging.Options));

        var load = await plugin.LoadAsync(
            new AccessProfileSessionRequest(
                config.ProfileId,
                config.ProfileVersion,
                config.ProjectSource,
                ProjectOwnershipMode.ApplicationManaged),
            CancellationToken.None);
        var profile = Assert.IsType<FakeAccessProfile>(load.Session);

        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, profile.Info.ProfileId);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileVersion, profile.Info.Version);
        Assert.Equal(FakeAccessProfilePlugin.DefaultDisplayName, profile.Info.DisplayName);
        Assert.Equal(FakeAccessProfilePlugin.DefaultDescription, profile.Info.Description);
        Assert.NotNull(profile.Configuration);
        Assert.NotNull(profile.Project);
        Assert.NotNull(profile.PlcControl);
        Assert.NotNull(profile.UnitTestLifecycle);
        Assert.NotNull(profile.UnitTestEventStream);
    }

    [Fact]
    public async Task Fake_profile_plugin_factory_receives_session_setup_only()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        AccessProfileSessionRequest? capturedRequest = null;
        var plugin = new FakeAccessProfilePlugin((request, _) =>
        {
            capturedRequest = request;
            return services;
        });
        plugin.Initialize(new AccessProfileContext(
            NullLoggerFactory.Instance,
            timeProvider,
            CreateTempDirectory(),
            CreateTempDirectory(),
            TestOperationalLogging.Options));

        var load = await plugin.LoadAsync(
            new AccessProfileSessionRequest(
                FakeAccessProfilePlugin.DefaultProfileId,
                FakeAccessProfilePlugin.DefaultProfileVersion,
                "setup-project.ap",
                ProjectOwnershipMode.ApplicationManaged),
            CancellationToken.None);

        Assert.True(load.IsSuccessful);
        Assert.NotNull(capturedRequest);
        Assert.Equal("setup-project.ap", capturedRequest!.ProjectSource);
        Assert.Equal(ProjectOwnershipMode.ApplicationManaged, capturedRequest.ProjectOwnershipMode);
    }

    [Fact]
    public async Task Fake_profile_plugin_supports_external_project_handling_without_project_source()
    {
        var timeProvider = FakeTime.Create();
        var plugin = FakeAccessProfileFactory.CreatePlugin(timeProvider);
        var config = FakeAccessProfileFactory.CreateDefaultConfig() with { ProjectSource = string.Empty };

        plugin.Initialize(new AccessProfileContext(
            NullLoggerFactory.Instance,
            timeProvider,
            CreateTempDirectory(),
            CreateTempDirectory(),
            TestOperationalLogging.Options));

        var load = await plugin.LoadAsync(
            new AccessProfileSessionRequest(
                config.ProfileId,
                config.ProfileVersion,
                config.ProjectSource,
                ProjectOwnershipMode.ExternallyManaged),
            CancellationToken.None);
        var profile = Assert.IsType<FakeAccessProfile>(load.Session);
        var runtimeTargets = await profile.PlcControl.DiscoverRuntimeTargetsAsync(
            new PlcRuntimeDiscoveryRequest(config),
            CancellationToken.None);

        Assert.True(load.IsSuccessful);
        Assert.True(runtimeTargets.IsSuccessful);
        Assert.Single(runtimeTargets.Value!.Candidates);
    }

    [Fact]
    public void Fake_default_config_uses_stable_discovery_identity()
    {
        var config = FakeAccessProfileFactory.CreateDefaultConfig();

        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileId, config.ProfileId);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileVersion, config.ProfileVersion);
    }

    [Fact]
    public void Fake_profile_rejects_mismatching_profile_id()
    {
        var plugin = FakeAccessProfileFactory.CreatePlugin();
        var validation = plugin.ValidateConfig(new AccessProfileConfig(
            "siemens",
            FakeAccessProfilePlugin.DefaultProfileVersion,
            AccessProfileConfigurationPayload.Empty,
            "sample.ap"));

        Assert.False(validation.IsValid);
        Assert.Single(validation.Diagnostics);
        Assert.Equal("FakeProfile.Config.ProfileMismatch", validation.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Fake_profile_executes_minimal_prepared_path_without_siemens()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var profile = new FakeAccessProfile(
            new AccessProfileInfo("fake", "Fake Access Profile", "1.0.0", "Test profile."),
            services,
            timeProvider);

        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        var runtimeTarget = new RuntimeTargetRef(
            new RuntimeTargetId("fake-runtime-target"),
            "Fake PLC Runtime Target",
            "FakeRuntimeTarget");
        var projectResult = await profile.Project.PrepareProjectAsync(
            new PlcProjectRequest(config),
            CancellationToken.None);
        Assert.True(projectResult.IsSuccessful);
        Assert.NotNull(projectResult.Value);
        Assert.Equal(
            config,
            Assert.IsType<FakePlcProjectService>(services.Project).LastPrepareProjectRequest?.Configuration);

        var projectTargetResult = await profile.Project.ResolveProjectTargetAsync(projectResult.Value, CancellationToken.None);
        Assert.True(projectTargetResult.IsSuccessful);
        Assert.NotNull(projectTargetResult.Value?.SelectedTarget);

        var runtimeResult = await profile.PlcControl.PrepareRuntimeAsync(
            new PlcRuntimePrepareRequest(config, RuntimeProvisioningMode.UseExisting, runtimeTarget),
            CancellationToken.None);
        Assert.True(runtimeResult.IsSuccessful);
        Assert.NotNull(runtimeResult.Value);
        var runtimePrepareRequest = Assert.IsType<FakePlcControlService>(services.PlcControl).LastPrepareRuntimeRequest;
        Assert.Equal(config, runtimePrepareRequest?.Configuration);
        Assert.Equal(RuntimeProvisioningMode.UseExisting, runtimePrepareRequest?.ProvisioningMode);
        Assert.Equal(runtimeTarget, runtimePrepareRequest?.RuntimeTarget);

        var runtimeTargetResult = await profile.PlcControl.ResolveRuntimeTargetAsync(runtimeResult.Value.Runtime, CancellationToken.None);
        Assert.True(runtimeTargetResult.IsSuccessful);
        Assert.NotNull(runtimeTargetResult.Value?.SelectedTarget);

        var startResult = await profile.PlcControl.StartAsync(runtimeResult.Value.Runtime, runtimeTargetResult.Value.SelectedTarget, CancellationToken.None);
        Assert.True(startResult.IsSuccessful);
        Assert.True(startResult.Value?.ReadyForExecution);

        var communicationResult = await profile.PrepareUnitTestCommunicationAsync(
            new CommunicationPrepareRequest(
                config,
                new ProjectExecutionContext(
                    ProjectOwnershipMode.ApplicationManaged,
                    projectResult.Value,
                    projectTargetResult.Value.SelectedTarget),
                runtimeResult.Value.Runtime,
                runtimeTargetResult.Value.SelectedTarget),
            CancellationToken.None);

        Assert.True(communicationResult.IsSuccessful);
        Assert.NotNull(communicationResult.Value);
        Assert.Equal(config, profile.LastPrepareUnitTestCommunicationRequest?.Configuration);
        Assert.Equal(FakeTime.DefaultStart, communicationResult.Value.PreparedAt);

        var unitTestContext = new UnitTestContext(
            communicationResult.Value,
            new ProjectExecutionContext(
                ProjectOwnershipMode.ApplicationManaged,
                projectResult.Value,
                projectTargetResult.Value.SelectedTarget),
            runtimeResult.Value.Runtime,
            runtimeTargetResult.Value.SelectedTarget);

        var monitoringResult = await profile.UnitTestEventStream.StartMonitoringAsync(unitTestContext, CancellationToken.None);
        Assert.True(monitoringResult.IsSuccessful);
        Assert.True(monitoringResult.Value?.IsMonitoring);

        var startUnitTestResult = await profile.UnitTestLifecycle.StartAsync(unitTestContext, CancellationToken.None);
        Assert.True(startUnitTestResult.IsSuccessful);
        Assert.True(startUnitTestResult.Value?.Accepted);
        Assert.IsType<FakeUnitTestLifecycleService>(profile.UnitTestLifecycle)
            .SimulateExternalRunStarted();
        Assert.Equal(UnitTestLifecycleState.Running, profile.UnitTestLifecycle.CurrentObservation.State);
    }

    [Fact]
    public async Task Fake_profile_reconnect_preserves_runtime_context_and_target()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var profile = new FakeAccessProfile(
            new AccessProfileInfo("fake", "Fake Access Profile", "1.0.0", "Test profile."),
            services,
            timeProvider);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        var target = Assert.IsType<FakePlcControlService>(services.PlcControl).DefaultRuntimeTarget;
        var prepared = await profile.PlcControl.PrepareRuntimeAsync(
            new PlcRuntimePrepareRequest(config, RuntimeProvisioningMode.UseExisting, target),
            CancellationToken.None);
        var originalRuntime = Assert.IsType<PreparedPlcRuntime>(prepared.Value);
        var plcControl = profile.PlcControl;
        var lifecycle = profile.UnitTestLifecycle;
        var eventStream = profile.UnitTestEventStream;

        var result = await profile.ReconnectRuntimeAsync(
            new PlcRuntimeReconnectRequest(
                config,
                RuntimeProvisioningMode.UseExisting,
                originalRuntime.Runtime,
                target),
            CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(result.Value);
        Assert.Equal(originalRuntime.Runtime.Id, result.Value.Runtime.Id);
        Assert.Equal(target, result.Value.RuntimeTarget);
        Assert.Same(plcControl, profile.PlcControl);
        Assert.Same(lifecycle, profile.UnitTestLifecycle);
        Assert.Same(eventStream, profile.UnitTestEventStream);
        Assert.Equal(
            1,
            Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle).ReleaseCommunicationCount);
        Assert.Equal(
            1,
            Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream).ReleaseCommunicationCount);
        Assert.Equal(target, profile.LastReconnectRuntimeRequest?.RuntimeTarget);
    }

    [Fact]
    public void Fake_plc_control_named_external_state_helpers_publish_runtime_state()
    {
        var service = new FakePlcControlService(FakeTime.Create());
        var observedStates = new List<PlcState>();
        service.PlcStateChangedEventHandler += (_, args) => observedStates.Add(args.State);

        service.SimulateExternalPlcRunning();
        service.SimulateExternalPlcStopped();
        service.SimulateRuntimeContextLost();
        service.SimulatePlcControlUnavailable();

        Assert.Equal(4, observedStates.Count);
        Assert.Equal(PlcOperatingState.Running, observedStates[0].OperatingState);
        Assert.True(observedStates[0].ReadyForExecution);
        Assert.Equal(PlcOperatingState.Stopped, observedStates[1].OperatingState);
        Assert.False(observedStates[1].ReadyForExecution);
        Assert.Equal(RuntimeInstanceState.Unavailable, observedStates[2].InstanceState);
        Assert.Equal(PlcOperatingState.Unavailable, observedStates[2].OperatingState);
        Assert.Empty(observedStates[2].RuntimeTargets);
        Assert.Equal(RuntimeInstanceState.Unavailable, observedStates[3].InstanceState);
        Assert.Equal(PlcOperatingState.Unavailable, observedStates[3].OperatingState);
        Assert.Equal(ServiceState.Invalidated, service.State);
    }

    [Fact]
    public async Task Fake_plc_control_can_hold_start_while_external_state_is_published()
    {
        var service = new FakePlcControlService(FakeTime.Create())
        {
            HoldNextStart = true
        };
        var runtime = new PlcRuntimeContext(new RuntimeContextId("fake-runtime"), "Fake PLC Runtime");
        var observedStates = new List<PlcState>();
        service.PlcStateChangedEventHandler += (_, args) => observedStates.Add(args.State);

        var start = service.StartAsync(runtime, service.DefaultRuntimeTarget, CancellationToken.None);
        service.SimulateExternalPlcStopped();

        Assert.True(service.IsStartPending);
        Assert.False(start.IsCompleted);
        Assert.Equal(PlcOperatingState.Stopped, Assert.Single(observedStates).OperatingState);

        service.CompletePendingStartAsRunning();
        var result = await start;

        Assert.True(result.IsSuccessful);
        Assert.False(service.IsStartPending);
        Assert.Equal(PlcOperatingState.Running, result.Value?.OperatingState);
        Assert.Equal(PlcOperatingState.Running, service.CurrentState.OperatingState);
    }

    [Fact]
    public async Task Fake_plc_control_can_hold_and_fail_stop()
    {
        var service = new FakePlcControlService(FakeTime.Create())
        {
            HoldNextStop = true
        };
        var runtime = new PlcRuntimeContext(new RuntimeContextId("fake-runtime"), "Fake PLC Runtime");

        var stop = service.StopAsync(runtime, service.DefaultRuntimeTarget, CancellationToken.None);

        Assert.True(service.IsStopPending);
        Assert.False(stop.IsCompleted);

        service.FailPendingStop("Fake stop interrupted.");
        var result = await stop;

        Assert.False(result.IsSuccessful);
        Assert.False(service.IsStopPending);
        Assert.Equal("Fake stop interrupted.", result.Message);
    }

    [Fact]
    public async Task Fake_plc_control_keeps_runtime_resource_when_requested()
    {
        var service = new FakePlcControlService(FakeTime.Create());
        var request = new PlcRuntimeReleaseRequest(
            new PlcRuntimeContext(new RuntimeContextId("fake-runtime"), "Fake PLC Runtime"),
            service.DefaultRuntimeTarget,
            RuntimeResourceReleaseMode.KeepResource);

        var result = await service.ReleaseRuntimeAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(PlcRuntimeReleaseOutcome.ResourceKept, result.Value);
        Assert.Equal(request, service.LastReleaseRuntimeRequest);
        Assert.Equal(ServiceState.NotPrepared, service.State);
    }

    [Fact]
    public async Task Fake_plc_control_destroys_runtime_resource_when_requested()
    {
        var service = new FakePlcControlService(FakeTime.Create());
        var request = new PlcRuntimeReleaseRequest(
            new PlcRuntimeContext(new RuntimeContextId("fake-runtime"), "Fake PLC Runtime"),
            service.DefaultRuntimeTarget,
            RuntimeResourceReleaseMode.DestroyResource);

        var result = await service.ReleaseRuntimeAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccessful);
        Assert.Equal(PlcRuntimeReleaseOutcome.ResourceDestroyed, result.Value);
        Assert.Equal(request, service.LastReleaseRuntimeRequest);
        Assert.Equal(ServiceState.NotPrepared, service.State);
    }

    [Fact]
    public async Task Fake_plc_control_reports_unsupported_destroy_with_kept_resource()
    {
        var service = new FakePlcControlService(FakeTime.Create())
        {
            SupportsRuntimeDestruction = false
        };
        var request = new PlcRuntimeReleaseRequest(
            new PlcRuntimeContext(new RuntimeContextId("fake-runtime"), "Fake PLC Runtime"),
            service.DefaultRuntimeTarget,
            RuntimeResourceReleaseMode.DestroyResource);

        var result = await service.ReleaseRuntimeAsync(request, CancellationToken.None);

        Assert.Equal(OperationResultState.NotSupported, result.State);
        Assert.True(result.IsUnsuccessful);
        Assert.Equal(PlcRuntimeReleaseOutcome.ResourceKept, result.Value);
        Assert.Equal(request, service.LastReleaseRuntimeRequest);
        Assert.Equal(ServiceState.NotPrepared, service.State);
    }

    [Fact]
    public async Task Fake_profile_can_simulate_unittest_communication_prepare_failure()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider) with
        {
            PrepareUnitTestCommunicationResult = OperationResult<CommunicationSession>.Failure("Fake communication unavailable.")
        };
        var profile = new FakeAccessProfile(
            new AccessProfileInfo("fake", "Fake Access Profile", "1.0.0", "Test profile."),
            services,
            timeProvider);
        var diagnostics = new List<PLCSIM.UnitTest.Common.Diagnostics.Diagnostic>();
        profile.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);
        var project = new PlcProjectContext(new ProjectContextId("fake-project"), "Fake PLC Project");
        var runtime = new PlcRuntimeContext(new RuntimeContextId("fake-runtime"), "Fake PLC Runtime");
        var projectTarget = new ProjectTargetRef(new ProjectTargetId("fake-project-target"), "Fake PLC", "CPU 1516");
        var runtimeTarget = new RuntimeTargetRef(new RuntimeTargetId("fake-runtime-target"), "Fake Runtime", "192.0.2.10");

        var result = await profile.PrepareUnitTestCommunicationAsync(
            new CommunicationPrepareRequest(
                FakeAccessProfileFactory.CreateDefaultConfig(),
                new ProjectExecutionContext(ProjectOwnershipMode.ApplicationManaged, project, projectTarget),
                runtime,
                runtimeTarget),
            CancellationToken.None);

        Assert.False(result.IsSuccessful);
        Assert.Equal("Fake communication unavailable.", result.Message);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "Fake.Communication.Prepare.Failed");
    }

    [Fact]
    public async Task Fake_profile_can_hold_and_complete_unittest_communication_prepare()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        services.HoldNextPrepareUnitTestCommunication = true;
        var profile = new FakeAccessProfile(
            new AccessProfileInfo("fake", "Fake Access Profile", "1.0.0", "Test profile."),
            services,
            timeProvider);

        var prepare = profile.PrepareUnitTestCommunicationAsync(
            CreateCommunicationPrepareRequest(),
            CancellationToken.None);

        Assert.True(services.IsPrepareUnitTestCommunicationPending);
        Assert.False(prepare.IsCompleted);

        var session = new CommunicationSession(
            new CommunicationSessionId("held-session"),
            timeProvider.GetUtcNow());
        services.CompletePendingPrepareUnitTestCommunication(
            OperationResult<CommunicationSession>.Success(session));
        var result = await prepare;

        Assert.True(result.IsSuccessful);
        Assert.False(services.IsPrepareUnitTestCommunicationPending);
        Assert.Equal("held-session", result.Value?.SessionId.Value);
    }

    [Fact]
    public async Task Fake_profile_can_hold_and_fail_unittest_communication_prepare()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        services.HoldNextPrepareUnitTestCommunication = true;
        var profile = new FakeAccessProfile(
            new AccessProfileInfo("fake", "Fake Access Profile", "1.0.0", "Test profile."),
            services,
            timeProvider);
        var diagnostics = new List<PLCSIM.UnitTest.Common.Diagnostics.Diagnostic>();
        profile.DiagnosticEventHandler += (_, args) => diagnostics.Add(args.Diagnostic);

        var prepare = profile.PrepareUnitTestCommunicationAsync(
            CreateCommunicationPrepareRequest(),
            CancellationToken.None);
        services.FailPendingPrepareUnitTestCommunication("Fake prepare interrupted.");
        var result = await prepare;

        Assert.False(result.IsSuccessful);
        Assert.False(services.IsPrepareUnitTestCommunicationPending);
        Assert.Equal("Fake prepare interrupted.", result.Message);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "Fake.Communication.Prepare.Failed");
    }

    [Fact]
    public async Task Fake_profile_cancels_held_unittest_communication_prepare()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        services.HoldNextPrepareUnitTestCommunication = true;
        var profile = new FakeAccessProfile(
            new AccessProfileInfo("fake", "Fake Access Profile", "1.0.0", "Test profile."),
            services,
            timeProvider);
        using var cancellation = new CancellationTokenSource();

        var prepare = profile.PrepareUnitTestCommunicationAsync(
            CreateCommunicationPrepareRequest(),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);
        Assert.False(services.IsPrepareUnitTestCommunicationPending);
    }

    [Fact]
    public async Task Fake_profile_registration_uses_profile_data_directory_and_ignores_legacy_cache()
    {
        var timeProvider = FakeTime.Create();
        var profileRoot = CreateTempDirectory();
        var profileDataDirectory = CreateTempDirectory();
        var legacyRegistryPath = Path.Combine(profileRoot, "cache", "profile-registry.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyRegistryPath)!);
        File.WriteAllText(legacyRegistryPath, "{");
        var store = new FileFakeProfileRegistryStore();
        var plugin = FakeAccessProfileFactory.CreatePlugin(timeProvider, profileRegistryStore: store);
        var context = new AccessProfileContext(
            NullLoggerFactory.Instance,
            timeProvider,
            profileRoot,
            profileDataDirectory,
            TestOperationalLogging.Options);

        plugin.Initialize(context);
        var result = await plugin.RegisterAsync(CancellationToken.None);
        var registry = await store.LoadAsync(context, CancellationToken.None);

        Assert.Equal(AccessProfileRegistrationState.Registered, result.State);
        Assert.Equal(
            Path.Combine(profileDataDirectory, "profile-registry.json"),
            store.GetRegistryPath(context));
        Assert.True(File.Exists(Path.Combine(profileDataDirectory, "profile-registry.json")));
        Assert.Equal("{", File.ReadAllText(legacyRegistryPath));
        Assert.Equal(FakeProfileRegistry.CurrentSchemaVersion, registry.SchemaVersion);
        Assert.Equal(AccessProfileRegistrationState.Registered, registry.State);
        var source = Assert.Single(registry.ProjectSources);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProjectSourceKind, source.Kind);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProjectSourceExtension, source.Extension);
        Assert.Equal(CapabilityAvailability.Usable, source.Availability);
    }

    [Fact]
    public async Task Fake_profile_registration_returns_error_when_profile_registry_write_fails()
    {
        var timeProvider = FakeTime.Create();
        var plugin = new FakeAccessProfilePlugin(
            FakeAccessProfilePlugin.CreateDefaultInfo(),
            (_, _) => FakeAccessProfileFactory.CreateServices(timeProvider),
            (_, _) => Task.FromResult(FakeAccessProfilePlugin.CreateDefaultRegistrationResult()),
            new ThrowingFakeProfileRegistryStore());
        var context = new AccessProfileContext(
            NullLoggerFactory.Instance,
            timeProvider,
            CreateTempDirectory(),
            CreateTempDirectory(),
            TestOperationalLogging.Options);

        plugin.Initialize(context);
        var result = await plugin.RegisterAsync(CancellationToken.None);

        Assert.Equal(AccessProfileRegistrationState.Error, result.State);
        Assert.Contains("could not be written", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fake_profile_project_source_evaluation_returns_supported_from_profile_registry()
    {
        var timeProvider = FakeTime.Create();
        var profileRoot = CreateTempDirectory();
        var store = new FileFakeProfileRegistryStore(new FixedFakeProfileRegistryPathResolver(profileRoot));
        var plugin = FakeAccessProfileFactory.CreatePlugin(timeProvider, profileRegistryStore: store);
        await RegisterFakeProfileAsync(plugin, timeProvider, profileRoot);

        var result = plugin.EvaluateProjectSource("sample.json");

        Assert.Equal(AccessProfileProjectSourceEvaluationState.Supported, result.State);
    }

    [Fact]
    public async Task Fake_profile_project_source_evaluation_returns_unsupported_for_non_matching_project_source()
    {
        var timeProvider = FakeTime.Create();
        var profileRoot = CreateTempDirectory();
        var store = new FileFakeProfileRegistryStore(new FixedFakeProfileRegistryPathResolver(profileRoot));
        var plugin = FakeAccessProfileFactory.CreatePlugin(timeProvider, profileRegistryStore: store);
        await RegisterFakeProfileAsync(plugin, timeProvider, profileRoot);

        var result = plugin.EvaluateProjectSource("sample.ap21");

        Assert.Equal(AccessProfileProjectSourceEvaluationState.Unsupported, result.State);
    }

    [Fact]
    public void Fake_profile_project_source_evaluation_returns_error_when_registry_is_missing()
    {
        var timeProvider = FakeTime.Create();
        var profileRoot = CreateTempDirectory();
        var store = new FileFakeProfileRegistryStore(new FixedFakeProfileRegistryPathResolver(profileRoot));
        var plugin = FakeAccessProfileFactory.CreatePlugin(timeProvider, profileRegistryStore: store);

        plugin.Initialize(CreateContext(timeProvider, profileRoot));

        var result = plugin.EvaluateProjectSource("sample.json");

        Assert.Equal(AccessProfileProjectSourceEvaluationState.Error, result.State);
    }

    [Fact]
    public void Fake_profile_project_source_evaluation_returns_error_when_registry_is_corrupt()
    {
        var timeProvider = FakeTime.Create();
        var profileRoot = CreateTempDirectory();
        var store = new FileFakeProfileRegistryStore(new FixedFakeProfileRegistryPathResolver(profileRoot));
        var plugin = FakeAccessProfileFactory.CreatePlugin(timeProvider, profileRegistryStore: store);
        var registryPath = Path.Combine(profileRoot, "profile-registry.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        File.WriteAllText(registryPath, "{");

        plugin.Initialize(CreateContext(timeProvider, profileRoot));

        var result = plugin.EvaluateProjectSource("sample.json");

        Assert.Equal(AccessProfileProjectSourceEvaluationState.Error, result.State);
    }

    private static CommunicationPrepareRequest CreateCommunicationPrepareRequest()
    {
        var project = new PlcProjectContext(new ProjectContextId("fake-project"), "Fake PLC Project");
        var runtime = new PlcRuntimeContext(new RuntimeContextId("fake-runtime"), "Fake PLC Runtime");
        var projectTarget = new ProjectTargetRef(new ProjectTargetId("fake-project-target"), "Fake PLC", "CPU 1516");
        var runtimeTarget = new RuntimeTargetRef(new RuntimeTargetId("fake-runtime-target"), "Fake Runtime", "192.0.2.10");

        return new CommunicationPrepareRequest(
            FakeAccessProfileFactory.CreateDefaultConfig(),
            new ProjectExecutionContext(ProjectOwnershipMode.ApplicationManaged, project, projectTarget),
            runtime,
            runtimeTarget);
    }

    private static async Task RegisterFakeProfileAsync(
        FakeAccessProfilePlugin plugin,
        TimeProvider timeProvider,
        string profileRoot)
    {
        plugin.Initialize(CreateContext(timeProvider, profileRoot));
        var result = await plugin.RegisterAsync(CancellationToken.None);
        Assert.Equal(AccessProfileRegistrationState.Registered, result.State);
    }

    private static AccessProfileContext CreateContext(
        TimeProvider timeProvider,
        string profileRoot)
    {
        return new AccessProfileContext(
            NullLoggerFactory.Instance,
            timeProvider,
            profileRoot,
            CreateTempDirectory(),
            TestOperationalLogging.Options);
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.FakeProfileRegistryTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class ThrowingFakeProfileRegistryStore : IFakeProfileRegistryStore
    {
        public string GetRegistryPath(AccessProfileContext context)
        {
            return Path.Combine(context.ProfileDataDirectory, "profile-registry.json");
        }

        public Task SaveAsync(
            AccessProfileContext context,
            FakeProfileRegistry registry,
            CancellationToken cancellationToken)
        {
            throw new IOException("Write denied.");
        }

        public Task<FakeProfileRegistry> LoadAsync(
            AccessProfileContext context,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public FakeProfileRegistry Load(AccessProfileContext context)
        {
            throw new NotSupportedException();
        }
    }
}
