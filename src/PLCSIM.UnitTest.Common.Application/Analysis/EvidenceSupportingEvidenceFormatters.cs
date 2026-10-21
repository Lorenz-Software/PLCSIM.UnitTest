using System.Globalization;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Common.Analysis;

internal sealed record EvidenceReportField(string Name, string Value);

internal sealed record EvidenceReportActivity(
    long Sequence,
    string Label,
    IReadOnlyList<EvidenceReportField> Fields);

internal sealed record EvidenceReportHierarchy(
    long SourceActivitySequence,
    string SourcePcObservedAt,
    EvidenceReportRunNode Root);

internal sealed record EvidenceReportRunNode(
    string Id,
    string Name,
    string DisplayName,
    string Kind,
    string LifecycleState,
    string EffectiveResult,
    string? PlcReportedResult,
    string EvidenceState,
    string? PlcCreatedAt,
    string? PlcLastUpdatedAt,
    EvidenceReportAggregation? Aggregation,
    EvidenceReportEventEvidence EventEvidence,
    IReadOnlyList<EvidenceReportFinding> Findings,
    IReadOnlyList<EvidenceReportRunNode> Children);

internal sealed record EvidenceReportAggregation(
    uint Total,
    uint? Inactive,
    uint? Idle,
    uint? Running,
    uint? Completed,
    uint Passed,
    uint Failed,
    uint Error,
    uint? Runtime,
    uint? LostEvents);

internal sealed record EvidenceReportEventEvidence(
    uint? CreationSequenceId,
    uint? CompletionSequenceId,
    uint? ResultSequenceId,
    uint? AggregationSequenceId);

internal sealed record EvidenceReportFinding(
    string Kind,
    string Result,
    string Name,
    string Message,
    uint? SourceSequenceId);

internal interface IEvidenceSupportingEvidenceFormatter
{
    string SectionTitle { get; }

    long[] SelectSequences(
        EvidenceAnalysisSnapshot snapshot,
        UnitTestActivityTraceSnapshot trace,
        IReadOnlyList<long> referencedSequences);

    EvidenceReportActivity Project(UnitTestActivityEntry entry);

    EvidenceReportHierarchy? CreateHierarchy(
        EvidenceAnalysisSnapshot snapshot,
        UnitTestActivityTraceSnapshot trace);
}

internal static class EvidenceSupportingEvidenceFormatterFactory
{
    public static IEvidenceSupportingEvidenceFormatter Create(EvidenceAnalysisScope scope) => scope switch
    {
        EvidenceAnalysisScope.Run => RunSupportingEvidenceFormatter.Instance,
        EvidenceAnalysisScope.Lifecycle => LifecycleSupportingEvidenceFormatter.Instance,
        EvidenceAnalysisScope.Session => SessionSupportingEvidenceFormatter.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported evidence-analysis scope.")
    };
}

internal abstract class EvidenceSupportingEvidenceFormatterBase : IEvidenceSupportingEvidenceFormatter
{
    public virtual string SectionTitle => "Supporting Evidence";

    public virtual long[] SelectSequences(
        EvidenceAnalysisSnapshot snapshot,
        UnitTestActivityTraceSnapshot trace,
        IReadOnlyList<long> referencedSequences)
    {
        if (referencedSequences.Count > 0)
        {
            return referencedSequences.Distinct().Order().ToArray();
        }

        return SelectFocusSequences(snapshot, trace);
    }

    public abstract EvidenceReportActivity Project(UnitTestActivityEntry entry);

    public virtual EvidenceReportHierarchy? CreateHierarchy(
        EvidenceAnalysisSnapshot snapshot,
        UnitTestActivityTraceSnapshot trace) => null;

    protected abstract long[] SelectFocusSequences(
        EvidenceAnalysisSnapshot snapshot,
        UnitTestActivityTraceSnapshot trace);

    protected static EvidenceReportActivity Activity(
        UnitTestActivityEntry entry,
        string label,
        params EvidenceReportField[] fields)
    {
        return new EvidenceReportActivity(
            entry.Sequence,
            label,
            [Field("Observed (PC UTC)", Timestamp(entry.ObservedAt)), .. fields]);
    }

    protected static EvidenceReportField Field(string name, object? value) =>
        new(name, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "-");

    protected static EvidenceReportField OptionalField(string name, string? value) =>
        Field(name, string.IsNullOrWhiteSpace(value) ? "-" : value);

    protected static EvidenceReportField[] DiagnosticFields(DiagnosticActivityPayload payload) =>
        string.IsNullOrWhiteSpace(payload.Diagnostic.TechnicalDetail)
            ?
            [
                Field("Code", payload.Diagnostic.Code),
                Field("Level", payload.Diagnostic.Level),
                Field("Message", payload.Diagnostic.Message)
            ]
            :
            [
                Field("Code", payload.Diagnostic.Code),
                Field("Level", payload.Diagnostic.Level),
                Field("Message", payload.Diagnostic.Message),
                Field("Technical detail", payload.Diagnostic.TechnicalDetail)
            ];

