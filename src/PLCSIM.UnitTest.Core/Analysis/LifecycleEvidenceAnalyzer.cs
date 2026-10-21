using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Core.Analysis;

/// <summary>
/// Deterministic on-demand analysis of one PLC lifecycle observation retained in a UnitTest activity trace.
/// </summary>
public sealed class LifecycleEvidenceAnalyzer
{
    private readonly TimeProvider timeProvider;

    public LifecycleEvidenceAnalyzer(CoreServiceContext coreContext)
        : this(coreContext?.TimeProvider ?? throw new ArgumentNullException(nameof(coreContext)))
    {
    }

    public LifecycleEvidenceAnalyzer(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public EvidenceAnalysisSnapshot Analyze(
        UnitTestActivityTraceSnapshot trace,
        LifecycleObservationId observationId)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (observationId.IsEmpty)
        {
            throw new ArgumentException("Lifecycle observation ID must not be empty.", nameof(observationId));
        }

        var generatedAt = timeProvider.GetUtcNow();
        var analysisId = new EvidenceSnapshotId(
            $"lifecycle:{trace.SessionId.Value}:{observationId.Value}:{trace.NewestSequence}");
        var entries = SelectEntries(trace, observationId);
        if (entries.Length == 0)
        {
            return CreateUnavailableAnalysis(trace, observationId, analysisId, generatedAt);
        }

        var completeness = IsWindowPrefixPossiblyEvicted(trace, entries)
            ? EvidenceCompleteness.Incomplete
            : EvidenceCompleteness.Complete;
        var factory = new IncidentFactory(analysisId, entries, observationId);
        var incidents = new List<EvidenceIncident>();

        AddCommandTransportIncidents(entries, completeness, factory, incidents);
        AddLifecycleReactionIncidents(entries, completeness, factory, incidents);
        AddContractConflictIncidents(entries, completeness, factory, incidents);
        AddIncompleteEvidenceIncident(entries, trace, observationId, ref completeness, factory, incidents);

        var assessment = DetermineAssessment(completeness, incidents);
        var summary = CreateSummary(entries, observationId, completeness, incidents);

        return new EvidenceAnalysisSnapshot(
            analysisId,
            trace.SessionId,
            EvidenceAnalysisScope.Lifecycle,
            null,
            observationId,
            generatedAt,
            entries[0].Sequence,
            entries[^1].Sequence,
            completeness,
            incidents.AsReadOnly(),
            assessment,
            summary);
    }

    private static UnitTestActivityEntry[] SelectEntries(
        UnitTestActivityTraceSnapshot trace,
        LifecycleObservationId observationId)
    {
        return trace.Entries
            .Where(entry => HasObservation(entry, observationId))
            .OrderBy(static entry => entry.Sequence)
            .ToArray();
    }

    private static bool IsWindowPrefixPossiblyEvicted(
        UnitTestActivityTraceSnapshot trace,
        IReadOnlyList<UnitTestActivityEntry> entries)
    {
        return trace.EvictedEntryCount > 0
            && entries.Count > 0
            && entries[0].Sequence == trace.OldestSequence;
    }

    private static bool HasObservation(UnitTestActivityEntry entry, LifecycleObservationId observationId)
    {
        return entry.LifecycleObservationId == observationId
            || entry.Payload is DiagnosticActivityPayload diagnostic
                && diagnostic.Diagnostic.Correlation.LifecycleObservationId == observationId;
    }

