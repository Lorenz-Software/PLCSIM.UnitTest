using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21;

internal interface ITiaOpennessV21EngineeringSessionHost
{
    Task<TiaOpennessV21EngineeringSessionHostOpenResult> OpenAsync(
        string publicApiDirectory,
        TiaProjectOpenRequest request,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken);
}

internal interface ITiaOpennessV21EngineeringSessionHandle
{
    string SessionId { get; }

    string ProjectSource { get; }

    string ProductVersion { get; }

    string ProjectVersion { get; }

    Task<TiaOpennessV21TargetDiscoveryHostResult> DiscoverTargetsAsync(CancellationToken cancellationToken);

    Task<TiaOpennessV21SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken);

    Task<TiaOpennessV21SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken);

    Task<TiaOpennessV21BuildHostResult> BuildAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken);

    Task<TiaOpennessV21TransferHostResult> TransferAsync(
        TiaProjectTargetRef projectTarget,
        TiaRuntimeTransferTarget runtimeTarget,
        CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

internal sealed class TiaOpennessV21EngineeringSessionHost : ITiaOpennessV21EngineeringSessionHost
{
    public async Task<TiaOpennessV21EngineeringSessionHostOpenResult> OpenAsync(
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
            return TiaOpennessV21EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V21.EngineeringSessionPublicApiMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V21 engineering-session host requires a PublicAPI directory.",
                    "OpenEngineeringSession",
                    "tia-openness-v21-engineering-session-host"),
                Array.Empty<TiaEvidence>());
        }

        if (string.IsNullOrWhiteSpace(request.ProjectSource))
        {
            return TiaOpennessV21EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V21.EngineeringSessionProjectSourceMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V21 engineering-session host requires a project source.",
                    "OpenEngineeringSession",
                    "tia-openness-v21-engineering-session-host"),
                Array.Empty<TiaEvidence>());
        }

        var projectSource = Path.GetFullPath(request.ProjectSource);
        var workerPath = TiaOpennessV21WorkerProcessHost.ResolveDeployedWorkerPath();
        var pipeName = TiaOpennessV21WorkerProcessHost.CreatePipeName();
        if (!File.Exists(workerPath))
        {
            return TiaOpennessV21EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V21.EngineeringSessionWorkerMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V21 engineering-session worker executable was not found.",
                    "OpenEngineeringSession",
                    "tia-openness-v21-engineering-session-host"),
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v21-engineering-session-host",
                        "EngineeringSessionHost",
                        workerPath)
                });
        }

        var workerInstanceId = Guid.NewGuid().ToString("N");
        TiaOpennessV21ArchiveWorkspace? archiveWorkspace = TiaOpennessV21ArchiveWorkspace.Create(
            request.OpenMode,
            request.ArchiveWorkDirectory,
            workerInstanceId);
        var startInfo = TiaOpennessV21WorkerProcessHost.CreateWorkerStartInfo(
            workerPath,
            pipeName,
            publicApiDirectory,
            projectSource,
            request.OpenMode,
            archiveWorkspace?.OwnedDirectory,
            bindingContext,
            workerInstanceId);

        TiaOpennessV21WorkerProcessLifetime? processLifetime = null;
        try
        {
            var processStart = await new TiaOpennessV21WorkerProcessHost()
                .StartAsync(startInfo, pipeName, cancellationToken)
                .ConfigureAwait(false);
            if (!processStart.Succeeded || processStart.Lifetime is null)
            {
                var failure = TiaOpennessV21EngineeringSessionHostOpenResult.Failed(
                    new TiaDiagnostic(
                        processStart.Code,
                        TiaDiagnosticSeverity.Error,
                        processStart.Message,
                        "OpenEngineeringSession",
                        "tia-openness-v21-engineering-session-host"),
                    processStart.Evidence);
                return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
            }

            processLifetime = processStart.Lifetime;
            var initialResponse = processStart.InitialResponse
                ?? throw new InvalidOperationException(
                    "TIA Openness V21 worker process host succeeded without an initial response.");
            var response = TiaOpennessV21WorkerResponse.Parse(initialResponse);
            if (!response.IsReady)
            {
                string error;
                if (response.Code == "TiaOpenness.V21.EngineeringSessionWorkerInvalidResponse")
                {
                    error = processLifetime.CompleteErrorCapture();
                    processLifetime.Terminate();
                }
                else
                {
                    error = await processLifetime.CompleteFailedStartAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                var failure = TiaOpennessV21EngineeringSessionHostOpenResult.Failed(
                    new TiaDiagnostic(
                        response.Code ?? "TiaOpenness.V21.EngineeringSessionOpenFailed",
                        TiaDiagnosticSeverity.Error,
                        response.Message ?? "TIA Openness V21 engineering-session worker failed to open a session.",
                        "OpenEngineeringSession",
                        "tia-openness-v21-engineering-session-host"),
                    new[]
                    {
                        new TiaEvidence(
                            "tia-openness-v21-engineering-session-worker-error",
                            "EngineeringSessionWorkerError",
                            error)
                    });
                return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
            }

            processLifetime.AdoptArchiveWorkspace(archiveWorkspace);
            archiveWorkspace = null;
            var sessionHandle = new TiaOpennessV21WorkerSessionHandle(
                    processLifetime,
                    response.SessionId ?? processLifetime.Process.Id.ToString(),
                    string.IsNullOrWhiteSpace(response.ProjectSource) ? projectSource : response.ProjectSource,
                    string.IsNullOrWhiteSpace(response.ProductVersion) ? "21" : response.ProductVersion,
                    string.IsNullOrWhiteSpace(response.ProjectVersion) ? "21" : response.ProjectVersion);
            return TiaOpennessV21EngineeringSessionHostOpenResult.SucceededResult(
                sessionHandle,
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v21-engineering-session-host",
                        "EngineeringSessionHost",
                        workerPath),
                    new TiaEvidence(
                        "tia-openness-v21-engineering-session-public-api",
                        "PublicApiDirectory",
                        publicApiDirectory),
                    new TiaEvidence(
                        "tia-openness-v21-project-source",
                        "ProjectSource",
                        projectSource),
                    new TiaEvidence(
                        "tia-openness-v21-worker-pipe",
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
            var failure = TiaOpennessV21EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V21.EngineeringSessionWorkerStartFailed",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V21 engineering-session worker process could not be started.",
                    "OpenEngineeringSession",
                    "tia-openness-v21-engineering-session-host"),
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v21-engineering-session-worker-error",
                        "EngineeringSessionWorkerError",
                        ex.Message)
                });
            return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
        }
    }

}

