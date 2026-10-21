using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V8;

public sealed class PlcsimAdvancedV8RealRuntimeTests
{
    private const string RealCreateOwnedValidationVariable = "PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V8_CREATEOWNED";
    private const string RealExistingStartStopValidationVariable = "PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V8_EXISTING_STARTSTOP";
    private const string RealExistingCommunicationValidationVariable = "PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V8_COMMUNICATION";
    private const string RealCommunicationPollIntervalVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_POLL_INTERVAL_MS";
    private const string RealCommunicationMonitoringIntervalVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MONITORING_INTERVAL_MS";
    private const string RealCommunicationDrainIntervalVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_DRAIN_INTERVAL_MS";
    private const string RealCommunicationMaxEventsPerBatchVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_MAX_EVENTS_PER_BATCH";
    private const string RealCommunicationRawLoggingVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_RAW_EVENT_LOGGING";
    private const string RealCommunicationCapacityMeasurementVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_MEASUREMENT";
    private const string RealCommunicationCapacityOutputVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_OUTPUT";
    private const string RealCommunicationCapacityRunCountVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_RUN_COUNT";
    private const string RealCommunicationCapacityWorkloadIdVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_WORKLOAD_ID";
    private const string RealCommunicationCapacityMeasurementRoleVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CAPACITY_MEASUREMENT_ROLE";
    private const string RealCommunicationObservationStrategyVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_OBSERVATION_STRATEGY";
    private const string RealCommunicationConfiguredPlcMinimumCycleTimeVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_CONFIGURED_PLC_MINIMUM_CYCLE_TIME_MS";
    private const string RealCommunicationSourceRevisionVariable = "PLCSIM_UNITTEST_SOURCE_REVISION";
    private const string RealEventReadMeasurementVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT";
    private const string RealEventReadMeasurementModeVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT_MODE";
    private const string RealEventReadMeasurementTargetVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT_TARGET";
    private const string RealEventReadMeasurementOutputVariable = "PLCSIM_UNITTEST_PLCSIM_ADVANCED_V8_EVENT_READ_MEASUREMENT_OUTPUT";
    private const string RealLifetimeValidationVariable = "PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V8_LIFETIMES";

