using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8;

public sealed class PlcsimAdvancedV8AbortRealRuntimeTests
{
    private const string ValidationVariable = "PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V8_ABORT";
    private const string SeedVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_ABORT_SEED";
    private const string OutputVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_ABORT_OUTPUT";
    private const string ObservationStrategyVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_OBSERVATION_STRATEGY";
    private const string MonitoringIntervalVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MONITORING_INTERVAL_MS";
    private const string DrainIntervalVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_DRAIN_INTERVAL_MS";
    private const string MaxEventsPerBatchVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MAX_EVENTS_PER_BATCH";
    private const string RawEventLoggingVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_RAW_EVENT_LOGGING";
    private const int SuccessfulAttemptsPerWindow = 5;
    private const int MaximumAttempts = 60;

    [Fact]
    public void Abort_window_plan_is_seeded_and_balanced()
    {
        var first = CreateAbortWindowPlan(122);
        var second = CreateAbortWindowPlan(122);

        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.Equal(15, first.Count);
        Assert.Equal(5, first.Count(window => window == AbortTimingWindow.Early));
        Assert.Equal(5, first.Count(window => window == AbortTimingWindow.Middle));
        Assert.Equal(5, first.Count(window => window == AbortTimingWindow.Late));
    }

    [Theory]
    [InlineData(UnitTestEventType.Trigger, false)]
    [InlineData(UnitTestEventType.Info, false)]
    [InlineData(UnitTestEventType.Framework, false)]
    [InlineData(UnitTestEventType.StateTransition, true)]
    [InlineData(UnitTestEventType.Result, true)]
    [InlineData(UnitTestEventType.Aggregation, true)]
    public void Only_full_owner_lifecycle_evidence_defines_an_observed_lifecycle(
        UnitTestEventType eventType,
        bool expected)
    {
        var entry = new UnitTestEventEntry(
            1,
            DateTimeOffset.UnixEpoch,
            UnitTestOwnerScope.TestSuite,
            eventType,
            LogLevel.Information,
            "UnitTest.Suite",
            string.Empty,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated,
            "test");

        Assert.Equal(expected, IsObservedLifecycleEvent(entry));
    }

