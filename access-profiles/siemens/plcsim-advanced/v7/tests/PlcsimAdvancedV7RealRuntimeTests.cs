using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.PlcsimAdvanced.V7;

public sealed class PlcsimAdvancedV7RealRuntimeTests
{
    private const string RealWorkerSessionValidationVariable = "PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V7_WORKER_SESSION";
    private const string RealCreateOwnedValidationVariable = "PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V7_CREATEOWNED";
    private const string RealExistingStartStopValidationVariable = "PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V7_EXISTING_STARTSTOP";
    private const string RealExistingCommunicationValidationVariable = "PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V7_COMMUNICATION";
    private const string RealCommunicationObservationStrategyVariable = "PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V7_OBSERVATION_STRATEGY";
    private const string RealLifetimeValidationVariable = "PLCSIM_UNITTEST_VALIDATE_PLCSIM_ADVANCED_V7_LIFETIMES";

    [Fact]
    public async Task V7_binding_can_open_and_close_real_worker_session_when_enabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(RealWorkerSessionValidationVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var binding = new PlcsimAdvancedV7Binding();
        binding.Initialize(CreateBindingContext());
        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);

        Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
        var session = Assert.IsType<PlcsimAdvancedV7RuntimeSession>(open.Session);
        var workerProcessId = Assert.IsType<int>(session.WorkerProcessId);

        await session.CloseAsync(CancellationToken.None);

