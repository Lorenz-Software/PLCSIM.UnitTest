using System;
using System.Collections.Generic;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;

public sealed class RuntimeTarget
{
    public RuntimeTarget(string id, string instanceName, string state, bool isOwned)
    {
        Id = id;
        InstanceName = instanceName;
        State = state;
        IsOwned = isOwned;
    }

    public string Id { get; }

    public string InstanceName { get; }

    public string State { get; }

    public bool IsOwned { get; }
}

public sealed class RuntimeObservationResult
{
    private RuntimeObservationResult(string status, RuntimeTarget? runtime, string code, string message)
    {
        Status = status;
        Runtime = runtime;
        Code = code;
        Message = message;
    }

    public string Status { get; }

    public RuntimeTarget? Runtime { get; }

    public string Code { get; }

    public string Message { get; }

    public bool Succeeded => string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase);

    public static RuntimeObservationResult Success(RuntimeTarget runtime)
    {
        return new RuntimeObservationResult("Succeeded", runtime, string.Empty, string.Empty);
    }

    public static RuntimeObservationResult Failed(string status, string code, string message)
    {
        return new RuntimeObservationResult(status, null, code, message);
    }
}

public sealed class RuntimePrepareResult
{
    private RuntimePrepareResult(
        string status,
        RuntimeTarget? runtime,
        string code,
        string message,
        RuntimePrepareCompensation? compensation)
    {
        Status = status;
        Runtime = runtime;
        Code = code;
        Message = message;
        Compensation = compensation;
    }

    public string Status { get; }

    public RuntimeTarget? Runtime { get; }

    public string Code { get; }

    public string Message { get; }

    public RuntimePrepareCompensation? Compensation { get; }

    public bool Succeeded => string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase);

    public static RuntimePrepareResult Success(RuntimeTarget runtime)
    {
        return new RuntimePrepareResult("Succeeded", runtime, string.Empty, string.Empty, null);
    }

    public static RuntimePrepareResult Failed(
        string status,
        string code,
        string message,
        RuntimePrepareCompensation? compensation = null)
    {
        return new RuntimePrepareResult(status, null, code, message, compensation);
    }
}

public sealed class RuntimePrepareCompensation
{
    public RuntimePrepareCompensation(
        string runtimeId,
        string instanceName,
        string storagePath,
        RuntimePrepareCompensationStep powerOff,
        RuntimePrepareCompensationStep unregister)
    {
        RuntimeId = runtimeId;
        InstanceName = instanceName;
        StoragePath = storagePath;
        PowerOff = powerOff;
        Unregister = unregister;
    }

    public string RuntimeId { get; }

    public string InstanceName { get; }

    public string StoragePath { get; }

    public RuntimePrepareCompensationStep PowerOff { get; }

    public RuntimePrepareCompensationStep Unregister { get; }

    public bool Complete => Unregister.Succeeded;
}

public sealed class RuntimePrepareCompensationStep
{
    public RuntimePrepareCompensationStep(bool attempted, bool succeeded, string message)
    {
        Attempted = attempted;
        Succeeded = succeeded;
        Message = message;
    }

    public bool Attempted { get; }

    public bool Succeeded { get; }

    public string Message { get; }
}

public sealed class RuntimeControlResult
{
    private RuntimeControlResult(string status, string state, string code, string message)
    {
        Status = status;
        State = state;
        Code = code;
        Message = message;
    }

    public string Status { get; }

    public string State { get; }

    public string Code { get; }

    public string Message { get; }

    public bool Succeeded => string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase);

    public static RuntimeControlResult Success(string state)
    {
        return new RuntimeControlResult("Succeeded", state, string.Empty, string.Empty);
    }

    public static RuntimeControlResult Failed(string status, string state, string code, string message)
    {
        return new RuntimeControlResult(status, state, code, message);
    }
}

public sealed class RuntimeReleaseResult
{
    private RuntimeReleaseResult(
        string status,
        string cleanupPreference,
        bool released,
        bool storageCleaned,
        string code,
        string message)
    {
        Status = status;
        CleanupPreference = cleanupPreference;
        Released = released;
        StorageCleaned = storageCleaned;
        Code = code;
        Message = message;
    }

    public string Status { get; }

    public string CleanupPreference { get; }

    public bool Released { get; }

    public bool StorageCleaned { get; }

    public string Code { get; }

    public string Message { get; }

    public bool Succeeded => string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase);

    public static RuntimeReleaseResult Success(string cleanupPreference, bool released, bool storageCleaned)
    {
        return new RuntimeReleaseResult("Succeeded", cleanupPreference, released, storageCleaned, string.Empty, string.Empty);
    }

    public static RuntimeReleaseResult Failed(string status, string code, string message)
    {
        return new RuntimeReleaseResult(status, string.Empty, false, false, code, message);
    }
}

