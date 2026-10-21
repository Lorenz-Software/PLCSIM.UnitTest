using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Common.Services;

/// <summary>
/// Event arguments emitted when the access profile has read and released a UnitTest event batch.
/// </summary>
public sealed record EventBatchReceivedEventArgs(
    UnitTestEventBatch Batch);
