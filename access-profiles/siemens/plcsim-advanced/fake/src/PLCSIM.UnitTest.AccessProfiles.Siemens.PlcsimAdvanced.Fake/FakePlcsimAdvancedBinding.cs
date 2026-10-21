using System.Collections.Concurrent;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.Fake;

public sealed class FakePlcsimAdvancedBinding : IPlcsimAdvancedRuntimeBinding, IPlcsimUnitTestCommunicationBinding
{
    private readonly FakePlcsimBehavior behavior;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<string, FakePlcsimRuntimeSession> runtimeSessions = new(StringComparer.Ordinal);

    public FakePlcsimAdvancedBinding()
        : this(FakePlcsimBehavior.Default, TimeProvider.System)
    {
    }

    internal FakePlcsimAdvancedBinding(FakePlcsimBehavior behavior)
        : this(behavior, TimeProvider.System)
    {
    }

    internal FakePlcsimAdvancedBinding(FakePlcsimBehavior behavior, TimeProvider timeProvider)
    {
        this.behavior = behavior;
        this.timeProvider = timeProvider;
    }

    public void Initialize(SiemensBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }

    public SiemensBindingInfo Info { get; } = new(
        SiemensBindingKinds.PlcSimAdvanced,
        "1.0.0",
        "Fake PLCSIM Advanced Binding");

    public SiemensBindingDeclaration Declaration { get; } = new(
        new[]
        {
            new SiemensAccessApiDeclaration(
                SiemensBindingKinds.PlcSimAdvanced,
                "fake-8",
                "fake-8")
        },
        Array.Empty<SiemensProjectSourceDeclaration>(),
        new[]
        {
            new SiemensRuntimeDeclaration(
                SiemensBindingKinds.PlcSimAdvanced,
                "fake",
                SiemensRuntimeLocality.Local,
                new[] { SiemensProjectSourceKinds.SiemensFakeProject })
        },
        new[] { UnitTestContractVersions.PlcsimAdvancedCommunication },
        new[] { UnitTestContractVersions.EventStreamModel });

    public SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
    {
        return SiemensBindingReadinessResult.Usable("Fake PLCSIM Advanced binding is usable without Siemens runtime API detection.");
    }

    public async Task<PlcsimRuntimeSessionOpenResult> OpenRuntimeSessionAsync(
        PlcsimRuntimeSessionOpenRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await behavior.OpenRuntimeSession.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

        if (!behavior.OpenRuntimeSession.Succeeded)
        {
            return new PlcsimRuntimeSessionOpenResult(
                behavior.OpenRuntimeSession.Status,
                null,
                behavior.OpenRuntimeSession.CreateDiagnostics("OpenRuntimeSession", "FakePlcsim.Runtime.OpenScenario"),
                new[] { CreateEvidence("FakePlcsim.Runtime.OpenRejected", "Fake runtime open scenario rejected the operation.") });
        }

        var reference = request.RuntimeReference ?? new PlcsimRuntimeReference("fake-runtime", "Fake PLCSIM Runtime");
        var session = new FakePlcsimRuntimeSession(reference, behavior, timeProvider, UnregisterRuntimeSession);
        runtimeSessions[session.Runtime.SessionId] = session;
        return new PlcsimRuntimeSessionOpenResult(
            behavior.OpenRuntimeSession.Status,
            session,
            behavior.OpenRuntimeSession.CreateDiagnostics("OpenRuntimeSession", "FakePlcsim.Runtime.OpenScenario"),
            new[] { CreateEvidence("FakePlcsim.Runtime.Opened", "Runtime session opened.") });
    }

    public async Task<PlcsimCommunicationPrepareResult> PrepareCommunicationAsync(
        PlcsimCommunicationPrepareRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await behavior.PrepareCommunication.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

        if (!behavior.PrepareCommunication.Succeeded)
        {
            return new PlcsimCommunicationPrepareResult(
                behavior.PrepareCommunication.Status,
                null,
                behavior.PrepareCommunication.CreateDiagnostics("PrepareCommunication", "FakePlcsim.Communication.PrepareScenario"),
                new[] { CreateEvidence("FakePlcsim.Communication.PrepareRejected", "Fake communication prepare scenario rejected the operation.") });
        }

        if (string.IsNullOrWhiteSpace(request.RuntimeSessionId)
            || !runtimeSessions.TryGetValue(request.RuntimeSessionId, out var runtimeSession))
        {
            return CommunicationUnavailable(
                "FakePlcsim.Runtime.SessionNotFound",
                "The requested fake runtime session is not available for UnitTest communication preparation.");
        }

        if (!runtimeSession.CanPrepareCommunication)
        {
            return CommunicationUnavailable(
                "FakePlcsim.Runtime.NotRunning",
                "Fake UnitTest communication requires a healthy running runtime session.");
        }

        var diagnostics = ValidateContracts(request);
        if (diagnostics.Count > 0)
        {
            return new PlcsimCommunicationPrepareResult(
                PlcsimOperationStatus.Rejected,
                null,
                diagnostics,
                new[] { CreateEvidence("FakePlcsim.Communication.ContractRejected", "Communication contract validation failed.") });
        }

        var session = new FakePlcsimCommunicationSession(request, behavior, timeProvider, runtimeSession);
        if (session.Health != PlcsimCommunicationHealthState.Healthy)
        {
            session.DetachRuntimeEvents();
            return CommunicationUnavailable(
                "FakePlcsim.Runtime.NotRunning",
                "The fake runtime session stopped or became unavailable during UnitTest communication preparation.");
        }

        return new PlcsimCommunicationPrepareResult(
            behavior.PrepareCommunication.Status,
            session,
            behavior.PrepareCommunication.CreateDiagnostics("PrepareCommunication", "FakePlcsim.Communication.PrepareScenario"),
            new[] { CreateEvidence("FakePlcsim.Communication.Prepared", "UnitTest communication session prepared.") });
    }

    internal bool SimulateExternalRuntimeStateChange(
        string runtimeSessionId,
        PlcsimRuntimeControlState state)
    {
        return runtimeSessions.TryGetValue(runtimeSessionId, out var session)
            && session.SimulateExternalStateChange(state);
    }

    internal bool SimulateExternalRuntimeInvalidation(string runtimeSessionId)
    {
        return runtimeSessions.TryGetValue(runtimeSessionId, out var session)
            && session.SimulateExternalInvalidation();
    }

    private static PlcsimCommunicationPrepareResult CommunicationUnavailable(string code, string message)
    {
        return new PlcsimCommunicationPrepareResult(
            PlcsimOperationStatus.Unavailable,
            null,
            new[]
            {
                new PlcsimDiagnostic(
                    code,
                    PlcsimDiagnosticSeverity.Error,
                    message,
                    "PrepareCommunication",
                    code)
            },
            new[] { CreateEvidence(code, message) });
    }

    private void UnregisterRuntimeSession(string sessionId)
    {
        runtimeSessions.TryRemove(sessionId, out _);
    }

    private static List<PlcsimDiagnostic> ValidateContracts(PlcsimCommunicationPrepareRequest request)
    {
        var diagnostics = new List<PlcsimDiagnostic>();
        if (!string.Equals(request.ExpectedRunControlContractVersion, UnitTestContractVersions.PlcsimAdvancedCommunication, StringComparison.Ordinal))
        {
            diagnostics.Add(new PlcsimDiagnostic(
                "FakePlcsim.RunControl.ContractMismatch",
                PlcsimDiagnosticSeverity.Error,
                "The requested RunControl contract version is not supported by the fake PLCSIM binding.",
                "PrepareCommunication",
                "FakePlcsim.Communication.ContractRejected"));
        }

        if (!string.Equals(request.ExpectedEventStreamContractVersion, UnitTestContractVersions.EventStreamModel, StringComparison.Ordinal))
        {
            diagnostics.Add(new PlcsimDiagnostic(
                "FakePlcsim.EventBuffer.ContractMismatch",
                PlcsimDiagnosticSeverity.Error,
                "The requested EventBuffer contract version is not supported by the fake PLCSIM binding.",
                "PrepareCommunication",
                "FakePlcsim.Communication.ContractRejected"));
        }

        return diagnostics;
    }

    private static PlcsimEvidence CreateEvidence(string id, string summary)
    {
        return new PlcsimEvidence(id, "FakePlcsim", summary);
    }

    private sealed class FakePlcsimRuntimeSession : IPlcsimRuntimeSession
    {
        private readonly FakePlcsimBehavior behavior;
        private readonly TimeProvider timeProvider;
        private readonly Action<string> unregister;
        private PlcsimRuntimeControlState state = PlcsimRuntimeControlState.Stop;
        private PlcsimRuntimeTargetCandidate? preparedRuntime;
        private int unregistered;

