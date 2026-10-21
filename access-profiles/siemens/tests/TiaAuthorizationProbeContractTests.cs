using System.Text.Json;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens;

public sealed class TiaAuthorizationProbeContractTests
{
    [Theory]
    [InlineData(TiaAuthorizationProbeStatus.Authorized, true)]
    [InlineData(TiaAuthorizationProbeStatus.Denied, false)]
    [InlineData(TiaAuthorizationProbeStatus.Unavailable, false)]
    [InlineData(TiaAuthorizationProbeStatus.TimedOut, false)]
    [InlineData(TiaAuthorizationProbeStatus.Failed, false)]
    [InlineData(TiaAuthorizationProbeStatus.CleanupFailed, false)]
    public void Probe_result_authorization_is_explicit(
        TiaAuthorizationProbeStatus status,
        bool expected)
    {
        var result = new TiaAuthorizationProbeResult(status, [], []);

        Assert.Equal(expected, result.IsAuthorized);
    }

    [Fact]
    public void Package_descriptor_can_publish_optional_authorization_probe_capability()
    {
        var descriptor = JsonSerializer.Deserialize<SiemensBindingPackageDescriptor>(
            """
            {
              "schemaVersion": 1,
              "info": {
                "bindingKind": "tia-openness",
                "bindingVersion": "21.0.0",
                "displayName": "TIA Openness Binding"
              },
              "entryPoint": {
                "assembly": "binding.dll",
                "typeName": "Binding"
              },
              "declaration": {
                "accessApis": [],
                "projectSources": [],
                "runtimes": [],
                "unitTestCommunicationContractVersions": [],
                "unitTestEventStreamContractVersions": []
              },
              "setup": {
                "tiaOpennessAuthorizationProbe": true
              }
            }
            """,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        Assert.NotNull(descriptor);
        Assert.True(descriptor.Setup?.TiaOpennessAuthorizationProbe);
    }

    [Fact]
    public void Existing_package_descriptor_without_setup_capabilities_remains_valid()
    {
        var descriptor = new SiemensBindingPackageDescriptor(
            1,
            new SiemensBindingInfo("tia-openness", "1.0.0", "Binding"),
            new SiemensBindingEntryPoint("binding.dll", "Binding"),
            SiemensBindingDeclaration.Empty);

        Assert.Null(descriptor.Setup);
    }
}
