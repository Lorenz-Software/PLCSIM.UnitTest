using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.App.Cli;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Core;
using PLCSIM.UnitTest.Core.AccessProfiles;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Fake;

public sealed class CliFullRunIntegrationTests : IDisposable
{
    private readonly string temporaryDirectory;
    private readonly CliApplicationOptions options;
    private RecordingConsole? recordingConsole;

    public CliFullRunIntegrationTests()
    {
        temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest.CliFullRunIntegrationTests",
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
    public async Task Fake_test_failure_returns_test_failed_and_writes_junit()
    {
        var services = FakeAccessProfileFactory.CreateServices(
            autoCompleteRuns: true,
            autoCompletionScenario: FakeUnitTestEvidenceScenario.FailedAssertion);
        var commandLine = await CreateRegisteredCommandLineAsync(services);
        var configuration = WriteRunConfiguration("run-failed.json");
        var output = Path.Combine(temporaryDirectory, "run-failed.xml");

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", output]);

        Assert.Equal((int)CliExitCode.TestFailed, exitCode);
        Assert.Contains(recordingConsole!.Output, line =>
            line.Contains("Discovering project targets", StringComparison.Ordinal));
        Assert.Contains(recordingConsole.Output, line =>
            line.Contains("Building and transferring project", StringComparison.Ordinal));
        Assert.Contains(recordingConsole.Output, line =>
            line.Contains("Writing JUnit artifact", StringComparison.Ordinal));
        Assert.Contains(recordingConsole.Output, line =>
            line.Contains("Runtime and profile resources released", StringComparison.Ordinal));
        var junit = await File.ReadAllTextAsync(output);
        Assert.Contains("failures=\"1\"", junit, StringComparison.Ordinal);
        AssertOwnedRuntimeWasDestroyed(services);
    }

    [Fact]
    public async Task Unknown_configured_project_target_is_invalid_configuration()
    {
        var commandLine = await CreateRegisteredCommandLineAsync();
        var configuration = WriteRunConfiguration(
            "run-invalid-target.json",
            projectTargetId: "missing-target");
        var output = Path.Combine(temporaryDirectory, "run-invalid-target.xml");

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", output]);

        Assert.Equal((int)CliExitCode.InvalidConfiguration, exitCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Fake_profile_load_failure_returns_environment_unavailable()
    {
        var plugin = new FakeAccessProfilePlugin((_, _) =>
            throw new InvalidOperationException("Injected fake profile load failure."));
        var commandLine = await CreateRegisteredCommandLineAsync(plugin);
        var configuration = WriteRunConfiguration("run-environment-failure.json");
        var output = Path.Combine(temporaryDirectory, "run-environment-failure.xml");

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", output]);

        Assert.Equal((int)CliExitCode.EnvironmentUnavailable, exitCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Fake_runtime_prepare_failure_returns_execution_failed()
    {
        var services = FakeAccessProfileFactory.CreateServices(autoCompleteRuns: true);
        var control = Assert.IsType<FakePlcControlService>(services.PlcControl);
        control.PrepareRuntimeResult = OperationResult<PreparedPlcRuntime>.Failure(
            "Injected fake runtime prepare failure.");
        var commandLine = await CreateRegisteredCommandLineAsync(services);
        var configuration = WriteRunConfiguration("run-runtime-failure.json");
        var output = Path.Combine(temporaryDirectory, "run-runtime-failure.xml");

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", output]);

        Assert.Equal((int)CliExitCode.ExecutionFailed, exitCode);
        Assert.False(File.Exists(output));
        Assert.Null(control.LastReleaseRuntimeRequest);
    }

    [Fact]
    public async Task Fake_communication_failure_returns_execution_failed_and_cleans_up()
    {
        var services = FakeAccessProfileFactory.CreateServices(autoCompleteRuns: true) with
        {
            PrepareUnitTestCommunicationResult = OperationResult<CommunicationSession>.Failure(
                "Injected fake communication failure.")
        };
        var commandLine = await CreateRegisteredCommandLineAsync(services);
        var configuration = WriteRunConfiguration("run-communication-failure.json");
        var output = Path.Combine(temporaryDirectory, "run-communication-failure.xml");

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", output]);

        Assert.Equal((int)CliExitCode.ExecutionFailed, exitCode);
        Assert.False(File.Exists(output));
        AssertOwnedRuntimeWasDestroyed(services);
    }

    [Fact]
    public async Task Fake_command_timeout_returns_timed_out_and_cleans_up()
    {
        var services = FakeAccessProfileFactory.CreateServices(autoCompleteRuns: true);
        var lifecycle = Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle);
        lifecycle.CommandReactionDelay = null;
        var commandLine = await CreateRegisteredCommandLineAsync(services);
        var configuration = WriteRunConfiguration("run-timeout.json");
        var output = Path.Combine(temporaryDirectory, "run-timeout.xml");

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", output]);

        Assert.Equal((int)CliExitCode.TimedOut, exitCode);
        AssertOwnedRuntimeWasDestroyed(services);
    }

    [Fact]
    public async Task Cancellation_during_fake_plc_start_destroys_owned_runtime()
    {
        var services = FakeAccessProfileFactory.CreateServices(autoCompleteRuns: true);
        var control = Assert.IsType<FakePlcControlService>(services.PlcControl);
        control.HoldNextStart = true;
        var commandLine = await CreateRegisteredCommandLineAsync(services);
        var configuration = WriteRunConfiguration("run-cancelled.json");
        var output = Path.Combine(temporaryDirectory, "run-cancelled.xml");
        using var cancellation = new CancellationTokenSource();

        var run = commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", output],
            cancellation.Token);
        await WaitUntilAsync(() => control.IsStartPending);
        cancellation.Cancel();
        var exitCode = await run;

        Assert.Equal((int)CliExitCode.Cancelled, exitCode);
        AssertOwnedRuntimeWasDestroyed(services);
        Assert.True(GetLifecycleReleaseCount(services) > 0);
    }

    [Fact]
    public async Task Artifact_failure_after_fake_run_destroys_owned_runtime()
    {
        var services = FakeAccessProfileFactory.CreateServices(autoCompleteRuns: true);
        var commandLine = await CreateRegisteredCommandLineAsync(services);
        var configuration = WriteRunConfiguration("run-artifact-failure.json");

        var exitCode = await commandLine.InvokeAsync(
            ["run", "--file", configuration, "--output", temporaryDirectory]);

        Assert.Equal((int)CliExitCode.ArtifactFailed, exitCode);
        AssertOwnedRuntimeWasDestroyed(services);
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private Task<CliCommandLine> CreateRegisteredCommandLineAsync(
        FakeAccessProfileServices? services = null)
    {
        services ??= FakeAccessProfileFactory.CreateServices(autoCompleteRuns: true);
        return CreateRegisteredCommandLineAsync(new FakeAccessProfilePlugin((_, _) => services));
    }

    private async Task<CliCommandLine> CreateRegisteredCommandLineAsync(
        IAccessProfilePlugin plugin)
    {
        var console = new RecordingConsole();
        recordingConsole = console;
        var commandLine = new CliCommandLine(console, _ => CreateBuiltInFakeHost(plugin));

        var registration = await commandLine.InvokeAsync(["register-profiles"]);

        Assert.Equal((int)CliExitCode.Success, registration);
        console.Clear();
        return commandLine;
    }

    private ServiceProvider CreateBuiltInFakeHost(IAccessProfilePlugin plugin)
    {
        var serviceCollection = new ServiceCollection();
        var builtInOptions = options with
        {
            ProfileDiscoveryDirectory = Path.Combine(temporaryDirectory, "built-in-profiles")
        };
        serviceCollection.AddLogging(builder => builder.ClearProviders());
        serviceCollection.AddCliApplicationServices(builtInOptions);
        serviceCollection.RemoveAll<AccessProfileDiscoveryOptions>();
        serviceCollection.AddSingleton(new AccessProfileDiscoveryOptions(
            builtInOptions.ProfileDiscoveryDirectory,
            IncludeBuiltInProfiles: true));
        serviceCollection.RemoveAll<AccessProfilePluginLoader>();
        serviceCollection.AddSingleton(provider => new AccessProfilePluginLoader(
            provider.GetRequiredService<CoreServiceContext>(),
            [plugin]));
        return serviceCollection.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private string WriteRunConfiguration(
        string fileName,
        string projectTargetId = "fake-project-target")
    {
        var path = Path.Combine(temporaryDirectory, fileName);
        File.WriteAllText(
            path,
            $$"""
            {
              "version": 1,
              "project": { "source": "fake-project.json" },
              "accessProfile": {
                "id": "fake",
                "version": "1.0.0",
                "values": {
                  "plcRef": { "id": "{{projectTargetId}}" }
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

    private static void AssertOwnedRuntimeWasDestroyed(FakeAccessProfileServices services)
    {
        Assert.Equal(
            RuntimeResourceReleaseMode.DestroyResource,
            Assert.IsType<FakePlcControlService>(services.PlcControl)
                .LastReleaseRuntimeRequest?.ReleaseMode);
    }

    private static int GetLifecycleReleaseCount(FakeAccessProfileServices services)
    {
        return Assert.IsType<FakeUnitTestLifecycleService>(services.UnitTestLifecycle)
            .ReleaseCommunicationCount;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected fake operation did not become pending.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class RecordingConsole : ICliConsole
    {
        public List<string> Output { get; } = [];

        public List<string> Errors { get; } = [];

        public void WriteOutput(string message) => Output.Add(message);

        public void WriteError(string message) => Errors.Add(message);

        public void Clear()
        {
            Output.Clear();
            Errors.Clear();
        }
    }
}