        Assert.True(WaitForProcessExit(workerProcessId));
    }

    [Fact]
    public async Task V7_binding_can_validate_real_create_owned_lifecycle_when_enabled()
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
        var binding = new PlcsimAdvancedV7Binding();
        binding.Initialize(CreateBindingContext());
        var open = await binding.OpenRuntimeSessionAsync(
            new PlcsimRuntimeSessionOpenRequest(
                new PlcsimRuntimeApiExpectation("7.0", "7.0", SiemensRuntimeLocality.Local),
                new PlcsimRuntimeReference(null, instanceName),
                new PlcsimRuntimeStoragePreference(storagePath),
                PlcsimRuntimeCleanupPreference.Release,
                "real-create-owned-validation"),
            CancellationToken.None);

        Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
        Assert.NotNull(open.Session);
        var session = open.Session;
        var concreteSession = Assert.IsType<PlcsimAdvancedV7RuntimeSession>(session);
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
    public async Task V7_binding_can_validate_real_existing_runtime_start_stop_when_enabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(RealExistingStartStopValidationVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var binding = new PlcsimAdvancedV7Binding();
        binding.Initialize(CreateBindingContext());
        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
        Assert.NotNull(open.Session);
        var session = open.Session!;
        var concreteSession = Assert.IsType<PlcsimAdvancedV7RuntimeSession>(session);
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
                diagnostic => diagnostic.Code == "PlcsimAdvanced.V7.Worker.RuntimeIdentityMismatch");
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
    public async Task V7_binding_can_validate_real_existing_runtime_unit_test_communication_when_enabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(RealExistingCommunicationValidationVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var binding = CreateApi7CompatibilityRuntimeBinding();
        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
        Assert.NotNull(open.Session);
        var session = open.Session!;
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
            Assert.Equal(PlcsimRuntimeControlState.Stop, prepared.Runtime?.State);

            var started = await session.StartRuntimeAsync(
                new PlcsimRuntimeControlRequest(
                    session.Runtime.SessionId,
                    TimeSpan.FromSeconds(60),
                    "real-communication-validation-start"),
                CancellationToken.None);
            Assert.True(started.Succeeded, FormatDiagnostics(started.Diagnostics, started.Evidence));
            await WaitForRuntimeStateAsync(runtimeChanges, runtimeChangeLock, PlcsimRuntimeControlState.Run);

            await ValidateRealUnitTestCommunicationCyclesAsync(
                binding,
                session.Runtime.SessionId,
                cycleCount: 3,
                "real-communication-validation-initial");

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

            await ValidateRealUnitTestCommunicationCyclesAsync(
                binding,
                session.Runtime.SessionId,
                cycleCount: 1,
                "real-communication-validation-recovery");
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
        }
    }

    [Fact]
    public async Task V7_binding_can_validate_real_repeated_session_lifetimes_when_enabled()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(RealLifetimeValidationVariable),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var initialWorkerIds = GetV7WorkerProcessIds();
        var binding = new PlcsimAdvancedV7Binding();
        binding.Initialize(CreateBindingContext());
        var open = await binding.OpenRuntimeSessionAsync(CreateOpenRequest(), CancellationToken.None);
        Assert.True(open.Succeeded, FormatDiagnostics(open.Diagnostics, open.Evidence));
        var runtime = Assert.IsAssignableFrom<IPlcsimRuntimeSession>(open.Session);
        var workerProcessId = await WaitForNewV7WorkerProcessAsync(initialWorkerIds);
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

        await WaitForV7WorkerProcessesAsync(initialWorkerIds);
        await ValidateRepeatedRealCreateOwnedLifetimesAsync(initialWorkerIds, cycleCount: 3);
        await WaitForV7WorkerProcessesAsync(initialWorkerIds);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async Task<SessionWeakReferences> CreateClosedSessionWeakReferencesAsync()
    {
        var worker = new StubPlcsimAdvancedV7WorkerSessionHandle("collectable-runtime-session");
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
            var binding = new PlcsimAdvancedV7Binding();
            binding.Initialize(CreateBindingContext());
            var open = await binding.OpenRuntimeSessionAsync(
                new PlcsimRuntimeSessionOpenRequest(
                    new PlcsimRuntimeApiExpectation("7.0", "7.0", SiemensRuntimeLocality.Local),
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
            await WaitForV7WorkerProcessesAsync(initialWorkerIds);
        }
    }

    private static IReadOnlySet<int> GetV7WorkerProcessIds()
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

    private static async Task<int> WaitForNewV7WorkerProcessAsync(IReadOnlySet<int> initialWorkerIds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var newIds = GetV7WorkerProcessIds()
                .Where(processId => !initialWorkerIds.Contains(processId))
                .ToArray();
            if (newIds.Length == 1)
            {
                return newIds[0];
            }

            Assert.True(newIds.Length <= 1, $"Expected one PLCSIM Advanced V7 worker, observed: {string.Join(", ", newIds)}");
            await Task.Delay(50);
        }

        Assert.Fail("PLCSIM Advanced V7 worker process was not observed.");
        return 0;
    }

    private static async Task WaitForV7WorkerProcessesAsync(IReadOnlySet<int> allowedWorkerIds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var unexpected = GetV7WorkerProcessIds()
                .Where(processId => !allowedWorkerIds.Contains(processId))
                .ToArray();
            if (unexpected.Length == 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        var remaining = GetV7WorkerProcessIds()
            .Where(processId => !allowedWorkerIds.Contains(processId))
            .ToArray();
        Assert.Fail($"PLCSIM Advanced V7 worker processes remained after cleanup: {string.Join(", ", remaining)}");
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
        Console.WriteLine($"PLCSIM Advanced V7 worker resource evidence: {evidence}");

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

    internal static PlcsimAdvancedV7CommunicationSession CreateFacadeCommunicationSession(
        IPlcsimAdvancedV7WorkerSessionHandle worker)
    {
        return new PlcsimAdvancedV7CommunicationSession(
            worker,
            CreateCommunicationInfo(worker),
            UnitTestContractVersions.PlcsimAdvancedCommunication,
            "test-framework",
            UnitTestContractVersions.EventStreamModel,
            "test-framework");
    }

    internal static PlcsimAdvancedV7EnvironmentProbeResult ReadyProbeWithRuntimeApiDirectory(string runtimeApiDirectory)
    {
        return PlcsimAdvancedV7EnvironmentProbeResult.Ready(new[]
        {
            new PlcsimEvidence(
                "plcsim-advanced-v7-api-directory",
                "RuntimeApiDirectory",
                runtimeApiDirectory),
            new PlcsimEvidence(
                "plcsim-advanced-v7-runtime-api-dll",
                "RuntimeApiDll",
                Path.Combine(runtimeApiDirectory, "Siemens.Simatic.Simulation.Runtime.Api.x64.dll"))
        });
    }

    private static PlcsimAdvancedV7Binding CreateApi7CompatibilityRuntimeBinding()
    {
        var registry = new PlcsimAdvancedV7EnvironmentEvidenceSource().ReadRegistryEvidence();
        Assert.True(registry.HasRegistryKey && !string.IsNullOrWhiteSpace(registry.InstallPath));

        var runtimeApiDirectory = Path.Combine(
            registry.InstallPath!,
            PlcsimAdvancedV7EnvironmentProbe.ApiVersionSubdirectory);
        var binding = new PlcsimAdvancedV7Binding(
            new StubPlcsimAdvancedV7EnvironmentProbe(
                ReadyProbeWithRuntimeApiDirectory(runtimeApiDirectory)),
            new PlcsimAdvancedV7WorkerSessionHost());
        binding.Initialize(CreateBindingContext());
        return binding;
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

    private static async Task ValidateRealUnitTestCommunicationCyclesAsync(
        PlcsimAdvancedV7Binding binding,
        string runtimeSessionId,
        int cycleCount,
        string correlationPrefix)
    {
        var usePlcCycle = string.Equals(
            Environment.GetEnvironmentVariable(RealCommunicationObservationStrategyVariable),
            "plcCycle",
            StringComparison.OrdinalIgnoreCase);
        var prepare = await binding.PrepareCommunicationAsync(
            new PlcsimCommunicationPrepareRequest(
                runtimeSessionId,
                "DB_UnitTestRunControl",
                "DB_UnitTestEventBuffer",
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                UnitTestContractVersions.EventStreamModel,
                new PlcsimCommunicationWorkerOptions(
                    usePlcCycle
                        ? PlcsimCommunicationObservationStrategyKind.PlcCycle
                        : PlcsimCommunicationObservationStrategyKind.Polling,
                    usePlcCycle ? null : TimeSpan.FromMilliseconds(10))
                {
                    DrainInterval = TimeSpan.FromMilliseconds(1),
                    MaxEventsPerBatch = 32
                },
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30),
                correlationPrefix + "-prepare"),
            CancellationToken.None);
        Assert.True(prepare.Succeeded, FormatDiagnostics(prepare.Diagnostics, prepare.Evidence));
        var communication = Assert.IsAssignableFrom<IPlcsimUnitTestCommunicationSession>(prepare.Session);
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
            if (eventBuffer.Observation.LostEventCount > 0)
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

                await WaitForEmptyEventBufferAsync(
                    monitoringStates,
                    monitoringStateLock,
                    cleanMonitoringStateStartIndex);

                int batchStartIndex;
                uint previousSequenceId;
                lock (batchLock)
                {
                    batchStartIndex = batches.Count;
                    previousSequenceId = batches
                        .SelectMany(batch => batch.Events)
                        .Select(entry => entry.SequenceId)
                        .DefaultIfEmpty(0u)
                        .Max();
                }

                var start = await communication.RequestRunControlCommandAsync(
                    new PlcsimRunControlCommandRequest(
                        communication.Communication.SessionId,
                        UnitTestRunCommand.Start,
                        TimeSpan.FromSeconds(10),
                        $"{correlationPrefix}-start-{cycle}"),
                    CancellationToken.None);
                Assert.True(start.Succeeded && start.Accepted, FormatDiagnostics(start.Diagnostics, start.Evidence));

                var runBatches = await WaitForCompletedRunEvidenceAsync(
                    batches,
                    batchLock,
                    batchStartIndex,
                    previousSequenceId);
                Assert.All(runBatches, batch => Assert.Equal((ushort)0, batch.LostEventCount));

                var entries = runBatches
                    .SelectMany(batch => batch.Events)
                    .Where(entry => entry.SequenceId > previousSequenceId)
                    .ToArray();
                Assert.Equal(entries.Length, entries.Select(entry => entry.SequenceId).Distinct().Count());
                Assert.True(entries.SequenceEqual(entries.OrderBy(entry => entry.SequenceId)));
                Assert.All(entries, entry => Assert.True(entry.Timestamp > DateTimeOffset.UnixEpoch));

            }
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

    private static async Task<IReadOnlyList<UnitTestEventBatch>> WaitForCompletedRunEvidenceAsync(
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
                return observed;
            }

            await Task.Delay(50);
        }

        UnitTestEventBatch[] timedOut;
        lock (batchLock)
        {
            timedOut = batches.Skip(startIndex).ToArray();
        }

        Assert.Fail(
            "Complete real RunControl/EventBuffer evidence was not observed. Observed: "
            + string.Join(
                ", ",
                timedOut.SelectMany(batch => batch.Events).Select(entry =>
                    $"{entry.SequenceId}:{entry.OwnerScope}/{entry.EventType}/{entry.State}")));
        return Array.Empty<UnitTestEventBatch>();
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

    private static async Task WaitForEmptyEventBufferAsync(
        List<PlcsimEventStreamMonitoringState> monitoringStates,
        object monitoringStateLock,
        int startIndex)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (monitoringStateLock)
            {
                if (monitoringStates
                    .Skip(startIndex)
                    .Any(state => state.BufferObservedEmpty
                        && state.LostEventCount == 0
                        && state.ProducerSequenceId == state.DeletedSequenceId))
                {
                    return;
                }
            }

            await Task.Delay(25);
        }

        Assert.Fail("The EventBuffer did not become empty after the RunControl reset.");
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
            new PlcsimRuntimeApiExpectation("7.0", "7.0", SiemensRuntimeLocality.Local),
            new PlcsimRuntimeReference(null, "PlcUnderTest"),
            new PlcsimRuntimeStoragePreference(null),
            PlcsimRuntimeCleanupPreference.Keep,
            null);
    }

    internal static PlcsimAdvancedV7Binding CreateBindingWithWorker(
        StubPlcsimAdvancedV7WorkerSessionHandle worker)
    {
        var binding = new PlcsimAdvancedV7Binding(
            new StubPlcsimAdvancedV7EnvironmentProbe(
                ReadyProbeWithRuntimeApiDirectory("C:\\PLCSIMADV\\API\\7.0")),
            new StubPlcsimAdvancedV7WorkerSessionHost(
                PlcsimAdvancedV7WorkerSessionHostOpenResult.SucceededResult(
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

    internal static PlcsimCommunicationSessionInfo CreateCommunicationInfo(IPlcsimAdvancedV7WorkerSessionHandle session)
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
            "access-profile-" + accessProfileId + "-plcsim-advanced-v7-worker-*.log"));
        return File.ReadAllText(logPath);
    }

    internal static string ResolveBuiltWorkerPath()
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
                "v7",
                "worker",
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7.Worker",
                "bin",
                configuration,
                "net48",
                "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7.Worker.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7.Worker.exe");
    }

    internal static string ResolveBuiltValidationWorkerPath()
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
                "v7",
                "tests",
                "worker",
                "ValidationWorker",
                "bin",
                configuration,
                "net48",
                "PLCSIM.UnitTest.PlcsimAdvanced.V7.ValidationWorker.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "PLCSIM.UnitTest.PlcsimAdvanced.V7.ValidationWorker.exe");
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

    internal static async Task<IPlcsimAdvancedV7WorkerSessionHandle> OpenValidationWorkerSessionAsync(
        string validationBehavior,
        TimeSpan? commandResponseTimeout = null,
        string? logDirectory = null,
        bool rawEventLoggingEnabled = false,
        LogLevel logLevel = LogLevel.Debug)
    {
        var workerPath = ResolveBuiltValidationWorkerPath();
        var effectiveLogDirectory = logDirectory ?? CreateTestLogDirectory();
        var host = new PlcsimAdvancedV7WorkerSessionHost(
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
        return Assert.IsAssignableFrom<IPlcsimAdvancedV7WorkerSessionHandle>(result.Session);
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

    internal sealed class ValidationEventStreamMonitoringResult
    {
        public ValidationEventStreamMonitoringResult(
            IPlcsimAdvancedV7WorkerSessionHandle worker,
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

        public IPlcsimAdvancedV7WorkerSessionHandle Worker { get; }

        public IPlcsimUnitTestCommunicationSession Communication { get; }

        public PlcsimEventStreamMonitoringResult Monitoring { get; }

        public IReadOnlyList<UnitTestEventBatch> Batches { get; }

        public IReadOnlyList<PlcsimEventStreamMonitoringState> MonitoringStates { get; }
    }


}