        public FakePlcsimRuntimeSession(
            PlcsimRuntimeReference runtimeReference,
            FakePlcsimBehavior behavior,
            TimeProvider timeProvider,
            Action<string> unregister)
        {
            this.behavior = behavior;
            this.timeProvider = timeProvider;
            this.unregister = unregister;
            Runtime = new PlcsimRuntimeSessionInfo(
                Guid.NewGuid().ToString("N"),
                runtimeReference,
                true,
                DateTimeOffset.UtcNow);
        }

        public PlcsimRuntimeSessionInfo Runtime { get; }

        public PlcsimRuntimeHealthState Health { get; private set; } = PlcsimRuntimeHealthState.Healthy;

        public bool CanPrepareCommunication =>
            Health == PlcsimRuntimeHealthState.Healthy
            && preparedRuntime is not null
            && state == PlcsimRuntimeControlState.Run;

        public event EventHandler<PlcsimRuntimeHealthChangedEventArgs>? HealthChanged;

        public event EventHandler<PlcsimRuntimeStateChangedEventArgs>? RuntimeStateChanged;

        public event EventHandler<PlcsimRuntimeProgressSnapshot>? ProgressChanged;

        public async Task<PlcsimRuntimeDiscoveryResult> DiscoverRuntimeTargetsAsync(
            PlcsimRuntimeDiscoveryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            await behavior.DiscoverRuntimeTargets.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

            return new PlcsimRuntimeDiscoveryResult(
                behavior.DiscoverRuntimeTargets.Status,
                behavior.DiscoverRuntimeTargets.Succeeded ? behavior.RuntimeCandidates : Array.Empty<PlcsimRuntimeTargetCandidate>(),
                behavior.DiscoverRuntimeTargets.CreateDiagnostics("DiscoverRuntimeTargets", "FakePlcsim.Runtime.DiscoveryScenario"),
                new[] { CreateEvidence("FakePlcsim.Runtime.Discovered", "Fake runtime candidates discovered.") });
        }

        public async Task<PlcsimRuntimePrepareResult> PrepareRuntimeAsync(
            PlcsimRuntimePrepareRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            await behavior.PrepareRuntime.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

            if (!behavior.PrepareRuntime.Succeeded)
            {
                return new PlcsimRuntimePrepareResult(
                    behavior.PrepareRuntime.Status,
                    null,
                    behavior.PrepareRuntime.CreateDiagnostics("PrepareRuntime", "FakePlcsim.Runtime.PrepareScenario"),
                    new[] { CreateEvidence("FakePlcsim.Runtime.PrepareScenario", "Fake runtime prepare scenario rejected the operation.") });
            }

            var selection = SelectRuntime(request);
            if (selection.Diagnostic is not null)
            {
                return new PlcsimRuntimePrepareResult(
                    selection.Status,
                    null,
                    new[] { selection.Diagnostic },
                    new[] { CreateEvidence("FakePlcsim.Runtime.PrepareRejected", "Fake runtime selection failed.") });
            }

            var selectedRuntime = selection.Runtime;
            if (selectedRuntime is null)
            {
                return new PlcsimRuntimePrepareResult(
                    PlcsimOperationStatus.Unclear,
                    null,
                    new[]
                    {
                        new PlcsimDiagnostic(
                            "FakePlcsim.Runtime.SelectionMissing",
                            PlcsimDiagnosticSeverity.Error,
                            "Fake runtime selection succeeded without a selected runtime.",
                            "PrepareRuntime",
                            "FakePlcsim.Runtime.PrepareRejected")
                    },
                    new[] { CreateEvidence("FakePlcsim.Runtime.PrepareRejected", "Fake runtime selection did not produce a runtime.") });
            }

            preparedRuntime = selectedRuntime;
            state = selectedRuntime.State;
            ProgressChanged?.Invoke(this, new PlcsimRuntimeProgressSnapshot("PrepareRuntime", "Prepared", "Fake runtime prepared.", 100));
            return new PlcsimRuntimePrepareResult(
                behavior.PrepareRuntime.Status,
                preparedRuntime,
                behavior.PrepareRuntime.CreateDiagnostics("PrepareRuntime", "FakePlcsim.Runtime.PrepareScenario"),
                new[] { CreateEvidence("FakePlcsim.Runtime.Prepared", "Fake runtime prepared.") });
        }

        public async Task<PlcsimRuntimeObservationResult> ObserveRuntimeAsync(
            PlcsimRuntimeObservationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            await behavior.ObserveRuntime.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

            return new PlcsimRuntimeObservationResult(
                behavior.ObserveRuntime.Status,
                preparedRuntime,
                behavior.ObserveRuntime.CreateDiagnostics("ObserveRuntime", "FakePlcsim.Runtime.ObserveScenario"),
                new[] { CreateEvidence("FakePlcsim.Runtime.Observed", "Fake runtime state observed.") });
        }

        public async Task<PlcsimRuntimeObservationResult> RevalidateRuntimeAsync(
            PlcsimRuntimeRevalidationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            await behavior.ObserveRuntime.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

            if (preparedRuntime is null
                || !Equals(preparedRuntime.Reference, request.ExpectedRuntime))
            {
                return new PlcsimRuntimeObservationResult(
                    PlcsimOperationStatus.Rejected,
                    null,
                    new[]
                    {
                        new PlcsimDiagnostic(
                            "FakePlcsim.Runtime.RevalidationIdentityMismatch",
                            PlcsimDiagnosticSeverity.Error,
                            "The prepared fake runtime does not match the expected runtime identity.",
                            "RevalidateRuntime",
                            "FakePlcsim.Runtime.Revalidation")
                    },
                    Array.Empty<PlcsimEvidence>());
            }

            return new PlcsimRuntimeObservationResult(
                behavior.ObserveRuntime.Status,
                preparedRuntime,
                behavior.ObserveRuntime.CreateDiagnostics("RevalidateRuntime", "FakePlcsim.Runtime.RevalidationScenario"),
                new[] { CreateEvidence("FakePlcsim.Runtime.Revalidated", "Fake runtime access revalidated in place.") });
        }

        public async Task<PlcsimRuntimeControlResult> StartRuntimeAsync(
            PlcsimRuntimeControlRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            await behavior.StartRuntime.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

            if (!behavior.StartRuntime.Succeeded)
            {
                return ScenarioControl(behavior.StartRuntime, state, "StartRuntime", "FakePlcsim.Runtime.StartScenario");
            }

            ProgressChanged?.Invoke(this, new PlcsimRuntimeProgressSnapshot("StartRuntime", "Running", "Fake runtime is running.", 100));
            SetRuntimeState(PlcsimRuntimeControlState.Run, "Fake runtime is running.");
            return ScenarioControl(behavior.StartRuntime, state, "StartRuntime", "FakePlcsim.Runtime.StartScenario");
        }

        public async Task<PlcsimRuntimeControlResult> StopRuntimeAsync(
            PlcsimRuntimeControlRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            await behavior.StopRuntime.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

            if (!behavior.StopRuntime.Succeeded)
            {
                return ScenarioControl(behavior.StopRuntime, state, "StopRuntime", "FakePlcsim.Runtime.StopScenario");
            }

            ProgressChanged?.Invoke(this, new PlcsimRuntimeProgressSnapshot("StopRuntime", "Stopped", "Fake runtime is stopped.", 100));
            SetRuntimeState(PlcsimRuntimeControlState.Stop, "Fake runtime is stopped.");
            return ScenarioControl(behavior.StopRuntime, state, "StopRuntime", "FakePlcsim.Runtime.StopScenario");
        }

