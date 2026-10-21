using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class EngineeringBuildTests
{
    [Fact]
    public void StandardTargetIsNotBlockedBySeparateSafetyTarget()
    {
        var target = EngineeringPlcTarget.FromXml(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "system.tsproj"), "System", EngineeringFixture.PlcXml());
        var safety = target with { Id = "separate-safety", PlcTreePath = "TISC^Safety" };
        XaeProjectAccess.ValidateCurrentTarget(target, [target, safety]);
        Assert.Throws<NotSupportedException>(() => XaeProjectAccess.ValidateCurrentTarget(safety, [target, safety]));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("port")]
    [InlineData("tree")]
    [InlineData("system")]
    public void ChangedTargetEvidenceRequiresNewPreparation(string field)
    {
        var target = EngineeringPlcTarget.FromXml(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "system.tsproj"), "System", EngineeringFixture.PlcXml());
        var changed = field switch
        {
            "identity" => target with { Id = "changed" },
            "port" => target with { PlcPort = target.PlcPort + 1 },
            "tree" => target with { PlcTreePath = "TIPC^Other" },
            _ => target with { SystemProjectPath = Path.Combine(Path.GetTempPath(), "other.tsproj") }
        };
        Assert.Throws<InvalidOperationException>(() => XaeProjectAccess.ValidateCurrentTarget(target, [changed]));
    }

    [Fact]
    public async Task StandardAuthorizationAndBuildUseSelectedTargetAndPreserveWarnings()
    {
        await using var test = new EngineeringFixture();
        var prepare = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        var selected = test.Service.SelectedTarget!;
        var access = Assert.Single(test.Accesses);
        var authorization = await test.Service.GetSafetyProgramAuthorizationRequirementAsync(prepare.Value!, selected.ToReference(), default);
        Assert.True(authorization.IsSuccessful);
        Assert.Equal(SafetyProgramAuthorizationRequirement.NotRequired, authorization.Value!.Requirement);
        access.BuildResult = new(true, [new("Beckhoff.Project.CompilerMessage", LogLevel.Warning, "Compiler warning")]);
        var result = await test.Service.BuildAsync(prepare.Value!, selected.ToReference(), default);
        Assert.True(result.IsSuccessful);
        Assert.True(result.Value!.Succeeded);
        Assert.True(result.Value.HasWarnings);
        Assert.Equal(selected, access.BuiltTarget);
        Assert.Equal(1, access.Builds);
        Assert.Equal("Compiler warning", Assert.Single(result.Value.Diagnostics).Message);
        Assert.Same(result.Value.Diagnostics[0], Assert.Single(test.Diagnostics));
        Assert.Equal(ServiceState.Ready, test.Service.State);
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
        Assert.Equal(0, access.Closes);
    }

    [Fact]
    public async Task FailedBuildReturnsEvidenceAndCanBeRetriedWithoutReopening()
    {
        await using var test = new EngineeringFixture();
        var prepare = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        var target = test.Service.SelectedTarget!.ToReference();
        var access = test.Accesses[0];
        access.BuildResult = new(false, [new("Beckhoff.Project.CompilerMessage", LogLevel.Error, "Unexpected token at MAIN:12")]);
        var result = await test.Service.BuildAsync(prepare.Value!, target, default);
        Assert.Equal(OperationResultState.Failed, result.State);
        Assert.False(result.Value!.Succeeded);
        Assert.False(result.Value.HasWarnings);
        Assert.Equal(LogLevel.Error, Assert.Single(result.Value.Diagnostics).Level);
        access.BuildResult = new(true, []);
        Assert.True((await test.Service.BuildAsync(prepare.Value!, target, default)).IsSuccessful);
        Assert.Equal(1, access.Opens);
        Assert.Equal(2, access.Builds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrChangedSelectionDoesNotBuild(bool staleContext)
    {
        await using var test = new EngineeringFixture();
        var prepare = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        var target = test.Service.SelectedTarget!.ToReference();
        if (staleContext)
        {
            await test.Service.ReleaseProjectAsync(default);
            await test.Service.PrepareProjectAsync(new(test.Config()), default);
        }
        else target = target with { Id = new("unknown") };
        Assert.False((await test.Service.BuildAsync(prepare.Value!, target, default)).IsSuccessful);
        Assert.False((await test.Service.GetSafetyProgramAuthorizationRequirementAsync(prepare.Value!, target, default)).IsSuccessful);
        Assert.All(test.Accesses, access => { Assert.Equal(0, access.Builds); Assert.Equal(0, access.AuthorizationChecks); });
    }

    [Fact]
    public async Task UnsupportedRequirementFailsBeforeBuildAndPasswordIsNeverConsumed()
    {
        await using var test = new EngineeringFixture();
        var prepare = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        var target = test.Service.SelectedTarget!.ToReference();
        test.Accesses[0].AuthorizationError = new NotSupportedException("TwinSAFE operation is not supported.");
        var result = await test.Service.GetSafetyProgramAuthorizationRequirementAsync(prepare.Value!, target, default);
        Assert.Equal(OperationResultState.NotSupported, result.State);
        Assert.Null(result.Value);
        Assert.Equal(OperationResultState.NotSupported, (await test.Service.BuildAsync(prepare.Value!, target, default)).State);
        Assert.Equal(0, test.Accesses[0].Builds);
        using var password = new SecureString();
        foreach (var character in "do-not-publish-secret") password.AppendChar(character);
        Assert.Equal(OperationResultState.NotSupported, (await test.Service.AuthorizeSafetyProgramAsync(prepare.Value!, target, password, default)).State);
        Assert.DoesNotContain(test.Diagnostics, diagnostic => diagnostic.Message.Contains("do-not-publish-secret"));
    }

    [Fact]
    public async Task FailedAuthorizationInspectionDoesNotPretendNotRequired()
    {
        await using var test = new EngineeringFixture();
        var prepare = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        var target = test.Service.SelectedTarget!.ToReference();
        test.Accesses[0].AuthorizationError = new COMException("XAE unavailable", unchecked((int)0x80004005));
        var result = await test.Service.GetSafetyProgramAuthorizationRequirementAsync(prepare.Value!, target, default);
        Assert.Equal(OperationResultState.Failed, result.State);
        Assert.Null(result.Value);
        Assert.Contains("80004005", Assert.Single(test.Diagnostics).Message);
    }

    [Fact]
    public async Task VendorBuildExceptionRetainsMessageAndHResult()
    {
        await using var test = new EngineeringFixture();
        var prepare = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        test.Accesses[0].BuildError = new COMException("Vendor compile failed", unchecked((int)0x80004005));
        var result = await test.Service.BuildAsync(prepare.Value!, test.Service.SelectedTarget!.ToReference(), default);
        Assert.Equal(OperationResultState.Failed, result.State);
        Assert.Contains("Vendor compile failed", result.Message);
        Assert.Contains("80004005", result.Message);
        Assert.Equal("Beckhoff.Project.BuildFailed", Assert.Single(test.Diagnostics).Code);
        Assert.Equal(ServiceState.Ready, test.Service.State);
    }

    [Fact]
    public async Task CancelledBuildDrainsBeforeReleaseAndPublishesReturnedDiagnostics()
    {
        await using var test = new EngineeringFixture();
        var prepare = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var access = test.Accesses[0];
        access.DuringBuild = async _ => { entered.SetResult(); await unblock.Task; };
        access.BuildResult = new(true, [new("Beckhoff.Project.CompilerMessage", LogLevel.Warning, "Returned diagnostic")]);
        using var cancellation = new CancellationTokenSource();
        var build = test.Service.BuildAsync(prepare.Value!, test.Service.SelectedTarget!.ToReference(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var release = test.Service.ReleaseProjectAsync(default);
        Assert.False(build.IsCompleted);
        Assert.False(release.IsCompleted);
        Assert.Equal(0, access.Closes);
        unblock.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
        Assert.True((await release).IsSuccessful);
        Assert.Equal(1, access.Closes);
        Assert.Contains(test.Diagnostics, item => item.Message == "Returned diagnostic");
        Assert.Contains(test.Diagnostics, item => item.Code == "Beckhoff.Project.BuildCancelled");
    }

    [Fact]
    public async Task PreCancelledBuildDoesNotCallBackend()
    {
        await using var test = new EngineeringFixture();
        var prepare = await test.Service.PrepareProjectAsync(new(test.Config()), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Service.BuildAsync(
            prepare.Value!, test.Service.SelectedTarget!.ToReference(), new(true)));
        Assert.Equal(0, test.Accesses[0].Builds);
    }

    [Fact]
    public async Task ExternalSessionDoesNotEnterEngineeringForBuildOrAuthorization()
    {
        await using var test = new EngineeringFixture(ownership: ProjectOwnershipMode.ExternallyManaged);
        Assert.Equal(OperationResultState.NotSupported, (await test.Service.BuildAsync(null!, null!, default)).State);
        Assert.Equal(OperationResultState.NotSupported, (await test.Service.GetSafetyProgramAuthorizationRequirementAsync(null!, null!, default)).State);
        Assert.Empty(test.Accesses);
    }
}
