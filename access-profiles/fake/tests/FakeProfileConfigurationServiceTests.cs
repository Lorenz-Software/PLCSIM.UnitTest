using System.Text.Json;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Fake;

public sealed class FakeProfileConfigurationServiceTests
{
    private const string NameFieldId = "fake.test.name";
    private const string IterationsFieldId = "fake.test.iterations";
    private const string FactorFieldId = "fake.test.factor";
    private const string EnabledFieldId = "fake.test.enabled";
    private const string ModeFieldId = "fake.test.mode";
    private const string InitialLostEventCountFieldId = "fake.eventBuffer.initialLostEventCount";

    private readonly FakeProfileConfigurationService service = new(
        FakeAccessProfilePlugin.CreateDefaultInfo(),
        "fake-project.json");

    [Fact]
    public void GetFields_exposes_stable_defaults_without_adding_explicit_values()
    {
        var result = service.GetFields(RuntimeProvisioningMode.UseExisting);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(result.Value);
        Assert.Collection(
            result.Value.Fields,
            field => AssertField(field, NameFieldId, AccessProfileConfigurationValueType.String, "Fake run"),
            field => AssertField(field, IterationsFieldId, AccessProfileConfigurationValueType.Integer, "1"),
            field => AssertField(field, FactorFieldId, AccessProfileConfigurationValueType.Float, "1"),
            field => AssertField(field, EnabledFieldId, AccessProfileConfigurationValueType.Boolean, "true"),
            field =>
            {
                AssertField(field, ModeFieldId, AccessProfileConfigurationValueType.String, "standard");
                Assert.Equal(["standard", "diagnostic"], field.AllowedValues.Select(value => value.Value));
            },
            field =>
            {
                Assert.Equal(InitialLostEventCountFieldId, field.Id);
                Assert.Equal(AccessProfileConfigurationValueType.Integer, field.ValueType);
                Assert.Equal("0", field.Value);
                Assert.Equal("0", field.DefaultValue);
                Assert.False(field.IsRequired);
            });
        Assert.All(result.Value.Fields, field => Assert.False(service.ContainsFieldValue(field.Id)));
    }

    [Fact]
    public void Load_and_serialize_preserve_unknown_payload_content()
    {
        const string payload =
            """
            {
              "testSettings": {
                "name": "Configured run",
                "iterations": 3,
                "factor": 1.25,
                "enabled": false,
                "mode": "diagnostic"
              },
              "future": { "preserve": true }
            }
            """;

        var load = service.LoadConfig(payload);
        var serialized = service.SerializeConfig();

        Assert.True(load.IsSuccessful);
        Assert.Equal("fake-project.json", load.Value?.ProjectSource);
        Assert.True(serialized.IsSuccessful);
        using var document = JsonDocument.Parse(Assert.IsType<string>(serialized.Value));
        Assert.True(document.RootElement.GetProperty("future").GetProperty("preserve").GetBoolean());
        Assert.Equal("Configured run", service.GetStringFieldValue(NameFieldId).Value);
        Assert.Equal(3, service.GetIntegerFieldValue(IterationsFieldId).Value);
        Assert.Equal(1.25f, service.GetFloatFieldValue(FactorFieldId).Value);
        Assert.False(service.GetBooleanFieldValue(EnabledFieldId).Value);
    }

    [Fact]
    public void Typed_crud_requires_add_then_set_and_supports_remove()
    {
        Assert.True(service.AddFieldValue(NameFieldId, "First").IsSuccessful);
        Assert.True(service.AddFieldValue(IterationsFieldId, 2).IsSuccessful);
        Assert.True(service.AddFieldValue(FactorFieldId, 1.5f).IsSuccessful);
        Assert.True(service.AddFieldValue(EnabledFieldId, true).IsSuccessful);
        Assert.True(service.AddFieldValue(ModeFieldId, "diagnostic").IsSuccessful);

        Assert.True(service.SetFieldValue(NameFieldId, "Second").IsSuccessful);
        Assert.Equal("Second", service.GetStringFieldValue(NameFieldId).Value);
        Assert.Equal(2, service.GetIntegerFieldValue(IterationsFieldId).Value);
        Assert.Equal(1.5f, service.GetFloatFieldValue(FactorFieldId).Value);
        Assert.True(service.GetBooleanFieldValue(EnabledFieldId).Value);
        Assert.Equal("diagnostic", service.GetStringFieldValue(ModeFieldId).Value);

        Assert.True(service.RemoveFieldValue(FactorFieldId).IsSuccessful);
        Assert.False(service.ContainsFieldValue(FactorFieldId));
        Assert.False(service.GetFloatFieldValue(FactorFieldId).IsSuccessful);
    }

    [Fact]
    public void Invalid_mutations_are_atomic()
    {
        Assert.True(service.LoadConfig("""{ "future": 42 }""").IsSuccessful);
        var before = service.Configuration.Values.Json;

        Assert.False(service.SetFieldValue(NameFieldId, "missing").IsSuccessful);
        Assert.False(service.AddFieldValue(IterationsFieldId, "wrong type").IsSuccessful);
        Assert.False(service.AddFieldValue(IterationsFieldId, 0).IsSuccessful);
        Assert.False(service.AddFieldValue(ModeFieldId, "unsupported").IsSuccessful);

        Assert.Equal(before, service.Configuration.Values.Json);
    }

