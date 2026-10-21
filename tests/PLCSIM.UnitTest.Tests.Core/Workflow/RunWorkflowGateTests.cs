using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using PLCSIM.UnitTest.Tests.Core.TestLogging;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

public sealed class RunWorkflowGateTests
{
    [Fact]
    public void Initial_publication_blocks_prepare_without_configuration()
    {
        var workflow = CreateWorkflow();

        var prepareGate = workflow.Current.ActionGates.Single(gate => gate.Action == WorkflowAction.PrepareProject);
        var discoverGate = workflow.Current.ActionGates.Single(gate => gate.Action == WorkflowAction.DiscoverProjectTargets);

        Assert.Equal(RunWorkflowState.NotConfigured, workflow.Current.State);
        Assert.Equal(RunWorkflowState.NotConfigured, workflow.Current.Status.State);
        Assert.False(workflow.Current.Status.HasAccessProfile);
        Assert.False(workflow.Current.Status.HasRunSetup);
        Assert.Null(workflow.Current.Status.RuntimeProvisioningMode);
        Assert.False(workflow.Current.Status.ProjectPrepared);
        Assert.False(workflow.Current.Status.RuntimePrepared);
        Assert.False(workflow.Current.Status.PlcRunning);
        Assert.False(workflow.Current.Status.MonitoringActive);
        Assert.False(workflow.Current.Status.IsExportable);
        Assert.Equal(ActionGateAvailability.Blocked, prepareGate.Availability);
        Assert.Equal(ActionGateAvailability.Blocked, discoverGate.Availability);
        Assert.False(prepareGate.IsAvailable);
        Assert.False(discoverGate.IsAvailable);
        Assert.Contains(prepareGate.Reasons, reason => reason.Code == ActionGateReasonCode.NotConfigured);
        Assert.Contains(discoverGate.Reasons, reason => reason.Code == ActionGateReasonCode.AccessProfileNotLoaded);
    }

    [Fact]
    public async Task Load_and_configure_publish_configured_state_and_enable_prepare_project()
    {
        var workflow = CreateWorkflow();
        var config = FakeAccessProfileFactory.CreateDefaultConfig();

        var load = await RunWorkflowProfileTestHelper.LoadAndBindDefaultFakeAsync(workflow, config);
        var result = workflow.Configure(CreateRunSetup());
        var prepareGate = workflow.Current.ActionGates.Single(gate => gate.Action == WorkflowAction.PrepareProject);

        Assert.True(load.Result.IsSuccessful);
        Assert.True(result.IsSuccessful);
        Assert.Equal(RunWorkflowState.Configured, workflow.Current.State);
        Assert.Equal(RunWorkflowState.Configured, workflow.Current.Status.State);
        Assert.True(workflow.Current.Status.HasAccessProfile);
        Assert.Equal(config.ProfileId, workflow.Current.Status.AccessProfileId);
        Assert.True(workflow.Current.Status.HasRunSetup);
        Assert.Equal(RuntimeProvisioningMode.UseExisting, workflow.Current.Status.RuntimeProvisioningMode);
        Assert.True(workflow.Current.Status.HasProjectTarget);
        Assert.True(workflow.Current.Status.HasRuntimeTarget);
        Assert.False(workflow.Current.Status.ProjectPrepared);
        Assert.False(workflow.Current.Status.RuntimePrepared);
        Assert.Equal(ActionGateAvailability.Available, prepareGate.Availability);
        Assert.True(prepareGate.IsAvailable);
    }

    [Fact]
    public async Task Prepare_project_enables_prepare_runtime_and_keeps_later_actions_blocked()
    {
        var workflow = CreateWorkflow();
        await LoadAndConfigureAsync(workflow);

        var result = await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);

        var runtimeGate = workflow.Current.ActionGates.Single(gate => gate.Action == WorkflowAction.PrepareRuntime);
        var startUnitTestGate = workflow.Current.ActionGates.Single(gate => gate.Action == WorkflowAction.StartUnitTest);

