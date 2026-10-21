using System.Text.Json;
using PLCSIM.UnitTest.AccessProfiles.Siemens;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens;

public sealed class SiemensProfileConfigurationServiceTests
{
    [Fact]
    public void Beckhoff_subscription_is_not_a_Siemens_strategy()
    {
        Load("""{ "unitTest": { "observationStrategy": "polling" } }""");
        var before = service.Configuration.Values.Json;
        Assert.False(service.SetFieldValue("siemens.unitTest.observationStrategy", "subscription").IsSuccessful);
        Assert.Equal(before, service.Configuration.Values.Json);
        Assert.True(service.SetFieldValue("siemens.unitTest.observationStrategy", "plcCycle").IsSuccessful);
        Assert.True(service.SetFieldValue("siemens.unitTest.observationStrategy", "polling").IsSuccessful);
    }

    private readonly SiemensProfileConfigurationService service = new(
        SiemensAccessProfilePlugin.CreateDefaultInfo(),
        new SiemensProfileConfigurationParser(),
        @"D:\Projects\Sample.ap21");

    [Fact]
    public void GetFields_returns_common_fields_and_ignores_inactive_owned_runtime_values()
    {
        Load(
            """
            {
              "runtimeRef": {
                "instanceName": 17,
                "storagePath": false,
                "provisioningMode": "obsolete"
              },
              "cleanup": "obsolete",
              "future": { "preserve": true }
            }
            """);

        var result = service.GetFields(RuntimeProvisioningMode.UseExisting);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(result.Value);
        Assert.Equal(
            [
                "siemens.unitTest.runControl",
                "siemens.unitTest.eventBuffer",
                "siemens.unitTest.observationStrategy",
                "siemens.unitTest.monitoringIntervalMs",
                "siemens.unitTest.drainIntervalMs",
                "siemens.unitTest.maxEventsPerBatch"
            ],
            result.Value.Fields.Select(field => field.Id));
        Assert.Equal("DB_UnitTestRunControl", result.Value.Fields[0].Value);
        Assert.Equal("DB_UnitTestEventBuffer", result.Value.Fields[1].Value);
        Assert.Equal("polling", result.Value.Fields[2].Value);
        Assert.Equal("10", result.Value.Fields[3].Value);
        Assert.Equal("1", result.Value.Fields[4].Value);
        Assert.Equal("32", result.Value.Fields[5].Value);
    }

    [Fact]
    public void GetFields_returns_create_owned_fields_with_effective_defaults()
    {
        Load(
            """
            {
              "runtimeRef": {
                "instanceName": "OwnedRuntime",
                "storagePath": "%TEMP%\\PLCSIM.UnitTest\\OwnedRuntime"
              },
              "cleanup": { "runtime": "release" },
              "unitTest": {
                "runControl": "DB_Run",
                "eventBuffer": "DB_Events",
                "observationStrategy": "plcCycle",
                "monitoringIntervalMs": 25,
                "drainIntervalMs": 2,
                "maxEventsPerBatch": 48
              }
            }
            """);

        var result = service.GetFields(RuntimeProvisioningMode.CreateOwned);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(result.Value);
        Assert.Equal(8, result.Value.Fields.Count);
        Assert.Equal("OwnedRuntime", Find(result.Value, "siemens.runtime.instanceName").Value);
        Assert.Equal("%TEMP%\\PLCSIM.UnitTest\\OwnedRuntime", Find(result.Value, "siemens.runtime.storagePath").Value);
        Assert.Equal("DB_Run", Find(result.Value, "siemens.unitTest.runControl").Value);
        Assert.Equal("DB_Events", Find(result.Value, "siemens.unitTest.eventBuffer").Value);
        Assert.Equal("plcCycle", Find(result.Value, "siemens.unitTest.observationStrategy").Value);
        Assert.Equal("25", Find(result.Value, "siemens.unitTest.monitoringIntervalMs").Value);
        Assert.Equal("2", Find(result.Value, "siemens.unitTest.drainIntervalMs").Value);
        Assert.Equal("48", Find(result.Value, "siemens.unitTest.maxEventsPerBatch").Value);
    }

