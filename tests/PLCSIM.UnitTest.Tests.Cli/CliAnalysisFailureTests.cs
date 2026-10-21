using System.IO.Compression;
using System.Text.Json;
using PLCSIM.UnitTest.AccessProfiles.Fake;
using PLCSIM.UnitTest.App.Cli;
using PLCSIM.UnitTest.App.Cli.Commands;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Configuration;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.References;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Cli;

public sealed partial class CliStartupReadinessTests
{
    [Fact]
    public async Task Readiness_timeout_report_includes_cli_deadline_without_claiming_plc_fault()
    {
        await using var fixture = new Fixture();
        var result = await fixture.Finish(fixture.Run(Resetting,
            ExportOptions(fixture) with { UnitTestReadyTimeoutSeconds = 1 }));
        Assert.Equal(CliFullRunState.TimedOut, result.State);
        Assert.Equal(CliExitCode.TimedOut, CliOutcomeResolver.Resolve(result));
        var report = await AssertPackage(fixture, result, "lifecycle", "Lifecycle");
        Assert.Contains("**Error**", report);
        Assert.Contains("Cli.Run.UnitTestReadyTimeout", report);
        Assert.Contains("State=Reset", report);
        Assert.Contains("CLI execution ended with TimedOut", report);
        Assert.Contains("not proof of a PLC framework fault", report);
        Assert.Empty(fixture.Commands);
        Assert.False(File.Exists(fixture.Output));
        fixture.AssertCleanup(result);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_ready_is_visible_in_report()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Run(Resetting, ExportOptions(fixture), cancellation.Token);
        await fixture.WaitForReadinessPhase();
        cancellation.Cancel();
        var result = await fixture.Finish(run);
        Assert.Equal(CliFullRunState.Cancelled, result.State);
        Assert.Equal(CliExitCode.Cancelled, CliOutcomeResolver.Resolve(result));
        var report = await AssertPackage(fixture, result, "lifecycle", "Lifecycle");
        Assert.Contains("**Warning**", report);
        Assert.Contains("CLI execution ended with Cancelled", report);
        Assert.Contains("CLI run was cancelled.", report);
        Assert.Empty(fixture.Commands);
        fixture.AssertCleanup(result);
    }

    [Fact]
    public async Task Explicit_runcontrol_error_preserves_cli_reason_and_observed_error()
    {
        await using var fixture = new Fixture();
        var result = await fixture.Finish(fixture.Run(
            Resetting with { ExecutionSignals = UnitTestExecutionSignals.Error }, ExportOptions(fixture)));
        Assert.Equal(CliFullRunState.Failed, result.State);
        var report = await AssertPackage(fixture, result, "lifecycle", "Lifecycle");
        Assert.Contains("**Error**", report);
        Assert.Contains("Cli.Run.UnitTestReadyFailed", report);
        Assert.Contains("Error=True", report);
        Assert.Contains("CLI execution ended with Failed", report);
        Assert.Empty(fixture.Commands);
        fixture.AssertCleanup(result);
    }

    [Fact]
    public async Task Successful_always_export_has_no_cli_failure_finding()
    {
        await using var fixture = new Fixture();
        var result = await fixture.Finish(fixture.Run(Ready,
            ExportOptions(fixture) with { AnalysisOn = CliAnalysisTrigger.Always }));
        Assert.Equal(CliFullRunState.Completed, result.State);
        Assert.Equal(CliExitCode.Success, CliOutcomeResolver.Resolve(result));
        var report = await AssertPackage(fixture, result, "run", "Run");
        Assert.Contains("**Healthy**", report);
        Assert.DoesNotContain("CLI execution outcome", report);
        Assert.True(File.Exists(fixture.Output));
        fixture.AssertCleanup(result);
    }

    [Theory]
    [InlineData(FakeUnitTestEvidenceScenario.FailedAssertion)]
    [InlineData(FakeUnitTestEvidenceScenario.FrameworkError)]
    public async Task Failed_test_exports_run_without_changing_junit_or_exit(FakeUnitTestEvidenceScenario scenario)
    {
        await using var fixture = new Fixture(scenario);
        var result = await fixture.Finish(fixture.Run(Ready, ExportOptions(fixture)));
        Assert.Equal(CliFullRunState.TestFailed, result.State);
        Assert.Equal(CliExitCode.TestFailed, CliOutcomeResolver.Resolve(result));
        Assert.True(File.Exists(fixture.Output));
        await AssertPackage(fixture, result, "run", "Run");
        fixture.AssertCleanup(result);
    }