    private static EvidenceAnalysisSnapshot CreateUnavailableAnalysis(
        UnitTestActivityTraceSnapshot trace,
        LifecycleObservationId observationId,
        EvidenceSnapshotId analysisId,
        DateTimeOffset generatedAt)
    {
        var incident = new EvidenceIncident(
            new EvidenceIncidentId($"{analysisId.Value}:1"),
            EvidenceIncidentClassification.EvidenceIncomplete,
            EvidenceIncidentSeverity.Error,
            EvidenceCompleteness.Unavailable,
            EvidenceIncidentScope.Lifecycle,
            "Lifecycle evidence unavailable",
            $"No retained activity was found for lifecycle observation '{observationId.Value}'.",
            trace.StartedAt,
            trace.EndedAt,
            null,
            null,
            [],
            [new EvidenceAnalysisStatement("lifecycle-observation-missing", "No retained activity matches the requested lifecycle observation ID.", [])],
            [new EvidenceAnalysisStatement("lifecycle-analysis-unavailable", "The requested lifecycle observation cannot be analyzed from the retained activity trace.", [])],
            [new EvidenceSuggestedCheck("verify-lifecycle-selection", "Verify that the selected lifecycle observation belongs to the retained application session and was captured before trace eviction.")],
            []);

        return new EvidenceAnalysisSnapshot(
            analysisId,
            trace.SessionId,
            EvidenceAnalysisScope.Lifecycle,
            null,
            observationId,
            generatedAt,
            trace.OldestSequence,
            trace.NewestSequence,
            EvidenceCompleteness.Unavailable,
            [incident],
            EvidenceAnalysisAssessment.Inconclusive,
            $"Lifecycle observation '{observationId.Value}' cannot be evaluated because no matching retained activity is available.");
    }

    private static EvidenceAnalysisAssessment DetermineAssessment(
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

        return completeness == EvidenceCompleteness.Complete
            ? EvidenceAnalysisAssessment.Healthy
            : EvidenceAnalysisAssessment.Inconclusive;
    }

    private static string CreateSummary(
        IReadOnlyList<UnitTestActivityEntry> entries,
        LifecycleObservationId observationId,
        EvidenceCompleteness completeness,
        IReadOnlyList<EvidenceIncident> incidents)
    {
        var completedCommands = entries
            .Where(static entry => entry.Payload is CommandActivityPayload { Phase: UnitTestActivityCommandPhase.Completed })
            .Select(static entry => (CommandActivityPayload)entry.Payload)
            .ToArray();
        var focusItems = completedCommands
            .Select(static command => $"{command.Command} command handover")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (entries.Any(static entry => entry.Payload is PlcStateActivityPayload
            {
                State.OperatingState: PlcOperatingState.Running
            }))
        {
            focusItems.Add("PLC startup initialization");
        }

        var focus = focusItems.Count == 0
            ? $"PLC lifecycle observation '{observationId.Value}'"
            : string.Join(", ", focusItems.Distinct(StringComparer.Ordinal));

        if (incidents.Count == 0)
        {
            return $"Analysis of {focus} found no reported lifecycle-reaction, command-transport, contract, or focus-local evidence-completeness problem in {entries.Count} retained activity entry or entries.";
        }

        return $"Analysis of {focus} produced {incidents.Count} finding(s). Evidence completeness is '{completeness}'. Review the findings and their focused chronology before drawing PLC-side conclusions.";
    }

    private static void AddCommandTransportIncidents(
        IReadOnlyList<UnitTestActivityEntry> entries,
        EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        foreach (var entry in entries.Where(IsFailedCommandCompletion))
        {
            var command = (CommandActivityPayload)entry.Payload;
            var outcome = command.Accepted == false
                ? "was not accepted"
                : $"completed with result '{command.ResultState}'";
            incidents.Add(factory.Create(
                EvidenceIncidentClassification.CommandTransportFailure,
                EvidenceIncidentSeverity.Error,
                completeness,
                $"{command.Command} command transport failed",
                $"The {command.Command} command {outcome}.",
                entry,
                [factory.Fact("command-transport-failed", $"The completed {command.Command} command {outcome}. {command.Message}".Trim(), entry)],
                [factory.Conclusion("command-not-delivered", "The application did not observe a successful command handover to the access service.", entry)],
                [new EvidenceSuggestedCheck("inspect-command-transport", "Inspect the command diagnostic and access health at the same operation timestamp.")]));
        }
    }

    private static bool IsFailedCommandCompletion(UnitTestActivityEntry entry)
    {
        if (entry.Payload is not CommandActivityPayload { Phase: UnitTestActivityCommandPhase.Completed } command)
        {
            return false;
        }

        return command.Accepted == false
            || command.ResultState is OperationResultState.Failed
                or OperationResultState.NotSupported
                or OperationResultState.Cancelled
                or OperationResultState.TimedOut;
    }

