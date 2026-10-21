using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20;

internal interface ITiaOpennessV20EngineeringSessionHost
{
    Task<TiaOpennessV20EngineeringSessionHostOpenResult> OpenAsync(
        string publicApiDirectory,
        TiaProjectOpenRequest request,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken);
}

internal interface ITiaOpennessV20EngineeringSessionHandle
{
    string SessionId { get; }

    string ProjectSource { get; }

    string ProductVersion { get; }

    string ProjectVersion { get; }

    Task<TiaOpennessV20TargetDiscoveryHostResult> DiscoverTargetsAsync(CancellationToken cancellationToken);

    Task<TiaOpennessV20SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken);

    Task<TiaOpennessV20SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken);

    Task<TiaOpennessV20BuildHostResult> BuildAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken);

    Task<TiaOpennessV20TransferHostResult> TransferAsync(
        TiaProjectTargetRef projectTarget,
        TiaRuntimeTransferTarget runtimeTarget,
        CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

internal sealed class TiaOpennessV20EngineeringSessionHost : ITiaOpennessV20EngineeringSessionHost
{
    public async Task<TiaOpennessV20EngineeringSessionHostOpenResult> OpenAsync(
        string publicApiDirectory,
        TiaProjectOpenRequest request,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(bindingContext);

        if (string.IsNullOrWhiteSpace(publicApiDirectory))
        {
            return TiaOpennessV20EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V20.EngineeringSessionPublicApiMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V20 engineering-session host requires a PublicAPI directory.",
                    "OpenEngineeringSession",
                    "tia-openness-v20-engineering-session-host"),
                Array.Empty<TiaEvidence>());
        }

        if (string.IsNullOrWhiteSpace(request.ProjectSource))
        {
            return TiaOpennessV20EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V20.EngineeringSessionProjectSourceMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V20 engineering-session host requires a project source.",
                    "OpenEngineeringSession",
                    "tia-openness-v20-engineering-session-host"),
                Array.Empty<TiaEvidence>());
        }

        var projectSource = Path.GetFullPath(request.ProjectSource);
        var workerPath = TiaOpennessV20WorkerProcessHost.ResolveDeployedWorkerPath();
        var pipeName = TiaOpennessV20WorkerProcessHost.CreatePipeName();
        if (!File.Exists(workerPath))
        {
            return TiaOpennessV20EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V20.EngineeringSessionWorkerMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V20 engineering-session worker executable was not found.",
                    "OpenEngineeringSession",
                    "tia-openness-v20-engineering-session-host"),
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v20-engineering-session-host",
                        "EngineeringSessionHost",
                        workerPath)
                });
        }

        var workerInstanceId = Guid.NewGuid().ToString("N");
        TiaOpennessV20ArchiveWorkspace? archiveWorkspace = TiaOpennessV20ArchiveWorkspace.Create(
            request.OpenMode,
            request.ArchiveWorkDirectory,
            workerInstanceId);
        var startInfo = TiaOpennessV20WorkerProcessHost.CreateWorkerStartInfo(
            workerPath,
            pipeName,
            publicApiDirectory,
            projectSource,
            request.OpenMode,
            archiveWorkspace?.OwnedDirectory,
            bindingContext,
            workerInstanceId);

        TiaOpennessV20WorkerProcessLifetime? processLifetime = null;
        try
        {
            var processStart = await new TiaOpennessV20WorkerProcessHost()
                .StartAsync(startInfo, pipeName, cancellationToken)
                .ConfigureAwait(false);
            if (!processStart.Succeeded || processStart.Lifetime is null)
            {
                var failure = TiaOpennessV20EngineeringSessionHostOpenResult.Failed(
                    new TiaDiagnostic(
                        processStart.Code,
                        TiaDiagnosticSeverity.Error,
                        processStart.Message,
                        "OpenEngineeringSession",
                        "tia-openness-v20-engineering-session-host"),
                    processStart.Evidence);
                return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
            }

            processLifetime = processStart.Lifetime;
            var initialResponse = processStart.InitialResponse
                ?? throw new InvalidOperationException(
                    "TIA Openness V20 worker process host succeeded without an initial response.");
            var response = TiaOpennessV20WorkerResponse.Parse(initialResponse);
            if (!response.IsReady)
            {
                string error;
                if (response.Code == "TiaOpenness.V20.EngineeringSessionWorkerInvalidResponse")
                {
                    error = processLifetime.CompleteErrorCapture();
                    processLifetime.Terminate();
                }
                else
                {
                    error = await processLifetime.CompleteFailedStartAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                var failure = TiaOpennessV20EngineeringSessionHostOpenResult.Failed(
                    new TiaDiagnostic(
                        response.Code ?? "TiaOpenness.V20.EngineeringSessionOpenFailed",
                        TiaDiagnosticSeverity.Error,
                        response.Message ?? "TIA Openness V20 engineering-session worker failed to open a session.",
                        "OpenEngineeringSession",
                        "tia-openness-v20-engineering-session-host"),
                    new[]
                    {
                        new TiaEvidence(
                            "tia-openness-v20-engineering-session-worker-error",
                            "EngineeringSessionWorkerError",
                            error)
                    });
                return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
            }

            processLifetime.AdoptArchiveWorkspace(archiveWorkspace);
            archiveWorkspace = null;
            var sessionHandle = new TiaOpennessV20WorkerSessionHandle(
                    processLifetime,
                    response.SessionId ?? processLifetime.Process.Id.ToString(),
                    string.IsNullOrWhiteSpace(response.ProjectSource) ? projectSource : response.ProjectSource,
                    string.IsNullOrWhiteSpace(response.ProductVersion) ? "20" : response.ProductVersion,
                    string.IsNullOrWhiteSpace(response.ProjectVersion) ? "20" : response.ProjectVersion);
            return TiaOpennessV20EngineeringSessionHostOpenResult.SucceededResult(
                sessionHandle,
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v20-engineering-session-host",
                        "EngineeringSessionHost",
                        workerPath),
                    new TiaEvidence(
                        "tia-openness-v20-engineering-session-public-api",
                        "PublicApiDirectory",
                        publicApiDirectory),
                    new TiaEvidence(
                        "tia-openness-v20-project-source",
                        "ProjectSource",
                        projectSource),
                    new TiaEvidence(
                        "tia-openness-v20-worker-pipe",
                        "WorkerPipe",
                        pipeName)
                });
        }
        catch (OperationCanceledException ex)
        {
            processLifetime?.Terminate();
            archiveWorkspace?.RecordCancellationCleanupFailure(ex);
            throw;
        }
        catch (Exception ex)
        {
            processLifetime?.Terminate();
            var failure = TiaOpennessV20EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V20.EngineeringSessionWorkerStartFailed",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V20 engineering-session worker process could not be started.",
                    "OpenEngineeringSession",
                    "tia-openness-v20-engineering-session-host"),
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v20-engineering-session-worker-error",
                        "EngineeringSessionWorkerError",
                        ex.Message)
                });
            return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
        }
    }

}

