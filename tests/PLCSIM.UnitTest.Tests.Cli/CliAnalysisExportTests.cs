using System.IO.Compression;
using PLCSIM.UnitTest.App.Cli.Commands;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Cli;

public sealed class CliAnalysisExportTests
{
    [Theory]
    [InlineData(UnitTestActivityCommandKind.Start)]
    [InlineData(UnitTestActivityCommandKind.Reset)]
    [InlineData(UnitTestActivityCommandKind.PlcStart)]
    public void Requested_command_without_completion_selects_lifecycle(UnitTestActivityCommandKind command)
    {
        var export = new CliAnalysisExport(TimeProvider.System);
        export.Capture(Trace(Activity(1, new CommandActivityPayload(command, UnitTestActivityCommandPhase.Requested),
            new("attempt"))));

        Assert.Null(export.Error);
        Assert.Equal(EvidenceAnalysisScope.Lifecycle, export.Package!.Metadata.Scope);
        Assert.Equal(new LifecycleObservationId("attempt"), export.Package.Metadata.LifecycleObservationId);
        Assert.Contains("Requested", export.Package.EventsNdjson);
    }

    [Fact]
    public void Latest_request_selects_its_own_window()
    {
        var export = new CliAnalysisExport(TimeProvider.System);
        export.Capture(Trace(
            Activity(1, new CommandActivityPayload(UnitTestActivityCommandKind.Reset, UnitTestActivityCommandPhase.Requested), new("reset")),
            Activity(2, new CommandActivityPayload(UnitTestActivityCommandKind.Start, UnitTestActivityCommandPhase.Requested), new("start"))));

        Assert.Equal(new LifecycleObservationId("start"), export.Package!.Metadata.LifecycleObservationId);
    }

    [Fact]
    public void Run_epoch_takes_precedence_over_lifecycle_and_uses_same_snapshot()
    {
        var root = new UnitTestRunNode(new("root"), "RunControl", "RunControl", UnitTestNodeKind.RunControl,
            UnitTestLifecycleState.Running, UnitTestResultState.NotEvaluated, EvidenceState.Partial,
            null, null, null, null, null, null, null, [], []);
        var run = new UnitTestRunEpochSnapshot(new("run"), UnitTestRunState.Running,
            UnitTestResultState.NotEvaluated, EvidenceState.Partial, null, null, [], [], root);
        var oldRun = new UnitTestRunEpochSnapshot(new("foreign-run"), UnitTestRunState.Completed,
            UnitTestResultState.Passed, EvidenceState.Complete, null, null, [], [], root with { Name = "ForeignOwner" });
        var trace = Trace(
            Activity(1, new RunEpochActivityPayload(UnitTestRunEpochActivityKind.Ended, oldRun)) with { RunEpochId = oldRun.EpochId },
            Activity(2, new CommandActivityPayload(UnitTestActivityCommandKind.Start, UnitTestActivityCommandPhase.Requested), new("start")),
            Activity(3, new RunEpochActivityPayload(UnitTestRunEpochActivityKind.Started, run)) with { RunEpochId = run.EpochId });
        var export = new CliAnalysisExport(TimeProvider.System);
        export.Capture(trace);

        Assert.Null(export.Error);
        Assert.Equal(EvidenceAnalysisScope.Run, export.Package!.Metadata.Scope);
        Assert.Equal(run.EpochId, export.Package.Metadata.RunEpochId);
        Assert.Equal(trace.CapturedAt, export.Package.Metadata.CapturedAt);
        Assert.NotNull(export.Package.HierarchyJson);
        Assert.DoesNotContain("foreign-run", export.Package.EventsNdjson);
        Assert.DoesNotContain("ForeignOwner", export.Package.HierarchyJson);
    }

    [Theory]
    [InlineData(UnitTestActivityCommandPhase.Requested)]
    [InlineData(UnitTestActivityCommandPhase.Completed)]
    public void Evicted_prefix_is_reported_as_incomplete(UnitTestActivityCommandPhase retainedPhase)
    {
        var trace = Trace(Activity(20, new CommandActivityPayload(UnitTestActivityCommandKind.Reset,
            retainedPhase), new("reset"))) with { EvictedEntryCount = 19 };
        var export = new CliAnalysisExport(TimeProvider.System);
        export.Capture(trace);
        Assert.Null(export.Error);
        Assert.Equal(EvidenceAnalysisScope.Lifecycle, export.Package!.Metadata.Scope);
        Assert.Equal(EvidenceCompleteness.Incomplete, export.Package!.Metadata.Completeness);
    }

