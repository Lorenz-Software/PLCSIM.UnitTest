using System.Text.Json;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.App.Cli.Configuration;
using PLCSIM.UnitTest.Core.Workflow;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Cli;

public sealed class CliConfigurationFileLoadServiceTests : IDisposable
{
    private readonly string temporaryDirectory;

    public CliConfigurationFileLoadServiceTests()
    {
        temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.CliConfigurationFileLoadServiceTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
    }

    [Fact]
    public async Task Load_async_accepts_valid_configuration_and_preserves_profile_payload()
    {
        var path = WriteConfiguration(
            """
            {
              "version": 1,
              "project": { "source": "fake-project.ap" },
              "accessProfile": {
                "id": "fake",
                "version": "1.0.0",
                "values": { "custom": { "enabled": true } }
              },
              "execution": {
                "timeouts": { "testRun": "00:01:30" }
              }
            }
            """);

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Configuration);
        Assert.NotNull(result.Setup);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("fake", result.Setup.AccessProfile.ProfileId);
        Assert.Equal("fake-project.ap", result.Setup.AccessProfile.ProjectSource);
        Assert.Equal(TimeSpan.FromSeconds(90), result.Setup.Execution.Timeouts.TestRun);
        using var values = JsonDocument.Parse(result.Setup.AccessProfile.Values.Json);
        Assert.True(values.RootElement.GetProperty("custom").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Load_async_applies_shared_defaults()
    {
        var path = WriteConfiguration(
            """
            {
              "version": 1,
              "project": { "source": "fake-project.ap" },
              "accessProfile": { "id": "fake" }
            }
            """);

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("artifacts", result.Setup!.OutputPolicy.Directory);
        Assert.Equal(TimeSpan.FromMinutes(2), result.Setup.Execution.Timeouts.TestRun);
    }

    [Fact]
    public async Task Load_async_preserves_configured_safety_password_for_cli_execution()
    {
        var path = WriteConfiguration(
            """
            {
              "version": 1,
              "project": {
                "source": "sample.ap21",
                "authorization": {
                  "safetyPassword": "cli-safety-secret"
                }
              },
              "accessProfile": { "id": "siemens" }
            }
            """);

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(
            "cli-safety-secret",
            result.Configuration?.Project.Authorization?.SafetyPassword);
        Assert.DoesNotContain(
            "cli-safety-secret",
            result.Configuration?.Project.Authorization?.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_async_returns_cli_diagnostic_for_missing_file()
    {
        var path = Path.Combine(temporaryDirectory, "missing.config.json");

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Null(result.Setup);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("Cli.Configuration.File.NotFound", diagnostic.Code);
        Assert.Equal(path, diagnostic.EvidenceReference?.Reference);
    }

    [Fact]
    public async Task Load_async_returns_cli_diagnostic_for_invalid_json()
    {
        var path = WriteConfiguration("{");

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "Cli.Configuration.Json.Invalid");
    }

    [Fact]
    public async Task Load_async_rejects_invalid_shape_before_setup_projection()
    {
        var path = WriteConfiguration(
            """
            {
              "version": 99,
              "project": { "source": "" },
              "accessProfile": { "id": "", "values": "invalid" }
            }
            """);

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Null(result.Setup);
        Assert.Collection(
            result.Diagnostics,
            diagnostic => Assert.Equal("Cli.Configuration.Version.Unsupported", diagnostic.Code),
            diagnostic => Assert.Equal("Cli.Configuration.Project.Source.Missing", diagnostic.Code),
            diagnostic => Assert.Equal("Cli.Configuration.AccessProfile.Id.Missing", diagnostic.Code),
            diagnostic => Assert.Equal("Cli.Configuration.AccessProfile.Values.Invalid", diagnostic.Code));
    }

    [Fact]
    public async Task Load_async_returns_shared_run_setup_validation_diagnostics()
    {
        var path = WriteConfiguration(
            """
            {
              "version": 1,
              "project": { "source": "fake-project.ap" },
              "accessProfile": { "id": "fake" },
              "execution": {
                "timeouts": { "testRun": "00:00:00" }
              }
            }
            """);

        var result = await CreateService().LoadAsync(path, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "RunSetup.Timeout.TestRun.Invalid");
    }

    [Fact]
    public async Task Load_async_propagates_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService().LoadAsync("ignored.config.json", cancellation.Token));
    }