public sealed class RuntimeControlRequest
{
    private const uint DefaultTimeoutMs = 30000;

    private RuntimeControlRequest(uint timeoutMs)
    {
        TimeoutMs = timeoutMs;
    }

    public uint TimeoutMs { get; }

    public static RuntimeControlRequest FromValues(IReadOnlyDictionary<string, string> values)
    {
        return new RuntimeControlRequest(
            values.TryGetValue("timeoutMs", out var timeoutText) && uint.TryParse(timeoutText, out var timeoutMs)
                ? timeoutMs
                : DefaultTimeoutMs);
    }
}

public sealed class RuntimePrepareRequest
{
    private const uint DefaultTimeoutMs = 30000;

    private RuntimePrepareRequest(string id, string instanceName, string storagePath, string provisioningMode, string cpuType, uint timeoutMs)
    {
        Id = id;
        InstanceName = instanceName;
        StoragePath = storagePath;
        ProvisioningMode = provisioningMode;
        CpuType = cpuType;
        TimeoutMs = timeoutMs;
    }

    public string Id { get; }

    public string InstanceName { get; }

    public string StoragePath { get; }

    public string ProvisioningMode { get; }

    public string CpuType { get; }

    public uint TimeoutMs { get; }

    public bool HasReference => !string.IsNullOrWhiteSpace(Id) || !string.IsNullOrWhiteSpace(InstanceName);

    public bool CanCreateOwnedRuntime => string.Equals(ProvisioningMode, "CreateOwned", StringComparison.OrdinalIgnoreCase);

    public static RuntimePrepareRequest FromValues(IReadOnlyDictionary<string, string> values)
    {
        return new RuntimePrepareRequest(
            values.TryGetValue("id", out var id) ? id : string.Empty,
            values.TryGetValue("instanceName", out var instanceName) ? instanceName : string.Empty,
            values.TryGetValue("storagePath", out var storagePath) ? storagePath : string.Empty,
            values.TryGetValue("provisioningMode", out var provisioningMode) ? provisioningMode : "UseExisting",
            values.TryGetValue("cpuType", out var cpuType) ? cpuType : "S71500",
            values.TryGetValue("timeoutMs", out var timeoutText) && uint.TryParse(timeoutText, out var timeoutMs)
                ? timeoutMs
                : DefaultTimeoutMs);
    }
}

public sealed class RuntimeReleaseRequest
{
    private const uint DefaultTimeoutMs = 30000;

    private RuntimeReleaseRequest(string cleanupPreference, uint timeoutMs)
    {
        CleanupPreference = cleanupPreference;
        TimeoutMs = timeoutMs;
    }

    public string CleanupPreference { get; }

    public uint TimeoutMs { get; }

    public static RuntimeReleaseRequest FromValues(IReadOnlyDictionary<string, string> values)
    {
        return new RuntimeReleaseRequest(
            values.TryGetValue("cleanupPreference", out var cleanupPreference) ? cleanupPreference : "Release",
            values.TryGetValue("timeoutMs", out var timeoutText) && uint.TryParse(timeoutText, out var timeoutMs)
                ? timeoutMs
                : DefaultTimeoutMs);
    }
}

public sealed class CommunicationPrepareRequest
{
    private const uint DefaultPrepareTimeoutMs = 30000;
    private const uint DefaultRevalidationTimeoutMs = 30000;

    private CommunicationPrepareRequest(
        string runtimeSessionId,
        string runControlRoot,
        string eventBufferRoot,
        string expectedRunControlContractVersion,
        string expectedEventStreamContractVersion,
        string observationStrategy,
        long? monitoringIntervalTicks,
        long? drainIntervalTicks,
        int? maxEventsPerBatch,
        uint prepareTimeoutMs,
        uint revalidationTimeoutMs)
    {
        RuntimeSessionId = runtimeSessionId;
        RunControlRoot = runControlRoot;
        EventBufferRoot = eventBufferRoot;
        ExpectedRunControlContractVersion = expectedRunControlContractVersion;
        ExpectedEventStreamContractVersion = expectedEventStreamContractVersion;
        ObservationStrategy = observationStrategy;
        MonitoringIntervalTicks = monitoringIntervalTicks;
        DrainIntervalTicks = drainIntervalTicks;
        MaxEventsPerBatch = maxEventsPerBatch;
        PrepareTimeoutMs = prepareTimeoutMs;
        RevalidationTimeoutMs = revalidationTimeoutMs;
    }

    public string RuntimeSessionId { get; }

    public string RunControlRoot { get; }

    public string EventBufferRoot { get; }

    public string ExpectedRunControlContractVersion { get; }

    public string ExpectedEventStreamContractVersion { get; }

    public string ObservationStrategy { get; }

    public long? MonitoringIntervalTicks { get; }

