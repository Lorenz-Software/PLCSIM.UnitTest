using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens;

public sealed class RuntimeProvisioningContractTests
{
    [Fact]
    public void Rename_preserves_numeric_identity_without_legacy_alias()
    {
        Assert.Equal(0, (int)PlcsimRuntimeProvisioningMode.UseExisting);
        Assert.Equal(1, (int)PlcsimRuntimeProvisioningMode.CreateOwned);
        Assert.Equal(new[] { "UseExisting", "CreateOwned" }, Enum.GetNames<PlcsimRuntimeProvisioningMode>());
        Assert.False(Enum.TryParse<PlcsimRuntimeProvisioningMode>("RequireExisting", out _));
        Assert.Equal(new Version(2, 0, 0, 0), typeof(PlcsimRuntimeProvisioningMode).Assembly.GetName().Version);
    }

    [Theory]
    [InlineData(null, "UseExisting", false)]
    [InlineData("UseExisting", "UseExisting", false)]
    [InlineData("CreateOwned", "CreateOwned", true)]
    public void Worker_prepare_preserves_default_and_creation_intent(
        string? mode, string expectedMode, bool canCreate)
    {
        var values = new Dictionary<string, string> { ["id"] = "existing-runtime" };
        if (mode is not null)
            values["provisioningMode"] = mode;

        var request = RuntimePrepareRequest.FromValues(values);

        Assert.Equal(expectedMode, request.ProvisioningMode);
        Assert.Equal(canCreate, request.CanCreateOwnedRuntime);
        Assert.Equal("existing-runtime", request.Id);
        Assert.True(request.HasReference);
    }
}
