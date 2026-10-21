namespace PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

public enum TiaOperationStatus
{
    Succeeded,
    SucceededWithWarnings,
    Failed,
    NotSupported,
    Cancelled,
    TimedOut,
    Invalidated,
    Unclear
}

public enum TiaDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public enum TiaAuthorizationProbeStatus
{
    Authorized,
    Denied,
    Unavailable,
    TimedOut,
    Failed,
    CleanupFailed
}

public sealed record TiaAuthorizationProbeRequest(
    string? CorrelationId);

public sealed record TiaAuthorizationProbeResult(
    TiaAuthorizationProbeStatus Status,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool IsAuthorized => Status == TiaAuthorizationProbeStatus.Authorized;
}

public enum TiaProjectHealthState
{
    NotEstablished,
    Healthy,
    RevalidationRequired,
    Invalidated,
    Disposed
}

public enum TiaTransferConnectionMode
{
    PnIe
}

public enum TiaTransferPcInterface
{
    PlcSim
}

public enum TiaProjectOpenMode
{
    DirectProject,
    RetrievedArchive,
    AttachExisting
}

public enum TiaSafetyProgramAuthorizationRequirement
{
    NotRequired,
    PasswordRequired
}

public sealed record TiaSafetyProgramAuthorizationRequirementResult(
    TiaOperationStatus Status,
    TiaSafetyProgramAuthorizationRequirement Requirement,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;
}

public sealed record TiaSafetyProgramAuthorizationResult(
    TiaOperationStatus Status,
    bool Authorized,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;
}

public sealed record TiaProjectOpenRequest(
    string ProjectSource,
    TiaProjectOpenMode OpenMode,
    string? ArchiveWorkDirectory,
    string? CorrelationId);

public sealed record TiaEngineeringSessionOpenResult(
    TiaOperationStatus Status,
    ITiaEngineeringSession? Session,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;
}

public sealed record TiaProjectSessionInfo(
    string SessionId,
    string ProjectSource,
    string ProductVersion,
    string ProjectVersion);

public sealed record TiaProjectTargetRef(
    string Id,
    string DisplayName,
    string TechnicalPath,
    string CpuFamily,
    string CpuType);

public sealed record TiaProjectTargetDiscoveryRequest(
    string? PlcRefId);

public sealed record TiaProjectTargetDiscoveryResult(
    TiaOperationStatus Status,
    IReadOnlyList<TiaProjectTargetRef> Targets,
    TiaProjectTargetRef? SelectedTarget,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;
}

public sealed record TiaBuildRequest(
    TiaProjectTargetRef Target);

public sealed record TiaBuildResult(
    TiaOperationStatus Status,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;
}

public sealed record TiaRuntimeTransferTarget(
    string Id,
    string DisplayName,
    string RuntimeKind,
    string RuntimeVersion,
    SiemensRuntimeLocality Locality,
    string? RuntimeInstanceName,
    TiaTransferConnectionMode ConnectionMode,
    TiaTransferPcInterface PcInterface,
    int? PcInterfaceNumber,
    string? TargetInterfaceName);

public sealed record TiaTransferRequest(
    TiaProjectTargetRef ProjectTarget,
    TiaRuntimeTransferTarget RuntimeTarget);

public sealed record TiaTransferResult(
    TiaOperationStatus Status,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;
}

public sealed record TiaProgressSnapshot(
    string Operation,
    string Phase,
    string Message,
    int? Percent);

public sealed record TiaDiagnostic(
    string Code,
    TiaDiagnosticSeverity Severity,
    string Message,
    string Phase,
    string? EvidenceId);

public sealed record TiaEvidence(
    string Id,
    string Kind,
    string Summary);