    protected static EvidenceReportField[] CommandFields(CommandActivityPayload payload) =>
    [
        Field("Command", payload.Command),
        Field("Phase", payload.Phase),
        Field("Result", payload.ResultState),
        Field("Accepted", payload.Accepted),
        OptionalField("Message", payload.Message)
    ];

    protected static EvidenceReportField[] PlcStateFields(PlcStateActivityPayload payload) =>
    [
        Field("Instance state", payload.State.InstanceState),
        Field("Operating state", payload.State.OperatingState),
        Field("Ready for transfer", payload.State.ReadyForTransfer),
        Field("Ready for execution", payload.State.ReadyForExecution)
    ];

    protected static EvidenceReportField[] RunControlFields(RunControlActivityPayload payload) =>
    [
        Field("Execution status", payload.Observation.ExecutionStatus),
        Field("Execution signals", payload.Observation.ExecutionSignals),
        Field("State", payload.Observation.State),
        Field("Result", payload.Observation.Result)
    ];

    protected static EvidenceReportField[] EventStreamFields(EventStreamActivityPayload payload) =>
    [
        Field("Read state", payload.State.LastReadState),
        Field("Batch release state", payload.State.BatchReleaseState),
        Field("Buffer empty", payload.State.BufferObservedEmpty),
        Field("Buffer full", payload.State.BufferFull),
        Field("Lost events", payload.State.LostEventCount),
        Field("Producer sequence", payload.State.ProducerSequenceId),
        Field("Deleted sequence", payload.State.DeletedSequenceId)
    ];

    protected static EvidenceReportField[] AccessHealthFields(AccessHealthActivityPayload payload) =>
    [
        Field("Service", payload.ServiceKind),
        Field("Health", payload.Health.State),
        Field("Problem", payload.Health.Problem),
        Field("Reason", payload.Health.ReasonCode),
        Field("Message", payload.Health.Message)
    ];

    protected static EvidenceReportField[] PlcEventFields(PlcEventActivityPayload payload) =>
    [
        Field("PLC timestamp", Timestamp(payload.Event.Timestamp)),
        Field("PLC sequence", payload.Event.SequenceId),
        Field("Owner", $"{payload.Event.OwnerScope}: {payload.Event.Name}"),
        Field("Event", payload.Event.EventType),
        Field("State", payload.Event.State),
        Field("Result", payload.Event.Result),
        Field("Severity", payload.Event.Severity),
        Field("Message", payload.Event.Message)
    ];

    protected static EvidenceReportField[] RunEpochFields(RunEpochActivityPayload payload) =>
    [
        Field("Activity", payload.Activity),
        Field("State", payload.Snapshot.State),
        Field("Result", payload.Snapshot.Result),
        Field("Evidence state", payload.Snapshot.EvidenceState),
        Field("Events", payload.Snapshot.EventStream.Count),
        Field("Framework events", payload.Snapshot.FrameworkEvents.Count),
        Field("Lost events", payload.Snapshot.LostEventCount)
    ];

    protected static long[] SelectBoundarySequences<TPayload>(IReadOnlyList<UnitTestActivityEntry> entries)
        where TPayload : UnitTestActivityPayload
    {
        var matching = entries.Where(static entry => entry.Payload is TPayload).ToArray();
        if (matching.Length == 0)
        {
            return [];
        }

        return matching.Length == 1
            ? [matching[0].Sequence]
            : [matching[0].Sequence, matching[^1].Sequence];
    }

    protected static EvidenceReportActivity ProjectFallback(UnitTestActivityEntry entry) => entry.Payload switch
    {
        SessionActivityPayload payload => Activity(entry, "Session", Field("Activity", payload.Activity)),
        PlcStateActivityPayload payload => Activity(entry, "PLC state", PlcStateFields(payload)),
        CommandActivityPayload payload => Activity(entry, "Lifecycle command", CommandFields(payload)),
        RunControlActivityPayload payload => Activity(entry, "Run control", RunControlFields(payload)),
        EventStreamActivityPayload payload => Activity(entry, "Event stream", EventStreamFields(payload)),
        PlcEventActivityPayload payload => Activity(entry, "PLC event", PlcEventFields(payload)),
        RunEpochActivityPayload payload => Activity(entry, "Run epoch", RunEpochFields(payload)),
        AccessHealthActivityPayload payload => Activity(entry, "Access health", AccessHealthFields(payload)),
        DiagnosticActivityPayload payload => Activity(entry, "Diagnostic", DiagnosticFields(payload)),
        _ => Activity(entry, entry.Kind.ToString())
    };

