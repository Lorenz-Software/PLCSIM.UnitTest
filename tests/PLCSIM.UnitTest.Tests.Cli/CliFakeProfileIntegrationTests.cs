using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PLCSIM.UnitTest.App.Cli;
using PLCSIM.UnitTest.App.Cli.Commands;
using PLCSIM.UnitTest.Common.AccessProfiles;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Cli;

public sealed class CliFakeProfileIntegrationTests : IDisposable
{
    [Theory]
    [InlineData("always", false)]
    [InlineData("failure", false)]
    [InlineData("always", true)]
    public async Task Optional_analysis_preserves_success_and_junit(string trigger, bool blockedOutput)
    {
        var console = new RecordingConsole();
        var cli = new CliCommandLine(console, _ => CliApplicationHost.Create(options));
        Assert.Equal((int)CliExitCode.Success, await cli.InvokeAsync(["register-profiles"]));
        console.Clear();
        var config = WriteRunConfiguration("analysis.json");
        var junit = Path.Combine(temporaryDirectory, "analysis.xml");
        var directory = Path.Combine(temporaryDirectory, "analysis");
        if (blockedOutput) await File.WriteAllTextAsync(directory, "keep");

        var result = await cli.InvokeAsync(["run", "-f", config, "-o", junit,
            "--analysis-output", directory, "--analysis-on", trigger]);

        Assert.Equal((int)CliExitCode.Success, result);
        Assert.True(File.Exists(junit));
        if (blockedOutput)
        {
            Assert.Contains(console.Errors, line => line.Contains("Evidence analysis export failed"));
            Assert.Equal("keep", await File.ReadAllTextAsync(directory));
        }
        else if (trigger == "always")
        {
            var path = Assert.Single(Directory.GetFiles(directory, "*.zip", SearchOption.AllDirectories));
            Assert.Equal("run.zip", Path.GetFileName(path));
            Assert.Contains(console.Output, line => line.Contains(path));
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            Assert.NotNull(archive.GetEntry("hierarchy.json"));
            Assert.NotNull(archive.GetEntry("events.ndjson"));
        }
        else
        {
            Assert.False(Directory.Exists(directory));
            Assert.DoesNotContain(console.Output, line => line.Contains("Evidence analysis package exported"));
        }
    }

    private readonly string temporaryDirectory;
    private readonly CliApplicationOptions options;

