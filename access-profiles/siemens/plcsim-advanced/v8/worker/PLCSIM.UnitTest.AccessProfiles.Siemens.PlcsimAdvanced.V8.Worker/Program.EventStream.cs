using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker
{
    internal static partial class Program
    {
        private sealed partial class RuntimeApiContext
        {
            private const int EventSignalBatchSize = 32;
            private PlcsimEventStreamMonitoringState? eventStreamObservation;

            private PlcsimEventStreamMonitoringState GetEventStreamObservation(string sessionId)
            {
                var observation = Volatile.Read(ref eventStreamObservation);
                return observation is not null
                    && string.Equals(observation.CommunicationSessionId, sessionId, StringComparison.Ordinal)
                        ? observation
                        : new PlcsimEventStreamMonitoringState(sessionId, eventStreamMonitoringActive,
                            EventBufferReadState.NotObserved, EventStreamBatchReleaseState.Idle, false, 0);
            }

            private void RecordEventBufferMetadata(EventBufferMetadataReadResult metadata)
            {
                var previous = GetEventStreamObservation(preparedCommunicationSessionId ?? string.Empty);
                var fifo = metadata.FifoControl!;
                var status = metadata.Status!;
                Volatile.Write(ref eventStreamObservation, previous with
                {
                    BufferObservedEmpty = fifo.BufferEmpty,
                    BufferFull = fifo.BufferFull,
                    ProducerSequenceId = status.SequenceId,
                    DeletedSequenceId = status.DeletedSequenceId,
                    LostEventCount = status.LostEventCount
                });
            }

            private EventStreamMonitoringResult CreateRetainedEventStreamResult(string sessionId, bool isMonitoring)
            {
                return EventStreamMonitoringResult.CreateMonitoringResult(
                    sessionId, isMonitoring, GetEventStreamObservation(sessionId));
            }

            private EventStreamMonitoringResult CreateFailedEventStreamResult(
                string status, string sessionId, string code, string message)
            {
                var observation = GetEventStreamObservation(sessionId);
                if (Monitor.IsEntered(eventStreamObservationLock)
                    && string.Equals(preparedCommunicationSessionId, sessionId, StringComparison.Ordinal))
                {
                    observation = observation with
                    {
                        LastReadState = string.Equals(code, TagListNotUpToDateDiagnosticCode, StringComparison.Ordinal)
                            ? EventBufferReadState.RetryRequired
                            : EventBufferReadState.SessionInvalid
                    };
                    Volatile.Write(ref eventStreamObservation, observation);
                }

                return EventStreamMonitoringResult.CreateFailureResult(status, sessionId, code, message, observation);
            }

            private EventStreamMonitoringResult CreateObservedEventStreamResult(
                string sessionId, bool isMonitoring, EventBufferPollingSnapshotClassification classification,
                EventStreamCapturedBatch? batch, string code = "", string message = "")
            {
                var observation = GetEventStreamObservation(sessionId) with
                {
                    IsMonitoring = isMonitoring,
                    LastReadState = classification.Snapshot.ReadState,
                    BatchReleaseState = batch is not null && batch.CapturedEntries.Count > 0
                        ? EventStreamBatchReleaseState.Completed
                        : EventStreamBatchReleaseState.Idle
                };
                Volatile.Write(ref eventStreamObservation, observation);
                return EventStreamMonitoringResult.CreateBufferObservationResult(
                    sessionId, isMonitoring, classification, observation.BatchReleaseState.ToString(), batch,
                    observation, code, message);
            }
            private const uint PlcCycleWaitTimeoutMs = 100;
#if EVENTSTREAM_PERFORMANCE_MEASUREMENT
            private const string EventReadMeasurementTargetVariable =
                "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT_TARGET";
            private const string EventReadMeasurementModeVariable =
                "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT_MODE";
#endif
            private double lastRunControlReadDurationMs;
            private bool eventStreamBacklogKnown;
            private bool eventStreamReleasePending;
            private bool? eventStreamDrainCadenceActive;
            private Stopwatch? eventStreamReleaseRoundtripStopwatch;
            private uint eventStreamReleaseSequenceId;
            private int eventStreamReleaseConfirmationPollCount;
            private bool eventStreamReleasePendingWarningLogged;
#if EVENTSTREAM_PERFORMANCE_MEASUREMENT
            private readonly int? eventReadMeasurementTarget = ReadEventReadMeasurementTarget();
            private bool eventReadMeasurementCompleted;
#endif

            public EventBufferResetResult RequestEventBufferReset(EventBufferResetRequest request)
            {
                if (preparedInstance is null)
                {
                    return EventBufferResetResult.Failed(
                        "Unavailable",
                        "PlcsimAdvanced.V8.Worker.RuntimeNotPrepared",
                        "No PLCSIM Advanced runtime is prepared for EventBuffer reset requests.");
                }

                var communication = ValidateCommunicationSession(request.CommunicationSessionId, "EventBuffer reset requests");
                if (!communication.Succeeded)
                {
                    return EventBufferResetResult.Failed(
                        communication.Status,
                        communication.Code,
                        communication.Message);
                }

                var resetTag = request.RunControlRoot + ".ControlData.BufferCmd.Reset";
                var write = WriteBool(resetTag, true);
                if (!write.Succeeded)
                {
                    return EventBufferResetResult.Failed(
                        "Failed",
                        "PlcsimAdvanced.V8.Worker.EventBufferResetWriteFailed",
                        $"PLCSIM Advanced Runtime API could not write EventBuffer reset bit '{resetTag}': {write.Message}");
                }

                return EventBufferResetResult.AcceptedResult();
            }

            public EventStreamMonitoringResult SetEventStreamMonitoring(
                EventStreamMonitoringRequest request,
                bool start,
                Action<RunControlObservationResult> publishRunControlObservation,
                Action<EventStreamCapturedBatch> publishBatch,
                Action<EventStreamMonitoringResult> publishMonitoringState,
                Action<PlcsimCommunicationHealthState, string> publishCommunicationHealth,
                Action<EventStreamMonitoringFault> publishFault)
            {
                if (preparedInstance is null)
                {
                    return CreateFailedEventStreamResult(
                        "Unavailable",
                        request.CommunicationSessionId,
                        "PlcsimAdvanced.V8.Worker.RuntimeNotPrepared",
                        "No PLCSIM Advanced runtime is prepared for EventStream monitoring.");
                }

                var communication = ValidateCommunicationSession(request.CommunicationSessionId, "EventStream monitoring");
                if (!communication.Succeeded)
                {
                    return CreateFailedEventStreamResult(
                        communication.Status,
                        request.CommunicationSessionId,
                        communication.Code,
                        communication.Message);
                }

                if (!start)
                {
                    if (!StopEventStreamObservation())
                    {
                        return CreateFailedEventStreamResult(
                            "Failed",
                            request.CommunicationSessionId,
                            "PlcsimAdvanced.V8.Worker.ObservationStopTimedOut",
                            "UnitTest communication observation did not stop within the cleanup timeout.");
                    }

                    ResetEventStreamReleasePendingState();
                    return CreateRetainedEventStreamResult(request.CommunicationSessionId, eventStreamMonitoringActive);
                }

                if (!StopEventStreamObservation())
                {
                    return CreateFailedEventStreamResult(
                        "Failed",
                        request.CommunicationSessionId,
                        "PlcsimAdvanced.V8.Worker.ObservationStopTimedOut",
                        "A previous UnitTest communication observation did not stop within the cleanup timeout.");
                }

                eventStreamMonitoringActive = true;
#if EVENTSTREAM_PERFORMANCE_MEASUREMENT
                eventReadMeasurementCompleted = false;
#endif
                ResetEventStreamReleasePendingState();
                var result = ObserveInitialEventStreamWithRevalidation(request);
                if (!result.Succeeded)
                {
                    eventStreamMonitoringActive = false;
                    return result;
                }

                StartEventStreamObservation(
                    request,
                    publishRunControlObservation,
                    publishBatch,
                    publishMonitoringState,
                    publishCommunicationHealth,
                    publishFault,
                    result);
                eventStreamMonitoringActive = true;
                return result;
            }

            private EventStreamMonitoringResult ObserveInitialEventStreamWithRevalidation(
                EventStreamMonitoringRequest request)
            {
                Stopwatch? revalidationStopwatch = null;
                var revalidationAttempts = 0;
                while (true)
                {
                    var result = ObserveEventStreamOnce(request, CancellationToken.None);
                    if (result.Succeeded)
                    {
                        if (revalidationStopwatch is not null)
                        {
                            logger.LogInformation(
                                "PLCSIM Advanced EventBuffer recovered before monitoring start. CommunicationSessionId={CommunicationSessionId} Attempts={Attempts} ElapsedMilliseconds={ElapsedMilliseconds}",
                                request.CommunicationSessionId,
                                revalidationAttempts,
                                revalidationStopwatch.ElapsedMilliseconds);
                        }

                        return result;
                    }

                    if (!TryHandleTagListRevalidation(
                        request.CommunicationSessionId,
                        result.Code,
                        ref revalidationStopwatch,
                        ref revalidationAttempts,
                        out var fault))
                    {
                        return result;
                    }

                    if (fault is not null)
                    {
                        return CreateFailedEventStreamResult(
                            fault.Status,
                            request.CommunicationSessionId,
                            fault.Code,
                            fault.Message);
                    }

                    Thread.Sleep(TimeSpan.FromMilliseconds(50));
                }
            }

            private EventStreamMonitoringResult ObserveEventStreamOnce(
                EventStreamMonitoringRequest request,
                CancellationToken cancellationToken)
            {
                lock (eventStreamObservationLock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pendingRelease = ReadUInt32(
                        request.RunControlRoot + ".ControlData.BufferCmd.DeleteSequenceId");
                    if (!pendingRelease.Succeeded)
                    {
                        var code = IsTagListNotUpToDate(pendingRelease.ErrorCode)
                            ? TagListNotUpToDateDiagnosticCode
                            : "PlcsimAdvanced.V8.Worker.EventStreamBatchReleaseStateReadFailed";
                        return CreateFailedEventStreamResult(
                            "Failed",
                            request.CommunicationSessionId,
                            code,
                            $"PLCSIM Advanced Runtime API could not read EventStream release state: {pendingRelease.Message}");
                    }

                    if (eventStreamReleaseRoundtripStopwatch is not null)
                    {
                        eventStreamReleaseConfirmationPollCount++;
                    }

                    if (pendingRelease.UInt32Value != 0)
                    {
                        ObserveEventStreamReleasePending(
                            request.CommunicationSessionId,
                            pendingRelease.UInt32Value);
                        return CreateRetainedEventStreamResult(
                            request.CommunicationSessionId,
                            eventStreamMonitoringActive);
                    }

                    ObserveEventStreamReleaseRecovered(request.CommunicationSessionId);
                    var snapshot = ReadEventBufferPollingSnapshot(
                        request.EventBufferRoot,
                        cancellationToken);
                    if (!snapshot.Succeeded)
                    {
                        eventStreamBacklogKnown = false;
                        return CreateFailedEventStreamResult(
                            snapshot.Status,
                            request.CommunicationSessionId,
                            snapshot.Code,
                            snapshot.Message);
                    }

#if EVENTSTREAM_PERFORMANCE_MEASUREMENT
                    if (snapshot.PayloadReadDeferred)
                    {
                        eventStreamBacklogKnown = false;
                        return CreateRetainedEventStreamResult(
                            request.CommunicationSessionId,
                            eventStreamMonitoringActive);
                    }
#endif

                    eventStreamBacklogKnown = snapshot.HasKnownRemainingEvents;
                    LogEventStreamSnapshotHealth(
                        request.CommunicationSessionId,
                        snapshot.Classification!);
                    var capturedBatch = EventStreamCapturedBatch.FromClassification(
                        request.CommunicationSessionId,
                        snapshot.Classification!,
                        snapshot.CapturedEntries,
                        GetEventStreamObservation(request.CommunicationSessionId));
                    if (capturedBatch is null)
                    {
                        return CreateObservedEventStreamResult(
                            request.CommunicationSessionId,
                            eventStreamMonitoringActive,
                            snapshot.Classification!,
                            null);
                    }

                    if (capturedBatch.CapturedEntries.Count == 0)
                    {
                        var publishEmptyTransition = lastObservedEventBufferEmpty == false;
                        lastObservedEventBufferEmpty = true;
                        return CreateObservedEventStreamResult(
                            request.CommunicationSessionId,
                            eventStreamMonitoringActive,
                            snapshot.Classification!,
                            publishEmptyTransition ? capturedBatch : null);
                    }

                    lastObservedEventBufferEmpty = false;
                    Volatile.Write(ref eventStreamObservation, GetEventStreamObservation(request.CommunicationSessionId) with
                    {
                        LastReadState = snapshot.Classification!.Snapshot.ReadState,
                        BatchReleaseState = EventStreamBatchReleaseState.Idle
                    });
                    var release = ReleaseCapturedBatch(request.RunControlRoot, capturedBatch.HighestCapturedSequenceId);
                    if (!release.Succeeded)
                    {
                        if (string.Equals(
                            release.Code,
                            TagListNotUpToDateDiagnosticCode,
                            StringComparison.Ordinal))
                        {
                            return CreateFailedEventStreamResult(
                                "Failed",
                                request.CommunicationSessionId,
                                release.Code,
                                release.Message);
                        }

                        if (string.Equals(release.ReleaseState, "Pending", StringComparison.OrdinalIgnoreCase))
                        {
                            logger.LogDebug(
                                "EventStream release remains pending; the captured batch will be re-read. CommunicationSessionId={CommunicationSessionId} DeleteSequenceId={DeleteSequenceId}",
                                request.CommunicationSessionId,
                                capturedBatch.HighestCapturedSequenceId);
                        }
                        else
                        {
                            logger.LogWarning(
                                "EventStream release write failed; the captured batch will not be published and will be re-read. CommunicationSessionId={CommunicationSessionId} DeleteSequenceId={DeleteSequenceId} Code={Code} Message={Message}",
                                request.CommunicationSessionId,
                                capturedBatch.HighestCapturedSequenceId,
                                release.Code,
                                release.Message);
                        }

                        return CreateObservedEventStreamResult(
                            request.CommunicationSessionId,
                            eventStreamMonitoringActive,
                            snapshot.Classification!,
                            null,
                            release.Code,
                            release.Message);
                    }

                    return CreateObservedEventStreamResult(
                        request.CommunicationSessionId,
                        eventStreamMonitoringActive,
                        snapshot.Classification!,
                        capturedBatch);
                }
            }

            private void StartEventStreamObservation(
                EventStreamMonitoringRequest request,
                Action<RunControlObservationResult> publishRunControlObservation,
                Action<EventStreamCapturedBatch> publishBatch,
                Action<EventStreamMonitoringResult> publishMonitoringState,
                Action<PlcsimCommunicationHealthState, string> publishCommunicationHealth,
                Action<EventStreamMonitoringFault> publishFault,
                EventStreamMonitoringResult initialState)
            {
                lastObservedEventBufferEmpty = initialState.BufferObservedEmpty;
                ResetPlcCycleMonitoringInterval();
                var cancellation = new CancellationTokenSource();
                eventStreamObservationCancellation = cancellation;
                eventStreamObservationTask = Task.Run(
                    () => ObserveEventStreamLoop(
                        request,
                        publishRunControlObservation,
                        publishBatch,
                        publishMonitoringState,
                        publishCommunicationHealth,
                        publishFault,
                        initialState,
                        cancellation.Token));
            }

            private bool StopEventStreamObservation()
            {
                Interlocked.Increment(ref communicationTeardownInProgress);
                try
                {
                    var cancellation = eventStreamObservationCancellation;
                    var task = eventStreamObservationTask;
                    eventStreamMonitoringActive = false;

                    cancellation?.Cancel();

                    var observationStopped = WaitForObservationTask(task, TimeSpan.FromSeconds(2));
                    if (!observationStopped)
                    {
                        logger.LogWarning(
                            "UnitTest communication observation did not stop within the cleanup timeout. CommunicationSessionId={CommunicationSessionId}",
                            preparedCommunicationSessionId ?? string.Empty);
                        return false;
                    }

                    if (ReferenceEquals(eventStreamObservationCancellation, cancellation))
                    {
                        eventStreamObservationCancellation = null;
                    }

                    if (ReferenceEquals(eventStreamObservationTask, task))
                    {
                        eventStreamObservationTask = null;
                    }

                    cancellation?.Dispose();
                    return true;
                }
                finally
                {
                    Interlocked.Decrement(ref communicationTeardownInProgress);
                }
            }

            private static bool WaitForObservationTask(Task? task, TimeSpan timeout)
            {
                if (task is null || task.IsCompleted)
                {
                    return true;
                }

                var stopwatch = Stopwatch.StartNew();
                while (!task.IsCompleted && stopwatch.Elapsed < timeout)
                {
                    Thread.Sleep(10);
                }

                return task.IsCompleted;
            }

            private void ObserveEventStreamLoop(
                EventStreamMonitoringRequest request,
                Action<RunControlObservationResult> publishRunControlObservation,
                Action<EventStreamCapturedBatch> publishBatch,
                Action<EventStreamMonitoringResult> publishMonitoringState,
                Action<PlcsimCommunicationHealthState, string> publishCommunicationHealth,
                Action<EventStreamMonitoringFault> publishFault,
                EventStreamMonitoringResult initialState,
                CancellationToken cancellationToken)
            {
                RunControlObservationResult? previousRunControl = null;
                var previousMonitoringState = initialState;
                Stopwatch? tagListRevalidationStopwatch = null;
                var tagListRevalidationAttempts = 0;
                var observationStopwatch = Stopwatch.StartNew();
                long wakeupCount = 0;
                long observationCount = 0;
                long noEventWakeupCount = 0;
                long successfulReadCount = 0;
                long emptyReadCount = 0;
                long batchReadCount = 0;
                long eventCount = 0;
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var wakeup = WaitForObservationWakeup(cancellationToken);
                        wakeupCount++;
                        if (wakeup.StopRequested)
                        {
                            return;
                        }

                        if (wakeup.Faulted)
                        {
                            eventStreamMonitoringActive = false;
                            publishFault(EventStreamMonitoringFault.Failed(
                                request.CommunicationSessionId,
                                wakeup.Code,
                                wakeup.Message));
                            return;
                        }

                        if (!wakeup.Observe)
                        {
                            noEventWakeupCount++;
                            continue;
                        }

                        observationCount++;

                        var runControl = ObserveRunControlOnce(new RunControlObservationRequest(
                            request.RuntimeSessionId,
                            request.CommunicationSessionId,
                            request.RunControlRoot));
                        if (!runControl.Succeeded)
                        {
                            if (IsExpectedObservationTeardownFailure(
                                request.CommunicationSessionId,
                                runControl.Code,
                                runControl.Message,
                                cancellationToken))
                            {
                                eventStreamMonitoringActive = false;
                                return;
                            }

                            var revalidationStarted = tagListRevalidationStopwatch is null;
                            if (TryHandleTagListRevalidation(
                                request.CommunicationSessionId,
                                runControl.Code,
                                ref tagListRevalidationStopwatch,
                                ref tagListRevalidationAttempts,
                                out var runControlRevalidationFault))
                            {
                                if (runControlRevalidationFault is not null)
                                {
                                    eventStreamMonitoringActive = false;
                                    publishFault(runControlRevalidationFault);
                                    return;
                                }

                                if (revalidationStarted)
                                {
                                    publishCommunicationHealth(
                                        PlcsimCommunicationHealthState.RevalidationRequired,
                                        "PLCSIM Advanced UnitTest communication is revalidating after the tag list became stale.");
                                }

                                continue;
                            }

                            eventStreamMonitoringActive = false;
                            publishFault(EventStreamMonitoringFault.Failed(
                                request.CommunicationSessionId,
                                string.IsNullOrWhiteSpace(runControl.Code)
                                    ? "PlcsimAdvanced.V8.Worker.RunControlObservationFaulted"
                                    : runControl.Code,
                                string.IsNullOrWhiteSpace(runControl.Message)
                                    ? "RunControl observation faulted."
                                    : runControl.Message));
                            return;
                        }

                        var previousEventStreamObservation = Volatile.Read(ref eventStreamObservation);
                        var result = ObserveEventStreamOnce(request, cancellationToken);
                        if (!result.Succeeded)
                        {
                            if (IsExpectedObservationTeardownFailure(
                                request.CommunicationSessionId,
                                result.Code,
                                result.Message,
                                cancellationToken))
                            {
                                eventStreamMonitoringActive = false;
                                return;
                            }

                            var revalidationStarted = tagListRevalidationStopwatch is null;
                            if (TryHandleTagListRevalidation(
                                request.CommunicationSessionId,
                                result.Code,
                                ref tagListRevalidationStopwatch,
                                ref tagListRevalidationAttempts,
                                out var eventStreamRevalidationFault))
                            {
                                if (eventStreamRevalidationFault is not null)
                                {
                                    eventStreamMonitoringActive = false;
                                    publishMonitoringState(result);
                                    publishFault(eventStreamRevalidationFault);
                                    return;
                                }

                                if (revalidationStarted)
                                {
                                    publishCommunicationHealth(
                                        PlcsimCommunicationHealthState.RevalidationRequired,
                                        "PLCSIM Advanced UnitTest communication is revalidating after the tag list became stale.");
                                }

                                continue;
                            }

                            eventStreamMonitoringActive = false;
                            publishMonitoringState(result);
                            publishFault(EventStreamMonitoringFault.FromResult(request.CommunicationSessionId, result));
                            return;
                        }

                        successfulReadCount++;
                        ResetPlcCycleMonitoringInterval();
                        if (result.CapturedBatch is null || result.CapturedBatch.CapturedEntries.Count == 0)
                        {
                            emptyReadCount++;
                        }
                        else
                        {
                            batchReadCount++;
                            eventCount += result.CapturedBatch.CapturedEntries.Count;
                        }

                        // A retained result while deletion is pending is not a completed read cycle.
                        if (tagListRevalidationStopwatch is not null
                            && !ReferenceEquals(previousEventStreamObservation, Volatile.Read(ref eventStreamObservation)))
                        {
                            logger.LogInformation(
                                "PLCSIM Advanced tag list recovered in the existing UnitTest communication session after a complete RunControl/EventBuffer read cycle. CommunicationSessionId={CommunicationSessionId} Attempts={Attempts} ElapsedMilliseconds={ElapsedMilliseconds}",
                                request.CommunicationSessionId,
                                tagListRevalidationAttempts,
                                tagListRevalidationStopwatch.ElapsedMilliseconds);
                            publishCommunicationHealth(
                                PlcsimCommunicationHealthState.Healthy,
                                "PLCSIM Advanced UnitTest communication recovered after a complete valid read cycle.");
                            tagListRevalidationStopwatch = null;
                            tagListRevalidationAttempts = 0;
                        }

                        if (HasEventStreamMonitoringStateChanged(previousMonitoringState, result))
                        {
                            publishMonitoringState(result);
                            previousMonitoringState = result;
                        }

                        if (previousRunControl is null
                            || HasRunControlObservationChanged(previousRunControl, runControl))
                        {
                            logger.LogDebug(
                                "RunControl observation changed. CommunicationSessionId={CommunicationSessionId} LifecycleState={LifecycleState} ExecutionStatus={ExecutionStatus} Result={Result} ExecutionSignals={ExecutionSignals}",
                                request.CommunicationSessionId,
                                runControl.State,
                                runControl.ExecutionStatus,
                                runControl.Result,
                                runControl.ExecutionSignals);
                            publishRunControlObservation(runControl);
                            previousRunControl = runControl;
                        }

                        if (result.CapturedBatch is not null)
                        {
                            if (!TryPublishCapturedBatch(
                                request.CommunicationSessionId,
                                result.CapturedBatch,
                                publishBatch,
                                publishFault))
                            {
                                eventStreamMonitoringActive = false;
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                {
                    logger.LogDebug(
                        ex.GetBaseException(),
                        "UnitTest communication observation stopped during cleanup. CommunicationSessionId={CommunicationSessionId}",
                        request.CommunicationSessionId);
                }
                catch (Exception ex)
                {
                    eventStreamMonitoringActive = false;
                    publishFault(EventStreamMonitoringFault.Failed(
                        request.CommunicationSessionId,
                        "PlcsimAdvanced.V8.Worker.EventStreamObservationLoopFailed",
                        ex.GetBaseException().Message));
                }
                finally
                {
                    observationStopwatch.Stop();
                    logger.LogInformation(
                        "UnitTest communication observation summary. CommunicationSessionId={CommunicationSessionId} Strategy={Strategy} DurationMs={DurationMs} Wakeups={Wakeups} Observations={Observations} NoEventWakeups={NoEventWakeups} SuccessfulReads={SuccessfulReads} EmptyReads={EmptyReads} BatchReads={BatchReads} EventsRead={EventsRead}",
                        request.CommunicationSessionId,
                        communicationObservationStrategy,
                        observationStopwatch.Elapsed.TotalMilliseconds,
                        wakeupCount,
                        observationCount,
                        noEventWakeupCount,
                        successfulReadCount,
                        emptyReadCount,
                        batchReadCount,
                        eventCount);
                }
            }

            private bool TryPublishCapturedBatch(
                string communicationSessionId,
                EventStreamCapturedBatch batch,
                Action<EventStreamCapturedBatch> publishBatch,
                Action<EventStreamMonitoringFault> publishFault)
            {
                try
                {
                    publishBatch(batch);
                    return true;
                }
                catch (Exception ex)
                {
                    var failure = ex.GetBaseException();
                    var firstSequenceId = batch.CapturedEntries.Count == 0
                        ? 0
                        : batch.CapturedEntries[0].SequenceId;
                    var lastSequenceId = batch.CapturedEntries.Count == 0
                        ? 0
                        : batch.CapturedEntries[batch.CapturedEntries.Count - 1].SequenceId;
                    logger.LogError(
                        failure,
                        "EventStream batch publication failed after PLC release. CommunicationSessionId={CommunicationSessionId} EventCount={EventCount} FirstSequenceId={FirstSequenceId} LastSequenceId={LastSequenceId}",
                        communicationSessionId,
                        batch.CapturedEntries.Count,
                        firstSequenceId,
                        lastSequenceId);

                    try
                    {
                        publishFault(EventStreamMonitoringFault.Failed(
                            communicationSessionId,
                            "PlcsimAdvanced.V8.Worker.EventBatchPublicationFailed",
                            $"EventStream batch publication failed after PLC release for sequence range {firstSequenceId}..{lastSequenceId}: {failure.Message}"));
                    }
                    catch (Exception faultPublicationException)
                    {
                        logger.LogError(
                            faultPublicationException.GetBaseException(),
                            "EventStream batch publication diagnostic could not be delivered. CommunicationSessionId={CommunicationSessionId} FirstSequenceId={FirstSequenceId} LastSequenceId={LastSequenceId}",
                            communicationSessionId,
                            firstSequenceId,
                            lastSequenceId);
                    }

                    return false;
                }
            }

            private static bool HasEventStreamMonitoringStateChanged(
                EventStreamMonitoringResult previous,
                EventStreamMonitoringResult current)
            {
                return previous.IsMonitoring != current.IsMonitoring
                    || !string.Equals(previous.LastReadState, current.LastReadState, StringComparison.Ordinal)
                    || !string.Equals(previous.BatchReleaseState, current.BatchReleaseState, StringComparison.Ordinal)
                    || previous.BufferObservedEmpty != current.BufferObservedEmpty
                    || previous.LostEventCount != current.LostEventCount
                    || previous.BufferFull != current.BufferFull
                    || previous.ProducerSequenceId != current.ProducerSequenceId
                    || previous.DeletedSequenceId != current.DeletedSequenceId;
            }

            private bool IsExpectedObservationTeardownFailure(
                string communicationSessionId,
                string diagnosticCode,
                string message,
                CancellationToken cancellationToken)
            {
                if (!IsInvalidOperatingStateFailure(diagnosticCode, message)
                    || (!cancellationToken.IsCancellationRequested
                        && Volatile.Read(ref communicationTeardownInProgress) == 0
                        && !HasPreparedRuntimeLeftRunState()))
                {
                    return false;
                }

                logger.LogInformation(
                    "Ignoring expected UnitTest observation failure while communication is stopping or the PLC runtime is leaving Run. CommunicationSessionId={CommunicationSessionId} Code={Code} Message={Message}",
                    communicationSessionId,
                    diagnosticCode,
                    message);
                return true;
            }

            private bool HasPreparedRuntimeLeftRunState()
            {
                var snapshot = preparedRuntimeSnapshot;
                if (snapshot is not null
                    && !string.Equals(snapshot.State, "Run", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (preparedInstance is null)
                {
                    return false;
                }

                try
                {
                    return !string.Equals(
                        MapOperatingState(ReadProperty(preparedInstance, "OperatingState")),
                        "Run",
                        StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }

            private static bool IsInvalidOperatingStateFailure(string diagnosticCode, string message)
            {
                return ContainsOrdinalIgnoreCase(diagnosticCode, "InvalidOperatingState")
                    || ContainsOrdinalIgnoreCase(message, "InvalidOperatingState")
                    || ContainsOrdinalIgnoreCase(message, "Error Code: -39")
                    || ContainsOrdinalIgnoreCase(message, "ErrorCode=-39");
            }

            private bool TryHandleTagListRevalidation(
                string communicationSessionId,
                string diagnosticCode,
                ref Stopwatch? revalidationStopwatch,
                ref int revalidationAttempts,
                out EventStreamMonitoringFault? fault)
            {
                fault = null;
                if (!string.Equals(
                    diagnosticCode,
                    TagListNotUpToDateDiagnosticCode,
                    StringComparison.Ordinal))
                {
                    return false;
                }

                revalidationStopwatch ??= Stopwatch.StartNew();
                revalidationAttempts++;
                var status = ReadTagListStatus();
                var update = InvokeRuntimeOperation(preparedInstance!, "UpdateTagList");
                if (!update.Succeeded && !IsTagListNotUpToDate(update.ErrorCode))
                {
                    fault = EventStreamMonitoringFault.Failed(
                        communicationSessionId,
                        "PlcsimAdvanced.V8.Worker.UpdateTagListFailed",
                        $"PLCSIM Advanced Runtime API UpdateTagList failed during communication revalidation: {update.Message}");
                    return true;
                }

                if (revalidationAttempts == 1)
                {
                    logger.LogWarning(
                        "PLCSIM Advanced tag list became stale during UnitTest communication observation; revalidation started and the complete poll cycle was discarded. CommunicationSessionId={CommunicationSessionId} TimeoutMilliseconds={TimeoutMilliseconds} TagListStatusSucceeded={TagListStatusSucceeded} TagListDetails={TagListDetails} HmiOnly={HmiOnly} TagListStatusErrorCode={TagListStatusErrorCode} TagListStatusMessage={TagListStatusMessage} UpdateSucceeded={UpdateSucceeded} UpdateErrorCode={UpdateErrorCode} UpdateMessage={UpdateMessage}",
                        communicationSessionId,
                        communicationRevalidationTimeout.TotalMilliseconds,
                        status.Succeeded,
                        status.Details,
                        status.HmiOnly,
                        status.ErrorCode,
                        status.Message,
                        update.Succeeded,
                        update.ErrorCode,
                        update.Message);
                }
                else
                {
                    logger.LogDebug(
                        "PLCSIM Advanced tag-list revalidation attempt completed; the complete poll cycle remains discarded. CommunicationSessionId={CommunicationSessionId} Attempt={Attempt} TagListStatusSucceeded={TagListStatusSucceeded} TagListDetails={TagListDetails} HmiOnly={HmiOnly} UpdateSucceeded={UpdateSucceeded} UpdateErrorCode={UpdateErrorCode}",
                        communicationSessionId,
                        revalidationAttempts,
                        status.Succeeded,
                        status.Details,
                        status.HmiOnly,
                        update.Succeeded,
                        update.ErrorCode);
                }

                if (communicationRevalidationTimeout == TimeSpan.Zero
                    || revalidationStopwatch.Elapsed < communicationRevalidationTimeout)
                {
                    return true;
                }

                fault = EventStreamMonitoringFault.Failed(
                    communicationSessionId,
                    "PlcsimAdvanced.V8.Worker.CommunicationRevalidationTimedOut",
                    $"PLCSIM Advanced UnitTest communication remained stale for {revalidationStopwatch.ElapsedMilliseconds} ms.");
                return true;
            }

            private static bool HasRunControlObservationChanged(
                RunControlObservationResult previous,
                RunControlObservationResult current)
            {
                return previous.State != current.State
                    || previous.ExecutionStatus != current.ExecutionStatus
                    || previous.Result != current.Result
                    || previous.Uninitialized != current.Uninitialized
                    || previous.ConfigError != current.ConfigError
                    || previous.Ready != current.Ready
                    || previous.Running != current.Running
                    || previous.Completed != current.Completed
                    || previous.Aborted != current.Aborted
                    || previous.Inactive != current.Inactive
                    || previous.Error != current.Error;
            }

            private static TimeSpan NormalizePollInterval(TimeSpan pollInterval)
            {
                if (pollInterval <= TimeSpan.Zero)
                {
                    return TimeSpan.FromMilliseconds(10);
                }

                return pollInterval;
            }

            private static string NormalizeObservationStrategyName(string observationStrategy)
            {
                return string.Equals(observationStrategy, "PlcCycle", StringComparison.OrdinalIgnoreCase)
                    ? "PlcCycle"
                    : "Polling";
            }

            private ObservationWakeupResult WaitForObservationWakeup(CancellationToken cancellationToken)
            {
                if (string.Equals(communicationObservationStrategy, "PlcCycle", StringComparison.OrdinalIgnoreCase))
                {
                    return WaitForPlcCycleObservationWakeup(cancellationToken);
                }

                var drainActive = eventStreamBacklogKnown
                    || eventStreamReleasePending
                    || eventStreamReleaseRoundtripStopwatch is not null;
                LogObservationCadenceTransition(drainActive);
                var interval = drainActive
                    ? communicationDrainInterval
                    : communicationMonitoringInterval ?? TimeSpan.FromMilliseconds(10);
                return WaitForCancellation(cancellationToken, NormalizePollInterval(interval))
                    ? ObservationWakeupResult.Stop()
                    : ObservationWakeupResult.ObserveNow();
            }

            private void LogObservationCadenceTransition(bool drainActive)
            {
                if (eventStreamDrainCadenceActive == drainActive)
                {
                    return;
                }

                eventStreamDrainCadenceActive = drainActive;
                var plcCycle = string.Equals(
                    communicationObservationStrategy,
                    "PlcCycle",
                    StringComparison.OrdinalIgnoreCase);
                logger.LogDebug(
                    "EventStream observation cadence changed. State={State} Strategy={Strategy} IntervalMs={IntervalMs} BacklogKnown={BacklogKnown} ReleasePending={ReleasePending}",
                    drainActive ? "Drain" : "Monitoring",
                    plcCycle ? "PlcCycle" : "Polling",
                    plcCycle
                        ? (double?)null
                        : (drainActive
                            ? communicationDrainInterval
                            : communicationMonitoringInterval ?? TimeSpan.FromMilliseconds(10)).TotalMilliseconds,
                    eventStreamBacklogKnown,
                    eventStreamReleasePending || eventStreamReleaseRoundtripStopwatch is not null);
            }

            private static bool WaitForCancellation(CancellationToken cancellationToken, TimeSpan timeout)
            {
                var maximumWait = TimeSpan.FromMilliseconds(int.MaxValue);
                var remaining = timeout;
                while (remaining > maximumWait)
                {
                    if (cancellationToken.WaitHandle.WaitOne(maximumWait))
                    {
                        return true;
                    }

                    remaining -= maximumWait;
                }

                return cancellationToken.WaitHandle.WaitOne(remaining);
            }

            private ObservationWakeupResult WaitForPlcCycleObservationWakeup(CancellationToken cancellationToken)
            {
                if (preparedInstance is null)
                {
                    return ObservationWakeupResult.Stop();
                }

                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.Zero))
                {
                    return ObservationWakeupResult.Stop();
                }

                var adapter = plcCycleObservationAdapter;
                if (adapter is null)
                {
                    return ObservationWakeupResult.FromFault(
                        "PlcsimAdvanced.V8.Worker.PlcCycleObservationUnavailable",
                        "PLCSIM Advanced V8 sync-point observation is not initialized.");
                }

                var wait = adapter.Wait(PlcCycleWaitTimeoutMs);
                if (cancellationToken.IsCancellationRequested)
                {
                    return ObservationWakeupResult.Stop();
                }

                if (wait.EventSignaled)
                {
                    return ShouldObservePlcCycleNow()
                        ? ObservationWakeupResult.ObserveNow()
                        : ObservationWakeupResult.NoEvent();
                }

                if (wait.TimedOut)
                {
                    return ObservationWakeupResult.NoEvent();
                }

                return ObservationWakeupResult.FromFault(
                    "PlcsimAdvanced.V8.Worker.PlcCycleObservationWaitFailed",
                    string.IsNullOrWhiteSpace(wait.Message)
                        ? "PLCSIM Advanced V8 sync-point observation wait failed."
                        : $"PLCSIM Advanced V8 sync-point observation wait failed: {wait.Message}");
            }

            private bool ShouldObservePlcCycleNow()
            {
                if (communicationMonitoringInterval is null
                    || eventStreamBacklogKnown
                    || eventStreamReleasePending
                    || eventStreamReleaseRoundtripStopwatch is not null)
                {
                    return true;
                }

                return plcCycleMonitoringIntervalStopwatch is null
                    || plcCycleMonitoringIntervalStopwatch.Elapsed >= communicationMonitoringInterval.Value;
            }

            private void ResetPlcCycleMonitoringInterval()
            {
                if (!string.Equals(
                        communicationObservationStrategy,
                        "PlcCycle",
                        StringComparison.OrdinalIgnoreCase)
                    || communicationMonitoringInterval is null)
                {
                    plcCycleMonitoringIntervalStopwatch = null;
                    return;
                }

                plcCycleMonitoringIntervalStopwatch ??= new Stopwatch();
                plcCycleMonitoringIntervalStopwatch.Restart();
            }

            private bool TryResetCommunicationState(bool preserveObservation = false)
            {
                if (!StopEventStreamObservation())
                {
                    return false;
                }

                DisablePlcCycleObservation();
                preparedCommunicationSessionId = null;
                if (!preserveObservation)
                {
                    Volatile.Write(ref eventStreamObservation, null);
                }
                eventStreamContractVersion = string.Empty;
                eventStreamFrameworkVersion = string.Empty;
                communicationObservationStrategy = "Polling";
                communicationMonitoringInterval = TimeSpan.FromMilliseconds(10);
                communicationDrainInterval = TimeSpan.FromMilliseconds(1);
                communicationMaxEventsPerBatch = 32;
                communicationRevalidationTimeout = TimeSpan.FromSeconds(30);
                eventStreamBacklogKnown = false;
                eventStreamReleasePending = false;
                eventStreamDrainCadenceActive = null;
                plcCycleMonitoringIntervalStopwatch = null;
                lastObservedEventBufferEmpty = null;
                lastObservedEventBufferFull = null;
                lastObservedEventBufferReadState = null;
                return true;
            }

            private void ResetCommunicationState()
            {
                if (!TryResetCommunicationState())
                {
                    throw new InvalidOperationException(
                        "UnitTest communication state could not be reset because the observation loop did not stop.");
                }
            }

            private void LogEventStreamSnapshotHealth(
                string communicationSessionId,
                EventBufferPollingSnapshotClassification classification)
            {
                var snapshot = classification.Snapshot;
                if (snapshot.FifoControl.BufferFull && lastObservedEventBufferFull != true)
                {
                    logger.LogWarning(
                        "EventStream PLC buffer is full. CommunicationSessionId={CommunicationSessionId} ProducerSequenceId={ProducerSequenceId} DeletedSequenceId={DeletedSequenceId} LostEventCount={LostEventCount}",
                        communicationSessionId,
                        snapshot.Status.SequenceId,
                        snapshot.Status.DeletedSequenceId,
                        snapshot.Status.LostEventCount);
                }
                else if (!snapshot.FifoControl.BufferFull && lastObservedEventBufferFull == true)
                {
                    logger.LogInformation(
                        "EventStream PLC buffer recovered from full state. CommunicationSessionId={CommunicationSessionId} ProducerSequenceId={ProducerSequenceId} DeletedSequenceId={DeletedSequenceId}",
                        communicationSessionId,
                        snapshot.Status.SequenceId,
                        snapshot.Status.DeletedSequenceId);
                }

                lastObservedEventBufferFull = snapshot.FifoControl.BufferFull;

                if (lastObservedEventBufferReadState != snapshot.ReadState)
                {
                    if (snapshot.ReadState != EventBufferReadState.Valid
                        && snapshot.ReadState != EventBufferReadState.Empty)
                    {
                        logger.LogWarning(
                            "EventStream snapshot is not stable. CommunicationSessionId={CommunicationSessionId} ReadState={ReadState} ProducerSequenceId={ProducerSequenceId} DeletedSequenceId={DeletedSequenceId}",
                            communicationSessionId,
                            snapshot.ReadState,
                            snapshot.Status.SequenceId,
                            snapshot.Status.DeletedSequenceId);
                    }
                    else if (lastObservedEventBufferReadState.HasValue
                        && lastObservedEventBufferReadState != EventBufferReadState.Valid
                        && lastObservedEventBufferReadState != EventBufferReadState.Empty)
                    {
                        logger.LogInformation(
                            "EventStream snapshot returned to a stable state. CommunicationSessionId={CommunicationSessionId} ReadState={ReadState}",
                            communicationSessionId,
                            snapshot.ReadState);
                    }

                    lastObservedEventBufferReadState = snapshot.ReadState;
                }
            }

            private RuntimeApiOperationResult EnablePlcCycleObservation()
            {
                if (!PlcCycleObservationAdapter.TryCreate(
                    preparedInstance,
                    out var adapter,
                    out var failure))
                {
                    return RuntimeApiOperationResult.Failed(failure);
                }

                plcCycleObservationAdapter = adapter;
                var enable = adapter!.SetDefaultModeEnabled(true);
                if (!enable.Succeeded)
                {
                    plcCycleObservationAdapter = null;
                    return enable;
                }

                syncPointDefaultModeEnabled = true;

                var register = adapter.Register();
                if (!register.Succeeded)
                {
                    DisablePlcCycleObservation();
                    return register;
                }

                syncPointEventRegistered = true;
                return RuntimeApiOperationResult.SucceededResult();
            }

            private void DisablePlcCycleObservation()
            {
                var adapter = plcCycleObservationAdapter;

                if (syncPointEventRegistered && adapter is not null)
                {
                    var unregister = adapter.Unregister();
                    if (!unregister.Succeeded)
                    {
                        logger.LogWarning(
                            "PLCSIM Advanced sync-point event unregistration failed during cleanup. ErrorCode={ErrorCode} Message={Message}",
                            unregister.ErrorCode,
                            unregister.Message);
                    }

                    syncPointEventRegistered = false;
                }
                else
                {
                    syncPointEventRegistered = false;
                }

                if (syncPointDefaultModeEnabled && adapter is not null)
                {
                    var disable = adapter.SetDefaultModeEnabled(false);
                    if (!disable.Succeeded)
                    {
                        logger.LogWarning(
                            "PLCSIM Advanced default-mode sync-point event generation could not be disabled during cleanup. ErrorCode={ErrorCode} Message={Message}",
                            disable.ErrorCode,
                            disable.Message);
                    }

                    syncPointDefaultModeEnabled = false;
                }
                else
                {
                    syncPointDefaultModeEnabled = false;
                }

                plcCycleObservationAdapter = null;
            }

            private EventStreamReleaseResult ReleaseCapturedBatch(string runControlRoot, uint deleteSequenceId)
            {
                if (deleteSequenceId == 0)
                {
                    return EventStreamReleaseResult.Failed(
                        "Rejected",
                        0,
                        "PlcsimAdvanced.V8.Worker.EventStreamReleaseSequenceInvalid",
                        "EventStream release requires a non-zero DeleteSequenceId.");
                }

                var roundtripStopwatch = Stopwatch.StartNew();
                var deleteSequenceTag = runControlRoot + ".ControlData.BufferCmd.DeleteSequenceId";
                var pendingReadStartedAt = Stopwatch.GetTimestamp();
                var pending = ReadUInt32(deleteSequenceTag);
                var pendingReadDurationMs = (Stopwatch.GetTimestamp() - pendingReadStartedAt) * 1000d / Stopwatch.Frequency;
                if (!pending.Succeeded)
                {
                    var code = IsTagListNotUpToDate(pending.ErrorCode)
                        ? TagListNotUpToDateDiagnosticCode
                        : "PlcsimAdvanced.V8.Worker.EventStreamReleasePendingReadFailed";
                    return EventStreamReleaseResult.Failed(
                        "Failed",
                        0,
                        code,
                        $"PLCSIM Advanced Runtime API could not read EventStream release request field '{deleteSequenceTag}': {pending.Message}");
                }

                if (pending.UInt32Value != 0)
                {
                    eventStreamReleasePending = true;
                    return EventStreamReleaseResult.Deferred(
                        pending.UInt32Value,
                        "PlcsimAdvanced.V8.Worker.EventStreamReleasePending",
                        $"EventStream release request field '{deleteSequenceTag}' is still pending with DeleteSequenceId {pending.UInt32Value}.");
                }

                var writeStartedAt = Stopwatch.GetTimestamp();
                var write = WriteUInt32(deleteSequenceTag, deleteSequenceId);
                var writeDurationMs = (Stopwatch.GetTimestamp() - writeStartedAt) * 1000d / Stopwatch.Frequency;
                if (!write.Succeeded)
                {
                    return EventStreamReleaseResult.Failed(
                        "Failed",
                        0,
                        "PlcsimAdvanced.V8.Worker.EventStreamReleaseWriteFailed",
                        $"PLCSIM Advanced Runtime API could not write EventStream DeleteSequenceId '{deleteSequenceTag}': {write.Message}");
                }

                eventStreamReleaseRoundtripStopwatch = roundtripStopwatch;
                eventStreamReleaseSequenceId = deleteSequenceId;
                eventStreamReleaseConfirmationPollCount = 0;
                eventStreamReleasePendingWarningLogged = false;
                eventStreamReleasePending = true;
                logger.LogDebug(
                    "EventStream release write completed. DeleteSequenceId={DeleteSequenceId} PendingReadDurationMs={PendingReadDurationMs} WriteDurationMs={WriteDurationMs}",
                    deleteSequenceId,
                    pendingReadDurationMs,
                    writeDurationMs);
                return EventStreamReleaseResult.Accepted(deleteSequenceId);
            }

            private void ObserveEventStreamReleasePending(
                string communicationSessionId,
                uint deleteSequenceId)
            {
                eventStreamReleasePending = true;
                if (eventStreamReleasePendingWarningLogged)
                {
                    return;
                }

                eventStreamReleasePendingWarningLogged = true;
                logger.LogWarning(
                    "EventStream capture is deferred while the previous release remains pending. CommunicationSessionId={CommunicationSessionId} DeleteSequenceId={DeleteSequenceId}",
                    communicationSessionId,
                    deleteSequenceId);
            }

            private void ObserveEventStreamReleaseRecovered(string communicationSessionId)
            {
                eventStreamReleasePending = false;
                if (eventStreamReleaseRoundtripStopwatch is null)
                {
                    return;
                }

                eventStreamReleaseRoundtripStopwatch.Stop();
                logger.LogInformation(
                    "EventStream release completed; capture resumed. CommunicationSessionId={CommunicationSessionId} DeleteSequenceId={DeleteSequenceId} RoundtripDurationMs={RoundtripDurationMs} ConfirmationPolls={ConfirmationPolls}",
                    communicationSessionId,
                    eventStreamReleaseSequenceId,
                    eventStreamReleaseRoundtripStopwatch.Elapsed.TotalMilliseconds,
                    eventStreamReleaseConfirmationPollCount);
                ResetEventStreamReleasePendingState();
            }

            private void ResetEventStreamReleasePendingState()
            {
                eventStreamReleasePending = false;
                eventStreamReleaseRoundtripStopwatch = null;
                eventStreamReleaseSequenceId = 0;
                eventStreamReleaseConfirmationPollCount = 0;
                eventStreamReleasePendingWarningLogged = false;
            }

            private EventStreamSnapshotReadResult ReadEventBufferPollingSnapshot(
                string eventBufferRoot,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshotStartedAt = Stopwatch.GetTimestamp();
                var preMetadataStartedAt = Stopwatch.GetTimestamp();
                var preMetadata = ReadEventBufferMetadata(eventBufferRoot);
                var preMetadataDurationMs = (Stopwatch.GetTimestamp() - preMetadataStartedAt) * 1000d / Stopwatch.Frequency;
                if (!preMetadata.Succeeded)
                {
                    return EventStreamSnapshotReadResult.Failed(preMetadata.Code, preMetadata.Message);
                }

                RecordEventBufferMetadata(preMetadata);

                var classifier = new EventBufferPollingSnapshotClassifier(eventStreamContractVersion, eventStreamFrameworkVersion);
#if EVENTSTREAM_PERFORMANCE_MEASUREMENT
                var allReadablePositions = classifier.DeriveReadablePositions(preMetadata.FifoControl!);
                if (eventReadMeasurementTarget is { } measurementTarget
                    && (eventReadMeasurementCompleted || allReadablePositions.Count < measurementTarget))
                {
                    return EventStreamSnapshotReadResult.Deferred(allReadablePositions.Count > 0);
                }
#endif

                var effectiveMaxEventsPerBatch = communicationMaxEventsPerBatch.HasValue
                    ? Math.Min(communicationMaxEventsPerBatch.Value, preMetadata.FifoControl!.BufferLength)
                    : preMetadata.FifoControl!.BufferLength;
                var readablePositions = classifier.DeriveReadablePositions(
                    preMetadata.FifoControl!,
                    effectiveMaxEventsPerBatch);
                var entriesStartedAt = Stopwatch.GetTimestamp();
                var entries = ReadEventBufferEntries(
                    eventBufferRoot,
                    readablePositions,
                    cancellationToken);
                var entriesDurationMs = (Stopwatch.GetTimestamp() - entriesStartedAt) * 1000d / Stopwatch.Frequency;
                if (!entries.Succeeded)
                {
                    return EventStreamSnapshotReadResult.Failed(entries.Code, entries.Message);
                }

#if EVENTSTREAM_PERFORMANCE_MEASUREMENT
                if (eventReadMeasurementTarget is { } completedMeasurementTarget)
                {
                    eventReadMeasurementCompleted = true;
                    logger.LogInformation(
                        "EventStream payload-read measurement completed. TargetEventCount={TargetEventCount} EventCount={EventCount} EntryReadDurationMs={EntryReadDurationMs}",
                        completedMeasurementTarget,
                        readablePositions.Count,
                        entriesDurationMs);
                }
#endif

                var capturedEntries = new List<EventBufferCapturedEntry>(entries.Entries);
                var capturedPositions = new List<ushort>(capturedEntries.Count);
                var sequenceIdsByBufferIndex = new Dictionary<ushort, uint>();
                foreach (var entry in capturedEntries)
                {
                    capturedPositions.Add(entry.BufferIndex);
                    sequenceIdsByBufferIndex[entry.BufferIndex] = entry.SequenceId;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var postMetadataStartedAt = Stopwatch.GetTimestamp();
                var postMetadata = ReadEventBufferMetadata(eventBufferRoot);
                var postMetadataDurationMs = (Stopwatch.GetTimestamp() - postMetadataStartedAt) * 1000d / Stopwatch.Frequency;
                if (!postMetadata.Succeeded)
                {
                    return EventStreamSnapshotReadResult.Failed(postMetadata.Code, postMetadata.Message);
                }

                RecordEventBufferMetadata(postMetadata);

                var classification = classifier.Classify(
                    preMetadata.FifoControl!,
                    preMetadata.Status!,
                    capturedPositions,
                    sequenceIdsByBufferIndex,
                    postMetadata.FifoControl!,
                    postMetadata.Status!,
                    DateTimeOffset.Now);
                var hasKnownRemainingEvents = classification.Snapshot.ReadState is EventBufferReadState.Valid or EventBufferReadState.Empty
                    && classifier.DeriveReadablePositions(postMetadata.FifoControl!).Count > capturedPositions.Count;
                if (classification.Snapshot.ReadState != EventBufferReadState.Valid)
                {
                    capturedEntries.Clear();
                }

                if (readablePositions.Count > 0 || hasKnownRemainingEvents)
                {
                    var eventChunkCount = (readablePositions.Count + EventSignalBatchSize - 1) / EventSignalBatchSize;
                    var numericBatchReadCalls = 2 + (eventChunkCount * 2);
                    var stringScalarReadCalls = readablePositions.Count * 3;
                    logger.LogDebug(
                        "EventStream snapshot read completed. EventCount={EventCount} CapturedEventCount={CapturedEventCount} ReadState={ReadState} EffectiveMaxEventsPerBatch={EffectiveMaxEventsPerBatch} KnownRemainingEvents={KnownRemainingEvents} DurationMs={DurationMs} RunControlReadDurationMs={RunControlReadDurationMs} PreMetadataDurationMs={PreMetadataDurationMs} EntryReadDurationMs={EntryReadDurationMs} PostMetadataDurationMs={PostMetadataDurationMs} RuntimeApiReadCalls={RuntimeApiReadCalls} MetadataBatchReadCalls=2 NumericBatchReadCalls={NumericBatchReadCalls} StringScalarReadCalls={StringScalarReadCalls}",
                        readablePositions.Count,
                        capturedEntries.Count,
                        classification.Snapshot.ReadState,
                        effectiveMaxEventsPerBatch,
                        hasKnownRemainingEvents,
                        (Stopwatch.GetTimestamp() - snapshotStartedAt) * 1000d / Stopwatch.Frequency,
                        lastRunControlReadDurationMs,
                        preMetadataDurationMs,
                        entriesDurationMs,
                        postMetadataDurationMs,
                        numericBatchReadCalls + stringScalarReadCalls,
                        numericBatchReadCalls,
                        stringScalarReadCalls);
                }

                return EventStreamSnapshotReadResult.Success(
                    classification,
                    capturedEntries.AsReadOnly(),
                    hasKnownRemainingEvents);
            }

#if EVENTSTREAM_PERFORMANCE_MEASUREMENT
            private static int? ReadEventReadMeasurementTarget()
            {
                if (!string.Equals(
                    Environment.GetEnvironmentVariable(EventReadMeasurementModeVariable),
                    "Controlled",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var value = Environment.GetEnvironmentVariable(EventReadMeasurementTargetVariable);
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                if (!int.TryParse(value, out var target) || target < 1 || target > 255)
                {
                    throw new InvalidOperationException(
                        $"{EventReadMeasurementTargetVariable} must be an integer from 1 through 255.");
                }

                return target;
            }
#endif

#if VALIDATION_WORKER
            private EventStreamSnapshotReadResult ReadEventBufferPollingSnapshot(string eventBufferRoot)
            {
                return ReadEventBufferPollingSnapshot(eventBufferRoot, CancellationToken.None);
            }
#endif

            private EventBufferEntriesReadResult ReadEventBufferEntries(
                string eventBufferRoot,
                IReadOnlyList<ushort> readablePositions,
                CancellationToken cancellationToken)
            {
                var capturedEntries = new List<EventBufferCapturedEntry>(readablePositions.Count);
                for (var offset = 0; offset < readablePositions.Count; offset += EventSignalBatchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(EventSignalBatchSize, readablePositions.Count - offset);
                    var requests = new List<SignalReadRequest>(count * 7);
                    for (var index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entryRoot = $"{eventBufferRoot}.Buffer[{readablePositions[offset + index]}]";
                        requests.Add(new SignalReadRequest(entryRoot + ".SequenceId", "UInt32"));
                        requests.Add(new SignalReadRequest(entryRoot + ".Timestamp", "Int64"));
                        requests.Add(new SignalReadRequest(entryRoot + ".OwnerScope", "UInt8"));
                        requests.Add(new SignalReadRequest(entryRoot + ".EventType", "UInt8"));
                        requests.Add(new SignalReadRequest(entryRoot + ".Severity", "UInt8"));
                        requests.Add(new SignalReadRequest(entryRoot + ".State", "Int16"));
                        requests.Add(new SignalReadRequest(entryRoot + ".Result", "Int16"));
                    }

                    var numericValues = ReadSignalBatch(requests);
                    if (!numericValues.Succeeded)
                    {
                        return FailedEventBufferEntriesRead(numericValues);
                    }

                    var names = new StringReadResult[count];
                    var resourceNames = new StringReadResult[count];
                    var messages = new StringReadResult[count];
                    for (var index = 0; index < count; index++)
                    {
                        var entryRoot = $"{eventBufferRoot}.Buffer[{readablePositions[offset + index]}]";
                        names[index] = ReadString(entryRoot + ".Name");
                        resourceNames[index] = ReadString(entryRoot + ".ResourceName");
                        messages[index] = ReadString(entryRoot + ".Message");
                        var failedString = FirstFailed(names[index], resourceNames[index], messages[index]);
                        if (failedString is not null)
                        {
                            var code = AnyTagListNotUpToDate(names[index], resourceNames[index], messages[index])
                                ? TagListNotUpToDateDiagnosticCode
                                : "PlcsimAdvanced.V8.Worker.EventBufferEntryReadFailed";
                            return EventBufferEntriesReadResult.Failed(
                                code,
                                $"PLCSIM Advanced Runtime API could not completely read EventBuffer Buffer[{readablePositions[offset + index]}]: {failedString.Message}");
                        }
                    }

                    var confirmationRequests = new List<SignalReadRequest>(count);
                    for (var index = 0; index < count; index++)
                    {
                        var entryRoot = $"{eventBufferRoot}.Buffer[{readablePositions[offset + index]}]";
                        confirmationRequests.Add(new SignalReadRequest(entryRoot + ".SequenceId", "UInt32"));
                    }

                    var confirmations = ReadSignalBatch(confirmationRequests);
                    if (!confirmations.Succeeded)
                    {
                        return FailedEventBufferEntriesRead(confirmations);
                    }

                    for (var index = 0; index < count; index++)
                    {
                        var valueOffset = index * 7;
                        var sequenceId = numericValues.Values[valueOffset].UInt32Value;
                        if (sequenceId == 0
                            || sequenceId != confirmations.Values[index].UInt32Value)
                        {
                            return EventBufferEntriesReadResult.Success(capturedEntries.AsReadOnly());
                        }

                        capturedEntries.Add(new EventBufferCapturedEntry(
                            readablePositions[offset + index],
                            sequenceId,
                            numericValues.Values[valueOffset + 1].Int64Value,
                            numericValues.Values[valueOffset + 2].UInt8Value,
                            numericValues.Values[valueOffset + 3].UInt8Value,
                            numericValues.Values[valueOffset + 4].UInt8Value,
                            names[index].Value,
                            resourceNames[index].Value,
                            numericValues.Values[valueOffset + 5].Int16Value,
                            numericValues.Values[valueOffset + 6].Int16Value,
                            messages[index].Value));
                    }
                }

                return EventBufferEntriesReadResult.Success(capturedEntries.AsReadOnly());
            }

            private static EventBufferEntriesReadResult FailedEventBufferEntriesRead(
                SignalBatchReadResult batch)
            {
                var code = IsTagListNotUpToDate(batch.ErrorCode)
                    ? TagListNotUpToDateDiagnosticCode
                    : "PlcsimAdvanced.V8.Worker.EventBufferEntryReadFailed";
                return EventBufferEntriesReadResult.Failed(
                    code,
                    "PLCSIM Advanced Runtime API could not completely read EventBuffer entries: " + batch.Message);
            }

            private EventBufferMetadataReadResult ReadEventBufferMetadata(string eventBufferRoot)
            {
                var requests = new[]
                {
                    new SignalReadRequest(eventBufferRoot + ".FifoControl.Index_OUT", "UInt16"),
                    new SignalReadRequest(eventBufferRoot + ".FifoControl.Index_IN", "UInt16"),
                    new SignalReadRequest(eventBufferRoot + ".FifoControl.BufferLength", "UInt16"),
                    new SignalReadRequest(eventBufferRoot + ".FifoControl.BufferEmpty", "Bool"),
                    new SignalReadRequest(eventBufferRoot + ".FifoControl.BufferFull", "Bool"),
                    new SignalReadRequest(eventBufferRoot + ".FifoControl.ConfigError", "Bool"),
                    new SignalReadRequest(eventBufferRoot + ".FifoControl.IndexError", "Bool"),
                    new SignalReadRequest(eventBufferRoot + ".Status.SequenceId", "UInt32"),
                    new SignalReadRequest(eventBufferRoot + ".Status.DeletedSequenceId", "UInt32"),
                    new SignalReadRequest(eventBufferRoot + ".Status.LostEventCount", "UInt16")
                };
                var metadata = ReadSignalBatch(requests);
                if (!metadata.Succeeded)
                {
                    var code = IsTagListNotUpToDate(metadata.ErrorCode)
                        ? TagListNotUpToDateDiagnosticCode
                        : "PlcsimAdvanced.V8.Worker.EventBufferMetadataReadFailed";
                    return EventBufferMetadataReadResult.Failed(
                        code,
                        "PLCSIM Advanced Runtime API could not read EventBuffer metadata: " + metadata.Message);
                }

                return EventBufferMetadataReadResult.Success(
                    new PlcsimEventBufferFifoControlSnapshot(
                        metadata.Values[0].UInt16Value,
                        metadata.Values[1].UInt16Value,
                        metadata.Values[2].UInt16Value,
                        metadata.Values[3].BoolValue,
                        metadata.Values[4].BoolValue,
                        metadata.Values[5].BoolValue,
                        metadata.Values[6].BoolValue),
                    new PlcsimEventBufferStatusSnapshot(
                        metadata.Values[7].UInt32Value,
                        metadata.Values[8].UInt32Value,
                        metadata.Values[9].UInt16Value));
            }

            private EventBufferEntryReadResult ReadEventBufferEntry(string eventBufferRoot, ushort bufferIndex)
            {
                var entryRoot = $"{eventBufferRoot}.Buffer[{bufferIndex}]";
                var sequenceId = ReadUInt32(entryRoot + ".SequenceId");
                var timestamp = ReadInt64(entryRoot + ".Timestamp");
                var ownerScope = ReadUInt8(entryRoot + ".OwnerScope");
                var eventType = ReadUInt8(entryRoot + ".EventType");
                var severity = ReadUInt8(entryRoot + ".Severity");
                var name = ReadString(entryRoot + ".Name");
                var resourceName = ReadString(entryRoot + ".ResourceName");
                var state = ReadInt16(entryRoot + ".State");
                var result = ReadInt16(entryRoot + ".Result");
                var message = ReadString(entryRoot + ".Message");
                var confirmedSequenceId = ReadUInt32(entryRoot + ".SequenceId");

                var failedValue = FirstFailed(
                    sequenceId,
                    timestamp,
                    ownerScope,
                    eventType,
                    severity,
                    state,
                    result,
                    confirmedSequenceId);
                var failedString = FirstFailed(name, resourceName, message);
                if (failedValue is not null || failedString is not null)
                {
                    var stale = AnyTagListNotUpToDate(
                        sequenceId,
                        timestamp,
                        ownerScope,
                        eventType,
                        severity,
                        state,
                        result,
                        confirmedSequenceId)
                        || AnyTagListNotUpToDate(name, resourceName, message);
                    var code = stale
                        ? TagListNotUpToDateDiagnosticCode
                        : "PlcsimAdvanced.V8.Worker.EventBufferEntryReadFailed";
                    return EventBufferEntryReadResult.Failed(
                        code,
                        $"PLCSIM Advanced Runtime API could not completely read EventBuffer Buffer[{bufferIndex}]: "
                        + (failedValue?.Message ?? failedString!.Message));
                }

                if (sequenceId.UInt32Value == 0
                    || sequenceId.UInt32Value != confirmedSequenceId.UInt32Value)
                {
                    return EventBufferEntryReadResult.Failed(
                        "PlcsimAdvanced.V8.Worker.EventBufferEntryChanged",
                        $"EventBuffer Buffer[{bufferIndex}] changed while its event payload was being captured.");
                }

                return EventBufferEntryReadResult.Success(new EventBufferCapturedEntry(
                    bufferIndex,
                    sequenceId.UInt32Value,
                    timestamp.Int64Value,
                    ownerScope.UInt8Value,
                    eventType.UInt8Value,
                    severity.UInt8Value,
                    name.Value,
                    resourceName.Value,
                    state.Int16Value,
                    result.Int16Value,
                    message.Value));
            }

            private EventBufferFifoReadResult ReadEventBufferFifoControl(string eventBufferRoot)
            {
                var indexOut = ReadUInt16(eventBufferRoot + ".FifoControl.Index_OUT");
                var indexIn = ReadUInt16(eventBufferRoot + ".FifoControl.Index_IN");
                var bufferLength = ReadUInt16(eventBufferRoot + ".FifoControl.BufferLength");
                var bufferEmpty = ReadBool(eventBufferRoot + ".FifoControl.BufferEmpty");
                var bufferFull = ReadBool(eventBufferRoot + ".FifoControl.BufferFull");
                var configError = ReadBool(eventBufferRoot + ".FifoControl.ConfigError");
                var indexError = ReadBool(eventBufferRoot + ".FifoControl.IndexError");
                var failed = FirstFailed(indexOut, indexIn, bufferLength, bufferEmpty, bufferFull, configError, indexError);
                if (failed is not null)
                {
                    var code = AnyTagListNotUpToDate(
                        indexOut,
                        indexIn,
                        bufferLength,
                        bufferEmpty,
                        bufferFull,
                        configError,
                        indexError)
                        ? TagListNotUpToDateDiagnosticCode
                        : "PlcsimAdvanced.V8.Worker.EventBufferFifoControlReadFailed";
                    return EventBufferFifoReadResult.Failed(
                        code,
                        $"PLCSIM Advanced Runtime API could not read EventBuffer FifoControl: {failed.Message}");
                }

                return EventBufferFifoReadResult.Success(new PlcsimEventBufferFifoControlSnapshot(
                    indexOut.UInt16Value,
                    indexIn.UInt16Value,
                    bufferLength.UInt16Value,
                    bufferEmpty.BoolValue,
                    bufferFull.BoolValue,
                    configError.BoolValue,
                    indexError.BoolValue));
            }

            private EventBufferStatusReadResult ReadEventBufferStatus(string eventBufferRoot)
            {
                var sequenceId = ReadUInt32(eventBufferRoot + ".Status.SequenceId");
                var deletedSequenceId = ReadUInt32(eventBufferRoot + ".Status.DeletedSequenceId");
                var lostEventCount = ReadUInt16(eventBufferRoot + ".Status.LostEventCount");
                var failed = FirstFailed(sequenceId, deletedSequenceId, lostEventCount);
                if (failed is not null)
                {
                    var code = AnyTagListNotUpToDate(sequenceId, deletedSequenceId, lostEventCount)
                        ? TagListNotUpToDateDiagnosticCode
                        : "PlcsimAdvanced.V8.Worker.EventBufferStatusReadFailed";
                    return EventBufferStatusReadResult.Failed(
                        code,
                        $"PLCSIM Advanced Runtime API could not read EventBuffer Status: {failed.Message}");
                }

                return EventBufferStatusReadResult.Success(new PlcsimEventBufferStatusSnapshot(
                    sequenceId.UInt32Value,
                    deletedSequenceId.UInt32Value,
                    lostEventCount.UInt16Value));
            }

            private static StringReadResult? FirstFailed(params StringReadResult[] results)
            {
                return Array.Find(results, result => !result.Succeeded);
            }

            private static bool AnyTagListNotUpToDate(params ValueReadResult[] results)
            {
                return Array.Exists(
                    results,
                    result => !result.Succeeded && IsTagListNotUpToDate(result.ErrorCode));
            }

            private static bool AnyTagListNotUpToDate(params StringReadResult[] results)
            {
                return Array.Exists(
                    results,
                    result => !result.Succeeded && IsTagListNotUpToDate(result.ErrorCode));
            }
        }
    }
}
