using PLCSIM.UnitTest.Common.Services;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.CommonContracts;

public sealed class AccessHealthContractTests
{
    [Fact]
    public void Access_health_default_helpers_publish_stable_contract_values()
    {
        var timestamp = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

        var notEstablished = AccessHealth.NotEstablished(timestamp);
        var healthy = AccessHealth.Healthy(timestamp);
        var disposed = AccessHealth.Disposed(timestamp);

        Assert.Equal(AccessHealthState.NotEstablished, notEstablished.State);
        Assert.Equal(AccessHealthProblem.None, notEstablished.Problem);
        Assert.Equal("AccessHealth.NotEstablished", notEstablished.ReasonCode);
        Assert.Equal("Access context is not established.", notEstablished.Message);
        Assert.Equal(timestamp, notEstablished.UpdatedAt);

        Assert.Equal(AccessHealthState.Healthy, healthy.State);
        Assert.Equal(AccessHealthProblem.None, healthy.Problem);
        Assert.Equal("AccessHealth.Healthy", healthy.ReasonCode);
        Assert.Equal("Access context is healthy.", healthy.Message);
        Assert.Equal(timestamp, healthy.UpdatedAt);

        Assert.Equal(AccessHealthState.Disposed, disposed.State);
        Assert.Equal(AccessHealthProblem.Released, disposed.Problem);
        Assert.Equal("AccessHealth.Disposed", disposed.ReasonCode);
        Assert.Equal("Access context is disposed.", disposed.Message);
        Assert.Equal(timestamp, disposed.UpdatedAt);
    }

    [Fact]
    public void Access_health_update_carries_service_kind_and_message_payload()
    {
        var timestamp = new DateTimeOffset(2026, 6, 25, 12, 1, 0, TimeSpan.Zero);
        var health = new AccessHealth(
            AccessHealthState.Invalidated,
            AccessHealthProblem.CommunicationLost,
            "Test.Runtime.Lost",
            "Runtime communication was lost.",
            timestamp);

        var update = new AccessServiceHealthUpdate(AccessServiceKind.PlcControl, health);

        Assert.Equal(AccessServiceKind.PlcControl, update.ServiceKind);
        Assert.Equal(AccessHealthState.Invalidated, update.Health.State);
        Assert.Equal(AccessHealthProblem.CommunicationLost, update.Health.Problem);
        Assert.Equal("Test.Runtime.Lost", update.Health.ReasonCode);
        Assert.Equal("Runtime communication was lost.", update.Health.Message);
        Assert.Equal(timestamp, update.Health.UpdatedAt);
    }
}
