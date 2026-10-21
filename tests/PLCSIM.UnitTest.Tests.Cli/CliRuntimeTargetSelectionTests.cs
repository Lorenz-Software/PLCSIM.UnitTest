using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Cli;

public sealed class CliRuntimeTargetSelectionTests
{
    private static readonly RuntimeTargetRef First = new(new RuntimeTargetId("first"), "First", "First");
    private static readonly RuntimeTargetRef Second = new(new RuntimeTargetId("second"), "Second", "Second");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_selection_never_infers_default_from_candidates(bool multiple)
    {
        var discovery = new RuntimeTargetDiscoveryResult(multiple ? [First, Second] : [First],
            TargetDiscoveryState.CandidatesAvailable, []);
        var result = RuntimeTargetResolver.Resolve(RuntimeProvisioningMode.UseExisting, null, discovery);
        Assert.Null(result.Target);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Profile_default_is_used_only_when_explicit_target_is_omitted()
    {
        var discovery = new RuntimeTargetDiscoveryResult([First, Second],
            TargetDiscoveryState.CandidatesAvailable, [], Second);
        var omitted = RuntimeTargetResolver.Resolve(RuntimeProvisioningMode.UseExisting, null, discovery);
        Assert.Equal(Second, omitted.Target);
        Assert.Null(omitted.Error);
        var explicitTarget = RuntimeTargetResolver.Resolve(RuntimeProvisioningMode.UseExisting, First.Id, discovery);
        Assert.Equal(First, explicitTarget.Target);
        Assert.Null(explicitTarget.Error);
        var invalid = RuntimeTargetResolver.Resolve(RuntimeProvisioningMode.UseExisting, new("missing"), discovery);
        Assert.Null(invalid.Target);
        Assert.Contains("No fallback", invalid.Error);
    }

    [Fact]
    public void Ambiguous_explicit_target_is_rejected()
    {
        var discovery = new RuntimeTargetDiscoveryResult([First, First],
            TargetDiscoveryState.CandidatesAvailable, [], Second);
        var result = RuntimeTargetResolver.Resolve(RuntimeProvisioningMode.UseExisting, First.Id, discovery);
        Assert.Null(result.Target);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void CreateOwned_does_not_reuse_a_default_or_discard_an_explicit_selection()
    {
        var discovery = new RuntimeTargetDiscoveryResult([First], TargetDiscoveryState.CandidatesAvailable, [], First);
        var omitted = RuntimeTargetResolver.Resolve(RuntimeProvisioningMode.CreateOwned, null, discovery);
        Assert.Null(omitted.Target);
        Assert.Null(omitted.Error);
        var conflict = RuntimeTargetResolver.Resolve(RuntimeProvisioningMode.CreateOwned, First.Id, discovery);
        Assert.Null(conflict.Target);
        Assert.NotNull(conflict.Error);
    }
}
