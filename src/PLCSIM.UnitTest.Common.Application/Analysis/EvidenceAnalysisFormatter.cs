using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PLCSIM.UnitTest.Common.Analysis;

public enum EvidenceAnalysisFormat
{
    PlainText,
    Markdown
}

public interface IEvidenceAnalysisFormatter
{
    string Format(EvidenceAnalysisSnapshot snapshot, EvidenceAnalysisFormat format);

    string Format(
        EvidenceAnalysisSnapshot snapshot,
        UnitTestActivityTraceSnapshot trace,
        EvidenceAnalysisFormat format);
}

/// <summary>
/// Stable, deterministic text representation of an evidence-analysis snapshot.
/// </summary>
public sealed class EvidenceAnalysisFormatter : IEvidenceAnalysisFormatter
{
    private const int AssessmentDescriptionLineLength = 100;

    private static readonly JsonSerializerOptions HierarchyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public string Format(EvidenceAnalysisSnapshot snapshot, EvidenceAnalysisFormat format)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return format switch
        {
            EvidenceAnalysisFormat.Markdown => FormatMarkdown(snapshot),
            EvidenceAnalysisFormat.PlainText => FormatPlainText(snapshot),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported evidence-analysis format.")
        };
    }

    public string Format(
        EvidenceAnalysisSnapshot snapshot,
        UnitTestActivityTraceSnapshot trace,
        EvidenceAnalysisFormat format)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(trace);
        if (snapshot.SessionId != trace.SessionId)
        {
            throw new ArgumentException("The analysis and activity trace belong to different application sessions.", nameof(trace));
        }

        var report = Format(snapshot, format).TrimEnd();
        var appendix = FormatReferencedEvidence(snapshot, trace, format);
        return report + Environment.NewLine + Environment.NewLine + appendix.TrimEnd() + Environment.NewLine;
    }

    private static string FormatMarkdown(EvidenceAnalysisSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(AnalysisTitle(snapshot.Scope)).AppendLine(" Evidence Analysis");
        builder.AppendLine();
        builder.AppendLine("## Assessment");
        builder.Append("**").Append(snapshot.Assessment).AppendLine("**");
        builder.AppendLine();
        AppendWrappedAssessmentDescription(builder, snapshot.Summary);

        builder.AppendLine();
        builder.AppendLine("## Findings");
        if (snapshot.Incidents.Count == 0)
        {
            builder.AppendLine("No findings were produced for this analysis focus.");
        }

        foreach (var incident in snapshot.Incidents)
        {
            builder.AppendLine();
            builder.Append("## ").Append('[').Append(incident.Severity).Append("] ")
                .AppendLine(EscapeMarkdown(incident.Title));
            AppendIncident(builder, incident, markdown: true);
        }

        builder.AppendLine();
        builder.AppendLine("## Technical Details");
        AppendMetadata(builder, snapshot, markdown: true);

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string FormatPlainText(EvidenceAnalysisSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.Append(AnalysisTitle(snapshot.Scope)).AppendLine(" Evidence Analysis");
        builder.AppendLine();
        builder.Append("Assessment: ").AppendLine(snapshot.Assessment.ToString());
        AppendWrappedAssessmentDescription(builder, snapshot.Summary);
        builder.AppendLine();
        builder.AppendLine("Findings:");
        if (snapshot.Incidents.Count == 0)
        {
            builder.AppendLine("  - No findings were produced for this analysis focus.");
        }

        foreach (var incident in snapshot.Incidents)
        {
            builder.AppendLine();
            builder.Append('[').Append(incident.Severity).Append("] ").AppendLine(Normalize(incident.Title));
            AppendIncident(builder, incident, markdown: false);
        }

        builder.AppendLine();
        builder.AppendLine("Technical Details:");
        AppendMetadata(builder, snapshot, markdown: false);

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendMetadata(StringBuilder builder, EvidenceAnalysisSnapshot snapshot, bool markdown)
    {
        AppendProperty(builder, "Session", snapshot.SessionId.Value, markdown);
        AppendProperty(builder, "Snapshot", snapshot.Id.Value, markdown);
        AppendProperty(builder, "Scope", snapshot.Scope.ToString(), markdown);
        AppendProperty(builder, "Run epoch", snapshot.RunEpochId?.Value ?? "-", markdown);
        AppendProperty(builder, "Lifecycle observation", snapshot.LifecycleObservationId?.Value ?? "-", markdown);
        AppendProperty(builder, "Generated (PC UTC)", FormatTimestamp(snapshot.GeneratedAt), markdown);
        AppendProperty(builder, "Activity range", $"{snapshot.AnalyzedFromSequence.ToString(CultureInfo.InvariantCulture)}..{snapshot.AnalyzedThroughSequence.ToString(CultureInfo.InvariantCulture)}", markdown);
        AppendProperty(builder, "Completeness", snapshot.Completeness.ToString(), markdown);
        AppendProperty(builder, "Incidents", snapshot.Incidents.Count.ToString(CultureInfo.InvariantCulture), markdown);
    }

    private static string AnalysisTitle(EvidenceAnalysisScope scope) => scope switch
    {
        EvidenceAnalysisScope.Session => "Communication Health",
        _ => scope.ToString()
    };

    private static void AppendIncident(StringBuilder builder, EvidenceIncident incident, bool markdown)
    {
        AppendProperty(builder, "Id", incident.Id.Value, markdown);
        AppendProperty(builder, "Classification", incident.Classification.ToString(), markdown);
        AppendProperty(builder, "Completeness", incident.Completeness.ToString(), markdown);
        AppendProperty(builder, "Scope", incident.Scope.ToString(), markdown);
        AppendProperty(builder, "Opened", FormatTimestamp(incident.OpenedAt), markdown);
        AppendProperty(builder, "Closed", incident.ClosedAt is null ? "-" : FormatTimestamp(incident.ClosedAt.Value), markdown);
        if (incident.Run is not null)
        {
            AppendProperty(builder, "Run epoch", incident.Run.EpochId.Value, markdown);
            AppendProperty(builder, "Run state", incident.Run.State.ToString(), markdown);
            AppendProperty(builder, "Effective PC Run result", incident.Run.Result.ToString(), markdown);
            AppendProperty(builder, "PLC-reported Run result", incident.Run.PlcReportedResult?.ToString() ?? "Not observed", markdown);
            AppendProperty(builder, "Run evidence", incident.Run.EvidenceState.ToString(), markdown);
            AppendProperty(builder, "Lost events", incident.Run.LostEventCount.ToString(CultureInfo.InvariantCulture), markdown);
        }

        if (incident.Lifecycle is not null)
        {
            AppendProperty(builder, "Lifecycle observation", incident.Lifecycle.ObservationId.Value, markdown);
            AppendProperty(builder, "Lifecycle anchor", incident.Lifecycle.Anchor.ToString(), markdown);
            AppendProperty(
                builder,
                "Reached Idle + Ready",
                incident.Lifecycle.ReachedIdleAndReady?.ToString(CultureInfo.InvariantCulture) ?? "Unknown",
                markdown);
        }

        builder.AppendLine();
        builder.AppendLine(Normalize(incident.Summary));
        AppendOwners(builder, incident.AffectedOwners, markdown);
        AppendStatements(builder, "Observed facts", incident.ObservedFacts, markdown);
        AppendStatements(builder, "Deterministic conclusions", incident.DeterministicConclusions, markdown);
        AppendSuggestedChecks(builder, incident.SuggestedChecks, markdown);
        AppendTimeline(builder, incident.Timeline, markdown);
    }

    private static void AppendOwners(StringBuilder builder, IReadOnlyList<EvidenceAffectedOwner> owners, bool markdown)
    {
        AppendHeading(builder, "Affected owners", markdown);
        if (owners.Count == 0)
        {
            AppendListItem(builder, "None", markdown);
            return;
        }

        foreach (var owner in owners)
        {
            AppendListItem(builder, $"{owner.Kind}: {owner.DisplayName} ({owner.Id.Value})", markdown);
        }
    }

    private static void AppendStatements(
        StringBuilder builder,
        string heading,
        IReadOnlyList<EvidenceAnalysisStatement> statements,
        bool markdown)
    {
        AppendHeading(builder, heading, markdown);
        if (statements.Count == 0)
        {
            AppendListItem(builder, "None", markdown);
            return;
        }

        foreach (var statement in statements)
        {
            AppendListItem(builder, $"[{statement.Code}] {statement.Message}{FormatReferences(statement.Evidence)}", markdown);
        }
    }

    private static void AppendSuggestedChecks(
        StringBuilder builder,
        IReadOnlyList<EvidenceSuggestedCheck> checks,
        bool markdown)
    {
        AppendHeading(builder, "Suggested checks", markdown);
        if (checks.Count == 0)
        {
            AppendListItem(builder, "None", markdown);
            return;
        }

        foreach (var check in checks)
        {
            AppendListItem(builder, $"[{check.Code}] {check.Message}", markdown);
        }
    }

    private static void AppendTimeline(
        StringBuilder builder,
        IReadOnlyList<EvidenceTimelineItem> timeline,
        bool markdown)
    {
        AppendHeading(builder, "Timeline", markdown);
        if (timeline.Count == 0)
        {
            AppendListItem(builder, "None", markdown);
            return;
        }

        foreach (var item in timeline.OrderBy(static item => item.Timestamp))
        {
            AppendListItem(
                builder,
                $"{FormatTimestamp(item.Timestamp)} [{item.Kind}] {item.Message}{FormatReferences(item.Evidence)}",
                markdown);
        }
    }

    private static string FormatReferences(IReadOnlyList<EvidenceReference> references)
    {
        if (references.Count == 0)
        {
            return string.Empty;
        }

        return " | Evidence: " + string.Join(
            ", ",
            references.Select(static reference =>
                reference.PlcEventSequenceId is null
                    ? $"activity:{reference.ActivitySequence.ToString(CultureInfo.InvariantCulture)}"
                    : $"activity:{reference.ActivitySequence.ToString(CultureInfo.InvariantCulture)}#plc:{reference.PlcEventSequenceId.Value.ToString(CultureInfo.InvariantCulture)}"));
    }

    private static string FormatReferencedEvidence(
        EvidenceAnalysisSnapshot snapshot,
        UnitTestActivityTraceSnapshot trace,
        EvidenceAnalysisFormat format)
    {
        var markdown = format switch
        {
            EvidenceAnalysisFormat.Markdown => true,
            EvidenceAnalysisFormat.PlainText => false,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported evidence-analysis format.")
        };
        var referencedSequences = snapshot.Incidents
            .SelectMany(static incident => incident.ObservedFacts
                .SelectMany(static statement => statement.Evidence)
                .Concat(incident.DeterministicConclusions.SelectMany(static statement => statement.Evidence))
                .Concat(incident.Timeline.SelectMany(static item => item.Evidence)))
            .Select(static reference => reference.ActivitySequence)
            .Distinct()
            .Order()
            .ToArray();
        var scopeFormatter = EvidenceSupportingEvidenceFormatterFactory.Create(snapshot.Scope);
        var selectedSequences = scopeFormatter.SelectSequences(snapshot, trace, referencedSequences);
        var entriesBySequence = trace.Entries.ToDictionary(static entry => entry.Sequence);
        var builder = new StringBuilder();
        AppendHierarchy(builder, scopeFormatter.CreateHierarchy(snapshot, trace), markdown);
        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(markdown ? "# " : string.Empty)
            .AppendLine(scopeFormatter.SectionTitle);
        if (selectedSequences.Length == 0)
        {
            AppendListItem(builder, "No retained activity entries match this analysis focus.", markdown);
            return builder.ToString();
        }

        foreach (var sequence in selectedSequences)
        {
            builder.AppendLine();
            if (!entriesBySequence.TryGetValue(sequence, out var entry))
            {
                builder.Append(markdown ? "## Activity " : "Activity ")
                    .AppendLine(sequence.ToString(CultureInfo.InvariantCulture));
                AppendProperty(builder, "Retention", "Referenced activity entry is no longer retained.", markdown);
                continue;
            }

            var activity = scopeFormatter.Project(entry);
            builder.Append(markdown ? "## Activity " : "Activity ")
                .Append(activity.Sequence.ToString(CultureInfo.InvariantCulture))
                .Append(" - ")
                .AppendLine(activity.Label);
            foreach (var field in activity.Fields)
            {
                AppendProperty(builder, field.Name, field.Value, markdown);
            }
        }

        return builder.ToString();
    }

    private static void AppendHierarchy(
        StringBuilder builder,
        EvidenceReportHierarchy? hierarchy,
        bool markdown)
    {
        if (hierarchy is null)
        {
            return;
        }

        builder.AppendLine(markdown ? "# Run Hierarchy Snapshot" : "Run Hierarchy Snapshot");
        builder.AppendLine();
        if (markdown)
        {
            builder.AppendLine("```json");
        }

        builder.AppendLine(JsonSerializer.Serialize(hierarchy, HierarchyJsonOptions));
        if (markdown)
        {
            builder.AppendLine("```");
        }
    }

    private static void AppendProperty(StringBuilder builder, string name, string value, bool markdown)
    {
        builder.Append(markdown ? "- **" : "")
            .Append(name)
            .Append(markdown ? ":** " : ": ")
            .AppendLine(Normalize(value));
    }

    private static void AppendHeading(StringBuilder builder, string heading, bool markdown)
    {
        builder.AppendLine();
        if (markdown)
        {
            builder.Append("### ").AppendLine(heading);
        }
        else
        {
            builder.Append(heading).AppendLine(":");
        }
    }

    private static void AppendListItem(StringBuilder builder, string text, bool markdown)
    {
        builder.Append(markdown ? "- " : "  - ").AppendLine(Normalize(text));
    }

    private static void AppendWrappedAssessmentDescription(StringBuilder builder, string text)
    {
        var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lineLength = 0;

        foreach (var word in words)
        {
            if (lineLength > 0 && lineLength + 1 + word.Length > AssessmentDescriptionLineLength)
            {
                builder.AppendLine();
                lineLength = 0;
            }

            if (lineLength > 0)
            {
                builder.Append(' ');
                lineLength++;
            }

            builder.Append(word);
            lineLength += word.Length;
        }

        builder.AppendLine();
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
    }

    private static string EscapeMarkdown(string value)
    {
        return Normalize(value).Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
    }
}
