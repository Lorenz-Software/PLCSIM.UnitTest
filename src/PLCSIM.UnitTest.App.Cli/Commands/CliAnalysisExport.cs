using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Core.Analysis;

namespace PLCSIM.UnitTest.App.Cli.Commands;

public sealed record CliAnalysisExportResult(string? OutputPath, string? Error);

/// <summary>
/// Selects one existing analyzer and isolates optional reporting from CLI execution.
/// </summary>
internal sealed class CliAnalysisExport(TimeProvider timeProvider)
{
    // Independent of run cancellation: allow a short best-effort artifact write after cleanup.
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    internal EvidenceAnalysisPackage? Package { get; private set; }
    internal string? Error { get; private set; }

    internal void Capture(UnitTestActivityTraceSnapshot? trace, CliFullRunResult? result = null)
    {
        try
        {
            if (trace is null || trace.Entries.Count == 0)
            {
                Error = "Evidence analysis unavailable: no activity trace was captured.";
                return;
            }

            EvidenceAnalysisSnapshot analysis;
            var run = trace.Entries.LastOrDefault(entry => entry.Payload is RunEpochActivityPayload);
            if (run?.Payload is RunEpochActivityPayload epoch)
            {
                analysis = new RunEvidenceAnalyzer(timeProvider).Analyze(trace, epoch.Snapshot.EpochId);
            }
            else
            {
                var command = trace.Entries.LastOrDefault(entry => entry.Payload is CommandActivityPayload
                {
                    Command: UnitTestActivityCommandKind.Start or UnitTestActivityCommandKind.Reset or UnitTestActivityCommandKind.PlcStart
                });
                LifecycleObservationId? observation = command?.LifecycleObservationId;
                if (observation is { } id)
                    analysis = new LifecycleEvidenceAnalyzer(timeProvider).Analyze(trace, id);
                else if (command is not null)
                {
                    Error = "Evidence analysis unavailable: the requested lifecycle action has no retained observation context.";
                    return;
                }
                else
                    analysis = new CommunicationSessionEvidenceAnalyzer(timeProvider).Analyze(trace);
            }

            Package = new EvidenceAnalysisPackageBuilder().Build(IncludeCliOutcome(analysis, result), trace);
        }
        catch (Exception ex)
        {
            Error = $"Evidence analysis capture failed: {ex.Message}";
        }
    }

    private static EvidenceAnalysisSnapshot IncludeCliOutcome(EvidenceAnalysisSnapshot analysis, CliFullRunResult? result)
    {
        if (result is null || result.State is CliFullRunState.Completed or CliFullRunState.TestFailed)
            return analysis;

        // The CLI result is separate from PLC evidence; never invent a PLC event or trace reference.
        var summary = $"CLI execution ended with {result.State}: {result.Message}";
        var facts = new List<EvidenceAnalysisStatement>
        {
            new("Cli.Execution.Outcome", summary, [])
        };
        facts.AddRange(result.Diagnostics.Where(diagnostic => diagnostic.Source == DiagnosticSource.CliApplication)
            .Select(diagnostic => new EvidenceAnalysisStatement(diagnostic.Code, diagnostic.Message, [])));
        var cancelled = result.State == CliFullRunState.Cancelled;
        var incident = new EvidenceIncident(
            new($"{analysis.Id.Value}:cli-outcome"), EvidenceIncidentClassification.Unknown,
            cancelled ? EvidenceIncidentSeverity.Warning : EvidenceIncidentSeverity.Error,
            analysis.Completeness,
            analysis.Scope switch
            {
                EvidenceAnalysisScope.Run => EvidenceIncidentScope.Run,
                EvidenceAnalysisScope.Lifecycle => EvidenceIncidentScope.Lifecycle,
                _ => EvidenceIncidentScope.Session
            },
            "CLI execution outcome", summary, analysis.GeneratedAt, analysis.GeneratedAt,
            null, null, [], facts,
            [new("Cli.Execution.Source", "This is the CLI termination reason, not proof of a PLC framework fault. "
                + "Activity evidence below reflects the state observed before cleanup.", [])], [], []);
        return analysis with
        {
            Assessment = cancelled && analysis.Assessment != EvidenceAnalysisAssessment.Error
                ? EvidenceAnalysisAssessment.Warning : EvidenceAnalysisAssessment.Error,
            Summary = summary + $" Evidence-only assessment: {analysis.Assessment}. " + analysis.Summary,
            Incidents = analysis.Incidents.Append(incident).ToArray()
        };
    }

    internal async Task<CliAnalysisExportResult> WriteAsync(string directory)
    {
        if (Package is null)
            return new(null, Error ?? "Evidence analysis unavailable: no package was captured.");

        try
        {
            var invocationDirectory = Path.Combine(Path.GetFullPath(directory),
                $"analysis-{Package.Metadata.CapturedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            var name = Package.Metadata.Scope switch
            {
                EvidenceAnalysisScope.Run => "run",
                EvidenceAnalysisScope.Lifecycle => "lifecycle",
                _ => "communication-health"
            };
            var path = Path.Combine(invocationDirectory, $"{name}.zip");
            using var timeout = new CancellationTokenSource(WriteTimeout);
            await new EvidenceAnalysisPackageZipWriter().WriteAsync(Package, path, timeout.Token).ConfigureAwait(false);
            return new(path, null);
        }
        catch (OperationCanceledException)
        {
            return new(null, "Evidence analysis export exceeded its 10-second write timeout.");
        }
        catch (Exception ex)
        {
            return new(null, $"Evidence analysis export failed: {ex.Message}");
        }
    }
}