    private static string Timestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

internal sealed class RunSupportingEvidenceFormatter : EvidenceSupportingEvidenceFormatterBase
{
    public static RunSupportingEvidenceFormatter Instance { get; } = new();

    private RunSupportingEvidenceFormatter()
    {
    }

    public override string SectionTitle => "Activity Timeline";

    public override long[] SelectSequences(
        EvidenceAnalysisSnapshot snapshot,
        UnitTestActivityTraceSnapshot trace,
        IReadOnlyList<long> referencedSequences)
    {
        if (snapshot.RunEpochId is null)
        {
            return referencedSequences.Distinct().Order().ToArray();
        }

        var orderedEntries = trace.Entries.OrderBy(static entry => entry.Sequence).ToArray();
        var epochEntries = orderedEntries
            .Where(entry => entry.RunEpochId == snapshot.RunEpochId)
            .ToArray();
        if (epochEntries.Length == 0)
        {
            return referencedSequences.Distinct().Order().ToArray();
        }

        var firstEpochSequence = epochEntries[0].Sequence;
        var lastEpochSequence = epochEntries[^1].Sequence;
        var precedingStart = orderedEntries
            .Where(entry => entry.Sequence < firstEpochSequence
                && entry.Payload is CommandActivityPayload
                {
                    Command: UnitTestActivityCommandKind.Start,
                    Phase: UnitTestActivityCommandPhase.Requested
                })
            .TakeLast(1)
            .FirstOrDefault();
        var firstTimelineSequence = precedingStart?.Sequence ?? firstEpochSequence;

        return orderedEntries
            .Where(entry => entry.Sequence >= firstTimelineSequence
                && entry.Sequence <= lastEpochSequence)
            .Select(static entry => entry.Sequence)
            .Concat(referencedSequences)
            .Distinct()
            .Order()
            .ToArray();
    }

    public override EvidenceReportActivity Project(UnitTestActivityEntry entry) => entry.Payload switch
    {
        PlcEventActivityPayload payload => Activity(entry, "PLC event", PlcEventFields(payload)),
        RunEpochActivityPayload payload => Activity(entry, "Run epoch", RunEpochFields(payload)),
        DiagnosticActivityPayload payload => Activity(entry, "Run diagnostic", DiagnosticFields(payload)),
        EventStreamActivityPayload payload => Activity(entry, "Event-stream state", EventStreamFields(payload)),
        _ => ProjectFallback(entry)
    };

    public override EvidenceReportHierarchy? CreateHierarchy(
        EvidenceAnalysisSnapshot snapshot,
        UnitTestActivityTraceSnapshot trace)
    {
        if (snapshot.RunEpochId is null)
        {
            return null;
        }

        var source = trace.Entries
            .Where(entry => entry.RunEpochId == snapshot.RunEpochId
                && entry.Payload is RunEpochActivityPayload)
            .OrderByDescending(static entry => entry.Sequence)
            .FirstOrDefault();
        if (source?.Payload is not RunEpochActivityPayload payload)
        {
            return null;
        }

        return new EvidenceReportHierarchy(
            source.Sequence,
            source.ObservedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ProjectNode(payload.Snapshot.Root));
    }

    protected override long[] SelectFocusSequences(
        EvidenceAnalysisSnapshot snapshot,
        UnitTestActivityTraceSnapshot trace)
    {
        if (snapshot.RunEpochId is null)
        {
            return [];
        }

        return trace.Entries
            .Where(entry => entry.RunEpochId == snapshot.RunEpochId
                && entry.Payload is RunEpochActivityPayload)
            .Select(static entry => entry.Sequence)
            .Distinct()
            .Order()
            .ToArray();
    }

    private static EvidenceReportRunNode ProjectNode(UnitTestRunNode node)
    {
        return new EvidenceReportRunNode(
            node.Id.Value,
            node.Name,
            node.DisplayName,
            node.Kind.ToString(),
            node.LifecycleState.ToString(),
            node.Result.ToString(),
            node.PlcReportedResult?.ToString(),
            node.EvidenceState.ToString(),
            FormatOptionalTimestamp(node.CreatedAt),
            FormatOptionalTimestamp(node.LastUpdatedAt),
            ProjectAggregation(node.Aggregation),
            new EvidenceReportEventEvidence(
                node.CreationEvent?.SequenceId,
                node.CompletionEvent?.SequenceId,
                node.ResultEvent?.SequenceId,
                node.AggregationEvent?.SequenceId),
            node.Findings.Select(static finding => new EvidenceReportFinding(
                finding.Kind.ToString(),
                finding.Result.ToString(),
                finding.Name,
                finding.Message,
                finding.SourceEvent?.SequenceId)).ToArray(),
            node.Children.Select(ProjectNode).ToArray());
    }

