using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Common.Analysis;

/// <summary>
/// Creates all analysis-package documents from one immutable trace snapshot.
/// </summary>
public sealed class EvidenceAnalysisPackageBuilder
{
    private static readonly JsonSerializerOptions CompactJsonOptions = CreateJsonOptions(writeIndented: false);
    private static readonly JsonSerializerOptions IndentedJsonOptions = CreateJsonOptions(writeIndented: true);
    private readonly IEvidenceAnalysisFormatter analysisFormatter;

    public EvidenceAnalysisPackageBuilder(IEvidenceAnalysisFormatter? analysisFormatter = null)
    {
        this.analysisFormatter = analysisFormatter ?? new EvidenceAnalysisFormatter();
    }

    public EvidenceAnalysisPackage Build(
        EvidenceAnalysisSnapshot analysis,
        UnitTestActivityTraceSnapshot trace)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(trace);
        if (analysis.SessionId != trace.SessionId)
        {
            throw new ArgumentException("The analysis and activity trace belong to different application sessions.", nameof(trace));
        }

        var selectedEntries = SelectPackageEntries(analysis, trace);
        var activityFromSequence = selectedEntries.FirstOrDefault()?.Sequence ?? analysis.AnalyzedFromSequence;
        var activityThroughSequence = selectedEntries.LastOrDefault()?.Sequence ?? analysis.AnalyzedThroughSequence;
        var metadata = new EvidenceAnalysisPackageMetadata(
            new($"package:{analysis.Id.Value}"),
            analysis.Id,
            analysis.SessionId,
            analysis.Scope,
            analysis.RunEpochId,
            analysis.LifecycleObservationId,
            trace.CapturedAt,
            activityFromSequence,
            activityThroughSequence,
            analysis.Completeness);
        var hierarchy = EvidenceSupportingEvidenceFormatterFactory
            .Create(analysis.Scope)
            .CreateHierarchy(analysis, trace);