    [Theory]
    [InlineData("plc", false)]
    [InlineData("plc", true)]
    [InlineData("reset", false)]
    [InlineData("reset", true)]
    [InlineData("start", false)]
    [InlineData("start", true)]
    public async Task Pending_command_exports_its_attempt_after_timeout_or_cancel(string command, bool cancel)
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Plc.HoldNextStart = command == "plc";
        fixture.Lifecycle.HoldNextCommand = command != "plc";
        var run = fixture.Run(command == "reset" ? Resetting : Ready,
            ExportOptions(fixture) with { UnitTestReadyTimeoutSeconds = 1, ResetOnStartupTimeout = command == "reset" },
            cancellation.Token);
        await fixture.AdvanceUntil(() => command == "plc"
            ? fixture.Plc.IsStartPending : fixture.Lifecycle.PendingCommand is not null);
        Assert.Null(fixture.Workflow.Current.CurrentRun);
        if (cancel) cancellation.Cancel();
        var result = await fixture.Finish(run);
        Assert.Equal(cancel ? CliFullRunState.Cancelled : CliFullRunState.TimedOut, result.State);
        Assert.Equal(cancel ? CliExitCode.Cancelled : CliExitCode.TimedOut, CliOutcomeResolver.Resolve(result));
        Assert.False(File.Exists(fixture.Output));
        var markdown = await AssertPackage(fixture, result, "lifecycle", "Lifecycle");
        Assert.Contains(command == "plc" ? "PlcStart" : command == "reset" ? "Reset" : "Start", markdown);
        fixture.AssertCleanup(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_start_without_run_epoch_still_exports_lifecycle(bool reject)
    {
        await using var fixture = new Fixture();
        fixture.Lifecycle.RejectNextCommand = reject;
        fixture.Lifecycle.CommandReactionDelay = null;
        var result = await fixture.Finish(fixture.Run(Ready, ExportOptions(fixture)));
        Assert.NotEqual(CliFullRunState.Completed, result.State);
        Assert.False(File.Exists(fixture.Output));
        await AssertPackage(fixture, result, "lifecycle", "Lifecycle");
        fixture.AssertCleanup(result);
    }

    [Fact]
    public async Task Cancelling_active_run_exports_partial_run_not_lifecycle()
    {
        await using var fixture = new Fixture();
        fixture.Lifecycle.AutoCompleteRunAfter = null;
        using var cancellation = new CancellationTokenSource();
        var run = fixture.Run(Ready, ExportOptions(fixture), cancellation.Token);
        await fixture.AdvanceUntil(() => fixture.Workflow.Current.CurrentRun is not null);
        cancellation.Cancel();
        var result = await fixture.Finish(run);
        Assert.Equal(CliFullRunState.Cancelled, result.State);
        Assert.False(File.Exists(fixture.Output));
        var report = await AssertPackage(fixture, result, "run", "Run");
        Assert.Contains("Incomplete", report);
        fixture.AssertCleanup(result);
    }

    [Fact]
    public async Task Early_setup_failure_exports_communication_health()
    {
        await using var fixture = new Fixture();
        fixture.Project.PrepareProjectResult = OperationResult<PlcProjectContext>.Failure("Injected setup failure");
        var result = await fixture.Finish(fixture.Run(Ready, ExportOptions(fixture)));
        Assert.Equal(CliFullRunState.Failed, result.State);
        await AssertPackage(fixture, result, "communication-health", "Session");
        fixture.AssertCleanup(result);
    }

    [Fact]
    public async Task Invalid_configuration_after_prior_run_does_not_export_previous_session()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Finish(fixture.Run(Ready));
        Assert.Equal(CliFullRunState.Completed, first.State);
        Assert.NotNull(fixture.Workflow.EvidenceAnalysis.CurrentTrace);
        fixture.Configuration.Invalid = true;
        var result = await fixture.Run(Ready, ExportOptions(fixture));
        Assert.Equal(CliFullRunState.InvalidConfiguration, result.State);
        Assert.Null(result.AnalysisExport!.OutputPath);
        Assert.Contains("no activity trace", result.AnalysisExport.Error);
        Assert.False(Directory.Exists(fixture.AnalysisDirectory));
        fixture.AssertCleanup(result);
    }

    [Fact]
    public async Task Report_write_failure_does_not_replace_failed_test_outcome()
    {
        await using var fixture = new Fixture(FakeUnitTestEvidenceScenario.FailedAssertion);
        var options = ExportOptions(fixture) with { AnalysisOutputDirectory = fixture.Output };
        var result = await fixture.Finish(fixture.Run(Ready, options));
        Assert.Equal(CliExitCode.TestFailed, CliOutcomeResolver.Resolve(result));
        Assert.Null(result.AnalysisExport!.OutputPath);
        Assert.Contains("export failed", result.AnalysisExport.Error);
        Assert.Contains("<testsuites", await File.ReadAllTextAsync(fixture.Output));
        fixture.AssertCleanup(result);
    }

    private static CliRunOptions ExportOptions(Fixture fixture) => new() { AnalysisOutputDirectory = fixture.AnalysisDirectory };

