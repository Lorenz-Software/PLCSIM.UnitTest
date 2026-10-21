using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18;

internal interface ITiaOpennessV18EngineeringSessionHost
{
    Task<TiaOpennessV18EngineeringSessionHostOpenResult> OpenAsync(
        string publicApiDirectory,
        TiaProjectOpenRequest request,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken);
}

internal interface ITiaOpennessV18EngineeringSessionHandle
{
    string SessionId { get; }

    string ProjectSource { get; }

    string ProductVersion { get; }

    string ProjectVersion { get; }

    Task<TiaOpennessV18TargetDiscoveryHostResult> DiscoverTargetsAsync(CancellationToken cancellationToken);

    Task<TiaOpennessV18SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken);

    Task<TiaOpennessV18SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken);

    Task<TiaOpennessV18BuildHostResult> BuildAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken);

    Task<TiaOpennessV18TransferHostResult> TransferAsync(
        TiaProjectTargetRef projectTarget,
        TiaRuntimeTransferTarget runtimeTarget,
        CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

internal sealed class TiaOpennessV18EngineeringSessionHost : ITiaOpennessV18EngineeringSessionHost
{
    public async Task<TiaOpennessV18EngineeringSessionHostOpenResult> OpenAsync(
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
            return TiaOpennessV18EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V18.EngineeringSessionPublicApiMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V18 engineering-session host requires a PublicAPI directory.",
                    "OpenEngineeringSession",
                    "tia-openness-v18-engineering-session-host"),
                Array.Empty<TiaEvidence>());
        }

        if (string.IsNullOrWhiteSpace(request.ProjectSource))
        {
            return TiaOpennessV18EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V18.EngineeringSessionProjectSourceMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V18 engineering-session host requires a project source.",
                    "OpenEngineeringSession",
                    "tia-openness-v18-engineering-session-host"),
                Array.Empty<TiaEvidence>());
        }

        var projectSource = Path.GetFullPath(request.ProjectSource);
        var workerPath = TiaOpennessV18WorkerProcessHost.ResolveDeployedWorkerPath();
        var pipeName = TiaOpennessV18WorkerProcessHost.CreatePipeName();
        if (!File.Exists(workerPath))
        {
            return TiaOpennessV18EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V18.EngineeringSessionWorkerMissing",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V18 engineering-session worker executable was not found.",
                    "OpenEngineeringSession",
                    "tia-openness-v18-engineering-session-host"),
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v18-engineering-session-host",
                        "EngineeringSessionHost",
                        workerPath)
                });
        }

        var workerInstanceId = Guid.NewGuid().ToString("N");
        TiaOpennessV18ArchiveWorkspace? archiveWorkspace = TiaOpennessV18ArchiveWorkspace.Create(
            request.OpenMode,
            request.ArchiveWorkDirectory,
            workerInstanceId);
        var startInfo = TiaOpennessV18WorkerProcessHost.CreateWorkerStartInfo(
            workerPath,
            pipeName,
            publicApiDirectory,
            projectSource,
            request.OpenMode,
            archiveWorkspace?.OwnedDirectory,
            bindingContext,
            workerInstanceId);

        TiaOpennessV18WorkerProcessLifetime? processLifetime = null;
        try
        {
            var processStart = await new TiaOpennessV18WorkerProcessHost()
                .StartAsync(startInfo, pipeName, cancellationToken)
                .ConfigureAwait(false);
            if (!processStart.Succeeded || processStart.Lifetime is null)
            {
                var failure = TiaOpennessV18EngineeringSessionHostOpenResult.Failed(
                    new TiaDiagnostic(
                        processStart.Code,
                        TiaDiagnosticSeverity.Error,
                        processStart.Message,
                        "OpenEngineeringSession",
                        "tia-openness-v18-engineering-session-host"),
                    processStart.Evidence);
                return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
            }

            processLifetime = processStart.Lifetime;
            var initialResponse = processStart.InitialResponse
                ?? throw new InvalidOperationException(
                    "TIA Openness V18 worker process host succeeded without an initial response.");
            var response = TiaOpennessV18WorkerResponse.Parse(initialResponse);
            if (!response.IsReady)
            {
                string error;
                if (response.Code == "TiaOpenness.V18.EngineeringSessionWorkerInvalidResponse")
                {
                    error = processLifetime.CompleteErrorCapture();
                    processLifetime.Terminate();
                }
                else
                {
                    error = await processLifetime.CompleteFailedStartAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                var failure = TiaOpennessV18EngineeringSessionHostOpenResult.Failed(
                    new TiaDiagnostic(
                        response.Code ?? "TiaOpenness.V18.EngineeringSessionOpenFailed",
                        TiaDiagnosticSeverity.Error,
                        response.Message ?? "TIA Openness V18 engineering-session worker failed to open a session.",
                        "OpenEngineeringSession",
                        "tia-openness-v18-engineering-session-host"),
                    new[]
                    {
                        new TiaEvidence(
                            "tia-openness-v18-engineering-session-worker-error",
                            "EngineeringSessionWorkerError",
                            error)
                    });
                return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
            }

            processLifetime.AdoptArchiveWorkspace(archiveWorkspace);
            archiveWorkspace = null;
            var sessionHandle = new TiaOpennessV18WorkerSessionHandle(
                    processLifetime,
                    response.SessionId ?? processLifetime.Process.Id.ToString(),
                    string.IsNullOrWhiteSpace(response.ProjectSource) ? projectSource : response.ProjectSource,
                    string.IsNullOrWhiteSpace(response.ProductVersion) ? "18" : response.ProductVersion,
                    string.IsNullOrWhiteSpace(response.ProjectVersion) ? "18" : response.ProjectVersion);
            return TiaOpennessV18EngineeringSessionHostOpenResult.SucceededResult(
                sessionHandle,
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v18-engineering-session-host",
                        "EngineeringSessionHost",
                        workerPath),
                    new TiaEvidence(
                        "tia-openness-v18-engineering-session-public-api",
                        "PublicApiDirectory",
                        publicApiDirectory),
                    new TiaEvidence(
                        "tia-openness-v18-project-source",
                        "ProjectSource",
                        projectSource),
                    new TiaEvidence(
                        "tia-openness-v18-worker-pipe",
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
            var failure = TiaOpennessV18EngineeringSessionHostOpenResult.Failed(
                new TiaDiagnostic(
                    "TiaOpenness.V18.EngineeringSessionWorkerStartFailed",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V18 engineering-session worker process could not be started.",
                    "OpenEngineeringSession",
                    "tia-openness-v18-engineering-session-host"),
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v18-engineering-session-worker-error",
                        "EngineeringSessionWorkerError",
                        ex.Message)
                });
            return archiveWorkspace?.CompleteFailedOpen(failure) ?? failure;
        }
    }

}