internal sealed record TiaOpennessV21EngineeringSessionHostOpenResult(
    TiaOperationStatus Status,
    ITiaOpennessV21EngineeringSessionHandle? Session,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV21EngineeringSessionHostOpenResult SucceededResult(
        ITiaOpennessV21EngineeringSessionHandle session,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV21EngineeringSessionHostOpenResult(
            TiaOperationStatus.Succeeded,
            session ?? throw new ArgumentNullException(nameof(session)),
            Array.Empty<TiaDiagnostic>(),
            evidence);
    }

    public static TiaOpennessV21EngineeringSessionHostOpenResult Failed(
        TiaDiagnostic diagnostic,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV21EngineeringSessionHostOpenResult(
            TiaOperationStatus.Failed,
            null,
            new[] { diagnostic },
            evidence);
    }
}

internal sealed class TiaOpennessV21WorkerSessionException : Exception
{
    public TiaOpennessV21WorkerSessionException(
        string message,
        Exception? innerException = null,
        string diagnosticCode = "TiaOpenness.V21.WorkerSessionFailed")
        : base(message, innerException)
    {
        DiagnosticCode = diagnosticCode;
    }

    public string DiagnosticCode { get; }
}

internal sealed class TiaOpennessV21WorkerCommandCanceledException : OperationCanceledException
{
    public TiaOpennessV21WorkerCommandCanceledException(
        string message,
        Exception innerException,
        CancellationToken cancellationToken)
        : base(message, innerException, cancellationToken)
    {
    }
}

internal sealed record TiaOpennessV21TargetDiscoveryHostResult(
    TiaOperationStatus Status,
    IReadOnlyList<TiaOpennessV21TargetProbe> Targets,
    IReadOnlyList<TiaDiagnostic> Diagnostics)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV21TargetDiscoveryHostResult SucceededResult(
        IReadOnlyList<TiaOpennessV21TargetProbe> targets)
    {
        return new TiaOpennessV21TargetDiscoveryHostResult(
            TiaOperationStatus.Succeeded,
            targets,
            Array.Empty<TiaDiagnostic>());
    }

    public static TiaOpennessV21TargetDiscoveryHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV21TargetDiscoveryHostResult(
            TiaOperationStatus.Failed,
            Array.Empty<TiaOpennessV21TargetProbe>(),
            new[] { diagnostic });
    }
}

internal sealed record TiaOpennessV21BuildHostResult(
    TiaOperationStatus Status,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV21BuildHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV21BuildHostResult(
            TiaOperationStatus.Failed,
            0,
            1,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>());
    }
}

internal sealed record TiaOpennessV21SafetyAuthorizationRequirementHostResult(
    TiaOperationStatus Status,
    TiaSafetyProgramAuthorizationRequirement Requirement,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence);

internal sealed record TiaOpennessV21SafetyAuthorizationHostResult(
    TiaOperationStatus Status,
    bool Authorized,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence);

internal sealed record TiaOpennessV21TransferHostResult(
    TiaOperationStatus Status,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV21TransferHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV21TransferHostResult(
            TiaOperationStatus.Failed,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>());
    }
}

internal sealed record TiaOpennessV21TargetProbe(
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

    public static TiaOpennessV21TargetProbe From(IReadOnlyDictionary<string, string> values)
    {
        return new TiaOpennessV21TargetProbe(
            TiaOpennessV21WorkerProtocol.ReadRequiredField(values, "id", "Target"),
            TiaOpennessV21WorkerProtocol.ReadRequiredField(values, "displayName", "Target"),
            TiaOpennessV21WorkerProtocol.ReadRequiredField(values, "technicalPath", "Target"),
            TiaOpennessV21WorkerProtocol.ReadRequiredField(values, "cpuFamily", "Target", allowEmpty: true),
            TiaOpennessV21WorkerProtocol.ReadRequiredField(values, "cpuType", "Target", allowEmpty: true),
            TiaOpennessV21WorkerProtocol.ReadRequiredField(values, "typeIdentifier", "Target", allowEmpty: true),
            TiaOpennessV21WorkerProtocol.ReadRequiredField(values, "classification", "Target"),
            TiaOpennessV21WorkerProtocol.ReadRequiredBoolean(values, "hasPlcSoftware", "Target"));
    }
}
