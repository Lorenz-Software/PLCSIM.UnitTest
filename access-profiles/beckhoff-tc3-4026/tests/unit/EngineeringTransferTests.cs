using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class EngineeringTransferTests
{
    private static readonly RuntimeTargetRef Runtime = new(new("192.0.2.10.1.1"), "Runtime", "Runtime");

    [Fact]
    public async Task ReleaseWaitsForCancelledInFlightDownloadBeforeClosingEngineering()
    {
        await using var test = new EngineeringFixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var project = (await test.Service.PrepareProjectAsync(new(test.Config()), default)).Value!;
        var access = test.Accesses[0];
        access.DuringDownload = async () => { entered.SetResult(); await finish.Task; };
        var transfer = test.Service.TransferToAsync(project, test.Service.SelectedTarget!.ToReference(), Runtime, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var release = test.Service.ReleaseProjectAsync(default);
        try
        {
            Assert.False(transfer.IsCompleted);
            Assert.False(release.IsCompleted);
            Assert.Equal(0, access.Closes);
        }
        finally { finish.SetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.True((await release).IsSuccessful);
        Assert.Equal(1, access.Closes);
    }

    [Fact]
    public async Task SelectedPortAndVerifiedIdentityArePassedToActivationAndDownload()
    {
        await using var test = new EngineeringFixture();
        var project = (await test.Service.PrepareProjectAsync(new(test.Config()), default)).Value!;
        var selected = test.Service.SelectedTarget!;
        var result = await test.Service.TransferToAsync(project, selected.ToReference(), Runtime, default);
        Assert.True(result.IsSuccessful, result.Message);
        Assert.Equal(project, result.Value!.Project);
        Assert.Equal(selected.ToReference(), result.Value.ProjectTarget);
        Assert.Equal(Runtime, result.Value.RuntimeTarget);
        Assert.Equal(["activate:192.0.2.10.1.1:852", "download:192.0.2.10.1.1:852"], test.Accesses[0].TransferCalls);
        Assert.True((await test.Service.ReleaseProjectAsync(default)).IsSuccessful);
        Assert.Null(test.Service.CurrentProject);
        Assert.Equal(1, test.Accesses[0].Closes);
        Assert.True(File.Exists(test.Source));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailureDoesNotRetryAndArchiveRemainsOwnedUntilCleanup(bool activation)
    {
        await using var test = new EngineeringFixture(archive: true);
        var project = (await test.Service.PrepareProjectAsync(new(test.Config()), default)).Value!;
        var access = test.Accesses[0];
        var owned = access.Workspace!.RootPath;
        if (activation) access.ActivateError = new IOException("activation failed");
        else access.DownloadError = new IOException("login failed");
        var result = await test.Service.TransferToAsync(project, test.Service.SelectedTarget!.ToReference(), Runtime, default);
        Assert.Equal(OperationResultState.Failed, result.State);
        Assert.Equal(activation ? 1 : 2, access.TransferCalls.Count);
        Assert.Equal(1, test.Transfer.Calls);
        Assert.Contains(test.Diagnostics, item => item.Code == "Beckhoff.Project.TransferFailed");
        Assert.True(Directory.Exists(owned));
        Assert.True((await test.Service.ReleaseProjectAsync(default)).IsSuccessful);
        Assert.False(Directory.Exists(owned));
        Assert.True(File.Exists(test.Source));
    }

    [Fact]
    public async Task CancellationAfterDispatchedDownloadReturnsWithoutClaimingSuccessAndCleanupStillWorks()
    {
        await using var test = new EngineeringFixture();
        using var cancellation = new CancellationTokenSource();
        var project = (await test.Service.PrepareProjectAsync(new(test.Config()), default)).Value!;
        test.Accesses[0].AfterDownload = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Service.TransferToAsync(
            project, test.Service.SelectedTarget!.ToReference(), Runtime, cancellation.Token));
        Assert.Equal(2, test.Accesses[0].TransferCalls.Count);
        Assert.True((await test.Service.ReleaseProjectAsync(default)).IsSuccessful);
    }

    [Fact]
    public async Task ExternalSessionCannotCreateEngineeringOrActivateRuntime()
    {
        await using var test = new EngineeringFixture(ownership: ProjectOwnershipMode.ExternallyManaged);
        Assert.Equal(OperationResultState.NotSupported,
            (await test.Service.TransferToAsync(null!, null!, Runtime, default)).State);
        Assert.Empty(test.Accesses);
        Assert.Equal(0, test.Transfer.Calls);
    }

    [Fact]
    public async Task StaleProjectContextCannotTransfer()
    {
        await using var test = new EngineeringFixture();
        var project = (await test.Service.PrepareProjectAsync(new(test.Config()), default)).Value!;
        var selected = test.Service.SelectedTarget!.ToReference();
        Assert.False((await test.Service.TransferToAsync(project with { Id = new("stale") }, selected, Runtime, default)).IsSuccessful);
        Assert.Equal(0, test.Transfer.Calls);
        Assert.Empty(test.Accesses[0].TransferCalls);
    }

    [Fact]
    public async Task FailedCleanupIsReportedAndCanBeRetriedWithoutDiscardingArchiveOwnership()
    {
        await using var test = new EngineeringFixture(archive: true);
        var project = (await test.Service.PrepareProjectAsync(new(test.Config()), default)).Value!;
        Assert.True((await test.Service.TransferToAsync(project, test.Service.SelectedTarget!.ToReference(), Runtime, default)).IsSuccessful);
        var access = test.Accesses[0];
        access.CloseError = new IOException("owned XAE still alive");
        Assert.False((await test.Service.ReleaseProjectAsync(default)).IsSuccessful);
        Assert.Equal(ServiceState.CleanupRequired, test.Service.State);
        Assert.True(Directory.Exists(access.Workspace!.RootPath));
        access.CloseError = null;
        Assert.True((await test.Service.ReleaseProjectAsync(default)).IsSuccessful);
        Assert.False(Directory.Exists(access.Workspace.RootPath));
    }
}