        public async Task<PlcsimRuntimeReleaseResult> ReleaseRuntimeAsync(
            PlcsimRuntimeReleaseRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            await behavior.ReleaseRuntime.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

            if (!behavior.ReleaseRuntime.Succeeded)
            {
                return new PlcsimRuntimeReleaseResult(
                    behavior.ReleaseRuntime.Status,
                    false,
                    request.CleanupPreference,
                    false,
                    behavior.ReleaseRuntime.CreateDiagnostics("ReleaseRuntime", "FakePlcsim.Runtime.ReleaseScenario"),
                    new[] { CreateEvidence("FakePlcsim.Runtime.ReleaseScenario", "Fake runtime release scenario rejected the operation.") });
            }

            var storageCleaned = request.CleanupPreference == PlcsimRuntimeCleanupPreference.Release
                && preparedRuntime?.IsOwned == true;
            SetHealth(PlcsimRuntimeHealthState.Disposed, "Fake runtime session released.");
            Unregister();
            return new PlcsimRuntimeReleaseResult(
                behavior.ReleaseRuntime.Status,
                true,
                request.CleanupPreference,
                storageCleaned,
                behavior.ReleaseRuntime.CreateDiagnostics("ReleaseRuntime", "FakePlcsim.Runtime.ReleaseScenario"),
                new[] { CreateEvidence("FakePlcsim.Runtime.Released", "Fake runtime released.") });
        }

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await behavior.CloseRuntime.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);
            if (Health != PlcsimRuntimeHealthState.Disposed)
            {
                SetHealth(PlcsimRuntimeHealthState.Disposed, "Fake runtime session closed.");
            }