    public long? DrainIntervalTicks { get; }

    public int? MaxEventsPerBatch { get; }

    public uint PrepareTimeoutMs { get; }

    public uint RevalidationTimeoutMs { get; }

    public TimeSpan? MonitoringInterval => MonitoringIntervalTicks is { } ticks
        ? TimeSpan.FromTicks(ticks)
        : null;

    public TimeSpan? DrainInterval => DrainIntervalTicks is { } ticks
        ? TimeSpan.FromTicks(ticks)
        : null;

    [Obsolete("Use MonitoringInterval.")]
    public long? PollIntervalTicks => MonitoringIntervalTicks;

    [Obsolete("Use MonitoringInterval.")]
    public TimeSpan? PollInterval => MonitoringInterval;

    public static CommunicationPrepareRequest FromValues(IReadOnlyDictionary<string, string> values)
    {
        var observationStrategy = values.TryGetValue("observationStrategy", out var configuredObservationStrategy)
            ? configuredObservationStrategy
            : "Polling";
        return new CommunicationPrepareRequest(
            values.TryGetValue("runtimeSessionId", out var runtimeSessionId) ? runtimeSessionId : string.Empty,
            values.TryGetValue("runControlRoot", out var runControlRoot) ? runControlRoot : "DB_UnitTestRunControl",
            values.TryGetValue("eventBufferRoot", out var eventBufferRoot) ? eventBufferRoot : "DB_UnitTestEventBuffer",
            values.TryGetValue("expectedRunControlContractVersion", out var runControlContractVersion) ? runControlContractVersion : string.Empty,
            values.TryGetValue("expectedEventStreamContractVersion", out var eventStreamContractVersion) ? eventStreamContractVersion : string.Empty,
            observationStrategy,
            ReadMonitoringIntervalTicks(values, observationStrategy),
            ReadOptionalPositiveInt64(values, "drainIntervalTicks"),
            ReadOptionalPositiveInt32(values, "maxEventsPerBatch"),
            values.TryGetValue("prepareTimeoutMs", out var prepareTimeoutText)
                && uint.TryParse(prepareTimeoutText, out var prepareTimeoutMs)
                    ? prepareTimeoutMs
                    : DefaultPrepareTimeoutMs,
            values.TryGetValue("revalidationTimeoutMs", out var revalidationTimeoutText)
                && uint.TryParse(revalidationTimeoutText, out var revalidationTimeoutMs)
                    ? revalidationTimeoutMs
                    : DefaultRevalidationTimeoutMs);
    }

    private static long? ReadMonitoringIntervalTicks(
        IReadOnlyDictionary<string, string> values,
        string observationStrategy)
    {
        if (values.TryGetValue("monitoringIntervalTicks", out var monitoringText)
            && long.TryParse(monitoringText, out var monitoringTicks))
        {
            return monitoringTicks;
        }

        if (values.TryGetValue("pollIntervalTicks", out var pollText)
            && long.TryParse(pollText, out var pollTicks))
        {
            return pollTicks;
        }

        return string.Equals(observationStrategy, "PlcCycle", StringComparison.OrdinalIgnoreCase)
            ? null
            : TimeSpan.FromMilliseconds(10).Ticks;
    }

    private static long? ReadOptionalPositiveInt64(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        return values.TryGetValue(key, out var text)
            && long.TryParse(text, out var value)
            && value > 0
                ? value
                : null;
    }

    private static int? ReadOptionalPositiveInt32(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        return values.TryGetValue(key, out var text)
            && int.TryParse(text, out var value)
            && value > 0
                ? value
                : null;
    }
}

public sealed class CommunicationPrepareResult
{
    private CommunicationPrepareResult(
        string status,
        string sessionId,
        string runControlContractVersion,
        string runControlFrameworkVersion,
        string eventStreamContractVersion,
        string eventStreamFrameworkVersion,
        string code,
        string message)
    {
        Status = status;
        SessionId = sessionId;
        RunControlContractVersion = runControlContractVersion;
        RunControlFrameworkVersion = runControlFrameworkVersion;
        EventStreamContractVersion = eventStreamContractVersion;
        EventStreamFrameworkVersion = eventStreamFrameworkVersion;
        Code = code;
        Message = message;
    }

    public string Status { get; }

    public string SessionId { get; }

    public string RunControlContractVersion { get; }

    public string RunControlFrameworkVersion { get; }

    public string EventStreamContractVersion { get; }

    public string EventStreamFrameworkVersion { get; }

    public string Code { get; }

    public string Message { get; }

