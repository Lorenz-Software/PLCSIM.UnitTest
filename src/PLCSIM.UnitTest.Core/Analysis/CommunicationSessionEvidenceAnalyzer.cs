using System.Globalization;
using PLCSIM.UnitTest.Common.Analysis;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Services;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Core.Analysis;

/// <summary>
/// Deterministic on-demand analysis of current and historical UnitTest communication health.
/// </summary>
public sealed class CommunicationSessionEvidenceAnalyzer
{
    private static readonly TimeSpan MinimumDeletionStallDuration = TimeSpan.FromSeconds(1);
    private const int MinimumDeletionStallObservations = 3;
    private const int MinimumBacklogDrainAdvances = 4;
    private readonly TimeProvider timeProvider;

    public CommunicationSessionEvidenceAnalyzer(CoreServiceContext coreContext)
        : this(coreContext?.TimeProvider ?? throw new ArgumentNullException(nameof(coreContext)))
    {
    }

    public CommunicationSessionEvidenceAnalyzer(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public EvidenceAnalysisSnapshot Analyze(UnitTestActivityTraceSnapshot trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var analysisId = new EvidenceSnapshotId($"session:{trace.SessionId.Value}:{trace.NewestSequence}");
        var entries = trace.Entries.OrderBy(static entry => entry.Sequence).ToArray();
        var context = SelectCurrentCommunicationContext(entries);
        var progress = AnalyzeEventBufferProgress(context.Entries);
        var episode = SelectLatestHealthEpisode(context.Entries, progress);
        var currentEntries = context.Entries.Where(entry => entry.Sequence >= episode.StartSequence).ToArray();
        var historicalEntries = entries
            .Where(entry => context.Id is not null
                ? entry.CommunicationSessionId != context.Id || entry.Sequence < episode.StartSequence
                : entry.Sequence < episode.StartSequence)
            .ToArray();
        var currentProgress = AnalyzeEventBufferProgress(currentEntries);
        var currentCompleteness = trace.EvictedEntryCount > 0 && episode.StartSequence <= trace.OldestSequence
            ? EvidenceCompleteness.Incomplete
            : EvidenceCompleteness.Complete;
        var factory = new IncidentFactory(analysisId, entries);
        var currentIncidents = new List<EvidenceIncident>();

        AddCommunicationInvalidations(currentEntries, currentCompleteness, factory, currentIncidents);
        AddPendingRevalidations(currentEntries, currentCompleteness, factory, currentIncidents);
        AddCommunicationContractConflicts(currentEntries, factory, currentIncidents);
        AddCurrentEventBufferHealth(currentEntries, currentCompleteness, factory, currentIncidents);
        AddUncorrelatedLifecycleEvidence(currentEntries, ref currentCompleteness, factory, currentIncidents);
        AddIncompleteEvidenceIncident(currentEntries, trace, episode, ref currentCompleteness, factory, currentIncidents);
        AddEventBufferProgressIncident(currentProgress.PrimaryStall, false, currentCompleteness, factory, currentIncidents);
        AddRecoveredBacklogIncident(currentProgress.RecoveredBacklog, currentCompleteness, factory, currentIncidents);

        var historicalIncidents = new List<EvidenceIncident>();
        foreach (var stall in progress.Stalls.Where(stall => stall.Start.Sequence < episode.StartSequence))
        {
            AddEventBufferProgressIncident(stall, true, EvidenceCompleteness.Complete, factory, historicalIncidents);
        }

        AddHistoricalHealthIncident(
            historicalEntries,
            trace.EvictedEntryCount > 0 && currentCompleteness == EvidenceCompleteness.Complete,
            trace.EvictedEntryCount,
            factory,
            historicalIncidents);

        var incidents = currentIncidents.Concat(historicalIncidents).ToArray();
        return new EvidenceAnalysisSnapshot(
            analysisId,
            trace.SessionId,
            EvidenceAnalysisScope.Session,
            null,
            null,
            timeProvider.GetUtcNow(),
            episode.StartSequence,
            trace.NewestSequence,
            currentCompleteness,
            incidents,
            DetermineAssessment(currentCompleteness, currentIncidents),
            CreateSummary(context, currentEntries, currentProgress, currentCompleteness, currentIncidents, historicalIncidents));
    }

    private static CommunicationContext SelectCurrentCommunicationContext(IReadOnlyList<UnitTestActivityEntry> entries)
    {
        var currentId = entries
            .Where(static entry => entry.CommunicationSessionId is not null)
            .Select(static entry => entry.CommunicationSessionId)
            .LastOrDefault();
        return currentId is null
            ? new CommunicationContext(null, entries.ToArray())
            : new CommunicationContext(currentId, entries.Where(entry => entry.CommunicationSessionId == currentId).ToArray());
    }

    private static HealthEpisode SelectLatestHealthEpisode(
        IReadOnlyList<UnitTestActivityEntry> entries,
        EventBufferProgress progress)
    {
        if (entries.Count == 0)
        {
            return new HealthEpisode(0);
        }

        var access = new Dictionary<AccessServiceKind, AccessHealthState>();
        EventStreamState? eventStream = null;
        var previousHealthy = true;
        long? latestHealthyObservation = null;
        long? latestRecovery = null;
        long? currentFaultStart = null;
        long? precedingHealthyBaseline = null;

        foreach (var entry in entries)
        {
            var positive = false;
            switch (entry.Payload)
            {
                case AccessHealthActivityPayload health:
                    access[health.ServiceKind] = health.Health.State;
                    positive = health.Health.State == AccessHealthState.Healthy;
                    break;
                case EventStreamActivityPayload stream:
                    eventStream = stream.State;
                    positive = IsHealthyEventStreamState(stream.State);
                    break;
            }

            var healthy = IsCurrentHealthStateHealthy(access, eventStream);
            if (healthy && positive)
            {
                latestHealthyObservation = entry.Sequence;
            }

            if (previousHealthy && !healthy)
            {
                currentFaultStart = entry.Sequence;
                precedingHealthyBaseline = latestHealthyObservation;
            }
            else if (!previousHealthy && healthy)
            {
                latestRecovery = entry.Sequence;
                currentFaultStart = null;
            }

            previousHealthy = healthy;
        }

        var unresolvedStall = progress.Stalls.LastOrDefault(static stall => stall.CatchUp is null);
        if (unresolvedStall is not null)
        {
            currentFaultStart = currentFaultStart is null
                ? unresolvedStall.Start.Sequence
                : Math.Min(currentFaultStart.Value, unresolvedStall.Start.Sequence);
            precedingHealthyBaseline = entries
                .Where(entry => entry.Sequence < currentFaultStart.Value && IsPositiveHealthObservation(entry))
                .Select(static entry => (long?)entry.Sequence)
                .LastOrDefault();
        }

        if (currentFaultStart is not null)
        {
            return new HealthEpisode(precedingHealthyBaseline ?? entries[0].Sequence);
        }

        var latestRecoveredStall = progress.Stalls
            .Where(static stall => stall.CatchUp is not null)
            .Select(static stall => (long?)stall.CatchUp!.Sequence)
            .LastOrDefault();
        var recoveryStart = new[] { latestRecovery, latestRecoveredStall }
            .Where(static value => value is not null)
            .Select(static value => value!.Value)
            .DefaultIfEmpty(entries[0].Sequence)
            .Max();
        return new HealthEpisode(recoveryStart);
    }

    private static bool IsCurrentHealthStateHealthy(
        IReadOnlyDictionary<AccessServiceKind, AccessHealthState> access,
        EventStreamState? eventStream) =>
        access.Values.All(static state => state is AccessHealthState.Healthy or AccessHealthState.Disposed)
        && (eventStream is null || IsHealthyEventStreamState(eventStream));

    private static bool IsPositiveHealthObservation(UnitTestActivityEntry entry) => entry.Payload switch
    {
        AccessHealthActivityPayload { Health.State: AccessHealthState.Healthy } => true,
        EventStreamActivityPayload stream when IsHealthyEventStreamState(stream.State) => true,
        _ => false
    };

    private static bool IsHealthyEventStreamState(EventStreamState state) =>
        state.LostEventCount == 0
        && !state.BufferFull
        && state.LastReadState is EventBufferReadState.Valid or EventBufferReadState.Empty
        && state.BatchReleaseState is not EventStreamBatchReleaseState.Rejected
            and not EventStreamBatchReleaseState.Failed
            and not EventStreamBatchReleaseState.Invalidated;

    private static void AddCommunicationInvalidations(
        IReadOnlyList<UnitTestActivityEntry> entries,
        EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var unresolved = entries
            .Where(IsCommunicationInvalidation)
            .Where(invalidation => !HasLaterAccessRecovery(entries, invalidation))
            .ToArray();
        if (unresolved.Length == 0)
        {
            return;
        }

        var last = unresolved[^1];
        incidents.Add(factory.Create(
            EvidenceIncidentClassification.CommunicationFailure,
            EvidenceIncidentSeverity.Error,
            completeness,
            "UnitTest communication invalidated",
            "At least one access service reported a terminal communication invalidation in the current health episode.",
            last,
            unresolved.Select(entry => factory.Fact("communication-invalidated", $"{LayerName(entry)}: {CommunicationMessage(entry)}", entry)).ToArray(),
            [factory.Conclusion("communication-unusable", "The affected access layer remains unusable at the analysis snapshot.", last)],
            [new EvidenceSuggestedCheck("reconnect-runtime", "Inspect the invalidation reason and reconnect runtime access after the external PLC or download operation has completed.")]));
    }

    private static bool HasLaterAccessRecovery(
        IReadOnlyList<UnitTestActivityEntry> entries,
        UnitTestActivityEntry invalidation)
    {
        if (invalidation.Payload is not AccessHealthActivityPayload invalidated)
        {
            return false;
        }

        return entries.Any(entry => entry.Sequence > invalidation.Sequence
            && entry.Payload is AccessHealthActivityPayload recovered
            && recovered.ServiceKind == invalidated.ServiceKind
            && recovered.Health.State == AccessHealthState.Healthy);
    }

    private static void AddPendingRevalidations(
        IReadOnlyList<UnitTestActivityEntry> entries,
        EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var pending = entries
            .Where(static entry => entry.Payload is AccessHealthActivityPayload)
            .GroupBy(static entry => ((AccessHealthActivityPayload)entry.Payload).ServiceKind)
            .Select(static group => group.OrderBy(entry => entry.Sequence).Last())
            .Where(static entry => ((AccessHealthActivityPayload)entry.Payload).Health.State == AccessHealthState.RevalidationRequired)
            .ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        var last = pending[^1];
        incidents.Add(factory.Create(
            EvidenceIncidentClassification.CommunicationFailure,
            EvidenceIncidentSeverity.Warning,
            completeness,
            "UnitTest communication revalidation remains pending",
            "At least one access layer has not reported recovery after requesting revalidation.",
            last,
            pending.Select(entry => factory.Fact("communication-revalidation-pending", $"{LayerName(entry)}: {CommunicationMessage(entry)}", entry)).ToArray(),
            [factory.Conclusion("communication-revalidation-open", "The current health episode ends while communication revalidation is still required.", last)],
            [new EvidenceSuggestedCheck("complete-revalidation", "Complete the external PLC or download operation and verify that communication reports recovery.")]));
    }

    private static void AddCommunicationContractConflicts(
        IReadOnlyList<UnitTestActivityEntry> entries,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var conflicts = entries.Where(static entry =>
            entry.Payload is EventStreamActivityPayload { State.LastReadState: EventBufferReadState.ContractInvalid }).ToArray();
        if (conflicts.Length == 0)
        {
            return;
        }

        var last = conflicts[^1];
        incidents.Add(factory.Create(
            EvidenceIncidentClassification.ContractConflict,
            EvidenceIncidentSeverity.Error,
            EvidenceCompleteness.Incomplete,
            "EventStream communication contract invalid",
            "The EventStream/EventBuffer layer does not satisfy the communication contract.",
            last,
            conflicts.Select(entry => factory.Fact("event-stream-contract-invalid", EvidenceFailureMessage(entry), entry)).ToArray(),
            [factory.Conclusion("event-stream-contract-unusable", "Event evidence cannot be interpreted while the EventBuffer contract is invalid.", last)],
            [new EvidenceSuggestedCheck("inspect-event-stream-contract", "Inspect the configured EventBuffer symbols and the PLC communication-contract implementation.")]));
    }

    private static void AddUncorrelatedLifecycleEvidence(
        IReadOnlyList<UnitTestActivityEntry> entries,
        ref EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var uncorrelated = entries.Where(static entry =>
            entry.LifecycleObservationId is null
            && entry.Payload is DiagnosticActivityPayload diagnostic
            && diagnostic.Diagnostic.Correlation.LifecycleObservationId is null
            && diagnostic.Diagnostic.Code == "Workflow.RunControlObservation.ContradictorySignals").ToArray();
        if (uncorrelated.Length == 0)
        {
            return;
        }

        completeness = EvidenceCompleteness.Incomplete;
        var last = uncorrelated[^1];
        incidents.Add(factory.Create(
            EvidenceIncidentClassification.EvidenceIncomplete,
            EvidenceIncidentSeverity.Warning,
            EvidenceCompleteness.Incomplete,
            "PLC lifecycle communication lacks correlation",
            "The lifecycle-communication layer contains a RunControl conflict that cannot be assigned to a concrete PLC lifecycle observation.",
            last,
            uncorrelated.Select(entry => factory.Fact("lifecycle-correlation-missing", $"{((DiagnosticActivityPayload)entry.Payload).Diagnostic.Code}: {((DiagnosticActivityPayload)entry.Payload).Diagnostic.Message}", entry)).ToArray(),
            [factory.Conclusion("lifecycle-analysis-focus-unavailable", "The conflicting RunControl observation cannot be evaluated in a focused PLC Lifecycle analysis without its lifecycle correlation.", last)],
            [new EvidenceSuggestedCheck("inspect-trace-correlation", "Inspect lifecycle observation creation and diagnostic correlation for the referenced RunControl observation.")]));
    }

    private static void AddCurrentEventBufferHealth(
        IReadOnlyList<UnitTestActivityEntry> entries,
        EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var latest = entries.LastOrDefault(static entry => entry.Payload is EventStreamActivityPayload);
        if (latest?.Payload is not EventStreamActivityPayload stream)
        {
            return;
        }

        if (stream.State.BufferFull)
        {
            incidents.Add(factory.Create(
                EvidenceIncidentClassification.EventBufferCapacity,
                EvidenceIncidentSeverity.Warning,
                completeness,
                "EventBuffer is full",
                "The EventStream/EventBuffer layer reports exhausted buffer capacity while the other communication layers may remain usable.",
                latest,
                [factory.Fact(
                    "event-buffer-full",
                    $"BufferFull is true at producer {stream.State.ProducerSequenceId}, deleted {stream.State.DeletedSequenceId}, backlog {Backlog(stream.State)}, and lost-event count {stream.State.LostEventCount}.",
                    latest)],
                [factory.Conclusion("event-buffer-capacity-degraded", "EventBuffer health is degraded; this does not prove loss of Runtime access or UnitTest binding.", latest)],
                [new EvidenceSuggestedCheck("inspect-event-production-and-drain", "Inspect EventBuffer production load and verify cyclic EventBuffer request processing.")]));
        }

        if (stream.State.BatchReleaseState is EventStreamBatchReleaseState.Rejected
            or EventStreamBatchReleaseState.Failed
            or EventStreamBatchReleaseState.Invalidated)
        {
            incidents.Add(factory.Create(
                EvidenceIncidentClassification.EventBufferProgress,
                EvidenceIncidentSeverity.Warning,
                completeness,
                "PC batch release is not progressing",
                "The latest EventStream observation reports an unsuccessful PC-side batch release state.",
                latest,
                [factory.Fact("event-buffer-release-failed", $"The latest batch release state is {stream.State.BatchReleaseState}.", latest)],
                [factory.Conclusion("event-buffer-release-unresolved", "The snapshot reports an unsuccessful PC batch release; it does not establish the PLC deletion acknowledgement state.", latest)],
                [new EvidenceSuggestedCheck("inspect-event-buffer-release", "Inspect the accompanying diagnostics for the cause of the PC batch release failure.")]));
        }
    }

    private static void AddIncompleteEvidenceIncident(
        IReadOnlyList<UnitTestActivityEntry> entries,
        UnitTestActivityTraceSnapshot trace,
        HealthEpisode episode,
        ref EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var failures = CompactEvidenceFailures(entries.Where(IsEvidenceFailure)).ToArray();
        var prefixEvicted = trace.EvictedEntryCount > 0 && episode.StartSequence <= trace.OldestSequence;
        if (failures.Length == 0 && !prefixEvicted)
        {
            return;
        }

        completeness = EvidenceCompleteness.Incomplete;
        var anchor = failures.LastOrDefault() ?? entries.LastOrDefault();
        if (anchor is null)
        {
            return;
        }

        var facts = failures.Select(entry => factory.Fact("communication-evidence-incomplete", EvidenceFailureMessage(entry), entry)).ToList();
        if (prefixEvicted)
        {
            facts.Add(factory.Fact(
                "trace-prefix-evicted",
                $"The bounded activity trace evicted {trace.EvictedEntryCount} earlier entry or entries before a complete current health baseline was retained.",
                anchor));
        }

        incidents.Add(factory.Create(
            EvidenceIncidentClassification.EvidenceIncomplete,
            EvidenceIncidentSeverity.Warning,
            EvidenceCompleteness.Incomplete,
            "Current communication evidence incomplete",
            "The latest health episode contains a sequence gap, lost PLC events, an invalid EventStream read state, or lacks a retained healthy baseline.",
            anchor,
            facts,
            [factory.Conclusion("communication-analysis-partial", "Some conclusions for the latest health episode may be unavailable because its evidence is incomplete.", anchor)],
            [new EvidenceSuggestedCheck("inspect-event-stream-health", "Inspect lost-event count, EventBuffer state, communication health, and trace retention before relying on absent evidence.")]));
    }

    private static void AddEventBufferProgressIncident(
        EventBufferDeletionStall? stall,
        bool historical,
        EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        if (stall is null)
        {
            return;
        }

        var facts = new List<EvidenceAnalysisStatement>
        {
            factory.Fact(
                "event-buffer-deletion-stall",
                $"Across {stall.ObservationCount} consecutive EventStream observations, DeletedSequenceId stayed at {stall.DeletedSequenceId} while ProducerSequenceId advanced from {stall.StartProducerSequenceId} to {stall.EndProducerSequenceId} over {FormatDuration(stall.End.ObservedAt - stall.Start.ObservedAt)} (PC observation time).",
                stall.Start,
                stall.End),
            factory.Fact(
                "event-buffer-maximum-backlog",
                $"The maximum observed producer/deleted backlog during this interval was {stall.MaximumBacklog} event(s).",
                stall.MaximumBacklogEntry)
        };
        var conclusions = new List<EvidenceAnalysisStatement>();
        if (stall.CatchUp?.Payload is EventStreamActivityPayload catchUp)
        {
            facts.Add(factory.Fact(
                "event-buffer-catch-up",
                $"Deletion subsequently advanced to {catchUp.State.DeletedSequenceId} with producer {catchUp.State.ProducerSequenceId}; buffer empty was {catchUp.State.BufferObservedEmpty}.",
                stall.CatchUp));
            conclusions.Add(factory.Conclusion("event-buffer-progress-recovered", "The trace proves that EventBuffer deletion stalled and later resumed; it does not prove the PLC-internal cause.", stall.CatchUp));
        }
        else
        {
            conclusions.Add(factory.Conclusion("event-buffer-progress-stalled", "The immutable snapshot ends without observed deletion catch-up; the trace does not prove the PLC-internal cause.", stall.End));
        }

        incidents.Add(factory.Create(
            EvidenceIncidentClassification.EventBufferProgress,
            historical ? EvidenceIncidentSeverity.Information : EvidenceIncidentSeverity.Warning,
            completeness,
            historical ? "Earlier EventBuffer deletion stall recovered" : "EventBuffer deletion progress stalled",
            historical
                ? "An earlier health episode contains a prolonged producer/deleted divergence that later caught up."
                : "The current health episode contains a prolonged producer/deleted divergence without observed catch-up.",
            stall.End,
            facts,
            conclusions,
            [new EvidenceSuggestedCheck("verify-cyclic-event-buffer-requests", "Verify that the PLC program invokes EventBuffer request processing cyclically while events are produced, including while RunControl is Running.")]));
    }

    private static void AddRecoveredBacklogIncident(
        RecoveredEventBufferBacklog? backlog,
        EvidenceCompleteness completeness,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        if (backlog is null)
        {
            return;
        }

        incidents.Add(factory.Create(
            EvidenceIncidentClassification.EventBufferProgress,
            EvidenceIncidentSeverity.Warning,
            completeness,
            "Large EventBuffer backlog observed and drained",
            "A temporary EventBuffer backlog was observed and subsequently drained without producer-side event loss.",
            backlog.Recovered,
            [
                factory.Fact(
                    "event-buffer-temporary-backlog",
                    $"The maximum observed producer/deleted backlog was {backlog.MaximumBacklog} event(s) at producer {backlog.ProducerSequenceId}, deleted {backlog.DeletedSequenceId}.",
                    backlog.Maximum),
                factory.Fact(
                    "event-buffer-backlog-drained",
                    $"Deletion subsequently caught up at producer {backlog.RecoveredProducerSequenceId}, deleted {backlog.RecoveredDeletedSequenceId}; the EventBuffer was observed empty after {backlog.ObservationCount} progress observations.",
                    backlog.Recovered)
            ],
            [factory.Conclusion("event-buffer-backlog-recovered", "The trace proves a large temporary backlog and later recovery; it does not prove the PLC-internal cause.", backlog.Recovered)],
            [new EvidenceSuggestedCheck("verify-cyclic-event-buffer-requests", "Verify that PLC-side EventBuffer request processing executes cyclically without conditional interruption.")]));
    }

    private static void AddHistoricalHealthIncident(
        IReadOnlyList<UnitTestActivityEntry> history,
        bool prefixEvicted,
        long evictedEntryCount,
        IncidentFactory factory,
        List<EvidenceIncident> incidents)
    {
        var adverse = history.Where(entry => IsCommunicationInvalidation(entry)
            || IsEvidenceFailure(entry)
            || entry.Payload is AccessHealthActivityPayload { Health.State: AccessHealthState.RevalidationRequired }).ToArray();
        if (adverse.Length == 0 && !prefixEvicted)
        {
            return;
        }

        var anchor = adverse.LastOrDefault() ?? history.LastOrDefault();
        if (anchor is null)
        {
            return;
        }

        var facts = adverse
            .GroupBy(HistoricalFactCode)
            .Select(static group => group.Last())
            .Select(entry => factory.Fact(HistoricalFactCode(entry), HistoricalMessage(entry), entry))
            .ToList();
        if (prefixEvicted)
        {
            facts.Add(factory.Fact("historical-trace-prefix-evicted", $"The bounded trace evicted {evictedEntryCount} entry or entries before the retained latest health episode.", anchor));
        }

        incidents.Add(factory.Create(
            EvidenceIncidentClassification.CommunicationFailure,
            EvidenceIncidentSeverity.Information,
            EvidenceCompleteness.Incomplete,
            "Earlier communication-health history retained",
            "Earlier health episodes contain adverse or incomplete evidence. They are retained as history and do not affect the current assessment.",
            anchor,
            facts,
            [factory.Conclusion("historical-health-non-status-affecting", "These observations precede the latest healthy baseline or belong to an earlier communication context.", anchor)],
            []));
    }

    private static string HistoricalFactCode(UnitTestActivityEntry entry) => entry.Payload switch
    {
        AccessHealthActivityPayload { Health.State: AccessHealthState.Invalidated } => "historical-communication-invalidation",
        AccessHealthActivityPayload { Health.State: AccessHealthState.RevalidationRequired } => "historical-communication-revalidation",
        EventStreamActivityPayload => "historical-event-stream-evidence",
        DiagnosticActivityPayload diagnostic => $"historical-{diagnostic.Diagnostic.Code}",
        _ => "historical-communication-evidence"
    };

    private static string HistoricalMessage(UnitTestActivityEntry entry) => entry.Payload switch
    {
        AccessHealthActivityPayload => CommunicationMessage(entry),
        _ => EvidenceFailureMessage(entry)
    };

    private static EventBufferProgress AnalyzeEventBufferProgress(IReadOnlyList<UnitTestActivityEntry> entries)
    {
        var observations = entries.Where(static entry => entry.Payload is EventStreamActivityPayload).ToArray();
        if (observations.Length == 0)
        {
            return EventBufferProgress.Empty;
        }

        var stalls = new List<EventBufferDeletionStall>();
        var resetCount = 0;
        var maximumBacklog = 0u;
        var maximumBacklogEntry = observations[0];
        var segmentStart = observations[0];
        var segmentEnd = observations[0];
        var segmentStartState = ((EventStreamActivityPayload)segmentStart.Payload).State;
        var segmentDeleted = segmentStartState.DeletedSequenceId;
        var segmentMaxBacklog = Backlog(segmentStartState);
        var segmentMaxBacklogEntry = segmentStart;
        var segmentObservationCount = 1;
        var previous = observations[0];
        UpdateMaximum(segmentStart, ref maximumBacklog, ref maximumBacklogEntry);

        for (var index = 1; index < observations.Length; index++)
        {
            var current = observations[index];
            var previousState = ((EventStreamActivityPayload)previous.Payload).State;
            var currentState = ((EventStreamActivityPayload)current.Payload).State;
            UpdateMaximum(current, ref maximumBacklog, ref maximumBacklogEntry);

            if (currentState.ProducerSequenceId < previousState.ProducerSequenceId
                || currentState.DeletedSequenceId < previousState.DeletedSequenceId)
            {
                CompleteSegment(current, stalls, segmentStart, segmentEnd, segmentDeleted, segmentStartState.ProducerSequenceId, segmentMaxBacklog, segmentMaxBacklogEntry, segmentObservationCount);
                resetCount++;
                segmentStart = current;
                segmentEnd = current;
                segmentStartState = currentState;
                segmentDeleted = currentState.DeletedSequenceId;
                segmentMaxBacklog = Backlog(currentState);
                segmentMaxBacklogEntry = current;
                segmentObservationCount = 1;
                previous = current;
                continue;
            }

            if (currentState.DeletedSequenceId != segmentDeleted || currentState.BufferObservedEmpty)
            {
                CompleteSegment(current, stalls, segmentStart, segmentEnd, segmentDeleted, segmentStartState.ProducerSequenceId, segmentMaxBacklog, segmentMaxBacklogEntry, segmentObservationCount);
                segmentStart = current;
                segmentStartState = currentState;
                segmentDeleted = currentState.DeletedSequenceId;
                segmentMaxBacklog = Backlog(currentState);
                segmentMaxBacklogEntry = current;
                segmentObservationCount = 1;
            }
            else
            {
                segmentObservationCount++;
            }

            segmentEnd = current;
            var backlog = Backlog(currentState);
            if (backlog > segmentMaxBacklog)
            {
                segmentMaxBacklog = backlog;
                segmentMaxBacklogEntry = current;
            }

            previous = current;
        }

        CompleteSegment(null, stalls, segmentStart, segmentEnd, segmentDeleted, segmentStartState.ProducerSequenceId, segmentMaxBacklog, segmentMaxBacklogEntry, segmentObservationCount);
        return new EventBufferProgress(
            ((EventStreamActivityPayload)observations[^1].Payload).State,
            maximumBacklog,
            maximumBacklogEntry,
            resetCount,
            stalls,
            FindRecoveredBacklog(observations));
    }

    private static RecoveredEventBufferBacklog? FindRecoveredBacklog(IReadOnlyList<UnitTestActivityEntry> observations)
    {
        var candidates = observations
            .Select((entry, index) => new
            {
                Entry = entry,
                Index = index,
                State = ((EventStreamActivityPayload)entry.Payload).State
            })
            .Where(candidate => Backlog(candidate.State) > 0
                && candidate.State.LostEventCount == 0
                && !candidate.State.BufferFull)
            .OrderByDescending(candidate => Backlog(candidate.State));

        foreach (var candidate in candidates)
        {
            var observationCount = 1;
            var deletionAdvanceCount = 0;
            var previousState = candidate.State;
            for (var index = candidate.Index + 1; index < observations.Count; index++)
            {
                var current = observations[index];
                var currentState = ((EventStreamActivityPayload)current.Payload).State;
                if (currentState.ProducerSequenceId < previousState.ProducerSequenceId
                    || currentState.DeletedSequenceId < previousState.DeletedSequenceId
                    || currentState.LostEventCount > 0
                    || currentState.BufferFull)
                {
                    break;
                }

                observationCount++;
                if (currentState.DeletedSequenceId > previousState.DeletedSequenceId)
                {
                    deletionAdvanceCount++;
                }

                if (deletionAdvanceCount >= MinimumBacklogDrainAdvances
                    && currentState.BufferObservedEmpty
                    && Backlog(currentState) == 0)
                {
                    return new RecoveredEventBufferBacklog(
                        candidate.Entry,
                        current,
                        Backlog(candidate.State),
                        candidate.State.ProducerSequenceId,
                        candidate.State.DeletedSequenceId,
                        currentState.ProducerSequenceId,
                        currentState.DeletedSequenceId,
                        observationCount);
                }

                previousState = currentState;
            }
        }

        return null;
    }

    private static void CompleteSegment(
        UnitTestActivityEntry? catchUp,
        ICollection<EventBufferDeletionStall> stalls,
        UnitTestActivityEntry start,
        UnitTestActivityEntry end,
        uint deletedSequenceId,
        uint startProducerSequenceId,
        uint maximumBacklog,
        UnitTestActivityEntry maximumBacklogEntry,
        int observationCount)
    {
        var endState = ((EventStreamActivityPayload)end.Payload).State;
        if (endState.ProducerSequenceId <= startProducerSequenceId
            || end.ObservedAt - start.ObservedAt < MinimumDeletionStallDuration
            || observationCount < MinimumDeletionStallObservations)
        {
            return;
        }

        stalls.Add(new EventBufferDeletionStall(
            start,
            end,
            catchUp,
            deletedSequenceId,
            startProducerSequenceId,
            endState.ProducerSequenceId,
            maximumBacklog,
            maximumBacklogEntry,
            observationCount));
    }

    private static IEnumerable<UnitTestActivityEntry> CompactEvidenceFailures(
        IEnumerable<UnitTestActivityEntry> failures)
    {
        var entries = failures.OrderBy(static entry => entry.Sequence).ToArray();
        var retained = new SortedDictionary<long, UnitTestActivityEntry>();
        foreach (var entry in entries.Where(static entry => !IsRepeatedLostEventEvidence(entry)))
        {
            retained[entry.Sequence] = entry;
        }

        foreach (var group in entries
            .Where(IsRepeatedLostEventEvidence)
            .GroupBy(LostEventEvidenceKey))
        {
            var first = group.First();
            var last = group.Last();
            retained[first.Sequence] = first;
            retained[last.Sequence] = last;
        }

        return retained.Values;
    }

    private static bool IsRepeatedLostEventEvidence(UnitTestActivityEntry entry) =>
        entry.Payload is EventStreamActivityPayload { State.LostEventCount: > 0 }
        || entry.Payload is DiagnosticActivityPayload
        {
            Diagnostic.Code: "Workflow.EventStreamProcessor.LostEvents"
        };

    private static string LostEventEvidenceKey(UnitTestActivityEntry entry) => entry.Payload switch
    {
        EventStreamActivityPayload stream => $"event-stream:{stream.State.LostEventCount}:{stream.State.LastReadState}",
        DiagnosticActivityPayload diagnostic => $"diagnostic:{diagnostic.Diagnostic.Code}",
        _ => entry.Sequence.ToString(CultureInfo.InvariantCulture)
    };

    private static void UpdateMaximum(
        UnitTestActivityEntry entry,
        ref uint maximumBacklog,
        ref UnitTestActivityEntry maximumBacklogEntry)
    {
        var backlog = Backlog(((EventStreamActivityPayload)entry.Payload).State);
        if (backlog > maximumBacklog)
        {
            maximumBacklog = backlog;
            maximumBacklogEntry = entry;
        }
    }

    private static uint Backlog(EventStreamState state) => state.ProducerSequenceId >= state.DeletedSequenceId
        ? state.ProducerSequenceId - state.DeletedSequenceId
        : 0;

    private static bool IsCommunicationInvalidation(UnitTestActivityEntry entry) =>
        entry.Payload is AccessHealthActivityPayload { Health.State: AccessHealthState.Invalidated }
        || entry.Payload is DiagnosticActivityPayload diagnostic
            && diagnostic.Diagnostic.Code.StartsWith("Workflow.AccessHealth.", StringComparison.Ordinal)
            && diagnostic.Diagnostic.Code.EndsWith(".Invalidated", StringComparison.Ordinal);

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

    private static string LayerName(UnitTestActivityEntry entry) => entry.Payload is AccessHealthActivityPayload health
        ? LayerFor(health.ServiceKind)
        : "Communication health";

    private static string LayerFor(AccessServiceKind serviceKind) => serviceKind switch
    {
        AccessServiceKind.Profile or AccessServiceKind.Project or AccessServiceKind.PlcControl => "Runtime access",
        AccessServiceKind.UnitTestLifecycle or AccessServiceKind.UnitTestEventStream => "UnitTest binding",
        _ => "Communication health"
    };

    private static string CommunicationMessage(UnitTestActivityEntry entry) => entry.Payload switch
    {
        AccessHealthActivityPayload health => $"{health.ServiceKind} access health changed to {health.Health.State}: {health.Health.Message}",
        DiagnosticActivityPayload diagnostic => $"{diagnostic.Diagnostic.Code}: {diagnostic.Diagnostic.Message}",
        _ => "Communication state change was observed."
    };

    private static string EvidenceFailureMessage(UnitTestActivityEntry entry) => entry.Payload switch
    {
        EventStreamActivityPayload stream => $"Event stream state is {stream.State.LastReadState} with {stream.State.LostEventCount} lost event(s).",
        DiagnosticActivityPayload diagnostic => $"{diagnostic.Diagnostic.Code}: {diagnostic.Diagnostic.Message}",
        _ => "Incomplete communication evidence was observed."
    };

    private static EvidenceAnalysisAssessment DetermineAssessment(
        EvidenceCompleteness completeness,
        IReadOnlyList<EvidenceIncident> currentIncidents)
    {
        if (currentIncidents.Any(static incident => incident.Severity == EvidenceIncidentSeverity.Error))
        {
            return EvidenceAnalysisAssessment.Error;
        }

        if (currentIncidents.Any(static incident => incident.Severity == EvidenceIncidentSeverity.Warning))
        {
            return EvidenceAnalysisAssessment.Warning;
        }

        return completeness == EvidenceCompleteness.Complete
            ? EvidenceAnalysisAssessment.Healthy
            : EvidenceAnalysisAssessment.Inconclusive;
    }

    private static string CreateSummary(
        CommunicationContext context,
        IReadOnlyList<UnitTestActivityEntry> currentEntries,
        EventBufferProgress progress,
        EvidenceCompleteness completeness,
        IReadOnlyList<EvidenceIncident> currentIncidents,
        IReadOnlyList<EvidenceIncident> historicalIncidents)
    {
        var runtime = AccessLayerStatus(context.Entries, static kind => kind is AccessServiceKind.Profile or AccessServiceKind.Project or AccessServiceKind.PlcControl);
        var binding = AccessLayerStatus(context.Entries, static kind => kind is AccessServiceKind.UnitTestLifecycle or AccessServiceKind.UnitTestEventStream);
        var lifecycle = currentEntries.Any(static entry => entry.Payload is DiagnosticActivityPayload diagnostic
            && diagnostic.Diagnostic.Code == "Workflow.RunControlObservation.ContradictorySignals")
                ? "Conflict observed"
                : "No communication conflict observed";
        var stream = progress.LatestState is null
            ? "Not observed"
            : $"{progress.LatestState.LastReadState}, monitoring {progress.LatestState.IsMonitoring}, buffer full {progress.LatestState.BufferFull}";
        var integrity = currentEntries.Any(IsEvidenceFailure) ? "Incomplete" : completeness.ToString();
        var progressSummary = progress.LatestState is null
            ? "No EventBuffer progress observation retained"
            : $"producer {progress.LatestState.ProducerSequenceId}, deleted {progress.LatestState.DeletedSequenceId}, current backlog {Backlog(progress.LatestState)}, maximum backlog {progress.MaximumBacklog}, counter resets {progress.ResetCount}";

        return $"Communication Health analysis evaluated the latest health episode for context '{context.Id?.Value ?? "not correlated"}' across {currentEntries.Count} retained activity entry or entries. "
            + $"Runtime access: {runtime}. UnitTest binding: {binding}. Lifecycle communication: {lifecycle}. "
            + $"EventStream/EventBuffer: {stream}. Evidence integrity: {integrity}. EventBuffer progress: {progressSummary}. "
            + $"The current episode produced {currentIncidents.Count} status-affecting finding(s); {historicalIncidents.Count} earlier finding(s) are retained as non-status-affecting history.";
    }

    private static string AccessLayerStatus(
        IReadOnlyList<UnitTestActivityEntry> entries,
        Func<AccessServiceKind, bool> include)
    {
        var latest = entries
            .Where(static entry => entry.Payload is AccessHealthActivityPayload)
            .Select(static entry => (AccessHealthActivityPayload)entry.Payload)
            .Where(payload => include(payload.ServiceKind))
            .GroupBy(static payload => payload.ServiceKind)
            .Select(static group => group.Last().Health.State)
            .ToArray();
        if (latest.Length == 0)
        {
            return "Not observed";
        }

        if (latest.Any(static state => state == AccessHealthState.Invalidated))
        {
            return "Invalidated";
        }

        if (latest.Any(static state => state == AccessHealthState.RevalidationRequired))
        {
            return "Revalidation required";
        }

        if (latest.Any(static state => state == AccessHealthState.NotEstablished))
        {
            return "Not established";
        }

        return latest.All(static state => state == AccessHealthState.Disposed) ? "Disposed" : "Healthy";
    }

    private static string FormatDuration(TimeSpan duration) =>
        $"{duration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms";

    private sealed record CommunicationContext(CommunicationSessionId? Id, IReadOnlyList<UnitTestActivityEntry> Entries);
    private sealed record HealthEpisode(long StartSequence);

    private sealed record EventBufferProgress(
        EventStreamState? LatestState,
        uint MaximumBacklog,
        UnitTestActivityEntry? MaximumBacklogEntry,
        int ResetCount,
        IReadOnlyList<EventBufferDeletionStall> Stalls,
        RecoveredEventBufferBacklog? RecoveredBacklog)
    {
        public static EventBufferProgress Empty { get; } = new(null, 0, null, 0, [], null);

        public EventBufferDeletionStall? PrimaryStall => Stalls
            .OrderByDescending(static stall => stall.MaximumBacklog)
            .ThenByDescending(static stall => stall.ObservationCount)
            .ThenByDescending(static stall => stall.End.ObservedAt - stall.Start.ObservedAt)
            .FirstOrDefault();
    }

    private sealed record RecoveredEventBufferBacklog(
        UnitTestActivityEntry Maximum,
        UnitTestActivityEntry Recovered,
        uint MaximumBacklog,
        uint ProducerSequenceId,
        uint DeletedSequenceId,
        uint RecoveredProducerSequenceId,
        uint RecoveredDeletedSequenceId,
        int ObservationCount);

    private sealed record EventBufferDeletionStall(
        UnitTestActivityEntry Start,
        UnitTestActivityEntry End,
        UnitTestActivityEntry? CatchUp,
        uint DeletedSequenceId,
        uint StartProducerSequenceId,
        uint EndProducerSequenceId,
        uint MaximumBacklog,
        UnitTestActivityEntry MaximumBacklogEntry,
        int ObservationCount);

    private sealed class IncidentFactory
    {
        private readonly EvidenceSnapshotId analysisId;
        private readonly IReadOnlyList<UnitTestActivityEntry> entries;
        private int nextIncident = 1;

        public IncidentFactory(EvidenceSnapshotId analysisId, IReadOnlyList<UnitTestActivityEntry> entries)
        {
            this.analysisId = analysisId;
            this.entries = entries;
        }

        public EvidenceAnalysisStatement Fact(string code, string message, params UnitTestActivityEntry[] entries) =>
            new(code, message, entries.Select(Reference).Distinct().ToArray());

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
            IReadOnlyList<EvidenceSuggestedCheck> suggestedChecks)
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

            return new EvidenceIncident(
                new EvidenceIncidentId($"{analysisId.Value}:{nextIncident++}"),
                classification,
                severity,
                completeness,
                EvidenceIncidentScope.Session,
                title,
                summary,
                referencedEntries.FirstOrDefault()?.ObservedAt ?? anchor.ObservedAt,
                referencedEntries.LastOrDefault()?.ObservedAt,
                null,
                null,
                [],
                facts,
                conclusions,
                suggestedChecks,
                referencedEntries.Select(CreateTimelineItem).ToArray());
        }

