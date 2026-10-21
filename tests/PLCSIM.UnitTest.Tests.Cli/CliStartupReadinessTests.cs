using Microsoft.Extensions.Time.Testing;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.App.Cli.Commands;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using PLCSIM.UnitTest.Core.AccessProfiles;
using PLCSIM.UnitTest.Core.Output;
using PLCSIM.UnitTest.Core.Workflow;
using PLCSIM.UnitTest.Exporters.JUnit;
using PLCSIM.UnitTest.Testing.Core;
using PLCSIM.UnitTest.Testing.Time;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Cli;

public sealed partial class CliStartupReadinessTests
{
    private static readonly UnitTestRunControlObservation Ready = new(
        UnitTestExecutionStatus.Idle, UnitTestExecutionSignals.Ready, UnitTestLifecycleState.Idle, UnitTestResultState.NotEvaluated);
    private static readonly UnitTestRunControlObservation Resetting = new(
        UnitTestExecutionStatus.Reset, UnitTestExecutionSignals.None, UnitTestLifecycleState.Reset, UnitTestResultState.NotEvaluated);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delayed_startup_keeps_consuming_without_reset_and_exports_after_ready(bool recoveryEnabled)
    {
        await using var fixture = new Fixture();
        var run = fixture.Run(Resetting, new() { ResetOnStartupTimeout = recoveryEnabled });
        await fixture.WaitForReadinessPhase();
        Assert.True(fixture.Workflow.Current.Status.MonitoringActive);
        fixture.Stream.PublishRunControlResetObserved();
        Assert.Contains(fixture.Workflow.EvidenceAnalysis.CurrentTrace!.Entries,
            entry => entry.Payload is PlcEventActivityPayload);
        fixture.Time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(run.IsCompleted);
        Assert.Empty(fixture.Commands);
        fixture.Lifecycle.PublishObservation(Ready);
        var result = await fixture.Finish(run);
        Assert.Equal(CliFullRunState.Completed, result.State);
        Assert.True(File.Exists(fixture.Output));
        Assert.Equal([UnitTestActivityCommandKind.Start], fixture.Commands);
        fixture.AssertCleanup(result);
    }