    public bool Succeeded => string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase);

    public static CommunicationPrepareResult Success(
        string sessionId,
        string runControlContractVersion,
        string runControlFrameworkVersion,
        string eventStreamContractVersion,
        string eventStreamFrameworkVersion)
    {
        return new CommunicationPrepareResult(
            "Succeeded",
            sessionId,
            runControlContractVersion,
            runControlFrameworkVersion,
            eventStreamContractVersion,
            eventStreamFrameworkVersion,
            string.Empty,
            string.Empty);
    }

    public static CommunicationPrepareResult Failed(string status, string code, string message)
    {
        return new CommunicationPrepareResult(status, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, code, message);
    }
}

public sealed class CommunicationCloseRequest
{
    private CommunicationCloseRequest(string runtimeSessionId, string communicationSessionId)
    {
        RuntimeSessionId = runtimeSessionId;
        CommunicationSessionId = communicationSessionId;
    }

    public string RuntimeSessionId { get; }

    public string CommunicationSessionId { get; }

    public static CommunicationCloseRequest FromValues(IReadOnlyDictionary<string, string> values)
    {
        return new CommunicationCloseRequest(
            values.TryGetValue("runtimeSessionId", out var runtimeSessionId) ? runtimeSessionId : string.Empty,
            values.TryGetValue("communicationSessionId", out var communicationSessionId) ? communicationSessionId : string.Empty);
    }
}

public sealed class CommunicationCloseResult
{
    private CommunicationCloseResult(string sessionId, string status, string code, string message)
    {
        SessionId = sessionId;
        Status = status;
        Code = code;
        Message = message;
    }

    public string SessionId { get; }

    public string Status { get; }

    public string Code { get; }

    public string Message { get; }

    public bool Succeeded => string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase);

    public static CommunicationCloseResult Success(string sessionId)
    {
        return new CommunicationCloseResult(sessionId, "Succeeded", string.Empty, string.Empty);
    }

    public static CommunicationCloseResult Failed(string sessionId, string status, string code, string message)
    {
        return new CommunicationCloseResult(sessionId, status, code, message);
    }
}

public sealed class RunControlObservationRequest
{
    public RunControlObservationRequest(string runtimeSessionId, string communicationSessionId, string runControlRoot)
    {
        RuntimeSessionId = runtimeSessionId;
        CommunicationSessionId = communicationSessionId;
        RunControlRoot = runControlRoot;
    }

    public string RuntimeSessionId { get; }

    public string CommunicationSessionId { get; }

    public string RunControlRoot { get; }

    public static RunControlObservationRequest FromValues(IReadOnlyDictionary<string, string> values)
    {
        return new RunControlObservationRequest(
            values.TryGetValue("runtimeSessionId", out var runtimeSessionId) ? runtimeSessionId : string.Empty,
            values.TryGetValue("communicationSessionId", out var communicationSessionId) ? communicationSessionId : string.Empty,
            values.TryGetValue("runControlRoot", out var runControlRoot) ? runControlRoot : "DB_UnitTestRunControl");
    }
}

public sealed class RunControlCommandRequest
{
    private const uint DefaultCommandTimeoutMs = 60000;

    private RunControlCommandRequest(
        string runtimeSessionId,
        string communicationSessionId,
        string runControlRoot,
        string command,
        uint timeoutMs)
    {
        RuntimeSessionId = runtimeSessionId;
        CommunicationSessionId = communicationSessionId;
        RunControlRoot = runControlRoot;
        Command = command;
        TimeoutMs = timeoutMs;
    }

    public string RuntimeSessionId { get; }

    public string CommunicationSessionId { get; }

    public string RunControlRoot { get; }

    public string Command { get; }

    public uint TimeoutMs { get; }

    public string CommandField
    {
        get
        {
            if (string.Equals(Command, "Start", StringComparison.OrdinalIgnoreCase))
            {
                return "Start";
            }

            if (string.Equals(Command, "Abort", StringComparison.OrdinalIgnoreCase))
            {
                return "Abort";
            }

            if (string.Equals(Command, "Reset", StringComparison.OrdinalIgnoreCase))
            {
                return "Reset";
            }

            return string.Empty;
        }
    }

    public static RunControlCommandRequest FromValues(IReadOnlyDictionary<string, string> values)
    {
        return new RunControlCommandRequest(
            values.TryGetValue("runtimeSessionId", out var runtimeSessionId) ? runtimeSessionId : string.Empty,
            values.TryGetValue("communicationSessionId", out var communicationSessionId) ? communicationSessionId : string.Empty,
            values.TryGetValue("runControlRoot", out var runControlRoot) ? runControlRoot : "DB_UnitTestRunControl",
            values.TryGetValue("command", out var command) ? command : string.Empty,
            values.TryGetValue("timeoutMs", out var timeoutText) && uint.TryParse(timeoutText, out var timeoutMs)
                ? timeoutMs
                : DefaultCommandTimeoutMs);
    }
}

