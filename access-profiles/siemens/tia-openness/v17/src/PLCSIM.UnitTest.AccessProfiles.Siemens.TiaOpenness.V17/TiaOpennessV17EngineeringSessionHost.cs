using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17;

internal interface ITiaOpennessV17EngineeringSessionHost
{
    Task<TiaOpennessV17EngineeringSessionHostOpenResult> OpenAsync(
        string publicApiDirectory,
        TiaProjectOpenRequest request,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken);
}

internal interface ITiaOpennessV17EngineeringSessionHandle
{
    string SessionId { get; }

    string ProjectSource { get; }

    string ProductVersion { get; }

    string ProjectVersion { get; }

    Task<TiaOpennessV17TargetDiscoveryHostResult> DiscoverTargetsAsync(CancellationToken cancellationToken);

    Task<TiaOpennessV17SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken);

    Task<TiaOpennessV17SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken);

    Task<TiaOpennessV17BuildHostResult> BuildAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken);

    Task<TiaOpennessV17TransferHostResult> TransferAsync(
        TiaProjectTargetRef projectTarget,
        TiaRuntimeTransferTarget runtimeTarget,
        CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

internal sealed class TiaOpennessV17EngineeringSessionHost : ITiaOpennessV17EngineeringSessionHost
{
    public async Task<TiaOpennessV17EngineeringSessionHostOpenResult> OpenAsync(
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
            return TiaOpennessV17EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V17.EngineeringSessionPublicApiMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V17 engineering-session host requires a PublicAPI directory.",
                    "OpenEngineeringSession",
                    "tia-openness-v17-engineering-session-host"),
                Array.Empty<TiaEvidence>());
        }

        if (string.IsNullOrWhiteSpace(request.ProjectSource))
        {
            return TiaOpennessV17EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V17.EngineeringSessionProjectSourceMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V17 engineering-session host requires a project source.",
                    "OpenEngineeringSession",
                    "tia-openness-v17-engineering-session-host"),
                Array.Empty<TiaEvidence>());
        }

        var projectSource = Path.GetFullPath(request.ProjectSource);
        var workerPath = TiaOpennessV17WorkerProcessHost.ResolveDeployedWorkerPath();
        var pipeName = TiaOpennessV17WorkerProcessHost.CreatePipeName();
        if (!File.Exists(workerPath))
        {
            return TiaOpennessV17EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V17.EngineeringSessionWorkerMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V17 engineering-session worker executable was not found.",
                    "OpenEngineeringSession",
                    "tia-openness-v17-engineering-session-host"),
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v17-engineering-session-host",
                        "EngineeringSessionHost",
                        workerPath)
                });
        }

        var workerInstanceId = Guid.NewGuid().ToString("N");
        TiaOpennessV17ArchiveWorkspace? archiveWorkspace = TiaOpennessV17ArchiveWorkspace.Create(
            request.OpenMode,
            request.ArchiveWorkDirectory,
            workerInstanceId);
        var startInfo = TiaOpennessV17WorkerProcessHost.CreateWorkerStartInfo(
            workerPath,
            pipeName,
            publicApiDirectory,
            projectSource,
            request.OpenMode,
            archiveWorkspace?.OwnedDirectory,
            bindingContext,
            workerInstanceId);

        TiaOpennessV17WorkerProcessLifetime? processLifetime = null;
        try
        {
            var processStart = await new TiaOpennessV17WorkerProcessHost()
                .StartAsync(startInfo, pipeName, cancellationToken)
                .ConfigureAwait(false);
            if (!processStart.Succeeded || processStart.Lifetime is null)
            {
                var failure = TiaOpennessV17EngineeringSessionHostOpenResult.Failed(
                    new TiaDiagnostic(
                        processStart.Code,
                        TiaDiagnosticSeverity.Error,
                        processStart.Message,
                        "OpenEngineeringSession",
                        "tia-openness-v17-engineering-session-host"),
                    processStart.Evidence);
                return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
            }

            processLifetime = processStart.Lifetime;
            var initialResponse = processStart.InitialResponse
                ?? throw new InvalidOperationException(
                    "TIA Openness V17 worker process host succeeded without an initial response.");
            var response = TiaOpennessV17WorkerResponse.Parse(initialResponse);
            if (!response.IsReady)
            {
                string error;
                if (response.Code == "TiaOpenness.V17.EngineeringSessionWorkerInvalidResponse")
                {
                    error = processLifetime.CompleteErrorCapture();
                    processLifetime.Terminate();
                }
                else
                {
                    error = await processLifetime.CompleteFailedStartAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                var failure = TiaOpennessV17EngineeringSessionHostOpenResult.Failed(
                    new TiaDiagnostic(
                        response.Code ?? "TiaOpenness.V17.EngineeringSessionOpenFailed",
                        TiaDiagnosticSeverity.Error,
                        response.Message ?? "TIA Openness V17 engineering-session worker failed to open a session.",
                        "OpenEngineeringSession",
                        "tia-openness-v17-engineering-session-host"),
                    new[]
                    {
                        new TiaEvidence(
                            "tia-openness-v17-engineering-session-worker-error",
                            "EngineeringSessionWorkerError",
                            error)
                    });
                return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
            }

            processLifetime.AdoptArchiveWorkspace(archiveWorkspace);
            archiveWorkspace = null;
            var sessionHandle = new TiaOpennessV17WorkerSessionHandle(
                    processLifetime,
                    response.SessionId ?? processLifetime.Process.Id.ToString(),
                    string.IsNullOrWhiteSpace(response.ProjectSource) ? projectSource : response.ProjectSource,
                    string.IsNullOrWhiteSpace(response.ProductVersion) ? "17" : response.ProductVersion,
                    string.IsNullOrWhiteSpace(response.ProjectVersion) ? "17" : response.ProjectVersion);
            return TiaOpennessV17EngineeringSessionHostOpenResult.SucceededResult(
                sessionHandle,
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v17-engineering-session-host",
                        "EngineeringSessionHost",
                        workerPath),
                    new TiaEvidence(
                        "tia-openness-v17-engineering-session-public-api",
                        "PublicApiDirectory",
                        publicApiDirectory),
                    new TiaEvidence(
                        "tia-openness-v17-project-source",
                        "ProjectSource",
                        projectSource),
                    new TiaEvidence(
                        "tia-openness-v17-worker-pipe",
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
            var failure = TiaOpennessV17EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V17.EngineeringSessionWorkerStartFailed",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V17 engineering-session worker process could not be started.",
                    "OpenEngineeringSession",
                    "tia-openness-v17-engineering-session-host"),
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v17-engineering-session-worker-error",
                        "EngineeringSessionWorkerError",
                        ex.Message)
                });
            return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
        }
    }

}

