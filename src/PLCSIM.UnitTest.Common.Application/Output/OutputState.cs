using PLCSIM.UnitTest.Common.Artifacts;
using PLCSIM.UnitTest.Common.Identifiers;

namespace PLCSIM.UnitTest.Common.Output;

/// <summary>
/// Current application-facing output state.
/// </summary>
public sealed record OutputState(
    IReadOnlyList<ExportResult> Artifacts,
    string? LogFilePath,
    string? TerminalText);

/// <summary>
/// Event arguments for output state changes.
/// </summary>
public sealed record OutputStateChangedEventArgs(
    OutputState OutputState,
    RunEpochId? RunEpochId = null);