    [Fact]
    public async Task V8_binding_can_validate_real_create_owned_lifecycle_when_enabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(RealCreateOwnedValidationVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var instanceName = "PUT" + Guid.NewGuid().ToString("N").Substring(0, 12);
        var storagePath = Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            "runtimes",
            instanceName);
        var binding = new PlcsimAdvancedV8Binding();
        binding.Initialize(CreateBindingContext());
        var open = await binding.OpenRuntimeSessionAsync(
            new PlcsimRuntimeSessionOpenRequest(
                new PlcsimRuntimeApiExpectation("8.0", "8.0", SiemensRuntimeLocality.Local),
                new PlcsimRuntimeReference(null, instanceName),
                new PlcsimRuntimeStoragePreference(storagePath),
                PlcsimRuntimeCleanupPreference.Release,
                "real-create-owned-validation"),
            CancellationToken.None);

        Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
        Assert.NotNull(open.Session);
        var session = open.Session;
        var concreteSession = Assert.IsType<PlcsimAdvancedV8RuntimeSession>(session);
        var workerSessionId = session.Runtime.SessionId;
        var workerProcessId = Assert.IsType<int>(concreteSession.WorkerProcessId);
        try
        {
            var prepared = await session.PrepareRuntimeAsync(
                new PlcsimRuntimePrepareRequest(
                    new PlcsimRuntimeReference(null, instanceName),
                    new PlcsimRuntimeStoragePreference(storagePath),
                    PlcsimRuntimeProvisioningMode.CreateOwned,
                    PlcsimRuntimeCpuType.S71500,
                    TimeSpan.FromSeconds(30),
                    "real-create-owned-validation"),
                CancellationToken.None);

            Assert.True(prepared.Succeeded, FormatDiagnostics(prepared.Diagnostics, prepared.Evidence));
            Assert.NotNull(prepared.Runtime);
            Assert.True(prepared.Runtime.IsOwned);
            Assert.Equal(instanceName, prepared.Runtime.Reference.InstanceName);
            Assert.NotEqual(PlcsimRuntimeControlState.Off, prepared.Runtime.State);
            var preparedReference = prepared.Runtime.Reference;

            var revalidated = await session.RevalidateRuntimeAsync(
                new PlcsimRuntimeRevalidationRequest(
                    preparedReference,
                    TimeSpan.FromSeconds(30),
                    "real-create-owned-reconnect-validation"),
                CancellationToken.None);

            Assert.True(revalidated.Succeeded, FormatDiagnostics(revalidated.Diagnostics, revalidated.Evidence));
            Assert.Equal(workerSessionId, session.Runtime.SessionId);
            Assert.Equal(workerProcessId, concreteSession.WorkerProcessId);
            Assert.Equal(preparedReference, revalidated.Runtime?.Reference);
            Assert.True(revalidated.Runtime?.IsOwned == true);

            var released = await session.ReleaseRuntimeAsync(
                new PlcsimRuntimeReleaseRequest(
                    session.Runtime.SessionId,
                    PlcsimRuntimeCleanupPreference.Release,
                    TimeSpan.FromSeconds(60),
                    "real-create-owned-validation"),
                CancellationToken.None);

            Assert.True(released.Succeeded, FormatDiagnostics(released.Diagnostics, released.Evidence));
            Assert.True(released.Released);
            Assert.True(released.StorageCleaned);

            var rediscovery = await session.DiscoverRuntimeTargetsAsync(
                new PlcsimRuntimeDiscoveryRequest("real-create-owned-validation"),
                CancellationToken.None);
            Assert.DoesNotContain(
                rediscovery.Candidates,
                candidate => string.Equals(candidate.Reference.InstanceName, instanceName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task V8_binding_can_validate_real_existing_runtime_start_stop_when_enabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(RealExistingStartStopValidationVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var binding = new PlcsimAdvancedV8Binding();
        binding.Initialize(CreateBindingContext());
        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
        Assert.NotNull(open.Session);
        var session = open.Session!;
        var concreteSession = Assert.IsType<PlcsimAdvancedV8RuntimeSession>(session);
        var workerSessionId = session.Runtime.SessionId;
        var workerProcessId = Assert.IsType<int>(concreteSession.WorkerProcessId);
        var changes = new List<PlcsimRuntimeStateChange>();
        var changeLock = new object();
        session.RuntimeStateChanged += (_, args) =>
        {
            lock (changeLock)
            {
                changes.Add(args.Change);
            }
        };

        try
        {
            var prepared = await session.PrepareRuntimeAsync(
                new PlcsimRuntimePrepareRequest(
                    new PlcsimRuntimeReference(null, "PlcUnderTest"),
                    new PlcsimRuntimeStoragePreference(null),
                    PlcsimRuntimeProvisioningMode.UseExisting,
                    PlcsimRuntimeCpuType.S71500,
                    TimeSpan.FromSeconds(30),
                    "real-existing-start-stop-validation"),
                CancellationToken.None);
            Assert.True(prepared.Succeeded, FormatDiagnostics(prepared.Diagnostics, prepared.Evidence));
            Assert.NotNull(prepared.Runtime);
            Assert.Equal("PlcUnderTest", prepared.Runtime.Reference.InstanceName);

            var observedBeforeStart = await session.ObserveRuntimeAsync(
                new PlcsimRuntimeObservationRequest("real-existing-start-stop-validation"),
                CancellationToken.None);
            Assert.True(observedBeforeStart.Succeeded, FormatDiagnostics(observedBeforeStart.Diagnostics, observedBeforeStart.Evidence));

            if (observedBeforeStart.Runtime?.State == PlcsimRuntimeControlState.Run)
            {
                var initialStop = await session.StopRuntimeAsync(
                    new PlcsimRuntimeControlRequest(
                        session.Runtime.SessionId,
                        TimeSpan.FromSeconds(60),
                        "real-existing-start-stop-validation-initial-stop"),
                    CancellationToken.None);
                Assert.True(initialStop.Succeeded, FormatDiagnostics(initialStop.Diagnostics, initialStop.Evidence));
                await WaitForRuntimeStateAsync(changes, changeLock, PlcsimRuntimeControlState.Stop);
                lock (changeLock)
                {
                    changes.Clear();
                }
            }

            var started = await session.StartRuntimeAsync(
                new PlcsimRuntimeControlRequest(
                    session.Runtime.SessionId,
                    TimeSpan.FromSeconds(60),
                    "real-existing-start-stop-validation"),
                CancellationToken.None);
            Assert.True(started.Succeeded, FormatDiagnostics(started.Diagnostics, started.Evidence));
            Assert.Equal(PlcsimRuntimeControlState.Run, started.State);
            await WaitForRuntimeStateAsync(changes, changeLock, PlcsimRuntimeControlState.Run);

            var observedAfterStart = await session.ObserveRuntimeAsync(
                new PlcsimRuntimeObservationRequest("real-existing-start-stop-validation"),
                CancellationToken.None);
            Assert.True(observedAfterStart.Succeeded, FormatDiagnostics(observedAfterStart.Diagnostics, observedAfterStart.Evidence));
            Assert.Equal(PlcsimRuntimeControlState.Run, observedAfterStart.Runtime?.State);
            var preparedReference = Assert.IsType<PlcsimRuntimeTargetCandidate>(prepared.Runtime).Reference;

            var runningRevalidation = await session.RevalidateRuntimeAsync(
                new PlcsimRuntimeRevalidationRequest(
                    preparedReference,
                    TimeSpan.FromSeconds(30),
                    "real-existing-running-reconnect-validation"),
                CancellationToken.None);

            Assert.True(
                runningRevalidation.Succeeded,
                FormatDiagnostics(runningRevalidation.Diagnostics, runningRevalidation.Evidence));
            Assert.Equal(workerSessionId, session.Runtime.SessionId);
            Assert.Equal(workerProcessId, concreteSession.WorkerProcessId);
            Assert.Equal(preparedReference, runningRevalidation.Runtime?.Reference);
            Assert.Equal(PlcsimRuntimeControlState.Run, runningRevalidation.Runtime?.State);
            Assert.True(runningRevalidation.Runtime?.IsOwned == false);

            var stopped = await session.StopRuntimeAsync(
                new PlcsimRuntimeControlRequest(
                    session.Runtime.SessionId,
                    TimeSpan.FromSeconds(60),
                    "real-existing-start-stop-validation"),
                CancellationToken.None);
            Assert.True(stopped.Succeeded, FormatDiagnostics(stopped.Diagnostics, stopped.Evidence));
            Assert.Equal(PlcsimRuntimeControlState.Stop, stopped.State);
            await WaitForRuntimeStateAsync(changes, changeLock, PlcsimRuntimeControlState.Stop);

            var observedAfterStop = await session.ObserveRuntimeAsync(
                new PlcsimRuntimeObservationRequest("real-existing-start-stop-validation"),
                CancellationToken.None);
            Assert.True(observedAfterStop.Succeeded, FormatDiagnostics(observedAfterStop.Diagnostics, observedAfterStop.Evidence));
            Assert.Equal(PlcsimRuntimeControlState.Stop, observedAfterStop.Runtime?.State);

            var stoppedRevalidation = await session.RevalidateRuntimeAsync(
                new PlcsimRuntimeRevalidationRequest(
                    preparedReference,
                    TimeSpan.FromSeconds(30),
                    "real-existing-stopped-reconnect-validation"),
                CancellationToken.None);

            Assert.True(
                stoppedRevalidation.Succeeded,
                FormatDiagnostics(stoppedRevalidation.Diagnostics, stoppedRevalidation.Evidence));
            Assert.Equal(workerSessionId, session.Runtime.SessionId);
            Assert.Equal(workerProcessId, concreteSession.WorkerProcessId);
            Assert.Equal(preparedReference, stoppedRevalidation.Runtime?.Reference);
            Assert.Equal(PlcsimRuntimeControlState.Stop, stoppedRevalidation.Runtime?.State);
            Assert.True(stoppedRevalidation.Runtime?.IsOwned == false);

            var rediscovery = await session.DiscoverRuntimeTargetsAsync(
                new PlcsimRuntimeDiscoveryRequest("real-existing-reconnect-validation"),
                CancellationToken.None);
            Assert.Contains(
                rediscovery.Candidates,
                candidate => string.Equals(
                    candidate.Reference.InstanceName,
                    "PlcUnderTest",
                    StringComparison.OrdinalIgnoreCase));

            var failedRevalidation = await session.RevalidateRuntimeAsync(
                new PlcsimRuntimeRevalidationRequest(
                    new PlcsimRuntimeReference(preparedReference.Id, "UnexpectedRuntime"),
                    TimeSpan.FromSeconds(30),
                    "real-existing-failed-reconnect-validation"),
                CancellationToken.None);

            Assert.False(failedRevalidation.Succeeded);
            Assert.Equal(PlcsimRuntimeHealthState.Invalidated, session.Health);
            Assert.Contains(
                failedRevalidation.Diagnostics,
                diagnostic => diagnostic.Code == "PlcsimAdvanced.V8.Worker.RuntimeIdentityMismatch");
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }

        var verificationOpen = await binding.OpenRuntimeSessionAsync(
            CreateOpenRequest(),
            CancellationToken.None);
        Assert.True(
            verificationOpen.Succeeded,
            FormatDiagnostics(verificationOpen.Diagnostics, verificationOpen.Evidence));
        var verificationSession = Assert.IsAssignableFrom<IPlcsimRuntimeSession>(verificationOpen.Session);
        try
        {
            var verificationDiscovery = await verificationSession.DiscoverRuntimeTargetsAsync(
                new PlcsimRuntimeDiscoveryRequest("real-existing-post-failure-cleanup-validation"),
                CancellationToken.None);
            Assert.True(
                verificationDiscovery.Succeeded,
                FormatDiagnostics(verificationDiscovery.Diagnostics, verificationDiscovery.Evidence));
            Assert.Contains(
                verificationDiscovery.Candidates,
                candidate => string.Equals(
                    candidate.Reference.InstanceName,
                    "PlcUnderTest",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await verificationSession.CloseAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task V8_binding_can_validate_real_existing_runtime_unit_test_communication_when_enabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(RealExistingCommunicationValidationVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var observationStrategy = ReadObservationStrategyEnvironmentVariable();
        var monitoringInterval = ReadRealCommunicationMonitoringInterval(observationStrategy);
        var drainInterval = ReadPositiveMillisecondsEnvironmentVariable(
            RealCommunicationDrainIntervalVariable,
            monitoringInterval ?? TimeSpan.FromMilliseconds(10));
        var maxEventsPerBatch = ReadPositiveIntEnvironmentVariable(
            RealCommunicationMaxEventsPerBatchVariable,
            defaultValue: null);
        var rawEventLoggingEnabled = string.Equals(
            Environment.GetEnvironmentVariable(RealCommunicationRawLoggingVariable),
            "1",
            StringComparison.Ordinal);
        var capacityMeasurementEnabled = string.Equals(
            Environment.GetEnvironmentVariable(RealCommunicationCapacityMeasurementVariable),
            "1",
            StringComparison.Ordinal);
        var capacityOutputPath = Environment.GetEnvironmentVariable(RealCommunicationCapacityOutputVariable);
        var configuredPlcMinimumCycleTimeMs = ReadPositiveIntEnvironmentVariable(
            RealCommunicationConfiguredPlcMinimumCycleTimeVariable,
            defaultValue: null);
        var capacityRunCount = ReadPositiveIntEnvironmentVariable(
            RealCommunicationCapacityRunCountVariable,
            defaultValue: 10)!.Value;
        var capacityWorkloadId = Environment.GetEnvironmentVariable(RealCommunicationCapacityWorkloadIdVariable) ?? string.Empty;
        var capacityMeasurementRole = Environment.GetEnvironmentVariable(RealCommunicationCapacityMeasurementRoleVariable) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(capacityOutputPath))
        {
            Assert.True(capacityMeasurementEnabled, $"{RealCommunicationCapacityOutputVariable} requires capacity measurement mode.");
            Assert.NotNull(configuredPlcMinimumCycleTimeMs);
            Assert.False(string.IsNullOrWhiteSpace(capacityWorkloadId), $"{RealCommunicationCapacityWorkloadIdVariable} is required for capacity evidence.");
            Assert.False(string.IsNullOrWhiteSpace(capacityMeasurementRole), $"{RealCommunicationCapacityMeasurementRoleVariable} is required for capacity evidence.");
        }

        var sourceRevision = Environment.GetEnvironmentVariable(RealCommunicationSourceRevisionVariable) ?? string.Empty;
        var capacityRuns = new List<RealEventStreamRunMeasurement>();
        RealCapacityContractEvidence? capacityContracts = null;
        var eventBufferResetBeforeMeasurement = false;
        var logDirectory = CreateTestLogDirectory();
        var binding = new PlcsimAdvancedV8Binding();
        binding.Initialize(CreateBindingContext(logDirectory, rawEventLoggingEnabled));
        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
        Assert.NotNull(open.Session);
        var session = open.Session!;
        var concreteSession = Assert.IsType<PlcsimAdvancedV8RuntimeSession>(session);
        var workerProcessId = Assert.IsType<int>(concreteSession.WorkerProcessId);
        var monitoringIntervalText = monitoringInterval is { } configuredMonitoringInterval
            ? configuredMonitoringInterval.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
            : "<every-plc-cycle>";
        Console.WriteLine(
            $"Real EventStream validation configuration: observationStrategy={observationStrategy}, monitoringIntervalMs={monitoringIntervalText}, drainIntervalMs={drainInterval.TotalMilliseconds:F0}, maxEventsPerBatch={maxEventsPerBatch?.ToString() ?? "<buffer-length>"}, rawEventLogging={rawEventLoggingEnabled}, capacityMeasurement={capacityMeasurementEnabled}, workerProcessId={workerProcessId}");
        var runtimeChanges = new List<PlcsimRuntimeStateChange>();
        var runtimeChangeLock = new object();
        session.RuntimeStateChanged += (_, args) =>
        {
            lock (runtimeChangeLock)
            {
                runtimeChanges.Add(args.Change);
            }
        };

        try
        {
            var prepared = await session.PrepareRuntimeAsync(
                new PlcsimRuntimePrepareRequest(
                    new PlcsimRuntimeReference(null, "PlcUnderTest"),
                    new PlcsimRuntimeStoragePreference(null),
                    PlcsimRuntimeProvisioningMode.UseExisting,
                    PlcsimRuntimeCpuType.S71500,
                    TimeSpan.FromSeconds(30),
                    "real-communication-validation"),
                CancellationToken.None);
            Assert.True(prepared.Succeeded, FormatDiagnostics(prepared.Diagnostics, prepared.Evidence));
            Assert.True(
                prepared.Runtime?.State is PlcsimRuntimeControlState.Stop or PlcsimRuntimeControlState.Run,
                $"Expected an existing stopped or running PLC, observed {prepared.Runtime?.State}.");

            if (prepared.Runtime?.State != PlcsimRuntimeControlState.Run)
            {
                var started = await session.StartRuntimeAsync(
                    new PlcsimRuntimeControlRequest(
                        session.Runtime.SessionId,
                        TimeSpan.FromSeconds(60),
                        "real-communication-validation-start"),
                    CancellationToken.None);
                Assert.True(started.Succeeded, FormatDiagnostics(started.Diagnostics, started.Evidence));
                await WaitForRuntimeStateAsync(runtimeChanges, runtimeChangeLock, PlcsimRuntimeControlState.Run);
            }

            var initialResult = await ValidateRealUnitTestCommunicationCyclesAsync(
                binding,
                session.Runtime.SessionId,
                cycleCount: capacityMeasurementEnabled ? capacityRunCount : 3,
                observationStrategy,
                monitoringInterval,
                drainInterval,
                maxEventsPerBatch,
                workerProcessId,
                capacityMeasurementEnabled,
                "real-communication-validation-initial",
                capacityRuns);
            eventBufferResetBeforeMeasurement |= initialResult.EventBufferResetPerformed;
            capacityContracts = initialResult.Contracts;
            if (capacityMeasurementEnabled && !initialResult.CapacityAvailable)
            {
                return;
            }

            if (!capacityMeasurementEnabled)
            {
                var stopped = await session.StopRuntimeAsync(
                    new PlcsimRuntimeControlRequest(
                        session.Runtime.SessionId,
                        TimeSpan.FromSeconds(60),
                        "real-communication-validation-stop"),
                    CancellationToken.None);
                Assert.True(stopped.Succeeded, FormatDiagnostics(stopped.Diagnostics, stopped.Evidence));
                await WaitForRuntimeStateAsync(runtimeChanges, runtimeChangeLock, PlcsimRuntimeControlState.Stop);

                lock (runtimeChangeLock)
                {
                    runtimeChanges.Clear();
                }

                var restarted = await session.StartRuntimeAsync(
                    new PlcsimRuntimeControlRequest(
                        session.Runtime.SessionId,
                        TimeSpan.FromSeconds(60),
                        "real-communication-validation-restart"),
                    CancellationToken.None);
                Assert.True(restarted.Succeeded, FormatDiagnostics(restarted.Diagnostics, restarted.Evidence));
                await WaitForRuntimeStateAsync(runtimeChanges, runtimeChangeLock, PlcsimRuntimeControlState.Run);

                var recoveryResult = await ValidateRealUnitTestCommunicationCyclesAsync(
                    binding,
                    session.Runtime.SessionId,
                    cycleCount: 1,
                    observationStrategy,
                    monitoringInterval,
                    drainInterval,
                    maxEventsPerBatch,
                    workerProcessId,
                    capacityMeasurementEnabled: false,
                    "real-communication-validation-recovery",
                    capacityRuns);
                eventBufferResetBeforeMeasurement |= recoveryResult.EventBufferResetPerformed;
            }
        }
        finally
        {
            try
            {
                var observed = await session.ObserveRuntimeAsync(
                    new PlcsimRuntimeObservationRequest("real-communication-validation-cleanup"),
                    CancellationToken.None);
                if (observed.Runtime?.State == PlcsimRuntimeControlState.Run)
                {
                    await session.StopRuntimeAsync(
                        new PlcsimRuntimeControlRequest(
                            session.Runtime.SessionId,
                            TimeSpan.FromSeconds(60),
                            "real-communication-validation-cleanup"),
                        CancellationToken.None);
                }
            }
            catch
            {
                // Preserve the primary validation failure while still attempting session cleanup.
            }

            await session.CloseAsync(CancellationToken.None);
            if (capacityMeasurementEnabled && !string.IsNullOrWhiteSpace(capacityOutputPath))
            {
                var observationSummary = ReadObservationLoopSummary(logDirectory);
                WriteRealCapacityMeasurement(
                    capacityOutputPath,
                    configuredPlcMinimumCycleTimeMs!.Value,
                    observationStrategy,
                    monitoringInterval,
                    drainInterval,
                    maxEventsPerBatch,
                    rawEventLoggingEnabled,
                    sourceRevision,
                    capacityWorkloadId,
                    capacityMeasurementRole,
                    capacityRunCount,
                    capacityContracts,
                    eventBufferResetBeforeMeasurement,
                    observationSummary,
                    capacityRuns);
            }
        }
    }

    [Fact]
    public async Task V8_binding_can_validate_real_repeated_session_lifetimes_when_enabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(RealLifetimeValidationVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var initialWorkerIds = GetV8WorkerProcessIds();
        var binding = new PlcsimAdvancedV8Binding();
        binding.Initialize(CreateBindingContext());
        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
        var runtime = Assert.IsAssignableFrom<IPlcsimRuntimeSession>(open.Session);
        var workerProcessId = await WaitForNewV8WorkerProcessAsync(initialWorkerIds);
        var runtimeChanges = new List<PlcsimRuntimeStateChange>();
        var runtimeChangeLock = new object();
        runtime.RuntimeStateChanged += (_, args) =>
        {
            lock (runtimeChangeLock)
            {
                runtimeChanges.Add(args.Change);
            }
        };
        var startedByTest = false;

        try
        {
            var prepared = await runtime.PrepareRuntimeAsync(
                new PlcsimRuntimePrepareRequest(
                    new PlcsimRuntimeReference(null, "PlcUnderTest"),
                    new PlcsimRuntimeStoragePreference(null),
                    PlcsimRuntimeProvisioningMode.UseExisting,
                    PlcsimRuntimeCpuType.S71500,
                    TimeSpan.FromSeconds(30),
                    "real-lifetime-validation"),
                CancellationToken.None);
            Assert.True(prepared.Succeeded, FormatDiagnostics(prepared.Diagnostics, prepared.Evidence));

            var observed = await runtime.ObserveRuntimeAsync(
                new PlcsimRuntimeObservationRequest("real-lifetime-validation"),
                CancellationToken.None);
            Assert.True(observed.Succeeded, FormatDiagnostics(observed.Diagnostics, observed.Evidence));
            if (observed.Runtime?.State != PlcsimRuntimeControlState.Run)
            {
                var started = await runtime.StartRuntimeAsync(
                    new PlcsimRuntimeControlRequest(
                        runtime.Runtime.SessionId,
                        TimeSpan.FromSeconds(60),
                        "real-lifetime-validation"),
                    CancellationToken.None);
                Assert.True(started.Succeeded, FormatDiagnostics(started.Diagnostics, started.Evidence));
                await WaitForRuntimeStateAsync(runtimeChanges, runtimeChangeLock, PlcsimRuntimeControlState.Run);
                startedByTest = true;
            }

            const int warmupCycles = 2;
            const int measuredCycles = 24;
            WorkerResourceSnapshot? baseline = null;
            var measurements = new List<WorkerResourceSnapshot>();
            for (var cycle = 0; cycle < warmupCycles + measuredCycles; cycle++)
            {
                var communicationResult = await binding.PrepareCommunicationAsync(
                    new PlcsimCommunicationPrepareRequest(
                        runtime.Runtime.SessionId,
                        "DB_UnitTestRunControl",
                        "DB_UnitTestEventBuffer",
                        UnitTestContractVersions.PlcsimAdvancedCommunication,
                        UnitTestContractVersions.EventStreamModel,
                        new PlcsimCommunicationWorkerOptions(
                            PlcsimCommunicationObservationStrategyKind.Polling,
                            TimeSpan.FromMilliseconds(50)),
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromSeconds(30),
                        $"real-lifetime-communication-{cycle}"),
                    CancellationToken.None);
                Assert.True(
                    communicationResult.Succeeded,
                    FormatDiagnostics(communicationResult.Diagnostics, communicationResult.Evidence));
                var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(communicationResult.Session);
                try
                {
                    var monitoring = await communication.StartEventStreamMonitoringAsync(
                        new PlcsimEventStreamMonitoringRequest(
                            communication.Communication.SessionId,
                            $"real-lifetime-monitoring-{cycle}"),
                        CancellationToken.None);
                    Assert.True(monitoring.Succeeded, FormatDiagnostics(monitoring.Diagnostics, monitoring.Evidence));
                    await Task.Delay(150);
                    var stopped = await communication.StopEventStreamMonitoringAsync(
                        new PlcsimEventStreamStopRequest(
                            communication.Communication.SessionId,
                            $"real-lifetime-monitoring-{cycle}"),
                        CancellationToken.None);
                    Assert.True(stopped.Succeeded, FormatDiagnostics(stopped.Diagnostics, stopped.Evidence));
                }
                finally
                {
                    await communication.CloseAsync(CancellationToken.None);
                }

                var snapshot = CaptureWorkerResources(workerProcessId, cycle);
                if (cycle == warmupCycles - 1)
                {
                    baseline = snapshot;
                }
                else if (cycle >= warmupCycles)
                {
                    measurements.Add(snapshot);
                }
            }

            Assert.NotNull(baseline);
            AssertWorkerResourcesRemainBounded(baseline!, measurements);
        }
        finally
        {
            if (startedByTest)
            {
                try
                {
                    var stopped = await runtime.StopRuntimeAsync(
                        new PlcsimRuntimeControlRequest(
                            runtime.Runtime.SessionId,
                            TimeSpan.FromSeconds(60),
                            "real-lifetime-validation-cleanup"),
                        CancellationToken.None);
                    if (stopped.Succeeded)
                    {
                        await WaitForRuntimeStateAsync(runtimeChanges, runtimeChangeLock, PlcsimRuntimeControlState.Stop);
                    }
                }
                catch
                {
                    // Preserve the primary lifetime-validation failure.
                }
            }

            await runtime.CloseAsync(CancellationToken.None);
        }

        await WaitForV8WorkerProcessesAsync(initialWorkerIds);
        await ValidateRepeatedRealCreateOwnedLifetimesAsync(initialWorkerIds, cycleCount: 3);
        await WaitForV8WorkerProcessesAsync(initialWorkerIds);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<SessionWeakReferences> CreateClosedSessionWeakReferencesAsync()
    {
        var worker = new StubPlcsimAdvancedV8WorkerSessionHandle("collectable-runtime-session");
        var binding = CreateBindingWithWorker(worker);
        var opened = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        var runtime = Assert.IsAssignableFrom<IPlcsimRuntimeSession>(opened.Session);
        var communication = CreateFacadeCommunicationSession(worker);
        var references = new SessionWeakReferences(
            new WeakReference(runtime),
            new WeakReference(communication));

        await communication.CloseAsync(CancellationToken.None);
        await runtime.CloseAsync(CancellationToken.None);
        return references;
    }

    internal static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static bool WaitForProcessExit(int processId)
    {
        return SpinWait.SpinUntil(
            () =>
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    return process.HasExited;
                }
                catch (ArgumentException)
                {
                    return true;
                }
            },
            TimeSpan.FromSeconds(2));
    }

    private static async Task ValidateRepeatedRealCreateOwnedLifetimesAsync(
        IReadOnlySet<int> initialWorkerIds,
        int cycleCount)
    {
        for (var cycle = 0; cycle < cycleCount; cycle++)
        {
            var instanceName = "PUT" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var storagePath = Path.Combine(
                Path.GetTempPath(),
                "PLCSIM.UnitTest",
                "runtimes",
                instanceName);
            var binding = new PlcsimAdvancedV8Binding();
            binding.Initialize(CreateBindingContext());
            var open = await binding.OpenRuntimeSessionAsync(
                new PlcsimRuntimeSessionOpenRequest(
                    new PlcsimRuntimeApiExpectation("8.0", "8.0", SiemensRuntimeLocality.Local),
                    new PlcsimRuntimeReference(null, instanceName),
                    new PlcsimRuntimeStoragePreference(storagePath),
                    PlcsimRuntimeCleanupPreference.Release,
                    $"real-lifetime-owned-{cycle}"),
                CancellationToken.None);
            Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
            var runtime = Assert.IsAssignableFrom<IPlcsimRuntimeSession>(open.Session);
            var released = false;
            try
            {
                var prepared = await runtime.PrepareRuntimeAsync(
                    new PlcsimRuntimePrepareRequest(
                        new PlcsimRuntimeReference(null, instanceName),
                        new PlcsimRuntimeStoragePreference(storagePath),
                        PlcsimRuntimeProvisioningMode.CreateOwned,
                        PlcsimRuntimeCpuType.S71500,
                        TimeSpan.FromSeconds(30),
                        $"real-lifetime-owned-{cycle}"),
                    CancellationToken.None);
                Assert.True(prepared.Succeeded, FormatDiagnostics(prepared.Diagnostics, prepared.Evidence));

                var release = await runtime.ReleaseRuntimeAsync(
                    new PlcsimRuntimeReleaseRequest(
                        runtime.Runtime.SessionId,
                        PlcsimRuntimeCleanupPreference.Release,
                        TimeSpan.FromSeconds(60),
                        $"real-lifetime-owned-{cycle}"),
                    CancellationToken.None);
                released = release.Succeeded;
                Assert.True(release.Succeeded, FormatDiagnostics(release.Diagnostics, release.Evidence));
                Assert.True(release.Released);
                Assert.True(release.StorageCleaned);

                var rediscovery = await runtime.DiscoverRuntimeTargetsAsync(
                    new PlcsimRuntimeDiscoveryRequest($"real-lifetime-owned-{cycle}"),
                    CancellationToken.None);
                Assert.DoesNotContain(
                    rediscovery.Candidates,
                    candidate => string.Equals(
                        candidate.Reference.InstanceName,
                        instanceName,
                        StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                if (!released)
                {
                    try
                    {
                        await runtime.ReleaseRuntimeAsync(
                            new PlcsimRuntimeReleaseRequest(
                                runtime.Runtime.SessionId,
                                PlcsimRuntimeCleanupPreference.Release,
                                TimeSpan.FromSeconds(60),
                                $"real-lifetime-owned-{cycle}-cleanup"),
                            CancellationToken.None);
                    }
                    catch
                    {
                        // Preserve the primary validation failure.
                    }
                }

                await runtime.CloseAsync(CancellationToken.None);
            }

            Assert.False(Directory.Exists(storagePath), $"Owned runtime storage remained after cycle {cycle}: {storagePath}");
            await WaitForV8WorkerProcessesAsync(initialWorkerIds);
        }
    }

    private static IReadOnlySet<int> GetV8WorkerProcessIds()
    {
        var processName = Path.GetFileNameWithoutExtension(ResolveBuiltWorkerPath());
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                ids.Add(process.Id);
            }
        }

        return ids;
    }

    private static async Task<int> WaitForNewV8WorkerProcessAsync(IReadOnlySet<int> initialWorkerIds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var newIds = GetV8WorkerProcessIds()
                .Where(processId => !initialWorkerIds.Contains(processId))
                .ToArray();
            if (newIds.Length == 1)
            {
                return newIds[0];
            }

            Assert.True(newIds.Length <= 1, $"Expected one PLCSIM Advanced V8 worker, observed: {string.Join(", ", newIds)}");
            await Task.Delay(50);
        }

        Assert.Fail("PLCSIM Advanced V8 worker process was not observed.");
        return 0;
    }

    private static async Task WaitForV8WorkerProcessesAsync(IReadOnlySet<int> allowedWorkerIds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var unexpected = GetV8WorkerProcessIds()
                .Where(processId => !allowedWorkerIds.Contains(processId))
                .ToArray();
            if (unexpected.Length == 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        var remaining = GetV8WorkerProcessIds()
            .Where(processId => !allowedWorkerIds.Contains(processId))
            .ToArray();
        Assert.Fail($"PLCSIM Advanced V8 worker processes remained after cleanup: {string.Join(", ", remaining)}");
    }

    private static WorkerResourceSnapshot CaptureWorkerResources(int processId, int cycle)
    {
        using var process = Process.GetProcessById(processId);
        process.Refresh();
        return new WorkerResourceSnapshot(
            cycle,
            process.PrivateMemorySize64,
            process.HandleCount,
            process.Threads.Count);
    }

    private static void AssertWorkerResourcesRemainBounded(
        WorkerResourceSnapshot baseline,
        IReadOnlyList<WorkerResourceSnapshot> measurements)
    {
        const long maximumPrivateMemoryGrowth = 32L * 1024 * 1024;
        const int maximumHandleGrowth = 8;
        const int maximumThreadGrowth = 2;
        Assert.NotEmpty(measurements);

        var maximumMemory = measurements.Max(snapshot => snapshot.PrivateMemoryBytes);
        var maximumHandles = measurements.Max(snapshot => snapshot.HandleCount);
        var maximumThreads = measurements.Max(snapshot => snapshot.ThreadCount);
        var evidence = string.Join(
            "; ",
            new[] { baseline }.Concat(measurements).Select(snapshot =>
                $"cycle={snapshot.Cycle},private={snapshot.PrivateMemoryBytes},handles={snapshot.HandleCount},threads={snapshot.ThreadCount}"));
        Console.WriteLine($"PLCSIM Advanced V8 worker resource evidence: {evidence}");

        Assert.True(
            maximumMemory <= baseline.PrivateMemoryBytes + maximumPrivateMemoryGrowth,
            $"Worker private memory exceeded the post-warm-up guardrail. {evidence}");
        Assert.True(
            maximumHandles <= baseline.HandleCount + maximumHandleGrowth,
            $"Worker handle count exceeded the post-warm-up guardrail. {evidence}");
        Assert.True(
            maximumThreads <= baseline.ThreadCount + maximumThreadGrowth,
            $"Worker thread count exceeded the post-warm-up guardrail. {evidence}");
    }

    internal static PlcsimAdvancedV8CommunicationSession CreateFacadeCommunicationSession(
        IPlcsimAdvancedV8WorkerSessionHandle worker)
    {
        return new PlcsimAdvancedV8CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            "test-framework",
            UnitTestContractVersions.EventStreamModel,
            "test-framework");
    }

    internal static PlcsimAdvancedV8EnvironmentProbeResult ReadyProbeWithRuntimeApiDirectory(string runtimeApiDirectory)
    {
        return PlcsimAdvancedV8EnvironmentProbeResult.Ready(new[]
        {
            new PlcsimEvidence(
                "plcsim-advanced-v8-api-directory",
                "RuntimeApiDirectory",
                runtimeApiDirectory),
            new PlcsimEvidence(
                "plcsim-advanced-v8-runtime-api-dll",
                "RuntimeApiDll",
                Path.Combine(runtimeApiDirectory, "Siemens.Simatic.Simulation.Runtime.Api.x64.dll"))
        });
    }

    private static TimeSpan? ReadRealCommunicationMonitoringInterval(
        PlcsimCommunicationObservationStrategyKind observationStrategy)
    {
        var monitoringConfigured = Environment.GetEnvironmentVariable(RealCommunicationMonitoringIntervalVariable);
        var legacyConfigured = Environment.GetEnvironmentVariable(RealCommunicationPollIntervalVariable);
        Assert.False(
            !string.IsNullOrWhiteSpace(monitoringConfigured) && !string.IsNullOrWhiteSpace(legacyConfigured),
            $"{RealCommunicationMonitoringIntervalVariable} and {RealCommunicationPollIntervalVariable} cannot both be set.");
        var configured = monitoringConfigured ?? legacyConfigured;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return observationStrategy == PlcsimCommunicationObservationStrategyKind.Polling
                ? TimeSpan.FromMilliseconds(10)
                : null;
        }

        return ReadPositiveMillisecondsValue(
            configured,
            RealCommunicationMonitoringIntervalVariable,
            TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public async Task V8_binding_can_measure_real_adaptive_event_payload_read_when_enabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(RealEventReadMeasurementVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var mode = Environment.GetEnvironmentVariable(RealEventReadMeasurementModeVariable) ?? string.Empty;
        Assert.True(
            string.Equals(mode, "Controlled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "Natural", StringComparison.OrdinalIgnoreCase),
            $"{RealEventReadMeasurementModeVariable} must be 'Controlled' or 'Natural'.");
        var target = ReadPositiveIntEnvironmentVariable(RealEventReadMeasurementTargetVariable, defaultValue: null);
        Assert.NotNull(target);
        Assert.InRange(target!.Value, 1, 255);
        if (string.Equals(mode, "Natural", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(32, target.Value);
        }

        var outputPath = Environment.GetEnvironmentVariable(RealEventReadMeasurementOutputVariable);
        Assert.False(string.IsNullOrWhiteSpace(outputPath), $"{RealEventReadMeasurementOutputVariable} is required.");
        var sourceRevision = Environment.GetEnvironmentVariable(RealCommunicationSourceRevisionVariable) ?? string.Empty;
        var logDirectory = CreateTestLogDirectory();
        var measurementWorkerPath = ResolveBuiltPerformanceMeasurementWorkerPath();
        Assert.True(
            File.Exists(measurementWorkerPath),
            $"Performance measurement worker was not found: {measurementWorkerPath}");
        var binding = new PlcsimAdvancedV8Binding(
            new PlcsimAdvancedV8EnvironmentProbe(),
            new PlcsimAdvancedV8WorkerSessionHost(() => measurementWorkerPath));
        binding.Initialize(CreateBindingContext(logDirectory, rawEventLoggingEnabled: false));
        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
        Assert.NotNull(open.Session);
        var session = open.Session!;
        RealEventPayloadReadMeasurement? measurement = null;

        try
        {
            var prepared = await session.PrepareRuntimeAsync(
                new PlcsimRuntimePrepareRequest(
                    new PlcsimRuntimeReference(null, "PlcUnderTest"),
                    new PlcsimRuntimeStoragePreference(null),
                    PlcsimRuntimeProvisioningMode.UseExisting,
                    PlcsimRuntimeCpuType.S71500,
                    TimeSpan.FromSeconds(30),
                    "real-event-read-measurement"),
                CancellationToken.None);
            Assert.True(prepared.Succeeded, FormatDiagnostics(prepared.Diagnostics, prepared.Evidence));

            if (prepared.Runtime?.State != PlcsimRuntimeControlState.Run)
            {
                var started = await session.StartRuntimeAsync(
                    new PlcsimRuntimeControlRequest(
                        session.Runtime.SessionId,
                        TimeSpan.FromSeconds(60),
                        "real-event-read-measurement-start-runtime"),
                    CancellationToken.None);
                Assert.True(started.Succeeded, FormatDiagnostics(started.Diagnostics, started.Evidence));
            }

            measurement = await MeasureRealEventPayloadReadAsync(
                binding,
                session.Runtime.SessionId,
                mode,
                target.Value,
                logDirectory,
                sourceRevision);
        }
        finally
        {
            try
            {
                var observed = await session.ObserveRuntimeAsync(
                    new PlcsimRuntimeObservationRequest("real-event-read-measurement-cleanup"),
                    CancellationToken.None);
                if (observed.Runtime?.State == PlcsimRuntimeControlState.Run)
                {
                    await session.StopRuntimeAsync(
                        new PlcsimRuntimeControlRequest(
                            session.Runtime.SessionId,
                            TimeSpan.FromSeconds(60),
                            "real-event-read-measurement-stop-runtime"),
                        CancellationToken.None);
                }
            }
            catch
            {
                // Preserve the primary measurement failure while still closing the worker session.
            }

            await session.CloseAsync(CancellationToken.None);
        }

        Assert.NotNull(measurement);
        var fullOutputPath = Path.GetFullPath(outputPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        File.WriteAllText(
            fullOutputPath,
            JsonSerializer.Serialize(measurement, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Real EventStream payload-read measurement written: {fullOutputPath}");
    }

    private static TimeSpan ReadPositiveMillisecondsEnvironmentVariable(string name, TimeSpan defaultValue)
    {
        return ReadPositiveMillisecondsValue(Environment.GetEnvironmentVariable(name), name, defaultValue);
    }

    private static TimeSpan ReadPositiveMillisecondsValue(string? configured, string name, TimeSpan defaultValue)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return defaultValue;
        }

        Assert.True(
            int.TryParse(configured, out var milliseconds) && milliseconds > 0,
            $"{name} must contain a positive integer.");
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static int? ReadPositiveIntEnvironmentVariable(string name, int? defaultValue)
    {
        var configured = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return defaultValue;
        }

        Assert.True(
            int.TryParse(configured, out var value) && value > 0,
            $"{name} must contain a positive integer.");
        return value;
    }

    private static string FormatDiagnostics(
        IReadOnlyList<PlcsimDiagnostic> diagnostics,
        IReadOnlyList<PlcsimEvidence> evidence)
    {
        var diagnosticText = diagnostics.Count == 0
            ? "Diagnostics: <none>"
            : "Diagnostics: " + string.Join(
                " | ",
                diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message} ({diagnostic.Phase})"));
        var evidenceText = evidence.Count == 0
            ? "Evidence: <none>"
            : "Evidence: " + string.Join(
                " | ",
                evidence.Select(item => $"{item.Kind}: {item.Summary}"));
        return diagnosticText + Environment.NewLine + evidenceText;
    }

    private static async Task<RealEventPayloadReadMeasurement> MeasureRealEventPayloadReadAsync(
        PlcsimAdvancedV8Binding binding,
        string runtimeSessionId,
        string mode,
        int targetEventCount,
        string logDirectory,
        string sourceRevision)
    {
        var prepare = await binding.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                runtimeSessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                UnitTestContractVersions.EventStreamModel,
                new PlcsimCommunicationWorkerOptions(
                    PlcsimCommunicationObservationStrategyKind.PlcCycle,
                    null)
                {
                    DrainInterval = TimeSpan.FromMilliseconds(1),
                    MaxEventsPerBatch = targetEventCount
                },
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30),
                "real-event-read-measurement-prepare"),
            CancellationToken.None);
        Assert.True(prepare.Succeeded, FormatDiagnostics(prepare.Diagnostics, prepare.Evidence));
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);

        try
        {
            var runControl = await communication.ValidateRunControlAsync(
                new PlcsimRunControlValidationRequest(
                    communication.Communication.SessionId,
                    UnitTestContractVersions.PlcsimAdvancedCommunication,
                    "real-event-read-measurement-validate-runcontrol"),
                CancellationToken.None);
            Assert.True(runControl.Succeeded && runControl.ContractValid, FormatDiagnostics(runControl.Diagnostics, runControl.Evidence));

            var eventBuffer = await communication.ValidateEventBufferAsync(
                new PlcsimEventBufferValidationRequest(
                    communication.Communication.SessionId,
                    UnitTestContractVersions.EventStreamModel,
                    "real-event-read-measurement-validate-eventbuffer"),
                CancellationToken.None);
            Assert.True(eventBuffer.Succeeded && eventBuffer.ContractValid, FormatDiagnostics(eventBuffer.Diagnostics, eventBuffer.Evidence));

            var resetEventBuffer = await communication.RequestEventBufferResetAsync(
                new PlcsimEventBufferResetRequest(
                    communication.Communication.SessionId,
                    TimeSpan.FromSeconds(10),
                    "real-event-read-measurement-reset-eventbuffer"),
                CancellationToken.None);
            Assert.True(
                resetEventBuffer.Succeeded && resetEventBuffer.Accepted,
                FormatDiagnostics(resetEventBuffer.Diagnostics, resetEventBuffer.Evidence));
            await WaitForCleanEventBufferAsync(communication, "real-event-read-measurement");

            var beforeRun = await communication.ObserveRunControlAsync(
                new PlcsimRunControlObservationRequest(
                    communication.Communication.SessionId,
                    "real-event-read-measurement-observe-before-run"),
                CancellationToken.None);
            Assert.True(beforeRun.Succeeded && beforeRun.Observation is not null, FormatDiagnostics(beforeRun.Diagnostics, beforeRun.Evidence));
            if (beforeRun.Observation.ExecutionStatus != UnitTestExecutionStatus.Idle || !beforeRun.Observation.Ready)
            {
                var resetRunControl = await communication.RequestRunControlCommandAsync(
                    new PlcsimRunControlCommandRequest(
                        communication.Communication.SessionId,
                        UnitTestRunCommand.Reset,
                        TimeSpan.FromSeconds(10),
                        "real-event-read-measurement-reset-runcontrol"),
                    CancellationToken.None);
                Assert.True(
                    resetRunControl.Succeeded && resetRunControl.Accepted,
                    FormatDiagnostics(resetRunControl.Diagnostics, resetRunControl.Evidence));
                await WaitForRunControlReadyAsync(communication);
            }

            var monitoring = await communication.StartEventStreamMonitoringAsync(
                new PlcsimEventStreamMonitoringRequest(
                    communication.Communication.SessionId,
                    "real-event-read-measurement-start-monitoring"),
                CancellationToken.None);
            Assert.True(monitoring.Succeeded && monitoring.State.IsMonitoring, FormatDiagnostics(monitoring.Diagnostics, monitoring.Evidence));

            var start = await communication.RequestRunControlCommandAsync(
                new PlcsimRunControlCommandRequest(
                    communication.Communication.SessionId,
                    UnitTestRunCommand.Start,
                    TimeSpan.FromSeconds(10),
                    "real-event-read-measurement-start-run"),
                CancellationToken.None);
            Assert.True(start.Succeeded && start.Accepted, FormatDiagnostics(start.Diagnostics, start.Evidence));

            var timing = await WaitForEventPayloadReadTimingAsync(logDirectory, targetEventCount);
            return new RealEventPayloadReadMeasurement(
                SchemaVersion: 1,
                RecordedAtUtc: DateTimeOffset.UtcNow,
                SourceRevision: sourceRevision,
                BuildConfiguration: GetBuildConfiguration(),
                Mode: mode,
                ConfiguredPlcMinimumCycleTimeMs: 1,
                ObservationStrategy: PlcsimCommunicationObservationStrategyKind.PlcCycle.ToString(),
                MonitoringIntervalMs: null,
                DrainIntervalMs: 1,
                RawEventLoggingEnabled: false,
                TargetEventCount: targetEventCount,
                EventCount: timing.EventCount,
                CapturedEventCount: timing.CapturedEventCount,
                ReadState: timing.ReadState,
                SnapshotDurationMs: timing.SnapshotDurationMs,
                PreMetadataDurationMs: timing.PreMetadataDurationMs,
                EntryReadDurationMs: timing.EntryReadDurationMs,
                PostMetadataDurationMs: timing.PostMetadataDurationMs);
        }
        finally
        {
            if (communication.Health == PlcsimCommunicationHealthState.Healthy)
            {
                var stop = await communication.StopEventStreamMonitoringAsync(
                    new PlcsimEventStreamStopRequest(
                        communication.Communication.SessionId,
                        "real-event-read-measurement-stop-monitoring"),
                    CancellationToken.None);
                Assert.True(stop.Succeeded, FormatDiagnostics(stop.Diagnostics, stop.Evidence));
            }

            await communication.CloseAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForRunControlReadyAsync(IPlcsimUnitTestCommunicationSession communication)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var observation = await communication.ObserveRunControlAsync(
                new PlcsimRunControlObservationRequest(
                    communication.Communication.SessionId,
                    "real-event-read-measurement-wait-ready"),
                CancellationToken.None);
            Assert.True(
                observation.Succeeded && observation.Observation is not null,
                FormatDiagnostics(observation.Diagnostics, observation.Evidence));
            if (observation.Observation.ExecutionStatus == UnitTestExecutionStatus.Idle
                && observation.Observation.Ready)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("RunControl did not reach Idle + Ready before the payload-read measurement.");
    }

    private static async Task<RealEventPayloadReadTiming> WaitForEventPayloadReadTimingAsync(
        string logDirectory,
        int targetEventCount)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        var pattern = new Regex(
            @"EventStream snapshot read completed\. EventCount=(?<events>\d+) CapturedEventCount=(?<captured>\d+) ReadState=(?<state>\S+).*?DurationMs=(?<duration>[0-9.,]+).*?PreMetadataDurationMs=(?<pre>[0-9.,]+) EntryReadDurationMs=(?<entries>[0-9.,]+) PostMetadataDurationMs=(?<post>[0-9.,]+)",
            RegexOptions.CultureInvariant);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var logPaths = Directory.GetFiles(
                logDirectory,
                "access-profile-siemens-test-plcsim-advanced-v8-worker-*.log");
            if (logPaths.Length == 1)
            {
                string log;
                using (var stream = new FileStream(
                    logPaths[0],
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    log = await reader.ReadToEndAsync();
                }

                var match = pattern.Matches(log)
                    .Cast<Match>()
                    .FirstOrDefault(candidate => int.Parse(candidate.Groups["events"].Value) == targetEventCount);
                if (match is not null)
                {
                    return new RealEventPayloadReadTiming(
                        int.Parse(match.Groups["events"].Value),
                        int.Parse(match.Groups["captured"].Value),
                        match.Groups["state"].Value.Trim('"'),
                        ParseLogDouble(match.Groups["duration"].Value),
                        ParseLogDouble(match.Groups["pre"].Value),
                        ParseLogDouble(match.Groups["entries"].Value),
                        ParseLogDouble(match.Groups["post"].Value));
                }
            }

            await Task.Delay(25);
        }

        Assert.Fail($"No EventStream payload-read timing for exactly {targetEventCount} events was logged.");
        throw new InvalidOperationException();
    }

    private static double ParseLogDouble(string value)
    {
        return double.Parse(
            value.Replace(',', '.'),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<RealCommunicationCycleValidationResult> ValidateRealUnitTestCommunicationCyclesAsync(
        PlcsimAdvancedV8Binding binding,
        string runtimeSessionId,
        int cycleCount,
        PlcsimCommunicationObservationStrategyKind observationStrategy,
        TimeSpan? monitoringInterval,
        TimeSpan drainInterval,
        int? maxEventsPerBatch,
        int workerProcessId,
        bool capacityMeasurementEnabled,
        string correlationPrefix,
        ICollection<RealEventStreamRunMeasurement> measurements)
    {
        var eventBufferResetPerformed = false;
        var prepare = await binding.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                runtimeSessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                UnitTestContractVersions.EventStreamModel,
                new PlcsimCommunicationWorkerOptions(
                    observationStrategy,
                    monitoringInterval)
                {
                    DrainInterval = drainInterval,
                    MaxEventsPerBatch = maxEventsPerBatch
                },
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30),
                correlationPrefix + "-prepare"),
            CancellationToken.None);
        Assert.True(prepare.Succeeded, FormatDiagnostics(prepare.Diagnostics, prepare.Evidence));
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);
        var concreteCommunication = Assert.IsType<PlcsimAdvancedV8CommunicationSession>(communication);
        var contracts = new RealCapacityContractEvidence(
            concreteCommunication.ObservedRunControlContractVersion,
            concreteCommunication.ObservedRunControlFrameworkVersion,
            concreteCommunication.ObservedEventStreamContractVersion,
            concreteCommunication.ObservedEventStreamFrameworkVersion);
        var observations = new List<PlcsimRunControlObservation>();
        var batches = new List<UnitTestEventBatch>();
        var monitoringStates = new List<PlcsimEventStreamMonitoringState>();
        var observationLock = new object();
        var batchLock = new object();
        var monitoringStateLock = new object();
        communication.RunControlObservationChanged += (_, args) =>
        {
            lock (observationLock)
            {
                observations.Add(args.Observation);
            }
        };
        communication.EventBatchReceived += (_, args) =>
        {
            lock (batchLock)
            {
                batches.Add(args.Batch);
            }
        };
        communication.EventStreamMonitoringStateChanged += (_, args) =>
        {
            lock (monitoringStateLock)
            {
                monitoringStates.Add(args.State);
            }
        };

        try
        {
            var runControl = await communication.ValidateRunControlAsync(
                new PlcsimRunControlValidationRequest(
                    communication.Communication.SessionId,
                    UnitTestContractVersions.PlcsimAdvancedCommunication,
                    correlationPrefix + "-validate-runcontrol"),
                CancellationToken.None);
            Assert.True(runControl.Succeeded && runControl.ContractValid, FormatDiagnostics(runControl.Diagnostics, runControl.Evidence));

            var eventBuffer = await communication.ValidateEventBufferAsync(
                new PlcsimEventBufferValidationRequest(
                    communication.Communication.SessionId,
                    UnitTestContractVersions.EventStreamModel,
                    correlationPrefix + "-validate-eventbuffer"),
                CancellationToken.None);
            Assert.True(eventBuffer.Succeeded && eventBuffer.ContractValid, FormatDiagnostics(eventBuffer.Diagnostics, eventBuffer.Evidence));
            if (capacityMeasurementEnabled && eventBuffer.Observation.LostEventCount > 0)
            {
                var eventBufferReset = await communication.RequestEventBufferResetAsync(
                    new PlcsimEventBufferResetRequest(
                        communication.Communication.SessionId,
                        TimeSpan.FromSeconds(10),
                        correlationPrefix + "-reset-event-buffer"),
                    CancellationToken.None);
                Assert.True(
                    eventBufferReset.Succeeded && eventBufferReset.Accepted,
                    FormatDiagnostics(eventBufferReset.Diagnostics, eventBufferReset.Evidence));
                eventBufferResetPerformed = true;
                eventBuffer = await WaitForCleanEventBufferAsync(communication, correlationPrefix);
            }
            Assert.Equal((ushort)0, eventBuffer.Observation.LostEventCount);

            int monitoringStartIndex;
            lock (monitoringStateLock)
            {
                monitoringStartIndex = monitoringStates.Count;
            }
            var monitoring = await communication.StartEventStreamMonitoringAsync(
                new PlcsimEventStreamMonitoringRequest(
                    communication.Communication.SessionId,
                    correlationPrefix + "-start-monitoring"),
                CancellationToken.None);
            Assert.True(monitoring.Succeeded && monitoring.State.IsMonitoring, FormatDiagnostics(monitoring.Diagnostics, monitoring.Evidence));

            for (var cycle = 0; cycle < cycleCount; cycle++)
            {
                var beforeRun = await communication.ObserveRunControlAsync(
                    new PlcsimRunControlObservationRequest(
                        communication.Communication.SessionId,
                        $"{correlationPrefix}-observe-before-run-{cycle}"),
                    CancellationToken.None);
                Assert.True(
                    beforeRun.Succeeded && beforeRun.Observation is not null,
                    FormatDiagnostics(beforeRun.Diagnostics, beforeRun.Evidence));

                var cleanMonitoringStateStartIndex = monitoringStartIndex;
                if (beforeRun.Observation.ExecutionStatus != UnitTestExecutionStatus.Idle || !beforeRun.Observation.Ready)
                {
                    int resetObservationStartIndex;
                    lock (observationLock)
                    {
                        resetObservationStartIndex = observations.Count;
                    }
                    lock (monitoringStateLock)
                    {
                        cleanMonitoringStateStartIndex = monitoringStates.Count;
                    }
                    var reset = await communication.RequestRunControlCommandAsync(
                        new PlcsimRunControlCommandRequest(
                            communication.Communication.SessionId,
                            UnitTestRunCommand.Reset,
                            TimeSpan.FromSeconds(10),
                            $"{correlationPrefix}-reset-{cycle}"),
                        CancellationToken.None);
                    Assert.True(reset.Succeeded && reset.Accepted, FormatDiagnostics(reset.Diagnostics, reset.Evidence));
                    await WaitForRunControlObservationAsync(
                        observations,
                        observationLock,
                        resetObservationStartIndex,
                        observation => observation.ExecutionStatus == UnitTestExecutionStatus.Idle && observation.Ready,
                        "Idle + Ready");
                }

                var cleanStart = await WaitForEmptyEventBufferAsync(
                    monitoringStates,
                    monitoringStateLock,
                    cleanMonitoringStateStartIndex);

                int batchStartIndex;
                int monitoringStateStartIndex;
                lock (batchLock)
                {
                    batchStartIndex = batches.Count;
                }
                lock (monitoringStateLock)
                {
                    monitoringStateStartIndex = monitoringStates.Count;
                }

                var resourcesBefore = CaptureEventStreamWorkerResources(workerProcessId);
                var startedAtUtc = DateTimeOffset.UtcNow;
                var runStopwatch = Stopwatch.StartNew();
                var start = await communication.RequestRunControlCommandAsync(
                    new PlcsimRunControlCommandRequest(
                        communication.Communication.SessionId,
                        UnitTestRunCommand.Start,
                        TimeSpan.FromSeconds(10),
                        $"{correlationPrefix}-start-{cycle}"),
                    CancellationToken.None);
                Assert.True(start.Succeeded && start.Accepted, FormatDiagnostics(start.Diagnostics, start.Evidence));

                var waitResult = await WaitForRunEvidenceAsync(
                    batches,
                    batchLock,
                    batchStartIndex,
                    cleanStart.ProducerSequenceId);
                runStopwatch.Stop();
                var resourcesAfter = CaptureEventStreamWorkerResources(workerProcessId);
                var runBatches = waitResult.Batches;

                var entries = runBatches
                    .SelectMany(batch => batch.Events)
                    .Where(entry => entry.SequenceId > cleanStart.ProducerSequenceId)
                    .ToArray();
                Assert.Equal(entries.Length, entries.Select(entry => entry.SequenceId).Distinct().Count());
                Assert.True(entries.SequenceEqual(entries.OrderBy(entry => entry.SequenceId)));
                Assert.All(entries, entry => Assert.True(entry.Timestamp > DateTimeOffset.UnixEpoch));
                PlcsimEventStreamMonitoringState[] runMonitoringStates;
                lock (monitoringStateLock)
                {
                    runMonitoringStates = monitoringStates.Skip(monitoringStateStartIndex).ToArray();
                }
                var maximumObservedBacklog = runMonitoringStates
                    .Select(state => state.ProducerSequenceId >= state.DeletedSequenceId
                        ? state.ProducerSequenceId - state.DeletedSequenceId
                        : 0u)
                    .DefaultIfEmpty(0u)
                    .Max();
                var maximumBatchEvents = runBatches
                    .Select(batch => batch.Events.Count)
                    .DefaultIfEmpty(0)
                    .Max();
                var maximumLostEvents = runBatches
                    .Select(batch => batch.LostEventCount)
                    .DefaultIfEmpty((ushort)0)
                    .Max();
                var outcome = waitResult.Complete
                    ? "Completed"
                    : waitResult.EventLossObserved
                        ? "EventLoss"
                        : "Timeout";
                var runResult = entries
                    .LastOrDefault(entry => entry.OwnerScope == UnitTestOwnerScope.RunControl
                        && entry.EventType == UnitTestEventType.Result)
                    ?.Result ?? UnitTestResultState.NotEvaluated;
                Console.WriteLine(
                    $"Real EventStream run: correlation={correlationPrefix}, cycle={cycle}, outcome={outcome}, runResult={runResult}, observationStrategy={observationStrategy}, monitoringIntervalMs={monitoringInterval?.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) ?? "<every-plc-cycle>"}, drainIntervalMs={drainInterval.TotalMilliseconds:F0}, maxEventsPerBatch={maxEventsPerBatch?.ToString() ?? "<buffer-length>"}, durationMs={runStopwatch.Elapsed.TotalMilliseconds:F1}, batches={runBatches.Count}, events={entries.Length}, maximumBatchEvents={maximumBatchEvents}, maximumObservedBacklog={maximumObservedBacklog}, maximumLostEvents={maximumLostEvents}, workerCpuMs={(resourcesAfter.ProcessorTime - resourcesBefore.ProcessorTime).TotalMilliseconds:F1}, workerPrivateMemoryDeltaBytes={resourcesAfter.PrivateMemoryBytes - resourcesBefore.PrivateMemoryBytes}");

                measurements.Add(new RealEventStreamRunMeasurement(
                        correlationPrefix,
                        cycle,
                        startedAtUtc,
                        outcome,
                        runResult.ToString(),
                        runStopwatch.Elapsed.TotalMilliseconds,
                        runBatches.Count,
                        entries.Length,
                        entries.FirstOrDefault()?.SequenceId,
                        entries.LastOrDefault()?.SequenceId,
                        maximumBatchEvents,
                        maximumObservedBacklog,
                        maximumLostEvents,
                        (resourcesAfter.ProcessorTime - resourcesBefore.ProcessorTime).TotalMilliseconds,
                        resourcesAfter.PrivateMemoryBytes - resourcesBefore.PrivateMemoryBytes,
                        entries
                            .GroupBy(entry => new
                            {
                                entry.OwnerScope,
                                entry.EventType,
                                entry.State,
                                entry.Result,
                                entry.Name,
                                entry.ResourceName
                            })
                            .OrderBy(group => group.Key.OwnerScope)
                            .ThenBy(group => group.Key.EventType)
                            .ThenBy(group => group.Key.State)
                            .ThenBy(group => group.Key.Result)
                            .ThenBy(group => group.Key.Name, StringComparer.Ordinal)
                            .ThenBy(group => group.Key.ResourceName, StringComparer.Ordinal)
                            .Select(group => new RealEventStreamEventBreakdown(
                                group.Key.OwnerScope,
                                group.Key.EventType,
                                group.Key.State,
                                group.Key.Result,
                                group.Key.Name,
                                group.Key.ResourceName,
                                group.Count()))
                            .ToArray(),
                        new RealEventStreamCleanStartEvidence(
                            cleanStart.ProducerSequenceId,
                            cleanStart.DeletedSequenceId,
                            cleanStart.LostEventCount,
                            cleanStart.BufferObservedEmpty)));

                if (!waitResult.Complete)
                {
                    if (capacityMeasurementEnabled)
                    {
                        return new RealCommunicationCycleValidationResult(
                            CapacityAvailable: false,
                            EventBufferResetPerformed: eventBufferResetPerformed,
                            Contracts: contracts);
                    }

                    Assert.Fail(
                        "Complete real RunControl/EventBuffer evidence was not observed. Observed: "
                        + string.Join(
                            ", ",
                            entries.Select(entry =>
                                $"{entry.SequenceId}:{entry.OwnerScope}/{entry.EventType}/{entry.State}")));
                }

                if (!capacityMeasurementEnabled)
                {
                    Assert.Equal(UnitTestResultState.Passed, runResult);
                }

                Assert.All(runBatches, batch => Assert.Equal((ushort)0, batch.LostEventCount));

            }

            return new RealCommunicationCycleValidationResult(
                CapacityAvailable: true,
                EventBufferResetPerformed: eventBufferResetPerformed,
                Contracts: contracts);
        }
        finally
        {
            if (communication.Health == PlcsimCommunicationHealthState.Healthy)
            {
                var stop = await communication.StopEventStreamMonitoringAsync(
                    new PlcsimEventStreamStopRequest(
                        communication.Communication.SessionId,
                        correlationPrefix + "-stop-monitoring"),
                    CancellationToken.None);
                Assert.True(stop.Succeeded, FormatDiagnostics(stop.Diagnostics, stop.Evidence));
            }

            await communication.CloseAsync(CancellationToken.None);
        }
    }

    private static EventStreamWorkerResources CaptureEventStreamWorkerResources(int processId)
    {
        using var process = Process.GetProcessById(processId);
        process.Refresh();
        return new EventStreamWorkerResources(process.TotalProcessorTime, process.PrivateMemorySize64);
    }

    private static async Task<RunEvidenceWaitResult> WaitForRunEvidenceAsync(
        List<UnitTestEventBatch> batches,
        object batchLock,
        int startIndex,
        uint previousSequenceId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            UnitTestEventBatch[] observed;
            lock (batchLock)
            {
                observed = batches.Skip(startIndex).ToArray();
            }

            var entries = observed
                .SelectMany(batch => batch.Events)
                .Where(entry => entry.SequenceId > previousSequenceId)
                .ToArray();
            if (observed.Any(batch => batch.LostEventCount > 0))
            {
                return new RunEvidenceWaitResult(observed, Complete: false, EventLossObserved: true);
            }
            var completed = entries.FirstOrDefault(entry =>
                entry.OwnerScope == UnitTestOwnerScope.RunControl
                && entry.EventType == UnitTestEventType.StateTransition
                && entry.State == UnitTestLifecycleState.Completed);
            var completeEvidence =
                entries.Any(entry =>
                    entry.OwnerScope == UnitTestOwnerScope.RunControl
                    && entry.EventType == UnitTestEventType.StateTransition
                    && entry.State == UnitTestLifecycleState.Starting)
                && completed is not null
                && entries.Any(entry => entry.OwnerScope == UnitTestOwnerScope.TestCase && entry.EventType == UnitTestEventType.Result)
                && entries.Any(entry => entry.OwnerScope == UnitTestOwnerScope.TestCase && entry.EventType == UnitTestEventType.Aggregation)
                && entries.Any(entry => entry.OwnerScope == UnitTestOwnerScope.TestSuite && entry.EventType == UnitTestEventType.Result)
                && entries.Any(entry => entry.OwnerScope == UnitTestOwnerScope.TestSuite && entry.EventType == UnitTestEventType.Aggregation)
                && entries.Any(entry => entry.OwnerScope == UnitTestOwnerScope.RunControl && entry.EventType == UnitTestEventType.Result)
                && entries.Any(entry => entry.OwnerScope == UnitTestOwnerScope.RunControl && entry.EventType == UnitTestEventType.Aggregation);
            var emptyAfterCompletion = completed is not null
                && observed.Any(batch =>
                    batch.BufferObservedEmpty
                    && batch.Events.Count == 0
                    && batch.ProducerSequenceId >= completed.SequenceId);

            if (completeEvidence && emptyAfterCompletion)
            {
                return new RunEvidenceWaitResult(observed, Complete: true, EventLossObserved: false);
            }

            await Task.Delay(50);
        }

        UnitTestEventBatch[] timedOut;
        lock (batchLock)
        {
            timedOut = batches.Skip(startIndex).ToArray();
        }

        return new RunEvidenceWaitResult(timedOut, Complete: false, EventLossObserved: false);
    }

    private static async Task<PlcsimEventBufferValidationResult> WaitForCleanEventBufferAsync(
        IPlcsimUnitTestCommunicationSession communication,
        string correlationPrefix)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        PlcsimEventBufferValidationResult? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            latest = await communication.ValidateEventBufferAsync(
                new PlcsimEventBufferValidationRequest(
                    communication.Communication.SessionId,
                    UnitTestContractVersions.EventStreamModel,
                    correlationPrefix + "-wait-clean-eventbuffer"),
                CancellationToken.None);
            if (latest.Succeeded
                && latest.ContractValid
                && latest.Observation.BufferObservedEmpty
                && latest.Observation.LostEventCount == 0)
            {
                return latest;
            }

            await Task.Delay(50);
        }

