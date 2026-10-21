using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using System.Globalization;
using System.Windows.Media;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public sealed record DiagnosticViewModel(
    string Key,
    DateTimeOffset LastSeenAt,
    string Timestamp,
    string FirstSeen,
    string LastSeen,
    string LastSeenDetail,
    string Occurrences,
    string Level,
    string Category,
    string Phase,
    string Code,
    string Source,
    string Message,
    string TechnicalDetail,
    string EvidenceReference,
    string Correlation,
    RunEpochId? RunEpochId,
    LifecycleObservationId? LifecycleObservationId,
    Brush SeverityBrush)
{
    public static DiagnosticViewModel FromDiagnostic(Diagnostic diagnostic)
    {
        return new DiagnosticViewModel(
            CreateKey(diagnostic),
            diagnostic.Timestamp,
            FormatLocalTime(diagnostic.Timestamp),
            FormatLocalTime(diagnostic.Timestamp),
            FormatLocalTimeCompact(diagnostic.Timestamp),
            FormatLocalTimePrecise(diagnostic.Timestamp),
            "1",
            diagnostic.Level.ToString(),
            diagnostic.Category.ToString(),
            diagnostic.Phase.ToString(),
            diagnostic.Code,
            diagnostic.Source.ToString(),
            diagnostic.Message,
            string.IsNullOrWhiteSpace(diagnostic.TechnicalDetail) ? "-" : diagnostic.TechnicalDetail,
            FormatEvidenceReference(diagnostic.EvidenceReference),
            FormatCorrelation(diagnostic.Correlation),
            diagnostic.Correlation.UnitTestRunEpochId,
            diagnostic.Correlation.LifecycleObservationId,
            diagnostic.Level >= Microsoft.Extensions.Logging.LogLevel.Error
                ? Brushes.Firebrick
                : diagnostic.Level >= Microsoft.Extensions.Logging.LogLevel.Warning
                    ? Brushes.Goldenrod
                    : Brushes.Gray);
    }

    public static DiagnosticViewModel FromEntry(DiagnosticEntry entry)
    {
        var diagnostic = entry.Current;
        return new DiagnosticViewModel(
            CreateKey(entry.Fingerprint),
            entry.LastSeenAt,
            FormatLocalTime(diagnostic.Timestamp),
            FormatLocalTime(entry.FirstSeenAt),
            FormatLocalTimeCompact(entry.LastSeenAt),
            FormatLocalTimePrecise(entry.LastSeenAt),
            entry.OccurrenceCount.ToString(CultureInfo.InvariantCulture),
            diagnostic.Level.ToString(),
            diagnostic.Category.ToString(),
            diagnostic.Phase.ToString(),
            diagnostic.Code,
            diagnostic.Source.ToString(),
            diagnostic.Message,
            string.IsNullOrWhiteSpace(diagnostic.TechnicalDetail) ? "-" : diagnostic.TechnicalDetail,
            FormatEvidenceReference(diagnostic.EvidenceReference),
            FormatCorrelation(diagnostic.Correlation),
            diagnostic.Correlation.UnitTestRunEpochId,
            diagnostic.Correlation.LifecycleObservationId,
            diagnostic.Level >= Microsoft.Extensions.Logging.LogLevel.Error
                ? Brushes.Firebrick
                : diagnostic.Level >= Microsoft.Extensions.Logging.LogLevel.Warning
                    ? Brushes.Goldenrod
                    : Brushes.Gray);
    }

    private static string CreateKey(Diagnostic diagnostic)
    {
        if (!string.IsNullOrWhiteSpace(diagnostic.Id))
        {
            return diagnostic.Id;
        }

        return string.Join(
            "|",
            diagnostic.Timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            diagnostic.Code,
            diagnostic.Source,
            diagnostic.Phase,
            diagnostic.Message);
    }

    private static string CreateKey(DiagnosticFingerprint fingerprint)
    {
        var scope = fingerprint.CorrelationScope;
        return string.Join(
            "|",
            fingerprint.Code,
            fingerprint.Source,
            fingerprint.Phase,
            fingerprint.Category,
            scope.SessionId?.Value,
            scope.WorkflowRunId?.Value,
            scope.OperationId?.Value,
            scope.AccessProfileId,
            scope.ServiceId,
            scope.ProjectContextId?.Value,
            scope.RuntimeTargetId?.Value,
            scope.UnitTestRunEpochId?.Value,
            scope.LifecycleObservationId?.Value,
            scope.EventStreamContextId?.Value,
            scope.EvidenceSnapshotId?.Value,
            scope.OutputRequestId?.Value);
    }

    private static string FormatEvidenceReference(DiagnosticEvidenceReference? reference)
    {
        return reference is null
            ? "-"
            : $"{reference.Kind}: {reference.Reference}";
    }

    private static string FormatLocalTime(DateTimeOffset timestamp)
    {
        var culture = CultureInfo.CurrentCulture;
        var format = $"{culture.DateTimeFormat.ShortDatePattern} {culture.DateTimeFormat.LongTimePattern}";
        return timestamp
            .ToLocalTime()
            .ToString(format, culture);
    }

    private static string FormatLocalTimeCompact(DateTimeOffset timestamp)
    {
        return timestamp
            .ToLocalTime()
            .ToString("T", CultureInfo.CurrentCulture);
    }

    private static string FormatLocalTimePrecise(DateTimeOffset timestamp)
    {
        var culture = CultureInfo.CurrentCulture;
        var format = $"{culture.DateTimeFormat.ShortDatePattern} {culture.DateTimeFormat.LongTimePattern}.fff";
        return timestamp
            .ToLocalTime()
            .ToString(format, culture);
    }

    private static string FormatCorrelation(DiagnosticCorrelation correlation)
    {
        var parts = new List<string>();
        Add(parts, "Session", correlation.SessionId?.Value);
        Add(parts, "Workflow", correlation.WorkflowRunId?.Value);
        Add(parts, "Operation", correlation.OperationId?.Value);
        Add(parts, "Profile", correlation.AccessProfileId);
        Add(parts, "Service", correlation.ServiceId);
        Add(parts, "Project", correlation.ProjectContextId?.Value);
        Add(parts, "Runtime", correlation.RuntimeTargetId?.Value);
        Add(parts, "Run epoch", correlation.UnitTestRunEpochId?.Value);
        Add(parts, "Lifecycle", correlation.LifecycleObservationId?.Value);
        Add(parts, "Event stream", correlation.EventStreamContextId?.Value);
        Add(parts, "Evidence", correlation.EvidenceSnapshotId?.Value);
        Add(parts, "Output", correlation.OutputRequestId?.Value);

        return parts.Count == 0 ? "-" : string.Join(Environment.NewLine, parts);
    }

    private static void Add(ICollection<string> parts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{label}: {value}");
        }
    }
}
