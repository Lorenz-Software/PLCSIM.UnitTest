using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17;

internal sealed class TiaOpennessV17EngineeringSession :
    ITiaEngineeringSession,
    ITiaSafetyProgramAuthorizationSession
{
    private readonly ITiaOpennessV17EngineeringSessionHandle handle;
    private TiaProjectHealthState health = TiaProjectHealthState.Healthy;

    public TiaOpennessV17EngineeringSession(ITiaOpennessV17EngineeringSessionHandle handle)
    {
        this.handle = handle ?? throw new ArgumentNullException(nameof(handle));
        Project = new TiaProjectSessionInfo(
            handle.SessionId,
            handle.ProjectSource,
            handle.ProductVersion,
            handle.ProjectVersion);
    }

    public TiaProjectSessionInfo Project { get; }

    public TiaProjectHealthState Health => health;

    public event EventHandler<TiaProjectHealthChangedEventArgs>? HealthChanged;

    public event EventHandler<TiaProgressSnapshot>? ProgressChanged;

    public async Task<TiaProjectTargetDiscoveryResult> DiscoverTargetsAsync(
        TiaProjectTargetDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TryCreateUnavailableTargetDiscoveryResult("DiscoverTargets", out var unavailable))
        {
            return unavailable;
        }

        ProgressChanged?.Invoke(
            this,
            new TiaProgressSnapshot(
                "DiscoverTargets",
                "Running",
                "TIA Openness V17 target discovery started.",
                null));

        TiaOpennessV17TargetDiscoveryHostResult result;
        try
        {
            result = await handle.DiscoverTargetsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetHealth(TiaProjectHealthState.Invalidated, "TIA Openness V17 target discovery failed because the worker session became unreliable.");
            return new TiaProjectTargetDiscoveryResult(
                TiaOperationStatus.Invalidated,
                Array.Empty<TiaProjectTargetRef>(),
                null,
                new[] { CreateWorkerFailureDiagnostic("DiscoverTargets", ex) },
                CreateSessionEvidence());
        }

        if (!result.Succeeded)
        {
            return new TiaProjectTargetDiscoveryResult(
                result.Status,
                Array.Empty<TiaProjectTargetRef>(),
                null,
                result.Diagnostics,
                CreateSessionEvidence());
        }

        var supportedTargets = result.Targets
            .Where(IsSupportedTarget)
            .Select(target => target.ToTargetRef())
            .ToArray();
        var unsupportedTargets = result.Targets
            .Where(target => !IsSupportedTarget(target))
            .ToArray();
        var diagnostics = new List<TiaDiagnostic>();
        if (unsupportedTargets.Length > 0)
        {
            diagnostics.Add(new TiaDiagnostic(
                "TiaOpenness.V17.TargetUnsupported",
                TiaDiagnosticSeverity.Warning,
                "TIA Openness V17 target discovery ignored unsupported non-S7-1500 or non-PLC targets.",
                "DiscoverTargets",
                "tia-openness-v17-target-discovery"));
        }

        var selected = ResolveSelectedTarget(request.PlcRefId, supportedTargets, diagnostics);
        var status = diagnostics.Any(diagnostic => diagnostic.Severity == TiaDiagnosticSeverity.Error)
            ? TiaOperationStatus.Failed
            : TiaOperationStatus.Succeeded;

        ProgressChanged?.Invoke(
            this,
            new TiaProgressSnapshot(
                "DiscoverTargets",
                "Completed",
                "TIA Openness V17 target discovery completed.",
                100));

        return new TiaProjectTargetDiscoveryResult(
            status,
            supportedTargets,
            selected,
            diagnostics,
            CreateTargetDiscoveryEvidence(result.Targets, supportedTargets));
    }

    public async Task<TiaBuildResult> BuildAsync(
        TiaBuildRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TryCreateUnavailableBuildResult("Build", out var unavailable))
        {
            return unavailable;
        }

        ProgressChanged?.Invoke(
            this,
            new TiaProgressSnapshot(
                "Build",
                "Running",
                "TIA Openness V17 build started.",
                null));

        TiaOpennessV17BuildHostResult result;
        try
        {
            result = await handle.BuildAsync(request.Target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetHealth(TiaProjectHealthState.Invalidated, "TIA Openness V17 build failed because the worker session became unreliable.");
            return new TiaBuildResult(
                TiaOperationStatus.Invalidated,
                0,
                1,
                new[] { CreateWorkerFailureDiagnostic("Build", ex) },
                CreateSessionEvidence());
        }

        ProgressChanged?.Invoke(
            this,
            new TiaProgressSnapshot(
                "Build",
                result.Succeeded ? "Completed" : "Failed",
                result.Succeeded
                    ? "TIA Openness V17 build completed."
                    : "TIA Openness V17 build failed.",
                100));

        return new TiaBuildResult(
            result.Status,
            result.WarningCount,
            result.ErrorCount,
            result.Diagnostics,
            CreateSessionEvidence().Concat(result.Evidence).ToArray());
    }

    public async Task<TiaSafetyProgramAuthorizationRequirementResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CreateUnavailableDiagnostic("DiscoverSafetyAuthorizationRequirement") is { } unavailable)
        {
            return new TiaSafetyProgramAuthorizationRequirementResult(
                TiaOperationStatus.Invalidated,
                TiaSafetyProgramAuthorizationRequirement.NotRequired,
                new[] { unavailable },
                Array.Empty<TiaEvidence>());
        }

        try
        {
            var result = await handle
                .GetSafetyProgramAuthorizationRequirementAsync(target, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status == TiaOperationStatus.Invalidated)
            {
                SetHealth(
                    TiaProjectHealthState.Invalidated,
                    "TIA Openness V17 Safety authorization requirement discovery invalidated the engineering session.");
            }

            return new TiaSafetyProgramAuthorizationRequirementResult(
                result.Status,
                result.Requirement,
                result.Diagnostics,
                CreateSessionEvidence().Concat(result.Evidence).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetHealth(
                TiaProjectHealthState.Invalidated,
                "TIA Openness V17 Safety authorization requirement discovery failed because the worker session became unreliable.");
            return new TiaSafetyProgramAuthorizationRequirementResult(
                TiaOperationStatus.Invalidated,
                TiaSafetyProgramAuthorizationRequirement.NotRequired,
                new[] { CreateWorkerFailureDiagnostic("DiscoverSafetyAuthorizationRequirement", ex) },
                CreateSessionEvidence());
        }
    }

    public async Task<TiaSafetyProgramAuthorizationResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(password);
        if (CreateUnavailableDiagnostic("AuthorizeSafetyProgram") is { } unavailable)
        {
            return new TiaSafetyProgramAuthorizationResult(
                TiaOperationStatus.Invalidated,
                false,
                new[] { unavailable },
                Array.Empty<TiaEvidence>());
        }

        try
        {
            var result = await handle
                .AuthorizeSafetyProgramAsync(target, password, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status == TiaOperationStatus.Invalidated)
            {
                SetHealth(
                    TiaProjectHealthState.Invalidated,
                    "TIA Openness V17 Safety authorization invalidated the engineering session.");
            }

            return new TiaSafetyProgramAuthorizationResult(
                result.Status,
                result.Authorized,
                result.Diagnostics,
                CreateSessionEvidence().Concat(result.Evidence).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetHealth(
                TiaProjectHealthState.Invalidated,
                "TIA Openness V17 Safety authorization failed because the worker session became unreliable.");
            return new TiaSafetyProgramAuthorizationResult(
                TiaOperationStatus.Invalidated,
                false,
                new[] { CreateWorkerFailureDiagnostic("AuthorizeSafetyProgram", ex) },
                CreateSessionEvidence());
        }
    }

    public async Task<TiaTransferResult> TransferAsync(
        TiaTransferRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TryCreateUnavailableTransferResult("Transfer", out var unavailable))
        {
            return unavailable;
        }

        var routeDiagnostic = ValidateTransferRoute(request.RuntimeTarget);
        if (routeDiagnostic is not null)
        {
            return new TiaTransferResult(
                TiaOperationStatus.Failed,
                new[] { routeDiagnostic },
                CreateSessionEvidence());
        }

        ProgressChanged?.Invoke(
            this,
            new TiaProgressSnapshot(
                "Transfer",
                "Running",
                "TIA Openness V17 transfer started.",
                null));

        TiaOpennessV17TransferHostResult result;
        try
        {
            result = await handle.TransferAsync(
                    request.ProjectTarget,
                    request.RuntimeTarget,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetHealth(TiaProjectHealthState.Invalidated, "TIA Openness V17 transfer failed because the worker session became unreliable.");
            return new TiaTransferResult(
                TiaOperationStatus.Invalidated,
                new[] { CreateWorkerFailureDiagnostic("Transfer", ex) },
                CreateSessionEvidence());
        }

        ProgressChanged?.Invoke(
            this,
            new TiaProgressSnapshot(
                "Transfer",
                result.Succeeded ? "Completed" : "Failed",
                result.Succeeded
                    ? "TIA Openness V17 transfer completed."
                    : "TIA Openness V17 transfer failed.",
                100));

        return new TiaTransferResult(
            result.Status,
            result.Diagnostics,
            CreateSessionEvidence().Concat(result.Evidence).ToArray());
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (health == TiaProjectHealthState.Disposed)
        {
            return;
        }

        var previous = health;
        try
        {
            await handle.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            health = TiaProjectHealthState.Disposed;
            try
            {
                HealthChanged?.Invoke(
                    this,
                    new TiaProjectHealthChangedEventArgs(
                        previous,
                        health,
                        "TIA Openness V17 engineering session was closed."));
            }
            finally
            {
                HealthChanged = null;
                ProgressChanged = null;
            }
        }
    }

    private bool TryCreateUnavailableTargetDiscoveryResult(
        string phase,
        out TiaProjectTargetDiscoveryResult result)
    {
        var diagnostic = CreateUnavailableDiagnostic(phase);
        if (diagnostic is null)
        {
            result = null!;
            return false;
        }

        result = new TiaProjectTargetDiscoveryResult(
            TiaOperationStatus.Invalidated,
            Array.Empty<TiaProjectTargetRef>(),
            null,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>());
        return true;
    }

    private bool TryCreateUnavailableBuildResult(
        string phase,
        out TiaBuildResult result)
    {
        var diagnostic = CreateUnavailableDiagnostic(phase);
        if (diagnostic is null)
        {
            result = null!;
            return false;
        }

        result = new TiaBuildResult(
            TiaOperationStatus.Invalidated,
            0,
            1,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>());
        return true;
    }

    private bool TryCreateUnavailableTransferResult(
        string phase,
        out TiaTransferResult result)
    {
        var diagnostic = CreateUnavailableDiagnostic(phase);
        if (diagnostic is null)
        {
            result = null!;
            return false;
        }

        result = new TiaTransferResult(
            TiaOperationStatus.Invalidated,
            new[] { diagnostic },
            Array.Empty<TiaEvidence>());
        return true;
    }

    private TiaDiagnostic? CreateUnavailableDiagnostic(string phase)
    {
        return health switch
        {
            TiaProjectHealthState.Healthy => null,
            TiaProjectHealthState.Disposed => new TiaDiagnostic(
                "TiaOpenness.V17.SessionDisposed",
                TiaDiagnosticSeverity.Error,
                "TIA Openness V17 engineering session is already closed.",
                phase,
                "tia-openness-v17-engineering-session"),
            _ => new TiaDiagnostic(
                "TiaOpenness.V17.SessionInvalidated",
                TiaDiagnosticSeverity.Error,
                "TIA Openness V17 engineering session is invalidated and must be reopened.",
                phase,
                "tia-openness-v17-engineering-session")
        };
    }

    private TiaDiagnostic CreateWorkerFailureDiagnostic(string phase, Exception ex)
    {
        var code = ex is TiaOpennessV17WorkerSessionException workerSessionException
            ? workerSessionException.DiagnosticCode
            : "TiaOpenness.V17.WorkerSessionFailed";
        return new TiaDiagnostic(
            code,
            TiaDiagnosticSeverity.Error,
            $"TIA Openness V17 worker session failed during {phase}: {ex.GetBaseException().Message}",
            phase,
            "tia-openness-v17-engineering-session");
    }

    private void SetHealth(TiaProjectHealthState next, string message)
    {
        if (health == next)
        {
            return;
        }

        var previous = health;
        health = next;
        HealthChanged?.Invoke(
            this,
            new TiaProjectHealthChangedEventArgs(previous, health, message));
    }

    private static bool IsSupportedTarget(TiaOpennessV17TargetProbe target)
    {
        return target.HasPlcSoftware
            && string.Equals(target.CpuFamily, "S7-1500", StringComparison.OrdinalIgnoreCase);
    }

    private static TiaDiagnostic? ValidateTransferRoute(TiaRuntimeTransferTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Id)
            || !string.Equals(target.RuntimeKind, SiemensBindingKinds.PlcSimAdvanced, StringComparison.Ordinal)
            || target.Locality != SiemensRuntimeLocality.Local
            || !Enum.IsDefined(target.ConnectionMode)
            || !Enum.IsDefined(target.PcInterface)
            || target.PcInterfaceNumber is <= 0)
        {
            return new TiaDiagnostic(
                "TiaOpenness.V17.TransferRouteInvalid",
                TiaDiagnosticSeverity.Error,
                "TIA Openness V17 transfer requires a valid local PLCSIM Advanced runtime route.",
                "Transfer",
                "tia-openness-v17-transfer-route");
        }

        return null;
    }

    private static TiaProjectTargetRef? ResolveSelectedTarget(
        string? plcRefId,
        IReadOnlyList<TiaProjectTargetRef> targets,
        List<TiaDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(plcRefId))
        {
            if (targets.Count == 1)
            {
                return targets[0];
            }

            if (targets.Count == 0)
            {
                diagnostics.Add(new TiaDiagnostic(
                    "TiaOpenness.V17.TargetNotFound",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V17 target discovery found no supported S7-1500 PLC target.",
                    "DiscoverTargets",
                    "tia-openness-v17-target-discovery"));
            }
            else
            {
                diagnostics.Add(new TiaDiagnostic(
                    "TiaOpenness.V17.TargetAmbiguous",
                    TiaDiagnosticSeverity.Error,
                    "TIA Openness V17 target discovery found multiple supported PLC targets and no plcRef.id was configured.",
                    "DiscoverTargets",
                    "tia-openness-v17-target-discovery"));
            }

            return null;
        }

        var matches = targets
            .Where(target => string.Equals(target.Id, plcRefId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 1)
        {
            return matches[0];
        }

        diagnostics.Add(new TiaDiagnostic(
            matches.Length == 0 ? "TiaOpenness.V17.TargetNotFound" : "TiaOpenness.V17.TargetAmbiguous",
            TiaDiagnosticSeverity.Error,
            matches.Length == 0
                ? $"Configured plcRef.id '{plcRefId}' does not match a supported TIA Openness V17 PLC target."
                : $"Configured plcRef.id '{plcRefId}' matches multiple supported TIA Openness V17 PLC targets.",
            "DiscoverTargets",
            "tia-openness-v17-target-discovery"));
        return null;
    }

    private TiaEvidence[] CreateSessionEvidence()
    {
        return new[]
        {
            new TiaEvidence(
                "tia-openness-v17-engineering-session",
                "EngineeringSession",
                Project.SessionId),
            new TiaEvidence(
                "tia-openness-v17-project-source",
                "ProjectSource",
                Project.ProjectSource)
        };
    }

    private TiaEvidence[] CreateTargetDiscoveryEvidence(
        IReadOnlyList<TiaOpennessV17TargetProbe> discoveredTargets,
        IReadOnlyList<TiaProjectTargetRef> supportedTargets)
    {
        return CreateSessionEvidence()
            .Concat(new[]
            {
                new TiaEvidence(
                    "tia-openness-v17-target-discovery",
                    "TargetDiscovery",
                    $"Discovered={discoveredTargets.Count}; Supported={supportedTargets.Count}")
            })
            .ToArray();
    }
}