    [Fact]
    public void GetFields_leaves_plc_cycle_monitoring_interval_unset_when_omitted()
    {
        Load("""{ "unitTest": { "observationStrategy": "plcCycle" } }""");

        var result = service.GetFields(RuntimeProvisioningMode.UseExisting);

        Assert.True(result.IsSuccessful);
        var field = Find(result.Value!, "siemens.unitTest.monitoringIntervalMs");
        Assert.Null(field.Value);
        Assert.Null(field.DefaultValue);
        Assert.False(field.IsRequired);
    }

    [Fact]
    public void GetFields_projects_archive_workspace_only_for_binding_declared_archive_source()
    {
        var archiveService = new SiemensProfileConfigurationService(
            SiemensAccessProfilePlugin.CreateDefaultInfo(),
            new SiemensProfileConfigurationParser(),
            @"D:\Projects\Sample.zap21",
            TiaProjectOpenMode.RetrievedArchive);
        var load = archiveService.LoadConfig(
            """
            {
              "tia": {
                "archiveWorkDirectory": "D:\\TIA\\ArchiveWork"
              }
            }
            """);

        var archiveFields = archiveService.GetFields(RuntimeProvisioningMode.UseExisting);
        var directFields = service.GetFields(RuntimeProvisioningMode.UseExisting);

        Assert.True(load.IsSuccessful);
        Assert.True(archiveFields.IsSuccessful);
        Assert.Equal(
            @"D:\TIA\ArchiveWork",
            Find(archiveFields.Value!, "siemens.tia.archiveWorkDirectory").Value);
        Assert.True(directFields.IsSuccessful);
        Assert.DoesNotContain(
            directFields.Value!.Fields,
            field => field.Id == "siemens.tia.archiveWorkDirectory");
    }

