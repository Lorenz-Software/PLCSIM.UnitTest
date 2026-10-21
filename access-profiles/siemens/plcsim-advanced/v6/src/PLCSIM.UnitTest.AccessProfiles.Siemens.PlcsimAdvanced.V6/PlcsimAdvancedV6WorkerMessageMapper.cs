using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.Identifiers;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Globalization;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V6;

internal sealed class PlcsimAdvancedV6WorkerMessageMapper
{
    private readonly string sessionId;
    private PlcsimEventStreamMonitoringState? lastMonitoringState;

    public PlcsimAdvancedV6WorkerMessageMapper(string sessionId)
    {
        this.sessionId = sessionId;
    }
    public PlcsimAdvancedV6WorkerAsyncEvent? MapAsyncMessage(string line)
    {
        var message = PlcsimAdvancedV6WorkerProtocol.ParseMessage(line);
        if (!message.IsProtocolVersionSupported)
        {
            return null;
        }

        if (string.Equals(message.Type, "RuntimeStateChanged", StringComparison.OrdinalIgnoreCase))
        {
            return new PlcsimAdvancedV6RuntimeStateWorkerEvent(CreateRuntimeStateChange(message.Values));
        }

        if (string.Equals(message.Type, "EventBatchReceived", StringComparison.OrdinalIgnoreCase))
        {
            return new PlcsimAdvancedV6EventBatchWorkerEvent(CreateEventBatch(message.Values));
        }

        if (string.Equals(message.Type, "EventStreamMonitoringStateChanged", StringComparison.OrdinalIgnoreCase))
        {
            return new PlcsimAdvancedV6EventStreamStateWorkerEvent(
                CreateEventStreamMonitoringState(message.Values, string.Empty));
        }

        if (string.Equals(message.Type, "RunControlObserved", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                ReadString(message.Values, "source", string.Empty),
                "monitor",
                StringComparison.OrdinalIgnoreCase))
        {
            return new PlcsimAdvancedV6RunControlWorkerEvent(CreateRunControlObservation(message.Values));
        }

        if (string.Equals(message.Type, "CommunicationHealthChanged", StringComparison.OrdinalIgnoreCase)
            && message.Values.TryGetValue("state", out var rawState)
            && Enum.TryParse<PlcsimCommunicationHealthState>(rawState, ignoreCase: true, out var health))
        {
            return new PlcsimAdvancedV6CommunicationHealthWorkerEvent(
                ReadString(message.Values, "communicationSessionId", string.Empty),
                health,
                ReadString(
                    message.Values,
                    "message",
                    "PLCSIM Advanced UnitTest communication health changed."));
        }

        if (string.Equals(message.Type, "EventStreamMonitoringFaulted", StringComparison.OrdinalIgnoreCase))
        {
            return new PlcsimAdvancedV6EventStreamFaultWorkerEvent(
                ReadString(message.Values, "communicationSessionId", string.Empty),
                ReadOperationStatus(message.Values, "status", PlcsimOperationStatus.Failed),
                CreateWorkerDiagnostic(
                    message.Values,
                    "PlcsimAdvanced.V6.EventStreamMonitoringFaulted",
                    "PLCSIM Advanced V6 worker event-stream monitoring faulted.",
                    "EventStreamMonitoring",
                    "plcsim-advanced-v6-eventstream-monitoring"));
        }

        return null;
    }

    public PlcsimRuntimeStateChange CreateConnectionLostChange(string message)
    {
        return new PlcsimRuntimeStateChange(
            sessionId,
            null,
            PlcsimRuntimeStateChangeKind.ConnectionLost,
            null,
            PlcsimRuntimeControlState.Unknown,
            DateTimeOffset.UtcNow,
            message,
            Array.Empty<PlcsimDiagnostic>(),
            Array.Empty<PlcsimEvidence>());
    }

    public PlcsimAdvancedV6EventStreamFaultWorkerEvent CreateEventStreamMonitoringFault(
        string communicationSessionId,
        PlcsimOperationStatus status,
        string code,
        string message,
        PlcsimDiagnosticSeverity severity)
    {
        return new PlcsimAdvancedV6EventStreamFaultWorkerEvent(
            communicationSessionId,
            status,
            new PlcsimDiagnostic(
                code,
                severity,
                message,
                "EventStreamMonitoring",
                "plcsim-advanced-v6-eventstream-monitoring"));
    }