    private static void AddLifecycleReactionIncidents(
        IReadOnlyList<UnitTestActivityEntry> entries,
        EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        foreach (var entry in entries.Where(IsLifecycleReactionFailure))
        {
            var diagnostic = ((DiagnosticActivityPayload)entry.Payload).Diagnostic;
            var incidentObservationId = GetLifecycleObservationId(entry);
            var contextEntries = incidentObservationId is { } currentObservationId
                ? entries.Where(candidate => HasObservation(candidate, currentObservationId)).ToArray()
                : entries;
            var frameworkEvent = FindMatchingFrameworkEvent(contextEntries, entry, diagnostic.Message);
            var cutoffSequence = frameworkEvent?.Sequence ?? entry.Sequence;
            var commandEntries = contextEntries
                .Where(candidate => candidate.Sequence < cutoffSequence
                    && candidate.Payload is CommandActivityPayload command
                    && command.Command is UnitTestActivityCommandKind.Reset or UnitTestActivityCommandKind.Start or UnitTestActivityCommandKind.Abort)
                .TakeLast(2)
                .ToArray();
            var plcStartupAnchor = contextEntries.FirstOrDefault(candidate =>
                candidate.Sequence < cutoffSequence
                && candidate.Payload is PlcStateActivityPayload
                {
                    State.OperatingState: PlcOperatingState.Running
                });
            var latestRunControl = contextEntries.LastOrDefault(candidate =>
                candidate.Sequence < cutoffSequence && candidate.Payload is RunControlActivityPayload);
            var lastOwnerProgress = contextEntries.LastOrDefault(candidate =>
                candidate.Sequence < cutoffSequence
                && candidate.Payload is PlcEventActivityPayload plcEvent
                && plcEvent.Event.OwnerScope is UnitTestOwnerScope.RunControl
                    or UnitTestOwnerScope.TestSuite
                    or UnitTestOwnerScope.TestCase
                    or UnitTestOwnerScope.Preparation);
            var directlyAffectedOwner = FindDirectlyAffectedOwner(contextEntries, frameworkEvent);
            var facts = new List<EvidenceAnalysisStatement>();
            if (plcStartupAnchor is not null)
            {
                facts.Add(factory.Fact(
                    "plc-startup-anchor",
                    "PLC entered the Running operating state and opened a new startup-initialization observation.",
                    plcStartupAnchor));
            }

            foreach (var commandEntry in commandEntries)
            {
                var command = (CommandActivityPayload)commandEntry.Payload;
                facts.Add(factory.Fact(
                    command.Phase == UnitTestActivityCommandPhase.Completed && command.Accepted == true
                        ? "command-handover-accepted"
                        : "command-requested",
                    $"{command.Command} command {command.Phase}: {command.ResultState}/{command.Accepted}.",
                    commandEntry));
            }

            if (latestRunControl is not null)
            {
                var observation = ((RunControlActivityPayload)latestRunControl.Payload).Observation;
                facts.Add(factory.Fact(
                    "latest-run-control-state",
                    $"Latest RunControl observation was {observation.ExecutionStatus}/{observation.State}/{observation.Result}; Ready={observation.Ready}, Running={observation.Running}.",
                    latestRunControl));
            }

            if (directlyAffectedOwner is not null)
            {
                var ownerEvent = ((PlcEventActivityPayload)directlyAffectedOwner.Payload).Event;
                facts.Add(factory.Fact(
                    "directly-affected-owner",
                    $"The PLC Framework event names {ownerEvent.OwnerScope} '{ownerEvent.Name}' as the directly affected lifecycle owner.",
                    directlyAffectedOwner));
            }

            if (lastOwnerProgress is not null)
            {
                var ownerEvent = ((PlcEventActivityPayload)lastOwnerProgress.Payload).Event;
                facts.Add(factory.Fact(
                    "last-owner-progress",
                    $"Last subordinate owner progress before the timeout was {ownerEvent.OwnerScope} '{ownerEvent.Name}' in state '{ownerEvent.State}' with result '{ownerEvent.Result}'.",
                    lastOwnerProgress));
            }

            if (frameworkEvent is not null)
            {
                var plcEvent = ((PlcEventActivityPayload)frameworkEvent.Payload).Event;
                facts.Add(factory.Fact(
                    "plc-framework-event",
                    $"PLC sequence {plcEvent.SequenceId} reported: {plcEvent.Message}",
                    frameworkEvent));
            }

            facts.Add(factory.Fact("lifecycle-failure-diagnostic", $"{diagnostic.Code}: {diagnostic.Message}", entry));
            var affectedOwners = new[] { directlyAffectedOwner, lastOwnerProgress }
                .Where(static owner => owner is not null)
                .Cast<UnitTestActivityEntry>()
                .Select(static owner => Owner(((PlcEventActivityPayload)owner.Payload).Event))
                .DistinctBy(static owner => (owner.Id, owner.Kind))
                .ToArray();
            incidents.Add(factory.Create(
                EvidenceIncidentClassification.LifecycleReactionFailure,
                EvidenceIncidentSeverity.Error,
                completeness,
                diagnostic.Code == "Workflow.EventStreamProcessor.FrameworkEvent"
                    ? "PLC Framework lifecycle timeout observed"
                    : "Lifecycle reaction failed",
                diagnostic.Message,
                entry,
                facts,
                [factory.Conclusion("required-reaction-not-observed", "An existing workflow or PLC diagnostic reported that the expected lifecycle reaction was not observed.", entry)],
                [new EvidenceSuggestedCheck("inspect-lifecycle-state", "Inspect the named PLC owner and its subordinate lifecycle states around the referenced Framework event.")],
                affectedOwners));
        }
    }

