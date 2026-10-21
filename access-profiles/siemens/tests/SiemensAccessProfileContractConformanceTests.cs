using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Testing.AccessProfiles;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens;

public sealed class SiemensAccessProfileContractConformanceTests
{
    [Fact]
    public async Task Deployed_siemens_profile_with_fake_bindings_conforms_to_public_contract()
    {
        var discoveryRoot = Path.Combine(AppContext.BaseDirectory, "conformance-profiles");
        var packageRoot = Path.Combine(discoveryRoot, "siemens");
        var projectSource = Path.Combine(packageRoot, "projects", "siemens-fake-project.apf");
        Assert.True(
            File.Exists(Path.Combine(packageRoot, "manifest.json")),
            $"The Siemens access-profile conformance package was not staged below '{packageRoot}'.");
        Assert.True(File.Exists(projectSource), $"The Siemens Fake project was not staged: '{projectSource}'.");

        var report = await AccessProfileContractConformanceHarness.RunAsync(
            new AccessProfileContractConformanceCase(
                discoveryRoot,
                "siemens",
                "2.0.0",
                projectSource,
                new AccessProfileConfigurationPayload(
                    """
                    {
                      "plcRef": {
                        "id": "siemens-fake-plc"
                      },
                      "runtimeRef": {
                        "id": "fake-runtime",
                        "instanceName": "SiemensConformanceRuntime",
                        "cpuType": "s7-1500"
                      },
                      "unitTest": {
                        "runControl": "DB_UnitTestRunControl",
                        "eventBuffer": "DB_UnitTestEventBuffer",
                        "observationStrategy": "polling",
                        "pollIntervalMs": 100
                      },
                      "cleanup": {
                        "runtime": "release"
                      }
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