internal sealed record TiaOpennessV17EngineeringSessionHostOpenResult(
    TiaOperationStatus Status,
    ITiaOpennessV17EngineeringSessionHandle? Session,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV17EngineeringSessionHostOpenResult SucceededResult(
        ITiaOpennessV17EngineeringSessionHandle session,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV17EngineeringSessionHostOpenResult(
            TiaOperationStatus.Succeeded,
            session ?? throw new ArgumentNullException(nameof(session)),
            Array.Empty<TiaDiagnostic>(),
            evidence);
    }

    public static TiaOpennessV17EngineeringSessionHostOpenResult Failed(
        TiaDiagnostic diagnostic,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV17EngineeringSessionHostOpenResult(
            TiaOperationStatus.Failed,
            null,
            new[] { diagnostic },
            evidence);
    }
}

internal sealed class TiaOpennessV17WorkerSessionException : Exception
{
    public TiaOpennessV17WorkerSessionException(
        string message,
        Exception? innerException = null,
        string diagnosticCode = "TiaOpenness.V17.WorkerSessionFailed")
        : base(message, innerException)
    {
        DiagnosticCode = diagnosticCode;
    }

    public string DiagnosticCode { get; }
}

internal sealed class TiaOpennessV17WorkerCommandCanceledException : OperationCanceledException
{
    public TiaOpennessV17WorkerCommandCanceledException(
        string message,
        Exception innerException,
        CancellationToken cancellationToken)
        : base(message, innerException, cancellationToken)
    {
    }
}

internal sealed record TiaOpennessV17TargetDiscoveryHostResult(
    TiaOperationStatus Status,
    IReadOnlyList<TiaOpennessV17TargetProbe> Targets,
    IReadOnlyList<TiaDiagnostic> Diagnostics)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV17TargetDiscoveryHostResult SucceededResult(
        IReadOnlyList<TiaOpennessV17TargetProbe> targets)
    {
        return new TiaOpennessV17TargetDiscoveryHostResult(
            TiaOperationStatus.Succeeded,
            targets,
            Array.Empty<TiaDiagnostic>());
    }

    public static TiaOpennessV17TargetDiscoveryHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV17TargetDiscoveryHostResult(
            TiaOperationStatus.Failed,
            Array.Empty<TiaOpennessV17TargetProbe>(),
            new[] { diagnostic });
    }
}

internal sealed record TiaOpennessV17BuildHostResult(
    TiaOperationStatus Status,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV17BuildHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV17BuildHostResult(
            TiaOperationStatus.Failed,
            0,
            1,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>());
    }
}

internal sealed record TiaOpennessV17SafetyAuthorizationRequirementHostResult(
    TiaOperationStatus Status,
    TiaSafetyProgramAuthorizationRequirement Requirement,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence);

internal sealed record TiaOpennessV17SafetyAuthorizationHostResult(
    TiaOperationStatus Status,
    bool Authorized,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence);

internal sealed record TiaOpennessV17TransferHostResult(
    TiaOperationStatus Status,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV17TransferHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV17TransferHostResult(
            TiaOperationStatus.Failed,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>());
    }
}

internal sealed record TiaOpennessV17TargetProbe(
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

    public static TiaOpennessV17TargetProbe From(IReadOnlyDictionary<string, string> values)
    {
        return new TiaOpennessV17TargetProbe(
            TiaOpennessV17WorkerProtocol.ReadRequiredField(values, "id", "Target"),
            TiaOpennessV17WorkerProtocol.ReadRequiredField(values, "displayName", "Target"),
            TiaOpennessV17WorkerProtocol.ReadRequiredField(values, "technicalPath", "Target"),
            TiaOpennessV17WorkerProtocol.ReadRequiredField(values, "cpuFamily", "Target", allowEmpty: true),
            TiaOpennessV17WorkerProtocol.ReadRequiredField(values, "cpuType", "Target", allowEmpty: true),
            TiaOpennessV17WorkerProtocol.ReadRequiredField(values, "typeIdentifier", "Target", allowEmpty: true),
            TiaOpennessV17WorkerProtocol.ReadRequiredField(values, "classification", "Target"),
            TiaOpennessV17WorkerProtocol.ReadRequiredBoolean(values, "hasPlcSoftware", "Target"));
    }
}
