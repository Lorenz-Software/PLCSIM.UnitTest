using System.Text.Json;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.CommonContracts;

public sealed class ConfigurationContractTests
{
    [Fact]
    public void Provisioning_rename_preserves_values_without_legacy_alias()
    {
        Assert.Equal(0, (int)RuntimeProvisioningMode.UseExisting);
        Assert.Equal(1, (int)RuntimeProvisioningMode.CreateOwned);
        Assert.Equal(new[] { "UseExisting", "CreateOwned" }, Enum.GetNames<RuntimeProvisioningMode>());
        Assert.False(Enum.TryParse<RuntimeProvisioningMode>("RequireExisting", out _));
        Assert.Equal(new Version(2, 0, 0, 0), typeof(RuntimeProvisioningMode).Assembly.GetName().Version);
    }

    [Fact]
    public void Runtime_intent_round_trips_as_a_name()
    {
        var configuration = new RunConfigurationFile(2, new ProjectConfiguration("sample.ap"),
            new AccessProfileConfiguration("fake"), Runtime: new RuntimeConfiguration(RuntimeProvisioningMode.UseExisting));
        var json = JsonSerializer.Serialize(configuration);
        Assert.Contains("\"ProvisioningMode\":\"UseExisting\"", json);
        Assert.Equal(configuration.Runtime, JsonSerializer.Deserialize<RunConfigurationFile>(json)!.Runtime);
    }

    [Fact]
    public void Defaults_match_first_configuration_contract()
    {
        var timeouts = RunConfigurationDefaults.CreateDefaultTimeoutPolicy();
        var output = RunConfigurationDefaults.CreateDefaultOutputPolicy();

        Assert.Equal(2, RunConfigurationDefaults.CurrentVersion);
        Assert.Equal(TimeSpan.FromMinutes(1), timeouts.ProjectPrepare);
        Assert.Equal(TimeSpan.FromMinutes(10), timeouts.BuildTransfer);
        Assert.Equal(TimeSpan.FromMinutes(1), timeouts.RuntimePrepare);
        Assert.Equal(TimeSpan.FromSeconds(10), timeouts.PlcCommand);
        Assert.Equal(TimeSpan.FromSeconds(30), timeouts.PlcStartReaction);
        Assert.Equal(TimeSpan.FromSeconds(30), timeouts.PlcStopReaction);
        Assert.Equal(TimeSpan.FromSeconds(30), timeouts.CommunicationPrepare);
        Assert.Equal(TimeSpan.FromSeconds(30), timeouts.CommunicationRevalidation);
        Assert.Equal(TimeSpan.FromSeconds(3), timeouts.UnitTestCommand);
        Assert.Equal(TimeSpan.FromSeconds(10), timeouts.UnitTestCommandReaction);
        Assert.Equal(TimeSpan.FromMinutes(2), timeouts.TestRun);
        Assert.Equal(TimeSpan.FromSeconds(10), timeouts.FinalEvent);
        Assert.Equal("artifacts", output.Directory);
        Assert.True(output.TestResult);
    }

    [Fact]
    public void Configuration_file_shape_round_trips_through_system_text_json()
    {
        var configuration = new RunConfigurationFile(
            1,
            new ProjectConfiguration(
                "sample.ap21",
                new ProjectAuthorizationConfiguration("safety-secret")),
            new AccessProfileConfiguration(
                "siemens",
                "1.2.3",
                JsonSerializer.Deserialize<JsonElement>("{\"plcRef\":{\"id\":\"PLC_1\"}}")),
            new ExecutionConfiguration(new TimeoutConfiguration(
                ProjectPrepare: TimeSpan.FromSeconds(40),
                PlcCommand: TimeSpan.FromSeconds(12),
                CommunicationRevalidation: TimeSpan.Zero,
                TestRun: TimeSpan.FromMinutes(2),
                FinalEvent: TimeSpan.FromSeconds(10))),
            new OutputConfiguration("artifacts", true));

        var json = JsonSerializer.Serialize(configuration);
        var deserialized = JsonSerializer.Deserialize<RunConfigurationFile>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(1, deserialized.Version);
        Assert.Equal("sample.ap21", deserialized.Project.Source);
        Assert.Equal("safety-secret", deserialized.Project.Authorization?.SafetyPassword);
        Assert.DoesNotContain("safety-secret", deserialized.Project.Authorization?.ToString());
        Assert.Equal("siemens", deserialized.AccessProfile.Id);
        Assert.Equal("1.2.3", deserialized.AccessProfile.Version);
        Assert.Equal("PLC_1", deserialized.AccessProfile.Values?.GetProperty("plcRef").GetProperty("id").GetString());
        Assert.Equal(TimeSpan.FromSeconds(40), deserialized.Execution?.Timeouts?.ProjectPrepare);
        Assert.Equal(TimeSpan.FromSeconds(12), deserialized.Execution?.Timeouts?.PlcCommand);
        Assert.Equal(TimeSpan.Zero, deserialized.Execution?.Timeouts?.CommunicationRevalidation);
        Assert.Equal("artifacts", deserialized.Output?.Directory);
        Assert.True(deserialized.Output?.TestResult);
    }
}