    private static string? FormatOptionalTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null
            ? null
            : timestamp.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static EvidenceReportAggregation? ProjectAggregation(UnitTestAggregation? aggregation)
    {
        if (aggregation is null)
        {
            return null;
        }

        return new EvidenceReportAggregation(
            aggregation.Total,
            aggregation.Inactive,
            aggregation.Idle,
            aggregation.Running,
            aggregation.Completed,
            aggregation.Passed,
            aggregation.Failed,
            aggregation.Error,
            aggregation.Runtime,
            aggregation.LostEvents);
    }
}

internal sealed class LifecycleSupportingEvidenceFormatter : EvidenceSupportingEvidenceFormatterBase
{
    public static LifecycleSupportingEvidenceFormatter Instance { get; } = new();

    private LifecycleSupportingEvidenceFormatter()
    {
    }

    public override EvidenceReportActivity Project(UnitTestActivityEntry entry) => entry.Payload switch
    {
        CommandActivityPayload payload => Activity(entry, "Lifecycle command", CommandFields(payload)),
        RunControlActivityPayload payload => Activity(entry, "Run-control observation", RunControlFields(payload)),
        PlcStateActivityPayload payload => Activity(entry, "PLC state", PlcStateFields(payload)),
        DiagnosticActivityPayload payload => Activity(entry, "Lifecycle diagnostic", DiagnosticFields(payload)),
        AccessHealthActivityPayload payload => Activity(entry, "Access health", AccessHealthFields(payload)),
        EventStreamActivityPayload payload => Activity(entry, "Event-stream boundary", EventStreamFields(payload)),
        PlcEventActivityPayload payload => Activity(entry, "PLC lifecycle event", PlcEventFields(payload)),
        _ => ProjectFallback(entry)
    };

    protected override long[] SelectFocusSequences(
        EvidenceAnalysisSnapshot snapshot,
        UnitTestActivityTraceSnapshot trace)
    {
        var entries = trace.Entries
            .Where(entry => snapshot.LifecycleObservationId is null
                || entry.LifecycleObservationId == snapshot.LifecycleObservationId)
            .OrderBy(static entry => entry.Sequence)
            .ToArray();
        if (entries.Length == 0)
        {
            return [];
        }

        return entries
            .Where(static entry => entry.Payload is CommandActivityPayload
                or DiagnosticActivityPayload
                or AccessHealthActivityPayload
                or PlcStateActivityPayload)
            .Select(static entry => entry.Sequence)
            .Concat(SelectBoundarySequences<RunControlActivityPayload>(entries))
            .Concat(SelectBoundarySequences<EventStreamActivityPayload>(entries))
            .Append(entries[0].Sequence)
            .Append(entries[^1].Sequence)
            .Distinct()
            .Order()
            .ToArray();
    }
}

internal sealed class SessionSupportingEvidenceFormatter : EvidenceSupportingEvidenceFormatterBase
{
    public static SessionSupportingEvidenceFormatter Instance { get; } = new();

    private SessionSupportingEvidenceFormatter()
    {
    }

    public override EvidenceReportActivity Project(UnitTestActivityEntry entry) => entry.Payload switch
    {
        SessionActivityPayload payload => Activity(entry, "Application session", Field("Activity", payload.Activity)),
        PlcStateActivityPayload payload => Activity(entry, "PLC state", PlcStateFields(payload)),
        CommandActivityPayload payload => Activity(entry, "Command", CommandFields(payload)),
        AccessHealthActivityPayload payload => Activity(entry, "Access health", AccessHealthFields(payload)),
        DiagnosticActivityPayload payload => Activity(entry, "Session diagnostic", DiagnosticFields(payload)),
        EventStreamActivityPayload payload => Activity(entry, "Event-stream boundary", EventStreamFields(payload)),
        RunControlActivityPayload payload => Activity(entry, "Latest run-control observation", RunControlFields(payload)),
        _ => ProjectFallback(entry)
    };

    protected override long[] SelectFocusSequences(
        EvidenceAnalysisSnapshot snapshot,
        UnitTestActivityTraceSnapshot trace)
    {
        var entries = trace.Entries.OrderBy(static entry => entry.Sequence).ToArray();
        if (entries.Length == 0)
        {
            return [];
        }

        return entries
            .Where(static entry => entry.Payload is SessionActivityPayload
                or PlcStateActivityPayload
                or CommandActivityPayload
                or AccessHealthActivityPayload
                or DiagnosticActivityPayload)
            .Select(static entry => entry.Sequence)
            .Concat(SelectBoundarySequences<EventStreamActivityPayload>(entries))
            .Concat(entries.Where(static entry => entry.Payload is RunControlActivityPayload)
                .TakeLast(1)
                .Select(static entry => entry.Sequence))
            .Distinct()
            .Order()
            .ToArray();
    }
}
