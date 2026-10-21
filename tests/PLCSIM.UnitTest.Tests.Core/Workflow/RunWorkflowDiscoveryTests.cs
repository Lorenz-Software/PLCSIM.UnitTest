using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

public sealed class RunWorkflowDiscoveryTests
{
    [Fact]
    public async Task Loaded_profile_enables_project_and_runtime_target_discovery_publication()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var projectService = (FakePlcProjectService)services.Project;
        var controlService = (FakePlcControlService)services.PlcControl;
        var workflow = CreateWorkflow(timeProvider, services);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();

        var load = await RunWorkflowProfileTestHelper.LoadAndBindAsync(workflow, config, services, timeProvider);
        var projectDiscovery = await workflow.DiscoverProjectTargetsAsync(config, CreatePolicy(), CancellationToken.None);
        var runtimeDiscovery = await workflow.DiscoverRuntimeTargetsAsync(config, CreatePolicy(), CancellationToken.None);

        Assert.True(load.Result.IsSuccessful);
        Assert.True(projectDiscovery.Result.IsSuccessful);
        Assert.True(runtimeDiscovery.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.DiscoveryReady, workflow.Current.State);
        Assert.Equal(projectService.DefaultProjectTarget, Assert.Single(workflow.Current.ProjectTargets!.Candidates));
        Assert.Equal(controlService.DefaultRuntimeTarget, Assert.Single(workflow.Current.RuntimeTargets!.Candidates));
    }

    [Fact]
    public async Task Project_discovery_publishes_multiple_candidates_without_selecting_for_the_application()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var projectService = (FakePlcProjectService)services.Project;
        var candidates = new[]
        {
            projectService.DefaultProjectTarget,
            new ProjectTargetRef(new ProjectTargetId("fake-project-target-2"), "Fake PLC 2", "FakeProjectTarget2")
        };
        projectService.DiscoverProjectTargetsResult =
            OperationResult<ProjectTargetDiscoveryResult>.Success(
                new ProjectTargetDiscoveryResult(
                    candidates,
                    TargetDiscoveryState.CandidatesAvailable,
                    []));
        var workflow = CreateWorkflow(timeProvider, services);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();

        await RunWorkflowProfileTestHelper.LoadAndBindAsync(workflow, config, services, timeProvider);
        var discovery = await workflow.DiscoverProjectTargetsAsync(config, CreatePolicy(), CancellationToken.None);

        Assert.True(discovery.Result.IsSuccessful);
        Assert.Equal(candidates, workflow.Current.ProjectTargets!.Candidates);
        Assert.False(workflow.Current.Status.HasProjectTarget);
    }

    [Fact]
    public async Task Bound_profile_session_enables_project_and_runtime_target_discovery_publication()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var projectService = (FakePlcProjectService)services.Project;
        var controlService = (FakePlcControlService)services.PlcControl;
        var workflow = CreateWorkflow(timeProvider, services);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        var session = await CreateSessionAsync(config, services, timeProvider);

        var bind = await workflow.BindAccessProfileSessionAsync(session, CreatePolicy(), CancellationToken.None);
        var projectDiscovery = await workflow.DiscoverProjectTargetsAsync(config, CreatePolicy(), CancellationToken.None);
        var runtimeDiscovery = await workflow.DiscoverRuntimeTargetsAsync(config, CreatePolicy(), CancellationToken.None);

        Assert.True(bind.Result.IsSuccessful);
        Assert.True(projectDiscovery.Result.IsSuccessful);
        Assert.True(runtimeDiscovery.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.DiscoveryReady, workflow.Current.State);
        Assert.Equal(config.ProfileId, workflow.Current.Status.AccessProfileId);
        Assert.Equal(config.ProfileVersion, workflow.Current.Status.AccessProfileVersion);
        Assert.Equal(projectService.DefaultProjectTarget, Assert.Single(workflow.Current.ProjectTargets!.Candidates));
        Assert.Equal(controlService.DefaultRuntimeTarget, Assert.Single(workflow.Current.RuntimeTargets!.Candidates));
    }

    [Fact]
    public async Task Unload_after_external_session_bind_unbinds_without_disposing_session_owner()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var workflow = CreateWorkflow(timeProvider, services);
        var session = await CreateSessionAsync(FakeAccessProfileFactory.CreateDefaultConfig(), services, timeProvider);
        var disposed = false;
        session.Profile.StateChangedEventHandler += (_, args) =>
        {
            if (args.State == AccessProfileState.Disposed)
            {
                disposed = true;
            }
        };

        await workflow.BindAccessProfileSessionAsync(session, CreatePolicy(), CancellationToken.None);
        var unload = await workflow.UnloadAccessProfileAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(unload.Result.IsSuccessful);
        Assert.False(disposed);
        Assert.Equal(RunWorkflowState.NotConfigured, workflow.Current.State);
        Assert.False(workflow.Current.Status.HasAccessProfile);
    }

    [Fact]
    public async Task Discovery_result_diagnostics_are_forwarded_to_workflow_publication()
    {
        var timeProvider = FakeTime.Create();
        var diagnostic = FakeDiagnostics.ConfigurationError(
            "Fake.Discovery.Project.Warning",
            "Fake discovery diagnostic.",
            timeProvider.GetUtcNow());
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var projectService = (FakePlcProjectService)services.Project;
        projectService.DiscoverProjectTargetsResult =
            OperationResult<ProjectTargetDiscoveryResult>.Success(
                new ProjectTargetDiscoveryResult(
                    [projectService.DefaultProjectTarget],
                    TargetDiscoveryState.CandidatesAvailable,
                    [diagnostic]));
        var workflow = CreateWorkflow(timeProvider, services);

        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(
            workflow,
            config,
            services,
            timeProvider);
        await workflow.DiscoverProjectTargetsAsync(config, CreatePolicy(), CancellationToken.None);

        Assert.Contains(workflow.Current.Diagnostics, entry => entry.Code == "Fake.Discovery.Project.Warning");
    }

    [Fact]
    public async Task Project_access_opened_during_discovery_is_published_before_discovery_completes()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var projectService = (FakePlcProjectService)services.Project;
        projectService.ProjectContextOpenedDuringDiscovery = new PlcProjectContext(
            new ProjectContextId("discovery-project"),
            "Discovery Project",
            "discovery-project.ap");
        var workflow = CreateWorkflow(timeProvider, services);
        var publications = new List<RunWorkflowPublication>();
        workflow.PublicationEventHandler += (_, args) => publications.Add(args.Publication);

        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(
            workflow,
            config,
            services,
            timeProvider);
        var discovery = await workflow.DiscoverProjectTargetsAsync(config, CreatePolicy(), CancellationToken.None);

        Assert.True(discovery.Result.IsSuccessful);
        Assert.Contains(
            publications,
            publication => publication.ActiveOperation?.Action == WorkflowAction.DiscoverProjectTargets
                && publication.Status.ProjectAccessActive);
        Assert.True(workflow.Current.Status.ProjectAccessActive);
    }

    [Fact]
    public async Task Managed_discovery_flow_releases_and_can_reopen_project_access_after_transfer()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var controlService = (FakePlcControlService)services.PlcControl;
        var workflow = CreateWorkflow(timeProvider, services);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();

        await RunWorkflowProfileTestHelper.LoadAndBindAsync(workflow, config, services, timeProvider);
        await workflow.DiscoverProjectTargetsAsync(config, CreatePolicy(), CancellationToken.None);
        await workflow.DiscoverRuntimeTargetsAsync(config, CreatePolicy(), CancellationToken.None);
        var setup = CreateRunSetup(
            config,
            Assert.Single(workflow.Current.ProjectTargets!.Candidates),
            Assert.Single(workflow.Current.RuntimeTargets!.Candidates));

        var configure = workflow.Configure(setup);
        var projectPrepare = await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        var runtimePrepare = await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);
        var transfer = await workflow.TransferProjectAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(configure.IsSuccessful);
        Assert.True(projectPrepare.Result.IsSuccessful);
        Assert.True(runtimePrepare.Result.IsSuccessful);
        Assert.True(transfer.Result.IsSuccessful);
        Assert.Equal(setup.RuntimeTarget, controlService.LastPrepareRuntimeRequest?.RuntimeTarget);
        Assert.Equal(ProjectSetupState.ManagedTransferCompleted, workflow.Current.Status.ProjectSetupState);
        Assert.True(workflow.Current.Status.ProjectSetupSatisfied);
        Assert.False(workflow.Current.Status.ProjectAccessActive);
        Assert.Null(services.Project.CurrentProject);
        Assert.True(workflow.Current.Status.RuntimePrepared);
        Assert.Equal(RunWorkflowState.ProjectTransferred, workflow.Current.State);
        Assert.True(workflow.Current.ActionGates.Single(gate => gate.Action == WorkflowAction.StartPlc).IsAvailable);

        var reopen = await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        Assert.True(reopen.Result.IsSuccessful);
        Assert.True(workflow.Current.Status.ProjectAccessActive);
        Assert.Equal(ProjectSetupState.ManagedProjectPrepared, workflow.Current.Status.ProjectSetupState);

        var repeatedTransfer = await workflow.TransferProjectAsync(CreatePolicy(), CancellationToken.None);
        Assert.True(repeatedTransfer.Result.IsSuccessful);
        Assert.False(workflow.Current.Status.ProjectAccessActive);
        Assert.Equal(ProjectSetupState.ManagedTransferCompleted, workflow.Current.Status.ProjectSetupState);
    }

    [Fact]
    public async Task Configure_does_not_prepare_output_directory()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "artifacts");
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var workflow = CreateWorkflow(timeProvider, services);
        var config = FakeAccessProfileFactory.CreateDefaultConfig();

        try
        {
            await RunWorkflowProfileTestHelper.LoadAndBindAsync(workflow, config, services, timeProvider);
            await workflow.DiscoverProjectTargetsAsync(config, CreatePolicy(), CancellationToken.None);
            await workflow.DiscoverRuntimeTargetsAsync(config, CreatePolicy(), CancellationToken.None);
            var setup = CreateRunSetup(
                config,
                Assert.Single(workflow.Current.ProjectTargets!.Candidates),
                Assert.Single(workflow.Current.RuntimeTargets!.Candidates),
                new OutputPolicy(outputDirectory, TestResult: true));

            var configure = workflow.Configure(setup);

            Assert.True(configure.IsSuccessful);
            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(Path.GetDirectoryName(outputDirectory)!, recursive: true);
            }
        }
    }

    private static RunWorkflowService CreateWorkflow(
        TimeProvider timeProvider,
        FakeAccessProfileServices services)
    {
        return new RunWorkflowService(
            new RunSetupValidator(timeProvider),
            TestCoreServiceContext.Create(timeProvider: timeProvider));
    }

    private static async Task<AccessProfileSession> CreateSessionAsync(
        AccessProfileConfig config,
        FakeAccessProfileServices services,
        TimeProvider timeProvider)
    {
        var plugin = new FakeAccessProfilePlugin((_, _) => services);
        plugin.Initialize(new AccessProfileContext(
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            timeProvider,
            CreateTempProfileRoot(),
            CreateTempProfileRoot(),
            TestOperationalLogging.Options));
        var request = new AccessProfileSessionRequest(
            config.ProfileId,
            config.ProfileVersion,
            config.ProjectSource,
            ProjectOwnershipMode.ApplicationManaged);
        var load = await plugin.LoadAsync(request, CancellationToken.None);
        return new AccessProfileSession(
            request,
            plugin.Info,
            load.Session!);
    }

    private static string CreateTempProfileRoot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "plcsim-unittest-workflow-profile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static WorkflowActionPolicy CreatePolicy()
    {
        return new WorkflowActionPolicy(TimeSpan.FromMinutes(1));
    }

    private static RunSetup CreateRunSetup(
        AccessProfileConfig config,
        ProjectTargetRef projectTarget,
        RuntimeTargetRef runtimeTarget,
        OutputPolicy? outputPolicy = null)
    {
        return new RunSetup(
            config,
            RuntimeProvisioningMode.UseExisting,
            outputPolicy ?? RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()),
            projectTarget,
            runtimeTarget);
    }
}
