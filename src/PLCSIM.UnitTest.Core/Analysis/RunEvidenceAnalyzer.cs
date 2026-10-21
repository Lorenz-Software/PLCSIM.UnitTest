using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.UnitTests;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.Core.Analysis;

/// <summary>
/// Deterministic on-demand analysis of one RunEpoch retained in a UnitTest activity trace.
/// </summary>
public sealed class RunEvidenceAnalyzer
{
    private readonly TimeProvider timeProvider;

    public RunEvidenceAnalyzer(CoreServiceContext coreContext)
        : this(coreContext?.TimeProvider ?? throw new ArgumentNullException(nameof(coreContext)))
    {
    }

    public RunEvidenceAnalyzer(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public EvidenceAnalysisSnapshot Analyze(
        UnitTestActivityTraceSnapshot trace,
        RunEpochId runEpochId)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (runEpochId.IsEmpty)
        {
            throw new ArgumentException("RunEpoch ID must not be empty.", nameof(runEpochId));
        }

        var runEntries = trace.Entries
            .Where(entry => IsRunEntry(entry, runEpochId))
            .OrderBy(static entry => entry.Sequence)
            .ToArray();
        var correlatedEntries = trace.Entries
            .Where(entry => entry.RunEpochId == runEpochId)
            .OrderBy(static entry => entry.Sequence)
            .ToArray();
        var generatedAt = timeProvider.GetUtcNow();
        var latestRunEntry = runEntries.LastOrDefault();
        if (latestRunEntry?.Payload is not RunEpochActivityPayload runPayload)
        {
            var unavailableThroughSequence = correlatedEntries.LastOrDefault()?.Sequence ?? trace.NewestSequence;
            var unavailableAnalysisId = new EvidenceSnapshotId(
                $"run:{trace.SessionId.Value}:{runEpochId.Value}:{unavailableThroughSequence}");
            return CreateUnavailableAnalysis(trace, runEpochId, unavailableAnalysisId, generatedAt);
        }

        var run = runPayload.Snapshot;
        var lateCompletion = FindLateCompletionEvidence(trace, latestRunEntry, run);
        var analyzedFromSequence = correlatedEntries.FirstOrDefault()?.Sequence ?? trace.OldestSequence;
        var analyzedThroughSequence = Math.Max(
            correlatedEntries.LastOrDefault()?.Sequence ?? trace.NewestSequence,
            lateCompletion?.Completion.Sequence ?? 0);
        var analysisId = new EvidenceSnapshotId(
            $"run:{trace.SessionId.Value}:{runEpochId.Value}:{analyzedThroughSequence}");
        var traceGap = trace.EvictedEntryCount > 0
            && runEntries.All(entry => ((RunEpochActivityPayload)entry.Payload).Activity != UnitTestRunEpochActivityKind.Started);
        var nodes = EnumerateNodes(run.Root).ToArray();
        var hasMissingEvidence = nodes.Any(node => HasDirectMissingTerminalEvidence(run, node));
        var completeness = DetermineCompleteness(run, traceGap, hasMissingEvidence);
        var runAnalysis = new RunEvidenceAnalysis(
            run.EpochId,
            run.State,
            run.Result,
            run.EvidenceState,
            run.LostEventCount)
        {
            PlcReportedResult = run.Root.PlcReportedResult
        };
        var incidentFactory = new IncidentFactory(analysisId, runAnalysis, latestRunEntry, trace);
        var incidents = new List<EvidenceIncident>();

        AddAbortIncident(run, completeness, incidentFactory, incidents);
        AddEventBufferCapacityIncident(run, completeness, incidentFactory, incidents);
        var frameworkRootSequence = AddFrameworkErrorIncident(run, completeness, incidentFactory, incidents);
        AddTimeoutIncident(run, completeness, frameworkRootSequence is not null, lateCompletion, incidentFactory, incidents);
        AddTestFailureIncident(run, completeness, frameworkRootSequence, incidentFactory, incidents);
        AddContractConflictIncident(run, completeness, incidentFactory, incidents);
        AddIncompleteEvidenceIncident(run, completeness, traceGap, incidentFactory, incidents);

        var assessment = DetermineAssessment(run.State, completeness, incidents);
        var summary = CreateSummary(run, nodes, completeness, incidents, lateCompletion is not null);

        return new EvidenceAnalysisSnapshot(
            analysisId,
            trace.SessionId,
            EvidenceAnalysisScope.Run,
            runEpochId,
            null,
            generatedAt,
            analyzedFromSequence,
            analyzedThroughSequence,
            completeness,
            incidents.AsReadOnly(),
            assessment,
            summary);
    }

    private static EvidenceAnalysisSnapshot CreateUnavailableAnalysis(
        UnitTestActivityTraceSnapshot trace,
        RunEpochId runEpochId,
        EvidenceSnapshotId analysisId,
        DateTimeOffset generatedAt)
    {
        var incident = new EvidenceIncident(
            new EvidenceIncidentId($"{analysisId.Value}:1"),
            EvidenceIncidentClassification.EvidenceIncomplete,
            EvidenceIncidentSeverity.Error,
            EvidenceCompleteness.Unavailable,
            EvidenceIncidentScope.Run,
            "Run evidence unavailable",
            $"No retained RunEpoch snapshot was found for '{runEpochId.Value}'.",
            trace.StartedAt,
            trace.EndedAt,
            null,
            null,
            [],
            [new EvidenceAnalysisStatement("run-snapshot-missing", "No retained RunEpoch snapshot matches the requested RunEpoch ID.", [])],
            [new EvidenceAnalysisStatement("run-analysis-unavailable", "The requested run cannot be analyzed from the retained activity trace.", [])],
            [new EvidenceSuggestedCheck("verify-run-selection", "Verify that the selected run belongs to the retained application session and was observed after trace capture started.")],
            []);

        return new EvidenceAnalysisSnapshot(
            analysisId,
            trace.SessionId,
            EvidenceAnalysisScope.Run,
            runEpochId,
            null,
            generatedAt,
            trace.OldestSequence,
            trace.NewestSequence,
            EvidenceCompleteness.Unavailable,
            [incident],
            EvidenceAnalysisAssessment.Inconclusive,
            $"RunEpoch '{runEpochId.Value}' cannot be evaluated because no retained RunEpoch snapshot is available.");
    }

