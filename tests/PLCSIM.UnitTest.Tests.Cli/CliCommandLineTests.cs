using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.App.Cli;
using PLCSIM.UnitTest.App.Cli.Commands;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Workflow;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Cli;

public sealed class CliCommandLineTests
{
    [Theory]
    [InlineData("always")]
    [InlineData("failure")]
    public async Task Analysis_options_reach_the_run_service(string trigger)
    {
        var console = new RecordingConsole();
        var service = new FakeFullRunService(new(CliFullRunState.Completed, null, null, [], null));
        var cli = new CliCommandLine(console, _ => CreateServices(fullRun: service));
        Assert.Equal(0, await cli.InvokeAsync(["run", "-f", "input.json", "-o", "junit.xml",
            "--analysis-output", "reports", "--analysis-on", trigger]));
        Assert.Equal("reports", service.RunOptions!.AnalysisOutputDirectory);
        Assert.Equal(trigger == "always" ? CliAnalysisTrigger.Always : CliAnalysisTrigger.Failure, service.RunOptions.AnalysisOn);
    }

    [Theory]
    [InlineData("--analysis-on", "always")]
    [InlineData("--analysis-on", "invalid")]
    [InlineData("--analysis-output", "")]
    public async Task Invalid_analysis_options_fail_before_execution(string option, string value)
    {
        var cli = new CliCommandLine(new RecordingConsole(), _ => throw new InvalidOperationException("Must not execute."));
        Assert.Equal((int)CliExitCode.InvalidArguments,
            await cli.InvokeAsync(["run", "-f", "input.json", "-o", "junit.xml", option, value]));
    }

    [Theory]
    [InlineData(CliExitCode.Success, 0)]
    [InlineData(CliExitCode.TestFailed, 1)]
    [InlineData(CliExitCode.InvalidArguments, 10)]
    [InlineData(CliExitCode.InvalidConfiguration, 11)]
    [InlineData(CliExitCode.ProfileUnavailable, 20)]
    [InlineData(CliExitCode.EnvironmentUnavailable, 21)]
    [InlineData(CliExitCode.ExecutionFailed, 30)]
    [InlineData(CliExitCode.TimedOut, 31)]
    [InlineData(CliExitCode.Cancelled, 32)]
    [InlineData(CliExitCode.ArtifactFailed, 40)]
    [InlineData(CliExitCode.CleanupFailed, 50)]
    [InlineData(CliExitCode.UnexpectedError, 99)]
    public void Exit_codes_use_agreed_category_ranges(CliExitCode code, int expected)
    {
        Assert.Equal(expected, (int)code);
    }