public sealed class RunControlObservationResult
{
    private RunControlObservationResult(
        string status,
        UnitTestLifecycleState state,
        UnitTestExecutionStatus executionStatus,
        UnitTestResultState result,
        UnitTestExecutionSignals executionSignals,
        string code,
        string message)
    {
        Status = status;
        State = state;
        ExecutionStatus = executionStatus;
        Result = result;
        ExecutionSignals = executionSignals;
        Code = code;
        Message = message;
    }

    public string Status { get; }

    public UnitTestLifecycleState State { get; }

    public UnitTestExecutionStatus ExecutionStatus { get; }

    public UnitTestResultState Result { get; }

    public UnitTestExecutionSignals ExecutionSignals { get; }

    public bool Uninitialized => HasSignal(UnitTestExecutionSignals.Uninitialized);

    public bool ConfigError => HasSignal(UnitTestExecutionSignals.ConfigError);

    public bool Ready => HasSignal(UnitTestExecutionSignals.Ready);

    public bool Running => HasSignal(UnitTestExecutionSignals.Running);

    public bool Completed => HasSignal(UnitTestExecutionSignals.Completed);

    public bool Aborted => HasSignal(UnitTestExecutionSignals.Aborted);

    public bool Inactive => HasSignal(UnitTestExecutionSignals.Inactive);

    public bool Error => HasSignal(UnitTestExecutionSignals.Error);

    public string Code { get; }

    public string Message { get; }

    public bool Succeeded => string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase);

    public static RunControlObservationResult Success(
        UnitTestLifecycleState state,
        UnitTestExecutionStatus executionStatus,
        UnitTestResultState result,
        UnitTestExecutionSignals executionSignals)
    {
        return new RunControlObservationResult(
            "Succeeded",
            state,
            executionStatus,
            result,
            executionSignals,
            string.Empty,
            string.Empty);
    }

    public static RunControlObservationResult Failed(string status, string code, string message)
    {
        return new RunControlObservationResult(
            status,
            UnitTestLifecycleState.Unknown,
            UnitTestExecutionStatus.Unknown,
            UnitTestResultState.NotEvaluated,
            UnitTestExecutionSignals.None,
            code,
            message);
    }

    public static RunControlObservationResult Unavailable()
    {
        return Failed(
            "Unavailable",
            "PlcsimAdvanced.V8.Worker.RunControlObservationUnavailable",
            "RunControl observation is unavailable.");
    }

    private bool HasSignal(UnitTestExecutionSignals signal)
    {
        return (ExecutionSignals & signal) == signal;
    }
}

public sealed class RunControlCommandResult
{
    private RunControlCommandResult(
        string status,
        string command,
        bool accepted,
        string code,
        string message)
    {
        Status = status;
        Command = command;
        Accepted = accepted;
        Code = code;
        Message = message;
    }

    public string Status { get; }

    public string Command { get; }

    public bool Accepted { get; }

    public string Code { get; }

    public string Message { get; }

    public bool Succeeded => string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase);

    public static RunControlCommandResult AcceptedResult(string command)
    {
        return new RunControlCommandResult(
            "Succeeded",
            command,
            true,
            string.Empty,
            string.Empty);
    }

    public static RunControlCommandResult Failed(
        string command,
        string status,
        string code,
        string message)
    {
        return new RunControlCommandResult(
            status,
            command,
            false,
            code,
            message);
    }
}

public sealed class EventBufferResetRequest
{
    private const uint DefaultCommandTimeoutMs = 60000;

    private EventBufferResetRequest(
        string runtimeSessionId,
        string communicationSessionId,
        string runControlRoot,
        uint timeoutMs)
    {
        RuntimeSessionId = runtimeSessionId;
        CommunicationSessionId = communicationSessionId;
        RunControlRoot = runControlRoot;
        TimeoutMs = timeoutMs;
    }

    public string RuntimeSessionId { get; }

    public string CommunicationSessionId { get; }

    public string RunControlRoot { get; }

    public uint TimeoutMs { get; }

    public static EventBufferResetRequest FromValues(IReadOnlyDictionary<string, string> values)
    {
        return new EventBufferResetRequest(
            values.TryGetValue("runtimeSessionId", out var runtimeSessionId) ? runtimeSessionId : string.Empty,
            values.TryGetValue("communicationSessionId", out var communicationSessionId) ? communicationSessionId : string.Empty,
            values.TryGetValue("runControlRoot", out var runControlRoot) ? runControlRoot : "DB_UnitTestRunControl",
            values.TryGetValue("timeoutMs", out var timeoutText) && uint.TryParse(timeoutText, out var timeoutMs)
                ? timeoutMs
                : DefaultCommandTimeoutMs);
    }
}

public sealed class EventBufferResetResult
{
    private EventBufferResetResult(string status, bool accepted, string code, string message)
    {
        Status = status;
        Accepted = accepted;
        Code = code;
        Message = message;
    }

    public string Status { get; }