    private static EvidenceAnalysisAssessment DetermineAssessment(
        UnitTestRunState runState,
        EvidenceCompleteness completeness,
        IReadOnlyList<EvidenceIncident> incidents)
    {
        if (completeness == EvidenceCompleteness.Unavailable)
        {
            return EvidenceAnalysisAssessment.Inconclusive;
        }

        if (incidents.Any(static incident => incident.Severity == EvidenceIncidentSeverity.Error))
        {
            return EvidenceAnalysisAssessment.Error;
        }

        if (incidents.Count > 0)
        {
            return EvidenceAnalysisAssessment.Warning;
        }

        if (!IsTerminal(runState))
        {
            return EvidenceAnalysisAssessment.Inconclusive;
        }

        return completeness == EvidenceCompleteness.Complete
            ? EvidenceAnalysisAssessment.Healthy
            : EvidenceAnalysisAssessment.Inconclusive;
    }

    private static string CreateSummary(
        UnitTestRunEpochSnapshot run,
        IReadOnlyList<UnitTestRunNode> nodes,
        EvidenceCompleteness completeness,
        IReadOnlyList<EvidenceIncident> incidents,
        bool lateCompletionObserved)
    {
        var suiteCount = nodes.Count(static node => node.Kind == UnitTestNodeKind.TestSuite);
        var testCaseCount = nodes.Count(static node => node.Kind == UnitTestNodeKind.TestCase);
        if (!IsTerminal(run.State) && incidents.Count == 0)
        {
            return $"RunControl '{run.Root.DisplayName}' is currently in state '{run.State}' with result '{run.Result}'. "
                + $"The retained evidence currently contains {suiteCount} TestSuite(s) and {testCaseCount} TestCase(s), but the run has not reached a terminal state. "
                + "No terminal Run result can be concluded from this snapshot.";
        }

        if (run.State == UnitTestRunState.TimedOut)
        {
            var reportedResult = run.Root.PlcReportedResult is null
                ? "No PLC-reported terminal RunControl result was observed."
                : $"The last PLC-reported RunControl result was '{run.Root.PlcReportedResult}'.";
            var lateEvidence = lateCompletionObserved
                ? " The PLC later published terminal RunControl evidence after the PC observation deadline; this does not change the timed-out PC Run outcome."
                : string.Empty;
            return $"RunControl '{run.Root.DisplayName}' observation timed out before complete terminal evidence was received. "
                + $"{reportedResult} The last effective PC-side partial result was '{run.Result}'. "
                + $"Evidence completeness is '{completeness}' and the analysis produced {incidents.Count} finding(s)."
                + lateEvidence;
        }

        if (incidents.Count == 0)
        {
            return $"RunControl '{run.Root.DisplayName}' ended in state '{run.State}' with PLC-reported result '{FormatReportedResult(run.Root)}' "
                + $"and effective PC result '{run.Result}'. "
                + $"Complete evidence was retained for {suiteCount} TestSuite(s) and {testCaseCount} TestCase(s). "
                + "No UnitTest failure, evidence gap, result-hierarchy conflict, timeout, or lost event was detected.";
        }

        var affectedOwnerCount = incidents
            .SelectMany(static incident => incident.AffectedOwners)
            .Select(static owner => owner.Id)
            .Distinct()
            .Count();
        var loss = run.LostEventCount == 0
            ? "No producer-side event loss was reported."
            : $"The producer reported {run.LostEventCount} lost event(s).";
        return $"RunControl '{run.Root.DisplayName}' ended in state '{run.State}' with PLC-reported result '{FormatReportedResult(run.Root)}' "
            + $"and effective PC result '{run.Result}'. "
            + $"The analysis produced {incidents.Count} finding(s) affecting {affectedOwnerCount} UnitTest owner(s). "
            + $"Evidence completeness is '{completeness}'. {loss}";
    }

    private static string FormatReportedResult(UnitTestRunNode node) =>
        node.PlcReportedResult?.ToString() ?? "Not observed";

    private static bool IsTerminal(UnitTestRunState state)
    {
        return state is UnitTestRunState.Completed
            or UnitTestRunState.Aborted
            or UnitTestRunState.TimedOut
            or UnitTestRunState.Invalidated;
    }

    private static void AddAbortIncident(
        UnitTestRunEpochSnapshot run,
        EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        if (run.State != UnitTestRunState.Aborted && run.Result != UnitTestResultState.Abort)
        {
            return;
        }

        incidents.Add(factory.Create(
            EvidenceIncidentClassification.RunAborted,
            EvidenceIncidentSeverity.Information,
            completeness,
            "Run aborted",
            "The observed RunEpoch ended with an Abort result.",
            [],
            [factory.Fact("run-aborted", $"Run state is '{run.State}' and result is '{run.Result}'.")],
            [factory.Conclusion("run-termination-abort", "The run terminated through the Abort lifecycle path.")],
            []));
    }