        Assert.True(result.Result.IsSuccessful);
        Assert.Equal(ProjectSetupState.ManagedProjectPrepared, workflow.Current.Status.ProjectSetupState);
        Assert.True(workflow.Current.Status.ProjectAccessActive);
        Assert.True(workflow.Current.Status.ProjectPrepared);
        Assert.False(workflow.Current.Status.RuntimePrepared);
        Assert.False(workflow.Current.Status.ProjectTransferred);
        Assert.False(workflow.Current.Status.UnitTestCommunicationPrepared);
        Assert.Equal(ActionGateAvailability.Available, runtimeGate.Availability);
        Assert.Equal(ActionGateAvailability.Blocked, startUnitTestGate.Availability);
        Assert.True(runtimeGate.IsAvailable);
        Assert.False(startUnitTestGate.IsAvailable);
        Assert.Contains(startUnitTestGate.Reasons, reason => reason.Code == ActionGateReasonCode.RuntimeNotPrepared);
    }

    [Fact]
    public async Task Create_owned_runtime_is_prepared_without_a_preselected_target_and_publishes_the_effective_target()
    {
        var timeProvider = FakeTime.Create();
        var workflow = CreateWorkflow();
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(workflow, config, services, timeProvider);
        var setup = new RunSetup(
            config,
            RuntimeProvisioningMode.CreateOwned,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()),
            ProjectTarget: CreateProjectTarget(),
            RuntimeTarget: null);

        var configure = workflow.Configure(setup);
        var prepareProject = await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        var runtimeGate = workflow.Current.ActionGates.Single(gate => gate.Action == WorkflowAction.PrepareRuntime);
        var prepareRuntime = await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);
        var controlService = Assert.IsType<FakePlcControlService>(services.PlcControl);

        Assert.True(configure.IsSuccessful);
        Assert.True(prepareProject.Result.IsSuccessful);
        Assert.Equal(RuntimeProvisioningMode.CreateOwned, workflow.Current.Status.RuntimeProvisioningMode);
        Assert.Equal(ActionGateAvailability.Available, runtimeGate.Availability);
        Assert.True(prepareRuntime.Result.IsSuccessful);
        Assert.True(workflow.Current.Status.HasRuntimeTarget);
        Assert.Equal(RuntimeProvisioningMode.CreateOwned, controlService.LastPrepareRuntimeRequest?.ProvisioningMode);
        Assert.Null(controlService.LastPrepareRuntimeRequest?.RuntimeTarget);
    }

    [Fact]
    public async Task External_project_setup_requires_only_a_runtime_target()
    {
        var workflow = CreateWorkflow();
        var config = FakeAccessProfileFactory.CreateDefaultConfig() with { ProjectSource = "" };
        await RunWorkflowProfileTestHelper.LoadAndBindDefaultFakeAsync(
            workflow,
            config,
            ProjectOwnershipMode.ExternallyManaged);
        var setup = new RunSetup(
            config,
            RuntimeProvisioningMode.UseExisting,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()),
            ProjectTarget: null,
            RuntimeTarget: CreateRuntimeTarget());

        var configure = workflow.Configure(setup);
        var prepareRuntime = await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(configure.IsSuccessful);
        Assert.True(prepareRuntime.Result.IsSuccessful);
        Assert.Equal(ProjectOwnershipMode.ExternallyManaged, workflow.Current.Status.ProjectOwnershipMode);
        Assert.Equal(ProjectSetupState.ExternalManagementAccepted, workflow.Current.Status.ProjectSetupState);
        Assert.True(workflow.Current.Status.ProjectSetupSatisfied);
        Assert.False(workflow.Current.Status.HasProjectTarget);
        Assert.True(workflow.Current.Status.RuntimePrepared);
        Assert.Equal(
            ActionGateAvailability.NotApplicable,
            workflow.Current.ActionGates.Single(gate => gate.Action == WorkflowAction.PrepareProject).Availability);
        Assert.Equal(
            ActionGateAvailability.NotApplicable,
            workflow.Current.ActionGates.Single(gate => gate.Action == WorkflowAction.TransferProject).Availability);
    }

    [Fact]
    public async Task Configure_requires_final_project_target_selection_for_managed_projects()
    {
        var workflow = CreateWorkflow();
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindDefaultFakeAsync(workflow, config);
        var incompleteSetup = new RunSetup(
            config,
            RuntimeProvisioningMode.UseExisting,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()),
            ProjectTarget: null,
            RuntimeTarget: CreateRuntimeTarget());

        var result = workflow.Configure(incompleteSetup);

        Assert.False(result.IsSuccessful);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Contains(workflow.Current.Diagnostics, diagnostic => diagnostic.Code == "Workflow.Configure.ProjectTargetMissing");
    }

    [Fact]
    public async Task Configure_requires_a_runtime_target_for_existing_runtime_provisioning()
    {
        var workflow = CreateWorkflow();
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindDefaultFakeAsync(workflow, config);
        var incompleteSetup = new RunSetup(
            config,
            RuntimeProvisioningMode.UseExisting,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()),
            ProjectTarget: CreateProjectTarget(),
            RuntimeTarget: null);

        var result = workflow.Configure(incompleteSetup);

        Assert.False(result.IsSuccessful);
        Assert.Equal(RunWorkflowState.Error, workflow.Current.State);
        Assert.Contains(workflow.Current.Diagnostics, diagnostic => diagnostic.Code == "RunSetup.RuntimeTarget.Missing");
    }

    [Fact]
    public void Configure_logs_setup_validation_diagnostic_details()
    {
        var loggerFactory = new ListLoggerFactory<RunWorkflowService>();
        var workflow = CreateWorkflow(loggerFactory);
        var invalidSetup = new RunSetup(
            new AccessProfileConfig(
                "",
                null,
                AccessProfileConfigurationPayload.Empty,
                ""),
            RuntimeProvisioningMode.UseExisting,
            new OutputPolicy("", TestResult: true),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()));

        var result = workflow.Configure(invalidSetup);

        Assert.False(result.IsSuccessful);
        var warning = Assert.Single(loggerFactory.Logger.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && entry.Message.Contains("Run workflow configuration failed", StringComparison.Ordinal));
        Assert.Contains("RunSetup.AccessProfileId.Missing", warning.Message, StringComparison.Ordinal);
        Assert.Contains("RunSetup.ProjectSource.Missing", warning.Message, StringComparison.Ordinal);
        Assert.Contains("RunSetup.OutputDirectory.Missing", warning.Message, StringComparison.Ordinal);
    }

    private static RunWorkflowService CreateWorkflow(ListLoggerFactory<RunWorkflowService>? loggerFactory = null)
    {
        var timeProvider = FakeTime.Create();
        return new RunWorkflowService(
            new RunSetupValidator(timeProvider),
            TestCoreServiceContext.Create(timeProvider: timeProvider, loggerFactory: loggerFactory));
    }

    private static RunSetup CreateRunSetup()
    {
        return new RunSetup(
            FakeAccessProfileFactory.CreateDefaultConfig(),
            RuntimeProvisioningMode.UseExisting,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()),
            CreateProjectTarget(),
            CreateRuntimeTarget());
    }

    private static WorkflowActionPolicy CreatePolicy()
    {
        return new WorkflowActionPolicy(TimeSpan.FromMinutes(1));
    }

    private static async Task LoadAndConfigureAsync(RunWorkflowService workflow)
    {
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        await RunWorkflowProfileTestHelper.LoadAndBindDefaultFakeAsync(workflow, config);
        workflow.Configure(CreateRunSetup());
    }

    private static ProjectTargetRef CreateProjectTarget()
    {
        return new ProjectTargetRef(new ProjectTargetId("fake-project-target"), "Fake PLC Project Target", "FakeProjectTarget");
    }

    private static RuntimeTargetRef CreateRuntimeTarget()
    {
        return new RuntimeTargetRef(new RuntimeTargetId("fake-runtime-target"), "Fake PLC Runtime Target", "FakeRuntimeTarget");
    }
}