            Unregister();
        }

        public bool SimulateExternalStateChange(PlcsimRuntimeControlState next)
        {
            if (Health == PlcsimRuntimeHealthState.Disposed)
            {
                return false;
            }

            SetRuntimeState(next, $"Fake runtime state changed externally to {next}.");
            return true;
        }

        public bool SimulateExternalInvalidation()
        {
            if (Health == PlcsimRuntimeHealthState.Disposed)
            {
                return false;
            }

            SetHealth(PlcsimRuntimeHealthState.Invalidated, "Fake runtime was invalidated externally.");
            return true;
        }

        private static PlcsimRuntimeControlResult ScenarioControl(
            FakePlcsimOperationScenario scenario,
            PlcsimRuntimeControlState state,
            string operation,
            string fallbackCode)
        {
            return new PlcsimRuntimeControlResult(
                scenario.Status,
                state,
                scenario.CreateDiagnostics(operation, fallbackCode),
                new[] { CreateEvidence(fallbackCode, $"Fake runtime scenario returned {scenario.Status}.") });
        }

        private void EnsureOpen()
        {
            if (Health == PlcsimRuntimeHealthState.Disposed)
            {
                throw new ObjectDisposedException(nameof(FakePlcsimRuntimeSession));
            }
        }

        private void SetHealth(PlcsimRuntimeHealthState next, string message)
        {
            var previous = Health;
            Health = next;
            HealthChanged?.Invoke(this, new PlcsimRuntimeHealthChangedEventArgs(previous, next, message));
        }

        private void SetRuntimeState(PlcsimRuntimeControlState next, string message)
        {
            var previous = state;
            state = next;
            preparedRuntime = preparedRuntime?.WithState(state);
            PublishRuntimeStateChanged(previous, state, message);
        }

        private void Unregister()
        {
            if (Interlocked.Exchange(ref unregistered, 1) == 0)
            {
                unregister(Runtime.SessionId);
            }
        }

        private void PublishRuntimeStateChanged(
            PlcsimRuntimeControlState previous,
            PlcsimRuntimeControlState current,
            string message)
        {
            RuntimeStateChanged?.Invoke(
                this,
                new PlcsimRuntimeStateChangedEventArgs(
                    new PlcsimRuntimeStateChange(
                        Runtime.SessionId,
                        preparedRuntime?.Reference ?? Runtime.RuntimeReference,
                        PlcsimRuntimeStateChangeKind.OperatingStateChanged,
                        previous,
                        current,
                        timeProvider.GetUtcNow(),
                        message,
                        Array.Empty<PlcsimDiagnostic>(),
                        new[]
                        {
                            CreateEvidence("FakePlcsim.Runtime.StateChanged", message)
                        })));
        }

        private FakeRuntimeSelection SelectRuntime(PlcsimRuntimePrepareRequest request)
        {
            if (request.ProvisioningMode == PlcsimRuntimeProvisioningMode.CreateOwned)
            {
                var collision = request.RuntimeReference is null
                    ? null
                    : behavior.RuntimeCandidates.FirstOrDefault(candidate =>
                        ReferencesMatch(candidate.Reference, request.RuntimeReference));
                if (collision is not null)
                {
                    return FakeRuntimeSelection.Rejected(
                        PlcsimOperationStatus.Rejected,
                        "FakePlcsim.Runtime.InstanceAlreadyExists",
                        "CreateOwned cannot use an already registered fake runtime target.");
                }

                return FakeRuntimeSelection.Selected(new PlcsimRuntimeTargetCandidate(
                    new PlcsimRuntimeReference(
                        "fake-runtime-created",
                        request.RuntimeReference?.InstanceName ?? "Created Fake PLCSIM Runtime"),
                    "fake-8",
                    "fake-8",
                    SiemensRuntimeLocality.Local,
                    PlcsimRuntimeControlState.Stop,
                    true,
                    true));
            }

            if (request.RuntimeReference is not null)
            {
                var explicitMatches = behavior.RuntimeCandidates
                    .Where(candidate => ReferencesMatch(candidate.Reference, request.RuntimeReference))
                    .ToArray();
                if (explicitMatches.Length == 1 && explicitMatches[0].IsUsable)
                {
                    return FakeRuntimeSelection.Selected(explicitMatches[0]);
                }

                return FakeRuntimeSelection.Rejected(
                    PlcsimOperationStatus.Unavailable,
                    "FakePlcsim.Runtime.TargetNotFound",
                    "The requested fake runtime target was not found or is not usable.");
            }

            var usable = behavior.RuntimeCandidates
                .Where(candidate => candidate.IsUsable && candidate.Locality == SiemensRuntimeLocality.Local)
                .ToArray();
            if (usable.Length == 1)
            {
                return FakeRuntimeSelection.Selected(usable[0]);
            }

            if (usable.Length > 1)
            {
                return FakeRuntimeSelection.Rejected(
                    PlcsimOperationStatus.Rejected,
                    "FakePlcsim.Runtime.TargetAmbiguous",
                    "Fake runtime target selection is ambiguous.");
            }

            return FakeRuntimeSelection.Rejected(
                PlcsimOperationStatus.Unavailable,
                "FakePlcsim.Runtime.TargetUnavailable",
                "No usable fake runtime target is available.");
        }

        private static bool ReferencesMatch(PlcsimRuntimeReference candidate, PlcsimRuntimeReference request)
        {
            return (!string.IsNullOrWhiteSpace(request.Id) && string.Equals(candidate.Id, request.Id, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(request.InstanceName) && string.Equals(candidate.InstanceName, request.InstanceName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class FakePlcsimCommunicationSession : IPlcsimUnitTestCommunicationSession
    {
        private readonly FakePlcsimBehavior behavior;
        private readonly TimeProvider timeProvider;
        private readonly FakePlcsimRuntimeSession runtimeSession;
        private readonly SemaphoreSlim batchPublicationGate = new(1, 1);
        private readonly object eventStreamSync = new();
        private readonly List<ITimer> pendingTimers = new();
        private readonly HashSet<Task> pendingBatchPublications = new();
        private PlcsimRunControlObservation observation;
        private CancellationTokenSource? eventStreamPublicationCancellation;
        private bool releaseInProgress;
        private uint nextSequenceId = 1;

        private PlcsimEventStreamMonitoringState monitoringState;

        public FakePlcsimCommunicationSession(
            PlcsimCommunicationPrepareRequest request,
            FakePlcsimBehavior behavior,
            TimeProvider timeProvider,
            FakePlcsimRuntimeSession runtimeSession)
        {
            this.behavior = behavior;
            this.timeProvider = timeProvider;
            this.runtimeSession = runtimeSession;
            observation = behavior.InitialRunControlObservation;
            Communication = new PlcsimCommunicationSessionInfo(
                Guid.NewGuid().ToString("N"),
                request.RuntimeSessionId,
                request.RunControlRoot,
                request.EventBufferRoot,
                DateTimeOffset.UtcNow);
            monitoringState = CreateMonitoringState(false);
            runtimeSession.RuntimeStateChanged += OnRuntimeStateChanged;
            runtimeSession.HealthChanged += OnRuntimeHealthChanged;
            if (!runtimeSession.CanPrepareCommunication)
            {
                InvalidateForRuntime("Fake runtime is not running.");
            }
        }

        public PlcsimCommunicationSessionInfo Communication { get; }

        public PlcsimCommunicationHealthState Health { get; private set; } = PlcsimCommunicationHealthState.Healthy;

        public event EventHandler<PlcsimCommunicationHealthChangedEventArgs>? HealthChanged;

        public event EventHandler<PlcsimRunControlObservationChangedEventArgs>? RunControlObservationChanged;

        public event EventHandler<PlcsimEventBatchReceivedEventArgs>? EventBatchReceived;

        public event EventHandler<PlcsimEventStreamMonitoringStateChangedEventArgs>? EventStreamMonitoringStateChanged;

        public async Task<PlcsimRunControlValidationResult> ValidateRunControlAsync(
            PlcsimRunControlValidationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            if (IsSessionMismatch(request.CommunicationSessionId))
            {
                return new PlcsimRunControlValidationResult(
                    PlcsimOperationStatus.Rejected,
                    ContractValid: false,
                    ObservedContractVersion: UnitTestContractVersions.PlcsimAdvancedCommunication,
                    CreateSessionMismatchDiagnostics("ValidateRunControl", request.CommunicationSessionId),
                    Array.Empty<PlcsimEvidence>());
            }

            await behavior.ValidateRunControl.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

            var scenarioValid = behavior.ValidateRunControl.Succeeded;
            var contractValid = scenarioValid && string.Equals(request.ExpectedContractVersion, UnitTestContractVersions.PlcsimAdvancedCommunication, StringComparison.Ordinal);
            return new PlcsimRunControlValidationResult(
                contractValid ? behavior.ValidateRunControl.Status : PlcsimOperationStatus.Rejected,
                contractValid,
                UnitTestContractVersions.PlcsimAdvancedCommunication,
                contractValid
                    ? behavior.ValidateRunControl.CreateDiagnostics("ValidateRunControl", "FakePlcsim.RunControl.ValidationScenario")
                    : CreateRejectedDiagnostics(behavior.ValidateRunControl, "FakePlcsim.RunControl.ContractMismatch", "ValidateRunControl"),
                new[] { CreateEvidence("FakePlcsim.RunControl.Validated", "Fake RunControl contract observed.") });
        }

        public async Task<PlcsimRunControlCommandResult> RequestRunControlCommandAsync(
            PlcsimRunControlCommandRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            if (IsSessionMismatch(request.CommunicationSessionId))
            {
                return new PlcsimRunControlCommandResult(
                    PlcsimOperationStatus.Rejected,
                    request.Command,
                    Accepted: false,
                    CreateSessionMismatchDiagnostics("RunControlCommand", request.CommunicationSessionId),
                    Array.Empty<PlcsimEvidence>());
            }

            var commandScenario = behavior.GetRunControlCommandScenario(request.Command);
            await commandScenario.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

            if (!commandScenario.Succeeded)
            {
                return new PlcsimRunControlCommandResult(
                    commandScenario.Status,
                    request.Command,
                    false,
                    commandScenario.CreateDiagnostics("RunControlCommand", $"FakePlcsim.RunControl.{request.Command}Scenario"),
                    new[] { CreateEvidence("FakePlcsim.RunControl.CommandScenario", "Fake RunControl command scenario rejected the operation.") });
            }

            ScheduleRunControlReaction(request.Command);
            return new PlcsimRunControlCommandResult(
                commandScenario.Status,
                request.Command,
                true,
                commandScenario.CreateDiagnostics("RunControlCommand", $"FakePlcsim.RunControl.{request.Command}Scenario"),
                new[] { CreateEvidence("FakePlcsim.RunControl.CommandAccepted", "Fake RunControl command accepted.") });
        }

        public async Task<PlcsimEventBufferResetResult> RequestEventBufferResetAsync(
            PlcsimEventBufferResetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            if (IsSessionMismatch(request.CommunicationSessionId))
            {
                return new PlcsimEventBufferResetResult(
                    PlcsimOperationStatus.Rejected,
                    Accepted: false,
                    CreateSessionMismatchDiagnostics("ResetEventBuffer", request.CommunicationSessionId),
                    Array.Empty<PlcsimEvidence>());
            }

            await behavior.ResetEventBuffer.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);
            return new PlcsimEventBufferResetResult(
                behavior.ResetEventBuffer.Status,
                behavior.ResetEventBuffer.Succeeded,
                behavior.ResetEventBuffer.CreateDiagnostics(
                    "ResetEventBuffer",
                    "FakePlcsim.EventBufferReset.Scenario"),
                behavior.ResetEventBuffer.Succeeded
                    ? new[] { CreateEvidence("FakePlcsim.EventBufferReset.Accepted", "Fake EventBuffer reset command accepted.") }
                    : Array.Empty<PlcsimEvidence>());
        }

        public async Task<PlcsimRunControlObservationResult> ObserveRunControlAsync(
            PlcsimRunControlObservationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            if (IsSessionMismatch(request.CommunicationSessionId))
            {
                return new PlcsimRunControlObservationResult(
                    PlcsimOperationStatus.Rejected,
                    null,
                    CreateSessionMismatchDiagnostics("ObserveRunControl", request.CommunicationSessionId),
                    Array.Empty<PlcsimEvidence>());
            }

            await behavior.ObserveRunControl.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

            return new PlcsimRunControlObservationResult(
                behavior.ObserveRunControl.Status,
                behavior.ObserveRunControl.Succeeded ? observation : null,
                behavior.ObserveRunControl.CreateDiagnostics("ObserveRunControl", "FakePlcsim.RunControl.ObservationScenario"),
                new[] { CreateEvidence("FakePlcsim.RunControl.Observed", "Fake RunControl state observed.") });
        }

        public async Task<PlcsimEventBufferValidationResult> ValidateEventBufferAsync(
            PlcsimEventBufferValidationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            if (IsSessionMismatch(request.CommunicationSessionId))
            {
                return new PlcsimEventBufferValidationResult(
                    PlcsimOperationStatus.Rejected,
                    ContractValid: false,
                    ObservedContractVersion: UnitTestContractVersions.EventStreamModel,
                    new PlcsimEventBufferObservation(
                        ContractValid: false,
                        EventBufferReadState.SessionInvalid,
                        EventStreamBatchReleaseState.Invalidated,
                        BufferObservedEmpty: true,
                        LostEventCount: 0),
                    CreateSessionMismatchDiagnostics("ValidateEventBuffer", request.CommunicationSessionId),
                    Array.Empty<PlcsimEvidence>());
            }

            await behavior.ValidateEventBuffer.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

            var scenarioValid = behavior.ValidateEventBuffer.Succeeded;
            var contractValid = scenarioValid && string.Equals(request.ExpectedContractVersion, UnitTestContractVersions.EventStreamModel, StringComparison.Ordinal);
            var eventStream = behavior.EventStream;
            var readState = contractValid ? eventStream.ReadState : EventBufferReadState.ContractInvalid;
            var batchReleaseState = contractValid ? eventStream.BatchReleaseState : EventStreamBatchReleaseState.Idle;
            return new PlcsimEventBufferValidationResult(
                contractValid ? behavior.ValidateEventBuffer.Status : PlcsimOperationStatus.Rejected,
                contractValid,
                UnitTestContractVersions.EventStreamModel,
                new PlcsimEventBufferObservation(contractValid, readState, batchReleaseState, eventStream.BufferObservedEmpty, eventStream.LostEventCount),
                contractValid
                    ? behavior.ValidateEventBuffer.CreateDiagnostics("ValidateEventBuffer", "FakePlcsim.EventBuffer.ValidationScenario")
                    : CreateRejectedDiagnostics(behavior.ValidateEventBuffer, "FakePlcsim.EventBuffer.ContractMismatch", "ValidateEventBuffer"),
                new[] { CreateEvidence("FakePlcsim.EventBuffer.Validated", "Fake EventBuffer contract observed.") });
        }

        public async Task<PlcsimEventStreamMonitoringResult> StartEventStreamMonitoringAsync(
            PlcsimEventStreamMonitoringRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            if (IsSessionMismatch(request.CommunicationSessionId))
            {
                return SessionMismatchMonitoring("StartEventStreamMonitoring", request.CommunicationSessionId);
            }

            if (!await TryDelayAsync(behavior.StartEventStreamMonitoring, cancellationToken).ConfigureAwait(false))
            {
                return CancelledMonitoring("StartEventStreamMonitoring", "FakePlcsim.EventStream.StartMonitoringCancelled");
            }

            if (!behavior.StartEventStreamMonitoring.Succeeded)
            {
                return ScenarioMonitoring(behavior.StartEventStreamMonitoring, "StartEventStreamMonitoring", "FakePlcsim.EventStream.StartMonitoringScenario");
            }

            monitoringState = monitoringState with { IsMonitoring = true };
            if (behavior.EventStream.Invalidated)
            {
                SetHealth(PlcsimCommunicationHealthState.Invalidated, "Fake event-stream communication was invalidated.");
            }
            else
            {
                ResetEventStreamPublicationCancellation();
            }

            foreach (var state in behavior.EventStream.MonitoringStates)
            {
                PublishMonitoringState(state with
                {
                    CommunicationSessionId = Communication.SessionId,
                    IsMonitoring = true
                });
            }

            foreach (var batch in behavior.EventStream.Batches)
            {
                QueueBatchForReleaseAndPublication(RebindBatch(batch));
            }

            return ScenarioMonitoring(behavior.StartEventStreamMonitoring, "StartEventStreamMonitoring", "FakePlcsim.EventStream.StartMonitoringScenario");
        }

        public async Task<PlcsimEventStreamMonitoringResult> StopEventStreamMonitoringAsync(
            PlcsimEventStreamStopRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            if (IsSessionMismatch(request.CommunicationSessionId))
            {
                return SessionMismatchMonitoring("StopEventStreamMonitoring", request.CommunicationSessionId);
            }

            await behavior.StopEventStreamMonitoring.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);

            if (!behavior.StopEventStreamMonitoring.Succeeded)
            {
                return ScenarioMonitoring(behavior.StopEventStreamMonitoring, "StopEventStreamMonitoring", "FakePlcsim.EventStream.StopMonitoringScenario");
            }

            await StopBatchPublicationsAsync().ConfigureAwait(false);
            monitoringState = monitoringState with { IsMonitoring = false };
            return ScenarioMonitoring(behavior.StopEventStreamMonitoring, "StopEventStreamMonitoring", "FakePlcsim.EventStream.StopMonitoringScenario");
        }

        private async Task<FakeEventStreamReleaseResult> ReleaseProcessedEventsCoreAsync(
            FakeEventStreamReleaseRequest request,
            CancellationToken cancellationToken)
        {
            lock (eventStreamSync)
            {
                if (Health is PlcsimCommunicationHealthState.Invalidated
                    or PlcsimCommunicationHealthState.Disposed
                    || monitoringState.BatchReleaseState == EventStreamBatchReleaseState.Invalidated)
                {
                    return InvalidatedRelease();
                }

                if (releaseInProgress)
                {
                    return PendingRelease();
                }

                releaseInProgress = true;
            }

            try
            {
                if (!await TryDelayAsync(behavior.ReleaseProcessedEvents, cancellationToken).ConfigureAwait(false))
                {
                    return CancelledRelease();
                }

                if (!behavior.ReleaseProcessedEvents.Succeeded)
                {
                    var batchReleaseState = behavior.ReleaseProcessedEvents.Status == PlcsimOperationStatus.TimedOut
                        ? EventStreamBatchReleaseState.Unclear
                        : EventStreamBatchReleaseState.Failed;
                    return new FakeEventStreamReleaseResult(
                        behavior.ReleaseProcessedEvents.Status,
                        batchReleaseState,
                        null,
                        behavior.ReleaseProcessedEvents.CreateDiagnostics("ReleaseProcessedEvents", "FakePlcsim.EventStream.ReleaseScenario"),
                        new[] { CreateEvidence("FakePlcsim.EventStream.ReleaseScenario", "Fake event-stream release scenario rejected the operation.") });
                }

                lock (eventStreamSync)
                {
                    if (monitoringState.BatchReleaseState == EventStreamBatchReleaseState.Invalidated)
                    {
                        return InvalidatedRelease();
                    }

                }

                return new FakeEventStreamReleaseResult(
                    behavior.ReleaseProcessedEvents.Status,
                    EventStreamBatchReleaseState.Completed,
                    request.DeleteSequenceId,
                    behavior.ReleaseProcessedEvents.CreateDiagnostics("ReleaseProcessedEvents", "FakePlcsim.EventStream.ReleaseScenario"),
                    new[] { CreateEvidence("FakePlcsim.EventStream.Released", "Fake event-stream release completed.") });
            }
            finally
            {
                lock (eventStreamSync)
                {
                    releaseInProgress = false;
                }
            }
        }

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await behavior.CloseCommunication.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);
            await StopBatchPublicationsAsync().ConfigureAwait(false);
            foreach (var timer in pendingTimers.ToArray())
            {
                await timer.DisposeAsync().ConfigureAwait(false);
            }

            pendingTimers.Clear();
            DetachRuntimeEvents();
            if (Health != PlcsimCommunicationHealthState.Disposed)
            {
                SetHealth(PlcsimCommunicationHealthState.Disposed, "Fake communication session closed.");
            }

        }

        public void DetachRuntimeEvents()
        {
            runtimeSession.RuntimeStateChanged -= OnRuntimeStateChanged;
            runtimeSession.HealthChanged -= OnRuntimeHealthChanged;
        }

        private void OnRuntimeStateChanged(object? sender, PlcsimRuntimeStateChangedEventArgs args)
        {
            if (args.Change.CurrentState != PlcsimRuntimeControlState.Run)
            {
                InvalidateForRuntime($"Fake runtime changed to {args.Change.CurrentState}.");
            }
        }

        private void OnRuntimeHealthChanged(object? sender, PlcsimRuntimeHealthChangedEventArgs args)
        {
            if (args.Current != PlcsimRuntimeHealthState.Healthy)
            {
                InvalidateForRuntime($"Fake runtime health changed to {args.Current}.");
            }
        }

        private void InvalidateForRuntime(string message)
        {
            if (Health is PlcsimCommunicationHealthState.Disposed or PlcsimCommunicationHealthState.Invalidated)
            {
                return;
            }

            monitoringState = monitoringState with
            {
                IsMonitoring = false,
                LastReadState = EventBufferReadState.SessionInvalid
            };
            CancelBatchPublications();
            SetHealth(PlcsimCommunicationHealthState.Invalidated, message);
        }

        private bool IsSessionMismatch(string communicationSessionId)
        {
            return !string.Equals(communicationSessionId, Communication.SessionId, StringComparison.Ordinal);
        }

        private static PlcsimEventStreamMonitoringResult SessionMismatchMonitoring(
            string phase,
            string communicationSessionId)
        {
            return new PlcsimEventStreamMonitoringResult(
                PlcsimOperationStatus.Rejected,
                new PlcsimEventStreamMonitoringState(
                    communicationSessionId,
                    IsMonitoring: false,
                    EventBufferReadState.SessionInvalid,
                    EventStreamBatchReleaseState.Idle,
                    BufferObservedEmpty: false,
                    LostEventCount: 0),
                CreateSessionMismatchDiagnostics(phase, communicationSessionId),
                Array.Empty<PlcsimEvidence>());
        }

        private static IReadOnlyList<PlcsimDiagnostic> CreateSessionMismatchDiagnostics(
            string phase,
            string communicationSessionId)
        {
            return new[]
            {
                new PlcsimDiagnostic(
                    "FakePlcsim.CommunicationSessionMismatch",
                    PlcsimDiagnosticSeverity.Error,
                    "The fake PLCSIM communication session id does not match the prepared session.",
                    phase,
                    communicationSessionId)
            };
        }

        private static IReadOnlyList<PlcsimDiagnostic> CreateRejectedDiagnostics(
            FakePlcsimOperationScenario scenario,
            string fallbackCode,
            string phase)
        {
            var diagnostics = scenario.CreateDiagnostics(phase, fallbackCode);
            return diagnostics.Count == 0
                ? new[] { ContractMismatch(fallbackCode, phase) }
                : diagnostics;
        }

        private static PlcsimDiagnostic ContractMismatch(string code, string phase)
        {
            return new PlcsimDiagnostic(
                code,
                PlcsimDiagnosticSeverity.Error,
                "The requested UnitTest contract version is not supported by the fake PLCSIM binding.",
                phase,
                null);
        }

        private void ScheduleRunControlReaction(UnitTestRunCommand command)
        {
            if (behavior.RunControlReactionDelay is not { } delay)
            {
                return;
            }

            ScheduleBatch(() => ApplyRunControlReaction(command), delay);
        }

        private void ApplyRunControlReaction(UnitTestRunCommand command)
        {
            if (!CanApplyRunControlReaction(command))
            {
                return;
            }

            observation = command switch
            {
                UnitTestRunCommand.Start => CompleteStartedRun(),
                UnitTestRunCommand.Abort => CompleteAbortedRun(),
                UnitTestRunCommand.Reset => ResetRunControl(),
                _ => observation
            };
        }

        private bool CanApplyRunControlReaction(UnitTestRunCommand command)
        {
            return command switch
            {
                UnitTestRunCommand.Start => observation.ExecutionStatus == UnitTestExecutionStatus.Idle
                    && observation.Ready,
                UnitTestRunCommand.Abort => observation.ExecutionStatus is UnitTestExecutionStatus.Starting
                    or UnitTestExecutionStatus.Running
                    or UnitTestExecutionStatus.Completing
                    or UnitTestExecutionStatus.Aborting,
                UnitTestRunCommand.Reset => true,
                _ => false
            };
        }

        private PlcsimEventStreamMonitoringResult ScenarioMonitoring(
            FakePlcsimOperationScenario scenario,
            string operation,
            string fallbackCode)
        {
            return new PlcsimEventStreamMonitoringResult(
                scenario.Status,
                monitoringState,
                scenario.CreateDiagnostics(operation, fallbackCode),
                new[] { CreateEvidence(fallbackCode, $"Fake event-stream monitoring scenario returned {scenario.Status}.") });
        }

        private PlcsimEventStreamMonitoringState CreateMonitoringState(bool isMonitoring)
        {
            var eventStream = behavior.EventStream;
            return new PlcsimEventStreamMonitoringState(
                Communication.SessionId,
                isMonitoring,
                eventStream.Invalidated ? EventBufferReadState.SessionInvalid : eventStream.ReadState,
                eventStream.Invalidated ? EventStreamBatchReleaseState.Invalidated : eventStream.BatchReleaseState,
                eventStream.BufferObservedEmpty,
                eventStream.LostEventCount);
        }

        private void PublishMonitoringState(PlcsimEventStreamMonitoringState state)
        {
            lock (eventStreamSync)
            {
                if (monitoringState == state)
                {
                    return;
                }

                monitoringState = state;
            }

            EventStreamMonitoringStateChanged?.Invoke(
                this,
                new PlcsimEventStreamMonitoringStateChangedEventArgs(state));
        }

        private async Task<bool> TryDelayAsync(FakePlcsimOperationScenario scenario, CancellationToken cancellationToken)
        {
            try
            {
                await scenario.DelayAsync(timeProvider, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        private PlcsimEventStreamMonitoringResult CancelledMonitoring(string operation, string evidenceId)
        {
            return new PlcsimEventStreamMonitoringResult(
                PlcsimOperationStatus.Cancelled,
                monitoringState,
                new[]
                {
                    new PlcsimDiagnostic(
                        evidenceId,
                        PlcsimDiagnosticSeverity.Warning,
                        "Fake event-stream monitoring was cancelled.",
                        operation,
                        evidenceId)
                },
                new[] { CreateEvidence(evidenceId, "Fake event-stream monitoring was cancelled.") });
        }

        private static FakeEventStreamReleaseResult InvalidatedRelease()
        {
            return new FakeEventStreamReleaseResult(
                PlcsimOperationStatus.Unavailable,
                EventStreamBatchReleaseState.Invalidated,
                null,
                new[]
                {
                    new PlcsimDiagnostic(
                        "FakePlcsim.EventStream.SessionInvalidated",
                        PlcsimDiagnosticSeverity.Error,
                        "Fake event-stream communication is invalidated.",
                        "ReleaseProcessedEvents",
                        "FakePlcsim.EventStream.SessionInvalidated")
                },
                new[] { CreateEvidence("FakePlcsim.EventStream.SessionInvalidated", "Fake event-stream release was rejected because the session is invalidated.") });
        }

        private static FakeEventStreamReleaseResult PendingRelease()
        {
            return new FakeEventStreamReleaseResult(
                PlcsimOperationStatus.Rejected,
                EventStreamBatchReleaseState.Pending,
                null,
                new[]
                {
                    new PlcsimDiagnostic(
                        "FakePlcsim.EventStream.ReleasePending",
                        PlcsimDiagnosticSeverity.Warning,
                        "Fake event-stream release is already pending.",
                        "ReleaseProcessedEvents",
                        "FakePlcsim.EventStream.ReleasePending")
                },
                new[] { CreateEvidence("FakePlcsim.EventStream.ReleasePending", "Fake event-stream release backpressure rejected the operation.") });
        }

        private FakeEventStreamReleaseResult CancelledRelease()
        {
            var batchReleaseState = monitoringState.BatchReleaseState == EventStreamBatchReleaseState.Invalidated
                ? EventStreamBatchReleaseState.Invalidated
                : EventStreamBatchReleaseState.Unclear;
            return new FakeEventStreamReleaseResult(
                PlcsimOperationStatus.Cancelled,
                batchReleaseState,
                null,
                new[]
                {
                    new PlcsimDiagnostic(
                        "FakePlcsim.EventStream.ReleaseCancelled",
                        PlcsimDiagnosticSeverity.Warning,
                        "Fake event-stream release was cancelled.",
                        "ReleaseProcessedEvents",
                        "FakePlcsim.EventStream.ReleaseCancelled")
                },
                new[] { CreateEvidence("FakePlcsim.EventStream.ReleaseCancelled", "Fake event-stream release was cancelled.") });
        }

        private void ResetEventStreamPublicationCancellation()
        {
            lock (eventStreamSync)
            {
                eventStreamPublicationCancellation?.Cancel();
                eventStreamPublicationCancellation?.Dispose();
                eventStreamPublicationCancellation = new CancellationTokenSource();
            }
        }

        private void CancelBatchPublications()
        {
            lock (eventStreamSync)
            {
                eventStreamPublicationCancellation?.Cancel();
            }
        }

        private async Task StopBatchPublicationsAsync()
        {
            Task[] publications;
            lock (eventStreamSync)
            {
                eventStreamPublicationCancellation?.Cancel();
                publications = pendingBatchPublications.ToArray();
            }

            if (publications.Length > 0)
            {
                await Task.WhenAll(publications).ConfigureAwait(false);
            }

            lock (eventStreamSync)
            {
                pendingBatchPublications.Clear();
                eventStreamPublicationCancellation?.Dispose();
                eventStreamPublicationCancellation = null;
            }
        }

        private void QueueBatchForReleaseAndPublication(UnitTestEventBatch batch)
        {
            Task publication;
            lock (eventStreamSync)
            {
                if (!monitoringState.IsMonitoring
                    || Health != PlcsimCommunicationHealthState.Healthy
                    || eventStreamPublicationCancellation is null)
                {
                    return;
                }

                pendingBatchPublications.RemoveWhere(candidate => candidate.IsCompleted);
                publication = ReleaseAndPublishBatchAsync(batch, eventStreamPublicationCancellation.Token);
                pendingBatchPublications.Add(publication);
            }
        }

        private async Task ReleaseAndPublishBatchAsync(
            UnitTestEventBatch batch,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            try
            {
                await batchPublicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (batch.Events.Count > 0)
                {
                    lock (eventStreamSync)
                    {
                        monitoringState = monitoringState with
                        {
                            LastReadState = batch.ReadState,
                            BatchReleaseState = EventStreamBatchReleaseState.Idle,
                            BufferObservedEmpty = batch.BufferObservedEmpty,
                            LostEventCount = batch.LostEventCount,
                            ProducerSequenceId = batch.ProducerSequenceId,
                            DeletedSequenceId = batch.DeletedSequenceId
                        };
                    }
                    var deleteSequenceId = batch.Events.Max(entry => entry.SequenceId);
                    var release = await ReleaseProcessedEventsCoreAsync(
                        new FakeEventStreamReleaseRequest(
                            Communication.SessionId,
                            deleteSequenceId,
                            TimeSpan.FromSeconds(1),
                            "fake-event-stream-intake"),
                        cancellationToken).ConfigureAwait(false);
                    if (!release.Succeeded)
                    {
                        return;
                    }
                }

                lock (eventStreamSync)
                {
                    if (cancellationToken.IsCancellationRequested
                        || !monitoringState.IsMonitoring
                        || Health != PlcsimCommunicationHealthState.Healthy)
                    {
                        return;
                    }

                    monitoringState = monitoringState with
                    {
                        LastReadState = batch.ReadState,
                        BatchReleaseState = batch.Events.Count == 0
                            ? EventStreamBatchReleaseState.Idle
                            : EventStreamBatchReleaseState.Completed,
                        BufferObservedEmpty = batch.BufferObservedEmpty,
                        LostEventCount = batch.LostEventCount,
                        ProducerSequenceId = batch.ProducerSequenceId,
                        DeletedSequenceId = batch.DeletedSequenceId
                    };
                }

                EventBatchReceived?.Invoke(this, new PlcsimEventBatchReceivedEventArgs(batch));
            }
            finally
            {
                batchPublicationGate.Release();
            }
        }

        private UnitTestEventBatch RebindBatch(UnitTestEventBatch batch)
        {
            return batch with { SessionId = new CommunicationSessionId(Communication.SessionId) };
        }

        private PlcsimRunControlObservation CompleteStartedRun()
        {
            var running = new PlcsimRunControlObservation(
                UnitTestExecutionStatus.Running,
                UnitTestExecutionSignals.Running,
                UnitTestLifecycleState.Running,
                UnitTestResultState.NotEvaluated);
            var completed = new PlcsimRunControlObservation(
                UnitTestExecutionStatus.Completed,
                UnitTestExecutionSignals.Completed,
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Passed);
            PublishRunControlStarted();
            PublishRunControlObservation(running);
            ScheduleBatch(() =>
            {
                observation = completed;
                PublishRunControlObservation(completed);
                PublishRunControlCompleted(UnitTestResultState.Passed);
            });
            return running;
        }

        private PlcsimRunControlObservation CompleteAbortedRun()
        {
            ScheduleBatch(() => PublishRunControlCompleted(UnitTestResultState.Abort));
            var aborted = new PlcsimRunControlObservation(
                UnitTestExecutionStatus.Completed,
                UnitTestExecutionSignals.Completed | UnitTestExecutionSignals.Aborted,
                UnitTestLifecycleState.Completed,
                UnitTestResultState.Abort);
            PublishRunControlObservation(aborted);
            return aborted;
        }

        private PlcsimRunControlObservation ResetRunControl()
        {
            PublishRunControlResetObserved();
            var reset = new PlcsimRunControlObservation(
                UnitTestExecutionStatus.Idle,
                UnitTestExecutionSignals.Ready,
                UnitTestLifecycleState.Idle,
                UnitTestResultState.NotEvaluated);
            PublishRunControlObservation(reset);
            return reset;
        }

        private void ScheduleBatch(Action publish)
        {
            ScheduleBatch(publish, TimeSpan.FromMilliseconds(25));
        }

        private void ScheduleBatch(Action publish, TimeSpan delay)
        {
            ITimer? timer = null;
            timer = timeProvider.CreateTimer(
                _ =>
                {
                    try
                    {
                        publish();
                    }
                    finally
                    {
                        if (timer is not null)
                        {
                            pendingTimers.Remove(timer);
                            timer.Dispose();
                        }
                    }
                },
                null,
                delay,
                Timeout.InfiniteTimeSpan);
            pendingTimers.Add(timer);
        }

        private void PublishRunControlStarted()
        {
            var sequenceId = NextSequenceId();
            RaiseBatch(CreateBatch(
                new[]
                {
                    CreateRunControlStateTransitionEntry(
                        sequenceId,
                        UnitTestLifecycleState.Starting,
                        UnitTestResultState.NotEvaluated,
                        "Fake RunControl started.")
                },
                producerSequenceId: sequenceId,
                deletedSequenceId: sequenceId - 1,
                bufferObservedEmpty: false,
                EventBufferReadState.Valid));
        }

        private void PublishRunControlCompleted(UnitTestResultState result)
        {
            var caseCompletionSequenceId = NextSequenceId();
            var caseResultSequenceId = NextSequenceId();
            var caseAggregationSequenceId = NextSequenceId();
            var suiteCompletionSequenceId = NextSequenceId();
            var suiteResultSequenceId = NextSequenceId();
            var suiteAggregationSequenceId = NextSequenceId();
            var resultSequenceId = NextSequenceId();
            var completionSequenceId = NextSequenceId();
            RaiseBatch(CreateBatch(
                new[]
                {
                    CreateOwnerStateTransitionEntry(
                        caseCompletionSequenceId,
                        UnitTestOwnerScope.TestCase,
                        "SampleSuite.SampleCase",
                        UnitTestLifecycleState.Completed,
                        UnitTestResultState.NotEvaluated,
                        "Fake sample test case completed."),
                    CreateOwnerResultEntry(
                        caseResultSequenceId,
                        UnitTestOwnerScope.TestCase,
                        "SampleSuite.SampleCase",
                        result,
                        $"Fake sample test case result is {result}."),
                    CreateOwnerAggregationEntry(
                        caseAggregationSequenceId,
                        UnitTestOwnerScope.TestCase,
                        "SampleSuite.SampleCase",
                        total: 1,
                        passed: result == UnitTestResultState.Passed ? 1u : 0u,
                        failed: result == UnitTestResultState.Failed ? 1u : 0u,
                        error: result == UnitTestResultState.Error ? 1u : 0u,
                        runtime: 25),
                    CreateOwnerStateTransitionEntry(
                        suiteCompletionSequenceId,
                        UnitTestOwnerScope.TestSuite,
                        "SampleSuite",
                        UnitTestLifecycleState.Completed,
                        UnitTestResultState.NotEvaluated,
                        "Fake sample suite completed."),
                    CreateOwnerResultEntry(
                        suiteResultSequenceId,
                        UnitTestOwnerScope.TestSuite,
                        "SampleSuite",
                        result,
                        $"Fake sample suite result is {result}."),
                    CreateOwnerAggregationEntry(
                        suiteAggregationSequenceId,
                        UnitTestOwnerScope.TestSuite,
                        "SampleSuite",
                        total: 1,
                        passed: result == UnitTestResultState.Passed ? 1u : 0u,
                        failed: result == UnitTestResultState.Failed ? 1u : 0u,
                        error: result == UnitTestResultState.Error ? 1u : 0u,
                        runtime: 25),
                    CreateRunControlResultEntry(
                        resultSequenceId,
                        result,
                        $"Fake RunControl result is {result}."),
                    CreateRunControlStateTransitionEntry(
                        completionSequenceId,
                        UnitTestLifecycleState.Completed,
                        UnitTestResultState.NotEvaluated,
                        "Fake RunControl completed.")
                },
                producerSequenceId: completionSequenceId,
                deletedSequenceId: completionSequenceId,
                bufferObservedEmpty: true,
                EventBufferReadState.Empty));
        }

        private void PublishRunControlResetObserved()
        {
            var sequenceId = NextSequenceId();
            RaiseBatch(CreateBatch(
                new[]
                {
                    CreateRunControlStateTransitionEntry(
                        sequenceId,
                        UnitTestLifecycleState.Reset,
                        UnitTestResultState.NotEvaluated,
                        "Fake RunControl reset observed.")
                },
                producerSequenceId: sequenceId,
                deletedSequenceId: sequenceId - 1,
                bufferObservedEmpty: false,
                EventBufferReadState.Valid));
        }

        private uint NextSequenceId()
        {
            return nextSequenceId++;
        }

        private UnitTestEventBatch CreateBatch(
            IReadOnlyList<UnitTestEventEntry> events,
            uint producerSequenceId,
            uint deletedSequenceId,
            bool bufferObservedEmpty,
            EventBufferReadState readState)
        {
            return new UnitTestEventBatch(
                new CommunicationSessionId(Communication.SessionId),
                events,
                producerSequenceId,
                deletedSequenceId,
                LostEventCount: 0,
                bufferObservedEmpty,
                readState);
        }

        private static UnitTestEventEntry CreateRunControlStateTransitionEntry(
            uint sequenceId,
            UnitTestLifecycleState state,
            UnitTestResultState result,
            string message)
        {
            return new UnitTestEventEntry(
                sequenceId,
                DateTimeOffset.UnixEpoch,
                UnitTestOwnerScope.RunControl,
                UnitTestEventType.StateTransition,
                Microsoft.Extensions.Logging.LogLevel.Information,
                "FakeRunControl",
                string.Empty,
                state,
                result,
                message);
        }

        private static UnitTestEventEntry CreateRunControlResultEntry(
            uint sequenceId,
            UnitTestResultState result,
            string message)
        {
            return CreateOwnerResultEntry(
                sequenceId,
                UnitTestOwnerScope.RunControl,
                "FakeRunControl",
                result,
                message);
        }

        private static UnitTestEventEntry CreateOwnerStateTransitionEntry(
            uint sequenceId,
            UnitTestOwnerScope ownerScope,
            string name,
            UnitTestLifecycleState state,
            UnitTestResultState result,
            string message)
        {
            return new UnitTestEventEntry(
                sequenceId,
                DateTimeOffset.UnixEpoch,
                ownerScope,
                UnitTestEventType.StateTransition,
                Microsoft.Extensions.Logging.LogLevel.Information,
                name,
                string.Empty,
                state,
                result,
                message);
        }

        private static UnitTestEventEntry CreateOwnerResultEntry(
            uint sequenceId,
            UnitTestOwnerScope ownerScope,
            string name,
            UnitTestResultState result,
            string message)
        {
            return new UnitTestEventEntry(
                sequenceId,
                DateTimeOffset.UnixEpoch,
                ownerScope,
                UnitTestEventType.Result,
                Microsoft.Extensions.Logging.LogLevel.Information,
                name,
                string.Empty,
                UnitTestLifecycleState.Completed,
                result,
                message);
        }

        private static UnitTestEventEntry CreateOwnerAggregationEntry(
            uint sequenceId,
            UnitTestOwnerScope ownerScope,
            string name,
            uint total,
            uint passed,
            uint failed,
            uint error,
            uint runtime)
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                total,
                completed = total,
                passed,
                failed,
                error,
                runtime
            });
            return new UnitTestEventEntry(
                sequenceId,
                DateTimeOffset.UnixEpoch,
                ownerScope,
                UnitTestEventType.Aggregation,
                Microsoft.Extensions.Logging.LogLevel.Information,
                name,
                string.Empty,
                UnitTestLifecycleState.Completed,
                passed == total ? UnitTestResultState.Passed : UnitTestResultState.NotEvaluated,
                payload);
        }

        private void SetHealth(PlcsimCommunicationHealthState next, string message)
        {
            var previous = Health;
            Health = next;
            HealthChanged?.Invoke(this, new PlcsimCommunicationHealthChangedEventArgs(previous, next, message));
        }

        private void EnsureOpen()
        {
            if (Health == PlcsimCommunicationHealthState.Disposed)
            {
                throw new ObjectDisposedException(nameof(FakePlcsimCommunicationSession));
            }
        }

        private void RaiseBatch(UnitTestEventBatch batch)
        {
            QueueBatchForReleaseAndPublication(batch);
        }

        private void PublishRunControlObservation(PlcsimRunControlObservation nextObservation)
        {
            RunControlObservationChanged?.Invoke(this, new PlcsimRunControlObservationChangedEventArgs(nextObservation));
        }
    }
}