    [Fact]
    public async Task Timed_out_active_run_keeps_run_scope_and_partial_evidence()
    {
        await using var baseline = new Fixture();
        baseline.Lifecycle.AutoCompleteRunAfter = null;
        var baselineResult = await baseline.Finish(baseline.Run(Ready));
        await using var fixture = new Fixture();
        fixture.Lifecycle.AutoCompleteRunAfter = null;
        var result = await fixture.Finish(fixture.Run(Ready, ExportOptions(fixture)));
        Assert.NotEqual(CliFullRunState.Completed, result.State);
        Assert.Equal(baselineResult.State, result.State);
        Assert.Equal(CliOutcomeResolver.Resolve(baselineResult), CliOutcomeResolver.Resolve(result));
        var report = await AssertPackage(fixture, result, "run", "Run");
        Assert.Contains("Incomplete", report);
        fixture.AssertCleanup(result);
    }

    [Fact]
    public async Task Cleanup_failure_keeps_its_exit_code_and_exports_pre_cleanup_snapshot()
    {
        await using var fixture = new Fixture();
        fixture.Plc.ReleaseRuntimeResult = OperationResult<PlcRuntimeReleaseOutcome>.Failure("Injected release failure");
        var result = await fixture.Finish(fixture.Run(Ready, ExportOptions(fixture)));
        Assert.Equal(CliFullRunState.Completed, result.State);
        Assert.Equal(CliExitCode.CleanupFailed, CliOutcomeResolver.Resolve(result));
        Assert.True(File.Exists(fixture.Output));
        var report = await AssertPackage(fixture, result, "run", "Run");
        Assert.DoesNotContain("Injected release failure", report);
        Assert.False(result.Release!.IsSuccessful);
    }

    [Fact]
    public async Task Failed_monitoring_after_plc_start_keeps_lifecycle_scope()
    {
        await using var fixture = new Fixture();
        fixture.Stream.HoldNextStartMonitoring = true;
        var run = fixture.Run(Ready, ExportOptions(fixture));
        await fixture.AdvanceUntil(() => fixture.Stream.IsStartMonitoringPending);
        fixture.Stream.CompletePendingStartMonitoring(OperationResult<EventStreamState>.Failure("Injected monitoring failure"));
        var result = await fixture.Finish(run);
        Assert.NotEqual(CliFullRunState.Completed, result.State);
        await AssertPackage(fixture, result, "lifecycle", "Lifecycle");
        fixture.AssertCleanup(result);
    }

    private static async Task<string> AssertPackage(Fixture fixture, CliFullRunResult result, string name, string scope)
    {
        Assert.NotNull(result.AnalysisExport);
        Assert.Null(result.AnalysisExport.Error);
        var path = Assert.Single(Directory.GetFiles(fixture.AnalysisDirectory, "*.zip", SearchOption.AllDirectories));
        Assert.Equal(result.AnalysisExport.OutputPath, path);
        Assert.Equal(name + ".zip", Path.GetFileName(path));
        using var zip = ZipFile.OpenRead(path);
        using var markdownReader = new StreamReader(zip.GetEntry("analysis.md")!.Open());
        var markdown = await markdownReader.ReadToEndAsync();
        using var eventsReader = new StreamReader(zip.GetEntry("events.ndjson")!.Open());
        var lines = (await eventsReader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var metadata = JsonDocument.Parse(lines[0]);
        var json = metadata.RootElement.GetProperty("metadata");
        Assert.Equal(scope, json.GetProperty("scope").GetString());
        Assert.Contains(json.GetProperty("packageId").GetString()!, markdown);
        Assert.DoesNotContain(lines, line => line.Contains("SessionActivityPayload") && line.Contains("\"Ended\""));
        if (name == "run")
        {
            using var hierarchyReader = new StreamReader(zip.GetEntry("hierarchy.json")!.Open());
            using var hierarchy = JsonDocument.Parse(await hierarchyReader.ReadToEndAsync());
            foreach (var property in json.EnumerateObject())
                Assert.Equal(property.Value.ToString(), hierarchy.RootElement.GetProperty("metadata").GetProperty(property.Name).ToString());
        }
        return markdown;
    }

    [Fact]
    public async Task Cleanup_timeout_does_not_prevent_export_of_captured_run()
    {
        await using var fixture = new Fixture();
        fixture.Stream.HoldNextReleaseCommunication = true;
        var run = fixture.Run(Ready, ExportOptions(fixture));
        await fixture.AdvanceUntil(() => fixture.Stream.IsReleaseCommunicationPending);
        fixture.Time.Advance(RunConfigurationDefaults.DefaultCleanupTimeout + TimeSpan.FromSeconds(1));
        var result = await fixture.Finish(run);
        Assert.Equal(CliExitCode.CleanupFailed, CliOutcomeResolver.Resolve(result));
        Assert.False(result.Release!.IsSuccessful);
        await AssertPackage(fixture, result, "run", "Run");
        Assert.True(File.Exists(fixture.Output));
        Assert.False(fixture.Stream.IsReleaseCommunicationPending);
    }
}