internal sealed record TiaOpennessV20EngineeringSessionHostOpenResult(
    TiaOperationStatus Status,
    ITiaOpennessV20EngineeringSessionHandle? Session,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV20EngineeringSessionHostOpenResult SucceededResult(
        ITiaOpennessV20EngineeringSessionHandle session,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV20EngineeringSessionHostOpenResult(
            TiaOperationStatus.Succeeded,
            session ?? throw new ArgumentNullException(nameof(session)),
            Array.Empty<TiaDiagnostic>(),
            evidence);
    }

    public static TiaOpennessV20EngineeringSessionHostOpenResult Failed(
        TiaDiagnostic diagnostic,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV20EngineeringSessionHostOpenResult(
            TiaOperationStatus.Failed,
            null,
            new[] { diagnostic },
            evidence);
    }
}

internal sealed class TiaOpennessV20WorkerSessionException : Exception
{
    public TiaOpennessV20WorkerSessionException(
        string message,
        Exception? innerException = null,
        string diagnosticCode = "TiaOpenness.V20.WorkerSessionFailed")
        : base(message, innerException)
    {
        DiagnosticCode = diagnosticCode;
    }

    public string DiagnosticCode { get; }
}

internal sealed class TiaOpennessV20WorkerCommandCanceledException : OperationCanceledException
{
    public TiaOpennessV20WorkerCommandCanceledException(
        string message,
        Exception innerException,
        CancellationToken cancellationToken)
        : base(message, innerException, cancellationToken)
    {
    }
}