    private static void AddTimeoutIncident(
        UnitTestRunEpochSnapshot run,
        EvidenceCompleteness completeness,
        bool plcFrameworkTimeoutObserved,
        LateRunCompletionEvidence? lateCompletion,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        if (run.State != UnitTestRunState.TimedOut || plcFrameworkTimeoutObserved)
        {
            return;
        }

        var facts = new List<EvidenceAnalysisStatement>
        {
            factory.Fact(
                "run-timeout",
                $"PC Run monitoring ended in the TimedOut state with last effective partial result '{run.Result}' and PLC-reported RunControl result '{FormatReportedResult(run.Root)}'.")
        };
        var conclusions = new List<EvidenceAnalysisStatement>
        {
            factory.Conclusion("run-terminal-observation-missing", "The PC observation deadline expired before terminal RunControl evidence was received; the last partial aggregate is not a terminal PLC result.")
        };
        if (lateCompletion is not null)
        {
            foreach (var entry in lateCompletion.TerminalEntries)
            {
                var plcEvent = ((PlcEventActivityPayload)entry.Payload).Event;
                facts.Add(factory.Fact(
                    "late-run-terminal-evidence",
                    $"After the PC timeout, the PLC published RunControl {plcEvent.EventType} with state '{plcEvent.State}' and result '{plcEvent.Result}'.",
                    entry));
            }

            conclusions.Add(factory.Conclusion(
                "late-plc-completion-observed",
                "The PLC later completed the physical run, but evidence received after the PC deadline does not change the TimedOut RunEpoch result.",
                lateCompletion.Completion));
        }

        incidents.Add(factory.Create(
            EvidenceIncidentClassification.RunObservationTimeout,
            EvidenceIncidentSeverity.Error,
            completeness,
            "Run observation timed out",
            "The RunEpoch did not reach the required terminal observation before the monitoring timeout.",
            [Owner(run.Root)],
            facts,
            conclusions,
            [new EvidenceSuggestedCheck(
                "inspect-run-observation-timeout",
                "Compare the configured PC Run observation timeout with the expected PLC Run duration and inspect whether terminal RunControl evidence arrived after the observation closed.")]));
    }

    private static void AddEventBufferCapacityIncident(
        UnitTestRunEpochSnapshot run,
        EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var capacityFindings = EnumerateNodes(run.Root)
            .SelectMany(node => node.Findings.Select(finding => (Node: node, Finding: finding)))
            .Where(static item => IsEventBufferCapacityFinding(item.Finding))
            .OrderBy(item => item.Finding.SourceEvent?.SequenceId ?? uint.MaxValue)
            .ToArray();
        if (capacityFindings.Length == 0 && run.LostEventCount == 0)
        {
            return;
        }

        var facts = capacityFindings
            .Select(item => factory.Fact(
                "event-buffer-capacity",
                $"{item.Finding.Name}: {item.Finding.Message}",
                item.Finding.SourceEvent))
            .ToList();
        if (run.LostEventCount > 0)
        {
            facts.Add(factory.Fact(
                "producer-events-lost",
                $"The EventBuffer producer reported {run.LostEventCount} lost event(s)."));
        }

        incidents.Add(factory.Create(
            EvidenceIncidentClassification.EventBufferCapacity,
            run.LostEventCount > 0 ? EvidenceIncidentSeverity.Error : EvidenceIncidentSeverity.Warning,
            completeness,
            run.LostEventCount > 0 ? "EventBuffer capacity exceeded" : "EventBuffer capacity warning",
            run.LostEventCount > 0
                ? $"The EventBuffer reached its capacity boundary and the producer reported {run.LostEventCount} lost event(s)."
                : "The EventBuffer reported capacity pressure, but no producer-side event loss was observed.",
            [],
            facts,
            [factory.Conclusion(
                run.LostEventCount > 0 ? "event-stream-loss" : "event-buffer-pressure",
                run.LostEventCount > 0
                    ? "Run-wide evidence confidence is reduced by producer-side event loss; this does not by itself identify a failed lifecycle reaction or a specific owner with missing terminal evidence."
                    : "EventBuffer capacity pressure was observed without proof of missing Run evidence.")],
            [new EvidenceSuggestedCheck(
                "inspect-event-production-rate",
                "Inspect EventBuffer capacity, event production bursts, and event-consumer throughput.")]));
    }