    [Fact]
    public void Typed_crud_updates_only_selected_value_and_preserves_unknown_content()
    {
        Load(
            """
            {
              "runtimeRef": {
                "instanceName": "InactiveOwnedRuntime",
                "custom": 42
              },
              "unitTest": {
                "runControl": "DB_Run",
                "custom": "unchanged"
              },
              "future": {
                "enabled": true
              }
            }
            """);

        Assert.True(service.AddFieldValue("siemens.unitTest.monitoringIntervalMs", 50).IsSuccessful);
        Assert.True(service.AddFieldValue("siemens.unitTest.drainIntervalMs", 2).IsSuccessful);
        Assert.True(service.AddFieldValue("siemens.unitTest.maxEventsPerBatch", 48).IsSuccessful);
        Assert.True(service.SetFieldValue("siemens.unitTest.runControl", "DB_Changed").IsSuccessful);
        Assert.Equal(50, service.GetIntegerFieldValue("siemens.unitTest.monitoringIntervalMs").Value);
        Assert.Equal(2, service.GetIntegerFieldValue("siemens.unitTest.drainIntervalMs").Value);
        Assert.Equal(48, service.GetIntegerFieldValue("siemens.unitTest.maxEventsPerBatch").Value);
        Assert.Equal("DB_Changed", service.GetStringFieldValue("siemens.unitTest.runControl").Value);

        using var document = JsonDocument.Parse(service.Configuration.Values.Json);
        var root = document.RootElement;
        Assert.Equal("unchanged", root.GetProperty("unitTest").GetProperty("custom").GetString());
        Assert.Equal("InactiveOwnedRuntime", root.GetProperty("runtimeRef").GetProperty("instanceName").GetString());
        Assert.Equal(42, root.GetProperty("runtimeRef").GetProperty("custom").GetInt32());
        Assert.True(root.GetProperty("future").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Mutation_rejects_field_not_projected_for_run_intent()
    {
        var result = service.GetField(
            "siemens.runtime.instanceName",
            RuntimeProvisioningMode.UseExisting);

        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "SiemensProfile.Config.Field.NotProjected");
    }

    [Fact]
    public void Mutation_does_not_replace_malformed_target_container()
    {
        Load("""{ "unitTest": "preserve" }""");

        var result = service.AddFieldValue("siemens.unitTest.runControl", "DB_Run");

        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "SiemensProfile.Config.Field.ContainerInvalid");
        Assert.Contains("\"preserve\"", service.Configuration.Values.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_optional_storage_path_preserves_sibling_values()
    {
        Load(
            """
            {
              "runtimeRef": {
                "instanceName": "OwnedRuntime",
                "storagePath": "D:\\Runtime",
                "future": 42
              }
            }
            """);

        var result = service.RemoveFieldValue("siemens.runtime.storagePath");

        Assert.True(result.IsSuccessful);
        using var document = JsonDocument.Parse(service.Configuration.Values.Json);
        var runtimeRef = document.RootElement.GetProperty("runtimeRef");
        Assert.False(runtimeRef.TryGetProperty("storagePath", out _));
        Assert.Equal("OwnedRuntime", runtimeRef.GetProperty("instanceName").GetString());
        Assert.Equal(42, runtimeRef.GetProperty("future").GetInt32());
    }

    [Fact]
    public void Invalid_mutations_are_atomic()
    {
        Load("""{ "future": true }""");
        var before = service.Configuration.Values.Json;

        Assert.False(service.AddFieldValue("siemens.unitTest.observationStrategy", "unsupported").IsSuccessful);
        Assert.False(service.AddFieldValue("siemens.unitTest.monitoringIntervalMs", 0).IsSuccessful);
        Assert.False(service.AddFieldValue("siemens.unitTest.drainIntervalMs", 0).IsSuccessful);
        Assert.False(service.AddFieldValue("siemens.unitTest.maxEventsPerBatch", 0).IsSuccessful);
        Assert.False(service.AddFieldValue("siemens.unitTest.monitoringIntervalMs", "wrong type").IsSuccessful);

        Assert.Equal(before, service.Configuration.Values.Json);
    }

    [Fact]
    public void LoadConfig_normalizes_deprecated_poll_interval_alias()
    {
        Load("""{ "unitTest": { "pollIntervalMs": 25 } }""");

        Assert.Equal(25, service.GetIntegerFieldValue("siemens.unitTest.monitoringIntervalMs").Value);
        using var document = JsonDocument.Parse(service.Configuration.Values.Json);
        var unitTest = document.RootElement.GetProperty("unitTest");
        Assert.Equal(25, unitTest.GetProperty("monitoringIntervalMs").GetInt32());
        Assert.False(unitTest.TryGetProperty("pollIntervalMs", out _));
    }

    [Fact]
    public void ValidateConfig_requires_instance_name_only_for_create_owned()
    {
        var createOwned = service.ValidateConfig(RuntimeProvisioningMode.CreateOwned);
        var useExisting = service.ValidateConfig(RuntimeProvisioningMode.UseExisting);

        Assert.False(createOwned.IsValid);
        Assert.Contains(createOwned.Diagnostics, diagnostic =>
            diagnostic.Code == "SiemensProfile.Config.runtimeRef.instanceName.Required");
        Assert.False(useExisting.IsValid);
        Assert.Contains(useExisting.Diagnostics, diagnostic =>
            diagnostic.Code == "SiemensProfile.Config.runtimeRef.id.Required");
        Assert.DoesNotContain(useExisting.Diagnostics, diagnostic =>
            diagnostic.Code == "SiemensProfile.Config.runtimeRef.instanceName.Required");
    }

    [Fact]
    public void ProvisioningValidationSeparatesExistingTargetFromCreationParameters()
    {
        Load("""{"runtimeRef":{"id":"runtime-1","instanceName":"NewRuntime","storagePath":"D:/runtime"}}""");
        var before = service.SerializeConfig().Value;
        Assert.True(service.ValidateConfig(RuntimeProvisioningMode.UseExisting).IsValid);
        var conflicting = service.ValidateConfig(RuntimeProvisioningMode.CreateOwned);
        Assert.False(conflicting.IsValid);
        Assert.Contains(conflicting.Diagnostics, diagnostic =>
            diagnostic.Code == "SiemensProfile.Config.runtimeRef.id.Conflicting");
        Assert.Equal(before, service.SerializeConfig().Value);
        Assert.True(service.SetRuntimeTarget(null).IsSuccessful);
        Assert.True(service.ValidateConfig(RuntimeProvisioningMode.CreateOwned).IsValid);
        using var document = JsonDocument.Parse(service.SerializeConfig().Value!);
        Assert.Equal("NewRuntime", document.RootElement.GetProperty("runtimeRef").GetProperty("instanceName").GetString());
        Assert.Equal("D:/runtime", document.RootElement.GetProperty("runtimeRef").GetProperty("storagePath").GetString());
    }

    [Fact]
    public void Target_accessors_keep_target_ids_inside_profile_payload()
    {
        var project = new ProjectTargetRef(new ProjectTargetId("plc-1"), "PLC 1");
        var runtime = new RuntimeTargetRef(new RuntimeTargetId("runtime-1"), "Runtime 1");

        Assert.True(service.SetProjectTarget(project).IsSuccessful);
        Assert.True(service.SetRuntimeTarget(runtime).IsSuccessful);
        Assert.Equal("plc-1", service.ProjectTargetId?.Value);
        Assert.Equal("runtime-1", service.RuntimeTargetId?.Value);

        using var document = JsonDocument.Parse(service.Configuration.Values.Json);
        Assert.Equal("plc-1", document.RootElement.GetProperty("plcRef").GetProperty("id").GetString());
        Assert.Equal("runtime-1", document.RootElement.GetProperty("runtimeRef").GetProperty("id").GetString());
    }

    [Fact]
    public void Invalid_load_keeps_previous_configuration()
    {
        Load("""{ "future": true }""");
        var before = service.Configuration;

        var result = service.LoadConfig("not-json");

        Assert.False(result.IsSuccessful);
        Assert.Equal(before, service.Configuration);
    }

    [Fact]
    public void Load_and_serialize_preserve_inactive_internal_policy_fields_without_projecting_them()
    {
        Load(
            """
            {
              "runtimeRef": {
                "id": "runtime-1",
                "provisioningMode": "createOwned",
                "timeouts": {
                  "prepareMs": 1234,
                  "releaseMs": 5678
                }
              },
              "binding": {
                "tia": "V21",
                "plcsim": "V8"
              }
            }
            """);

        var projection = service.GetFields(RuntimeProvisioningMode.UseExisting);
        var validation = service.ValidateConfig(RuntimeProvisioningMode.UseExisting);
        var serialized = service.SerializeConfig();

        Assert.True(projection.IsSuccessful);
        Assert.DoesNotContain(projection.Value!.Fields, field =>
            field.Id.Contains("provisioning", StringComparison.OrdinalIgnoreCase)
            || field.Id.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || field.Id.Contains("binding", StringComparison.OrdinalIgnoreCase));
        Assert.True(validation.IsValid);
        Assert.True(serialized.IsSuccessful);
        using var document = JsonDocument.Parse(serialized.Value!);
        Assert.Equal(
            "createOwned",
            document.RootElement.GetProperty("runtimeRef").GetProperty("provisioningMode").GetString());
        Assert.Equal(
            1234,
            document.RootElement.GetProperty("runtimeRef").GetProperty("timeouts").GetProperty("prepareMs").GetInt32());
        Assert.Equal("V21", document.RootElement.GetProperty("binding").GetProperty("tia").GetString());
    }

    private void Load(string values)
    {
        var result = service.LoadConfig(values);
        Assert.True(result.IsSuccessful);
    }

    private static AccessProfileConfigurationField Find(
        AccessProfileConfigurationProjection projection,
        string id)
    {
        return Assert.Single(projection.Fields, field => field.Id == id);
    }
}
