using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1;

internal interface ITiaOpennessV15_1EngineeringSessionHost
{
    Task<TiaOpennessV15_1EngineeringSessionHostOpenResult> OpenAsync(
        string publicApiDirectory,
        TiaProjectOpenRequest request,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken);
}

internal interface ITiaOpennessV15_1EngineeringSessionHandle
{
    string SessionId { get; }

    string ProjectSource { get; }

    string ProductVersion { get; }

    string ProjectVersion { get; }

    Task<TiaOpennessV15_1TargetDiscoveryHostResult> DiscoverTargetsAsync(CancellationToken cancellationToken);

    Task<TiaOpennessV15_1SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken);

    Task<TiaOpennessV15_1SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken);

    Task<TiaOpennessV15_1BuildHostResult> BuildAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken);

    Task<TiaOpennessV15_1TransferHostResult> TransferAsync(
        TiaProjectTargetRef projectTarget,
        TiaRuntimeTransferTarget runtimeTarget,
        CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

internal sealed class TiaOpennessV15_1EngineeringSessionHost : ITiaOpennessV15_1EngineeringSessionHost
{
    public async Task<TiaOpennessV15_1EngineeringSessionHostOpenResult> OpenAsync(
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
            return TiaOpennessV15_1EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V15_1.EngineeringSessionPublicApiMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V15.1 engineering-session host requires a PublicAPI directory.",
                    "OpenEngineeringSession",
                    "tia-openness-v15_1-engineering-session-host"),
                Array.Empty<TiaEvidence>());
        }

        if (string.IsNullOrWhiteSpace(request.ProjectSource))
        {
            return TiaOpennessV15_1EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V15_1.EngineeringSessionProjectSourceMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V15.1 engineering-session host requires a project source.",
                    "OpenEngineeringSession",
                    "tia-openness-v15_1-engineering-session-host"),
                Array.Empty<TiaEvidence>());
        }

        var projectSource = Path.GetFullPath(request.ProjectSource);
        var workerPath = TiaOpennessV15_1WorkerProcessHost.ResolveDeployedWorkerPath();
        var pipeName = TiaOpennessV15_1WorkerProcessHost.CreatePipeName();
        if (!File.Exists(workerPath))
        {
            return TiaOpennessV15_1EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V15_1.EngineeringSessionWorkerMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V15.1 engineering-session worker executable was not found.",
                    "OpenEngineeringSession",
                    "tia-openness-v15_1-engineering-session-host"),
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v15_1-engineering-session-host",
                        "EngineeringSessionHost",
                        workerPath)
                });
        }

        var workerInstanceId = Guid.NewGuid().ToString("N");
        TiaOpennessV15_1ArchiveWorkspace? archiveWorkspace = TiaOpennessV15_1ArchiveWorkspace.Create(
            request.OpenMode,
            request.ArchiveWorkDirectory,
            workerInstanceId);
        var startInfo = TiaOpennessV15_1WorkerProcessHost.CreateWorkerStartInfo(
            workerPath,
            pipeName,
            publicApiDirectory,
            projectSource,
            request.OpenMode,
            archiveWorkspace?.OwnedDirectory,
            bindingContext,
            workerInstanceId);

        TiaOpennessV15_1WorkerProcessLifetime? processLifetime = null;
        try
        {
            var processStart = await new TiaOpennessV15_1WorkerProcessHost()
                .StartAsync(startInfo, pipeName, cancellationToken)
                .ConfigureAwait(false);
            if (!processStart.Succeeded || processStart.Lifetime is null)
            {
                var failure = TiaOpennessV15_1EngineeringSessionHostOpenResult.Failed(
                    new TiaDiagnostic(
                        processStart.Code,
                        TiaDiagnosticSeverity.Error,
                        processStart.Message,
                        "OpenEngineeringSession",
                        "tia-openness-v15_1-engineering-session-host"),
                    processStart.Evidence);
                return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
            }

            processLifetime = processStart.Lifetime;
            var initialResponse = processStart.InitialResponse
                ?? throw new InvalidOperationException(
                    "TIA Openness V15.1 worker process host succeeded without an initial response.");
            var response = TiaOpennessV15_1WorkerResponse.Parse(initialResponse);
            if (!response.IsReady)
            {
                string error;
                if (response.Code == "TiaOpenness.V15_1.EngineeringSessionWorkerInvalidResponse")
                {
                    error = processLifetime.CompleteErrorCapture();
                    processLifetime.Terminate();
                }
                else
                {
                    error = await processLifetime.CompleteFailedStartAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                var failure = TiaOpennessV15_1EngineeringSessionHostOpenResult.Failed(
                    new TiaDiagnostic(
                        response.Code ?? "TiaOpenness.V15_1.EngineeringSessionOpenFailed",
                        TiaDiagnosticSeverity.Error,
                        response.Message ?? "TIA Openness V15.1 engineering-session worker failed to open a session.",
                        "OpenEngineeringSession",
                        "tia-openness-v15_1-engineering-session-host"),
                    new[]
                    {
                        new TiaEvidence(
                            "tia-openness-v15_1-engineering-session-worker-error",
                            "EngineeringSessionWorkerError",
                            error)
                    });
                return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
            }

            processLifetime.AdoptArchiveWorkspace(archiveWorkspace);
            archiveWorkspace = null;
            var sessionHandle = new TiaOpennessV15_1WorkerSessionHandle(
                    processLifetime,
                    response.SessionId ?? processLifetime.Process.Id.ToString(),
                    string.IsNullOrWhiteSpace(response.ProjectSource) ? projectSource : response.ProjectSource,
                    string.IsNullOrWhiteSpace(response.ProductVersion) ? "15.1" : response.ProductVersion,
                    string.IsNullOrWhiteSpace(response.ProjectVersion) ? "15.1" : response.ProjectVersion);
            return TiaOpennessV15_1EngineeringSessionHostOpenResult.SucceededResult(
                sessionHandle,
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v15_1-engineering-session-host",
                        "EngineeringSessionHost",
                        workerPath),
                    new TiaEvidence(
                        "tia-openness-v15_1-engineering-session-public-api",
                        "PublicApiDirectory",
                        publicApiDirectory),
                    new TiaEvidence(
                        "tia-openness-v15_1-project-source",
                        "ProjectSource",
                        projectSource),
                    new TiaEvidence(
                        "tia-openness-v15_1-worker-pipe",
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
            var failure = TiaOpennessV15_1EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V15_1.EngineeringSessionWorkerStartFailed",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V15.1 engineering-session worker process could not be started.",
                    "OpenEngineeringSession",
                    "tia-openness-v15_1-engineering-session-host"),
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v15_1-engineering-session-worker-error",
                        "EngineeringSessionWorkerError",
                        ex.Message)
                });
            return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
        }
    }

}

