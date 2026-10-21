using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

public sealed class RunWorkflowArtifactExportTests
{
    [Fact]
    public async Task Completed_run_with_complete_evidence_is_exportable()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var workflow = CreateWorkflow(services);

        await CompleteRunAsync(workflow, services);

        Assert.True(workflow.Current.Status.IsExportable);
        Assert.Equal(TestRunMonitoringState.Completed, workflow.Current.MonitorState);
        Assert.Equal(UnitTestRunState.Completed, workflow.Current.CurrentRun?.State);
        Assert.Equal(EvidenceState.Complete, workflow.Current.CurrentRun?.EvidenceState);
    }

    [Fact]
    public async Task Completed_run_with_incomplete_evidence_is_not_exportable()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var workflow = CreateWorkflow(services);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);

        await PrepareAndStartRunAsync(workflow, services);
        lifecycle.CompleteRun(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompletionAnchor(lostEventCount: 1);

        Assert.Equal(TestRunMonitoringState.Completed, workflow.Current.MonitorState);
        Assert.Equal(EvidenceState.Incomplete, workflow.Current.CurrentRun?.EvidenceState);
        Assert.False(workflow.Current.Status.IsExportable);
    }

    [Fact]
    public async Task Aborted_run_is_not_exportable()
    {
        var services = FakeAccessProfileFactory.CreateServices(FakeTime.Create());
        var workflow = CreateWorkflow(services);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);

        await PrepareAndStartRunAsync(workflow, services);
        await workflow.AbortUnitTestAsync(CreatePolicy(), CancellationToken.None);
        lifecycle.CompleteAbort();
        eventStream.PublishRunControlAbortCompleted();

        Assert.Equal(TestRunMonitoringState.Aborted, workflow.Current.MonitorState);
        Assert.Equal(UnitTestRunState.Aborted, workflow.Current.CurrentRun?.State);
        Assert.False(workflow.Current.Status.IsExportable);
        _ = lifecycle;
    }

    private static RunWorkflowService CreateWorkflow(FakeAccessProfileServices services)
    {
        var timeProvider = FakeTime.Create();
        return new RunWorkflowService(
            new RunSetupValidator(timeProvider),
            TestCoreServiceContext.Create(timeProvider: timeProvider));
    }

    private static async Task CompleteRunAsync(
        RunWorkflowService workflow,
        FakeAccessProfileServices services)
    {
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        var eventStream = Assert.IsType<FakeUnitTestEventStreamService>(services.UnitTestEventStream);

        await PrepareAndStartRunAsync(workflow, services);
        lifecycle.CompleteRun(UnitTestResultState.Passed);
        eventStream.PublishRunControlCompleted(UnitTestResultState.Passed);

        Assert.Equal(RunWorkflowState.RunCompleted, workflow.Current.State);
    }

    private static async Task PrepareAndStartRunAsync(
        RunWorkflowService workflow,
        FakeAccessProfileServices services)
    {
        await RunWorkflowProfileTestHelper.LoadAndBindAsync(
            workflow,
            FakeAccessProfileFactory.CreateDefaultConfig(),
            services,
            FakeTime.Create());
        workflow.Configure(CreateRunSetup());
        await workflow.PrepareProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.PrepareRuntimeAsync(CreatePolicy(), CancellationToken.None);
        await workflow.TransferProjectAsync(CreatePolicy(), CancellationToken.None);
        await workflow.StartPlcAsync(CreatePolicy(), CancellationToken.None);
        await workflow.StartMonitoringAsync(CreatePolicy(), CancellationToken.None);
        await workflow.StartUnitTestAsync(CreatePolicy(), CancellationToken.None);
        Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle)
            .SimulateExternalRunStarted();
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
}
