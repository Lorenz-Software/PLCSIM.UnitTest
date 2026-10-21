using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.Common.Identifiers;

namespace PLCSIM.UnitTest.Common.UnitTests;

/// <summary>
/// Contract-near batch of UnitTest event entries handed from an access profile to shared core.
/// </summary>
public sealed record UnitTestEventBatch(
    CommunicationSessionId SessionId,
    IReadOnlyList<UnitTestEventEntry> Events,
    uint ProducerSequenceId,
    uint DeletedSequenceId,
    ushort LostEventCount,
    bool BufferObservedEmpty,
    EventBufferReadState ReadState);

/// <summary>
/// Contract-near UnitTest event entry normalized by an access profile.
/// </summary>
public sealed record UnitTestEventEntry(
    uint SequenceId,
    DateTimeOffset Timestamp,
    UnitTestOwnerScope OwnerScope,
    UnitTestEventType EventType,
    LogLevel Severity,
    string Name,
    string ResourceName,
    UnitTestLifecycleState State,
    UnitTestResultState Result,
    string Message);

/// <summary>
/// PLC-owned owner scope values from the UnitTest event-stream contract.
/// </summary>
public enum UnitTestOwnerScope
{
    Unknown = 0,
    Framework = 5,
    RunControl = 10,
    TestSuiteOrchestrator = 15,
    TestSuite = 20,
    TestCaseOrchestrator = 25,
    TestCase = 30,
    Assertion = 40,
    Preparation = 50
}

/// <summary>
/// PLC-owned event type values from the UnitTest event-stream contract.
/// </summary>
public enum UnitTestEventType
{
    Unknown = 0,
    Result = 10,
    Trigger = 20,
    StateTransition = 30,
    Info = 40,
    Aggregation = 50,
    Framework = 60
}