internal sealed record TiaOpennessV15_1EngineeringSessionHostOpenResult(
    TiaOperationStatus Status,
    ITiaOpennessV15_1EngineeringSessionHandle? Session,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV15_1EngineeringSessionHostOpenResult SucceededResult(
        ITiaOpennessV15_1EngineeringSessionHandle session,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV15_1EngineeringSessionHostOpenResult(
            TiaOperationStatus.Succeeded,
            session ?? throw new ArgumentNullException(nameof(session)),
            Array.Empty<TiaDiagnostic>(),
            evidence);
    }

    public static TiaOpennessV15_1EngineeringSessionHostOpenResult Failed(
        TiaDiagnostic diagnostic,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV15_1EngineeringSessionHostOpenResult(
            TiaOperationStatus.Failed,
            null,
            new[] { diagnostic },
            evidence);
    }
}

internal sealed class TiaOpennessV15_1WorkerSessionException : Exception
{
    public TiaOpennessV15_1WorkerSessionException(
        string message,
        Exception? innerException = null,
        string diagnosticCode = "TiaOpenness.V15_1.WorkerSessionFailed")
        : base(message, innerException)
    {
        DiagnosticCode = diagnosticCode;
    }

    public string DiagnosticCode { get; }
}

internal sealed class TiaOpennessV15_1WorkerCommandCanceledException : OperationCanceledException
{
    public TiaOpennessV15_1WorkerCommandCanceledException(
        string message,
        Exception innerException,
        CancellationToken cancellationToken)
        : base(message, innerException, cancellationToken)
    {
    }
}

internal sealed record TiaOpennessV15_1TargetDiscoveryHostResult(
    TiaOperationStatus Status,
    IReadOnlyList<TiaOpennessV15_1TargetProbe> Targets,
    IReadOnlyList<TiaDiagnostic> Diagnostics)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV15_1TargetDiscoveryHostResult SucceededResult(
        IReadOnlyList<TiaOpennessV15_1TargetProbe> targets)
    {
        return new TiaOpennessV15_1TargetDiscoveryHostResult(
            TiaOperationStatus.Succeeded,
            targets,
            Array.Empty<TiaDiagnostic>());
    }

    public static TiaOpennessV15_1TargetDiscoveryHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV15_1TargetDiscoveryHostResult(
            TiaOperationStatus.Failed,
            Array.Empty<TiaOpennessV15_1TargetProbe>(),
            new[] { diagnostic });
    }
}

internal sealed record TiaOpennessV15_1BuildHostResult(
    TiaOperationStatus Status,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV15_1BuildHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV15_1BuildHostResult(
            TiaOperationStatus.Failed,
            0,
            1,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>());
    }
}

internal sealed record TiaOpennessV15_1SafetyAuthorizationRequirementHostResult(
    TiaOperationStatus Status,
    TiaSafetyProgramAuthorizationRequirement Requirement,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence);

internal sealed record TiaOpennessV15_1SafetyAuthorizationHostResult(
    TiaOperationStatus Status,
    bool Authorized,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence);

internal sealed record TiaOpennessV15_1TransferHostResult(
    TiaOperationStatus Status,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV15_1TransferHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV15_1TransferHostResult(
            TiaOperationStatus.Failed,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>());
    }
}

internal sealed record TiaOpennessV15_1TargetProbe(
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

    public static TiaOpennessV15_1TargetProbe From(IReadOnlyDictionary<string, string> values)
    {
        return new TiaOpennessV15_1TargetProbe(
            TiaOpennessV15_1WorkerProtocol.ReadRequiredField(values, "id", "Target"),
            TiaOpennessV15_1WorkerProtocol.ReadRequiredField(values, "displayName", "Target"),
            TiaOpennessV15_1WorkerProtocol.ReadRequiredField(values, "technicalPath", "Target"),
            TiaOpennessV15_1WorkerProtocol.ReadRequiredField(values, "cpuFamily", "Target", allowEmpty: true),
            TiaOpennessV15_1WorkerProtocol.ReadRequiredField(values, "cpuType", "Target", allowEmpty: true),
            TiaOpennessV15_1WorkerProtocol.ReadRequiredField(values, "typeIdentifier", "Target", allowEmpty: true),
            TiaOpennessV15_1WorkerProtocol.ReadRequiredField(values, "classification", "Target"),
            TiaOpennessV15_1WorkerProtocol.ReadRequiredBoolean(values, "hasPlcSoftware", "Target"));
    }
}