    private static uint? AddFrameworkErrorIncident(
        UnitTestRunEpochSnapshot run,
        EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var nodes = EnumerateNodes(run.Root).ToArray();
        var frameworkFindings = nodes
            .SelectMany(node => node.Findings.Select(finding => (Node: node, Finding: finding)))
            .Where(item => item.Finding.Kind == UnitTestFindingKind.Framework
                && !IsEventBufferCapacityFinding(item.Finding)
                && factory.IsErrorPlcEvent(item.Finding.SourceEvent))
            .OrderBy(item => item.Finding.SourceEvent?.SequenceId ?? uint.MaxValue)
            .ToArray();
        if (frameworkFindings.Length == 0)
        {
            return null;
        }

        var rootFinding = frameworkFindings[0];
        var rootSequence = rootFinding.Finding.SourceEvent!.SequenceId;
        var timeout = rootFinding.Finding.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
        var consequences = nodes
            .SelectMany(node => node.Findings.Select(finding => (Node: node, Finding: finding)))
            .Where(item => item.Finding.Kind == UnitTestFindingKind.OwnerResult
                && item.Finding.SourceEvent is not null
                && item.Finding.SourceEvent.SequenceId > rootSequence
                && item.Finding.Result is UnitTestResultState.Abort or UnitTestResultState.Error)
            .OrderBy(item => item.Finding.SourceEvent!.SequenceId)
            .ToArray();
        var facts = new List<EvidenceAnalysisStatement>
        {
            factory.Fact(
                timeout ? "plc-framework-timeout" : "plc-framework-error",
                $"{rootFinding.Finding.Name}: {rootFinding.Finding.Message}",
                rootFinding.Finding.SourceEvent)
        };
        facts.AddRange(consequences.Select(item => factory.Fact(
            item.Finding.Result == UnitTestResultState.Abort
                ? "recovery-consequence"
                : "propagated-owner-result",
            $"{item.Node.Kind} '{item.Node.DisplayName}' subsequently reported '{item.Finding.Result}': {item.Finding.Message}",
            item.Finding.SourceEvent)));

        incidents.Add(factory.Create(
            EvidenceIncidentClassification.LifecycleReactionFailure,
            EvidenceIncidentSeverity.Error,
            completeness,
            timeout ? "PLC Framework timeout observed" : "PLC Framework error observed",
            timeout
                ? $"The PLC Framework reported a timeout before {consequences.Length} subsequent owner recovery or propagation result(s)."
                : $"The PLC Framework reported an error before {consequences.Length} subsequent owner recovery or propagation result(s).",
            new[] { rootFinding.Node }
                .Concat(consequences.Select(static item => item.Node))
                .Select(Owner)
                .DistinctBy(static owner => owner.Id)
                .ToArray(),
            facts,
            [factory.Conclusion(
                timeout ? "plc-framework-timeout-root" : "plc-framework-error-root",
                "The PLC Framework event is the first retained causal error; later Abort or owner Error results are consequences, not independent test failures.")],
            [new EvidenceSuggestedCheck(
                timeout ? "inspect-plc-timeout-owner" : "inspect-plc-framework-error",
                $"Inspect PLC Framework event '{rootFinding.Finding.Name}' and the last owner progress before PLC sequence {rootSequence}: {rootFinding.Finding.Message}")]));

        return rootSequence;
    }