internal sealed record FakeEventStreamReleaseRequest(
    string CommunicationSessionId,
    uint DeleteSequenceId,
    TimeSpan Timeout,
    string? CorrelationId);

internal sealed record FakeEventStreamReleaseResult(
    PlcsimOperationStatus Status,
    EventStreamBatchReleaseState BatchReleaseState,
    uint? DeletedSequenceId,
    IReadOnlyList<PlcsimDiagnostic> Diagnostics,
    IReadOnlyList<PlcsimEvidence> Evidence)
{
    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;
}

internal sealed record FakePlcsimBehavior(
    IReadOnlyList<PlcsimRuntimeTargetCandidate> RuntimeCandidates,
    PlcsimRunControlObservation InitialRunControlObservation,
    FakePlcsimEventStreamBehavior EventStream,
    FakePlcsimOperationScenario OpenRuntimeSession,
    FakePlcsimOperationScenario DiscoverRuntimeTargets,
    FakePlcsimOperationScenario PrepareRuntime,
    FakePlcsimOperationScenario ObserveRuntime,
    FakePlcsimOperationScenario StartRuntime,
    FakePlcsimOperationScenario StopRuntime,
    FakePlcsimOperationScenario ReleaseRuntime,
    FakePlcsimOperationScenario CloseRuntime,
    FakePlcsimOperationScenario PrepareCommunication,
    FakePlcsimOperationScenario ValidateRunControl,
    FakePlcsimOperationScenario StartRunControlCommand,
    FakePlcsimOperationScenario AbortRunControlCommand,
    FakePlcsimOperationScenario ResetRunControlCommand,
    FakePlcsimOperationScenario ResetEventBuffer,
    FakePlcsimOperationScenario ObserveRunControl,
    FakePlcsimOperationScenario ValidateEventBuffer,
    FakePlcsimOperationScenario StartEventStreamMonitoring,
    FakePlcsimOperationScenario StopEventStreamMonitoring,
    FakePlcsimOperationScenario ReleaseProcessedEvents,
    FakePlcsimOperationScenario CloseCommunication,
    TimeSpan? RunControlReactionDelay)
{
    public static FakePlcsimBehavior Default { get; } = new(
        new[]
        {
            new PlcsimRuntimeTargetCandidate(
                new PlcsimRuntimeReference("fake-runtime", "Fake PLCSIM Runtime"),
                "fake-8",
                "fake-8",
                SiemensRuntimeLocality.Local,
                PlcsimRuntimeControlState.Stop,
                true,
                false)
        },
        new PlcsimRunControlObservation(
            UnitTestExecutionStatus.Idle,
            UnitTestExecutionSignals.Ready,
            UnitTestLifecycleState.Idle,
            UnitTestResultState.NotEvaluated),
        FakePlcsimEventStreamBehavior.Default,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        FakePlcsimOperationScenario.SucceededScenario,
        TimeSpan.FromMilliseconds(1));

    public FakePlcsimOperationScenario GetRunControlCommandScenario(UnitTestRunCommand command)
    {
        return command switch
        {
            UnitTestRunCommand.Start => StartRunControlCommand,
            UnitTestRunCommand.Abort => AbortRunControlCommand,
            UnitTestRunCommand.Reset => ResetRunControlCommand,
            _ => FakePlcsimOperationScenario.SucceededScenario
        };
    }
}