    private static LifecycleObservationId? GetLifecycleObservationId(UnitTestActivityEntry entry)
    {
        return entry.LifecycleObservationId
            ?? (entry.Payload as DiagnosticActivityPayload)?.Diagnostic.Correlation.LifecycleObservationId;
    }

    private static UnitTestActivityEntry? FindMatchingFrameworkEvent(
        IReadOnlyList<UnitTestActivityEntry> entries,
        UnitTestActivityEntry diagnosticEntry,
        string message)
    {
        return entries
            .Where(entry => entry.Payload is PlcEventActivityPayload plcEvent
                && (plcEvent.Event.OwnerScope == UnitTestOwnerScope.Framework
                    || plcEvent.Event.EventType == UnitTestEventType.Framework)
                && plcEvent.Event.Message.Equals(message, StringComparison.Ordinal))
            .OrderBy(entry => Math.Abs(entry.Sequence - diagnosticEntry.Sequence))
            .FirstOrDefault();
    }

    private static UnitTestActivityEntry? FindDirectlyAffectedOwner(
        IReadOnlyList<UnitTestActivityEntry> entries,
        UnitTestActivityEntry? frameworkEvent)
    {
        if (frameworkEvent?.Payload is not PlcEventActivityPayload frameworkPayload)
        {
            return null;
        }

        return entries
            .Where(entry => entry.Sequence <= frameworkEvent.Sequence
                && entry.Payload is PlcEventActivityPayload ownerPayload
                && ownerPayload.Event.OwnerScope is UnitTestOwnerScope.RunControl
                    or UnitTestOwnerScope.TestSuite
                    or UnitTestOwnerScope.TestCase
                    or UnitTestOwnerScope.Preparation
                && ownerPayload.Event.Name.Equals(frameworkPayload.Event.Name, StringComparison.Ordinal))
            .LastOrDefault();
    }

    private static EvidenceAffectedOwner Owner(UnitTestEventEntry entry)
    {
        var kind = entry.OwnerScope switch
        {
            UnitTestOwnerScope.RunControl => UnitTestNodeKind.RunControl,
            UnitTestOwnerScope.TestSuite or UnitTestOwnerScope.TestSuiteOrchestrator => UnitTestNodeKind.TestSuite,
            _ => UnitTestNodeKind.TestCase
        };
        return new EvidenceAffectedOwner(new UnitTestNodeId(entry.Name), kind, entry.Name);
    }