    [Theory]
    [InlineData(1, "", RuntimeProvisioningMode.CreateOwned)]
    [InlineData(2, "", RuntimeProvisioningMode.CreateOwned)]
    [InlineData(2, ", \"runtime\": {}", RuntimeProvisioningMode.CreateOwned)]
    [InlineData(2, ", \"runtime\": {\"provisioningMode\":\"CreateOwned\"}", RuntimeProvisioningMode.CreateOwned)]
    [InlineData(2, ", \"runtime\": {\"provisioningMode\":\"UseExisting\"}", RuntimeProvisioningMode.UseExisting)]
    public async Task Load_preserves_intent_before_target_resolution(int version, string runtime, RuntimeProvisioningMode expected)
    {
        var path = WriteConfiguration($$"""
            { "version": {{version}}, "project": {"source":"fake.ap"},
              "accessProfile": {"id":"fake", "values":{"runtimeRef":{"id":"explicit-target"} } } {{runtime}} }
            """);
        var result = await CreateService().LoadAsync(path, CancellationToken.None);
        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Setup!.RuntimeProvisioningMode);
        Assert.Contains("explicit-target", result.Setup.AccessProfile.Values.Json);
        Assert.Null(result.Setup.RuntimeTarget);
    }

    [Theory]
    [InlineData("\"RequireExisting\"")]
    [InlineData("\"unknown\"")]
    [InlineData("0")]
    [InlineData("\"0\"")]
    [InlineData("null")]
    public async Task Load_rejects_invalid_or_numeric_modes(string mode)
    {
        var path = WriteConfiguration($$"""
            {"version":2,"project":{"source":"fake.ap"},"accessProfile":{"id":"fake"},
             "runtime":{"provisioningMode":{{mode}} } }
            """);
        var result = await CreateService().LoadAsync(path, CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Null(result.Setup);
    }

    [Fact]
    public async Task Load_rejects_misspelled_runtime_field_instead_of_defaulting_to_creation()
    {
        var path = WriteConfiguration("""
            {"version":2,"project":{"source":"fake.ap"},"accessProfile":{"id":"fake"},
             "runtime":{"provisionMode":"UseExisting"}}
            """);
        var result = await CreateService().LoadAsync(path, CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Null(result.Setup);
    }

    [Fact]
    public async Task Load_rejects_runtime_section_in_legacy_schema()
    {
        var path = WriteConfiguration("""
            {"version":1,"project":{"source":"fake.ap"},"accessProfile":{"id":"fake"},
             "runtime":{"provisioningMode":"UseExisting"}}
            """);
        var result = await CreateService().LoadAsync(path, CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, item => item.Code == "Cli.Configuration.Runtime.RequiresVersion2");
    }

    [Theory]
    [InlineData("null", "Empty")]
    [InlineData("{\"version\":2,\"project\":null,\"accessProfile\":{\"id\":\"fake\"}}", "Project.Source.Missing")]
    public async Task Shared_shape_errors_keep_cli_diagnostic_context(string json, string code)
    {
        var path = WriteConfiguration(json);
        var result = await CreateService().LoadAsync(path, CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Null(result.Setup);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("Cli.Configuration." + code, diagnostic.Code);
        Assert.Equal(PLCSIM.UnitTest.Common.Diagnostics.DiagnosticSource.CliApplication, diagnostic.Source);
        Assert.Equal(path, diagnostic.EvidenceReference?.Reference);
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private string WriteConfiguration(string content)
    {
        var path = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.config.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static CliConfigurationFileLoadService CreateService()
    {
        var timeProvider = TimeProvider.System;
        return new CliConfigurationFileLoadService(
            new RunSetupValidator(timeProvider),
            timeProvider);
    }
}
