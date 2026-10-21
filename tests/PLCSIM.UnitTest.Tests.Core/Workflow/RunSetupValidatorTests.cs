using System.Text.Json;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Workflow;

public sealed class RunSetupValidatorTests
{
    [Fact]
    public void Create_from_configuration_applies_defaults_and_preserves_profile_values()
    {
        var validator = CreateValidator();
        var configuration = new RunConfigurationFile(
            1,
            new ProjectConfiguration("sample.ap"),
            new AccessProfileConfiguration(
                "fake",
                FakeAccessProfilePlugin.DefaultProfileVersion,
                JsonSerializer.Deserialize<JsonElement>("{\"project\":\"sample\"}")));

        var setup = validator.CreateFromConfiguration(configuration);

        Assert.Equal("fake", setup.AccessProfile.ProfileId);
        Assert.Equal(FakeAccessProfilePlugin.DefaultProfileVersion, setup.AccessProfile.ProfileVersion);
        Assert.Equal("sample.ap", setup.AccessProfile.ProjectSource);
        Assert.Equal("{\"project\":\"sample\"}", setup.AccessProfile.Values.Json);
        Assert.Equal(RuntimeProvisioningMode.CreateOwned, setup.RuntimeProvisioningMode);
        Assert.Null(setup.RuntimeTarget);
        Assert.Equal("artifacts", setup.OutputPolicy.Directory);
        Assert.True(setup.OutputPolicy.TestResult);
        Assert.Equal(TimeSpan.FromMinutes(1), setup.Execution.Timeouts.ProjectPrepare);
        Assert.Equal(TimeSpan.FromMinutes(10), setup.Execution.Timeouts.BuildTransfer);
        Assert.Equal(TimeSpan.FromSeconds(10), setup.Execution.Timeouts.PlcCommand);
        Assert.Equal(TimeSpan.FromSeconds(30), setup.Execution.Timeouts.PlcStartReaction);
        Assert.Equal(TimeSpan.FromSeconds(30), setup.Execution.Timeouts.PlcStopReaction);
        Assert.Equal(TimeSpan.FromMinutes(2), setup.Execution.Timeouts.TestRun);
        Assert.Equal(TimeSpan.FromSeconds(10), setup.Execution.Timeouts.FinalEvent);
    }

    [Fact]
    public void Draft_defers_only_target_resolution_and_final_validation_still_requires_it()
    {
        var validator = CreateValidator();
        var configuration = new RunConfigurationFile(2, new ProjectConfiguration("sample.ap"),
            new AccessProfileConfiguration("fake"), Runtime: new RuntimeConfiguration(RuntimeProvisioningMode.UseExisting));
        var setup = validator.CreateFromConfiguration(configuration);
        Assert.Equal(RuntimeProvisioningMode.UseExisting, setup.RuntimeProvisioningMode);
        Assert.True(validator.ValidateDraft(setup).IsValid);
        Assert.False(validator.Validate(setup).IsValid);
        Assert.False(validator.ValidateDraft(setup with { OutputPolicy = new OutputPolicy("", true) }).IsValid);
        Assert.False(validator.ValidateDraft(setup with { RuntimeProvisioningMode = (RuntimeProvisioningMode)99 }).IsValid);
    }

    [Fact]
    public void Validate_accepts_minimal_valid_setup()
    {
        var validator = CreateValidator();
        var setup = new RunSetup(
            FakeAccessProfileFactory.CreateDefaultConfig(),
            RuntimeProvisioningMode.CreateOwned,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()));

        var result = validator.Validate(setup);

        Assert.True(result.IsValid);
        Assert.Equal(
            RunConfigurationDefaults.CreateDefaultCommunicationTimeoutPolicy(),
            result.Setup?.Execution.Timeouts.ToCommunicationTimeoutPolicy());
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Validate_accepts_disabled_communication_revalidation_timeout()
    {
        var timeouts = RunConfigurationDefaults.CreateDefaultTimeoutPolicy() with
        {
            CommunicationPrepare = TimeSpan.FromSeconds(15),
            CommunicationRevalidation = TimeSpan.Zero
        };
        var setup = new RunSetup(
            FakeAccessProfileFactory.CreateDefaultConfig(),
            RuntimeProvisioningMode.CreateOwned,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(timeouts));

        var result = CreateValidator().Validate(setup);

        Assert.True(result.IsValid);
        Assert.Equal(TimeSpan.FromSeconds(15), result.Setup?.Execution.Timeouts.CommunicationPrepare);
        Assert.Equal(TimeSpan.Zero, result.Setup?.Execution.Timeouts.CommunicationRevalidation);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(0, 1, "RunSetup.Timeout.CommunicationPrepare.Invalid")]
    [InlineData(1, -1, "RunSetup.Timeout.CommunicationRevalidation.Invalid")]
    public void Validate_rejects_invalid_communication_timeouts(
        int prepareSeconds,
        int revalidationSeconds,
        string expectedDiagnosticCode)
    {
        var timeouts = RunConfigurationDefaults.CreateDefaultTimeoutPolicy() with
        {
            CommunicationPrepare = TimeSpan.FromSeconds(prepareSeconds),
            CommunicationRevalidation = TimeSpan.FromSeconds(revalidationSeconds)
        };
        var setup = new RunSetup(
            FakeAccessProfileFactory.CreateDefaultConfig(),
            RuntimeProvisioningMode.CreateOwned,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(timeouts));

        var result = CreateValidator().Validate(setup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedDiagnosticCode);
    }