    [Theory]
    [InlineData(UnitTestLifecycleState.Reset)]
    [InlineData(UnitTestLifecycleState.Uninitialized)]
    [InlineData(UnitTestLifecycleState.Completed)]
    public async Task Default_timeout_never_resets_or_starts(UnitTestLifecycleState state)
    {
        await using var fixture = new Fixture();
        var observation = state == UnitTestLifecycleState.Completed
            ? new UnitTestRunControlObservation(UnitTestExecutionStatus.Completed, UnitTestExecutionSignals.Completed, state, UnitTestResultState.Failed)
            : Resetting with { State = state };
        var run = fixture.Run(observation);
        await fixture.WaitForReadinessPhase();
        fixture.Time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(run.IsCompleted);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CliFullRunState.TimedOut, result.State);
        Assert.Contains(result.Diagnostics, item => item.Code == "Cli.Run.UnitTestReadyTimeout");
        Assert.Contains($"State={state}", result.Message);
        Assert.Empty(fixture.Commands);
        Assert.False(File.Exists(fixture.Output));
        fixture.AssertCleanup(result);
    }

    [Fact]
    public async Task Missing_observation_is_not_interpreted_as_ready()
    {
        await using var fixture = new Fixture();
        Assert.Null(fixture.Workflow.Current.Status.LifecycleObservation);
        var wait = fixture.Service.EnsureUnitTestReadyAsync(new(), RunConfigurationDefaults.CreateDefaultTimeoutPolicy(), null, CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        var result = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CliFullRunState.TimedOut, result!.State);
        Assert.Contains("No RunControl observation", result.Message);
    }

    [Fact]
    public async Task Configured_initial_deadline_is_independent_of_command_reaction_timeout()
    {
        await using var fixture = new Fixture();
        var run = fixture.Run(Resetting, new() { UnitTestReadyTimeoutSeconds = 30 });
        await fixture.WaitForReadinessPhase();
        fixture.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(run.IsCompleted);
        Assert.Empty(fixture.Commands);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CliFullRunState.TimedOut, (await run.WaitAsync(TimeSpan.FromSeconds(5))).State);
    }

    [Fact]
    public async Task Rejected_recovery_command_is_not_retried()
    {
        await using var fixture = new Fixture();
        fixture.Lifecycle.RejectNextCommand = true;
        var run = fixture.Run(Resetting, new() { UnitTestReadyTimeoutSeconds = 1, ResetOnStartupTimeout = true });
        await fixture.WaitForReadinessPhase();
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var result = await fixture.Finish(run);
        Assert.Equal(CliFullRunState.Failed, result.State);
        Assert.Equal([UnitTestActivityCommandKind.Reset], fixture.Commands);
        fixture.AssertCleanup(result);
    }

    [Fact]
    public async Task Completed_failed_run_can_be_reset_only_with_explicit_recovery()
    {
        await using var fixture = new Fixture();
        var run = fixture.Run(new(UnitTestExecutionStatus.Completed, UnitTestExecutionSignals.Completed,
            UnitTestLifecycleState.Completed, UnitTestResultState.Failed),
            new() { UnitTestReadyTimeoutSeconds = 1, ResetOnStartupTimeout = true });
        await fixture.WaitForReadinessPhase();
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CliFullRunState.Completed, (await fixture.Finish(run)).State);
        Assert.Equal([UnitTestActivityCommandKind.Reset, UnitTestActivityCommandKind.Start], fixture.Commands);
    }

    [Fact]
    public async Task Error_during_recovery_aborts_without_second_reset_or_start()
    {
        await using var fixture = new Fixture();
        fixture.Lifecycle.AutoCompleteResetAfter = null;
        var run = fixture.Run(Resetting, new() { UnitTestReadyTimeoutSeconds = 1, ResetOnStartupTimeout = true });
        await fixture.WaitForReadinessPhase();
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.AdvanceUntil(() => fixture.Workflow.EvidenceAnalysis.CurrentTrace!.Entries.Any(entry =>
            entry.Payload is CommandActivityPayload { Command: UnitTestActivityCommandKind.Reset,
                Phase: UnitTestActivityCommandPhase.Completed }));
        fixture.Lifecycle.PublishObservation(Resetting with { ExecutionSignals = UnitTestExecutionSignals.Error });
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CliFullRunState.Failed, result.State);
        Assert.Equal([UnitTestActivityCommandKind.Reset], fixture.Commands);
        fixture.AssertCleanup(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recovery_is_one_shot_and_start_requires_reset_completion(bool resetCompletes)
    {
        await using var fixture = new Fixture();
        fixture.Lifecycle.AutoCompleteResetAfter = resetCompletes ? TimeSpan.FromSeconds(1) : null;
        var run = fixture.Run(Resetting, new() { UnitTestReadyTimeoutSeconds = 2, ResetOnStartupTimeout = true });
        await fixture.WaitForReadinessPhase();
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(fixture.Commands);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var result = await fixture.Finish(run);
        Assert.Equal(resetCompletes ? CliFullRunState.Completed : CliFullRunState.TimedOut, result.State);
        Assert.Equal(1, fixture.Commands.Count(command => command == UnitTestActivityCommandKind.Reset));
        Assert.Equal(resetCompletes ? 1 : 0, fixture.Commands.Count(command => command == UnitTestActivityCommandKind.Start));
        Assert.Equal(resetCompletes, File.Exists(fixture.Output));
        if (!resetCompletes) Assert.Contains("after the single recovery Reset", result.Message);
        fixture.AssertCleanup(result);
    }

    [Theory]
    [InlineData(UnitTestExecutionSignals.ConfigError)]
    [InlineData(UnitTestExecutionSignals.Error)]
    public async Task Explicit_error_aborts_even_with_recovery_enabled(UnitTestExecutionSignals signal)
    {
        await using var fixture = new Fixture();
        var result = await fixture.Run(Resetting with { ExecutionSignals = signal },
            new() { ResetOnStartupTimeout = true }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CliFullRunState.Failed, result.State);
        Assert.Contains(result.Diagnostics, item => item.Code == "Cli.Run.UnitTestReadyFailed");
        Assert.Empty(fixture.Commands);
        fixture.AssertCleanup(result);
    }

    [Fact]
    public async Task Error_published_while_waiting_aborts_without_recovery()
    {
        await using var fixture = new Fixture();
        var run = fixture.Run(Resetting, new() { ResetOnStartupTimeout = true });
        await fixture.WaitForReadinessPhase();
        fixture.Lifecycle.PublishObservation(Resetting with { ExecutionSignals = UnitTestExecutionSignals.Error });
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CliFullRunState.Failed, result.State);
        Assert.Empty(fixture.Commands);
        fixture.AssertCleanup(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_initial_or_recovery_wait_cleans_up(bool afterReset)
    {
        await using var fixture = new Fixture();
        fixture.Lifecycle.AutoCompleteResetAfter = null;
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Run(Resetting, new() { UnitTestReadyTimeoutSeconds = 1, ResetOnStartupTimeout = true }, cancellation.Token);
        await fixture.WaitForReadinessPhase();
        if (afterReset)
        {
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            await fixture.AdvanceUntil(() => fixture.Commands.Contains(UnitTestActivityCommandKind.Reset));
        }
        cancellation.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CliFullRunState.Cancelled, result.State);
        Assert.DoesNotContain(UnitTestActivityCommandKind.Start, fixture.Commands);
        Assert.Equal(afterReset ? 1 : 0, fixture.Commands.Count(command => command == UnitTestActivityCommandKind.Reset));
        fixture.AssertCleanup(result);
    }

    [Fact]
    public async Task Readiness_arriving_at_recovery_boundary_prevents_reset()
    {
        await using var fixture = new Fixture();
        fixture.OnProgress = message =>
        {
            if (message.Contains("Attempting one", StringComparison.Ordinal)) fixture.Lifecycle.PublishObservation(Ready);
        };
        var run = fixture.Run(Resetting, new() { UnitTestReadyTimeoutSeconds = 1, ResetOnStartupTimeout = true });
        await fixture.WaitForReadinessPhase();
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CliFullRunState.Completed, (await fixture.Finish(run)).State);
        Assert.Equal([UnitTestActivityCommandKind.Start], fixture.Commands);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "CliStartupReadinessTests", Guid.NewGuid().ToString("N"));
        private readonly AccessProfileSessionService sessions;
        private readonly TaskCompletionSource readinessPhase = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeTimeProvider Time { get; } = FakeTime.Create();
        public FakeUnitTestLifecycleService Lifecycle { get; }
        public FakeUnitTestEventStreamService Stream { get; }
        public FakePlcControlService Plc { get; }
        public FakePlcProjectService Project { get; }
        public ConfigurationCheck Configuration { get; } = new();
        public string AnalysisDirectory => Path.Combine(directory, "analysis");
        public RunWorkflowService Workflow { get; }
        public CliFullRunService Service { get; }
        public Action<string>? OnProgress { get; set; }
        public string Output => Path.Combine(directory, "junit.xml");
        public UnitTestActivityCommandKind[] Commands => Workflow.EvidenceAnalysis.CurrentTrace?.Entries
            .Select(entry => entry.Payload).OfType<CommandActivityPayload>()
            .Where(payload => payload.Phase == UnitTestActivityCommandPhase.Requested
                && payload.Command != UnitTestActivityCommandKind.PlcStart)
            .Select(payload => payload.Command).ToArray() ?? [];

        public Fixture(FakeUnitTestEvidenceScenario scenario = FakeUnitTestEvidenceScenario.Passed)
        {
            var context = TestCoreServiceContext.Create(timeProvider: Time, accessProfileDataRootDirectory: directory);
            var services = FakeAccessProfileFactory.CreateServices(Time, autoCompleteRuns: true, autoCompletionScenario: scenario);
            Lifecycle = (FakeUnitTestLifecycleService)services.UnitTestLifecycle;
            Lifecycle.CommandReactionDelay = TimeSpan.Zero;
            Stream = (FakeUnitTestEventStreamService)services.UnitTestEventStream;
            Plc = (FakePlcControlService)services.PlcControl;
            Project = (FakePlcProjectService)services.Project;
            sessions = new AccessProfileSessionService(
                new AccessProfilePluginLoader(context, [new FakeAccessProfilePlugin((_, _) => services)]),
                new AccessProfileDiscoveryOptions("", IncludeBuiltInProfiles: true), context);
            Workflow = new RunWorkflowService(new RunSetupValidator(Time), context);
            Service = new CliFullRunService(Configuration, sessions, Workflow,
                new RunSessionReleaseService(Workflow, sessions, context),
                new ArtifactExportService(context, [new JUnitArtifactExporter(Time)]), Time);
        }

        public Task<CliFullRunResult> Run(UnitTestRunControlObservation observation, CliRunOptions? options = null, CancellationToken cancellationToken = default)
            => Service.RunAsync(Path.Combine(directory, "input.json"), Output, message =>
            {
                if (message.StartsWith("Waiting for UnitTest Idle", StringComparison.Ordinal))
                {
                    Lifecycle.PublishObservation(observation);
                    readinessPhase.TrySetResult();
                }
                if (message == "Starting UnitTest run...")
                {
                    Assert.True(Workflow.Current.Status.LifecycleObservation!.Ready);
                    Assert.False(Workflow.Current.Status.LifecycleObservation.Running);
                }
                OnProgress?.Invoke(message);
            }, cancellationToken, options);

        public Task WaitForReadinessPhase() => readinessPhase.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public async Task AdvanceUntil(Func<bool> condition)
        {
            for (var i = 0; i < 400 && !condition(); i++)
            {
                Time.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(1);
            }
            Assert.True(condition());
        }

        public async Task<CliFullRunResult> Finish(Task<CliFullRunResult> run)
        {
            await AdvanceUntil(() => run.IsCompleted);
            return await run;
        }

        public void AssertCleanup(CliFullRunResult result)
        {
            Assert.True(result.Release!.IsSuccessful);
            Assert.Null(sessions.CurrentSession);
            Assert.False(Stream.CurrentMonitoringState.IsMonitoring);
            Assert.Equal(0, Stream.EventBufferResetCount);
        }

        public async ValueTask DisposeAsync()
        {
            await sessions.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ConfigurationCheck : ICliConfigurationCheckService
    {
        public bool Invalid { get; set; }
        public Task<CliConfigurationCheckResult> CheckAsync(string path, CancellationToken cancellationToken)
        {
            if (Invalid) return Task.FromResult(new CliConfigurationCheckResult(
                CliConfigurationCheckState.InvalidConfiguration, null, null, []));
            var config = FakeAccessProfileFactory.CreateDefaultConfig() with
            {
                Values = new AccessProfileConfigurationPayload("""{"plcRef":{"id":"fake-project-target"}}""")
            };
            var setup = new RunSetup(config, RuntimeProvisioningMode.CreateOwned,
                RunConfigurationDefaults.CreateDefaultOutputPolicy(),
                new ExecutionPolicy(RunConfigurationDefaults.CreateDefaultTimeoutPolicy() with
                {
                    UnitTestCommandReaction = TimeSpan.FromSeconds(3),
                    TestRun = TimeSpan.FromSeconds(5),
                    FinalEvent = TimeSpan.FromSeconds(2)
                }));
            var profile = new AccessProfileRegistryEntry("fake", "1.0.0", "Fake",
                new AccessProfileRegistrySource("BuiltIn", "", ""),
                new AccessProfileRegistryRegistration(AccessProfileRegistrationState.Registered, DateTimeOffset.UnixEpoch, null),
                AccessProfileCapabilities.Empty);
            return Task.FromResult(new CliConfigurationCheckResult(CliConfigurationCheckState.Valid, setup, profile, []));
        }
    }
}