    private static bool IsEventBufferCapacityFinding(UnitTestFinding finding)
    {
        return finding.Kind == UnitTestFindingKind.Framework
            && (finding.Name.Equals("UnitTestEventBuffer", StringComparison.OrdinalIgnoreCase)
                || finding.Message.Contains("event buffer", StringComparison.OrdinalIgnoreCase)
                || finding.Message.Contains("eventbuffer", StringComparison.OrdinalIgnoreCase))
            && (finding.Message.Contains("full", StringComparison.OrdinalIgnoreCase)
                || finding.Message.Contains("capacity", StringComparison.OrdinalIgnoreCase)
                || finding.Message.Contains("lost", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddTestFailureIncident(
        UnitTestRunEpochSnapshot run,
        EvidenceCompleteness completeness,
        uint? frameworkRootSequence,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var nodes = EnumerateNodes(run.Root).ToArray();
        var assertionFindings = nodes
            .SelectMany(node => node.Findings.Select(finding => (Node: node, Finding: finding)))
            .Where(static item => item.Finding.Kind == UnitTestFindingKind.Assertion
                && item.Finding.Result is UnitTestResultState.Failed or UnitTestResultState.Error)
            .OrderBy(item => item.Finding.SourceEvent?.SequenceId ?? uint.MaxValue)
            .ToArray();
        var ownerResultFindings = nodes
            .SelectMany(node => node.Findings.Select(finding => (Node: node, Finding: finding)))
            .Where(item => item.Finding.Kind == UnitTestFindingKind.OwnerResult
                && item.Finding.Result is UnitTestResultState.Failed or UnitTestResultState.Error
                && (frameworkRootSequence is null
                    || item.Finding.SourceEvent is not null
                        && item.Finding.SourceEvent.SequenceId < frameworkRootSequence.Value))
            .ToArray();
        var failedFindings = assertionFindings.Concat(ownerResultFindings).ToArray();
        var failedNodes = nodes
            .Where(node => node.Result is UnitTestResultState.Failed or UnitTestResultState.Error
                && node.ResultEvent is not null
                && (assertionFindings.Length > 0 || frameworkRootSequence is null))
            .ToArray();
        if (failedNodes.Length == 0 && failedFindings.Length == 0)
        {
            return;
        }

        var facts = new List<EvidenceAnalysisStatement>();
        if (assertionFindings.Length > 0)
        {
            var first = assertionFindings[0];
            facts.Add(factory.Fact(
                "first-failing-assertion",
                $"{first.Node.DisplayName}/{first.Finding.Name}: {first.Finding.Message}",
                first.Finding.SourceEvent));
            facts.AddRange(assertionFindings.Skip(1).Select(item => factory.Fact(
                "failing-assertion",
                $"{item.Node.DisplayName}/{item.Finding.Name}: {item.Finding.Message}",
                item.Finding.SourceEvent)));
        }

        facts.AddRange(ownerResultFindings.Select(item => factory.Fact(
            "owner-result-failed",
            $"{item.Node.DisplayName}: {item.Finding.Message}",
            item.Finding.SourceEvent)));
        foreach (var node in failedNodes.Where(node => node.Findings.Count == 0))
        {
            var reported = node.PlcReportedResult;
            facts.Add(factory.Fact(
                reported is null || reported == node.Result
                    ? "owner-result-failed"
                    : "effective-owner-result-escalated",
                reported is null
                    ? $"{node.Kind} '{node.DisplayName}' has effective PC result '{node.Result}'; no PLC owner Result event was observed."
                    : reported == node.Result
                        ? $"{node.Kind} '{node.DisplayName}' reported result '{reported}'."
                        : $"{node.Kind} '{node.DisplayName}' reported '{reported}', while child-result escalation produced effective PC result '{node.Result}'.",
                node.ResultEvent));
        }

        incidents.Add(factory.Create(
            EvidenceIncidentClassification.TestFailure,
            EvidenceIncidentSeverity.Error,
            completeness,
            "UnitTest failures observed",
            $"The run contains {assertionFindings.Length} failed assertion finding(s) and {failedNodes.Length} owner(s) with a failed or erroneous effective PC result.",
            failedNodes.Concat(failedFindings.Select(static item => item.Node)).Select(Owner).DistinctBy(static owner => owner.Id).ToArray(),
            facts,
            [factory.Conclusion("test-result-not-passed", "At least one UnitTest owner or finding did not pass.")],
            assertionFindings.Length == 0
                ? [new EvidenceSuggestedCheck("inspect-failed-findings", "Inspect the affected TestCase findings and PLC owner-result messages.")]
                : [new EvidenceSuggestedCheck(
                    "inspect-first-failing-assertion",
                    $"Inspect Assertion '{assertionFindings[0].Finding.Name}' in TestCase '{assertionFindings[0].Node.DisplayName}' and verify its reported actual and expected inputs: {assertionFindings[0].Finding.Message}")]));
    }

    private static void AddContractConflictIncident(
        UnitTestRunEpochSnapshot run,
        EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var nodes = EnumerateNodes(run.Root).ToArray();
        var consistencyFindings = nodes
            .SelectMany(node => node.Findings.Select(finding => (Node: node, Finding: finding)))
            .Where(static item => item.Finding.Kind == UnitTestFindingKind.Consistency)
            .ToArray();
        var hierarchyConflicts = nodes
            .Where(HasConflictingChildResult)
            .ToArray();
        if (consistencyFindings.Length == 0
            && hierarchyConflicts.Length == 0
            && run.EvidenceState != EvidenceState.Conflicting)
        {
            return;
        }

        var facts = consistencyFindings
            .Select(item => factory.Fact(
                "consistency-finding",
                $"{item.Node.DisplayName}: {item.Finding.Message}",
                item.Finding.SourceEvent))
            .ToList();
        facts.AddRange(hierarchyConflicts.Select(node => factory.Fact(
            "hierarchy-result-conflict",
            $"{node.Kind} '{node.DisplayName}' has PLC-reported result '{FormatReportedResult(node)}' while a child has an incompatible effective result; "
                + $"the effective PC result is '{node.Result}'.",
            node.ResultEvent)));
        if (run.EvidenceState == EvidenceState.Conflicting)
        {
            facts.Add(factory.Fact("run-evidence-conflicting", "The RunEpoch evidence state is Conflicting."));
        }

        incidents.Add(factory.Create(
            EvidenceIncidentClassification.ContractConflict,
            EvidenceIncidentSeverity.Error,
            completeness,
            "Conflicting run evidence",
            "The retained RunEpoch contains internally conflicting result or contract evidence.",
            consistencyFindings.Select(static item => item.Node).Concat(hierarchyConflicts).Select(Owner).DistinctBy(static owner => owner.Id).ToArray(),
            facts,
            [factory.Conclusion("run-evidence-conflict", "The run result hierarchy cannot be treated as internally consistent.")],
            [new EvidenceSuggestedCheck("inspect-contract-events", "Inspect the referenced PLC events and verify the UnitTest communication-contract event order and payloads.")]));
    }

    private static void AddIncompleteEvidenceIncident(
        UnitTestRunEpochSnapshot run,
        EvidenceCompleteness completeness,
        bool traceGap,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var directlyMissingNodes = EnumerateNodes(run.Root)
            .Where(node => HasDirectMissingTerminalEvidence(run, node))
            .ToArray();
        var affectedBranchNodes = FindAffectedBranchNodes(run.Root, directlyMissingNodes)
            .ToArray();
        if (completeness == EvidenceCompleteness.Complete
            && run.LostEventCount == 0
            && directlyMissingNodes.Length == 0)
        {
            return;
        }

        var facts = new List<EvidenceAnalysisStatement>();
        if (run.LostEventCount > 0)
        {
            facts.Add(factory.Fact("lost-events", $"The producer reported {run.LostEventCount} lost event(s)."));
        }

        if (run.EvidenceState != EvidenceState.Complete)
        {
            facts.Add(factory.Fact("run-evidence-state", $"The RunEpoch evidence state is '{run.EvidenceState}'."));
        }

        if (traceGap)
        {
            facts.Add(factory.Fact("trace-prefix-evicted", "The retained trace does not contain the RunEpoch start boundary and older activity entries were evicted."));
        }

        if (run.State is UnitTestRunState.TimedOut or UnitTestRunState.Invalidated)
        {
            facts.Add(factory.Fact("run-terminal-state", $"The RunEpoch ended in state '{run.State}'."));
        }

        facts.AddRange(directlyMissingNodes.Select(node => factory.Fact(
            "owner-terminal-evidence-missing",
            CreateMissingEvidenceMessage(node))));
        facts.AddRange(affectedBranchNodes
            .Where(node => directlyMissingNodes.All(missing => missing.Id != node.Id))
            .Select(node => factory.Fact(
                "owner-branch-affected",
                $"{node.Kind} '{node.DisplayName}' is an ancestor of an owner with missing terminal evidence; its effective branch result cannot be fully evidenced.")));

        var summary = directlyMissingNodes.Length == 0
            ? "Run-wide evidence confidence is incomplete, but no retained owner can be factually identified as missing terminal Result, Aggregation, or completion evidence."
            : $"Terminal evidence is directly missing for {directlyMissingNodes.Length} owner(s); only their affected hierarchy branch is attributed.";

        incidents.Add(factory.Create(
            EvidenceIncidentClassification.EvidenceIncomplete,
            EvidenceIncidentSeverity.Error,
            completeness,
            "Incomplete run evidence",
            summary,
            affectedBranchNodes.Select(Owner).ToArray(),
            facts,
            [factory.Conclusion("run-evidence-incomplete", "The RunEpoch analysis is incomplete and must not be treated as a fully evidenced result.")],
            [CreateIncompleteEvidenceCheck(run, directlyMissingNodes)]));
    }

    private static bool HasDirectMissingTerminalEvidence(UnitTestRunEpochSnapshot run, UnitTestRunNode node)
    {
        if (run.State is not (UnitTestRunState.Completed or UnitTestRunState.Aborted or UnitTestRunState.TimedOut or UnitTestRunState.Invalidated))
        {
            return false;
        }

        if (node.CreationEvent is null)
        {
            return false;
        }

        if (node.ResultEvent is null)
        {
            return true;
        }

        if (node.LifecycleState == UnitTestLifecycleState.Completed && node.CompletionEvent is null)
        {
            return true;
        }

        return false;
    }

    private static string CreateMissingEvidenceMessage(UnitTestRunNode node)
    {
        var missing = new List<string>();
        if (node.ResultEvent is null)
        {
            missing.Add("Result");
        }

        if (node.AggregationEvent is null)
        {
            missing.Add("Aggregation");
        }

        if (node.CompletionEvent is null)
        {
            missing.Add("completion");
        }

        return $"{node.Kind} '{node.DisplayName}' is missing {string.Join(", ", missing)} evidence; "
            + $"its last observed lifecycle state is '{node.LifecycleState}' and its effective PC result is '{node.Result}'.";
    }

    private static IReadOnlyList<UnitTestRunNode> FindAffectedBranchNodes(
        UnitTestRunNode root,
        IReadOnlyList<UnitTestRunNode> directlyMissingNodes)
    {
        var missingIds = directlyMissingNodes.Select(static node => node.Id).ToHashSet();
        var affected = new List<UnitTestRunNode>();
        AddAffectedBranch(root, missingIds, affected);
        return affected;
    }

    private static bool AddAffectedBranch(
        UnitTestRunNode node,
        IReadOnlySet<UnitTestNodeId> missingIds,
        List<UnitTestRunNode> affected)
    {
        var containsMissing = missingIds.Contains(node.Id);
        foreach (var child in node.Children)
        {
            containsMissing |= AddAffectedBranch(child, missingIds, affected);
        }

        if (containsMissing)
        {
            affected.Add(node);
        }

        return containsMissing;
    }

    private static EvidenceSuggestedCheck CreateIncompleteEvidenceCheck(
        UnitTestRunEpochSnapshot run,
        IReadOnlyList<UnitTestRunNode> directlyMissingNodes)
    {
        if (run.LostEventCount > 0)
        {
            return new EvidenceSuggestedCheck(
                "inspect-event-loss",
                "Inspect the reported EventBuffer loss and adjacent PLC event sequence gaps; do not assume that every retained owner lacks terminal evidence.");
        }

        if (run.State == UnitTestRunState.TimedOut)
        {
            return new EvidenceSuggestedCheck(
                "inspect-run-observation-timeout",
                "Compare the configured PC Run observation timeout with the expected PLC Run duration and inspect whether terminal RunControl evidence arrived after the observation closed.");
        }

        return new EvidenceSuggestedCheck(
            "inspect-missing-owner-evidence",
            directlyMissingNodes.Count == 0
                ? "Inspect communication invalidation and RunEpoch boundaries that reduced global Run confidence."
                : $"Inspect the directly affected PLC owner lifecycle and verify that its missing {DescribeMissingEvidence(directlyMissingNodes)} evidence is emitted before the parent completes.");
    }

    private static string DescribeMissingEvidence(IReadOnlyList<UnitTestRunNode> nodes)
    {
        var missing = new List<string>();
        if (nodes.Any(static node => node.ResultEvent is null))
        {
            missing.Add("Result");
        }

        if (nodes.Any(static node => node.AggregationEvent is null))
        {
            missing.Add("Aggregation");
        }

        if (nodes.Any(static node => node.CompletionEvent is null))
        {
            missing.Add("completion");
        }

        return string.Join(", ", missing);
    }

    private static bool HasConflictingChildResult(UnitTestRunNode node)
    {
        var parentResult = node.PlcReportedResult ?? node.Result;
        if (parentResult is UnitTestResultState.NotEvaluated or UnitTestResultState.Unknown)
        {
            return false;
        }

        return node.Children.Any(child => child.Result switch
        {
            UnitTestResultState.Error => parentResult != UnitTestResultState.Error,
            UnitTestResultState.Abort => parentResult is not (UnitTestResultState.Abort or UnitTestResultState.Error),
            UnitTestResultState.Failed => parentResult == UnitTestResultState.Passed,
            _ => false
        });
    }

    private static EvidenceCompleteness DetermineCompleteness(
        UnitTestRunEpochSnapshot run,
        bool traceGap,
        bool hasMissingEvidence)
    {
        if (run.EvidenceState == EvidenceState.None)
        {
            return EvidenceCompleteness.Unavailable;
        }

        if (traceGap
            || hasMissingEvidence
            || run.LostEventCount > 0
            || run.State is UnitTestRunState.TimedOut or UnitTestRunState.Invalidated)
        {
            return EvidenceCompleteness.Incomplete;
        }

        return run.EvidenceState switch
        {
            EvidenceState.Complete or EvidenceState.Conflicting => EvidenceCompleteness.Complete,
            _ => EvidenceCompleteness.Incomplete
        };
    }

    private static IEnumerable<UnitTestRunNode> EnumerateNodes(UnitTestRunNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumerateNodes(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsRunEntry(UnitTestActivityEntry entry, RunEpochId runEpochId)
    {
        return entry.Payload is RunEpochActivityPayload payload
            && payload.Snapshot.EpochId == runEpochId;
    }

    private static LateRunCompletionEvidence? FindLateCompletionEvidence(
        UnitTestActivityTraceSnapshot trace,
        UnitTestActivityEntry runEntry,
        UnitTestRunEpochSnapshot run)
    {
        if (run.State != UnitTestRunState.TimedOut)
        {
            return null;
        }

        var candidates = trace.Entries
            .Where(entry => entry.Sequence > runEntry.Sequence
                && entry.CommunicationSessionId == runEntry.CommunicationSessionId
                && entry.RunEpochId is null
                && entry.Payload is PlcEventActivityPayload)
            .OrderBy(static entry => entry.Sequence)
            .TakeWhile(entry => !IsRunControlStartAnchor(((PlcEventActivityPayload)entry.Payload).Event))
            .ToArray();
        var completion = candidates.FirstOrDefault(entry =>
            IsRunControlCompletionAnchor(((PlcEventActivityPayload)entry.Payload).Event));
        if (completion is null)
        {
            return null;
        }

        var terminalEntries = candidates
            .TakeWhile(entry => entry.Sequence <= completion.Sequence)
            .Where(entry =>
            {
                var plcEvent = ((PlcEventActivityPayload)entry.Payload).Event;
                return plcEvent.OwnerScope == UnitTestOwnerScope.RunControl
                    && plcEvent.EventType is UnitTestEventType.Result
                        or UnitTestEventType.Aggregation
                        or UnitTestEventType.StateTransition
                    && (plcEvent.EventType != UnitTestEventType.StateTransition
                        || plcEvent.State is UnitTestLifecycleState.Completing or UnitTestLifecycleState.Completed);
            })
            .ToArray();
        return new LateRunCompletionEvidence(completion, terminalEntries);
    }

    private static bool IsRunControlStartAnchor(UnitTestEventEntry entry) =>
        entry.OwnerScope == UnitTestOwnerScope.RunControl
        && entry.EventType == UnitTestEventType.StateTransition
        && entry.State == UnitTestLifecycleState.Starting;

    private static bool IsRunControlCompletionAnchor(UnitTestEventEntry entry) =>
        entry.OwnerScope == UnitTestOwnerScope.RunControl
        && entry.EventType == UnitTestEventType.StateTransition
        && entry.State == UnitTestLifecycleState.Completed;

    private sealed record LateRunCompletionEvidence(
        UnitTestActivityEntry Completion,
        IReadOnlyList<UnitTestActivityEntry> TerminalEntries);

    private static EvidenceAffectedOwner Owner(UnitTestRunNode node) =>
        new(node.Id, node.Kind, node.DisplayName);

    private sealed class IncidentFactory
    {
        private readonly EvidenceSnapshotId analysisId;
        private readonly RunEvidenceAnalysis run;
        private readonly UnitTestActivityEntry runEntry;
        private readonly IReadOnlyList<UnitTestActivityEntry> traceEntries;
        private readonly IReadOnlyDictionary<uint, UnitTestActivityEntry> plcEventsBySequence;
        private int nextIncident = 1;

        public IncidentFactory(
            EvidenceSnapshotId analysisId,
            RunEvidenceAnalysis run,
            UnitTestActivityEntry runEntry,
            UnitTestActivityTraceSnapshot trace)
        {
            this.analysisId = analysisId;
            this.run = run;
            this.runEntry = runEntry;
            traceEntries = trace.Entries;
            plcEventsBySequence = trace.Entries
                .Where(entry => entry.RunEpochId == run.EpochId && entry.Payload is PlcEventActivityPayload)
                .GroupBy(entry => ((PlcEventActivityPayload)entry.Payload).Event.SequenceId)
                .ToDictionary(static group => group.Key, static group => group.Last());
        }

        public EvidenceAnalysisStatement Fact(string code, string message, UnitTestEventRef? source = null) =>
            new(code, message, References(source));

        public EvidenceAnalysisStatement Fact(string code, string message, UnitTestActivityEntry source) =>
            new(code, message, [Reference(source)]);

        public EvidenceAnalysisStatement Conclusion(string code, string message) =>
            new(code, message, [new EvidenceReference(runEntry.Sequence)]);

        public EvidenceAnalysisStatement Conclusion(string code, string message, UnitTestActivityEntry source) =>
            new(code, message, [Reference(source)]);

        public bool IsErrorPlcEvent(UnitTestEventRef? source)
        {
            return source is not null
                && plcEventsBySequence.TryGetValue(source.SequenceId, out var entry)
                && entry.Payload is PlcEventActivityPayload payload
                && payload.Event.Severity is LogLevel.Error or LogLevel.Critical;
        }

        public EvidenceIncident Create(
            EvidenceIncidentClassification classification,
            EvidenceIncidentSeverity severity,
            EvidenceCompleteness completeness,
            string title,
            string summary,
            IReadOnlyList<EvidenceAffectedOwner> affectedOwners,
            IReadOnlyList<EvidenceAnalysisStatement> facts,
            IReadOnlyList<EvidenceAnalysisStatement> conclusions,
            IReadOnlyList<EvidenceSuggestedCheck> suggestedChecks)
        {
            var factReferences = facts
                .SelectMany(static fact => fact.Evidence)
                .Distinct()
                .OrderBy(static reference => reference.ActivitySequence)
                .ToArray();
            var references = factReferences
                .Append(new EvidenceReference(runEntry.Sequence))
                .Distinct()
                .OrderBy(static reference => reference.ActivitySequence)
                .ToArray();
            var timelineReferences = ExpandCausalWindow(references, factReferences.FirstOrDefault());
            var timeline = timelineReferences
                .Select(CreateTimelineItem)
                .ToArray();
            var openedAt = factReferences
                .Select(reference => traceEntries.FirstOrDefault(entry => entry.Sequence == reference.ActivitySequence))
                .FirstOrDefault(static entry => entry is not null)?.ObservedAt
                ?? runEntry.ObservedAt;
            return new EvidenceIncident(
                new EvidenceIncidentId($"{analysisId.Value}:{nextIncident++}"),
                classification,
                severity,
                completeness,
                EvidenceIncidentScope.Run,
                title,
                summary,
                openedAt,
                runEntry.ObservedAt,
                run,
                null,
                affectedOwners,
                facts,
                conclusions,
                suggestedChecks,
                timeline);
        }

        private IReadOnlyList<EvidenceReference> ExpandCausalWindow(
            IReadOnlyList<EvidenceReference> references,
            EvidenceReference? firstFactReference)
        {
            var selected = references
                .GroupBy(static reference => reference.ActivitySequence)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.FirstOrDefault(static reference => reference.PlcEventSequenceId is not null)
                        ?? group.First());
            if (firstFactReference is null)
            {
                return selected.Values.OrderBy(static reference => reference.ActivitySequence).ToArray();
            }

            var causalEntries = traceEntries
                .Where(entry => entry.RunEpochId == run.EpochId
                    && entry.Sequence <= runEntry.Sequence
                    && entry.Payload is PlcEventActivityPayload or RunControlActivityPayload or RunEpochActivityPayload)
                .OrderBy(static entry => entry.Sequence)
                .ToArray();
            var preceding = causalEntries.LastOrDefault(entry => entry.Sequence < firstFactReference.ActivitySequence);
            if (preceding is not null)
            {
                selected[preceding.Sequence] = Reference(preceding);
            }

            foreach (var following in causalEntries
                .Where(entry => entry.Sequence > firstFactReference.ActivitySequence)
                .Take(6))
            {
                selected[following.Sequence] = Reference(following);
            }

            var recoveryCommand = traceEntries.FirstOrDefault(entry =>
                entry.Sequence > firstFactReference.ActivitySequence
                && entry.Payload is CommandActivityPayload);
            if (recoveryCommand is not null)
            {
                selected[recoveryCommand.Sequence] = Reference(recoveryCommand);
                if (recoveryCommand.OperationId is not null)
                {
                    foreach (var commandEntry in traceEntries.Where(entry =>
                        entry.OperationId == recoveryCommand.OperationId
                        && entry.Payload is CommandActivityPayload))
                    {
                        selected[commandEntry.Sequence] = Reference(commandEntry);
                    }
                }
            }

            return selected.Values.OrderBy(static reference => reference.ActivitySequence).ToArray();
        }

        private static EvidenceReference Reference(UnitTestActivityEntry entry)
        {
            return entry.Payload is PlcEventActivityPayload plcEvent
                ? new EvidenceReference(entry.Sequence, plcEvent.Event.SequenceId)
                : new EvidenceReference(entry.Sequence);
        }

        private IReadOnlyList<EvidenceReference> References(UnitTestEventRef? source)
        {
            if (source is not null && plcEventsBySequence.TryGetValue(source.SequenceId, out var eventEntry))
            {
                return [new EvidenceReference(eventEntry.Sequence, source.SequenceId)];
            }

            return [new EvidenceReference(runEntry.Sequence, source?.SequenceId)];
        }

        private EvidenceTimelineItem CreateTimelineItem(EvidenceReference reference)
        {
            if (reference.PlcEventSequenceId is not null
                && plcEventsBySequence.TryGetValue(reference.PlcEventSequenceId.Value, out var eventEntry)
                && eventEntry.Payload is PlcEventActivityPayload eventPayload)
            {
                return new EvidenceTimelineItem(
                    eventEntry.ObservedAt,
                    EvidenceTimelineKind.PlcEvent,
                    $"{eventPayload.Event.OwnerScope}/{eventPayload.Event.EventType}: {eventPayload.Event.Message}",
                    [reference]);
            }

            var activityEntry = traceEntries.FirstOrDefault(entry => entry.Sequence == reference.ActivitySequence);
            if (activityEntry?.Payload is PlcEventActivityPayload plcEvent)
            {
                return new EvidenceTimelineItem(
                    activityEntry.ObservedAt,
                    EvidenceTimelineKind.PlcEvent,
                    $"{plcEvent.Event.OwnerScope}/{plcEvent.Event.EventType}: {plcEvent.Event.Message}",
                    [reference]);
            }

            if (activityEntry?.Payload is RunEpochActivityPayload runEpoch)
            {
                return new EvidenceTimelineItem(
                    activityEntry.ObservedAt,
                    EvidenceTimelineKind.Analysis,
                    $"RunEpoch {runEpoch.Activity}: {runEpoch.Snapshot.State}/{runEpoch.Snapshot.Result}/{runEpoch.Snapshot.EvidenceState}.",
                    [reference]);
            }

            if (activityEntry?.Payload is CommandActivityPayload command)
            {
                return new EvidenceTimelineItem(
                    activityEntry.ObservedAt,
                    EvidenceTimelineKind.CommandTransport,
                    $"{command.Command} command {command.Phase}.",
                    [reference]);
            }

            return new EvidenceTimelineItem(
                activityEntry?.ObservedAt ?? runEntry.ObservedAt,
                EvidenceTimelineKind.Analysis,
                activityEntry is null
                    ? $"RunEpoch {run.State}/{run.Result}/{run.EvidenceState}."
                    : $"{activityEntry.Kind} activity.",
                [reference]);
        }
    }
}