internal sealed record TiaOpennessV20TargetDiscoveryHostResult(
    TiaOperationStatus Status,
    IReadOnlyList<TiaOpennessV20TargetProbe> Targets,
    IReadOnlyList<TiaDiagnostic> Diagnostics)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV20TargetDiscoveryHostResult SucceededResult(
        IReadOnlyList<TiaOpennessV20TargetProbe> targets)
    {
        return new TiaOpennessV20TargetDiscoveryHostResult(
            TiaOperationStatus.Succeeded,
            targets,
            Array.Empty<TiaDiagnostic>());
    }

    public static TiaOpennessV20TargetDiscoveryHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV20TargetDiscoveryHostResult(
            TiaOperationStatus.Failed,
            Array.Empty<TiaOpennessV20TargetProbe>(),
            new[] { diagnostic });
    }
}

internal sealed record TiaOpennessV20BuildHostResult(
    TiaOperationStatus Status,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV20BuildHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV20BuildHostResult(
            TiaOperationStatus.Failed,
            0,
            1,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>());
    }
}

internal sealed record TiaOpennessV20SafetyAuthorizationRequirementHostResult(
    TiaOperationStatus Status,
    TiaSafetyProgramAuthorizationRequirement Requirement,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence);

internal sealed record TiaOpennessV20SafetyAuthorizationHostResult(
    TiaOperationStatus Status,
    bool Authorized,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence);

internal sealed record TiaOpennessV20TransferHostResult(
    TiaOperationStatus Status,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV20TransferHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV20TransferHostResult(
            TiaOperationStatus.Failed,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>());
    }
}

internal sealed record TiaOpennessV20TargetProbe(
    string Id,
    string DisplayName,
    string TechnicalPath,
    string CpuFamily,
    string CpuType,
    string TypeIdentifier,
    string Classification,
    bool HasPlcSoftware)
{
    public TiaProjectTargetRef ToTargetRef()
    {
        return new TiaProjectTargetRef(
            Id,
            DisplayName,
            TechnicalPath,
            CpuFamily,
            CpuType);
    }

    public static TiaOpennessV20TargetProbe From(IReadOnlyDictionary<string, string> values)
    {
        return new TiaOpennessV20TargetProbe(
            TiaOpennessV20WorkerProtocol.ReadRequiredField(values, "id", "Target"),
            TiaOpennessV20WorkerProtocol.ReadRequiredField(values, "displayName", "Target"),
            TiaOpennessV20WorkerProtocol.ReadRequiredField(values, "technicalPath", "Target"),
            TiaOpennessV20WorkerProtocol.ReadRequiredField(values, "cpuFamily", "Target", allowEmpty: true),
            TiaOpennessV20WorkerProtocol.ReadRequiredField(values, "cpuType", "Target", allowEmpty: true),
            TiaOpennessV20WorkerProtocol.ReadRequiredField(values, "typeIdentifier", "Target", allowEmpty: true),
            TiaOpennessV20WorkerProtocol.ReadRequiredField(values, "classification", "Target"),
            TiaOpennessV20WorkerProtocol.ReadRequiredBoolean(values, "hasPlcSoftware", "Target"));
    }
}