        return new EvidenceAnalysisPackage(
            metadata,
            analysis,
            FormatAnalysisMarkdown(analysis, metadata, hierarchy is not null),
            hierarchy is null ? null : FormatHierarchy(metadata, hierarchy),
            FormatEvents(metadata, selectedEntries));
    }

    private string FormatAnalysisMarkdown(
        EvidenceAnalysisSnapshot analysis,
        EvidenceAnalysisPackageMetadata metadata,
        bool hasHierarchy)
    {
        var builder = new StringBuilder(analysisFormatter
            .Format(analysis, EvidenceAnalysisFormat.Markdown)
            .TrimEnd());
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("## Package Evidence");
        if (hasHierarchy)
        {
            builder.AppendLine("- [Run hierarchy](hierarchy.json)");
        }

        builder.AppendLine("- [Ordered activity evidence](events.ndjson)");
        builder.AppendLine();
        builder.AppendLine("## Package Metadata");
        AppendMarkdownMetadata(builder, metadata);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string FormatHierarchy(
        EvidenceAnalysisPackageMetadata metadata,
        EvidenceReportHierarchy hierarchy)
    {
        return JsonSerializer.Serialize(
            new EvidenceHierarchyDocument(ProjectMetadata(metadata), hierarchy),
            IndentedJsonOptions) + Environment.NewLine;
    }

    private static string FormatEvents(
        EvidenceAnalysisPackageMetadata metadata,
        IReadOnlyList<UnitTestActivityEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine(JsonSerializer.Serialize(
            new EvidenceEventMetadataRecord("metadata", ProjectMetadata(metadata)),
            CompactJsonOptions));

        foreach (var entry in entries)
        {
            builder.AppendLine(JsonSerializer.Serialize(
                new EvidenceEventActivityRecord(
                    "activity",
                    entry.Sequence,
                    entry.ObservedAt,
                    entry.CommunicationSessionId?.Value,
                    entry.RunEpochId?.Value,
                    entry.LifecycleObservationId?.Value,
                    entry.EventStreamContextId?.Value,
                    entry.OperationId?.Value,
                    entry.Kind.ToString(),
                    entry.Payload.GetType().Name,
                    ProjectPayload(entry.Payload)),
                CompactJsonOptions));
        }

        return builder.ToString();
    }

    private static IReadOnlyList<UnitTestActivityEntry> SelectPackageEntries(
        EvidenceAnalysisSnapshot analysis,
        UnitTestActivityTraceSnapshot trace)
    {
        var referencedSequences = analysis.Incidents
            .SelectMany(static incident => incident.ObservedFacts
                .Concat(incident.DeterministicConclusions)
                .SelectMany(static statement => statement.Evidence)
                .Concat(incident.Timeline.SelectMany(static item => item.Evidence)))
            .Select(static reference => reference.ActivitySequence)
            .ToHashSet();

        return trace.Entries
            .Where(entry => entry.Sequence >= analysis.AnalyzedFromSequence
                    && entry.Sequence <= analysis.AnalyzedThroughSequence
                || referencedSequences.Contains(entry.Sequence))
            .OrderBy(static entry => entry.Sequence)
            .ToArray();
    }

    private static object ProjectPayload(UnitTestActivityPayload payload)
    {
        if (payload is not PlcEventActivityPayload plcEvent)
        {
            return payload;
        }

        var entry = plcEvent.Event;
        return new EvidencePlcEventActivityPayload(
            new EvidencePlcEventDocument(
                entry.SequenceId,
                entry.Timestamp,
                entry.OwnerScope,
                entry.EventType,
                entry.Severity,
                entry.Name,
                entry.ResourceName,
                entry.State,
                entry.Result,
                entry.Message),
            plcEvent.Kind);
    }

    private static EvidencePackageMetadataDocument ProjectMetadata(EvidenceAnalysisPackageMetadata metadata) =>
        new(
            metadata.PackageId.Value,
            metadata.SnapshotId.Value,
            metadata.SessionId.Value,
            metadata.Scope.ToString(),
            metadata.RunEpochId?.Value,
            metadata.LifecycleObservationId?.Value,
            metadata.CapturedAt,
            metadata.ActivityFromSequence,
            metadata.ActivityThroughSequence,
            metadata.Completeness.ToString());

    private static void AppendMarkdownMetadata(
        StringBuilder builder,
        EvidenceAnalysisPackageMetadata metadata)
    {
        builder.Append("- **Package:** ").AppendLine(metadata.PackageId.Value);
        builder.Append("- **Snapshot:** ").AppendLine(metadata.SnapshotId.Value);
        builder.Append("- **Session:** ").AppendLine(metadata.SessionId.Value);
        builder.Append("- **Scope:** ").AppendLine(metadata.Scope.ToString());
        builder.Append("- **Run epoch:** ").AppendLine(metadata.RunEpochId?.Value ?? "-");
        builder.Append("- **Lifecycle observation:** ").AppendLine(metadata.LifecycleObservationId?.Value ?? "-");
        builder.Append("- **Captured (PC UTC):** ").AppendLine(metadata.CapturedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        builder.Append("- **Activity range:** ")
            .Append(metadata.ActivityFromSequence.ToString(CultureInfo.InvariantCulture))
            .Append("..")
            .AppendLine(metadata.ActivityThroughSequence.ToString(CultureInfo.InvariantCulture));
        builder.Append("- **Completeness:** ").AppendLine(metadata.Completeness.ToString());
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record EvidencePackageMetadataDocument(
        string PackageId,
        string SnapshotId,
        string SessionId,
        string Scope,
        string? RunEpochId,
        string? LifecycleObservationId,
        DateTimeOffset PcCapturedAt,
        long ActivityFromSequence,
        long ActivityThroughSequence,
        string Completeness);

    private sealed record EvidenceHierarchyDocument(
        EvidencePackageMetadataDocument Metadata,
        EvidenceReportHierarchy Hierarchy);

    private sealed record EvidenceEventMetadataRecord(
        string RecordType,
        EvidencePackageMetadataDocument Metadata);

    private sealed record EvidenceEventActivityRecord(
        string RecordType,
        long Sequence,
        DateTimeOffset PcObservedAt,
        string? CommunicationSessionId,
        string? RunEpochId,
        string? LifecycleObservationId,
        string? EventStreamContextId,
        string? OperationId,
        string Kind,
        string PayloadType,
        object Payload);

    private sealed record EvidencePlcEventActivityPayload(
        EvidencePlcEventDocument Event,
        UnitTestActivityKind Kind);

    private sealed record EvidencePlcEventDocument(
        uint SequenceId,
        DateTimeOffset PlcTimestamp,
        UnitTestOwnerScope OwnerScope,
        UnitTestEventType EventType,
        Microsoft.Extensions.Logging.LogLevel Severity,
        string Name,
        string ResourceName,
        UnitTestLifecycleState State,
        UnitTestResultState Result,
        string Message);
}
