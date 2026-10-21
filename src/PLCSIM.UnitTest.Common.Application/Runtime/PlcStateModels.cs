namespace PLCSIM.UnitTest.Common.Runtime;

/// <summary>
/// Provider-neutral lifecycle state of the runtime instance hosting a PLC.
/// </summary>
public enum RuntimeInstanceState
{
    Unknown,
    Unavailable,
    Starting,
    Available,
    Stopping,
    Faulted
}

/// <summary>
/// Provider-neutral operating state of a PLC.
/// </summary>
public enum PlcOperatingState
{
    Unknown,
    Unavailable,
    Stopped,
    Starting,
    Running,
    Stopping,
    Frozen,
    Held
}