    public PlcsimRuntimeStateChange CreateRuntimeStateChange(IReadOnlyDictionary<string, string> values)
    {
        var kind = values.TryGetValue("kind", out var rawKind)
            && Enum.TryParse<PlcsimRuntimeStateChangeKind>(rawKind, ignoreCase: true, out var parsedKind)
                ? parsedKind
                : PlcsimRuntimeStateChangeKind.OperatingStateChanged;
        var currentState = ReadRuntimeControlState(values, "currentState");
        var previousState = values.ContainsKey("previousState")
            ? ReadRuntimeControlState(values, "previousState")
            : (PlcsimRuntimeControlState?)null;
        var observedAt = values.TryGetValue("observedAt", out var rawObservedAt)
            && DateTimeOffset.TryParse(rawObservedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedObservedAt)
                ? parsedObservedAt
                : DateTimeOffset.UtcNow;
        var message = ReadString(values, "message", "PLCSIM Advanced V6 runtime state changed.");
        var code = ReadString(values, "code", "PlcsimAdvanced.V6.RuntimeStateChanged");
        var diagnostics = kind is PlcsimRuntimeStateChangeKind.RuntimeManagerLost or PlcsimRuntimeStateChangeKind.ConnectionLost
            ? new[]
            {
                new PlcsimDiagnostic(
                    code,
                    PlcsimDiagnosticSeverity.Error,
                    message,
                    "RuntimeStateChanged",
                    "plcsim-advanced-v6-runtime-state")
            }
            : Array.Empty<PlcsimDiagnostic>();

        return new PlcsimRuntimeStateChange(
            ReadString(values, "runtimeSessionId", sessionId),
            CreateRuntimeReference(values),
            kind,
            previousState,
            currentState,
            observedAt,
            message,
            diagnostics,
            new[]
            {
                new PlcsimEvidence(
                    "plcsim-advanced-v6-runtime-state",
                    "RuntimeStateChanged",
                    code)
            });
    }

