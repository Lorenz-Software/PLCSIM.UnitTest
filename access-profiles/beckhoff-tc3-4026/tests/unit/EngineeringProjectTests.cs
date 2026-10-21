using System.Text.Json;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class EngineeringProjectTests
{
    [Fact]
    public async Task DiscoveryAllowsMultipleTargetsAndPreparationReusesContextAfterSelection()
    {
        await using var test = new EngineeringFixture();
        test.ConfigureAccess = access => access.OnOpen = () => access.Targets.Add(
            EngineeringPlcTarget.FromXml(access.Workspace!.RootPath, Path.Combine(access.Workspace.RootPath, "other.tsproj"), "Other", EngineeringFixture.PlcXml()));
        var discovery = await test.Service.DiscoverProjectTargetsAsync(new(test.Config()), default);
        Assert.True(discovery.IsSuccessful);
        Assert.Equal(2, discovery.Value!.Candidates.Count);
        var context = test.Service.CurrentProject!;
        var unresolved = await test.Service.ResolveProjectTargetAsync(context, default);
        Assert.Equal(TargetResolutionState.Ambiguous, unresolved.Value!.State);
        var prepare = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        Assert.False(prepare.IsSuccessful);
        var id = discovery.Value.Candidates[1].Id.Value;
        prepare = await test.Service.PrepareProjectAsync(new(test.Config(JsonSerializer.Serialize(new { plcRef = new { id } }))), default);
        Assert.True(prepare.IsSuccessful);
        Assert.Equal(context.Id, prepare.Value!.Id);
        Assert.Equal(id, test.Service.SelectedTarget!.Id);
        Assert.Equal(852, test.Service.SelectedTarget.PlcPort);
        Assert.Equal(1, Assert.Single(test.Accesses).Opens);
        Assert.Equal(0, test.Accesses[0].Closes);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"runtimeRef\":{\"plcPort\":852}}", true)]
    [InlineData("{\"runtimeRef\":{\"plcPort\":851}}", false)]
    [InlineData("{\"plcRef\":{\"id\":\"missing\"}}", false)]
    public async Task ManagedSelectionDerivesPortAndRejectsExplicitConflicts(string json, bool succeeds)
    {
        await using var test = new EngineeringFixture();
        var result = await test.Service.PrepareProjectAsync(new(test.Config(json)), default);
        Assert.Equal(succeeds, result.IsSuccessful);
        if (succeeds) Assert.Equal(852, test.Service.SelectedTarget!.PlcPort);
        else Assert.Null(test.Service.SelectedTarget);
    }

    [Fact]
    public async Task NoCandidateIsDiscoverySuccessButNotPreparedTarget()
    {
        await using var test = new EngineeringFixture();
        test.ConfigureAccess = access => access.OnOpen = access.Targets.Clear;
        var discovery = await test.Service.DiscoverProjectTargetsAsync(new(test.Config()), default);
        Assert.True(discovery.IsSuccessful);
        Assert.Equal(TargetDiscoveryState.NoCandidates, discovery.Value!.State);
        Assert.False((await test.Service.PrepareProjectAsync(new(test.Config()), default)).IsSuccessful);
    }

    [Fact]
    public async Task ExternalSessionNeverCreatesEngineeringAccess()
    {
        await using var test = new EngineeringFixture(ownership: ProjectOwnershipMode.ExternallyManaged);
        Assert.Equal(OperationResultState.NotSupported,
            (await test.Service.DiscoverProjectTargetsAsync(new(test.Config()), default)).State);
        Assert.Equal(OperationResultState.NotSupported,
            (await test.Service.PrepareProjectAsync(new(test.Config()), default)).State);
        Assert.Empty(test.Accesses);
        Assert.Null(test.Service.CurrentProject);
    }

    [Theory]
    [InlineData("missing.sln")]
    [InlineData("standalone.plcproj")]
    public async Task InvalidSourceDoesNotStartEngineering(string file)
    {
        await using var test = new EngineeringFixture();
        var result = await test.Service.DiscoverProjectTargetsAsync(new(test.Config(source: Path.Combine(test.Root, file))), default);
        Assert.False(result.IsSuccessful);
        Assert.Empty(test.Accesses);
        Assert.Single(test.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOpenOrDiscoveryReleasesAccessAndOnlyOwnedWorkspace(bool failOpen)
    {
        await using var test = new EngineeringFixture(archive: true);
        test.ConfigureAccess = access =>
        {
            if (failOpen) access.OpenError = new IOException("Open failed.");
            else access.DiscoverError = new IOException("Read failed.");
        };
        var result = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        Assert.False(result.IsSuccessful);
        var access = Assert.Single(test.Accesses);
        Assert.Equal(1, access.Closes);
        Assert.False(Directory.Exists(access.Workspace!.RootPath));
        Assert.True(File.Exists(test.Source));
        Assert.Null(test.Service.CurrentProject);
    }

    [Fact]
    public async Task FailedCleanupRetainsWorkspaceBlocksNewContextAndCanBeRetried()
    {
        await using var test = new EngineeringFixture(archive: true);
        Assert.True((await test.Service.PrepareProjectAsync(new(test.Config()), default)).IsSuccessful);
        var access = Assert.Single(test.Accesses);
        access.CloseError = new IOException("XAE still active.");
        Assert.False((await test.Service.ReleaseProjectAsync(default)).IsSuccessful);
        Assert.True(Directory.Exists(access.Workspace!.RootPath));
        Assert.Equal(AccessHealthState.Invalidated, test.Service.Health.State);
        Assert.Single(test.Diagnostics.Where(diagnostic => diagnostic.Code == "Beckhoff.Project.CleanupFailed"));
        Assert.False((await test.Service.PrepareProjectAsync(new(test.Config()), default)).IsSuccessful);
        Assert.Single(test.Accesses);
        access.CloseError = null;
        Assert.True((await test.Service.ReleaseProjectAsync(default)).IsSuccessful);
        Assert.False(Directory.Exists(access.Workspace.RootPath));
    }

    [Fact]
    public async Task CancelledDispatchedOpenIsDrainedBeforeCleanupAndQueuedCallDoesNotExecute()
    {
        await using var test = new EngineeringFixture(archive: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.ConfigureAccess = access => access.DuringOpen = async _ => { entered.SetResult(); await unblock.Task; };
        using var cancellation = new CancellationTokenSource();
        var prepare = test.Service.PrepareProjectAsync(new(test.Config()), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = test.Service.PrepareProjectAsync(new(test.Config()), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(prepare.IsCompleted);
        Assert.Equal(0, test.Accesses[0].Closes);
        unblock.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);
        Assert.Equal(1, test.Accesses[0].Closes);
        Assert.False(Directory.Exists(test.Accesses[0].Workspace!.RootPath));
    }

    [Fact]
    public async Task PreCancelledCallCreatesNothingAndStaleContextCannotResolve()
    {
        await using var test = new EngineeringFixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Service.PrepareProjectAsync(new(test.Config()), new(true)));
        Assert.Empty(test.Accesses);
        var result = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        await test.Service.ReleaseProjectAsync(default);
        Assert.False((await test.Service.ResolveProjectTargetAsync(result.Value!, default)).IsSuccessful);
        Assert.True(File.Exists(test.Source));
    }

    [Fact]
    public async Task ReleaseIsIdempotentAndReopenProducesNewContext()
    {
        await using var test = new EngineeringFixture();
        var first = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        await test.Service.ReleaseProjectAsync(default);
        await test.Service.ReleaseProjectAsync(default);
        Assert.Equal(1, test.Accesses[0].Closes);
        var second = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        Assert.NotEqual(first.Value!.Id, second.Value!.Id);
        Assert.Equal(2, test.Accesses.Count);
    }

    [Fact]
    public async Task DuplicateTargetIdentityIsRejectedAndOperationsRequirePreparedTarget()
    {
        await using var test = new EngineeringFixture();
        test.ConfigureAccess = access => access.OnOpen = () => access.Targets.Add(access.Targets[0]);
        Assert.False((await test.Service.DiscoverProjectTargetsAsync(new(test.Config()), default)).IsSuccessful);
        Assert.Equal(1, test.Accesses[0].Closes);
        Assert.Equal(OperationResultState.Failed, (await test.Service.BuildAsync(null!, null!, default)).State);
        Assert.Equal(OperationResultState.Failed, (await test.Service.TransferToAsync(null!, null!, null!, default)).State);
        Assert.Equal(OperationResultState.Failed, (await test.Service.GetSafetyProgramAuthorizationRequirementAsync(null!, null!, default)).State);
    }

    [Fact]
    public async Task ArchiveCleanupWaitsForHostCloseAndPreservesParentAndUnrelatedFiles()
    {
        await using var test = new EngineeringFixture(archive: true);
        var parent = Path.Combine(test.Root, "imports");
        Directory.CreateDirectory(parent);
        var unrelated = Path.Combine(parent, "keep.txt");
        File.WriteAllText(unrelated, "keep");
        var configuration = test.Config(JsonSerializer.Serialize(new { twincat = new { archiveWorkDirectory = parent } }));
        Assert.True((await test.Service.PrepareProjectAsync(new(configuration), default)).IsSuccessful);
        var first = test.Accesses[0];
        first.OnClose = () => Assert.True(Directory.Exists(first.Workspace!.RootPath));
        var identity = test.Service.SelectedTarget!.Id;
        Assert.True((await test.Service.ReleaseProjectAsync(default)).IsSuccessful);
        Assert.False(Directory.Exists(first.Workspace!.RootPath));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(test.Source));
        Assert.True((await test.Service.PrepareProjectAsync(new(configuration), default)).IsSuccessful);
        Assert.NotEqual(first.Workspace.RootPath, test.Accesses[1].Workspace!.RootPath);
        Assert.Equal(identity, test.Service.SelectedTarget!.Id);
    }

    [Fact]
    public async Task SourceSwitchClosesPreviousHostBeforeOpeningAnother()
    {
        await using var test = new EngineeringFixture();
        var first = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        var secondSource = Path.Combine(test.Root, "second.sln");
        File.Copy(test.Source, secondSource);
        test.ConfigureAccess = access => access.OnOpen = () => Assert.Equal(1, test.Accesses[0].Closes);
        var second = await test.Service.PrepareProjectAsync(new(test.Config(source: secondSource)), default);
        Assert.True(second.IsSuccessful);
        Assert.NotEqual(first.Value!.Id, second.Value!.Id);
        Assert.Equal(2, test.Accesses.Count);
        Assert.True(File.Exists(test.Source));
        Assert.True(File.Exists(secondSource));
    }

    [Fact]
    public async Task FailedOpenAndCleanupRetainWorkspaceAndReportEachFailureOnce()
    {
        await using var test = new EngineeringFixture(archive: true);
        test.ConfigureAccess = access =>
        {
            access.OpenError = new IOException("open failure");
            access.CloseError = new IOException("cleanup failure");
        };
        Assert.False((await test.Service.PrepareProjectAsync(new(test.Config()), default)).IsSuccessful);
        Assert.Single(test.Diagnostics.Where(diagnostic => diagnostic.Code == "Beckhoff.Project.PrepareFailed"));
        Assert.Single(test.Diagnostics.Where(diagnostic => diagnostic.Code == "Beckhoff.Project.CleanupFailed"));
        Assert.True(Directory.Exists(test.Accesses[0].Workspace!.RootPath));
        Assert.Equal(ServiceState.CleanupRequired, test.Service.State);
    }

    [Theory]
    [InlineData("{\"runtimeRef\":{\"plcPort\":0}}")]
    [InlineData("{\"twincat\":{\"archiveWorkDirectory\":\"\"}}")]
    [InlineData("invalid JSON")]
    public async Task InvalidConfigurationDoesNotOpenEngineering(string json)
    {
        await using var test = new EngineeringFixture(archive: true);
        Assert.False((await test.Service.PrepareProjectAsync(new(test.Config(json)), default)).IsSuccessful);
        Assert.Empty(test.Accesses);
    }
}
