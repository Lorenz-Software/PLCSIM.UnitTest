using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

internal static class RunWorkflowProfileTestHelper
{
    public static Task<WorkflowActionResult> LoadAndBindDefaultFakeAsync(
        RunWorkflowService workflow,
        AccessProfileConfig config,
        ProjectOwnershipMode projectOwnershipMode = ProjectOwnershipMode.ApplicationManaged)
    {
        var timeProvider = FakeTime.Create();
        return LoadAndBindAsync(
            workflow,
            config,
            FakeAccessProfileFactory.CreatePlugin(timeProvider),
            timeProvider,
            projectOwnershipMode);
    }

    public static Task<WorkflowActionResult> LoadAndBindAsync(
        RunWorkflowService workflow,
        AccessProfileConfig config,
        FakeAccessProfileServices services,
        TimeProvider timeProvider)
    {
        return LoadAndBindAsync(
            workflow,
            config,
            new FakeAccessProfilePlugin((_, _) => services),
            timeProvider);
    }

    public static async Task<WorkflowActionResult> LoadAndBindAsync(
        RunWorkflowService workflow,
        AccessProfileConfig config,
        IAccessProfilePlugin plugin,
        TimeProvider timeProvider,
        ProjectOwnershipMode projectOwnershipMode = ProjectOwnershipMode.ApplicationManaged)
    {
        var coreContext = TestCoreServiceContext.Create(timeProvider: timeProvider);
        var sessionService = new AccessProfileSessionService(
            new AccessProfilePluginLoader(coreContext, [plugin]),
            new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true),
            coreContext);
        var load = await sessionService.LoadAsync(
            new AccessProfileSessionRequest(
                config.ProfileId,
                config.ProfileVersion,
                config.ProjectSource,
                projectOwnershipMode),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(AccessProfileLoadState.Loaded, load.State);
        Assert.NotNull(load.Session);

        return await workflow.BindAccessProfileSessionAsync(
            load.Session,
            new WorkflowActionPolicy(TimeSpan.FromMinutes(1)),
            CancellationToken.None);
    }

    public static async Task<PLCSIM.UnitTest.Core.AccessProfiles.AccessProfileSessionLoadResult> TryLoadAsync(
        AccessProfileConfig config,
        IAccessProfilePlugin plugin,
        TimeProvider timeProvider)
    {
        var coreContext = TestCoreServiceContext.Create(timeProvider: timeProvider);
        var sessionService = new AccessProfileSessionService(
            new AccessProfilePluginLoader(coreContext, [plugin]),
            new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true),
            coreContext);

        return await sessionService.LoadAsync(
            new AccessProfileSessionRequest(
                config.ProfileId,
                config.ProfileVersion,
                config.ProjectSource,
                ProjectOwnershipMode.ApplicationManaged),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
    }
}
