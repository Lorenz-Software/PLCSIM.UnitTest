using System.IO.Compression;
using System.Text.Json;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.UnitTests;
using Xunit;

namespace PLCSIM.UnitTest.Tests.Core.CommonContracts;

public sealed class EvidenceAnalysisPackageTests
{
    [Fact]
    public void Export_preserves_retained_buffer_metadata_and_pc_batch_state()
    {
        var state = new PLCSIM.UnitTest.Common.Services.EventStreamState(
            new CommunicationSessionId("session-1"), false, EventBufferReadState.SessionInvalid,
            EventStreamBatchReleaseState.Completed, false, 7, true, 412, 256);
        var package = new EvidenceAnalysisPackageBuilder().Build(
            Analysis(EvidenceAnalysisScope.Session, null, 10, 10),
            Trace([Activity(10, new EventStreamActivityPayload(state))]));
        var line = package.EventsNdjson.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[1];
        using var document = JsonDocument.Parse(line);
        var exported = document.RootElement.GetProperty("payload").GetProperty("state");
        Assert.Equal("Completed", exported.GetProperty("batchReleaseState").GetString());
        Assert.Equal("SessionInvalid", exported.GetProperty("lastReadState").GetString());
        Assert.Equal(412u, exported.GetProperty("producerSequenceId").GetUInt32());
        Assert.Equal(256u, exported.GetProperty("deletedSequenceId").GetUInt32());
        Assert.Equal(7, exported.GetProperty("lostEventCount").GetInt32());
        Assert.False(exported.GetProperty("bufferObservedEmpty").GetBoolean());
        Assert.False(exported.TryGetProperty("releaseState", out _));
    }