    public PlcsimRuntimeReference? CreateRuntimeReference(IReadOnlyDictionary<string, string> values)
    {
        var runtimeId = values.TryGetValue("runtimeId", out var id) && !string.IsNullOrWhiteSpace(id)
            ? id
            : null;
        var instanceName = values.TryGetValue("instanceName", out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;
        return runtimeId is null && instanceName is null
            ? null
            : new PlcsimRuntimeReference(runtimeId, instanceName);
    }

    public PlcsimDiagnostic CreateDrainedCancellationDiagnostic(
        string code,
        string message,
        string phase,
        string evidenceId)
    {
        return new PlcsimDiagnostic(
            code,
            PlcsimDiagnosticSeverity.Warning,
            message,
            phase,
            evidenceId);
    }

    public PlcsimRuntimeDiscoveryResult FailedDiscovery(string code, string message)
    {
        return new PlcsimRuntimeDiscoveryResult(
            PlcsimOperationStatus.Failed,
            Array.Empty<PlcsimRuntimeTargetCandidate>(),
            new[]
            {
                new PlcsimDiagnostic(
                    code,
                    PlcsimDiagnosticSeverity.Error,
                    message,
                    "DiscoverRuntimeTargets",
                    "plcsim-advanced-v6-runtime-discovery")
            },
            Array.Empty<PlcsimEvidence>());
    }

    public PlcsimRuntimeObservationResult FailedObservation(
        PlcsimOperationStatus status,
        string code,
        string message,
        PlcsimDiagnosticSeverity severity = PlcsimDiagnosticSeverity.Error)
    {
        return new PlcsimRuntimeObservationResult(
            status,
            null,
            new[]
            {
                new PlcsimDiagnostic(
                    code,
                    severity,
                    message,
                    "ObserveRuntime",
                    "plcsim-advanced-v6-runtime-observation")
            },
            Array.Empty<PlcsimEvidence>());
    }

    public PlcsimRuntimeControlResult FailedControl(
        string command,
        PlcsimOperationStatus status,
        string code,
        string message,
        PlcsimDiagnosticSeverity severity = PlcsimDiagnosticSeverity.Error)
    {
        return new PlcsimRuntimeControlResult(
            status,
            PlcsimRuntimeControlState.Unknown,
            new[]
            {
                new PlcsimDiagnostic(
                    code,
                    severity,
                    message,
                    command,
                    command == "StartRuntime" ? "plcsim-advanced-v6-runtime-start" : "plcsim-advanced-v6-runtime-stop")
            },
            Array.Empty<PlcsimEvidence>());
    }

    public PlcsimRuntimePrepareResult FailedPrepare(
        PlcsimOperationStatus status,
        string code,
        string message,
        PlcsimDiagnosticSeverity severity = PlcsimDiagnosticSeverity.Error)
    {
        return new PlcsimRuntimePrepareResult(
            status,
            null,
            new[]
            {
                new PlcsimDiagnostic(
                    code,
                    severity,
                    message,
                    "PrepareRuntime",
                    "plcsim-advanced-v6-runtime-prepare")
            },
            Array.Empty<PlcsimEvidence>());
    }

    public PlcsimRuntimePrepareResult MapRuntimePrepareFailure(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<PlcsimEvidence> baseEvidence)
    {
        var compensationAttempted = ReadBool(values, "compensationAttempted");
        var compensationComplete = ReadBool(values, "compensationComplete");
        var diagnostics = new List<PlcsimDiagnostic>
        {
            CreateWorkerDiagnostic(
                values,
                "PlcsimAdvanced.V6.RuntimePrepareWorkerError",
                "PLCSIM Advanced V6 worker reported a runtime preparation error.",
                "PrepareRuntime",
                "plcsim-advanced-v6-runtime-prepare")
        };
        var evidence = new List<PlcsimEvidence>(baseEvidence);

        if (compensationAttempted)
        {
            var runtimeId = ReadString(values, "runtimeId", "<unknown>");
            var instanceName = ReadString(values, "instanceName", "<unknown>");
            var storagePath = ReadString(values, "storagePath", "<unknown>");
            var powerOffStatus = ReadString(values, "powerOffStatus", "NotAttempted");
            var unregisterStatus = ReadString(values, "unregisterStatus", "NotAttempted");

            evidence.Add(new PlcsimEvidence(
                "plcsim-advanced-v6-runtime-prepare-compensation-resource",
                "RuntimePrepareCompensationResource",
                $"runtimeId={runtimeId}; instanceName={instanceName}; storagePath={storagePath}"));
            evidence.Add(new PlcsimEvidence(
                "plcsim-advanced-v6-runtime-prepare-compensation-poweroff",
                "RuntimePrepareCompensationPowerOff",
                $"status={powerOffStatus}; message={ReadString(values, "powerOffMessage", string.Empty)}"));
            evidence.Add(new PlcsimEvidence(
                "plcsim-advanced-v6-runtime-prepare-compensation-unregister",
                "RuntimePrepareCompensationUnregister",
                $"status={unregisterStatus}; message={ReadString(values, "unregisterMessage", string.Empty)}"));

            if (string.Equals(powerOffStatus, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.RuntimePrepareCompensationPowerOffFailed",
                    compensationComplete ? PlcsimDiagnosticSeverity.Warning : PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 could not power off the partially created owned runtime during compensation.",
                    "PrepareRuntimeCompensation",
                    "plcsim-advanced-v6-runtime-prepare-compensation-poweroff"));
            }

            if (string.Equals(unregisterStatus, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new PlcsimDiagnostic(
                    "PlcsimAdvanced.V6.RuntimePrepareCompensationUnregisterFailed",
                    PlcsimDiagnosticSeverity.Error,
                    "PLCSIM Advanced V6 could not prove removal of the partially created owned runtime during compensation.",
                    "PrepareRuntimeCompensation",
                    "plcsim-advanced-v6-runtime-prepare-compensation-unregister"));
            }
        }

        var status = ReadOperationStatus(values, "status", PlcsimOperationStatus.Failed);
        if (compensationAttempted && !compensationComplete)
        {
            status = PlcsimOperationStatus.Unclear;
        }

        return new PlcsimRuntimePrepareResult(status, null, diagnostics, evidence);
    }

    public bool HasIncompleteRuntimePrepareCompensation(IReadOnlyDictionary<string, string> values)
    {
        return ReadBool(values, "compensationAttempted")
            && !ReadBool(values, "compensationComplete");
    }