    [Fact]
    public async Task Capture_failure_is_reported_without_writing_a_package()
    {
        var export = new CliAnalysisExport(TimeProvider.System);
        var trace = Trace(Activity(1, new CommandActivityPayload(UnitTestActivityCommandKind.Start,
            UnitTestActivityCommandPhase.Requested), new LifecycleObservationId("")));
        export.Capture(trace);
        var result = await export.WriteAsync("unused");
        Assert.Null(result.OutputPath);
        Assert.Contains("capture failed", result.Error);
    }

    [Fact]
    public void Early_trace_selects_communication_health()
    {
        var export = new CliAnalysisExport(TimeProvider.System);
        export.Capture(Trace(Activity(1, new SessionActivityPayload(UnitTestSessionActivityKind.Started))));
        Assert.Null(export.Error);
        Assert.NotNull(export.Package);
        Assert.NotEqual(EvidenceAnalysisScope.Run, export.Package.Metadata.Scope);
        Assert.NotEqual(EvidenceAnalysisScope.Lifecycle, export.Package.Metadata.Scope);
    }

    [Fact]
    public void Cli_outcome_does_not_change_raw_trace_or_package_window()
    {
        var trace = Trace(Activity(1, new CommandActivityPayload(UnitTestActivityCommandKind.PlcStart,
            UnitTestActivityCommandPhase.Requested), new("startup")));
        var baseline = new CliAnalysisExport(TimeProvider.System);
        baseline.Capture(trace);
        var export = new CliAnalysisExport(TimeProvider.System);
        export.Capture(trace, new(CliFullRunState.TimedOut, null, null, [], "CLI readiness deadline expired."));
        Assert.Null(export.Error);
        Assert.Equal(baseline.Package!.EventsNdjson, export.Package!.EventsNdjson);
        Assert.Equal(baseline.Package.Metadata, export.Package.Metadata);
        Assert.Equal(baseline.Package.HierarchyJson, export.Package.HierarchyJson);
        Assert.Single(trace.Entries);
        var outcome = Assert.Single(export.Package.Analysis.Incidents.Where(item => item.Title == "CLI execution outcome"));
        Assert.Empty(outcome.Timeline);
        Assert.All(outcome.ObservedFacts, fact => Assert.Empty(fact.Evidence));
    }

    [Fact]
    public void Missing_trace_does_not_fabricate_a_report()
    {
        var export = new CliAnalysisExport(TimeProvider.System);
        export.Capture(null);
        Assert.Null(export.Package);
        Assert.Contains("unavailable", export.Error);
    }

    [Fact]
    public async Task Zip_exports_are_unique_and_contain_the_existing_documents()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var export = new CliAnalysisExport(TimeProvider.System);
            export.Capture(Trace(Activity(1, new CommandActivityPayload(UnitTestActivityCommandKind.Reset,
                UnitTestActivityCommandPhase.Requested), new("reset"))));
            var first = await export.WriteAsync(directory);
            var second = await export.WriteAsync(directory);
            Assert.Null(first.Error);
            Assert.Null(second.Error);
            Assert.NotEqual(first.OutputPath, second.OutputPath);
            using var archive = ZipFile.OpenRead(first.OutputPath!);
            Assert.NotNull(archive.GetEntry("analysis.md"));
            Assert.NotNull(archive.GetEntry("events.ndjson"));
            Assert.Equal(2, Directory.GetFiles(directory, "*.zip", SearchOption.AllDirectories).Length);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static UnitTestActivityEntry Activity(long sequence, UnitTestActivityPayload payload,
        LifecycleObservationId? lifecycle = null) =>
        new(sequence, DateTimeOffset.UtcNow, null, null, lifecycle, null, null, payload);

    [Fact]
    public async Task Cancelled_zip_write_preserves_existing_destination_and_removes_temporary_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.zip");
        try
        {
            await File.WriteAllTextAsync(path, "existing artifact");
            var export = new CliAnalysisExport(TimeProvider.System);
            export.Capture(Trace(Activity(1, new SessionActivityPayload(UnitTestSessionActivityKind.Started))));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EvidenceAnalysisPackageZipWriter()
                .WriteAsync(export.Package!, path, new CancellationToken(canceled: true)));
            Assert.Equal("existing artifact", await File.ReadAllTextAsync(path));
            Assert.Equal(path, Assert.Single(Directory.GetFiles(directory)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static UnitTestActivityTraceSnapshot Trace(params UnitTestActivityEntry[] entries) =>
        new(new("session"), DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow,
            true, entries[0].Sequence, entries[^1].Sequence, 0, entries);
}