    [Fact]
    public void Build_creates_synchronized_run_documents_from_one_trace_snapshot()
    {
        var entries = new List<UnitTestActivityEntry>
        {
            Activity(10, new SessionActivityPayload(UnitTestSessionActivityKind.Started)),
            Activity(11, new CommandActivityPayload(UnitTestActivityCommandKind.Start, UnitTestActivityCommandPhase.Completed, Accepted: true)),
            Activity(12, new RunEpochActivityPayload(UnitTestRunEpochActivityKind.Ended, RunSnapshot()))
        };
        var trace = Trace(entries);
        var analysis = Analysis(EvidenceAnalysisScope.Run, new RunEpochId("run-1"), 10, 12);

        var package = new EvidenceAnalysisPackageBuilder().Build(analysis, trace);

        Assert.Equal("package:snapshot-1", package.Metadata.PackageId.Value);
        Assert.Contains("[Run hierarchy](hierarchy.json)", package.AnalysisMarkdown, StringComparison.Ordinal);
        Assert.Contains("package:snapshot-1", package.AnalysisMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("# Run Hierarchy Snapshot", package.AnalysisMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("# Activity Timeline", package.AnalysisMarkdown, StringComparison.Ordinal);

        using var hierarchy = JsonDocument.Parse(package.HierarchyJson!);
        AssertMetadata(hierarchy.RootElement.GetProperty("metadata"));
        Assert.Equal("run-control", hierarchy.RootElement
            .GetProperty("hierarchy")
            .GetProperty("root")
            .GetProperty("id")
            .GetString());

        var lines = package.EventsNdjson.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        using var metadata = JsonDocument.Parse(lines[0]);
        Assert.Equal("metadata", metadata.RootElement.GetProperty("recordType").GetString());
        AssertMetadata(metadata.RootElement.GetProperty("metadata"));
        Assert.Equal([10L, 11L, 12L], lines.Skip(1).Select(ReadSequence));
        using var command = JsonDocument.Parse(lines[2]);
        Assert.Equal("CommandActivityPayload", command.RootElement.GetProperty("payloadType").GetString());
        Assert.Equal("Start", command.RootElement.GetProperty("payload").GetProperty("command").GetString());
        Assert.True(command.RootElement.GetProperty("payload").GetProperty("accepted").GetBoolean());

        entries.Add(Activity(13, new SessionActivityPayload(UnitTestSessionActivityKind.Ended)));
        Assert.DoesNotContain("\"sequence\":13", package.EventsNdjson, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_omits_hierarchy_for_lifecycle_analysis()
    {
        var package = new EvidenceAnalysisPackageBuilder().Build(
            Analysis(EvidenceAnalysisScope.Lifecycle, null, 10, 10),
            Trace([Activity(10, new SessionActivityPayload(UnitTestSessionActivityKind.Started))]));

        Assert.Null(package.HierarchyJson);
        Assert.DoesNotContain("hierarchy.json", package.AnalysisMarkdown, StringComparison.Ordinal);
        Assert.Contains("events.ndjson", package.AnalysisMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_includes_referenced_evidence_outside_the_analysis_window_without_exporting_unrelated_history()
    {
        var reference = new EvidenceReference(2);
        var incident = new EvidenceIncident(
            new EvidenceIncidentId("incident-1"),
            EvidenceIncidentClassification.CommunicationFailure,
            EvidenceIncidentSeverity.Information,
            EvidenceCompleteness.Complete,
            EvidenceIncidentScope.Session,
            "Recovered history",
            "Earlier evidence remains relevant.",
            Timestamp(),
            Timestamp(),
            null,
            null,
            [],
            [new EvidenceAnalysisStatement("history", "Earlier evidence.", [reference])],
            [],
            [],
            []);
        var analysis = Analysis(EvidenceAnalysisScope.Session, null, 10, 12) with
        {
            Incidents = [incident]
        };
        var trace = Trace(
        [
            Activity(2, new SessionActivityPayload(UnitTestSessionActivityKind.Started)),
            Activity(3, new SessionActivityPayload(UnitTestSessionActivityKind.Started)),
            Activity(10, new SessionActivityPayload(UnitTestSessionActivityKind.Started)),
            Activity(11, new SessionActivityPayload(UnitTestSessionActivityKind.Started)),
            Activity(12, new SessionActivityPayload(UnitTestSessionActivityKind.Ended))
        ]);

        var package = new EvidenceAnalysisPackageBuilder().Build(analysis, trace);
        var lines = package.EventsNdjson.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal([2L, 10L, 11L, 12L], lines.Skip(1).Select(ReadSequence));
        Assert.DoesNotContain("\"sequence\":3", package.EventsNdjson, StringComparison.Ordinal);
        Assert.Equal(2, package.Metadata.ActivityFromSequence);
        Assert.Equal(12, package.Metadata.ActivityThroughSequence);
    }

    [Fact]
    public void Build_labels_pc_and_plc_time_domains_explicitly()
    {
        var plcTimestamp = Timestamp().AddDays(-1);
        var plcEvent = new UnitTestEventEntry(
            42,
            plcTimestamp,
            UnitTestOwnerScope.TestCase,
            UnitTestEventType.Result,
            Microsoft.Extensions.Logging.LogLevel.Information,
            "Run.Suite.Case",
            "Case",
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Passed,
            "Passed.");
        var entries = new[]
        {
            Activity(10, new PlcEventActivityPayload(plcEvent)) with { RunEpochId = new RunEpochId("run-1") },
            Activity(11, new RunEpochActivityPayload(UnitTestRunEpochActivityKind.Ended, RunSnapshot()))
        };

        var package = new EvidenceAnalysisPackageBuilder().Build(
            Analysis(EvidenceAnalysisScope.Run, new RunEpochId("run-1"), 10, 11),
            Trace(entries));

        var lines = package.EventsNdjson.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        using var metadata = JsonDocument.Parse(lines[0]);
        Assert.Equal(Timestamp().AddSeconds(1), metadata.RootElement
            .GetProperty("metadata")
            .GetProperty("pcCapturedAt")
            .GetDateTimeOffset());
        Assert.False(metadata.RootElement.GetProperty("metadata").TryGetProperty("capturedAt", out _));

        using var activity = JsonDocument.Parse(lines[1]);
        Assert.Equal(Timestamp().AddMilliseconds(10), activity.RootElement.GetProperty("pcObservedAt").GetDateTimeOffset());
        var eventPayload = activity.RootElement.GetProperty("payload").GetProperty("event");
        Assert.Equal(plcTimestamp, eventPayload.GetProperty("plcTimestamp").GetDateTimeOffset());
        Assert.False(eventPayload.TryGetProperty("timestamp", out _));
        Assert.False(activity.RootElement.TryGetProperty("plcEventTimestamp", out _));
        Assert.False(activity.RootElement.TryGetProperty("observedAt", out _));

        using var hierarchy = JsonDocument.Parse(package.HierarchyJson!);
        var hierarchyElement = hierarchy.RootElement.GetProperty("hierarchy");
        Assert.Equal(Timestamp().AddMilliseconds(11), hierarchyElement.GetProperty("sourcePcObservedAt").GetDateTimeOffset());
        var root = hierarchyElement.GetProperty("root");
        Assert.Equal(Timestamp(), root.GetProperty("plcCreatedAt").GetDateTimeOffset());
        Assert.Equal(Timestamp().AddSeconds(1), root.GetProperty("plcLastUpdatedAt").GetDateTimeOffset());
        Assert.False(root.TryGetProperty("createdAt", out _));
        Assert.False(root.TryGetProperty("lastUpdatedAt", out _));
        Assert.Contains("Generated (PC UTC)", package.AnalysisMarkdown, StringComparison.Ordinal);
        Assert.Contains("Captured (PC UTC)", package.AnalysisMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zip_writer_replaces_destination_with_exact_package_documents()
    {
        var package = new EvidenceAnalysisPackageBuilder().Build(
            Analysis(EvidenceAnalysisScope.Run, new RunEpochId("run-1"), 10, 12),
            Trace(
            [
                Activity(10, new SessionActivityPayload(UnitTestSessionActivityKind.Started)),
                Activity(12, new RunEpochActivityPayload(UnitTestRunEpochActivityKind.Ended, RunSnapshot()))
            ]));
        var directory = Path.Combine(Path.GetTempPath(), $"plcsim-unittest-package-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "analysis.zip");

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "old content");

            await new EvidenceAnalysisPackageZipWriter().WriteAsync(package, path);

            using var archive = ZipFile.OpenRead(path);
            Assert.Equal(
                new[] { "analysis.md", "events.ndjson", "hierarchy.json" },
                archive.Entries.Select(static entry => entry.FullName).Order().ToArray());
            Assert.Equal(package.AnalysisMarkdown, await ReadEntryAsync(archive, "analysis.md"));
            Assert.Equal(package.HierarchyJson, await ReadEntryAsync(archive, "hierarchy.json"));
            Assert.Equal(package.EventsNdjson, await ReadEntryAsync(archive, "events.ndjson"));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Build_rejects_trace_from_another_session()
    {
        var trace = Trace([Activity(10, new SessionActivityPayload(UnitTestSessionActivityKind.Started))]) with
        {
            SessionId = new ApplicationSessionId("other-session")
        };

        Assert.Throws<ArgumentException>(() => new EvidenceAnalysisPackageBuilder().Build(
            Analysis(EvidenceAnalysisScope.Session, null, 10, 10),
            trace));
    }

    private static EvidenceAnalysisSnapshot Analysis(
        EvidenceAnalysisScope scope,
        RunEpochId? runEpochId,
        long from,
        long through) =>
        EvidenceAnalysisSnapshot.Empty(
            new EvidenceSnapshotId("snapshot-1"),
            new ApplicationSessionId("session-1"),
            scope,
            runEpochId,
            scope == EvidenceAnalysisScope.Lifecycle ? new LifecycleObservationId("lifecycle-1") : null,
            Timestamp().AddSeconds(2),
            from,
            through,
            EvidenceCompleteness.Complete);

    private static UnitTestActivityTraceSnapshot Trace(IReadOnlyList<UnitTestActivityEntry> entries) =>
        new(
            new ApplicationSessionId("session-1"),
            Timestamp(),
            null,
            Timestamp().AddSeconds(1),
            true,
            entries.Count == 0 ? 0 : entries.Min(static entry => entry.Sequence),
            entries.Count == 0 ? 0 : entries.Max(static entry => entry.Sequence),
            0,
            entries);

    private static UnitTestActivityEntry Activity(long sequence, UnitTestActivityPayload payload) =>
        new(
            sequence,
            Timestamp().AddMilliseconds(sequence),
            new CommunicationSessionId("communication-1"),
            payload is RunEpochActivityPayload ? new RunEpochId("run-1") : null,
            null,
            null,
            null,
            payload);

    private static UnitTestRunEpochSnapshot RunSnapshot()
    {
        var root = new UnitTestRunNode(
            new UnitTestNodeId("run-control"),
            "RunControl",
            "Run Control",
            UnitTestNodeKind.RunControl,
            UnitTestLifecycleState.Completed,
            UnitTestResultState.Passed,
            EvidenceState.Complete,
            null,
            null,
            null,
            null,
            null,
            Timestamp(),
            Timestamp().AddSeconds(1),
            [],
            []);
        return new UnitTestRunEpochSnapshot(
            new RunEpochId("run-1"),
            UnitTestRunState.Completed,
            UnitTestResultState.Passed,
            EvidenceState.Complete,
            Timestamp(),
            Timestamp().AddSeconds(1),
            [],
            [],
            root);
    }

    private static void AssertMetadata(JsonElement metadata)
    {
        Assert.Equal("package:snapshot-1", metadata.GetProperty("packageId").GetString());
        Assert.Equal("snapshot-1", metadata.GetProperty("snapshotId").GetString());
        Assert.Equal("session-1", metadata.GetProperty("sessionId").GetString());
        Assert.Equal("Run", metadata.GetProperty("scope").GetString());
        Assert.Equal("run-1", metadata.GetProperty("runEpochId").GetString());
        Assert.Equal(Timestamp().AddSeconds(1), metadata.GetProperty("pcCapturedAt").GetDateTimeOffset());
        Assert.Equal(10, metadata.GetProperty("activityFromSequence").GetInt64());
        Assert.Equal(12, metadata.GetProperty("activityThroughSequence").GetInt64());
        Assert.Equal("Complete", metadata.GetProperty("completeness").GetString());
    }

    private static long ReadSequence(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal("activity", document.RootElement.GetProperty("recordType").GetString());
        return document.RootElement.GetProperty("sequence").GetInt64();
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new Xunit.Sdk.XunitException($"ZIP entry '{name}' was not found.");
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static DateTimeOffset Timestamp() =>
        new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
}