    [Fact]
    public void Invalid_load_keeps_previous_configuration()
    {
        Assert.True(service.LoadConfig("""{ "future": 42 }""").IsSuccessful);
        var before = service.Configuration;

        var result = service.LoadConfig("not-json");

        Assert.False(result.IsSuccessful);
        Assert.Equal(before, service.Configuration);
    }

    [Fact]
    public void Target_convenience_accessors_update_profile_owned_payload()
    {
        var project = new ProjectTargetRef(new ProjectTargetId("plc-a"), "PLC A");
        var runtime = new RuntimeTargetRef(new RuntimeTargetId("runtime-a"), "Runtime A");

        Assert.True(service.SetProjectTarget(project).IsSuccessful);
        Assert.True(service.SetRuntimeTarget(runtime).IsSuccessful);
        Assert.Equal("plc-a", service.ProjectTargetId?.Value);
        Assert.Equal("runtime-a", service.RuntimeTargetId?.Value);

        using var document = JsonDocument.Parse(service.Configuration.Values.Json);
        Assert.Equal("plc-a", document.RootElement.GetProperty("plcRef").GetProperty("id").GetString());
        Assert.Equal("runtime-a", document.RootElement.GetProperty("runtimeRef").GetProperty("id").GetString());
    }

    [Fact]
    public void ValidateConfig_reports_invalid_explicit_stored_values()
    {
        Assert.True(service.LoadConfig(
            """
            {
              "testSettings": {
                "iterations": 0,
                "mode": "unsupported"
              }
            }
            """).IsSuccessful);

        var result = service.ValidateConfig(RuntimeProvisioningMode.UseExisting);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Diagnostics.Count);
    }

    [Fact]
    public void Load_applies_initial_lost_event_count_to_fake_runtime()
    {
        ushort appliedValue = ushort.MaxValue;
        var configuredService = new FakeProfileConfigurationService(
            FakeAccessProfilePlugin.CreateDefaultInfo(),
            "fake-project.json",
            value => appliedValue = value);

        var result = configuredService.LoadConfig(
            """{ "testSettings": { "initialLostEventCount": 3 } }""");

        Assert.True(result.IsSuccessful);
        Assert.Equal((ushort)3, appliedValue);
        Assert.True(configuredService.ValidateConfig(RuntimeProvisioningMode.UseExisting).IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void ValidateConfig_rejects_initial_lost_event_count_outside_ushort_range(int value)
    {
        Assert.True(service.LoadConfig(
            $$"""{ "testSettings": { "initialLostEventCount": {{value}} } }""").IsSuccessful);

        var result = service.ValidateConfig(RuntimeProvisioningMode.UseExisting);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "FakeProfile.Config.Field.InvalidValue");
    }

    [Fact]
    public void Load_applies_zero_when_initial_lost_event_count_is_missing()
    {
        ushort appliedValue = ushort.MaxValue;
        var configuredService = new FakeProfileConfigurationService(
            FakeAccessProfilePlugin.CreateDefaultInfo(),
            "fake-project.json",
            value => appliedValue = value);

        Assert.True(configuredService.LoadConfig("{}").IsSuccessful);

        Assert.Equal((ushort)0, appliedValue);
    }

    [Fact]
    public void Load_applies_explicit_safety_authorization_requirement_to_fake_project_service()
    {
        OperationResult<SafetyProgramAuthorizationRequirementResult>? applied = null;
        var configuredService = new FakeProfileConfigurationService(
            FakeAccessProfilePlugin.CreateDefaultInfo(),
            "fake-project.json",
            safetyAuthorizationRequirementApplied: result => applied = result);

        var result = configuredService.LoadConfig(
            """{ "testSettings": { "safetyAuthorizationRequirement": "passwordRequired" } }""");

        Assert.True(result.IsSuccessful);
        Assert.True(applied?.IsSuccessful);
        Assert.Equal(
            SafetyProgramAuthorizationRequirement.PasswordRequired,
            applied?.Value?.Requirement);
    }

    [Fact]
    public void Load_without_safety_authorization_requirement_preserves_injected_fake_behavior()
    {
        var applicationCount = 0;
        var configuredService = new FakeProfileConfigurationService(
            FakeAccessProfilePlugin.CreateDefaultInfo(),
            "fake-project.json",
            safetyAuthorizationRequirementApplied: _ => applicationCount++);

        var result = configuredService.LoadConfig("{}");

        Assert.True(result.IsSuccessful);
        Assert.Equal(0, applicationCount);
    }

    private static void AssertField(
        AccessProfileConfigurationField field,
        string id,
        AccessProfileConfigurationValueType valueType,
        string value)
    {
        Assert.Equal(id, field.Id);
        Assert.Equal(valueType, field.ValueType);
        Assert.Equal(value, field.Value);
        Assert.Equal(value, field.DefaultValue);
        Assert.True(field.IsRequired);
    }
}
