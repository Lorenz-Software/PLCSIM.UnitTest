using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using WorkerCommunicationPrepareRequest = PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker.CommunicationPrepareRequest;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.Fake;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.Fake;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens;

public sealed partial class SiemensAccessProfilePluginTests
{
    [Fact]
    public void CreateDefaultInfo_returns_stable_siemens_profile_metadata()
    {
        var info = SiemensAccessProfilePlugin.CreateDefaultInfo();

        Assert.Equal("siemens", info.ProfileId);
        Assert.Equal("2.0.0", info.Version);
        Assert.Equal("Siemens Access Profile", info.DisplayName);
        Assert.Equal("Siemens TIA Openness and PLCSIM Advanced access profile.", info.Description);
    }

    [Fact]
    public void ValidateConfig_accepts_matching_profile_identity()
    {
        var plugin = new SiemensAccessProfilePlugin();

        var result = plugin.ValidateConfig(CreateConfig(
            SiemensAccessProfilePlugin.DefaultProfileId,
            SiemensAccessProfilePlugin.DefaultProfileVersion));

        Assert.True(result.IsValid);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ValidateConfig_accepts_missing_project_source_for_mode_specific_load_validation()
    {
        var plugin = new SiemensAccessProfilePlugin();

        var result = plugin.ValidateConfig(CreateConfig(
            SiemensAccessProfilePlugin.DefaultProfileId,
            SiemensAccessProfilePlugin.DefaultProfileVersion,
            projectSource: string.Empty));

        Assert.True(result.IsValid);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ValidateConfig_rejects_other_profile_identity()
    {
        var plugin = new SiemensAccessProfilePlugin();

        var result = plugin.ValidateConfig(CreateConfig("fake", SiemensAccessProfilePlugin.DefaultProfileVersion));

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Config.ProfileMismatch");
    }

    [Fact]
    public void ValidateConfig_rejects_other_profile_version()
    {
        var plugin = new SiemensAccessProfilePlugin();

        var result = plugin.ValidateConfig(CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, "9.9.9"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Config.VersionMismatch");
    }

    [Fact]
    public void Parser_applies_profile_defaults_for_empty_values()
    {
        var parser = new SiemensProfileConfigurationParser();

        var result = parser.Parse(
            CreateConfig(SiemensAccessProfilePlugin.DefaultProfileId, SiemensAccessProfilePlugin.DefaultProfileVersion),
            SiemensProfileConfigurationValidationMode.Strict);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Configuration);
        Assert.Equal(".ap21", result.Configuration.ProjectSourceExtension);
        Assert.Null(result.Configuration.PlcRef.Id);
        Assert.Null(result.Configuration.RuntimeRef.Id);
        Assert.Null(result.Configuration.RuntimeRef.InstanceName);
        Assert.Null(result.Configuration.RuntimeRef.StoragePath);
        Assert.Equal(SiemensRuntimeCpuType.S71500, result.Configuration.RuntimeRef.CpuType);
        Assert.Equal(SiemensProfileConfigurationParser.DefaultRunControlDbName, result.Configuration.UnitTest.RunControl);
        Assert.Equal(SiemensProfileConfigurationParser.DefaultEventBufferDbName, result.Configuration.UnitTest.EventBuffer);
        Assert.Equal(SiemensCommunicationObservationStrategy.Polling, result.Configuration.UnitTest.ObservationStrategy);
        Assert.Equal(SiemensProfileConfigurationParser.DefaultUnitTestMonitoringInterval, result.Configuration.UnitTest.MonitoringInterval);
        Assert.Equal(SiemensProfileConfigurationParser.DefaultUnitTestDrainInterval, result.Configuration.UnitTest.DrainInterval);
        Assert.Equal(SiemensProfileConfigurationParser.DefaultUnitTestMaxEventsPerBatch, result.Configuration.UnitTest.MaxEventsPerBatch);
        Assert.Null(result.Configuration.Tia.ArchiveWorkDirectory);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Worker_protocol_fallback_matches_profile_monitoring_interval_default()
    {
        var request = WorkerCommunicationPrepareRequest.FromValues(
            new Dictionary<string, string>());

        Assert.Equal(
            SiemensProfileConfigurationParser.DefaultUnitTestMonitoringInterval,
            request.MonitoringInterval);
        Assert.Null(request.DrainInterval);
        Assert.Null(request.MaxEventsPerBatch);
    }

    [Fact]
    public void Worker_protocol_reads_canonical_monitoring_configuration()
    {
        var request = WorkerCommunicationPrepareRequest.FromValues(
            new Dictionary<string, string>
            {
                ["monitoringIntervalTicks"] = TimeSpan.FromMilliseconds(25).Ticks.ToString(),
                ["drainIntervalTicks"] = TimeSpan.FromMilliseconds(2).Ticks.ToString(),
                ["maxEventsPerBatch"] = "48"
            });

        Assert.Equal(TimeSpan.FromMilliseconds(25), request.MonitoringInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(2), request.DrainInterval);
        Assert.Equal(48, request.MaxEventsPerBatch);
    }

    [Fact]
    public void Worker_protocol_accepts_legacy_poll_interval_ticks()
    {
        var request = WorkerCommunicationPrepareRequest.FromValues(
            new Dictionary<string, string>
            {
                ["pollIntervalTicks"] = TimeSpan.FromMilliseconds(40).Ticks.ToString()
            });

        Assert.Equal(TimeSpan.FromMilliseconds(40), request.MonitoringInterval);
    }

    [Fact]
    public void Parser_reads_canonical_event_stream_monitoring_values()
    {
        var parser = new SiemensProfileConfigurationParser();

        var result = parser.Parse(
            CreateConfig(
                SiemensAccessProfilePlugin.DefaultProfileId,
                SiemensAccessProfilePlugin.DefaultProfileVersion,
                """
                {
                  "unitTest": {
                    "monitoringIntervalMs": 25,
                    "drainIntervalMs": 2,
                    "maxEventsPerBatch": 48
                  }
                }
                """),
            SiemensProfileConfigurationValidationMode.Strict);

        Assert.True(result.IsValid);
        Assert.Equal(TimeSpan.FromMilliseconds(25), result.Configuration?.UnitTest.MonitoringInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(2), result.Configuration?.UnitTest.DrainInterval);
        Assert.Equal(48, result.Configuration?.UnitTest.MaxEventsPerBatch);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Parser_accepts_deprecated_poll_interval_alias_without_diagnostic()
    {
        var parser = new SiemensProfileConfigurationParser();

        var result = parser.Parse(
            CreateConfig(
                SiemensAccessProfilePlugin.DefaultProfileId,
                SiemensAccessProfilePlugin.DefaultProfileVersion,
                """{ "unitTest": { "pollIntervalMs": 25 } }"""),
            SiemensProfileConfigurationValidationMode.Strict);

        Assert.True(result.IsValid);
        Assert.Equal(TimeSpan.FromMilliseconds(25), result.Configuration?.UnitTest.MonitoringInterval);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Parser_rejects_canonical_and_deprecated_monitoring_interval_together()
    {
        var parser = new SiemensProfileConfigurationParser();

        var result = parser.Parse(
            CreateConfig(
                SiemensAccessProfilePlugin.DefaultProfileId,
                SiemensAccessProfilePlugin.DefaultProfileVersion,
                """{ "unitTest": { "monitoringIntervalMs": 10, "pollIntervalMs": 25 } }"""),
            SiemensProfileConfigurationValidationMode.Strict);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "SiemensProfile.Config.unitTest.MonitoringIntervalAmbiguous");
    }

    [Theory]
    [InlineData("monitoringIntervalMs")]
    [InlineData("drainIntervalMs")]
    [InlineData("maxEventsPerBatch")]
    public void Parser_rejects_non_positive_event_stream_monitoring_values(string fieldName)
    {
        var parser = new SiemensProfileConfigurationParser();

        var result = parser.Parse(
            CreateConfig(
                SiemensAccessProfilePlugin.DefaultProfileId,
                SiemensAccessProfilePlugin.DefaultProfileVersion,
                $$"""{ "unitTest": { "{{fieldName}}": 0 } }"""),
            SiemensProfileConfigurationValidationMode.Strict);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code.Contains(fieldName, StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_reads_supported_profile_values()
    {
        var parser = new SiemensProfileConfigurationParser();

        var result = parser.Parse(
            CreateConfig(
                SiemensAccessProfilePlugin.DefaultProfileId,
                SiemensAccessProfilePlugin.DefaultProfileVersion,
                """
                {
                  "plcRef": { "id": "PlantLine1/PLC_1" },
                  "runtimeRef": {
                    "id": "UnitTestRuntime",
                    "instanceName": "UnitTestRuntime",
                    "storagePath": "%TEMP%\\PLCSIM.UnitTest\\runtime-state",
                    "provisioningMode": "createOwned",
                    "cpuType": "s7-1500",
                    "timeouts": {
                      "prepareMs": 1000,
                      "startMs": 2000,
                      "stopMs": 3000,
                      "releaseMs": 4000
                    }
                  },
                  "unitTest": {
                    "runControl": "DB_CustomRunControl",
                    "eventBuffer": "DB_CustomEventBuffer",
                    "observationStrategy": "plcCycle",
                    "pollIntervalMs": 25
                  },
                  "cleanup": { "runtime": "release" },
                  "tia": { "archiveWorkDirectory": "%TEMP%\\PLCSIM.UnitTest\\project-state" }
                }
                """),
            SiemensProfileConfigurationValidationMode.Strict,
            RuntimeProvisioningMode.CreateOwned);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Configuration);
        Assert.Equal("PlantLine1/PLC_1", result.Configuration.PlcRef.Id);
        Assert.Equal("UnitTestRuntime", result.Configuration.RuntimeRef.Id);
        Assert.Equal("UnitTestRuntime", result.Configuration.RuntimeRef.InstanceName);
        Assert.EndsWith(Path.Combine("PLCSIM.UnitTest", "runtime-state"), result.Configuration.RuntimeRef.StoragePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SiemensRuntimeCpuType.S71500, result.Configuration.RuntimeRef.CpuType);
        Assert.Equal("DB_CustomRunControl", result.Configuration.UnitTest.RunControl);
        Assert.Equal("DB_CustomEventBuffer", result.Configuration.UnitTest.EventBuffer);
        Assert.Equal(SiemensCommunicationObservationStrategy.PlcCycle, result.Configuration.UnitTest.ObservationStrategy);
        Assert.Equal(TimeSpan.FromMilliseconds(25), result.Configuration.UnitTest.MonitoringInterval);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "project-state")),
            result.Configuration.Tia.ArchiveWorkDirectory);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Parser_warns_for_unknown_fields_inside_known_objects()
    {
        var parser = new SiemensProfileConfigurationParser();

        var result = parser.Parse(
            CreateConfig(
                SiemensAccessProfilePlugin.DefaultProfileId,
                SiemensAccessProfilePlugin.DefaultProfileVersion,
                """
                {
                  "plcRef": {
                    "id": "PLC_1",
                    "displayName": "Ignored"
                  },
                  "futureObject": {
                    "value": true
                  }
                }
                """),
            SiemensProfileConfigurationValidationMode.Strict);

        Assert.True(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Config.plcRef.displayName.Unknown");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code.Contains("futureObject", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_preserves_and_ignores_legacy_runtime_cleanup()
    {
        var parser = new SiemensProfileConfigurationParser();

        var result = parser.Parse(
            CreateConfig(
                SiemensAccessProfilePlugin.DefaultProfileId,
                SiemensAccessProfilePlugin.DefaultProfileVersion,
                """
                {
                  "runtimeRef": {
                    "instanceName": "OwnedRuntime"
                  },
                  "cleanup": {
                    "runtime": "free"
                  }
                }
                """),
            SiemensProfileConfigurationValidationMode.Strict,
            RuntimeProvisioningMode.CreateOwned);

        Assert.True(result.IsValid);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Parser_ignores_legacy_runtime_policy_but_rejects_active_cpu_type()
    {
        var parser = new SiemensProfileConfigurationParser();

        var result = parser.Parse(
            CreateConfig(
                SiemensAccessProfilePlugin.DefaultProfileId,
                SiemensAccessProfilePlugin.DefaultProfileVersion,
                """
                {
                  "runtimeRef": {
                    "instanceName": "Owned Runtime",
                    "provisioningMode": "maybe",
                    "cpuType": "s7-1200",
                    "timeouts": {
                      "prepareMs": 0
                    }
                  }
                }
                """),
            SiemensProfileConfigurationValidationMode.Strict,
            RuntimeProvisioningMode.CreateOwned);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Config.runtimeRef.cpuType.Unsupported");
        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Code.Contains("provisioningMode", StringComparison.Ordinal)
            || diagnostic.Code.Contains("timeouts", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_ignores_inactive_owned_runtime_values_for_require_existing()
    {
        var parser = new SiemensProfileConfigurationParser();
        var result = parser.Parse(
            CreateConfig(
                SiemensAccessProfilePlugin.DefaultProfileId,
                SiemensAccessProfilePlugin.DefaultProfileVersion,
                """
                {
                  "runtimeRef": {
                    "id": "runtime-1",
                    "instanceName": 17,
                    "storagePath": false,
                    "provisioningMode": "obsolete",
                    "cpuType": "unsupported",
                    "timeouts": "obsolete"
                  },
                  "cleanup": "obsolete"
                }
                """),
            SiemensProfileConfigurationValidationMode.Strict,
            RuntimeProvisioningMode.UseExisting);

        Assert.True(result.IsValid);
        Assert.Equal("runtime-1", result.Configuration?.RuntimeRef.Id);
        Assert.Null(result.Configuration?.RuntimeRef.InstanceName);
        Assert.Null(result.Configuration?.RuntimeRef.StoragePath);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Parser_create_owned_preserves_omitted_storage_path()
    {
        var parser = new SiemensProfileConfigurationParser();
        var result = parser.Parse(
            CreateConfig(
                SiemensAccessProfilePlugin.DefaultProfileId,
                SiemensAccessProfilePlugin.DefaultProfileVersion,
                """{ "runtimeRef": { "instanceName": "OwnedRuntime" } }"""),
            SiemensProfileConfigurationValidationMode.Strict,
            RuntimeProvisioningMode.CreateOwned);

        Assert.True(result.IsValid);
        Assert.Null(result.Configuration?.RuntimeRef.StoragePath);
    }

    [Fact]
    public void Parser_plc_cycle_strategy_without_monitoring_interval_observes_every_cycle()
    {
        var parser = new SiemensProfileConfigurationParser();
        var result = parser.Parse(
            CreateConfig(
                SiemensAccessProfilePlugin.DefaultProfileId,
                SiemensAccessProfilePlugin.DefaultProfileVersion,
                """
                {
                  "unitTest": {
                    "observationStrategy": "plcCycle"
                  }
                }
                """),
            SiemensProfileConfigurationValidationMode.Strict);

        Assert.True(result.IsValid);
        Assert.Equal(SiemensCommunicationObservationStrategy.PlcCycle, result.Configuration?.UnitTest.ObservationStrategy);
        Assert.Null(result.Configuration?.UnitTest.MonitoringInterval);
        Assert.Null(result.Configuration?.UnitTest.DrainInterval);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Parser_plc_cycle_strategy_rejects_invalid_explicit_monitoring_interval()
    {
        var parser = new SiemensProfileConfigurationParser();
        var result = parser.Parse(
            CreateConfig(
                SiemensAccessProfilePlugin.DefaultProfileId,
                SiemensAccessProfilePlugin.DefaultProfileVersion,
                """
                {
                  "unitTest": {
                    "observationStrategy": "plcCycle",
                    "monitoringIntervalMs": "inactive"
                  }
                }
                """),
            SiemensProfileConfigurationValidationMode.Strict);

        Assert.False(result.IsValid);
        Assert.Null(result.Configuration?.UnitTest.MonitoringInterval);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "SiemensProfile.Config.unitTest.monitoringIntervalMs.InvalidType");
    }

    [Fact]
    public void Parser_keeps_zap21_extension_in_strict_mode()
    {
        var parser = new SiemensProfileConfigurationParser();
        var config = new AccessProfileConfig(
            SiemensAccessProfilePlugin.DefaultProfileId,
            SiemensAccessProfilePlugin.DefaultProfileVersion,
            AccessProfileConfigurationPayload.Empty,
            @"D:\Projects\Sample.zap21");

        var result = parser.Parse(config, SiemensProfileConfigurationValidationMode.Strict);

        Assert.True(result.IsValid);
        Assert.Equal(".zap21", result.Configuration?.ProjectSourceExtension);
        Assert.Null(result.Configuration?.Tia.ArchiveWorkDirectory);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Parser_keeps_configured_archive_work_base_directory_for_zap21()
    {
        var parser = new SiemensProfileConfigurationParser();
        var config = new AccessProfileConfig(
            SiemensAccessProfilePlugin.DefaultProfileId,
            SiemensAccessProfilePlugin.DefaultProfileVersion,
            new AccessProfileConfigurationPayload(
                """
                {
                  "tia": {
                    "archiveWorkDirectory": "%TEMP%\\PLCSIM.UnitTest\\archive-work"
                  }
                }
                """),
            @"D:\Projects\Sample.zap21");

        var result = parser.Parse(config, SiemensProfileConfigurationValidationMode.Strict);

        Assert.True(result.IsValid);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PLCSIM.UnitTest", "archive-work")),
            result.Configuration?.Tia.ArchiveWorkDirectory);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Partial_parser_keeps_zap21_without_blocking_import()
    {
        var parser = new SiemensProfileConfigurationParser();
        var config = new AccessProfileConfig(
            SiemensAccessProfilePlugin.DefaultProfileId,
            SiemensAccessProfilePlugin.DefaultProfileVersion,
            AccessProfileConfigurationPayload.Empty,
            @"D:\Projects\Sample.zap21");

        var result = parser.Parse(config, SiemensProfileConfigurationValidationMode.Partial);

        Assert.True(result.IsValid);
        Assert.Equal(".zap21", result.Configuration?.ProjectSourceExtension);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Parser_accepts_siemens_fake_project_source()
    {
        var parser = new SiemensProfileConfigurationParser();
        var config = new AccessProfileConfig(
            SiemensAccessProfilePlugin.DefaultProfileId,
            SiemensAccessProfilePlugin.DefaultProfileVersion,
            new AccessProfileConfigurationPayload(
                """
                {
                  "plcRef": { "id": "siemens-fake-plc" },
                  "runtimeRef": { "id": "siemens-fake-runtime" }
                }
                """),
            @"D:\Projects\siemens-fake-project.apf");

        var result = parser.Parse(config, SiemensProfileConfigurationValidationMode.Strict);

        Assert.True(result.IsValid);
        Assert.Equal(".apf", result.Configuration?.ProjectSourceExtension);
        Assert.Equal("siemens-fake-plc", result.Configuration?.PlcRef.Id);
        Assert.Equal("siemens-fake-runtime", result.Configuration?.RuntimeRef.Id);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ValidateConfig_rejects_invalid_siemens_values()
    {
        var plugin = new SiemensAccessProfilePlugin();

        var result = plugin.ValidateConfig(CreateConfig(
            SiemensAccessProfilePlugin.DefaultProfileId,
            SiemensAccessProfilePlugin.DefaultProfileVersion,
            """
            {
              "runtimeRef": "UnitTestRuntime"
            }
            """));

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiemensProfile.Config.runtimeRef.InvalidType");
    }

}
