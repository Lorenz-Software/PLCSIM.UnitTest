using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.UnitTests;
using System.Collections.Concurrent;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7;

public sealed class PlcsimAdvancedV7Binding : IPlcsimAdvancedRuntimeBinding, IPlcsimUnitTestCommunicationBinding
{
    private readonly IPlcsimAdvancedV7EnvironmentProbe environmentProbe;
    private readonly IPlcsimAdvancedV7WorkerSessionHost workerSessionHost;
    private readonly ConcurrentDictionary<string, IPlcsimAdvancedV7WorkerSessionHandle> workerSessions = new(StringComparer.Ordinal);
    private SiemensBindingContext? context;

    public PlcsimAdvancedV7Binding()
        : this(new PlcsimAdvancedV7EnvironmentProbe(), new PlcsimAdvancedV7WorkerSessionHost())
    {
    }

    internal PlcsimAdvancedV7Binding(
        IPlcsimAdvancedV7EnvironmentProbe environmentProbe,
        IPlcsimAdvancedV7WorkerSessionHost? workerSessionHost = null)
    {
        this.environmentProbe = environmentProbe ?? throw new ArgumentNullException(nameof(environmentProbe));
        this.workerSessionHost = workerSessionHost ?? new PlcsimAdvancedV7WorkerSessionHost();
    }

    public void Initialize(SiemensBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context ??= context;
    }

    public SiemensBindingInfo Info { get; } = new(
        SiemensBindingKinds.PlcSimAdvanced,
        "2.0.1",
        "PLCSIM Advanced V7 Binding");

    public SiemensBindingDeclaration Declaration { get; } = new(
        new[]
        {
            new SiemensAccessApiDeclaration(
                SiemensBindingKinds.PlcSimAdvanced,
                "7.0",
                "7.0")
        },
        Array.Empty<SiemensProjectSourceDeclaration>(),
        new[]
        {
            new SiemensRuntimeDeclaration(
                SiemensBindingKinds.PlcSimAdvanced,
                "7.0",
                SiemensRuntimeLocality.Local,
                new[] { SiemensProjectSourceKinds.TiaProject })
        },
        new[] { UnitTestContractVersions.PlcsimAdvancedCommunication },
        new[] { UnitTestContractVersions.EventStreamModel });

    public SiemensBindingReadinessResult ResolveReadiness(SiemensBindingReadinessRequest request)
    {
        try
        {
            var probe = environmentProbe.ProbeAsync(CancellationToken.None).GetAwaiter().GetResult();
            return probe.State switch
            {
                PlcsimAdvancedV7EnvironmentProbeState.Ready => SiemensBindingReadinessResult.Usable(CreateEvidenceSummary(probe)),
                PlcsimAdvancedV7EnvironmentProbeState.Unavailable => SiemensBindingReadinessResult.Unavailable(CreateEvidenceSummary(probe)),
                PlcsimAdvancedV7EnvironmentProbeState.Failed => CreateReadinessError(probe),
                _ => SiemensBindingReadinessResult.Error(
                    "PlcsimAdvanced.V7.ReadinessUnknown",
                    "PLCSIM Advanced V7 readiness probe returned an unsupported state.")
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
                "PlcsimAdvanced.V7.ReadinessProbeFailed",
                "PLCSIM Advanced V7 readiness probe failed.",
                ex.Message);
        }
    }

    public async Task<PlcsimRuntimeSessionOpenResult> OpenRuntimeSessionAsync(
        PlcsimRuntimeSessionOpenRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var probe = await environmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!probe.IsReady)
        {
            return new PlcsimRuntimeSessionOpenResult(
                probe.State == PlcsimAdvancedV7EnvironmentProbeState.Unavailable
                    ? PlcsimOperationStatus.Unavailable
                    : PlcsimOperationStatus.Failed,
                null,
                probe.Diagnostics,
                probe.Evidence);
        }

