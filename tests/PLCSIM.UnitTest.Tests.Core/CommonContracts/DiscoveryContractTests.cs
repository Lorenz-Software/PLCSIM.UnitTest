using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.CommonContracts;

public sealed class DiscoveryContractTests
{
    [Fact]
    public void Project_target_discovery_result_contains_candidates_without_suggested_target()
    {
        var target = new ProjectTargetRef(new ProjectTargetId("plc-1"), "PLC_1", "PLC_1");
        var result = new ProjectTargetDiscoveryResult(
            [target],
            TargetDiscoveryState.CandidatesAvailable,
            Array.Empty<Diagnostic>());

        Assert.Equal(TargetDiscoveryState.CandidatesAvailable, result.State);
        Assert.Equal(target, Assert.Single(result.Candidates));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Runtime_target_discovery_result_can_represent_no_candidates()
    {
        var result = new RuntimeTargetDiscoveryResult(
            Array.Empty<RuntimeTargetRef>(),
            TargetDiscoveryState.NoCandidates,
            Array.Empty<Diagnostic>());

        Assert.Equal(TargetDiscoveryState.NoCandidates, result.State);
        Assert.Empty(result.Candidates);
        Assert.Empty(result.Diagnostics);
    }
}