    public bool Accepted { get; }

    public string Code { get; }

    public string Message { get; }

    public bool Succeeded => string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase);

    public static EventBufferResetResult AcceptedResult()
    {
        return new EventBufferResetResult("Succeeded", true, string.Empty, string.Empty);
    }

    public static EventBufferResetResult Failed(string status, string code, string message)
    {
        return new EventBufferResetResult(status, false, code, message);
    }
}

public sealed class EventStreamMonitoringRequest
{
    private EventStreamMonitoringRequest(string runtimeSessionId, string communicationSessionId, string runControlRoot, string eventBufferRoot)
    {
        RuntimeSessionId = runtimeSessionId;
        CommunicationSessionId = communicationSessionId;
        RunControlRoot = runControlRoot;
        EventBufferRoot = eventBufferRoot;
    }

    public string RuntimeSessionId { get; }

    public string CommunicationSessionId { get; }

    public string RunControlRoot { get; }

    public string EventBufferRoot { get; }

    public static EventStreamMonitoringRequest FromValues(IReadOnlyDictionary<string, string> values)
    {
        return new EventStreamMonitoringRequest(
            values.TryGetValue("runtimeSessionId", out var runtimeSessionId) ? runtimeSessionId : string.Empty,
            values.TryGetValue("communicationSessionId", out var communicationSessionId) ? communicationSessionId : string.Empty,
            values.TryGetValue("runControlRoot", out var runControlRoot) ? runControlRoot : "DB_UnitTestRunControl",
            values.TryGetValue("eventBufferRoot", out var eventBufferRoot) ? eventBufferRoot : "DB_UnitTestEventBuffer");
    }
}

public sealed class EventStreamMonitoringResult
{
    private EventStreamMonitoringResult(
        string status,
        string communicationSessionId,
        bool isMonitoring,
        string lastReadState,
        string batchReleaseState,
        bool bufferObservedEmpty,
        ushort lostEventCount,
        bool bufferFull,
        uint producerSequenceId,
        uint deletedSequenceId,
        string code,
        string message,
        EventStreamCapturedBatch? capturedBatch = null)
    {
        Status = status;
        CommunicationSessionId = communicationSessionId;
        IsMonitoring = isMonitoring;
        LastReadState = lastReadState;
        BatchReleaseState = batchReleaseState;
        BufferObservedEmpty = bufferObservedEmpty;
        LostEventCount = lostEventCount;
        BufferFull = bufferFull;
        ProducerSequenceId = producerSequenceId;
        DeletedSequenceId = deletedSequenceId;
        Code = code;
        Message = message;
        CapturedBatch = capturedBatch;
    }

    public string Status { get; }

    public string CommunicationSessionId { get; }

    public bool IsMonitoring { get; }

    public string LastReadState { get; }

    public string BatchReleaseState { get; }

    public bool BufferObservedEmpty { get; }

    public ushort LostEventCount { get; }

    public bool BufferFull { get; }

    public uint ProducerSequenceId { get; }

    public uint DeletedSequenceId { get; }

    public string Code { get; }

    public string Message { get; }

    public EventStreamCapturedBatch? CapturedBatch { get; }