internal sealed record TiaOpennessV18EngineeringSessionHostOpenResult(
    TiaOperationStatus Status,
    ITiaOpennessV18EngineeringSessionHandle? Session,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV18EngineeringSessionHostOpenResult SucceededResult(
        ITiaOpennessV18EngineeringSessionHandle session,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV18EngineeringSessionHostOpenResult(
            TiaOperationStatus.Succeeded,
            session ?? throw new ArgumentNullException(nameof(session)),
            Array.Empty<TiaDiagnostic>(),
            evidence);
    }

    public static TiaOpennessV18EngineeringSessionHostOpenResult Failed(
        TiaDiagnostic diagnostic,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV18EngineeringSessionHostOpenResult(
            TiaOperationStatus.Failed,
            null,
            new[] { diagnostic },
            evidence);
    }
}

internal sealed class TiaOpennessV18WorkerSessionException : Exception
{
    public TiaOpennessV18WorkerSessionException(
        string message,
        Exception? innerException = null,
        string diagnosticCode = "TiaOpenness.V18.WorkerSessionFailed")
        : base(message, innerException)
    {
        DiagnosticCode = diagnosticCode;
    }

    public string DiagnosticCode { get; }
}

internal sealed class TiaOpennessV18WorkerCommandCanceledException : OperationCanceledException
{
    public TiaOpennessV18WorkerCommandCanceledException(
        string message,
        Exception innerException,
        CancellationToken cancellationToken)
        : base(message, innerException, cancellationToken)
    {
    }
}

internal sealed record TiaOpennessV18TargetDiscoveryHostResult(
    TiaOperationStatus Status,
    IReadOnlyList<TiaOpennessV18TargetProbe> Targets,
    IReadOnlyList<TiaDiagnostic> Diagnostics)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV18TargetDiscoveryHostResult SucceededResult(
        IReadOnlyList<TiaOpennessV18TargetProbe> targets)
    {
        return new TiaOpennessV18TargetDiscoveryHostResult(
            TiaOperationStatus.Succeeded,
            targets,
            Array.Empty<TiaDiagnostic>());
    }

    public static TiaOpennessV18TargetDiscoveryHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV18TargetDiscoveryHostResult(
            TiaOperationStatus.Failed,
            Array.Empty<TiaOpennessV18TargetProbe>(),
            new[] { diagnostic });
    }
}

internal sealed record TiaOpennessV18BuildHostResult(
    TiaOperationStatus Status,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV18BuildHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV18BuildHostResult(
            TiaOperationStatus.Failed,
            0,
            1,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>());
    }
}

internal sealed record TiaOpennessV18SafetyAuthorizationRequirementHostResult(
    TiaOperationStatus Status,
    TiaSafetyProgramAuthorizationRequirement Requirement,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence);

internal sealed record TiaOpennessV18SafetyAuthorizationHostResult(
    TiaOperationStatus Status,
    bool Authorized,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence);

internal sealed record TiaOpennessV18TransferHostResult(
    TiaOperationStatus Status,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public bool Succeeded => Status is TiaOperationStatus.Succeeded or TiaOperationStatus.SucceededWithWarnings;

    public static TiaOpennessV18TransferHostResult Failed(TiaDiagnostic diagnostic)
    {
        return new TiaOpennessV18TransferHostResult(
            TiaOperationStatus.Failed,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>());
    }
}

internal sealed record TiaOpennessV18TargetProbe(
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

    public static TiaOpennessV18TargetProbe From(IReadOnlyDictionary<string, string> values)
    {
        return new TiaOpennessV18TargetProbe(
            TiaOpennessV18WorkerProtocol.ReadRequiredField(values, "id", "Target"),
            TiaOpennessV18WorkerProtocol.ReadRequiredField(values, "displayName", "Target"),
            TiaOpennessV18WorkerProtocol.ReadRequiredField(values, "technicalPath", "Target"),
            TiaOpennessV18WorkerProtocol.ReadRequiredField(values, "cpuFamily", "Target", allowEmpty: true),
            TiaOpennessV18WorkerProtocol.ReadRequiredField(values, "cpuType", "Target", allowEmpty: true),
            TiaOpennessV18WorkerProtocol.ReadRequiredField(values, "typeIdentifier", "Target", allowEmpty: true),
            TiaOpennessV18WorkerProtocol.ReadRequiredField(values, "classification", "Target"),
            TiaOpennessV18WorkerProtocol.ReadRequiredBoolean(values, "hasPlcSoftware", "Target"));
    }
}