    public IReadOnlyList<PlcsimEvidence> CreateRuntimeEvidence(string id, string kind)
    {
        return new[]
        {
            new PlcsimEvidence(
                id,
                kind,
                "PLCSIM Advanced V6 worker session")
        };
    }

    public PlcsimDiagnostic CreateWorkerDiagnostic(
        IReadOnlyDictionary<string, string> values,
        string fallbackCode,
        string fallbackMessage,
        string phase,
        string evidenceId)
    {
        return new PlcsimDiagnostic(
            values.TryGetValue("code", out var code) ? code : fallbackCode,
            PlcsimDiagnosticSeverity.Error,
            values.TryGetValue("message", out var message) ? message : fallbackMessage,
            phase,
            evidenceId);
    }

    public PlcsimRuntimeReleaseResult FailedRelease(
        PlcsimOperationStatus status,
        string code,
        string message,
        PlcsimRuntimeCleanupPreference cleanupPreference,
        PlcsimDiagnosticSeverity severity = PlcsimDiagnosticSeverity.Error)
    {
        return new PlcsimRuntimeReleaseResult(
            status,
            false,
            cleanupPreference,
            false,
            new[]
            {
                new PlcsimDiagnostic(
                    code,
                    severity,
                    message,
                    "ReleaseRuntime",
                    "plcsim-advanced-v6-runtime-release")
            },
            Array.Empty<PlcsimEvidence>());
    }

    public PlcsimCommunicationPrepareResult FailedCommunicationPrepare(
        PlcsimOperationStatus status,
        string code,
        string message,
        PlcsimDiagnosticSeverity severity = PlcsimDiagnosticSeverity.Error)
    {
        return new PlcsimCommunicationPrepareResult(
            status,
            null,
            new[]
            {
                new PlcsimDiagnostic(
                    code,
                    severity,
                    message,
                    "PrepareCommunication",
                    "plcsim-advanced-v6-communication-prepare")
            },
            Array.Empty<PlcsimEvidence>());
    }

    public PlcsimRunControlObservationResult FailedRunControlObservation(
        PlcsimOperationStatus status,
        string code,
        string message,
        PlcsimDiagnosticSeverity severity = PlcsimDiagnosticSeverity.Error)
    {
        return new PlcsimRunControlObservationResult(
            status,
            null,
            new[]
            {
                new PlcsimDiagnostic(
                    code,
                    severity,
                    message,
                    "ObserveRunControl",
                    "plcsim-advanced-v6-runcontrol-observation")
            },
            Array.Empty<PlcsimEvidence>());
    }

    public PlcsimRunControlCommandResult FailedRunControlCommand(
        PLCSIM.UnitTest.Common.UnitTests.UnitTestRunCommand command,
        PlcsimOperationStatus status,
        string code,
        string message,
        PlcsimDiagnosticSeverity severity = PlcsimDiagnosticSeverity.Error)
    {
        return new PlcsimRunControlCommandResult(
            status,
            command,
            false,
            new[]
            {
                new PlcsimDiagnostic(
                    code,
                    severity,
                    message,
                    "RunControlCommand",
                    "plcsim-advanced-v6-runcontrol-command")
            },
            Array.Empty<PlcsimEvidence>());
    }

    public PlcsimEventBufferResetResult FailedEventBufferReset(
        PlcsimOperationStatus status,
        string code,
        string message,
        PlcsimDiagnosticSeverity severity = PlcsimDiagnosticSeverity.Error)
    {
        return new PlcsimEventBufferResetResult(
            status,
            Accepted: false,
            new[]
            {
                new PlcsimDiagnostic(
                    code,
                    severity,
                    message,
                    "EventBufferReset",
                    "plcsim-advanced-v6-event-buffer-reset")
            },
            Array.Empty<PlcsimEvidence>());
    }

    public PlcsimEventStreamMonitoringResult FailedEventStreamMonitoring(
        string communicationSessionId,
        PlcsimOperationStatus status,
        string code,
        string message,
        PlcsimDiagnosticSeverity severity = PlcsimDiagnosticSeverity.Error)
    {
        return new PlcsimEventStreamMonitoringResult(
            status,
            CreateUnavailableEventStreamMonitoringState(communicationSessionId),
            new[]
            {
                new PlcsimDiagnostic(
                    code,
                    severity,
                    message,
                    "EventStreamMonitoring",
                    "plcsim-advanced-v6-eventstream-monitoring")
            },
            Array.Empty<PlcsimEvidence>());
    }

