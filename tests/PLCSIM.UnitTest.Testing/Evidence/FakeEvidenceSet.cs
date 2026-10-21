using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.Testing.Evidence;

/// <summary>
/// Named set of contract-near UnitTest event batches.
/// </summary>
public sealed record FakeEvidenceSet(
    string Name,
    string Description,
    IReadOnlyList<UnitTestEventBatch> Batches);