        private static EvidenceReference Reference(UnitTestActivityEntry entry) => entry.Payload is PlcEventActivityPayload plcEvent
            ? new EvidenceReference(entry.Sequence, plcEvent.Event.SequenceId)
            : new EvidenceReference(entry.Sequence);

        private static EvidenceTimelineItem CreateTimelineItem(UnitTestActivityEntry entry) =>
            new(entry.ObservedAt, TimelineKind(entry.Payload), TimelineMessage(entry.Payload), [Reference(entry)]);

        private static EvidenceTimelineKind TimelineKind(UnitTestActivityPayload payload) => payload switch
        {
            EventStreamActivityPayload => EvidenceTimelineKind.EventStreamState,
            AccessHealthActivityPayload => EvidenceTimelineKind.CommunicationHealth,
            DiagnosticActivityPayload => EvidenceTimelineKind.Diagnostic,
            _ => EvidenceTimelineKind.LifecycleAnchor
        };

        private static string TimelineMessage(UnitTestActivityPayload payload) => payload switch
        {
            EventStreamActivityPayload stream => $"Event stream {stream.State.LastReadState}, producer {stream.State.ProducerSequenceId}, deleted {stream.State.DeletedSequenceId}, lost events {stream.State.LostEventCount}.",
            AccessHealthActivityPayload health => $"{health.ServiceKind} access health {health.Health.State}: {health.Health.Message}",
            DiagnosticActivityPayload diagnostic => $"{diagnostic.Diagnostic.Code}: {diagnostic.Diagnostic.Message}",
            SessionActivityPayload session => $"Application session {session.Activity}.",
            _ => payload.Kind.ToString()
        };
    }
}
