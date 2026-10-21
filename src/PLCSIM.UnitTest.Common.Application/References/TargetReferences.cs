using PLCSIM.UnitTest.Common.Identifiers;

namespace PLCSIM.UnitTest.Common.References;

/// <summary>
/// Application-facing reference to a PLC project target.
/// </summary>
public sealed record ProjectTargetRef(
    ProjectTargetId Id,
    string DisplayName,
    string? TechnicalName = null);

/// <summary>
/// Application-facing reference to a PLC runtime target.
/// </summary>
public sealed record RuntimeTargetRef(
    RuntimeTargetId Id,
    string DisplayName,
    string? TechnicalName = null);

/// <summary>
/// Prepared PLC project context exposed by an access profile.
/// </summary>
public sealed record PlcProjectContext(
    ProjectContextId Id,
    string DisplayName,
    string? SourcePath = null);

/// <summary>
/// Prepared PLC runtime context exposed by an access profile.
/// </summary>
public sealed record PlcRuntimeContext(
    RuntimeContextId Id,
    string DisplayName,
    string? RuntimeName = null);
