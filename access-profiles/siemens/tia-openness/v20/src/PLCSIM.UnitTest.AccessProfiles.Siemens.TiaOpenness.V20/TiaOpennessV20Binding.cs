using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20;

public sealed class TiaOpennessV20Binding :
    ITiaOpennessEngineeringBinding,
    ITiaOpennessAuthorizationProbeBinding
{
    private readonly ITiaOpennessV20EnvironmentProbe environmentProbe;
    private readonly ITiaOpennessV20EngineeringSessionHost engineeringSessionHost;
    private readonly ITiaOpennessV20AuthorizationProbeHost authorizationProbeHost;
    private SiemensBindingContext? context;

    public TiaOpennessV20Binding()
        : this(
            new TiaOpennessV20EnvironmentProbe(),
            new TiaOpennessV20EngineeringSessionHost(),
            new TiaOpennessV20AuthorizationProbeHost())
    {
    }

    internal TiaOpennessV20Binding(
        ITiaOpennessV20EnvironmentProbe environmentProbe,
        ITiaOpennessV20EngineeringSessionHost? engineeringSessionHost = null,
        ITiaOpennessV20AuthorizationProbeHost? authorizationProbeHost = null)
    {
        this.environmentProbe = environmentProbe ?? throw new ArgumentNullException(nameof(environmentProbe));
        this.engineeringSessionHost = engineeringSessionHost ?? new TiaOpennessV20EngineeringSessionHost();
        this.authorizationProbeHost = authorizationProbeHost ?? new TiaOpennessV20AuthorizationProbeHost();
    }

    public void Initialize(SiemensBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context ??= context;
    }

    public SiemensBindingInfo Info { get; } = new(
        SiemensBindingKinds.TiaOpenness,
        "2.0.0",
        "TIA Openness V20 Binding");

    public SiemensBindingDeclaration Declaration { get; } = new(
        new[]
        {
            new SiemensAccessApiDeclaration(
                SiemensBindingKinds.TiaOpenness,
                "20",
                "20")
        },
        new[]
        {
            new SiemensProjectSourceDeclaration(
                SiemensProjectSourceKinds.TiaProject,
                ".ap20",
                "20",
                TiaProjectOpenMode.DirectProject),
            new SiemensProjectSourceDeclaration(
                SiemensProjectSourceKinds.TiaProject,
                ".zap20",
                "20",
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
                TiaOpennessV20EnvironmentProbeState.Ready => SiemensBindingReadinessResult.Usable(CreateEvidenceSummary(probe)),
                TiaOpennessV20EnvironmentProbeState.Unavailable => SiemensBindingReadinessResult.Unavailable(CreateEvidenceSummary(probe)),
                TiaOpennessV20EnvironmentProbeState.Failed => CreateReadinessError(probe),
                _ => SiemensBindingReadinessResult.Error(
                    "TiaOpenness.V20.ReadinessUnknown",
                    "TIA Openness V20 readiness probe returned an unsupported state.")
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
                "TiaOpenness.V20.ReadinessProbeFailed",
                "TIA Openness V20 readiness probe failed.",
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
            var status = probe.State == TiaOpennessV20EnvironmentProbeState.Unavailable
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
                        "TiaOpenness.V20.OpenModeUnsupported",
                        TiaDiagnosticSeverity.Error,
                        "The TIA Openness V20 binding does not support attaching to an existing TIA Portal session.",
                        "OpenProject",
                        null)
                },
                Array.Empty<TiaEvidence>()));
        }

        var expectedExtension = request.OpenMode switch
        {
            TiaProjectOpenMode.DirectProject => ".ap20",
            TiaProjectOpenMode.RetrievedArchive => ".zap20",
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
                        "TiaOpenness.V20.ProjectSourceUnsupported",
                        TiaDiagnosticSeverity.Error,
                        $"The TIA Openness V20 binding requires the {expectedExtension ?? "supported"} extension for open mode {request.OpenMode}.",
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
                        "TiaOpenness.V20.ProjectSourceNotFound",
                        TiaDiagnosticSeverity.Error,
                        "TIA Portal V20 project source file was not found.",
                        "OpenProject",
                        "tia-openness-v20-project-source")
                },
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v20-project-source",
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
                "tia-openness-v20-engineering-session",
                "EngineeringSession",
                sessionOpen.Session.SessionId)
        }).ToArray();

        return new TiaEngineeringSessionOpenResult(
            TiaOperationStatus.Succeeded,
            new TiaOpennessV20EngineeringSession(sessionOpen.Session),
            Array.Empty<TiaDiagnostic>(),
            evidence);
    }

    private static SiemensBindingReadinessResult CreateReadinessError(TiaOpennessV20EnvironmentProbeResult probe)
    {
        var diagnostic = probe.Diagnostics.FirstOrDefault();
        var technicalDetail = diagnostic?.EvidenceId is null
            ? null
            : probe.Evidence.FirstOrDefault(evidence => evidence.Id == diagnostic.EvidenceId)?.Summary;
        return SiemensBindingReadinessResult.Error(
            diagnostic?.Code ?? "TiaOpenness.V20.ReadinessProbeFailed",
            diagnostic?.Message ?? "TIA Openness V20 readiness probe failed.",
            technicalDetail ?? CreateEvidenceSummary(probe));
    }

    private SiemensBindingContext RequireContext()
    {
        return context ?? throw new InvalidOperationException("The TIA Openness V20 binding must be initialized before opening a project.");
    }

    private static string CreateEvidenceSummary(TiaOpennessV20EnvironmentProbeResult probe)
    {
        var publicApi = probe.Evidence.FirstOrDefault(evidence => evidence.Kind == "PublicApiDirectory");
        if (publicApi is not null)
        {
            return $"TIA Openness V20 PublicAPI evidence is usable at '{publicApi.Summary}'.";
        }

        var diagnostic = probe.Diagnostics.FirstOrDefault();
        if (diagnostic is not null)
        {
            return diagnostic.Message;
        }

        return "TIA Openness V20 readiness evidence was resolved by the binding.";
    }

}
