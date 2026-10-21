using System.Text.Json.Nodes;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff;
using PLCSIM.UnitTest.Common.AccessProfiles;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("runtimeRef", "id", "null")]
    [InlineData("plcRef", "id", "42")]
    [InlineData("unitTest", "monitoringIntervalMs", "\"10\"")]
    [InlineData("unitTest", "drainIntervalMs", "-1")]
    [InlineData("unitTest", "maxEventsPerBatch", "true")]
    [InlineData("unitTest", "eventBuffer", "\" \"")]
    [InlineData("runtimeRef", "plcPort", "850")]
    [InlineData("runtimeRef", "plcPort", "null")]
    [InlineData("twincat", "archiveWorkDirectory", "\"\\u0000\"")]
    public void InvalidKnownValuesAreRejectedAtomically(string section, string field, string jsonValue)
    {
        var service = Create();
        Assert.True(service.LoadConfig("{\"unitTest\":{\"maxEventsPerBatch\":512}}").IsSuccessful);
        var before = service.SerializeConfig().Value;
        var values = new JsonObject { [section] = new JsonObject { [field] = JsonNode.Parse(jsonValue) } };
        Assert.False(service.LoadConfig(values.ToJsonString()).IsSuccessful);
        Assert.Equal(before, service.SerializeConfig().Value);
    }

    [Fact]
    public void CompleteConfigRoundTripsWithoutTouchingTheArchiveWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "beckhoff-config-" + Guid.NewGuid().ToString("N"));
        var values = JsonNode.Parse("""
            {"plcRef":{"id":"System/PLC"},"runtimeRef":{"id":"192.0.2.10.1.1","plcPort":65535},
             "unitTest":{"runControl":"GVL.Tests.RunControl","eventBuffer":"GVL.Tests.Events",
               "observationStrategy":"polling","monitoringIntervalMs":2147483647,"drainIntervalMs":1,"maxEventsPerBatch":512},
             "twincat":{},"extension":{"preserve":true}}
            """)!;
        values["twincat"]!["archiveWorkDirectory"] = workspace;
        var service = Create(ProjectOwnershipMode.ApplicationManaged, "not-opened.tszip");
        Assert.True(service.LoadConfig(values.ToJsonString()).IsSuccessful);
        Assert.True(service.ValidateConfig(RuntimeProvisioningMode.UseExisting).IsValid);
        Assert.Equal("System/PLC", service.ProjectTargetId!.Value.Value);
        Assert.Equal("192.0.2.10.1.1", service.RuntimeTargetId!.Value.Value);
        Assert.Equal(512, service.GetIntegerFieldValue("beckhoff.unitTest.maxEventsPerBatch").Value);
        var copy = Create(ProjectOwnershipMode.ApplicationManaged, "not-opened.tszip");
        Assert.True(copy.LoadConfig(service.SerializeConfig().Value!).IsSuccessful);
        Assert.True(JsonNode.DeepEquals(values, JsonNode.Parse(copy.SerializeConfig().Value!)));
        Assert.False(Directory.Exists(workspace));
    }

    [Fact]
    public void ReturnedConfigurationIsASnapshotAndWrongTypedMutationsDoNotChangeIt()
    {
        var service = Create();
        var before = service.Configuration;
        const string field = "beckhoff.unitTest.drainIntervalMs";
        Assert.True(service.AddFieldValue(field, 3).IsSuccessful);
        Assert.Equal("{}", before.Values.Json);
        Assert.False(service.SetFieldValue(field, 0).IsSuccessful);
        Assert.False(service.SetFieldValue(field, true).IsSuccessful);
        Assert.False(service.GetStringFieldValue(field).IsSuccessful);
        Assert.False(service.GetBooleanFieldValue(field).IsSuccessful);
        Assert.False(service.GetFloatFieldValue(field).IsSuccessful);
        Assert.False(service.AddFieldValue("unknown", "value").IsSuccessful);
        Assert.Equal(3, service.GetIntegerFieldValue(field).Value);
    }

    [Fact]
    public void ManagedSourceAndRuntimeSelectionAreValidatedWithoutVendorAccess()
    {
        var managed = Create(ProjectOwnershipMode.ApplicationManaged, "");
        Assert.False(managed.ValidateConfig(RuntimeProvisioningMode.CreateOwned).IsValid);
        var external = Create();
        Assert.False(external.ValidateConfig((RuntimeProvisioningMode)999).IsValid);
        Assert.False(external.ValidateConfig(RuntimeProvisioningMode.UseExisting).IsValid);
        Assert.True(external.ValidateConfig(RuntimeProvisioningMode.CreateOwned).IsValid);
        Assert.True(external.LoadConfig("{\"runtimeRef\":{\"id\":\"192.0.2.10.1.1\"}}").IsSuccessful);
        Assert.True(external.ValidateConfig(RuntimeProvisioningMode.UseExisting).IsValid);
        Assert.False(external.ContainsFieldValue("beckhoff.runtimeRef.plcPort"));
    }

    private static BeckhoffConfigurationService Create(ProjectOwnershipMode ownership = ProjectOwnershipMode.ExternallyManaged, string source = "") =>
        new(source, ownership, TimeProvider.System);
}