    public bool Succeeded => string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "SucceededWithWarnings", StringComparison.OrdinalIgnoreCase);

    public static EventStreamMonitoringResult CreateMonitoringResult(
        string communicationSessionId,
        bool isMonitoring,
        PlcsimEventStreamMonitoringState? observation = null)
    {
        observation = GetSessionObservation(communicationSessionId, observation);
        return new EventStreamMonitoringResult(
            "Succeeded",
            communicationSessionId,
            isMonitoring,
            observation.LastReadState.ToString(),
            observation.BatchReleaseState.ToString(),
            observation.BufferObservedEmpty,
            observation.LostEventCount,
            observation.BufferFull,
            observation.ProducerSequenceId,
            observation.DeletedSequenceId,
            string.Empty,
            string.Empty);
    }

    public static EventStreamMonitoringResult CreateBufferObservationResult(
        string communicationSessionId,
        bool isMonitoring,
        EventBufferPollingSnapshotClassification classification)
    {
        return CreateBufferObservationResult(
            communicationSessionId,
            isMonitoring,
            classification,
            "Idle",
            null);
    }

    public static EventStreamMonitoringResult CreateBufferObservationResult(
        string communicationSessionId,
        bool isMonitoring,
        EventBufferPollingSnapshotClassification classification,
        string batchReleaseState,
        EventStreamCapturedBatch? capturedBatch,
        PlcsimEventStreamMonitoringState? observation = null,
        string code = "",
        string message = "")
    {
        var snapshot = classification.Snapshot;
        observation = GetSessionObservation(communicationSessionId, observation ?? new PlcsimEventStreamMonitoringState(
            communicationSessionId, isMonitoring, snapshot.ReadState, EventStreamBatchReleaseState.Idle,
            snapshot.FifoControl.BufferEmpty, snapshot.Status.LostEventCount, snapshot.FifoControl.BufferFull,
            snapshot.Status.SequenceId, snapshot.Status.DeletedSequenceId));
        var status = snapshot.ReadState == EventBufferReadState.Valid || snapshot.ReadState == EventBufferReadState.Empty
            ? "Succeeded"
            : "SucceededWithWarnings";
        if (!string.IsNullOrEmpty(code))
        {
            status = "SucceededWithWarnings";
        }

        return new EventStreamMonitoringResult(
            status,
            communicationSessionId,
            isMonitoring,
            snapshot.ReadState.ToString(),
            batchReleaseState,
            observation.BufferObservedEmpty,
            observation.LostEventCount,
            observation.BufferFull,
            observation.ProducerSequenceId,
            observation.DeletedSequenceId,
            code,
            message,
            capturedBatch);
    }

    public static EventStreamMonitoringResult CreateFailureResult(
        string status, string communicationSessionId, string code, string message,
        PlcsimEventStreamMonitoringState? observation = null)
    {
        observation = GetSessionObservation(communicationSessionId, observation);
        return new EventStreamMonitoringResult(
            status,
            communicationSessionId,
            false,
            "SessionInvalid",
            observation.BatchReleaseState.ToString(),
            observation.BufferObservedEmpty,
            observation.LostEventCount,
            observation.BufferFull,
            observation.ProducerSequenceId,
            observation.DeletedSequenceId,
            code,
            message);
    }

    private static PlcsimEventStreamMonitoringState GetSessionObservation(
        string communicationSessionId, PlcsimEventStreamMonitoringState? observation)
    {
        return observation is not null
            && string.Equals(observation.CommunicationSessionId, communicationSessionId, StringComparison.Ordinal)
                ? observation
                : new PlcsimEventStreamMonitoringState(communicationSessionId, false,
                    EventBufferReadState.NotObserved, EventStreamBatchReleaseState.Idle, false, 0);
    }
}

public sealed class EventStreamCapturedBatch
{
    private EventStreamCapturedBatch(
        string communicationSessionId,
        uint producerSequenceId,
        uint deletedSequenceId,
        ushort lostEventCount,
        bool bufferObservedEmpty,
        string readState,
        IReadOnlyList<EventBufferCapturedEntry> capturedEntries)
    {
        CommunicationSessionId = communicationSessionId;
        ProducerSequenceId = producerSequenceId;
        DeletedSequenceId = deletedSequenceId;
        LostEventCount = lostEventCount;
        BufferObservedEmpty = bufferObservedEmpty;
        ReadState = readState;
        CapturedEntries = capturedEntries;
    }

    public string CommunicationSessionId { get; }

    public uint ProducerSequenceId { get; }

    public uint DeletedSequenceId { get; }

    public ushort LostEventCount { get; }

    public bool BufferObservedEmpty { get; }

    public string ReadState { get; }

    public IReadOnlyList<EventBufferCapturedEntry> CapturedEntries { get; }

    public uint HighestCapturedSequenceId => CapturedEntries.Count == 0
        ? 0
        : CapturedEntries[CapturedEntries.Count - 1].SequenceId;

    public static EventStreamCapturedBatch? FromClassification(
        string communicationSessionId,
        EventBufferPollingSnapshotClassification classification,
        IReadOnlyList<EventBufferCapturedEntry> capturedEntries,
        PlcsimEventStreamMonitoringState? observation = null)
    {
        var snapshot = classification.Snapshot;
        if (observation is not null
            && !string.Equals(observation.CommunicationSessionId, communicationSessionId, StringComparison.Ordinal))
        {
            observation = null;
        }
        if (classification.CapturedSequences.Count == 0)
        {
            return capturedEntries.Count == 0
                && snapshot.ReadState == EventBufferReadState.Empty
                && snapshot.FifoControl.BufferEmpty
                    ? new EventStreamCapturedBatch(
                        communicationSessionId,
                        observation?.ProducerSequenceId ?? snapshot.Status.SequenceId,
                        observation?.DeletedSequenceId ?? snapshot.Status.DeletedSequenceId,
                        observation?.LostEventCount ?? snapshot.Status.LostEventCount,
                        bufferObservedEmpty: observation?.BufferObservedEmpty ?? true,
                        snapshot.ReadState.ToString(),
                        capturedEntries)
                    : null;
        }

        if (capturedEntries.Count != classification.CapturedSequences.Count)
        {
            return null;
        }

        for (var index = 0; index < capturedEntries.Count; index++)
        {
            if (capturedEntries[index].BufferIndex != classification.CapturedSequences[index].BufferIndex
                || capturedEntries[index].SequenceId != classification.CapturedSequences[index].SequenceId)
            {
                return null;
            }
        }

        return new EventStreamCapturedBatch(
            communicationSessionId,
            observation?.ProducerSequenceId ?? snapshot.Status.SequenceId,
            observation?.DeletedSequenceId ?? snapshot.Status.DeletedSequenceId,
            observation?.LostEventCount ?? snapshot.Status.LostEventCount,
            observation?.BufferObservedEmpty ?? snapshot.FifoControl.BufferEmpty,
            snapshot.ReadState.ToString(),
            capturedEntries);
    }
}