        Assert.Fail(
            "The EventBuffer did not reach an empty, loss-free state after the reset command. "
            + (latest is null
                ? "No validation result was observed."
                : FormatDiagnostics(latest.Diagnostics, latest.Evidence)));
        throw new InvalidOperationException();
    }

    private static async Task<PlcsimEventStreamMonitoringState> WaitForEmptyEventBufferAsync(
        List<PlcsimEventStreamMonitoringState> monitoringStates,
        object monitoringStateLock,
        int startIndex)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (monitoringStateLock)
            {
                var cleanState = monitoringStates
                    .Skip(startIndex)
                    .LastOrDefault(state => state.IsMonitoring
                        && state.LastReadState == EventBufferReadState.Empty
                        && state.BatchReleaseState == EventStreamBatchReleaseState.Idle
                        && state.BufferObservedEmpty
                        && state.LostEventCount == 0
                        && state.ProducerSequenceId == state.DeletedSequenceId);
                if (cleanState is not null)
                {
                    return cleanState;
                }
            }

            await Task.Delay(25);
        }

        PlcsimEventStreamMonitoringState[] latestStates;
        lock (monitoringStateLock)
        {
            latestStates = monitoringStates.Skip(startIndex).TakeLast(5).ToArray();
        }

        Assert.Fail(
            "The EventBuffer did not reach a confirmed empty monitoring state before the UnitTest run. Observed: "
            + string.Join(
                " | ",
                latestStates.Select(state =>
                    $"monitoring={state.IsMonitoring}, read={state.LastReadState}, release={state.BatchReleaseState}, empty={state.BufferObservedEmpty}, lost={state.LostEventCount}, producer={state.ProducerSequenceId}, deleted={state.DeletedSequenceId}")));
        throw new InvalidOperationException();
    }

    private static void WriteRealCapacityMeasurement(
        string outputPath,
        int configuredPlcMinimumCycleTimeMs,
        PlcsimCommunicationObservationStrategyKind observationStrategy,
        TimeSpan? monitoringInterval,
        TimeSpan drainInterval,
        int? maxEventsPerBatch,
        bool rawEventLoggingEnabled,
        string sourceRevision,
        string workloadId,
        string measurementRole,
        int intendedRunCount,
        RealCapacityContractEvidence? contracts,
        bool eventBufferResetBeforeMeasurement,
        RealObservationLoopSummary observationSummary,
        IReadOnlyCollection<RealEventStreamRunMeasurement> runs)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var measurement = new RealCapacityMeasurement(
            SchemaVersion: 3,
            RecordedAtUtc: DateTimeOffset.UtcNow,
            SourceRevision: sourceRevision,
            WorkloadId: workloadId,
            MeasurementRole: measurementRole,
            BuildConfiguration: GetBuildConfiguration(),
            RuntimePlatform: new RealRuntimePlatform(
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture.ToString(),
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.MachineName,
                Environment.ProcessorCount),
            Settings: new RealCapacityMeasurementSettings(
                configuredPlcMinimumCycleTimeMs,
                observationStrategy.ToString(),
                monitoringInterval?.TotalMilliseconds,
                drainInterval.TotalMilliseconds,
                maxEventsPerBatch,
                rawEventLoggingEnabled,
                intendedRunCount),
            Contracts: contracts,
            EventBufferResetBeforeMeasurement: eventBufferResetBeforeMeasurement,
            ObservationLoop: observationSummary,
            Runs: runs.ToArray());
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        File.WriteAllText(fullPath, JsonSerializer.Serialize(measurement, options));
        Console.WriteLine($"Real EventStream capacity evidence written: {fullPath}");
    }

    private static PlcsimCommunicationObservationStrategyKind ReadObservationStrategyEnvironmentVariable()
    {
        var value = Environment.GetEnvironmentVariable(RealCommunicationObservationStrategyVariable);
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "Polling", StringComparison.OrdinalIgnoreCase))
        {
            return PlcsimCommunicationObservationStrategyKind.Polling;
        }

        Assert.True(
            string.Equals(value, "PlcCycle", StringComparison.OrdinalIgnoreCase),
            $"{RealCommunicationObservationStrategyVariable} must be 'Polling' or 'PlcCycle'.");
        return PlcsimCommunicationObservationStrategyKind.PlcCycle;
    }

    private static RealObservationLoopSummary ReadObservationLoopSummary(string logDirectory)
    {
        var log = ReadWorkerLog(logDirectory, "siemens-test");
        var matches = Regex.Matches(
            log,
            @"UnitTest communication observation summary\. CommunicationSessionId=\S+ Strategy=(?<strategy>\S+) DurationMs=(?<duration>[0-9.,]+) Wakeups=(?<wakeups>\d+) Observations=(?<observations>\d+) NoEventWakeups=(?<noEvents>\d+) SuccessfulReads=(?<successfulReads>\d+) EmptyReads=(?<emptyReads>\d+) BatchReads=(?<batchReads>\d+) EventsRead=(?<eventsRead>\d+)");
        var match = Assert.Single(matches.Cast<Match>());
        return new RealObservationLoopSummary(
            match.Groups["strategy"].Value,
            double.Parse(match.Groups["duration"].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(match.Groups["wakeups"].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(match.Groups["observations"].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(match.Groups["noEvents"].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(match.Groups["successfulReads"].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(match.Groups["emptyReads"].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(match.Groups["batchReads"].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(match.Groups["eventsRead"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string GetBuildConfiguration()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    private static async Task WaitForRunControlObservationAsync(
        List<PlcsimRunControlObservation> observations,
        object observationLock,
        int startIndex,
        Func<PlcsimRunControlObservation, bool> predicate,
        string expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (observationLock)
            {
                if (observations.Skip(startIndex).Any(predicate))
                {
                    return;
                }
            }

            await Task.Delay(50);
        }

        PlcsimRunControlObservation[] observed;
        lock (observationLock)
        {
            observed = observations.Skip(startIndex).ToArray();
        }

        Assert.Fail(
            $"RunControl observation '{expected}' was not received. Observed: "
            + string.Join(
                ", ",
                observed.Select(observation =>
                    $"{observation.ExecutionStatus}/{observation.State}/{observation.ExecutionSignals}")));
    }

    private static async Task WaitForRuntimeStateAsync(
        List<PlcsimRuntimeStateChange> changes,
        object changeLock,
        PlcsimRuntimeControlState expectedState)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (changeLock)
            {
                if (changes.Any(change =>
                    change.Kind == PlcsimRuntimeStateChangeKind.OperatingStateChanged
                    && change.CurrentState == expectedState))
                {
                    return;
                }
            }

            await Task.Delay(100);
        }

        IReadOnlyList<PlcsimRuntimeStateChange> observed;
        lock (changeLock)
        {
            observed = changes.ToArray();
        }

        Assert.Fail(
            $"Runtime state event '{expectedState}' was not observed. Observed: "
            + string.Join(", ", observed.Select(change => $"{change.Kind}:{change.CurrentState}")));
    }

    internal static PlcsimRuntimeSessionOpenRequest CreateOpenRequest()
    {
        return new PlcsimRuntimeSessionOpenRequest(
            new PlcsimRuntimeApiExpectation("8.0", "8.0", SiemensRuntimeLocality.Local),
            new PlcsimRuntimeReference(null, "PlcUnderTest"),
            new PlcsimRuntimeStoragePreference(null),
            PlcsimRuntimeCleanupPreference.Keep,
            null);
    }

    internal static PlcsimAdvancedV8Binding CreateBindingWithWorker(
        StubPlcsimAdvancedV8WorkerSessionHandle worker)
    {
        var binding = new PlcsimAdvancedV8Binding(
            new StubPlcsimAdvancedV8EnvironmentProbe(
                ReadyProbeWithRuntimeApiDirectory("C:\\PLCSIMADV\\API\\8.0")),
            new StubPlcsimAdvancedV8WorkerSessionHost(
                PlcsimAdvancedV8WorkerSessionHostOpenResult.SucceededResult(
                    worker,
                    Array.Empty<PlcsimEvidence>())));
        binding.Initialize(CreateBindingContext());
        return binding;
    }

    internal static PlcsimCommunicationPrepareRequest CreateCommunicationPrepareRequest(
        string runtimeSessionId)
    {
        return new PlcsimCommunicationPrepareRequest(
            runtimeSessionId,
            "DB_UnitTestRunControl",
            "DB_UnitTestEventBuffer",
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            UnitTestContractVersions.EventStreamModel,
            CreateWorkerOptions(),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            "prepare");
    }

    internal static PlcsimCommunicationWorkerOptions CreateWorkerOptions()
    {
        return new PlcsimCommunicationWorkerOptions(
            PlcsimCommunicationObservationStrategyKind.Polling,
            TimeSpan.FromMilliseconds(100));
    }

    internal static PlcsimCommunicationWorkerOptions CreatePlcCycleWorkerOptions()
    {
        return new PlcsimCommunicationWorkerOptions(
            PlcsimCommunicationObservationStrategyKind.PlcCycle,
            TimeSpan.FromMilliseconds(100));
    }

    internal static PlcsimCommunicationSessionInfo CreateCommunicationInfo(IPlcsimAdvancedV8WorkerSessionHandle session)
    {
        return new PlcsimCommunicationSessionInfo(
            session.SessionId + "-communication",
            session.SessionId,
            "DB_UnitTestRunControl",
            "DB_UnitTestEventBuffer",
            DateTimeOffset.UtcNow);
    }

    internal static UnitTestEventBatch CreateEventBatch(string communicationSessionId, uint sequenceId)
    {
        return new UnitTestEventBatch(
            new CommunicationSessionId(communicationSessionId),
            new[]
            {
                new UnitTestEventEntry(
                    sequenceId,
                    DateTimeOffset.UtcNow,
                    UnitTestOwnerScope.Unknown,
                    UnitTestEventType.Unknown,
                    default,
                    string.Empty,
                    string.Empty,
                    UnitTestLifecycleState.Unknown,
                    UnitTestResultState.Unknown,
                    string.Empty)
            },
            ProducerSequenceId: sequenceId,
            DeletedSequenceId: 0,
            LostEventCount: 0,
            BufferObservedEmpty: false,
            ReadState: EventBufferReadState.Valid);
    }

    internal static SiemensBindingContext CreateBindingContext(
        string? logDirectory = null,
        bool rawEventLoggingEnabled = false,
        LogLevel logLevel = LogLevel.Debug)
    {
        return new SiemensBindingContext(
            "siemens-test",
            new OperationalLoggingOptions(
                logDirectory ?? CreateTestLogDirectory(),
                logLevel,
                10 * 1024 * 1024,
                5,
                rawEventLoggingEnabled));
    }

    internal static string CreateTestLogDirectory()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "PLCSIM.UnitTest",
            Guid.NewGuid().ToString("N"),
            "logs");
    }

    internal static string ReadWorkerLog(string logDirectory, string accessProfileId)
    {
        var logPath = Assert.Single(Directory.GetFiles(
            logDirectory,
            "access-profile-" + accessProfileId + "-plcsim-advanced-v8-worker-*.log"));
        return File.ReadAllText(logPath);
    }

    private static string ResolveBuiltWorkerPath()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "plcsim-advanced",
                "v8",
                "worker",
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker",
                "bin",
                configuration,
                "net48",
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V8.Worker.exe");
    }

    private static string ResolveBuiltValidationWorkerPath()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "plcsim-advanced",
                "v8",
                "tests",
                "worker",
                "ValidationWorker",
                "bin",
                configuration,
                "net48",
                "PLCSIM.UnitTest.PlcsimAdvanced.V8.ValidationWorker.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "PLCSIM.UnitTest.PlcsimAdvanced.V8.ValidationWorker.exe");
    }

    private static string ResolveBuiltPerformanceMeasurementWorkerPath()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "access-profiles",
                "siemens",
                "plcsim-advanced",
                "v8",
                "tests",
                "worker",
                "PerformanceMeasurementWorker",
                "bin",
                configuration,
                "net48",
                "PLCSIM.UnitTest.PlcsimAdvanced.V8.PerformanceMeasurementWorker.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "PLCSIM.UnitTest.PlcsimAdvanced.V8.PerformanceMeasurementWorker.exe");
    }

    private static PlcsimRuntimePrepareRequest CreateOwnedPrepareRequest(string correlationId = "compensation")
    {
        return new PlcsimRuntimePrepareRequest(
            new PlcsimRuntimeReference(null, "CompensationTestRuntime"),
            new PlcsimRuntimeStoragePreference("D:\\RuntimeStorage\\CompensationTestRuntime"),
            PlcsimRuntimeProvisioningMode.CreateOwned,
            PlcsimRuntimeCpuType.S71500,
            TimeSpan.FromSeconds(5),
            correlationId);
    }

    internal static async Task<IPlcsimAdvancedV8WorkerSessionHandle> OpenValidationWorkerSessionAsync(
        string validationBehavior,
        TimeSpan? commandResponseTimeout = null,
        string? logDirectory = null,
        bool rawEventLoggingEnabled = false,
        LogLevel logLevel = LogLevel.Debug)
    {
        var workerPath = ResolveBuiltValidationWorkerPath();
        var effectiveLogDirectory = logDirectory ?? CreateTestLogDirectory();
        var host = new PlcsimAdvancedV8WorkerSessionHost(
            () => workerPath,
            commandResponseTimeout: commandResponseTimeout ?? TimeSpan.FromSeconds(5));

        var result = await host.OpenAsync(
            string.IsNullOrWhiteSpace(validationBehavior) ? "validation" : validationBehavior,
            CreateBindingContext(effectiveLogDirectory, rawEventLoggingEnabled, logLevel),
            CancellationToken.None);
        Assert.True(
            result.Succeeded,
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)
                    .Concat(result.Evidence.Select(evidence => evidence.Kind + ": " + evidence.Summary))
                    .Append("WorkerPath: " + workerPath)
                    .Append("LogDirectory: " + effectiveLogDirectory)));
        return Assert.IsAssignableFrom<IPlcsimAdvancedV8WorkerSessionHandle>(result.Session);
    }

    internal static async Task<ValidationEventStreamMonitoringResult> StartValidationEventStreamMonitoringAsync(
        string validationBehavior,
        string? logDirectory = null,
        bool rawEventLoggingEnabled = false,
        LogLevel logLevel = LogLevel.Debug,
        PlcsimCommunicationWorkerOptions? workerOptions = null)
    {
        var worker = await OpenValidationWorkerSessionAsync(
            validationBehavior,
            logDirectory: logDirectory,
            rawEventLoggingEnabled: rawEventLoggingEnabled,
            logLevel: logLevel);
        var prepare = await worker.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                worker.SessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                UnitTestContractVersions.EventStreamModel,
                workerOptions ?? CreateWorkerOptions(),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                "prepare"),
            CancellationToken.None);
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);
        var batches = new List<UnitTestEventBatch>();
        var monitoringStates = new List<PlcsimEventStreamMonitoringState>();
        communication.EventBatchReceived += (_, args) => batches.Add(args.Batch);
        communication.EventStreamMonitoringStateChanged += (_, args) => monitoringStates.Add(args.State);
        var monitoring = await communication.StartEventStreamMonitoringAsync(
            new PlcsimEventStreamMonitoringRequest(
                communication.Communication.SessionId,
                "start"),
            CancellationToken.None);

        return new ValidationEventStreamMonitoringResult(worker, communication, monitoring, batches, monitoringStates);
    }

    internal sealed record SessionWeakReferences(
        WeakReference Runtime,
        WeakReference Communication);

    private sealed record WorkerResourceSnapshot(
        int Cycle,
        long PrivateMemoryBytes,
        int HandleCount,
        int ThreadCount);

    private sealed record EventStreamWorkerResources(
        TimeSpan ProcessorTime,
        long PrivateMemoryBytes);

    private sealed record RunEvidenceWaitResult(
        IReadOnlyList<UnitTestEventBatch> Batches,
        bool Complete,
        bool EventLossObserved);

    private sealed record RealCommunicationCycleValidationResult(
        bool CapacityAvailable,
        bool EventBufferResetPerformed,
        RealCapacityContractEvidence Contracts);

    private sealed record RealCapacityMeasurement(
        int SchemaVersion,
        DateTimeOffset RecordedAtUtc,
        string SourceRevision,
        string WorkloadId,
        string MeasurementRole,
        string BuildConfiguration,
        RealRuntimePlatform RuntimePlatform,
        RealCapacityMeasurementSettings Settings,
        RealCapacityContractEvidence? Contracts,
        bool EventBufferResetBeforeMeasurement,
        RealObservationLoopSummary ObservationLoop,
        IReadOnlyList<RealEventStreamRunMeasurement> Runs);

    private sealed record RealRuntimePlatform(
        string Framework,
        string OperatingSystem,
        string OperatingSystemArchitecture,
        string ProcessArchitecture,
        string MachineName,
        int LogicalProcessorCount);

    private sealed record RealCapacityMeasurementSettings(
        int ConfiguredPlcMinimumCycleTimeMs,
        string ObservationStrategy,
        double? MonitoringIntervalMs,
        double DrainIntervalMs,
        int? MaxEventsPerBatch,
        bool RawEventLoggingEnabled,
        int IntendedRunCount);

    private sealed record RealCapacityContractEvidence(
        string? RunControlContractVersion,
        string? RunControlFrameworkVersion,
        string? EventStreamContractVersion,
        string? EventStreamFrameworkVersion);

    private sealed record RealObservationLoopSummary(
        string Strategy,
        double DurationMs,
        long Wakeups,
        long Observations,
        long NoEventWakeups,
        long SuccessfulReads,
        long EmptyReads,
        long BatchReads,
        long EventsRead);

    private sealed record RealEventPayloadReadMeasurement(
        int SchemaVersion,
        DateTimeOffset RecordedAtUtc,
        string SourceRevision,
        string BuildConfiguration,
        string Mode,
        int ConfiguredPlcMinimumCycleTimeMs,
        string ObservationStrategy,
        double? MonitoringIntervalMs,
        double DrainIntervalMs,
        bool RawEventLoggingEnabled,
        int TargetEventCount,
        int EventCount,
        int CapturedEventCount,
        string ReadState,
        double SnapshotDurationMs,
        double PreMetadataDurationMs,
        double EntryReadDurationMs,
        double PostMetadataDurationMs);

    private sealed record RealEventPayloadReadTiming(
        int EventCount,
        int CapturedEventCount,
        string ReadState,
        double SnapshotDurationMs,
        double PreMetadataDurationMs,
        double EntryReadDurationMs,
        double PostMetadataDurationMs);

    private sealed record RealEventStreamRunMeasurement(
        string Phase,
        int Cycle,
        DateTimeOffset StartedAtUtc,
        string Outcome,
        string RunResult,
        double DurationMs,
        int BatchCount,
        int EventCount,
        uint? FirstSequenceId,
        uint? LastSequenceId,
        int MaximumBatchEvents,
        uint MaximumObservedBacklog,
        ushort MaximumLostEvents,
        double WorkerCpuMs,
        long WorkerPrivateMemoryDeltaBytes,
        IReadOnlyList<RealEventStreamEventBreakdown> EventBreakdown,
        RealEventStreamCleanStartEvidence CleanStart);

    private sealed record RealEventStreamEventBreakdown(
        UnitTestOwnerScope OwnerScope,
        UnitTestEventType EventType,
        UnitTestLifecycleState State,
        UnitTestResultState Result,
        string Name,
        string ResourceName,
        int Count);

    private sealed record RealEventStreamCleanStartEvidence(
        uint ProducerSequenceId,
        uint DeletedSequenceId,
        ushort LostEventCount,
        bool BufferObservedEmpty);

    internal sealed class ValidationEventStreamMonitoringResult
    {
        public ValidationEventStreamMonitoringResult(
            IPlcsimAdvancedV8WorkerSessionHandle worker,
            IPlcsimUnitTestCommunicationSession communication,
            PlcsimEventStreamMonitoringResult monitoring,
            IReadOnlyList<UnitTestEventBatch> batches,
            IReadOnlyList<PlcsimEventStreamMonitoringState> monitoringStates)
        {
            Worker = worker;
            Communication = communication;
            Monitoring = monitoring;
            Batches = batches;
            MonitoringStates = monitoringStates;
        }

        public IPlcsimAdvancedV8WorkerSessionHandle Worker { get; }

        public IPlcsimUnitTestCommunicationSession Communication { get; }

        public PlcsimEventStreamMonitoringResult Monitoring { get; }

        public IReadOnlyList<UnitTestEventBatch> Batches { get; }

        public IReadOnlyList<PlcsimEventStreamMonitoringState> MonitoringStates { get; }
    }


}