    public CliFakeProfileIntegrationTests()
    {
        temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.CliFakeProfileIntegrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        options = CliApplicationOptions.CreateDefault() with
        {
            ProfileDiscoveryDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "integration-profiles"),
            ProfileRegistryPath = Path.Combine(
                temporaryDirectory,
                "application-profile-registry.json"),
            LogDirectory = Path.Combine(temporaryDirectory, "logs")
        };
    }

    [Fact]
    public async Task Cli_commands_integrate_with_deployed_fake_profile_package()
    {
        Assert.True(
            File.Exists(Path.Combine(
                options.ProfileDiscoveryDirectory,
                "fake",
                "manifest.json")),
            "The Fake profile integration package was not deployed.");

        var console = new RecordingConsole();
        var commandLine = new CliCommandLine(
            console,
            _ => CliApplicationHost.Create(options));

        var registerExitCode = await commandLine.InvokeAsync(["register-profiles"]);

        Assert.Equal((int)CliExitCode.Success, registerExitCode);
        Assert.True(File.Exists(options.ProfileRegistryPath));
        Assert.Contains(console.Output, line =>
            line.Contains("fake 1.0.0", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(console.Errors);

        console.Clear();
        var listExitCode = await commandLine.InvokeAsync(["list-profiles"]);

        Assert.Equal((int)CliExitCode.Success, listExitCode);
        Assert.Contains(console.Output, line =>
            line.StartsWith("fake 1.0.0 | Fake Access Profile | Registered", StringComparison.Ordinal));
        Assert.Empty(console.Errors);

        var validConfiguration = WriteConfiguration("valid.json", "fake");
        console.Clear();
        var validExitCode = await commandLine.InvokeAsync(
            ["check-config", "--file", validConfiguration]);

        Assert.Equal((int)CliExitCode.Success, validExitCode);
        Assert.Contains(console.Output, line =>
            line.Contains("Configuration is valid", StringComparison.Ordinal));
        Assert.Empty(console.Errors);

        var unavailableConfiguration = WriteConfiguration("unavailable.json", "missing");
        console.Clear();
        var unavailableExitCode = await commandLine.InvokeAsync(
            ["check-config", "--file", unavailableConfiguration]);

        Assert.Equal((int)CliExitCode.ProfileUnavailable, unavailableExitCode);
        Assert.Contains(console.Errors, line =>
            line.Contains("AccessProfile.Unavailable", StringComparison.Ordinal));

        var invalidConfiguration = Path.Combine(temporaryDirectory, "invalid.json");
        File.WriteAllText(invalidConfiguration, "{");
        console.Clear();
        var invalidExitCode = await commandLine.InvokeAsync(
            ["check-config", "--file", invalidConfiguration]);

        Assert.Equal((int)CliExitCode.InvalidConfiguration, invalidExitCode);
        Assert.Contains(console.Errors, line =>
            line.Contains("Cli.Configuration.Json.Invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Check_config_and_run_require_explicit_registration_after_package_change()
    {
        var console = new RecordingConsole();
        var commandLine = new CliCommandLine(
            console,
            _ => CliApplicationHost.Create(options));
        Assert.Equal(
            (int)CliExitCode.Success,
            await commandLine.InvokeAsync(["register-profiles"]));

        var registry = JsonNode.Parse(await File.ReadAllTextAsync(options.ProfileRegistryPath))
            ?? throw new InvalidDataException("The test profile registry is empty.");
        registry["profiles"]![0]!["source"]!["fingerprint"] = "stale-test-fingerprint";
        await File.WriteAllTextAsync(options.ProfileRegistryPath, registry.ToJsonString());
        var configuration = WriteRunConfiguration("stale-registration.json");
        var output = Path.Combine(temporaryDirectory, "stale-registration.xml");

        console.Clear();
        var checkExitCode = await commandLine.InvokeAsync(
            ["check-config", "--file", configuration]);

        Assert.Equal((int)CliExitCode.ProfileUnavailable, checkExitCode);
        Assert.Contains(console.Errors, line =>
            line.Contains("Cli.Configuration.AccessProfile.RegistrationNotCurrent", StringComparison.Ordinal));
        Assert.Contains(console.Errors, line =>
            line.Contains("register-profiles", StringComparison.Ordinal));

        console.Clear();
        var runExitCode = await commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", output]);

        Assert.Equal((int)CliExitCode.ProfileUnavailable, runExitCode);
        Assert.False(File.Exists(output));
        Assert.Contains(console.Errors, line =>
            line.Contains("Cli.Configuration.AccessProfile.RegistrationNotCurrent", StringComparison.Ordinal));

        console.Clear();
        Assert.Equal(
            (int)CliExitCode.Success,
            await commandLine.InvokeAsync(["register-profiles"]));
        console.Clear();
        Assert.Equal(
            (int)CliExitCode.Success,
            await commandLine.InvokeAsync(["check-config", "--file", configuration]));
    }

    [Fact]
    public async Task Run_integrates_with_deployed_fake_profile_package_and_writes_junit()
    {
        var console = new RecordingConsole();
        var commandLine = new CliCommandLine(
            console,
            _ => CliApplicationHost.Create(options));
        Assert.Equal(
            (int)CliExitCode.Success,
            await commandLine.InvokeAsync(["register-profiles"]));
        console.Clear();
        var configuration = WriteRunConfiguration("run-success.json");
        var output = Path.Combine(temporaryDirectory, "run-success.xml");
        var originalConfiguration = await File.ReadAllBytesAsync(configuration);
        await File.WriteAllTextAsync(output, "stale artifact");

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", output]);

        Assert.True(
            exitCode == (int)CliExitCode.Success,
            $"Expected success but got {exitCode}: {string.Join(" | ", console.Errors)}");
        Assert.True(File.Exists(output));
        var junit = await File.ReadAllTextAsync(output);
        Assert.Contains("<testsuites", junit, StringComparison.Ordinal);
        Assert.Contains("failures=\"0\"", junit, StringComparison.Ordinal);
        Assert.DoesNotContain("stale artifact", junit, StringComparison.Ordinal);
        Assert.Equal(originalConfiguration, await File.ReadAllBytesAsync(configuration));
        Assert.Contains(console.Output, line =>
            line.Contains("Outcome=Success", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("case")]
    [InlineData("relative-output")]
    [InlineData("relative-input")]
    [InlineData("dot-segments")]
    public async Task Run_rejects_output_matching_configuration_without_changing_input(string pathVariant)
    {
        if (pathVariant == "case" && !OperatingSystem.IsWindows())
        {
            return;
        }

        var console = new RecordingConsole();
        var commandLine = new CliCommandLine(console, _ => CliApplicationHost.Create(options));
        Assert.Equal(
            (int)CliExitCode.Success,
            await commandLine.InvokeAsync(["register-profiles"]));
        console.Clear();
        var configuration = WriteRunConfiguration("protected-input.json");
        var originalConfiguration = await File.ReadAllBytesAsync(configuration);
        var input = pathVariant == "relative-input"
            ? Path.GetRelativePath(Environment.CurrentDirectory, configuration)
            : configuration;
        var output = pathVariant switch
        {
            "case" => configuration.ToUpperInvariant(),
            "relative-output" => Path.GetRelativePath(Environment.CurrentDirectory, configuration),
            "dot-segments" => Path.Combine(temporaryDirectory, "unused", "..", "protected-input.json"),
            _ => configuration
        };

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", input, "--output", output]);

        Assert.Equal((int)CliExitCode.ArtifactFailed, exitCode);
        Assert.Equal(originalConfiguration, await File.ReadAllBytesAsync(configuration));
        Assert.Contains(console.Errors, line =>
            line.Contains("output path must differ from the configuration file path", StringComparison.Ordinal));
        Assert.DoesNotContain(console.Output, line =>
            line.Contains("Validating configuration", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(temporaryDirectory, "unused")));
    }

    [Fact]
    public async Task Run_with_dirty_fake_event_buffer_fails_without_reset_or_artifact()
    {
        var console = new RecordingConsole();
        var commandLine = new CliCommandLine(
            console,
            _ => CliApplicationHost.Create(options));
        Assert.Equal(
            (int)CliExitCode.Success,
            await commandLine.InvokeAsync(["register-profiles"]));
        console.Clear();
        var configuration = WriteRunConfiguration("run-dirty-buffer.json", initialLostEventCount: 2);
        var output = Path.Combine(temporaryDirectory, "run-dirty-buffer.xml");
        await File.WriteAllTextAsync(output, "stale artifact");

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", output]);

        Assert.Equal((int)CliExitCode.ExecutionFailed, exitCode);
        Assert.False(File.Exists(output));
        Assert.Contains(console.Errors, line =>
            line.Contains("Cli.Run.EventBufferHasLostEvents", StringComparison.Ordinal));
        Assert.Contains(console.Errors, line =>
            line.Contains("does not reset", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(console.Output, line =>
            line.Contains("Artifact=NotWritten", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_fails_as_invalid_configuration_when_required_safety_password_is_missing()
    {
        var console = new RecordingConsole();
        var commandLine = new CliCommandLine(
            console,
            _ => CliApplicationHost.Create(options));
        Assert.Equal(
            (int)CliExitCode.Success,
            await commandLine.InvokeAsync(["register-profiles"]));
        console.Clear();
        var configuration = WriteRunConfiguration(
            "run-safety-password-missing.json",
            safetyAuthorizationRequirement: "passwordRequired");
        var output = Path.Combine(temporaryDirectory, "run-safety-password-missing.xml");

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", output]);

        Assert.Equal((int)CliExitCode.InvalidConfiguration, exitCode);
        Assert.False(File.Exists(output));
        Assert.Contains(console.Errors, line =>
            line.Contains("project.authorization.safetyPassword", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_uses_configured_safety_password_without_exposing_it()
    {
        const string safetyPassword = "cli-safety-secret";
        var console = new RecordingConsole();
        var commandLine = new CliCommandLine(
            console,
            _ => CliApplicationHost.Create(options));
        Assert.Equal(
            (int)CliExitCode.Success,
            await commandLine.InvokeAsync(["register-profiles"]));
        console.Clear();
        var configuration = WriteRunConfiguration(
            "run-safety-password.json",
            safetyAuthorizationRequirement: "passwordRequired",
            safetyPassword: safetyPassword);
        var output = Path.Combine(temporaryDirectory, "run-safety-password.xml");

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", output]);

        Assert.Equal((int)CliExitCode.Success, exitCode);
        Assert.True(File.Exists(output));
        Assert.DoesNotContain(console.Output, line => line.Contains(safetyPassword, StringComparison.Ordinal));
        Assert.DoesNotContain(console.Errors, line => line.Contains(safetyPassword, StringComparison.Ordinal));
        Assert.Contains(console.Output, line =>
            line.Contains("Authorizing Safety program", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_stops_on_unsupported_safety_program_without_build_or_artifact()
    {
        const string safetyPassword = "unused-cli-safety-secret";
        var console = new RecordingConsole();
        var commandLine = new CliCommandLine(
            console,
            _ => CliApplicationHost.Create(options));
        Assert.Equal(
            (int)CliExitCode.Success,
            await commandLine.InvokeAsync(["register-profiles"]));
        console.Clear();
        var configuration = WriteRunConfiguration(
            "run-safety-unsupported.json",
            safetyAuthorizationRequirement: "notSupported",
            safetyPassword: safetyPassword);
        var output = Path.Combine(temporaryDirectory, "run-safety-unsupported.xml");

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", output]);

        Assert.Equal((int)CliExitCode.EnvironmentUnavailable, exitCode);
        Assert.False(File.Exists(output));
        Assert.DoesNotContain(console.Output, line => line.Contains(safetyPassword, StringComparison.Ordinal));
        Assert.DoesNotContain(console.Errors, line => line.Contains(safetyPassword, StringComparison.Ordinal));
        Assert.Contains(console.Errors, line =>
            line.Contains("does not support Safety-program handling", StringComparison.Ordinal));
        Assert.DoesNotContain(console.Output, line =>
            line.Contains("Building and transferring project", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelled_fake_profile_registration_cleans_up_and_returns_cancelled()
    {
        var console = new RecordingConsole();
        var commandLine = new CliCommandLine(
            console,
            _ => CliApplicationHost.Create(options));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exitCode = await commandLine.InvokeAsync(
            ["register-profiles"],
            cancellation.Token);

        Assert.Equal((int)CliExitCode.Cancelled, exitCode);
        Assert.Contains(console.Errors, line =>
            line.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private string WriteConfiguration(string fileName, string profileId)
    {
        var path = Path.Combine(temporaryDirectory, fileName);
        File.WriteAllText(
            path,
            $$"""
            {
              "version": 1,
              "project": { "source": "fake-project.json" },
              "accessProfile": {
                "id": "{{profileId}}",
                "version": "1.0.0",
                "values": {}
              }
            }
            """);
        return path;
    }

    [Theory]
    [InlineData(null, "UseExisting", true)]
    [InlineData(null, "CreateOwned", true)]
    [InlineData(null, null, true)]
    [InlineData("fake-runtime-target", "UseExisting", true)]
    [InlineData("missing-target", "UseExisting", false)]
    [InlineData("fake-runtime-target", "CreateOwned", false)]
    [InlineData("fake-runtime-target", null, false)]
    public async Task Run_preserves_target_and_mode_through_the_real_cli_flow(
        string? target, string? mode, bool success)
    {
        var commandLine = new CliCommandLine(new RecordingConsole(), _ => CliApplicationHost.Create(options));
        Assert.Equal((int)CliExitCode.Success, await commandLine.InvokeAsync(["register-profiles"]));
        var path = WriteRunConfiguration("target-selection.json");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        config["version"] = 2;
        if (mode is not null) config["runtime"] = new JsonObject { ["provisioningMode"] = mode };
        if (target is not null)
            config["accessProfile"]!["values"]!["runtimeRef"] = new JsonObject { ["id"] = target };
        await File.WriteAllTextAsync(path, config.ToJsonString());
        var original = await File.ReadAllTextAsync(path);
        var output = Path.Combine(temporaryDirectory, "target-selection.xml");
        await using var host = CliApplicationHost.Create(options);
        var result = await host.GetRequiredService<ICliFullRunService>().RunAsync(path, output, null, CancellationToken.None);
        Assert.Equal(success ? CliFullRunState.Completed : CliFullRunState.InvalidConfiguration, result.State);
        Assert.Equal(success, File.Exists(output));
        Assert.True(result.Release!.IsSuccessful);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        if (success)
            Assert.Equal(mode == "UseExisting" ? RuntimeProvisioningMode.UseExisting : RuntimeProvisioningMode.CreateOwned,
                result.Publication!.Status.RuntimeProvisioningMode);
        else
            Assert.NotNull(result.Message);
    }

    private string WriteRunConfiguration(
        string fileName,
        ushort initialLostEventCount = 0,
        string? safetyAuthorizationRequirement = null,
        string? safetyPassword = null)
    {
        var path = Path.Combine(temporaryDirectory, fileName);
        var authorization = safetyPassword is null
            ? string.Empty
            : $", \"authorization\": {{ \"safetyPassword\": \"{safetyPassword}\" }}";
        var safetyRequirement = safetyAuthorizationRequirement is null
            ? string.Empty
            : $", \"safetyAuthorizationRequirement\": \"{safetyAuthorizationRequirement}\"";
        File.WriteAllText(
            path,
            $$"""
            {
              "version": 1,
              "project": { "source": "fake-project.json"{{authorization}} },
              "accessProfile": {
                "id": "fake",
                "version": "1.0.0",
                "values": {
                  "plcRef": { "id": "fake-project-target" },
                  "testSettings": {
                    "initialLostEventCount": {{initialLostEventCount}}{{safetyRequirement}}
                  }
                }
              },
              "execution": {
                "timeouts": {
                  "projectPrepare": "00:00:02",
                  "buildTransfer": "00:00:02",
                  "runtimePrepare": "00:00:02",
                  "plcCommand": "00:00:02",
                  "plcStartReaction": "00:00:02",
                  "plcStopReaction": "00:00:02",
                  "communicationPrepare": "00:00:02",
                  "communicationRevalidation": "00:00:02",
                  "unitTestCommand": "00:00:02",
                  "unitTestCommandReaction": "00:00:02",
                  "testRun": "00:00:03",
                  "finalEvent": "00:00:02"
                }
              }
            }
            """);
        return path;
    }

    private sealed class RecordingConsole : ICliConsole
    {
        public List<string> Output { get; } = [];

        public List<string> Errors { get; } = [];

        public void WriteOutput(string message)
        {
            Output.Add(message);
        }

        public void WriteError(string message)
        {
            Errors.Add(message);
        }

        public void Clear()
        {
            Output.Clear();
            Errors.Clear();
        }
    }
}