    [Fact]
    public async Task V8_binding_can_validate_clean_abort_and_follow_up_runs_when_enabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(ValidationVariable), "1", StringComparison.Ordinal))
        {
            return;
        }

        var seed = ReadPositiveInt(SeedVariable, 122);
        var outputPath = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.False(string.IsNullOrWhiteSpace(outputPath), $"{OutputVariable} is required.");
        var options = ReadWorkerOptions();
        var random = new Random(seed);
        var windows = new Queue<AbortTimingWindow>(CreateAbortWindowPlan(seed));
        var attempts = new List<AbortAttemptEvidence>();
        var successful = new List<AbortSequenceEvidence>();
        var recoveries = new List<AbortRecoveryEvidence>();
        var logDirectory = PlcsimAdvancedV8RealRuntimeTests.CreateTestLogDirectory();
        var rawEventLoggingEnabled = string.Equals(
            Environment.GetEnvironmentVariable(RawEventLoggingVariable),
            "1",
            StringComparison.Ordinal);
        var binding = new PlcsimAdvancedV8Binding();
        binding.Initialize(PlcsimAdvancedV8RealRuntimeTests.CreateBindingContext(logDirectory, rawEventLoggingEnabled));
        var open = await binding.OpenRuntimeSessionAsync(
            PlcsimAdvancedV8RealRuntimeTests.CreateOpenRequest(),
            CancellationToken.None);
        Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
        var runtime = Assert.IsAssignableFrom<IPlcsimRuntimeSession>(open.Session);
        var startedByHarness = false;
        double? calibrationActiveDurationMs = null;
        Exception? failure = null;

        try
        {
            var prepared = await runtime.PrepareRuntimeAsync(
                new PlcsimRuntimePrepareRequest(
                    new PlcsimRuntimeReference(null, "PlcUnderTest"),
                    new PlcsimRuntimeStoragePreference(null),
                    PlcsimRuntimeProvisioningMode.UseExisting,
                    PlcsimRuntimeCpuType.S71500,
                    TimeSpan.FromSeconds(30),
                    "abort-validation-prepare-runtime"),
                CancellationToken.None);
            Assert.True(prepared.Succeeded, FormatDiagnostics(prepared.Diagnostics, prepared.Evidence));
            Assert.True(
                prepared.Runtime?.State is PlcsimRuntimeControlState.Stop or PlcsimRuntimeControlState.Run,
                $"Expected an existing stopped or running PLC, observed {prepared.Runtime?.State}.");

            if (prepared.Runtime?.State != PlcsimRuntimeControlState.Run)
            {
                var startRuntime = await runtime.StartRuntimeAsync(
                    new PlcsimRuntimeControlRequest(
                        runtime.Runtime.SessionId,
                        TimeSpan.FromSeconds(60),
                        "abort-validation-start-runtime"),
                    CancellationToken.None);
                Assert.True(startRuntime.Succeeded, FormatDiagnostics(startRuntime.Diagnostics, startRuntime.Evidence));
                startedByHarness = true;
            }

            var result = await ValidateAbortLifecycleAsync(
                binding,
                runtime.Runtime.SessionId,
                options,
                random,
                windows,
                attempts,
                successful,
                recoveries);
            calibrationActiveDurationMs = result.CalibrationActiveDurationMs;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                if (startedByHarness)
                {
                    var observed = await runtime.ObserveRuntimeAsync(
                        new PlcsimRuntimeObservationRequest("abort-validation-cleanup-observe"),
                        CancellationToken.None);
                    if (observed.Runtime?.State == PlcsimRuntimeControlState.Run)
                    {
                        await runtime.StopRuntimeAsync(
                            new PlcsimRuntimeControlRequest(
                                runtime.Runtime.SessionId,
                                TimeSpan.FromSeconds(60),
                                "abort-validation-cleanup-stop"),
                            CancellationToken.None);
                    }
                }
            }
            catch
            {
                // Preserve the primary validation failure while still closing the worker session.
            }

            await runtime.CloseAsync(CancellationToken.None);
            WriteEvidence(
                outputPath!,
                seed,
                options,
                logDirectory,
                rawEventLoggingEnabled,
                calibrationActiveDurationMs,
                attempts,
                successful,
                recoveries,
                failure);
        }
    }

    private static async Task<AbortValidationResult> ValidateAbortLifecycleAsync(
        PlcsimAdvancedV8Binding binding,
        string runtimeSessionId,
        PlcsimCommunicationWorkerOptions options,
        Random random,
        Queue<AbortTimingWindow> windows,
        ICollection<AbortAttemptEvidence> attempts,
        ICollection<AbortSequenceEvidence> successful,
        ICollection<AbortRecoveryEvidence> recoveries)
    {
        var prepare = await binding.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                runtimeSessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                UnitTestContractVersions.EventStreamModel,
                options,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30),
                "abort-validation-prepare-communication"),
            CancellationToken.None);
        Assert.True(prepare.Succeeded, FormatDiagnostics(prepare.Diagnostics, prepare.Evidence));
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);
        var batches = new List<UnitTestEventBatch>();
        var monitoringStates = new List<PlcsimEventStreamMonitoringState>();
        var batchLock = new object();
        var monitoringLock = new object();
        communication.EventBatchReceived += (_, args) =>
        {
            lock (batchLock)
            {
                batches.Add(args.Batch);
            }
        };
        communication.EventStreamMonitoringStateChanged += (_, args) =>
        {
            lock (monitoringLock)
            {
                monitoringStates.Add(args.State);
            }
        };

        try
        {
            await ValidateContractsAsync(communication);
            var monitoring = await communication.StartEventStreamMonitoringAsync(
                new PlcsimEventStreamMonitoringRequest(
                    communication.Communication.SessionId,
                    "abort-validation-start-monitoring"),
                CancellationToken.None);
            Assert.True(
                monitoring.Succeeded && monitoring.State.IsMonitoring,
                FormatDiagnostics(monitoring.Diagnostics, monitoring.Evidence));

            var calibrationBoundary = await PrepareStartBoundaryAsync(
                communication,
                monitoringStates,
                monitoringLock,
                "abort-validation-calibration");
            var calibration = await ExecuteNormalRunAsync(
                communication,
                batches,
                batchLock,
                calibrationBoundary,
                "abort-validation-calibration-run");
            Assert.Equal(UnitTestResultState.Passed, calibration.Result);
            Assert.True(calibration.ActiveDurationMs > 0, "Calibration active duration must be positive.");

            var attemptNumber = 0;
            while (windows.Count > 0 && attemptNumber < MaximumAttempts)
            {
                attemptNumber++;
                var window = windows.Peek();
                var fraction = NextFraction(random, window);
                var boundary = await PrepareStartBoundaryAsync(
                    communication,
                    monitoringStates,
                    monitoringLock,
                    $"abort-validation-attempt-{attemptNumber}");
                var attempt = await ExecuteAbortAttemptAsync(
                    communication,
                    batches,
                    batchLock,
                    boundary,
                    window,
                    fraction,
                    calibration.ActiveDurationMs,
                    attemptNumber);
                attempts.Add(attempt.Evidence);

                if (!attempt.Aborted)
                {
                    Assert.Equal(UnitTestResultState.Passed, attempt.RunEvidence.Result);
                    continue;
                }

                Assert.Equal(UnitTestResultState.Abort, attempt.RunEvidence.Result);
                var abortStatus = await ObserveRunControlAsync(
                    communication,
                    $"abort-validation-observe-aborted-{attemptNumber}");
                Assert.Equal(UnitTestExecutionStatus.Completed, abortStatus.ExecutionStatus);
                Assert.True(abortStatus.Completed);
                Assert.True(abortStatus.Aborted);

                var followUpBoundary = await PrepareStartBoundaryAsync(
                    communication,
                    monitoringStates,
                    monitoringLock,
                    $"abort-validation-follow-up-{attemptNumber}");
                NormalRunResult followUp;
                try
                {
                    followUp = await ExecuteNormalRunAsync(
                        communication,
                        batches,
                        batchLock,
                        followUpBoundary,
                        $"abort-validation-follow-up-run-{attemptNumber}");
                }
                catch (LifecycleEvidenceValidationException exception)
                {
                    var recoveryBoundary = await PrepareStartBoundaryAsync(
                        communication,
                        monitoringStates,
                        monitoringLock,
                        $"abort-validation-recovery-{attemptNumber}");
                    var recovery = await ExecuteNormalRunAsync(
                        communication,
                        batches,
                        batchLock,
                        recoveryBoundary,
                        $"abort-validation-recovery-run-{attemptNumber}");
                    recoveries.Add(new AbortRecoveryEvidence(
                        attemptNumber,
                        window.ToString(),
                        ToRunEvidence(exception.Evidence),
                        ToRunEvidence(recovery),
                        true));
                    throw;
                }
                Assert.Equal(UnitTestResultState.Passed, followUp.Result);

                windows.Dequeue();
                successful.Add(new AbortSequenceEvidence(
                    successful.Count + 1,
                    window.ToString(),
                    attempt.Evidence,
                    ToRunEvidence(followUp)));
            }

            Assert.Empty(windows);
            Assert.Equal(15, successful.Count);
            return new AbortValidationResult(calibration.ActiveDurationMs);
        }
        finally
        {
            if (communication.Health == PlcsimCommunicationHealthState.Healthy)
            {
                var stop = await communication.StopEventStreamMonitoringAsync(
                    new PlcsimEventStreamStopRequest(
                        communication.Communication.SessionId,
                        "abort-validation-stop-monitoring"),
                    CancellationToken.None);
                Assert.True(stop.Succeeded, FormatDiagnostics(stop.Diagnostics, stop.Evidence));
            }

            await communication.CloseAsync(CancellationToken.None);
        }
    }

    private static async Task ValidateContractsAsync(IPlcsimUnitTestCommunicationSession communication)
    {
        var runControl = await communication.ValidateRunControlAsync(
            new PlcsimRunControlValidationRequest(
                communication.Communication.SessionId,
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                "abort-validation-runcontrol-contract"),
            CancellationToken.None);
        Assert.True(
            runControl.Succeeded && runControl.ContractValid,
            FormatDiagnostics(runControl.Diagnostics, runControl.Evidence));

        var eventBuffer = await communication.ValidateEventBufferAsync(
            new PlcsimEventBufferValidationRequest(
                communication.Communication.SessionId,
                UnitTestContractVersions.EventStreamModel,
                "abort-validation-eventbuffer-contract"),
            CancellationToken.None);
        Assert.True(
            eventBuffer.Succeeded && eventBuffer.ContractValid,
            FormatDiagnostics(eventBuffer.Diagnostics, eventBuffer.Evidence));
        Assert.Equal((ushort)0, eventBuffer.Observation.LostEventCount);
        Assert.True(
            eventBuffer.Observation.BufferObservedEmpty,
            "Abort validation requires an initially empty EventBuffer. Reset it explicitly before starting the harness.");
    }

    private static async Task<CleanBoundary> PrepareStartBoundaryAsync(
        IPlcsimUnitTestCommunicationSession communication,
        List<PlcsimEventStreamMonitoringState> monitoringStates,
        object monitoringLock,
        string correlationPrefix)
    {
        var current = await ObserveRunControlAsync(communication, correlationPrefix + "-observe-before-reset");
        var resetRequired = current.ExecutionStatus != UnitTestExecutionStatus.Idle || !current.Ready;
        var monitoringStartIndex = resetRequired ? GetCount(monitoringStates, monitoringLock) : 0;
        if (resetRequired)
        {
            var reset = await communication.RequestRunControlCommandAsync(
                new PlcsimRunControlCommandRequest(
                    communication.Communication.SessionId,
                    UnitTestRunCommand.Reset,
                    TimeSpan.FromSeconds(10),
                    correlationPrefix + "-reset"),
                CancellationToken.None);
            Assert.True(reset.Succeeded && reset.Accepted, FormatDiagnostics(reset.Diagnostics, reset.Evidence));
            await WaitForRunControlAsync(
                communication,
                observation => observation.ExecutionStatus == UnitTestExecutionStatus.Idle && observation.Ready,
                correlationPrefix + "-wait-ready",
                "Idle + Ready");
        }

        return await WaitForEmptyBoundaryAsync(
            monitoringStates,
            monitoringLock,
            monitoringStartIndex,
            correlationPrefix);
    }

    private static async Task<NormalRunResult> ExecuteNormalRunAsync(
        IPlcsimUnitTestCommunicationSession communication,
        List<UnitTestEventBatch> batches,
        object batchLock,
        CleanBoundary boundary,
        string correlationPrefix)
    {
        var batchStartIndex = GetCount(batches, batchLock);
        var runStopwatch = Stopwatch.StartNew();
        var start = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(10),
                correlationPrefix + "-start"),
            CancellationToken.None);
        Assert.True(start.Succeeded && start.Accepted, FormatDiagnostics(start.Diagnostics, start.Evidence));
        await WaitForRunControlAsync(
            communication,
            observation => observation.ExecutionStatus == UnitTestExecutionStatus.Running && observation.Running,
            correlationPrefix + "-wait-running",
            "Running");
        var runningObservedAtMs = runStopwatch.Elapsed.TotalMilliseconds;
        var evidence = await WaitForLifecycleEvidenceAsync(
            batches,
            batchLock,
            batchStartIndex,
            boundary.ProducerSequenceId,
            runStopwatch,
            correlationPrefix);
        ValidateLifecycleEvidence(evidence, UnitTestResultState.Passed);
        return new NormalRunResult(
            evidence.Result,
            Math.Max(0.1, evidence.CompletionObservedAtMs - runningObservedAtMs),
            evidence.TotalDurationMs,
            evidence.Entries);
    }

    private static async Task<AbortAttemptResult> ExecuteAbortAttemptAsync(
        IPlcsimUnitTestCommunicationSession communication,
        List<UnitTestEventBatch> batches,
        object batchLock,
        CleanBoundary boundary,
        AbortTimingWindow window,
        double targetFraction,
        double calibrationActiveDurationMs,
        int attemptNumber)
    {
        var correlationPrefix = $"abort-validation-attempt-{attemptNumber}";
        var batchStartIndex = GetCount(batches, batchLock);
        var runStopwatch = Stopwatch.StartNew();
        var start = await communication.RequestRunControlCommandAsync(
            new PlcsimRunControlCommandRequest(
                communication.Communication.SessionId,
                UnitTestRunCommand.Start,
                TimeSpan.FromSeconds(10),
                correlationPrefix + "-start"),
            CancellationToken.None);
        Assert.True(start.Succeeded && start.Accepted, FormatDiagnostics(start.Diagnostics, start.Evidence));
        await WaitForRunControlAsync(
            communication,
            observation => observation.ExecutionStatus == UnitTestExecutionStatus.Running && observation.Running,
            correlationPrefix + "-wait-running",
            "Running");
        var runningObservedAtMs = runStopwatch.Elapsed.TotalMilliseconds;
        var targetDelayMs = Math.Max(1, calibrationActiveDurationMs * targetFraction);
        await Task.Delay(TimeSpan.FromMilliseconds(targetDelayMs));

        var completionBeforeAbort = HasRunControlCompletion(
            batches,
            batchLock,
            batchStartIndex,
            boundary.ProducerSequenceId);
        DateTimeOffset? abortRequestedAtUtc = null;
        double? actualAbortDelayMs = null;
        if (!completionBeforeAbort)
        {
            abortRequestedAtUtc = DateTimeOffset.UtcNow;
            actualAbortDelayMs = Math.Max(0, runStopwatch.Elapsed.TotalMilliseconds - runningObservedAtMs);
            var abort = await communication.RequestRunControlCommandAsync(
                new PlcsimRunControlCommandRequest(
                    communication.Communication.SessionId,
                    UnitTestRunCommand.Abort,
                    TimeSpan.FromSeconds(10),
                    correlationPrefix + "-abort"),
                CancellationToken.None);
            Assert.True(abort.Succeeded && abort.Accepted, FormatDiagnostics(abort.Diagnostics, abort.Evidence));
        }

        var runEvidence = await WaitForLifecycleEvidenceAsync(
            batches,
            batchLock,
            batchStartIndex,
            boundary.ProducerSequenceId,
            runStopwatch,
            correlationPrefix);
        var aborted = runEvidence.Result == UnitTestResultState.Abort;
        if (aborted)
        {
            ValidateLifecycleEvidence(runEvidence, UnitTestResultState.Abort);
        }
        else
        {
            ValidateLifecycleEvidence(runEvidence, UnitTestResultState.Passed);
        }

        var attemptEvidence = new AbortAttemptEvidence(
            attemptNumber,
            window.ToString(),
            targetFraction,
            targetDelayMs,
            actualAbortDelayMs,
            abortRequestedAtUtc.HasValue,
            aborted,
            runEvidence.Result.ToString(),
            runEvidence.TotalDurationMs,
            runEvidence.Entries.Count,
            runEvidence.Entries.FirstOrDefault()?.SequenceId,
            runEvidence.Entries.LastOrDefault()?.SequenceId,
            runEvidence.FullOwnerCount,
            runEvidence.MaximumLostEvents,
            aborted ? "Aborted" : "NaturalCompletionWon");
        return new AbortAttemptResult(aborted, attemptEvidence, runEvidence);
    }

    private static async Task<LifecycleEvidence> WaitForLifecycleEvidenceAsync(
        List<UnitTestEventBatch> batches,
        object batchLock,
        int startIndex,
        uint previousSequenceId,
        Stopwatch runStopwatch,
        string correlationPrefix)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        double? completionObservedAtMs = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var observed = Snapshot(batches, batchLock, startIndex);
            var entries = observed
                .SelectMany(batch => batch.Events)
                .Where(entry => entry.SequenceId > previousSequenceId)
                .OrderBy(entry => entry.SequenceId)
                .ToArray();
            var maximumLostEvents = observed.Select(batch => batch.LostEventCount).DefaultIfEmpty((ushort)0).Max();
            Assert.Equal((ushort)0, maximumLostEvents);
            var completed = entries.LastOrDefault(IsRunControlCompletion);
            if (completed is not null && !completionObservedAtMs.HasValue)
            {
                completionObservedAtMs = runStopwatch.Elapsed.TotalMilliseconds;
            }

            var emptyAfterCompletion = completed is not null
                && observed.Any(batch =>
                    batch.BufferObservedEmpty
                    && batch.Events.Count == 0
                    && batch.ProducerSequenceId >= completed.SequenceId
                    && batch.ProducerSequenceId == batch.DeletedSequenceId);
            if (emptyAfterCompletion)
            {
                var result = entries
                    .LastOrDefault(entry =>
                        entry.OwnerScope == UnitTestOwnerScope.RunControl
                        && entry.EventType == UnitTestEventType.Result)
                    ?.Result ?? UnitTestResultState.NotEvaluated;
                return new LifecycleEvidence(
                    result,
                    completionObservedAtMs ?? runStopwatch.Elapsed.TotalMilliseconds,
                    runStopwatch.Elapsed.TotalMilliseconds,
                    entries,
                    entries.Where(IsObservedLifecycleEvent).Select(OwnerKey.From).Distinct().Count(),
                    maximumLostEvents);
            }

            await Task.Delay(20);
        }

        Assert.Fail($"{correlationPrefix} did not reach RunControl completion plus an empty EventBuffer within 60 seconds.");
        throw new InvalidOperationException();
    }

    private static void ValidateLifecycleEvidence(
        LifecycleEvidence evidence,
        UnitTestResultState expectedRunControlResult)
    {
        try
        {
            ValidateLifecycleEvidence(evidence.Entries, expectedRunControlResult);
        }
        catch (Exception exception)
        {
            throw new LifecycleEvidenceValidationException(expectedRunControlResult, evidence, exception);
        }
    }

    private static void ValidateLifecycleEvidence(
        IReadOnlyList<UnitTestEventEntry> entries,
        UnitTestResultState expectedRunControlResult)
    {
        Assert.NotEmpty(entries);
        Assert.Equal(entries.Count, entries.Select(entry => entry.SequenceId).Distinct().Count());
        Assert.True(entries.SequenceEqual(entries.OrderBy(entry => entry.SequenceId)));
        Assert.Contains(entries, IsRunControlCompletion);
        var runControlResult = Assert.Single(entries.Where(entry =>
            entry.OwnerScope == UnitTestOwnerScope.RunControl
            && entry.EventType == UnitTestEventType.Result));
        Assert.Equal(expectedRunControlResult, runControlResult.Result);
        Assert.Single(entries.Where(entry =>
            entry.OwnerScope == UnitTestOwnerScope.RunControl
            && entry.EventType == UnitTestEventType.Aggregation));

        var owners = entries
            .Where(IsObservedLifecycleEvent)
            .Select(OwnerKey.From)
            .Distinct()
            .ToArray();
        Assert.Contains(owners, owner => owner.Scope == UnitTestOwnerScope.RunControl);
        foreach (var owner in owners)
        {
            var ownerEntries = entries.Where(entry => owner.Equals(OwnerKey.From(entry))).ToArray();
            Assert.Contains(ownerEntries, entry =>
                entry.EventType == UnitTestEventType.StateTransition
                && entry.State == UnitTestLifecycleState.Completed);
            Assert.Contains(ownerEntries, entry => entry.EventType == UnitTestEventType.Result);
            Assert.Contains(ownerEntries, entry => entry.EventType == UnitTestEventType.Aggregation);
        }
    }

    private static async Task<CleanBoundary> WaitForEmptyBoundaryAsync(
        List<PlcsimEventStreamMonitoringState> states,
        object stateLock,
        int startIndex,
        string correlationPrefix)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (stateLock)
            {
                var state = states
                    .Skip(startIndex)
                    .LastOrDefault(item =>
                        item.IsMonitoring
                        && item.LastReadState == EventBufferReadState.Empty
                        && item.BatchReleaseState == EventStreamBatchReleaseState.Idle
                        && item.BufferObservedEmpty
                        && item.LostEventCount == 0
                        && item.ProducerSequenceId == item.DeletedSequenceId);
                if (state is not null)
                {
                    return new CleanBoundary(state.ProducerSequenceId, state.DeletedSequenceId);
                }
            }

            await Task.Delay(20);
        }

        Assert.Fail($"{correlationPrefix} did not reach a new empty, loss-free EventBuffer boundary.");
        throw new InvalidOperationException();
    }

    private static async Task<PlcsimRunControlObservation> WaitForRunControlAsync(
        IPlcsimUnitTestCommunicationSession communication,
        Func<PlcsimRunControlObservation, bool> predicate,
        string correlationId,
        string expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        PlcsimRunControlObservation? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            latest = await ObserveRunControlAsync(communication, correlationId);
            if (predicate(latest))
            {
                return latest;
            }

            await Task.Delay(20);
        }

        Assert.Fail(
            $"RunControl did not reach {expected}. Latest: "
            + (latest is null
                ? "<none>"
                : $"{latest.ExecutionStatus}/{latest.State}/{latest.ExecutionSignals}/{latest.Result}"));
        throw new InvalidOperationException();
    }

    private static async Task<PlcsimRunControlObservation> ObserveRunControlAsync(
        IPlcsimUnitTestCommunicationSession communication,
        string correlationId)
    {
        var result = await communication.ObserveRunControlAsync(
            new PlcsimRunControlObservationRequest(communication.Communication.SessionId, correlationId),
            CancellationToken.None);
        Assert.True(
            result.Succeeded && result.Observation is not null,
            FormatDiagnostics(result.Diagnostics, result.Evidence));
        return result.Observation!;
    }

    private static bool HasRunControlCompletion(
        List<UnitTestEventBatch> batches,
        object batchLock,
        int startIndex,
        uint previousSequenceId) =>
        Snapshot(batches, batchLock, startIndex)
            .SelectMany(batch => batch.Events)
            .Any(entry => entry.SequenceId > previousSequenceId && IsRunControlCompletion(entry));

    private static bool IsRunControlCompletion(UnitTestEventEntry entry) =>
        entry.OwnerScope == UnitTestOwnerScope.RunControl
        && entry.EventType == UnitTestEventType.StateTransition
        && entry.State == UnitTestLifecycleState.Completed;

    private static bool IsFullOwnerEvent(UnitTestEventEntry entry) =>
        entry.OwnerScope is UnitTestOwnerScope.RunControl or UnitTestOwnerScope.TestSuite or UnitTestOwnerScope.TestCase;

    private static bool IsObservedLifecycleEvent(UnitTestEventEntry entry) =>
        IsFullOwnerEvent(entry)
        && entry.EventType is UnitTestEventType.StateTransition or UnitTestEventType.Result or UnitTestEventType.Aggregation;

    private static T[] Snapshot<T>(List<T> source, object sourceLock, int startIndex)
    {
        lock (sourceLock)
        {
            return source.Skip(startIndex).ToArray();
        }
    }

    private static int GetCount<T>(List<T> source, object sourceLock)
    {
        lock (sourceLock)
        {
            return source.Count;
        }
    }

    internal static IReadOnlyList<AbortTimingWindow> CreateAbortWindowPlan(int seed)
    {
        var random = new Random(seed);
        var windows = Enum.GetValues<AbortTimingWindow>()
            .SelectMany(window => Enumerable.Repeat(window, SuccessfulAttemptsPerWindow))
            .ToArray();
        for (var index = windows.Length - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (windows[index], windows[swapIndex]) = (windows[swapIndex], windows[index]);
        }

        return windows;
    }

    private static double NextFraction(Random random, AbortTimingWindow window)
    {
        var (minimum, maximum) = window switch
        {
            AbortTimingWindow.Early => (0.02, 0.20),
            AbortTimingWindow.Middle => (0.30, 0.55),
            AbortTimingWindow.Late => (0.65, 0.85),
            _ => throw new ArgumentOutOfRangeException(nameof(window), window, null)
        };
        return minimum + ((maximum - minimum) * random.NextDouble());
    }

    private static PlcsimCommunicationWorkerOptions ReadWorkerOptions()
    {
        var strategyText = Environment.GetEnvironmentVariable(ObservationStrategyVariable);
        var strategy = string.Equals(strategyText, "PlcCycle", StringComparison.OrdinalIgnoreCase)
            ? PlcsimCommunicationObservationStrategyKind.PlcCycle
            : PlcsimCommunicationObservationStrategyKind.Polling;
        Assert.True(
            string.IsNullOrWhiteSpace(strategyText)
            || string.Equals(strategyText, "Polling", StringComparison.OrdinalIgnoreCase)
            || string.Equals(strategyText, "PlcCycle", StringComparison.OrdinalIgnoreCase),
            $"{ObservationStrategyVariable} must be 'Polling' or 'PlcCycle'.");
        var monitoringInterval = ReadPositiveInt(
            MonitoringIntervalVariable,
            strategy == PlcsimCommunicationObservationStrategyKind.Polling ? 10 : 0);
        var configuredMonitoringInterval = monitoringInterval > 0
            ? TimeSpan.FromMilliseconds(monitoringInterval)
            : (TimeSpan?)null;
        return new PlcsimCommunicationWorkerOptions(strategy, configuredMonitoringInterval)
        {
            DrainInterval = TimeSpan.FromMilliseconds(ReadPositiveInt(DrainIntervalVariable, 1)),
            MaxEventsPerBatch = ReadPositiveInt(MaxEventsPerBatchVariable, 32)
        };
    }

    private static int ReadPositiveInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        Assert.True(int.TryParse(value, out var parsed) && parsed > 0, $"{name} must be a positive integer.");
        return parsed;
    }

    private static string FormatDiagnostics(
        IReadOnlyList<PlcsimDiagnostic> diagnostics,
        IReadOnlyList<PlcsimEvidence> evidence) =>
        "Diagnostics: "
        + (diagnostics.Count == 0
            ? "<none>"
            : string.Join(" | ", diagnostics.Select(item => $"{item.Code}: {item.Message} ({item.Phase})")))
        + Environment.NewLine
        + "Evidence: "
        + (evidence.Count == 0
            ? "<none>"
            : string.Join(" | ", evidence.Select(item => $"{item.Kind}: {item.Summary}")));

    private static AbortRunEvidence ToRunEvidence(NormalRunResult run) =>
        new(
            run.Result.ToString(),
            run.TotalDurationMs,
            run.Entries.Count,
            run.Entries.FirstOrDefault()?.SequenceId,
            run.Entries.LastOrDefault()?.SequenceId,
            run.Entries.Where(IsObservedLifecycleEvent).Select(OwnerKey.From).Distinct().Count(),
            0);

    private static AbortRunEvidence ToRunEvidence(LifecycleEvidence run) =>
        new(
            run.Result.ToString(),
            run.TotalDurationMs,
            run.Entries.Count,
            run.Entries.FirstOrDefault()?.SequenceId,
            run.Entries.LastOrDefault()?.SequenceId,
            run.FullOwnerCount,
            run.MaximumLostEvents);

    private static void WriteEvidence(
        string outputPath,
        int seed,
        PlcsimCommunicationWorkerOptions options,
        string logDirectory,
        bool rawEventLoggingEnabled,
        double? calibrationActiveDurationMs,
        IReadOnlyCollection<AbortAttemptEvidence> attempts,
        IReadOnlyCollection<AbortSequenceEvidence> successful,
        IReadOnlyCollection<AbortRecoveryEvidence> recoveries,
        Exception? failure)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var lifecycleFailure = failure as LifecycleEvidenceValidationException;
        var evidence = new AbortValidationEvidence(
            1,
            DateTimeOffset.UtcNow,
            seed,
            options.ObservationStrategy.ToString(),
            options.MonitoringInterval?.TotalMilliseconds,
            options.DrainInterval?.TotalMilliseconds,
            options.MaxEventsPerBatch,
            rawEventLoggingEnabled,
            logDirectory,
            calibrationActiveDurationMs,
            successful.Count == 15 && failure is null,
            failure?.GetType().FullName,
            failure?.Message,
            lifecycleFailure is null
                ? null
                : new FailedLifecycleEvidence(
                    lifecycleFailure.ExpectedResult.ToString(),
                    lifecycleFailure.Evidence.Result.ToString(),
                    lifecycleFailure.Evidence.TotalDurationMs,
                    lifecycleFailure.Evidence.MaximumLostEvents,
                    lifecycleFailure.Evidence.Entries),
            attempts.ToArray(),
            successful.ToArray(),
            recoveries.ToArray());
        var serializerOptions = new JsonSerializerOptions { WriteIndented = true };
        serializerOptions.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(evidence, serializerOptions));
        Console.WriteLine($"Abort lifecycle validation evidence written: {fullPath}");
    }

    internal enum AbortTimingWindow
    {
        Early,
        Middle,
        Late
    }

    private sealed record OwnerKey(UnitTestOwnerScope Scope, string Name, string ResourceName)
    {
        public static OwnerKey From(UnitTestEventEntry entry) => new(entry.OwnerScope, entry.Name, entry.ResourceName);
    }

    private sealed record CleanBoundary(uint ProducerSequenceId, uint DeletedSequenceId);
    private sealed record AbortValidationResult(double CalibrationActiveDurationMs);
    private sealed record NormalRunResult(
        UnitTestResultState Result,
        double ActiveDurationMs,
        double TotalDurationMs,
        IReadOnlyList<UnitTestEventEntry> Entries);
    private sealed record LifecycleEvidence(
        UnitTestResultState Result,
        double CompletionObservedAtMs,
        double TotalDurationMs,
        IReadOnlyList<UnitTestEventEntry> Entries,
        int FullOwnerCount,
        ushort MaximumLostEvents);
    private sealed class LifecycleEvidenceValidationException : Exception
    {
        public LifecycleEvidenceValidationException(
            UnitTestResultState expectedResult,
            LifecycleEvidence evidence,
            Exception innerException)
            : base(
                $"Lifecycle evidence validation failed. Expected RunControl result '{expectedResult}', observed '{evidence.Result}'.",
                innerException)
        {
            ExpectedResult = expectedResult;
            Evidence = evidence;
        }

        public UnitTestResultState ExpectedResult { get; }
        public LifecycleEvidence Evidence { get; }
    }
    private sealed record AbortAttemptResult(
        bool Aborted,
        AbortAttemptEvidence Evidence,
        LifecycleEvidence RunEvidence);
    private sealed record AbortValidationEvidence(
        int SchemaVersion,
        DateTimeOffset RecordedAtUtc,
        int Seed,
        string ObservationStrategy,
        double? MonitoringIntervalMs,
        double? DrainIntervalMs,
        int? MaxEventsPerBatch,
        bool RawEventLoggingEnabled,
        string LogDirectory,
        double? CalibrationActiveDurationMs,
        bool Completed,
        string? FailureType,
        string? FailureMessage,
        FailedLifecycleEvidence? FailedLifecycle,
        IReadOnlyList<AbortAttemptEvidence> Attempts,
        IReadOnlyList<AbortSequenceEvidence> SuccessfulSequences,
        IReadOnlyList<AbortRecoveryEvidence> Recoveries);
    private sealed record FailedLifecycleEvidence(
        string ExpectedRunControlResult,
        string ActualRunControlResult,
        double TotalDurationMs,
        ushort MaximumLostEvents,
        IReadOnlyList<UnitTestEventEntry> Entries);
    private sealed record AbortSequenceEvidence(
        int Sequence,
        string Window,
        AbortAttemptEvidence Abort,
        AbortRunEvidence FollowUpRun);
    private sealed record AbortRecoveryEvidence(
        int Attempt,
        string Window,
        AbortRunEvidence FailedFollowUpRun,
        AbortRunEvidence RecoveryRun,
        bool SameSession);
    private sealed record AbortAttemptEvidence(
        int Attempt,
        string Window,
        double TargetFraction,
        double TargetDelayMs,
        double? ActualAbortDelayMs,
        bool AbortRequested,
        bool Aborted,
        string RunResult,
        double RunDurationMs,
        int EventCount,
        uint? FirstSequenceId,
        uint? LastSequenceId,
        int FullOwnerCount,
        ushort MaximumLostEvents,
        string Outcome);
    private sealed record AbortRunEvidence(
        string RunResult,
        double RunDurationMs,
        int EventCount,
        uint? FirstSequenceId,
        uint? LastSequenceId,
        int FullOwnerCount,
        ushort MaximumLostEvents);
}
