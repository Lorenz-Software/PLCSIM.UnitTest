using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Core.Diagnostics;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.Diagnostics;

public sealed class DiagnosticSinkTests
{
    [Fact]
    public void Add_coalesces_repeated_finding_and_updates_occurrence_metadata()
    {
        var sink = new DiagnosticSink();
        var first = CreateDiagnostic(new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero), "First.");
        var second = CreateDiagnostic(new DateTimeOffset(2026, 6, 19, 12, 1, 0, TimeSpan.Zero), "Second.");

        sink.Add(first);
        var snapshot = sink.Add(second);

        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(2, entry.OccurrenceCount);
        Assert.Equal(first.Timestamp, entry.FirstSeenAt);
        Assert.Equal(second.Timestamp, entry.LastSeenAt);
        Assert.Equal("Second.", entry.Current.Message);
    }

    [Fact]
    public void Add_keeps_materially_different_context_as_distinct_entry()
    {
        var sink = new DiagnosticSink();

        sink.Add(CreateDiagnostic(
            new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero),
            "Runtime A.",
            new DiagnosticCorrelation(RuntimeTargetId: new RuntimeTargetId("runtime-a"))));
        var snapshot = sink.Add(CreateDiagnostic(
            new DateTimeOffset(2026, 6, 19, 12, 1, 0, TimeSpan.Zero),
            "Runtime B.",
            new DiagnosticCorrelation(RuntimeTargetId: new RuntimeTargetId("runtime-b"))));

        Assert.Equal(2, snapshot.Entries.Count);
    }

    [Fact]
    public void Add_coalesces_repeated_finding_across_volatile_run_context()
    {
        var sink = new DiagnosticSink();

        sink.Add(CreateDiagnostic(
            new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero),
            "First run.",
            new DiagnosticCorrelation(
                OperationId: new OperationId("op-1"),
                RuntimeTargetId: new RuntimeTargetId("runtime-a"),
                UnitTestRunEpochId: new RunEpochId("epoch-1"),
                EvidenceSnapshotId: new EvidenceSnapshotId("evidence-1"))));
        var snapshot = sink.Add(CreateDiagnostic(
            new DateTimeOffset(2026, 6, 19, 12, 1, 0, TimeSpan.Zero),
            "Second run.",
            new DiagnosticCorrelation(
                OperationId: new OperationId("op-2"),
                RuntimeTargetId: new RuntimeTargetId("runtime-a"),
                UnitTestRunEpochId: new RunEpochId("epoch-2"),
                EvidenceSnapshotId: new EvidenceSnapshotId("evidence-2"))));

        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(2, entry.OccurrenceCount);
        Assert.Equal("Second run.", entry.Current.Message);
        Assert.Equal("op-2", entry.Current.Correlation.OperationId?.Value);
        Assert.Equal("epoch-2", entry.Current.Correlation.UnitTestRunEpochId?.Value);
        Assert.Null(entry.Fingerprint.CorrelationScope.OperationId);
        Assert.Null(entry.Fingerprint.CorrelationScope.UnitTestRunEpochId);
        Assert.Equal("runtime-a", entry.Fingerprint.CorrelationScope.RuntimeTargetId?.Value);
    }

    [Fact]
    public void Add_does_not_move_snapshot_update_time_backwards()
    {
        var sink = new DiagnosticSink();
        var first = CreateDiagnostic(new DateTimeOffset(2026, 6, 19, 12, 2, 0, TimeSpan.Zero), "First.");
        var older = CreateDiagnostic(new DateTimeOffset(2026, 6, 19, 12, 1, 0, TimeSpan.Zero), "Older.");

        sink.Add(first);
        var snapshot = sink.Add(older);

        Assert.Equal(first.Timestamp, snapshot.UpdatedAt);
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(first.Timestamp, entry.LastSeenAt);
        Assert.Equal("First.", entry.Current.Message);
        Assert.Equal(2, entry.OccurrenceCount);
    }

    [Fact]
    public void Add_publishes_snapshot_changed_event()
    {
        var sink = new DiagnosticSink();
        var changes = new List<DiagnosticSnapshot>();
        sink.SnapshotChangedEventHandler += (_, args) => changes.Add(args.Snapshot);

        var snapshot = sink.Add(CreateDiagnostic(new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero), "First."));

        Assert.Same(snapshot, Assert.Single(changes));
    }

    [Fact]
    public void AddRange_coalesces_batch_and_updates_snapshot_once_to_latest_timestamp()
    {
        var sink = new DiagnosticSink();
        var first = CreateDiagnostic(new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero), "First.");
        var second = CreateDiagnostic(new DateTimeOffset(2026, 6, 19, 12, 2, 0, TimeSpan.Zero), "Second.");
        var third = CreateDiagnostic(
            new DateTimeOffset(2026, 6, 19, 12, 1, 0, TimeSpan.Zero),
            "Runtime B.",
            new DiagnosticCorrelation(RuntimeTargetId: new RuntimeTargetId("runtime-b")));

        var snapshot = sink.AddRange([first, second, third]);

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.Equal(second.Timestamp, snapshot.UpdatedAt);

        var runtimeA = Assert.Single(snapshot.Entries, entry => entry.Fingerprint.CorrelationScope.RuntimeTargetId?.Value == "runtime-a");
        Assert.Equal(2, runtimeA.OccurrenceCount);
        Assert.Equal(first.Timestamp, runtimeA.FirstSeenAt);
        Assert.Equal(second.Timestamp, runtimeA.LastSeenAt);
        Assert.Equal("Second.", runtimeA.Current.Message);
    }

    [Fact]
    public void AddRange_coalesces_framework_event_diagnostics_across_run_epoch_and_evidence_context()
    {
        var sink = new DiagnosticSink();
        var first = CreateFrameworkEventDiagnostic(
            new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero),
            "epoch-1",
            "1",
            "Event buffer overflow detected.");
        var second = CreateFrameworkEventDiagnostic(
            new DateTimeOffset(2026, 6, 19, 12, 1, 0, TimeSpan.Zero),
            "epoch-2",
            "2",
            "Event buffer overflow detected again.");

        var snapshot = sink.AddRange([first, second]);

        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal("Workflow.EventStreamProcessor.FrameworkEvent", entry.Current.Code);
        Assert.Equal(2, entry.OccurrenceCount);
        Assert.Equal(second.Timestamp, entry.LastSeenAt);
        Assert.Equal("2", entry.Current.EvidenceReference?.Reference);
        Assert.Equal(new RunEpochId("epoch-2"), entry.Current.Correlation.UnitTestRunEpochId);
        Assert.Null(entry.Fingerprint.CorrelationScope.UnitTestRunEpochId);
    }

    [Fact]
    public void AddRange_with_empty_sequence_keeps_current_snapshot_instance()
    {
        var sink = new DiagnosticSink();
        var current = sink.Current;
        var changes = new List<DiagnosticSnapshot>();
        sink.SnapshotChangedEventHandler += (_, args) => changes.Add(args.Snapshot);

        var snapshot = sink.AddRange(Array.Empty<Diagnostic>());

        Assert.Same(current, snapshot);
        Assert.Empty(snapshot.Entries);
        Assert.Empty(changes);
    }

    [Fact]
    public void Clear_removes_current_entries_and_updates_snapshot_time()
    {
        var sink = new DiagnosticSink();
        var clearedAt = new DateTimeOffset(2026, 6, 19, 12, 2, 0, TimeSpan.Zero);
        sink.Add(CreateDiagnostic(new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero), "First."));

        var snapshot = sink.Clear(clearedAt);

        Assert.Empty(snapshot.Entries);
        Assert.Empty(sink.Current.Entries);
        Assert.Equal(clearedAt, snapshot.UpdatedAt);
    }

    [Fact]
    public void Clear_publishes_snapshot_changed_event()
    {
        var sink = new DiagnosticSink();
        var clearedAt = new DateTimeOffset(2026, 6, 19, 12, 2, 0, TimeSpan.Zero);
        var changes = new List<DiagnosticSnapshot>();
        sink.Add(CreateDiagnostic(new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero), "First."));
        sink.SnapshotChangedEventHandler += (_, args) => changes.Add(args.Snapshot);

        var snapshot = sink.Clear(clearedAt);

        Assert.Same(snapshot, Assert.Single(changes));
    }

    private static Diagnostic CreateDiagnostic(
        DateTimeOffset timestamp,
        string message,
        DiagnosticCorrelation? correlation = null)
    {
        return new Diagnostic(
            "Workflow.Test",
            DiagnosticCategory.Workflow,
            LogLevel.Warning,
            DiagnosticSource.SharedWorkflow,
            DiagnosticPhase.ExecutionMonitoring,
            message,
            correlation ?? new DiagnosticCorrelation(RuntimeTargetId: new RuntimeTargetId("runtime-a")),
            timestamp);
    }

    private static Diagnostic CreateFrameworkEventDiagnostic(
        DateTimeOffset timestamp,
        string epochId,
        string eventSequenceId,
        string message)
    {
        return new Diagnostic(
            "Workflow.EventStreamProcessor.FrameworkEvent",
            DiagnosticCategory.Workflow,
            LogLevel.Error,
            DiagnosticSource.RunObservation,
            DiagnosticPhase.ExecutionMonitoring,
            message,
            new DiagnosticCorrelation(UnitTestRunEpochId: new RunEpochId(epochId)),
            timestamp,
            TechnicalDetail: "Framework event reported a runtime problem.",
            EvidenceReference: new DiagnosticEvidenceReference("unit-test-event", eventSequenceId));
    }
}
