using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Testing.AccessProfiles;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.AccessProfiles;

public sealed class FakeAccessProfileContractConformanceTests
{
    [Fact]
    public async Task Deployed_fake_profile_conforms_to_public_access_profile_contract()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "conformance-profiles");
        Assert.True(
            File.Exists(Path.Combine(root, "fake", "manifest.json")),
            $"The Fake access-profile conformance package was not staged below '{root}'.");

        var report = await AccessProfileContractConformanceHarness.RunAsync(
            new AccessProfileContractConformanceCase(
                root,
                "fake",
                "1.0.0",
                "fake-project.ap",
                new AccessProfileConfigurationPayload(
                    """
                    {
                      "projectTarget": { "id": "fake-project-target" },
                      "runtimeTarget": { "id": "fake-runtime-target" },
                      "testSettings": { "iterations": 1 }
                    }
                    """),
                ProjectOwnershipMode.ApplicationManaged,
                RuntimeProvisioningMode.UseExisting),
            TestCoreServiceContext.Create(timeProvider: FakeTime.Create()));

        Assert.True(
            report.Passed,
            string.Join(
                Environment.NewLine,
                report.Failures.Select(failure => $"{failure.Name}: {failure.Detail}")));
    }
}