internal sealed record FakePlcsimEventStreamBehavior(
    IReadOnlyList<UnitTestEventBatch> Batches,
    EventBufferReadState ReadState,
    EventStreamBatchReleaseState BatchReleaseState,
    bool BufferObservedEmpty,
    ushort LostEventCount,
    bool Invalidated)
{
    public IReadOnlyList<PlcsimEventStreamMonitoringState> MonitoringStates { get; init; } =
        Array.Empty<PlcsimEventStreamMonitoringState>();

    public static FakePlcsimEventStreamBehavior Default { get; } = new(
        Array.Empty<UnitTestEventBatch>(),
        EventBufferReadState.Empty,
        EventStreamBatchReleaseState.Idle,
        true,
        0,
        false);
}

internal static class PlcsimRuntimeTargetCandidateExtensions
{
    public static PlcsimRuntimeTargetCandidate WithState(
        this PlcsimRuntimeTargetCandidate candidate,
        PlcsimRuntimeControlState state)
    {
        return candidate with { State = state };
    }
}

internal sealed record FakeRuntimeSelection(
    PlcsimOperationStatus Status,
    PlcsimRuntimeTargetCandidate? Runtime,
    PlcsimDiagnostic? Diagnostic)
{
    public static FakeRuntimeSelection Selected(PlcsimRuntimeTargetCandidate runtime)
    {
        return new FakeRuntimeSelection(PlcsimOperationStatus.Succeeded, runtime, null);
    }

    public static FakeRuntimeSelection Rejected(
        PlcsimOperationStatus status,
        string code,
        string message)
    {
        return new FakeRuntimeSelection(
            status,
            null,
            new PlcsimDiagnostic(
                code,
                PlcsimDiagnosticSeverity.Error,
                message,
                "PrepareRuntime",
                "FakePlcsim.Runtime.PrepareRejected"));
    }
}