    private static bool IsLifecycleReactionFailure(UnitTestActivityEntry entry)
    {
        if (entry.Payload is not DiagnosticActivityPayload payload)
        {
            return false;
        }

        var diagnostic = payload.Diagnostic;
        return diagnostic.Code is "Workflow.TestRunMonitor.CommandReactionTimeout"
                or "Workflow.PlcCommand.StateObservationTimeout"
            || diagnostic.Code == "Workflow.EventStreamProcessor.FrameworkEvent"
                && (diagnostic.Message.Contains("reset timeout", StringComparison.OrdinalIgnoreCase)
                    || diagnostic.Message.Contains("initialization timeout", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddContractConflictIncidents(
        IReadOnlyList<UnitTestActivityEntry> entries,
        EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var conflicts = entries.Where(entry => entry.Payload is DiagnosticActivityPayload diagnostic
            && diagnostic.Diagnostic.Code == "Workflow.RunControlObservation.ContradictorySignals").ToArray();
        if (conflicts.Length == 0)
        {
            return;
        }

        incidents.Add(factory.Create(
            EvidenceIncidentClassification.ContractConflict,
            EvidenceIncidentSeverity.Warning,
            completeness,
            "Contradictory RunControl signals",
            "The observed RunControl state contained mutually contradictory execution signals.",
            conflicts[^1],
            conflicts.Select(entry => factory.Fact(
                "run-control-signals-contradictory",
                ((DiagnosticActivityPayload)entry.Payload).Diagnostic.Message,
                entry)).ToArray(),
            [factory.Conclusion("run-control-observation-conflicting", "The RunControl observation cannot be interpreted as one coherent lifecycle state.", conflicts[^1])],
            [new EvidenceSuggestedCheck("inspect-run-control-signals", "Inspect the PLC-owned execution status and signal bits in the same PLC cycle.")]));
    }

    private static void AddIncompleteEvidenceIncident(
        IReadOnlyList<UnitTestActivityEntry> entries,
        UnitTestActivityTraceSnapshot trace,
        LifecycleObservationId observationId,
        ref EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var evidenceFailures = entries.Where(IsEvidenceFailure).ToArray();
        var traceGap = IsWindowPrefixPossiblyEvicted(trace, entries);
        if (evidenceFailures.Length == 0 && !traceGap)
        {
            return;
        }

        completeness = EvidenceCompleteness.Incomplete;
        var anchor = evidenceFailures.LastOrDefault() ?? entries.LastOrDefault();
        if (anchor is null)
        {
            return;
        }

        var facts = evidenceFailures.Select(entry => factory.Fact(
            "lifecycle-evidence-incomplete",
            EvidenceFailureMessage(entry),
            entry)).ToList();
        if (traceGap)
        {
            facts.Add(factory.Fact(
                "trace-prefix-evicted",
                $"The bounded activity trace evicted {trace.EvictedEntryCount} earlier entry or entries.",
                anchor));
        }

        incidents.Add(factory.Create(
            EvidenceIncidentClassification.EvidenceIncomplete,
            EvidenceIncidentSeverity.Warning,
            EvidenceCompleteness.Incomplete,
            "PLC lifecycle evidence incomplete",
            "The selected PLC lifecycle window contains a sequence gap, lost PLC events, or an evicted window prefix.",
            anchor,
            facts,
            [factory.Conclusion("lifecycle-analysis-partial", "Some lifecycle conclusions may be unavailable because the retained observation is incomplete.", anchor)],
            [new EvidenceSuggestedCheck("inspect-event-stream-health", "Inspect lost-event count, EventBuffer state, and trace eviction before relying on absent lifecycle evidence.")]));
    }

    private static bool IsEvidenceFailure(UnitTestActivityEntry entry)
    {
        if (entry.Payload is EventStreamActivityPayload stream)
        {
            return stream.State.LostEventCount > 0
                || stream.State.LastReadState is EventBufferReadState.Inconsistent
                    or EventBufferReadState.ContractInvalid
                    or EventBufferReadState.SessionInvalid;
        }

        return entry.Payload is DiagnosticActivityPayload diagnostic
            && diagnostic.Diagnostic.Code is "Workflow.EventStreamProcessor.LostEvents"
                or "Workflow.EventStreamProcessor.SequenceGap"
                or "Workflow.EventStreamProcessor.ReadStateInvalidated"
                or "Workflow.EventStreamProcessor.ReadStateInconsistent";
    }

    private static string EvidenceFailureMessage(UnitTestActivityEntry entry)
    {
        return entry.Payload switch
        {
            EventStreamActivityPayload stream => $"Event stream state is {stream.State.LastReadState} with {stream.State.LostEventCount} lost event(s).",
            DiagnosticActivityPayload diagnostic => $"{diagnostic.Diagnostic.Code}: {diagnostic.Diagnostic.Message}",
            _ => "Incomplete lifecycle evidence was observed."
        };
    }

    private sealed class IncidentFactory
    {
        private readonly EvidenceSnapshotId analysisId;
        private readonly IReadOnlyList<UnitTestActivityEntry> entries;
        private readonly LifecycleObservationId observationId;
        private int nextIncident = 1;

        public IncidentFactory(
            EvidenceSnapshotId analysisId,
            IReadOnlyList<UnitTestActivityEntry> entries,
            LifecycleObservationId observationId)
        {
            this.analysisId = analysisId;
            this.entries = entries;
            this.observationId = observationId;
        }

        public EvidenceAnalysisStatement Fact(string code, string message, UnitTestActivityEntry entry) =>
            new(code, message, [Reference(entry)]);

        public EvidenceAnalysisStatement Conclusion(string code, string message, UnitTestActivityEntry entry) =>
            new(code, message, [Reference(entry)]);

        public EvidenceIncident Create(
            EvidenceIncidentClassification classification,
            EvidenceIncidentSeverity severity,
            EvidenceCompleteness completeness,
            string title,
            string summary,
            UnitTestActivityEntry anchor,
            IReadOnlyList<EvidenceAnalysisStatement> facts,
            IReadOnlyList<EvidenceAnalysisStatement> conclusions,
            IReadOnlyList<EvidenceSuggestedCheck> suggestedChecks,
            IReadOnlyList<EvidenceAffectedOwner>? affectedOwners = null)
        {
            var references = facts.SelectMany(static fact => fact.Evidence)
                .Append(Reference(anchor))
                .Distinct()
                .OrderBy(static reference => reference.ActivitySequence)
                .ToArray();
            var referencedEntries = references
                .Select(reference => entries.FirstOrDefault(entry => entry.Sequence == reference.ActivitySequence))
                .Where(static entry => entry is not null)
                .Cast<UnitTestActivityEntry>()
                .ToArray();
            var lifecycle = CreateLifecycleAnalysis(observationId, entries);

            return new EvidenceIncident(
                new EvidenceIncidentId($"{analysisId.Value}:{nextIncident++}"),
                classification,
                severity,
                completeness,
                EvidenceIncidentScope.Lifecycle,
                title,
                summary,
                referencedEntries.FirstOrDefault()?.ObservedAt ?? anchor.ObservedAt,
                referencedEntries.LastOrDefault()?.ObservedAt,
                null,
                lifecycle,
                affectedOwners ?? [],
                facts,
                conclusions,
                suggestedChecks,
                referencedEntries.Select(CreateTimelineItem).ToArray());
        }

        private static LifecycleEvidenceAnalysis CreateLifecycleAnalysis(
            LifecycleObservationId observationId,
            IReadOnlyList<UnitTestActivityEntry> entries)
        {
            var first = entries[0];
            var last = entries[^1];
            var runControl = entries
                .Where(static entry => entry.Payload is RunControlActivityPayload)
                .Select(static entry => ((RunControlActivityPayload)entry.Payload).Observation)
                .ToArray();
            bool? reachedIdleAndReady = runControl.Length == 0
                ? null
                : runControl.Any(static observation => observation.Ready
                    && !observation.Running
                    && observation.ExecutionStatus == UnitTestExecutionStatus.Idle);
            return new LifecycleEvidenceAnalysis(
                observationId,
                Anchor(first),
                first.ObservedAt,
                last.ObservedAt,
                reachedIdleAndReady);
        }

        private static LifecycleAnchorKind Anchor(UnitTestActivityEntry entry)
        {
            return entry.Payload switch
            {
                SessionActivityPayload { Activity: UnitTestSessionActivityKind.Started } => LifecycleAnchorKind.CommunicationSessionStarted,
                SessionActivityPayload { Activity: UnitTestSessionActivityKind.Ended } => LifecycleAnchorKind.SessionEnded,
                PlcStateActivityPayload => LifecycleAnchorKind.PlcOperatingStateChanged,
                CommandActivityPayload { Command: UnitTestActivityCommandKind.Start } => LifecycleAnchorKind.StartCommand,
                CommandActivityPayload { Command: UnitTestActivityCommandKind.PlcStart } => LifecycleAnchorKind.PlcStartCommand,
                CommandActivityPayload { Command: UnitTestActivityCommandKind.Abort } => LifecycleAnchorKind.AbortCommand,
                CommandActivityPayload { Command: UnitTestActivityCommandKind.Reset } => LifecycleAnchorKind.ResetCommand,
                CommandActivityPayload { Command: UnitTestActivityCommandKind.EventBufferReset } => LifecycleAnchorKind.EventBufferResetCommand,
                RunEpochActivityPayload { Activity: UnitTestRunEpochActivityKind.Started } => LifecycleAnchorKind.RunEpochStarted,
                RunEpochActivityPayload { Activity: UnitTestRunEpochActivityKind.Ended } => LifecycleAnchorKind.RunEpochEnded,
                AccessHealthActivityPayload { Health.State: AccessHealthState.RevalidationRequired } => LifecycleAnchorKind.CommunicationRevalidation,
                AccessHealthActivityPayload { Health.State: AccessHealthState.Healthy } => LifecycleAnchorKind.CommunicationRecovered,
                _ => LifecycleAnchorKind.CommunicationSessionStarted
            };
        }

        private static EvidenceReference Reference(UnitTestActivityEntry entry)
        {
            return entry.Payload is PlcEventActivityPayload plcEvent
                ? new EvidenceReference(entry.Sequence, plcEvent.Event.SequenceId)
                : new EvidenceReference(entry.Sequence);
        }

        private static EvidenceTimelineItem CreateTimelineItem(UnitTestActivityEntry entry)
        {
            return new EvidenceTimelineItem(
                entry.ObservedAt,
                TimelineKind(entry.Payload),
                TimelineMessage(entry.Payload),
                [Reference(entry)]);
        }

        private static EvidenceTimelineKind TimelineKind(UnitTestActivityPayload payload)
        {
            return payload switch
            {
                CommandActivityPayload => EvidenceTimelineKind.CommandTransport,
                RunControlActivityPayload => EvidenceTimelineKind.RunControlObservation,
                EventStreamActivityPayload => EvidenceTimelineKind.EventStreamState,
                PlcEventActivityPayload => EvidenceTimelineKind.PlcEvent,
                AccessHealthActivityPayload => EvidenceTimelineKind.CommunicationHealth,
                DiagnosticActivityPayload => EvidenceTimelineKind.Diagnostic,
                _ => EvidenceTimelineKind.LifecycleAnchor
            };
        }

        private static string TimelineMessage(UnitTestActivityPayload payload)
        {
            return payload switch
            {
                CommandActivityPayload command => $"{command.Command} command {command.Phase}: {command.ResultState}/{command.Accepted}.",
                RunControlActivityPayload runControl => $"RunControl {runControl.Observation.ExecutionStatus}/{runControl.Observation.State}/{runControl.Observation.Result}.",
                EventStreamActivityPayload stream => $"Event stream {stream.State.LastReadState}, lost events {stream.State.LostEventCount}.",
                PlcEventActivityPayload plcEvent => $"{plcEvent.Event.OwnerScope}/{plcEvent.Event.EventType}: {plcEvent.Event.Message}",
                AccessHealthActivityPayload health => $"{health.ServiceKind} access health {health.Health.State}: {health.Health.Message}",
                DiagnosticActivityPayload diagnostic => $"{diagnostic.Diagnostic.Code}: {diagnostic.Diagnostic.Message}",
                PlcStateActivityPayload plc => $"PLC operating state {plc.State.OperatingState}.",
                SessionActivityPayload session => $"Application session {session.Activity}.",
                RunEpochActivityPayload run => $"RunEpoch {run.Activity}: {run.Snapshot.State}/{run.Snapshot.Result}.",
                _ => payload.Kind.ToString()
            };
        }
    }
}
