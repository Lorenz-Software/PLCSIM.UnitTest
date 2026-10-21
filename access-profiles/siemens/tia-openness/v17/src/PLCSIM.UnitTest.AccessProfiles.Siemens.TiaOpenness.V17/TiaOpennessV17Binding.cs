using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17;

public sealed class TiaOpennessV17Binding :
    ITiaOpennessEngineeringBinding,
    ITiaOpennessAuthorizationProbeBinding
{
    private readonly ITiaOpennessV17EnvironmentProbe environmentProbe;
    private readonly ITiaOpennessV17EngineeringSessionHost engineeringSessionHost;
    private readonly ITiaOpennessV17AuthorizationProbeHost authorizationProbeHost;
    private SiemensBindingContext? context;

    public TiaOpennessV17Binding()
        : this(
            new TiaOpennessV17EnvironmentProbe(),
            new TiaOpennessV17EngineeringSessionHost(),
            new TiaOpennessV17AuthorizationProbeHost())
    {
    }

    internal TiaOpennessV17Binding(
        ITiaOpennessV17EnvironmentProbe environmentProbe,
        ITiaOpennessV17EngineeringSessionHost? engineeringSessionHost = null,
        ITiaOpennessV17AuthorizationProbeHost? authorizationProbeHost = null)
    {
        this.environmentProbe = environmentProbe ?? throw new ArgumentNullException(nameof(environmentProbe));
        this.engineeringSessionHost = engineeringSessionHost ?? new TiaOpennessV17EngineeringSessionHost();
        this.authorizationProbeHost = authorizationProbeHost ?? new TiaOpennessV17AuthorizationProbeHost();
    }

    public void Initialize(SiemensBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context ??= context;
    }

    public SiemensBindingInfo Info { get; } = new(
        SiemensBindingKinds.TiaOpenness,
        "2.0.0",
        "TIA Openness V17 Binding");

    public SiemensBindingDeclaration Declaration { get; } = new(
        new[]
        {
            new SiemensAccessApiDeclaration(
                SiemensBindingKinds.TiaOpenness,
                "17",
                "17")
        },
        new[]
        {
            new SiemensProjectSourceDeclaration(
                SiemensProjectSourceKinds.TiaProject,
                ".ap17",
                "17",
                TiaProjectOpenMode.DirectProject),
            new SiemensProjectSourceDeclaration(
                SiemensProjectSourceKinds.TiaProject,
                ".zap17",
                "17",
                TiaProjectOpenMode.RetrievedArchive)
        },
        Array.Empty<SiemensRuntimeDeclaration>(),
        Array.Empty<string>(),
        Array.Empty<string>());

    public SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
    {
        try
        {
            var probe = environmentProbe.ProbeAsync(CancellationToken.None).GetAwaiter().GetResult();
            return probe.State switch
            {
                TiaOpennessV17EnvironmentProbeState.Ready => SiemensBindingReadinessResult.Usable(CreateEvidenceSummary(probe)),
                TiaOpennessV17EnvironmentProbeState.Unavailable => SiemensBindingReadinessResult.Unavailable(CreateEvidenceSummary(probe)),
                TiaOpennessV17EnvironmentProbeState.Failed => CreateReadinessError(probe),
                _ => SiemensBindingReadinessResult.Error(
                    "TiaOpenness.V17.ReadinessUnknown",
                    "TIA Openness V17 readiness probe returned an unsupported state.")
            };
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException
            or InvalidOperationException)
        {
            return SiemensBindingReadinessResult.Error(
                "TiaOpenness.V17.ReadinessProbeFailed",
                "TIA Openness V17 readiness probe failed.",
                ex.Message);
        }
    }

    public async Task<TiaAuthorizationProbeResult> ProbeAuthorizationAsync(
        TiaAuthorizationProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var probe = await environmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!probe.IsReady)
        {
            var status = probe.State == TiaOpennessV17EnvironmentProbeState.Unavailable
                ? TiaAuthorizationProbeStatus.Unavailable
                : TiaAuthorizationProbeStatus.Failed;
            return new TiaAuthorizationProbeResult(status, probe.Diagnostics, probe.Evidence);
        }

        var result = await authorizationProbeHost.ProbeAsync(
            probe.PublicApiDirectory!,
            RequireContext(),
            cancellationToken).ConfigureAwait(false);
        return new TiaAuthorizationProbeResult(
            result.Status,
            result.Diagnostics,
            probe.Evidence.Concat(result.Evidence).ToArray());
    }

    public Task<TiaEngineeringSessionOpenResult> OpenProjectAsync(
        TiaProjectOpenRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.ProjectSource))
        {
            return Task.FromResult(new TiaEngineeringSessionOpenResult(
                TiaOperationStatus.Failed,
                null,
                new[]
                {
                    new TiaDiagnostic(
                        "TiaOpenness.ProjectSourceMissing",
                        TiaDiagnosticSeverity.Error,
                        "Project source is required.",
                        "OpenProject",
                    null)
                },
                Array.Empty<TiaEvidence>()));
        }

        if (request.OpenMode == TiaProjectOpenMode.AttachExisting)
        {
            return Task.FromResult(new TiaEngineeringSessionOpenResult(
                TiaOperationStatus.Failed,
                null,
                new[]
                {
                    new TiaDiagnostic(
                        "TiaOpenness.V17.OpenModeUnsupported",
                        TiaDiagnosticSeverity.Error,
                        "The TIA Openness V17 binding does not support attaching to an existing TIA Portal session.",
                        "OpenProject",
                        null)
                },
                Array.Empty<TiaEvidence>()));
        }

        var expectedExtension = request.OpenMode switch
        {
            TiaProjectOpenMode.DirectProject => ".ap17",
            TiaProjectOpenMode.RetrievedArchive => ".zap17",
            _ => null
        };
        if (expectedExtension is null
            || !string.Equals(Path.GetExtension(request.ProjectSource), expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new TiaEngineeringSessionOpenResult(
                TiaOperationStatus.Failed,
                null,
                new[]
                {
                    new TiaDiagnostic(
                        "TiaOpenness.V17.ProjectSourceUnsupported",
                        TiaDiagnosticSeverity.Error,
                        $"The TIA Openness V17 binding requires the {expectedExtension ?? "supported"} extension for open mode {request.OpenMode}.",
                        "OpenProject",
                        null)
                },
                Array.Empty<TiaEvidence>()));
        }

        if (!File.Exists(request.ProjectSource))
        {
            return Task.FromResult(new TiaEngineeringSessionOpenResult(
                TiaOperationStatus.Failed,
                null,
                new[]
                {
                    new TiaDiagnostic(
                        "TiaOpenness.V17.ProjectSourceNotFound",
                        TiaDiagnosticSeverity.Error,
                        "TIA Portal V17 project source file was not found.",
                        "OpenProject",
                        "tia-openness-v17-project-source")
                },
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v17-project-source",
                        "ProjectSource",
                        request.ProjectSource)
                }));
        }

        return OpenProjectAfterValidationAsync(request, cancellationToken);
    }

    private async Task<TiaEngineeringSessionOpenResult> OpenProjectAfterValidationAsync(
        TiaProjectOpenRequest request,
        CancellationToken cancellationToken)
    {
        var probe = await environmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!probe.IsReady)
        {
            return new TiaEngineeringSessionOpenResult(
                TiaOperationStatus.Failed,
                null,
                probe.Diagnostics,
                probe.Evidence);
        }

        var sessionOpen = await engineeringSessionHost.OpenAsync(
            probe.PublicApiDirectory!,
            request,
            RequireContext(),
            cancellationToken).ConfigureAwait(false);
        if (!sessionOpen.Succeeded || sessionOpen.Session is null)
        {
            return new TiaEngineeringSessionOpenResult(
                TiaOperationStatus.Failed,
                null,
                sessionOpen.Diagnostics,
                probe.Evidence.Concat(sessionOpen.Evidence).ToArray());
        }

        var evidence = probe.Evidence.Concat(sessionOpen.Evidence).Concat(new[]
        {
            new TiaEvidence(
                "tia-openness-v17-engineering-session",
                "EngineeringSession",
                sessionOpen.Session.SessionId)
        }).ToArray();

        return new TiaEngineeringSessionOpenResult(
            TiaOperationStatus.Succeeded,
            new TiaOpennessV17EngineeringSession(sessionOpen.Session),
            Array.Empty<TiaDiagnostic>(),
            evidence);
    }

    private static SiemensBindingReadinessResult CreateReadinessError(TiaOpennessV17EnvironmentProbeResult probe)
    {
        var diagnostic = probe.Diagnostics.FirstOrDefault();
        var technicalDetail = diagnostic?.EvidenceId is null
            ? null
            : probe.Evidence.FirstOrDefault(evidence => evidence.Id == diagnostic.EvidenceId)?.Summary;
        return SiemensBindingReadinessResult.Error(
            diagnostic?.Code ?? "TiaOpenness.V17.ReadinessProbeFailed",
            diagnostic?.Message ?? "TIA Openness V17 readiness probe failed.",
            technicalDetail ?? CreateEvidenceSummary(probe));
    }

    private SiemensBindingContext RequireContext()
    {
        return context ?? throw new InvalidOperationException("The TIA Openness V17 binding must be initialized before opening a project.");
    }

    private static string CreateEvidenceSummary(TiaOpennessV17EnvironmentProbeResult probe)
    {
        var publicApi = probe.Evidence.FirstOrDefault(evidence => evidence.Kind == "PublicApiDirectory");
        if (publicApi is not null)
        {
            return $"TIA Openness V17 PublicAPI evidence is usable at '{publicApi.Summary}'.";
        }

        var diagnostic = probe.Diagnostics.FirstOrDefault();
        if (diagnostic is not null)
        {
            return diagnostic.Message;
        }

        return "TIA Openness V17 readiness evidence was resolved by the binding.";
    }

}
