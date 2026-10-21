using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

public sealed record PlcsimEventBufferContractSnapshot(
    string ContractVersion,
    string? FrameworkVersion,
    bool ContractValid);

public sealed record PlcsimEventBufferFifoControlSnapshot(
    ushort IndexOut,
    ushort IndexIn,
    ushort BufferLength,
    bool BufferEmpty,
    bool BufferFull,
    bool ConfigError,
    bool IndexError);

public sealed record PlcsimEventBufferStatusSnapshot(
    uint SequenceId,
    uint DeletedSequenceId,
    ushort LostEventCount);

public sealed record PlcsimEventBufferEntrySnapshot(
    ushort BufferIndex,
    UnitTestEventEntry Entry);

public sealed record PlcsimEventBufferSnapshot(
    PlcsimEventBufferContractSnapshot Contract,
    PlcsimEventBufferFifoControlSnapshot FifoControl,
    PlcsimEventBufferStatusSnapshot Status,
    IReadOnlyList<PlcsimEventBufferEntrySnapshot> Entries,
    EventBufferReadState ReadState,
    DateTimeOffset ObservedAt);

public sealed record PlcsimEventBufferReleaseSnapshot(
    uint DeleteSequenceId,
    bool ApiWriteAccepted,
    DateTimeOffset RequestedAt);

public sealed record PlcsimEventBufferWorkerBatchPayload(
    UnitTestEventBatch Batch,
    PlcsimEventBufferSnapshot Snapshot,
    PlcsimEventBufferReleaseSnapshot Release,
    PlcsimEventBufferBackpressureSnapshot Backpressure);

public sealed record PlcsimEventBufferBackpressureSnapshot(
    bool QueueWasFull,
    bool QueuePressureObserved,
    bool EventLossObserved,
    string? Message);