    public PlcsimRunControlObservation CreateRunControlObservation(IReadOnlyDictionary<string, string> values)
    {
        return new PlcsimRunControlObservation(
            ReadUnitTestExecutionStatus(values, "executionStatus"),
            ReadUnitTestExecutionSignals(values),
            ReadUnitTestLifecycleState(values, "state"),
            ReadUnitTestResultState(values, "result"));
    }

    public UnitTestExecutionStatus ReadUnitTestExecutionStatus(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        var value = ReadInt(values, key);
        return value.HasValue && Enum.IsDefined(typeof(UnitTestExecutionStatus), value.Value)
            ? (UnitTestExecutionStatus)value.Value
            : UnitTestExecutionStatus.Unknown;
    }

    public UnitTestExecutionSignals ReadUnitTestExecutionSignals(IReadOnlyDictionary<string, string> values)
    {
        var value = ReadInt(values, "executionSignals");
        const UnitTestExecutionSignals knownSignals =
            UnitTestExecutionSignals.Uninitialized
            | UnitTestExecutionSignals.ConfigError
            | UnitTestExecutionSignals.Ready
            | UnitTestExecutionSignals.Running
            | UnitTestExecutionSignals.Completed
            | UnitTestExecutionSignals.Aborted
            | UnitTestExecutionSignals.Inactive
            | UnitTestExecutionSignals.Error;

        return value.HasValue && (value.Value & ~(int)knownSignals) == 0
            ? (UnitTestExecutionSignals)value.Value
            : UnitTestExecutionSignals.None;
    }

    public UnitTestResultState ReadUnitTestResultState(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        var value = ReadInt(values, key);
        if (value is null
            || !Enum.IsDefined(typeof(UnitTestResultState), value.Value))
        {
            return UnitTestResultState.NotEvaluated;
        }

        return (UnitTestResultState)value.Value;
    }

    public PlcsimEventStreamMonitoringState CreateEventStreamMonitoringState(
        IReadOnlyDictionary<string, string> values,
        string fallbackCommunicationSessionId)
    {
        var state = new PlcsimEventStreamMonitoringState(
            ReadString(values, "communicationSessionId", fallbackCommunicationSessionId),
            ReadBool(values, "isMonitoring"),
            ReadEventBufferReadState(values, "lastReadState", EventBufferReadState.NotObserved),
            ReadEventStreamBatchReleaseState(values, "batchReleaseState", EventStreamBatchReleaseState.Idle),
            ReadBool(values, "bufferObservedEmpty"),
            ReadUShort(values, "lostEventCount"),
            ReadBool(values, "bufferFull"),
            ReadUInt(values, "producerSequenceId") ?? 0,
            ReadUInt(values, "deletedSequenceId") ?? 0);
        Volatile.Write(ref lastMonitoringState, state);
        return state;
    }

    public UnitTestEventBatch CreateEventBatch(IReadOnlyDictionary<string, string> values)
    {
        var eventCount = Math.Max(0, ReadInt(values, "eventCount") ?? 0);
        var entries = new List<UnitTestEventEntry>(eventCount);
        for (var index = 0; index < eventCount; index++)
        {
            var prefix = "event" + index;
            var sequenceId = ReadUInt(values, prefix + ".sequenceId") ?? 0;
            if (sequenceId == 0)
            {
                continue;
            }

            entries.Add(new UnitTestEventEntry(
                sequenceId,
                MapPlcTimestamp(ReadLong(values, prefix + ".timestampNanoseconds") ?? 0),
                MapOwnerScope(ReadInt(values, prefix + ".ownerScope")),
                MapEventType(ReadInt(values, prefix + ".eventType")),
                MapSeverity(ReadInt(values, prefix + ".severity")),
                ReadProtocolString(values, prefix + ".name"),
                ReadProtocolString(values, prefix + ".resourceName"),
                ReadUnitTestLifecycleState(values, prefix + ".state"),
                MapResultState(ReadInt(values, prefix + ".result")),
                ReadProtocolString(values, prefix + ".message")));
        }

        return new UnitTestEventBatch(
            new CommunicationSessionId(ReadString(values, "communicationSessionId", string.Empty)),
            entries,
            ReadUInt(values, "producerSequenceId") ?? 0,
            ReadUInt(values, "deletedSequenceId") ?? 0,
            ReadUShort(values, "lostEventCount"),
            ReadBool(values, "bufferObservedEmpty"),
            ReadEventBufferReadState(values, "readState", EventBufferReadState.Empty));
    }

