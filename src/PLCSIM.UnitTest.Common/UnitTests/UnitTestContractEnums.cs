namespace PLCSIM.UnitTest.Common.UnitTests;

/// <summary>
/// Detailed PLC-owned RunControl state-machine values from the communication contract.
/// </summary>
public enum UnitTestLifecycleState
{
    Error = -10,
    Uninitialized = 0,
    Reset = 1,
    Idle = 10,
    Starting = 11,
    Preparing = 20,
    Running = 50,
    Completing = 80,
    Completed = 89,
    Aborting = 90,
    AbortingFromError = 91,
    Aborted = 99,
    Unknown = 999
}

/// <summary>
/// PLC-owned UnitTest result state values from the communication contract.
/// </summary>
public enum UnitTestResultState
{
    Error = -10,
    NotEvaluated = 0,
    Passed = 10,
    Failed = 20,
    Abort = 30,
    Unknown = 999
}

/// <summary>
/// PLC-owned coarse UnitTest execution status values from the PLCSIM Advanced communication contract.
/// </summary>
public enum UnitTestExecutionStatus
{
    Error = -10,
    Unknown = 0,
    Inactive = 1,
    NotReady = 9,
    Idle = 10,
    Starting = 20,
    Running = 30,
    Completing = 40,
    Completed = 50,
    Aborting = 60,
    Aborted = 70,
    Reset = 80
}

/// <summary>
/// Independent PLC-owned UnitTest execution signals from the communication contract.
/// </summary>
[Flags]
public enum UnitTestExecutionSignals : ushort
{
    None = 0,
    Uninitialized = 1 << 0,
    ConfigError = 1 << 1,
    Ready = 1 << 2,
    Running = 1 << 3,
    Completed = 1 << 4,
    Aborted = 1 << 5,
    Inactive = 1 << 6,
    Error = 1 << 7
}

/// <summary>
/// UnitTest RunControl commands exposed to applications and profile bindings.
/// </summary>
public enum UnitTestRunCommand
{
    Start,
    Abort,
    Reset
}

/// <summary>
/// Technical read state of an event-buffer access attempt.
/// </summary>
public enum EventBufferReadState
{
    Valid,
    Empty,
    RetryRequired,
    Inconsistent,
    ContractInvalid,
    SessionInvalid,
    /// <summary>No event-buffer read outcome has been observed in this communication session.</summary>
    NotObserved
}

/// <summary>
/// PC-side release state of captured event data for further processing.
/// This is not the PLC deletion request or its acknowledgement.
/// </summary>
public enum EventStreamBatchReleaseState
{
    /// <summary>Reserved legacy value. Use Idle when there is no batch to release.</summary>
    NotRequired,
    Idle,
    Pending,
    Completed,
    Rejected,
    Failed,
    Unclear,
    Invalidated
}
