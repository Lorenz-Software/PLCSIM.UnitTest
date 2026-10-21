using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

public sealed class RunSessionReleaseServiceTests
{
    [Fact]
    public async Task ReleaseAsync_without_loaded_session_is_successful_and_idempotent()
    {
        var fixture = CreateFixture();

        var first = await fixture.Release.ReleaseAsync(
            CreatePolicy(),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        var second = await fixture.Release.ReleaseAsync(
            CreatePolicy(),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.True(first.IsSuccessful);
        Assert.True(second.IsSuccessful);
        Assert.Equal(RunWorkflowState.NotConfigured, fixture.Workflow.Current.State);
        Assert.Null(fixture.Sessions.CurrentSession);
    }

    [Fact]
    public async Task ReleaseAsync_unloads_profile_after_workflow_cleanup_is_cancelled()
    {
        var cleanup = new CancellationHoldingEventStreamService();
        var fixture = CreateFixture(cleanup);
        await LoadAndBindAsync(fixture);
        using var cancellation = new CancellationTokenSource();

        var releaseTask = fixture.Release.ReleaseAsync(
            CreatePolicy(),
            TimeSpan.FromSeconds(30),
            cancellation.Token);
        await cleanup.WaitUntilCleanupStartedAsync();
        cancellation.Cancel();
        var result = await releaseTask;

        Assert.Equal(OperationResultState.Cancelled, result.WorkflowCleanup.State);
        Assert.True(result.ProfileUnload.IsSuccessful);
        Assert.Null(fixture.Sessions.CurrentSession);
        Assert.Equal(RunWorkflowState.NotConfigured, fixture.Workflow.Current.State);
    }

    [Fact]
    public async Task Scoped_release_blocks_new_workflow_work_but_allows_release_actions()
    {
        var fixture = CreateFixture();
        await LoadAndBindAsync(fixture);
        fixture.Workflow.Configure(CreateRunSetup());

        var quiesce = await fixture.Release.BeginScopedReleaseAsync(
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        try
        {
            var blockedPrepare = await fixture.Workflow.PrepareProjectAsync(
                CreatePolicy(),
                CancellationToken.None);
            var projectRelease = await fixture.Workflow.ReleaseProjectAsync(
                CreatePolicy(),
                CancellationToken.None);

            Assert.True(quiesce.IsSuccessful);
            Assert.Equal(OperationResultState.Failed, blockedPrepare.Result.State);
            Assert.True(projectRelease.Result.IsSuccessful);
        }
        finally
        {
            fixture.Release.EndScopedRelease();
        }

        var prepareAfterRelease = await fixture.Workflow.PrepareProjectAsync(
            CreatePolicy(),
            CancellationToken.None);
        Assert.True(prepareAfterRelease.Result.IsSuccessful);
    }

    [Fact]
    public async Task ReleaseAsync_cancels_active_workflow_operation_before_cleanup_and_profile_unload()
    {
        var projectOperationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var projectOperationCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var project = new FakePlcProjectService(FakeTime.Create())
        {
            PrepareProjectHandler = async cancellationToken =>
            {
                projectOperationStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("The project operation must be cancelled by session release.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    projectOperationCancelled.TrySetResult();
                    throw;
                }
            }
        };
        var fixture = CreateFixture(project: project);
        await LoadAndBindAsync(fixture);
        fixture.Workflow.Configure(CreateRunSetup());

        var prepareTask = fixture.Workflow.PrepareProjectAsync(
            CreatePolicy(),
            CancellationToken.None);
        await projectOperationStarted.Task;

        var release = await fixture.Release.ReleaseAsync(
            CreatePolicy(),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        var prepare = await prepareTask;

        Assert.True(projectOperationCancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(OperationResultState.Cancelled, prepare.Result.State);
        Assert.True(release.IsSuccessful);
        Assert.Null(fixture.Sessions.CurrentSession);
        Assert.Equal(RunWorkflowState.NotConfigured, fixture.Workflow.Current.State);
    }

    private static ReleaseFixture CreateFixture(
        IUnitTestEventStreamService? eventStream = null,
        IPlcProjectService? project = null)
    {
        var timeProvider = FakeTime.Create();
        var coreContext = TestCoreServiceContext.Create(timeProvider: timeProvider);
        var services = FakeAccessProfileFactory.CreateServices(timeProvider);
        if (eventStream is not null)
        {
            services = services with { UnitTestEventStream = eventStream };
        }

        if (project is not null)
        {
            services = services with { Project = project };
        }

        var plugin = new FakeAccessProfilePlugin((_, _) => services);
        var sessions = new AccessProfileSessionService(
            new AccessProfilePluginLoader(coreContext, [plugin]),
            new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true),
            coreContext);
        var workflow = new RunWorkflowService(
            new RunSetupValidator(timeProvider),
            coreContext);
        return new ReleaseFixture(
            workflow,
            sessions,
            new RunSessionReleaseService(workflow, sessions, coreContext));
    }

    private static async Task LoadAndBindAsync(ReleaseFixture fixture)
    {
        var config = FakeAccessProfileFactory.CreateDefaultConfig();
        var request = new AccessProfileSessionRequest(
            config.ProfileId,
            config.ProfileVersion,
            config.ProjectSource,
            ProjectOwnershipMode.ApplicationManaged);
        var load = await fixture.Sessions.LoadAsync(
            request,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        var session = Assert.IsType<AccessProfileSession>(load.Session);

        var bind = await fixture.Workflow.BindAccessProfileSessionAsync(
            session,
            CreatePolicy(),
            CancellationToken.None);
        Assert.True(bind.Result.IsSuccessful);
    }

    private static WorkflowActionPolicy CreatePolicy()
    {
        return new WorkflowActionPolicy(TimeSpan.FromSeconds(30));
    }

    private static RunSetup CreateRunSetup()
    {
        return new RunSetup(
            FakeAccessProfileFactory.CreateDefaultConfig(),
            RuntimeProvisioningMode.UseExisting,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()),
            new ProjectTargetRef(
                new ProjectTargetId("fake-project-target"),
                "Fake PLC Project Target",
                "FakeProjectTarget"),
            new RuntimeTargetRef(
                new RuntimeTargetId("fake-runtime-target"),
                "Fake PLC Runtime Target",
                "FakeRuntimeTarget"));
    }

    private sealed record ReleaseFixture(
        RunWorkflowService Workflow,
        AccessProfileSessionService Sessions,
        RunSessionReleaseService Release);

    private sealed class CancellationHoldingEventStreamService : FakeUnitTestEventStreamService
    {
        private readonly TaskCompletionSource cleanupStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilCleanupStartedAsync()
        {
            return cleanupStarted.Task;
        }

        public override async Task<OperationResult> CleanupAsync(CancellationToken cancellationToken)
        {
            cleanupStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return OperationResult.Success();
        }
    }
}