public sealed class EventBufferCapturedEntry
{
    public EventBufferCapturedEntry(
        ushort bufferIndex,
        uint sequenceId,
        long plcTimestampNanoseconds,
        byte ownerScope,
        byte eventType,
        byte severity,
        string name,
        string resourceName,
        short state,
        short result,
        string message)
    {
        BufferIndex = bufferIndex;
        SequenceId = sequenceId;
        PlcTimestampNanoseconds = plcTimestampNanoseconds;
        OwnerScope = ownerScope;
        EventType = eventType;
        Severity = severity;
        Name = name ?? string.Empty;
        ResourceName = resourceName ?? string.Empty;
        State = state;
        Result = result;
        Message = message ?? string.Empty;
    }

    public ushort BufferIndex { get; }

    public uint SequenceId { get; }

    public long PlcTimestampNanoseconds { get; }

    public byte OwnerScope { get; }

    public byte EventType { get; }

    public byte Severity { get; }

    public string Name { get; }

    public string ResourceName { get; }

    public short State { get; }

    public short Result { get; }

    public string Message { get; }
}

public sealed class EventStreamReleaseResult
{
    private EventStreamReleaseResult(
        string status,
        string releaseState,
        uint deletedSequenceId,
        string code,
        string message)
    {
        Status = status;
        ReleaseState = releaseState;
        DeletedSequenceId = deletedSequenceId;
        Code = code;
        Message = message;
    }

    public string Status { get; }

    public string ReleaseState { get; }

    public uint DeletedSequenceId { get; }

    public string Code { get; }

    public string Message { get; }

    public bool Succeeded => string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase);

    public static EventStreamReleaseResult Accepted(uint deletedSequenceId)
    {
        return new EventStreamReleaseResult("Succeeded", "Completed", deletedSequenceId, string.Empty, string.Empty);
    }

    public static EventStreamReleaseResult Deferred(uint deletedSequenceId, string code, string message)
    {
        return new EventStreamReleaseResult("Rejected", "Pending", deletedSequenceId, code, message);
    }

    public static EventStreamReleaseResult Failed(string status, uint deletedSequenceId, string code, string message)
    {
        return new EventStreamReleaseResult(status, "Failed", deletedSequenceId, code, message);
    }
}

public sealed class EventStreamMonitoringFault
{
    private EventStreamMonitoringFault(string status, string communicationSessionId, string code, string message)
    {
        Status = status;
        CommunicationSessionId = communicationSessionId;
        Code = code;
        Message = message;
    }

    public string Status { get; }

    public string CommunicationSessionId { get; }

    public string Code { get; }

    public string Message { get; }

    public static EventStreamMonitoringFault FromResult(string communicationSessionId, EventStreamMonitoringResult result)
    {
        return new EventStreamMonitoringFault(
            result.Status,
            communicationSessionId,
            string.IsNullOrWhiteSpace(result.Code) ? "PlcsimAdvanced.V8.Worker.EventStreamObservationFaulted" : result.Code,
            string.IsNullOrWhiteSpace(result.Message) ? "EventStream observation faulted." : result.Message);
    }

    public static EventStreamMonitoringFault Failed(string communicationSessionId, string code, string message)
    {
        return new EventStreamMonitoringFault("Failed", communicationSessionId, code, message);
    }
}

public sealed class EventBufferPollingSnapshotClassification
{
    public EventBufferPollingSnapshotClassification(
        PlcsimEventBufferSnapshot snapshot,
        IReadOnlyList<ushort> readablePositions,
        IReadOnlyList<EventBufferCapturedSequence> capturedSequences)
    {
        Snapshot = snapshot;
        ReadablePositions = readablePositions;
        CapturedSequences = capturedSequences;
    }

    public PlcsimEventBufferSnapshot Snapshot { get; }

    public IReadOnlyList<ushort> ReadablePositions { get; }

    public IReadOnlyList<EventBufferCapturedSequence> CapturedSequences { get; }
}

public sealed class EventBufferCapturedSequence
{
    public EventBufferCapturedSequence(ushort bufferIndex, uint sequenceId)
    {
        BufferIndex = bufferIndex;
        SequenceId = sequenceId;
    }

    public ushort BufferIndex { get; }

    public uint SequenceId { get; }
}