        var runtimeApiDirectory = probe.Evidence
            .FirstOrDefault(evidence => string.Equals(evidence.Kind, "RuntimeApiDirectory", StringComparison.OrdinalIgnoreCase))
            ?.Summary;
        if (string.IsNullOrWhiteSpace(runtimeApiDirectory))
        {
            return new PlcsimRuntimeSessionOpenResult(
                PlcsimOperationStatus.Failed,
                null,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V7.RuntimeApiDirectoryMissing",
                        PlcsimDiagnosticSeverity.Error,
                        "PLCSIM Advanced V7 readiness probe did not provide a Runtime API directory.",
                        "OpenRuntimeSession",
                        "plcsim-advanced-v7-runtime-api-directory")
                },
                probe.Evidence);
        }

        var open = await workerSessionHost.OpenAsync(
            runtimeApiDirectory,
            RequireContext(),
            cancellationToken).ConfigureAwait(false);
        if (!open.Succeeded || open.Session is null)
        {
            return new PlcsimRuntimeSessionOpenResult(
                open.Status,
                null,
                open.Diagnostics,
                open.Evidence);
        }

        return new PlcsimRuntimeSessionOpenResult(
            PlcsimOperationStatus.Succeeded,
            CreateRuntimeSession(open.Session, request.RuntimeReference),
            Array.Empty<PlcsimDiagnostic>(),
            open.Evidence.Concat(probe.Evidence).ToArray());
    }

    public async Task<PlcsimCommunicationPrepareResult> PrepareCommunicationAsync(
        PlcsimCommunicationPrepareRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.RuntimeSessionId)
            || !workerSessions.TryGetValue(request.RuntimeSessionId, out var workerSession))
        {
            return new PlcsimCommunicationPrepareResult(
                PlcsimOperationStatus.Unavailable,
                null,
                new[]
                {
                    new PlcsimDiagnostic(
                        "PlcsimAdvanced.V7.RuntimeSessionNotFound",
                        PlcsimDiagnosticSeverity.Error,
                        "The requested PLCSIM Advanced V7 runtime session is not available for UnitTest communication preparation.",
                        "PrepareCommunication",
                        "plcsim-advanced-v7-communication-prepare")
                },
                Array.Empty<PlcsimEvidence>());
        }

        var result = await workerSession.PrepareCommunicationAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Session is null)
        {
            return MapUnsupportedPlcCycleObservation(request, result);
        }

        return result;
    }

    private static PlcsimCommunicationPrepareResult MapUnsupportedPlcCycleObservation(
        PlcsimCommunicationPrepareRequest request,
        PlcsimCommunicationPrepareResult result)
    {
        if (request.WorkerOptions.ObservationStrategy != PlcsimCommunicationObservationStrategyKind.PlcCycle
            || !result.Diagnostics.Any(diagnostic => string.Equals(
                diagnostic.Code,
                "PlcsimAdvanced.V7.Worker.PlcCycleObservationUnavailable",
                StringComparison.Ordinal)))
        {
            return result;
        }

        var diagnostics = result.Diagnostics
            .Select(diagnostic => string.Equals(
                diagnostic.Code,
                "PlcsimAdvanced.V7.Worker.PlcCycleObservationUnavailable",
                StringComparison.Ordinal)
                    ? new PlcsimDiagnostic(
                        "PlcsimAdvanced.V7.PlcCycleObservationNotSupported",
                        diagnostic.Severity,
                        "PLC-cycle observation is not supported by the current PLCSIM Advanced V7 binding. Use polling instead.",
                        diagnostic.Phase,
                        diagnostic.EvidenceId)
                    : diagnostic)
            .ToArray();

        return new PlcsimCommunicationPrepareResult(
            result.Status,
            result.Session,
            diagnostics,
            result.Evidence);
    }

    private PlcsimAdvancedV7RuntimeSession CreateRuntimeSession(
        IPlcsimAdvancedV7WorkerSessionHandle workerSession,
        PlcsimRuntimeReference? runtimeReference)
    {
        workerSessions[workerSession.SessionId] = workerSession;
        return new PlcsimAdvancedV7RuntimeSession(
            workerSession,
            runtimeReference,
            DateTimeOffset.UtcNow,
            () => UnregisterWorkerSession(workerSession));
    }

    private void UnregisterWorkerSession(IPlcsimAdvancedV7WorkerSessionHandle workerSession)
    {
        if (string.IsNullOrWhiteSpace(workerSession.SessionId))
        {
            return;
        }

        ((ICollection<KeyValuePair<string, IPlcsimAdvancedV7WorkerSessionHandle>>)workerSessions)
            .Remove(new KeyValuePair<string, IPlcsimAdvancedV7WorkerSessionHandle>(
                workerSession.SessionId,
                workerSession));
    }

    private SiemensBindingContext RequireContext()
    {
        return context ?? throw new InvalidOperationException(
            "The PLCSIM Advanced V7 binding must be initialized before opening a runtime session.");
    }

    private static SiemensBindingReadinessResult CreateReadinessError(PlcsimAdvancedV7EnvironmentProbeResult probe)
    {
        var diagnostic = probe.Diagnostics.FirstOrDefault();
        return SiemensBindingReadinessResult.Error(
            diagnostic?.Code ?? "PlcsimAdvanced.V7.ReadinessProbeFailed",
            diagnostic?.Message ?? "PLCSIM Advanced V7 readiness probe failed.",
            diagnostic?.EvidenceId ?? CreateEvidenceSummary(probe));
    }

    private static string CreateEvidenceSummary(PlcsimAdvancedV7EnvironmentProbeResult probe)
    {
        var runtimeApi = probe.Evidence.FirstOrDefault(evidence => evidence.Kind == "RuntimeApiDll");
        if (runtimeApi is not null)
        {
            return $"PLCSIM Advanced V7 Runtime API evidence is usable: {runtimeApi.Summary}.";
        }

        var diagnostic = probe.Diagnostics.FirstOrDefault();
        if (diagnostic is not null)
        {
            return diagnostic.Message;
        }

        return "PLCSIM Advanced V7 readiness evidence was resolved by the binding.";
    }
}