    [Fact]
    public void Validate_preserves_selected_target_references()
    {
        var validator = CreateValidator();
        var projectTarget = new ProjectTargetRef(new ProjectTargetId("project-plc-1"), "PLC_1", "PLC_1");
        var runtimeTarget = new RuntimeTargetRef(new RuntimeTargetId("runtime-plc-1"), "Runtime PLC_1", "PLC_1");
        var setup = new RunSetup(
            FakeAccessProfileFactory.CreateDefaultConfig(),
            RuntimeProvisioningMode.UseExisting,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()),
            projectTarget,
            runtimeTarget);

        var result = validator.Validate(setup);

        Assert.True(result.IsValid);
        Assert.Equal(projectTarget, result.Setup?.ProjectTarget);
        Assert.Equal(runtimeTarget, result.Setup?.RuntimeTarget);
    }

    [Fact]
    public void Validate_requires_a_runtime_target_for_existing_runtime_provisioning()
    {
        var setup = new RunSetup(
            FakeAccessProfileFactory.CreateDefaultConfig(),
            RuntimeProvisioningMode.UseExisting,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()));

        var result = CreateValidator().Validate(setup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RunSetup.RuntimeTarget.Missing");
    }

    [Fact]
    public void Validate_rejects_a_runtime_target_for_owned_runtime_provisioning()
    {
        var setup = new RunSetup(
            FakeAccessProfileFactory.CreateDefaultConfig(),
            RuntimeProvisioningMode.CreateOwned,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()),
            RuntimeTarget: new RuntimeTargetRef(
                new RuntimeTargetId("runtime-plc-1"),
                "Runtime PLC_1",
                "PLC_1"));

        var result = CreateValidator().Validate(setup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RunSetup.RuntimeTarget.Unexpected");
    }

    [Fact]
    public void Validate_rejects_an_unknown_runtime_provisioning_mode()
    {
        var setup = new RunSetup(
            FakeAccessProfileFactory.CreateDefaultConfig(),
            (RuntimeProvisioningMode)int.MaxValue,
            RunConfigurationDefaults.CreateDefaultOutputPolicy(),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy()));

        var result = CreateValidator().Validate(setup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RunSetup.RuntimeProvisioningMode.Unsupported");
    }

    [Fact]
    public void Validate_reports_common_setup_errors_without_profile_specific_interpretation()
    {
        var timeProvider = FakeTime.Create();
        var validator = CreateValidator(timeProvider);
        var setup = new RunSetup(
            new AccessProfileConfig(
                "",
                null,
                new AccessProfileConfigurationPayload("{\"siemensSpecific\":\"unchanged\"}"),
                ""),
            RuntimeProvisioningMode.UseExisting,
            new OutputPolicy("", TestResult: true),
            new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy() with
            {
                PlcCommand = TimeSpan.Zero,
                PlcStartReaction = TimeSpan.FromSeconds(-1)
            }));

        var result = validator.Validate(setup);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RunSetup.AccessProfileId.Missing");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RunSetup.ProjectSource.Missing");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RunSetup.OutputDirectory.Missing");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RunSetup.Timeout.PlcCommand.Invalid");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RunSetup.Timeout.PlcStartReaction.Invalid");
        Assert.Equal("{\"siemensSpecific\":\"unchanged\"}", result.Setup?.AccessProfile.Values.Json);
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(timeProvider.GetUtcNow(), diagnostic.Timestamp));
    }

    private static RunSetupValidator CreateValidator(TimeProvider? timeProvider = null)
    {
        return new RunSetupValidator(timeProvider ?? FakeTime.Create());
    }
}