internal sealed record FakePlcsimOperationScenario(
    PlcsimOperationStatus Status,
    int DelayMs,
    string? DiagnosticCode,
    PlcsimDiagnosticSeverity DiagnosticSeverity,
    string? DiagnosticMessage)
{
    public static FakePlcsimOperationScenario SucceededScenario { get; } = new(
        PlcsimOperationStatus.Succeeded,
        0,
        null,
        PlcsimDiagnosticSeverity.Information,
        null);

    public bool Succeeded => Status is PlcsimOperationStatus.Succeeded or PlcsimOperationStatus.SucceededWithWarnings;

    public async Task DelayAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (DelayMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(DelayMs), timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<PlcsimDiagnostic> CreateDiagnostics(string phase, string fallbackCode)
    {
        if (Status == PlcsimOperationStatus.Succeeded && string.IsNullOrWhiteSpace(DiagnosticCode))
        {
            return Array.Empty<PlcsimDiagnostic>();
        }

        var code = string.IsNullOrWhiteSpace(DiagnosticCode)
            ? fallbackCode
            : DiagnosticCode;
        var message = string.IsNullOrWhiteSpace(DiagnosticMessage)
            ? $"Fake PLCSIM scenario returned {Status}."
            : DiagnosticMessage;
        return new[]
        {
            new PlcsimDiagnostic(
                code,
                DiagnosticSeverity,
                message,
                phase,
                null)
        };
    }
}