    [Fact]
    public async Task Missing_required_file_returns_invalid_arguments()
    {
        var console = new RecordingConsole();
        var commandLine = new CliCommandLine(console, _ =>
            throw new InvalidOperationException("Services must not be created."));

        var exitCode = await commandLine.InvokeAsync(["check-config"]);

        Assert.Equal((int)CliExitCode.InvalidArguments, exitCode);
        Assert.Contains(console.Errors, line => line.Contains("--file", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run --file unit-test.json")]
    [InlineData("run --output junit.xml")]
    public async Task Run_requires_configuration_and_output_paths(string command)
    {
        var console = new RecordingConsole();
        var commandLine = new CliCommandLine(console, _ =>
            throw new InvalidOperationException("Services must not be created."));

        var exitCode = await commandLine.InvokeAsync(command.Split(' '));

        Assert.Equal((int)CliExitCode.InvalidArguments, exitCode);
    }

    [Fact]
    public async Task Pre_cancelled_run_does_not_create_services_or_delete_existing_artifact()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var artifact = Path.Combine(directory, "result.xml");
        await File.WriteAllTextAsync(artifact, "existing artifact");
        try
        {
            var console = new RecordingConsole();
            var commandLine = new CliCommandLine(console, _ =>
                throw new InvalidOperationException("Services must not be created."));

            var exitCode = await commandLine.InvokeAsync(
                ["run", "--file", "unit-test.json", "--output", artifact],
                new CancellationToken(canceled: true));

            Assert.Equal((int)CliExitCode.Cancelled, exitCode);
            Assert.Equal("existing artifact", await File.ReadAllTextAsync(artifact));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Run_invokes_full_run_service_and_maps_completed_result()
    {
        var console = new RecordingConsole();
        var fullRun = new FakeFullRunService(new CliFullRunResult(
            CliFullRunState.Completed,
            null,
            null,
            [],
            "UnitTest run completed."));
        var commandLine = new CliCommandLine(console, _ =>
            CreateServices(fullRun: fullRun));

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", "unit-test.json", "--output", "result.xml"]);

        Assert.Equal((int)CliExitCode.Success, exitCode);
        Assert.Equal("unit-test.json", fullRun.ConfigurationPath);
        Assert.Equal("result.xml", fullRun.OutputPath);
        Assert.Equal(10, fullRun.RunOptions!.UnitTestReadyTimeoutSeconds);
        Assert.False(fullRun.RunOptions.ResetOnStartupTimeout);
        Assert.Contains("UnitTest run completed.", console.Output);
        Assert.Contains("Fake run progress.", console.Output);
    }

    [Fact]
    public async Task Run_routes_diagnostics_by_severity_and_labels_them()
    {
        var console = new RecordingConsole();
        var diagnostics = new[]
        {
            CreateDiagnostic("Test.Info", LogLevel.Information),
            CreateDiagnostic("Test.Warning", LogLevel.Warning),
            CreateDiagnostic("Test.Error", LogLevel.Error)
        };
        var fullRun = new FakeFullRunService(new CliFullRunResult(
            CliFullRunState.Completed,
            null,
            null,
            diagnostics,
            "UnitTest run completed."));
        var commandLine = new CliCommandLine(console, _ =>
            CreateServices(fullRun: fullRun));

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", "unit-test.json", "--output", "result.xml"]);

        Assert.Equal((int)CliExitCode.Success, exitCode);
        Assert.Contains("[Information] Test.Info: diagnostic", console.Output);
        Assert.Contains("[Warning] Test.Warning: diagnostic", console.Errors);
        Assert.Contains("[Error] Test.Error: diagnostic", console.Errors);
    }

    [Fact]
    public async Task Run_passes_explicit_readiness_options()
    {
        var fullRun = new FakeFullRunService(new(CliFullRunState.Completed, null, null, [], "Done"));
        var commandLine = new CliCommandLine(new RecordingConsole(), _ => CreateServices(fullRun: fullRun));
        var exit = await commandLine.InvokeAsync(["run", "-f", "test.json", "-o", "out.xml",
            "--unittest-ready-timeout", "30", "--reset-on-startup-timeout"]);
        Assert.Equal((int)CliExitCode.Success, exit);
        Assert.Equal(30, fullRun.RunOptions!.UnitTestReadyTimeoutSeconds);
        Assert.True(fullRun.RunOptions.ResetOnStartupTimeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("invalid")]
    [InlineData("2147483647")]
    public async Task Invalid_readiness_timeout_is_rejected_before_services_are_created(string value)
    {
        var commandLine = new CliCommandLine(new RecordingConsole(), _ => throw new InvalidOperationException("Unexpected services"));
        Assert.Equal((int)CliExitCode.InvalidArguments,
            await commandLine.InvokeAsync(["run", "-f", "test.json", "-o", "out.xml", "--unittest-ready-timeout", value]));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Abort")]
    public async Task Run_maps_completed_test_failure_to_dedicated_exit_code(string result)
    {
        var console = new RecordingConsole();
        var fullRun = new FakeFullRunService(new CliFullRunResult(
            CliFullRunState.TestFailed,
            null,
            null,
            [],
            $"UnitTest run completed with result '{result}'.",
            "result.xml"));
        var commandLine = new CliCommandLine(console, _ =>
            CreateServices(fullRun: fullRun));

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", "unit-test.json", "--output", "result.xml"]);

        Assert.Equal((int)CliExitCode.TestFailed, exitCode);
        Assert.Contains(console.Output, line => line.Contains("Outcome=TestFailed", StringComparison.Ordinal));
        Assert.Contains(console.Output, line => line.Contains("Artifact=result.xml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cleanup_failure_has_precedence_over_cancelled_run()
    {
        var console = new RecordingConsole();
        var release = new RunSessionReleaseResult(
            OperationResult.Success(),
            OperationResult.Failure("cleanup failed"),
            OperationResult.Success());
        var fullRun = new FakeFullRunService(new CliFullRunResult(
            CliFullRunState.Cancelled,
            null,
            release,
            [],
            "Run cancelled."));
        var commandLine = new CliCommandLine(console, _ =>
            CreateServices(fullRun: fullRun));

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", "unit-test.json", "--output", "result.xml"]);

        Assert.Equal((int)CliExitCode.CleanupFailed, exitCode);
        Assert.Contains(console.Output, line => line.Contains("Outcome=CleanupFailed", StringComparison.Ordinal));
        Assert.Contains(console.Errors, line =>
            line.Contains("Cleanup workflow failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_maps_artifact_failure_to_dedicated_exit_code()
    {
        var console = new RecordingConsole();
        var fullRun = new FakeFullRunService(new CliFullRunResult(
            CliFullRunState.ArtifactFailed,
            null,
            null,
            [],
            "JUnit XML could not be written."));
        var commandLine = new CliCommandLine(console, _ =>
            CreateServices(fullRun: fullRun));

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", "unit-test.json", "--output", "result.xml"]);

        Assert.Equal((int)CliExitCode.ArtifactFailed, exitCode);
        Assert.Contains("JUnit XML could not be written.", console.Errors);
    }

    [Fact]
    public async Task Explicit_help_writes_usage_without_creating_services()
    {
        var console = new RecordingConsole();
        var commandLine = new CliCommandLine(console, _ =>
            throw new InvalidOperationException("Services must not be created."));

        var exitCode = await commandLine.InvokeAsync(["help"]);

        Assert.Equal((int)CliExitCode.Success, exitCode);
        Assert.Contains(console.Output, line => line.StartsWith("Usage:", StringComparison.Ordinal));
        Assert.Contains(console.Output, line => line.Contains("run -f|--file", StringComparison.Ordinal));
        Assert.Contains(console.Output, line => line.Contains("register-profiles", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false, LogLevel.Information)]
    [InlineData(false, true, LogLevel.Trace)]
    [InlineData(true, false, LogLevel.Debug)]
    [InlineData(true, true, LogLevel.Debug)]
    public async Task Logging_flags_use_agreed_precedence(
        bool debug,
        bool verbose,
        LogLevel expectedLevel)
    {
        var console = new RecordingConsole();
        CliApplicationOptions? capturedOptions = null;
        var commandLine = new CliCommandLine(console, options =>
        {
            capturedOptions = options;
            return CreateServices(profileList: new FakeProfileListService([]));
        });
        var args = new List<string> { "list-profiles" };
        if (debug)
        {
            args.Add("--debug");
        }

        if (verbose)
        {
            args.Add("--verbose");
        }

        var exitCode = await commandLine.InvokeAsync([.. args]);

        Assert.Equal((int)CliExitCode.Success, exitCode);
        Assert.NotNull(capturedOptions);
        Assert.Equal(expectedLevel, capturedOptions.LogLevel);
    }

    [Fact]
    public async Task Raw_event_logging_without_debug_is_ignored_with_warning()
    {
        var console = new RecordingConsole();
        CliApplicationOptions? capturedOptions = null;
        var commandLine = new CliCommandLine(console, options =>
        {
            capturedOptions = options;
            return CreateServices(profileList: new FakeProfileListService([]));
        });

        var exitCode = await commandLine.InvokeAsync(
            ["list-profiles", "--raw-event-logging"]);

        Assert.Equal((int)CliExitCode.Success, exitCode);
        Assert.False(capturedOptions!.RawEventLoggingEnabled);
        Assert.Contains(console.Errors, line =>
            line.Contains("requires --debug", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Raw_event_logging_with_debug_is_enabled()
    {
        var console = new RecordingConsole();
        CliApplicationOptions? capturedOptions = null;
        var commandLine = new CliCommandLine(console, options =>
        {
            capturedOptions = options;
            return CreateServices(profileList: new FakeProfileListService([]));
        });

        var exitCode = await commandLine.InvokeAsync(
            ["--debug", "list-profiles", "--raw-event-logging"]);

        Assert.Equal((int)CliExitCode.Success, exitCode);
        Assert.True(capturedOptions!.RawEventLoggingEnabled);
        Assert.Equal(LogLevel.Debug, capturedOptions.LogLevel);
        Assert.Empty(console.Errors);
    }

    [Fact]
    public async Task Check_config_maps_profile_unavailable_to_dedicated_exit_code()
    {
        var console = new RecordingConsole();
        var checkResult = new CliConfigurationCheckResult(
            CliConfigurationCheckState.ProfileUnavailable,
            null,
            null,
            []);
        var commandLine = new CliCommandLine(console, _ =>
            CreateServices(configurationCheck: new FakeConfigurationCheckService(checkResult)));

        var exitCode = await commandLine.InvokeAsync(
            ["check-config", "--file", "unit-test.json"]);

        Assert.Equal((int)CliExitCode.ProfileUnavailable, exitCode);
    }

    [Fact]
    public async Task List_profiles_writes_deterministic_profile_output()
    {
        var console = new RecordingConsole();
        var profile = CreateProfile();
        var commandLine = new CliCommandLine(console, _ =>
            CreateServices(profileList: new FakeProfileListService([profile])));

        var exitCode = await commandLine.InvokeAsync(["list-profiles"]);

        Assert.Equal((int)CliExitCode.Success, exitCode);
        var line = Assert.Single(console.Output);
        Assert.StartsWith("fake 1.0.0 | Fake Profile | Registered", line);
    }

    private static ServiceProvider CreateServices(
        ICliConfigurationCheckService? configurationCheck = null,
        ICliProfileListService? profileList = null,
        ICliFullRunService? fullRun = null)
    {
        var services = new ServiceCollection();
        if (configurationCheck is not null)
        {
            services.AddSingleton(configurationCheck);
        }

        if (profileList is not null)
        {
            services.AddSingleton(profileList);
        }

        if (fullRun is not null)
        {
            services.AddSingleton(fullRun);
        }

        return services.BuildServiceProvider();
    }

    private static Diagnostic CreateDiagnostic(string code, LogLevel level)
    {
        return new Diagnostic(
            code,
            DiagnosticCategory.Workflow,
            level,
            DiagnosticSource.CliApplication,
            DiagnosticPhase.ExecutionMonitoring,
            "diagnostic",
            new DiagnosticCorrelation(),
            DateTimeOffset.UnixEpoch);
    }

    private static AccessProfileRegistryEntry CreateProfile()
    {
        return new AccessProfileRegistryEntry(
            "fake",
            "1.0.0",
            "Fake Profile",
            new AccessProfileRegistrySource(
                "DirectoryPackage",
                Path.Combine("profiles", "fake"),
                "fingerprint"),
            new AccessProfileRegistryRegistration(
                AccessProfileRegistrationState.Registered,
                DateTimeOffset.UnixEpoch,
                null),
            AccessProfileCapabilities.Empty);
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
    }

    private sealed class FakeConfigurationCheckService : ICliConfigurationCheckService
    {
        private readonly CliConfigurationCheckResult result;

        public FakeConfigurationCheckService(CliConfigurationCheckResult result)
        {
            this.result = result;
        }

        public Task<CliConfigurationCheckResult> CheckAsync(
            string path,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class FakeFullRunService : ICliFullRunService
    {
        private readonly CliFullRunResult result;

        public FakeFullRunService(CliFullRunResult result)
        {
            this.result = result;
        }

        public string? ConfigurationPath { get; private set; }

        public string? OutputPath { get; private set; }

        public CliRunOptions? RunOptions { get; private set; }

        public Task<CliFullRunResult> RunAsync(
            string configurationPath,
            string outputPath,
            Action<string>? progress,
            CancellationToken cancellationToken,
            CliRunOptions? runOptions = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfigurationPath = configurationPath;
            OutputPath = outputPath;
            RunOptions = runOptions;
            progress?.Invoke("Fake run progress.");
            return Task.FromResult(result);
        }
    }

    private sealed class FakeProfileListService : ICliProfileListService
    {
        private readonly IReadOnlyList<AccessProfileRegistryEntry> profiles;

        public FakeProfileListService(IReadOnlyList<AccessProfileRegistryEntry> profiles)
        {
            this.profiles = profiles;
        }

        public Task<IReadOnlyList<AccessProfileRegistryEntry>> ListAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(profiles);
        }
    }
}