    public DateTimeOffset MapPlcTimestamp(long nanosecondsSinceUnixEpoch)
    {
        try
        {
            return DateTimeOffset.UnixEpoch.AddTicks(nanosecondsSinceUnixEpoch / 100);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    public UnitTestOwnerScope MapOwnerScope(int? value)
    {
        return value.HasValue && Enum.IsDefined(typeof(UnitTestOwnerScope), value.Value)
            ? (UnitTestOwnerScope)value.Value
            : UnitTestOwnerScope.Unknown;
    }

    public UnitTestEventType MapEventType(int? value)
    {
        return value.HasValue && Enum.IsDefined(typeof(UnitTestEventType), value.Value)
            ? (UnitTestEventType)value.Value
            : UnitTestEventType.Unknown;
    }

    public UnitTestLifecycleState ReadUnitTestLifecycleState(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        var value = ReadInt(values, key);
        return value.HasValue && Enum.IsDefined(typeof(UnitTestLifecycleState), value.Value)
            ? (UnitTestLifecycleState)value.Value
            : UnitTestLifecycleState.Unknown;
    }

    public UnitTestResultState MapResultState(int? value)
    {
        return value.HasValue && Enum.IsDefined(typeof(UnitTestResultState), value.Value)
            ? (UnitTestResultState)value.Value
            : UnitTestResultState.Unknown;
    }

    public Microsoft.Extensions.Logging.LogLevel MapSeverity(int? value)
    {
        return value switch
        {
            0 => Microsoft.Extensions.Logging.LogLevel.Trace,
            1 => Microsoft.Extensions.Logging.LogLevel.Debug,
            2 => Microsoft.Extensions.Logging.LogLevel.Information,
            3 => Microsoft.Extensions.Logging.LogLevel.Warning,
            4 => Microsoft.Extensions.Logging.LogLevel.Error,
            5 => Microsoft.Extensions.Logging.LogLevel.Critical,
            _ => Microsoft.Extensions.Logging.LogLevel.None
        };
    }

    public PlcsimEventStreamMonitoringState CreateUnavailableEventStreamMonitoringState(
        string communicationSessionId)
    {
        var previous = Volatile.Read(ref lastMonitoringState);
        if (previous is not null
            && string.Equals(previous.CommunicationSessionId, communicationSessionId, StringComparison.Ordinal))
        {
            return previous with { IsMonitoring = false, LastReadState = EventBufferReadState.SessionInvalid };
        }

        return new PlcsimEventStreamMonitoringState(
            communicationSessionId,
            IsMonitoring: false,
            EventBufferReadState.SessionInvalid,
            EventStreamBatchReleaseState.Idle,
            BufferObservedEmpty: false,
            LostEventCount: 0,
            BufferFull: false,
            ProducerSequenceId: 0,
            DeletedSequenceId: 0);
    }

    public IReadOnlyList<PlcsimEvidence> CreateRunControlObservationEvidence(IReadOnlyDictionary<string, string> values)
    {
        return new[]
        {
            new PlcsimEvidence(
                "plcsim-advanced-v6-runcontrol-observation",
                "RunControlObservation",
                string.Join(
                    "; ",
                    new[]
                    {
                        "state=" + (values.TryGetValue("state", out var state) ? state : string.Empty),
                        "executionStatus=" + (values.TryGetValue("executionStatus", out var executionStatus) ? executionStatus : string.Empty),
                        "executionSignals=" + (values.TryGetValue("executionSignals", out var executionSignals) ? executionSignals : string.Empty),
                        "result=" + (values.TryGetValue("result", out var result) ? result : string.Empty)
                    }))
        };
    }

    public IReadOnlyList<PlcsimEvidence> CreateRunControlCommandEvidence(
        PLCSIM.UnitTest.Common.UnitTests.UnitTestRunCommand command,
        IReadOnlyDictionary<string, string> values)
    {
        return new[]
        {
            new PlcsimEvidence(
                "plcsim-advanced-v6-runcontrol-command",
                "RunControlCommand",
                command + "; apiWriteAccepted=" + (values.TryGetValue("accepted", out var accepted) ? accepted : string.Empty))
        };
    }

    public IReadOnlyList<PlcsimEvidence> CreateEventBufferResetEvidence(
        IReadOnlyDictionary<string, string> values)
    {
        return new[]
        {
            new PlcsimEvidence(
                "plcsim-advanced-v6-event-buffer-reset",
                "EventBufferReset",
                "accepted=" + (values.TryGetValue("accepted", out var accepted) ? accepted : string.Empty))
        };
    }

    public PlcsimRuntimeTargetCandidate CreateRuntimeTarget(IReadOnlyDictionary<string, string> values)
    {
        return new PlcsimRuntimeTargetCandidate(
            new PlcsimRuntimeReference(
                values.TryGetValue("id", out var id) ? id : null,
                values.TryGetValue("instanceName", out var instanceName) ? instanceName : null),
            values.TryGetValue("productVersion", out var productVersion) ? productVersion : "6.0",
            values.TryGetValue("accessApiVersion", out var accessApiVersion) ? accessApiVersion : "6.0",
            ReadLocality(values, "locality"),
            ReadRuntimeControlState(values, "state"),
            ReadBool(values, "isUsable"),
            ReadBool(values, "isOwned"));
    }

    public SiemensRuntimeLocality ReadLocality(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        return values.TryGetValue(key, out var value)
            && Enum.TryParse<SiemensRuntimeLocality>(value, ignoreCase: true, out var locality)
                ? locality
                : SiemensRuntimeLocality.Local;
    }

    public PlcsimRuntimeControlState ReadRuntimeControlState(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        return values.TryGetValue(key, out var value)
            && Enum.TryParse<PlcsimRuntimeControlState>(value, ignoreCase: true, out var state)
                ? state
                : PlcsimRuntimeControlState.Unknown;
    }

    public PlcsimOperationStatus ReadOperationStatus(
        IReadOnlyDictionary<string, string> values,
        string key,
        PlcsimOperationStatus fallback)
    {
        return values.TryGetValue(key, out var value)
            && Enum.TryParse<PlcsimOperationStatus>(value, ignoreCase: true, out var status)
                ? status
                : fallback;
    }

    public PlcsimRuntimeCleanupPreference ReadCleanupPreference(
        IReadOnlyDictionary<string, string> values,
        string key,
        PlcsimRuntimeCleanupPreference fallback)
    {
        return values.TryGetValue(key, out var value)
            && Enum.TryParse<PlcsimRuntimeCleanupPreference>(value, ignoreCase: true, out var preference)
                ? preference
                : fallback;
    }

    public string ReadString(IReadOnlyDictionary<string, string> values, string key, string fallback)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    public string ReadProtocolString(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value)
            ? value
            : string.Empty;
    }

    public bool ReadBool(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;
    }

    public EventBufferReadState ReadEventBufferReadState(
        IReadOnlyDictionary<string, string> values,
        string key,
        EventBufferReadState fallback)
    {
        return values.TryGetValue(key, out var value)
            && Enum.TryParse<EventBufferReadState>(value, ignoreCase: true, out var state)
                ? state
                : fallback;
    }

    public EventStreamBatchReleaseState ReadEventStreamBatchReleaseState(
        IReadOnlyDictionary<string, string> values,
        string key,
        EventStreamBatchReleaseState fallback)
    {
        return values.TryGetValue(key, out var value)
            && Enum.TryParse<EventStreamBatchReleaseState>(value, ignoreCase: true, out var state)
                ? state
                : fallback;
    }

    public int? ReadInt(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    public long? ReadLong(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value)
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    public ushort ReadUShort(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && ushort.TryParse(value, out var parsed)
            ? parsed
            : (ushort)0;
    }

    public uint? ReadUInt(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && uint.TryParse(value, out var parsed)
            ? parsed
            : null;
    }
}
