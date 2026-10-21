using Microsoft.Extensions.Logging.Abstractions;
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

public sealed class RunWorkflowDiagnosticAndCleanupTests
{
    [Fact]
    public async Task Service_diagnostics_are_forwarded_to_workflow_publication()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var workflow = CreateWorkflow(timeProvider, services);
        await LoadAndConfigureAsync(workflow, services, timeProvider);

        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        ((FakePlcProjectService)services.Project).ReportDiagnostic(
            FakeDiagnostics.ConfigurationError("Fake.Project.Warning", "Fake project diagnostic.", FakeTime.DefaultStart));

        Assert.Contains(workflow.Current.Diagnostics, diagnostic => diagnostic.Code == "Fake.Project.Warning");
    }

    [Fact]
    public async Task Repeated_service_diagnostics_are_coalesced_in_workflow_publication()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var workflow = CreateWorkflow(timeProvider, services);
        await LoadAndConfigureAsync(workflow, services, timeProvider);

        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        ((FakePlcProjectService)services.Project).ReportDiagnostic(
            FakeDiagnostics.ConfigurationError("Fake.Project.Warning", "First fake project diagnostic.", FakeTime.DefaultStart));
        ((FakePlcProjectService)services.Project).ReportDiagnostic(
            FakeDiagnostics.ConfigurationError("Fake.Project.Warning", "Second fake project diagnostic.", FakeTime.DefaultStart.AddSeconds(1)));

        var entry = Assert.Single(workflow.Current.DiagnosticSnapshot.Entries);
        Assert.Single(workflow.Current.Diagnostics);
        Assert.Equal(2, entry.OccurrenceCount);
        Assert.Equal("Second fake project diagnostic.", entry.Current.Message);
    }

    [Fact]
    public async Task Clear_diagnostics_clears_sink_without_changing_workflow_state_or_gates()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var workflow = CreateWorkflow(timeProvider, services);
        await LoadAndConfigureAsync(workflow, services, timeProvider);
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        ((FakePlcProjectService)services.Project).ReportDiagnostic(
            FakeDiagnostics.ConfigurationError("Fake.Project.Warning", "Fake project diagnostic.", FakeTime.DefaultStart));

        var stateBeforeClear = workflow.Current.State;
        var gatesBeforeClear = workflow.Current.ActionGates
            .Select(gate => new
            {
                gate.Action,
                gate.Availability,
                Reasons = string.Join(",", gate.Reasons.Select(reason => reason.Code))
            })
            .ToArray();
        Assert.NotEmpty(workflow.Current.Diagnostics);

        workflow.ClearDiagnostics();

        Assert.Equal(stateBeforeClear, workflow.Current.State);
        Assert.Empty(workflow.Current.Diagnostics);
        Assert.Empty(workflow.Current.DiagnosticSnapshot.Entries);
        Assert.Equal(
            gatesBeforeClear,
            workflow.Current.ActionGates.Select(gate => new
            {
                gate.Action,
                gate.Availability,
                Reasons = string.Join(",", gate.Reasons.Select(reason => reason.Code))
            }));
    }

    [Fact]
    public async Task Clear_diagnostics_allows_same_finding_to_repopulate_as_fresh_entry()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var workflow = CreateWorkflow(timeProvider, services);
        await LoadAndConfigureAsync(workflow, services, timeProvider);
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);

        ((FakePlcProjectService)services.Project).ReportDiagnostic(
            FakeDiagnostics.ConfigurationError("Fake.Project.Warning", "First fake project diagnostic.", timeProvider.GetUtcNow()));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        ((FakePlcProjectService)services.Project).ReportDiagnostic(
            FakeDiagnostics.ConfigurationError("Fake.Project.Warning", "Repeated fake project diagnostic.", timeProvider.GetUtcNow()));

        var repeatedEntry = Assert.Single(workflow.Current.DiagnosticSnapshot.Entries);
        Assert.Equal(2, repeatedEntry.OccurrenceCount);

        workflow.ClearDiagnostics();
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        ((FakePlcProjectService)services.Project).ReportDiagnostic(
            FakeDiagnostics.ConfigurationError("Fake.Project.Warning", "Repopulated fake project diagnostic.", timeProvider.GetUtcNow()));

        var repopulatedEntry = Assert.Single(workflow.Current.DiagnosticSnapshot.Entries);
        Assert.Single(workflow.Current.Diagnostics);
        Assert.Equal(1, repopulatedEntry.OccurrenceCount);
        Assert.Equal("Repopulated fake project diagnostic.", repopulatedEntry.Current.Message);
        Assert.Equal(timeProvider.GetUtcNow(), repopulatedEntry.FirstSeenAt);
        Assert.Equal(timeProvider.GetUtcNow(), repopulatedEntry.LastSeenAt);
    }

    [Fact]
    public async Task Cleanup_without_loaded_profile_is_available_and_idempotent()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        var workflow = CreateWorkflow(timeProvider, services);
        var cleanupGate = workflow.Current.ActionGates.Single(gate => gate.Action == WorkflowAction.Cleanup);

        var cleanup = await workflow.CleanupAsync(CreatePolicy(), CancellationToken.None);

        Assert.True(cleanupGate.IsAvailable);
        Assert.True(cleanup.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.NotConfigured, workflow.Current.State);
        Assert.False(workflow.Current.Status.HasAccessProfile);
        Assert.Empty(workflow.Current.Diagnostics);
    }

    [Fact]
    public async Task Cleanup_reports_step_diagnostics_but_resets_workflow_context()
    {
        var timeProvider = FakeTime.Create();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider) with
        {
            UnitTestEventStream = new CleanupFailingEventStreamService()
        };
        var workflow = CreateWorkflow(timeProvider, services);
        await LoadAndConfigureAsync(workflow, services, timeProvider);

        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        var cleanup = await workflow.CleanupAsync(CreatePolicy(), CancellationToken.None);

        Assert.False(cleanup.Result.IsSuccessful);
        Assert.Equal(RunWorkflowState.NotConfigured, workflow.Current.State);
        Assert.Contains(workflow.Current.Diagnostics, diagnostic => diagnostic.Code == "Workflow.Cleanup.EventStream.Failed");
    }

    [Fact]
    public async Task Cleanup_timeout_still_detaches_all_workflow_context()
    {
        var timeProvider = FakeTime.Create();
        var eventStream = new CancellationHoldingEventStreamService();
        var services = FakeAccessProfileFactory.CreateServices(timeProvider) with
        {
            UnitTestEventStream = eventStream
        };
        var workflow = CreateWorkflow(timeProvider, services);
        await LoadAndConfigureAsync(workflow, services, timeProvider);
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);

        var cleanupTask = workflow.CleanupAsync(
            new WorkflowActionPolicy(TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        await eventStream.WaitUntilCleanupStartedAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var cleanup = await cleanupTask;

        Assert.Equal(OperationResultState.TimedOut, cleanup.Result.State);
        Assert.Equal(RunWorkflowState.NotConfigured, workflow.Current.State);
        Assert.False(workflow.Current.Status.HasAccessProfile);
        Assert.False(workflow.Current.Status.HasRunSetup);
        Assert.False(workflow.Current.Status.ProjectPrepared);
        Assert.False(workflow.Current.Status.RuntimePrepared);
        Assert.False(workflow.Current.Status.UnitTestCommunicationPrepared);
        Assert.False(workflow.Current.Status.UnitTestCommunicationContextActive);
        Assert.False(workflow.Current.Status.MonitoringActive);
    }

    [Fact]
    public async Task Post_transfer_project_release_uses_its_own_deadline()
    {
        var timeProvider = FakeTime.Create();
        var releaseStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var project = new FakePlcProjectService(timeProvider)
        {
            ReleaseProjectHandler = async cancellationToken =>
            {
                releaseStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return OperationResult.Success();
            }
        };
        var services = FakeAccessProfileFactory.CreateServices(timeProvider) with
        {
            Project = project
        };
        var workflow = CreateWorkflow(timeProvider, services);
        await LoadAndConfigureAsync(workflow, services, timeProvider);
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);

        var transferTask = workflow.TransferProjectAsync(
            new WorkflowActionPolicy(TimeSpan.FromMinutes(1)),
            new WorkflowActionPolicy(TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        await releaseStarted.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var transfer = await transferTask;

        Assert.True(transfer.Result.IsSuccessful);
        Assert.Equal(1, project.ReleaseProjectCallCount);
        Assert.True(workflow.Current.Status.ProjectAccessActive);
        Assert.Contains(
            workflow.Current.Diagnostics,
            diagnostic => diagnostic.Code == "Workflow.Project.PostTransferRelease.TimedOut");
    }

    private static RunWorkflowService CreateWorkflow(
        TimeProvider timeProvider,
        FakeAccessProfileServices services)
    {
        return new RunWorkflowService(
            new RunSetupValidator(timeProvider),
            TestCoreServiceContext.Create(timeProvider: timeProvider));
    }

    private static RunSetup CreateRunSetup()
    {
        return new RunSetup(
            FakeAccessProfileFactory.CreateDefaultConfig(),
            RuntimeProvisioningMode.UseExisting,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()),
            new ProjectTargetRef(new ProjectTargetId("fake-project-target"), "Fake PLC Project Target", "FakeProjectTarget"),
            new RuntimeTargetRef(new RuntimeTargetId("fake-runtime-target"), "Fake PLC Runtime Target", "FakeRuntimeTarget"));
    }

    private static WorkflowActionPolicy CreatePolicy()
    {
        return new WorkflowActionPolicy(TimeSpan.FromMinutes(1));
    }

    private static async Task LoadAndConfigureAsync(
        RunWorkflowService workflow,
        FakeAccessProfileServices services,
        TimeProvider timeProvider)
    {
        var setup = CreateRunSetup();
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(workflow, setup.AccessProfile, services, timeProvider);
        workflow.Configure(setup);
    }

    private sealed class CleanupFailingEventStreamService : FakeUnitTestEventStreamService
    {
        public override Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(OperationResult.Failure("Fake event-stream cleanup failed."));
        }
    }

    private sealed class CancellationHoldingEventStreamService : FakeUnitTestEventStreamService
    {
        private readonly TaskCompletionSource cleanupStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
        {
            cleanupStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return OperationResult.Success();
        }

        public Task WaitUntilCleanupStartedAsync()
        {
            return cleanupStarted.Task;
        }
    }
}
