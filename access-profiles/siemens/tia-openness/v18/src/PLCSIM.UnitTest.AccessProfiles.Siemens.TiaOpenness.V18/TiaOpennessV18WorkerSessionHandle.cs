using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18;

internal sealed class TiaOpennessV18WorkerSessionHandle : ITiaOpennessV18EngineeringSessionHandle
{
    private readonly TiaOpennessV18WorkerCommandDispatcher dispatcher;

    public TiaOpennessV18WorkerSessionHandle(
        TiaOpennessV18WorkerProcessLifetime processLifetime,
        string sessionId,
        string projectSource,
        string productVersion,
        string projectVersion)
    {
        ArgumentNullException.ThrowIfNull(processLifetime);
        dispatcher = new TiaOpennessV18WorkerCommandDispatcher(processLifetime);
        SessionId = string.IsNullOrWhiteSpace(sessionId)
            ? processLifetime.Process.Id.ToString()
            : sessionId;
        ProjectSource = projectSource;
        ProductVersion = productVersion;
        ProjectVersion = projectVersion;
    }

    public string SessionId { get; }

    public string ProjectSource { get; }

    public string ProductVersion { get; }

    public string ProjectVersion { get; }

    public Task<TiaOpennessV18TargetDiscoveryHostResult> DiscoverTargetsAsync(
        CancellationToken cancellationToken)
    {
        var targets = new List<TiaOpennessV18TargetProbe>();
        return dispatcher.DispatchAsync(
            "target discovery",
            TiaOpennessV18WorkerProtocol.CreateRequest("ProbeTargets"),
            message =>
            {
                if (string.Equals(message.Type, "EndTargets", StringComparison.OrdinalIgnoreCase))
                {
                    return TiaOpennessV18WorkerCommandStep<TiaOpennessV18TargetDiscoveryHostResult>.Complete(
                        TiaOpennessV18TargetDiscoveryHostResult.SucceededResult(targets));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV18WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "code",
                        "Error");
                    var workerMessage = TiaOpennessV18WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "message",
                        "Error");
                    return TiaOpennessV18WorkerCommandStep<TiaOpennessV18TargetDiscoveryHostResult>.Complete(
                        TiaOpennessV18TargetDiscoveryHostResult.Failed(
                            new TiaDiagnostic(
                                code,
                                TiaDiagnosticSeverity.Error,
                                workerMessage,
                                "DiscoverTargets",
                                "tia-openness-v18-worker")));
                }

                if (!string.Equals(message.Type, "Target", StringComparison.OrdinalIgnoreCase))
                {
                    throw dispatcher.CreateTransportException(
                        "TIA Openness V18 worker returned an unexpected target discovery response.");
                }

                targets.Add(TiaOpennessV18TargetProbe.From(message.Values));
                return TiaOpennessV18WorkerCommandStep<TiaOpennessV18TargetDiscoveryHostResult>.Continue();
            },
            cancellationToken);
    }

    public Task<TiaOpennessV18BuildHostResult> BuildAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<TiaDiagnostic>();
        var evidence = new List<TiaEvidence>();

        return dispatcher.DispatchAsync(
            "build",
            TiaOpennessV18WorkerProtocol.CreateRequest(
                "BuildTarget",
                new KeyValuePair<string, string>("targetId", target.Id)),
            message =>
            {
                if (string.Equals(message.Type, "BuildDiagnostic", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(CreateBuildDiagnostic(message.Values));
                    return TiaOpennessV18WorkerCommandStep<TiaOpennessV18BuildHostResult>.Continue();
                }

                if (string.Equals(message.Type, "BuildEvidence", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(CreateBuildEvidence(message.Values));
                    return TiaOpennessV18WorkerCommandStep<TiaOpennessV18BuildHostResult>.Continue();
                }

                if (string.Equals(message.Type, "BuildResult", StringComparison.OrdinalIgnoreCase))
                {
                    var status = TiaOpennessV18WorkerProtocol.ReadRequiredOperationStatus(
                        message.Values,
                        "status",
                        "BuildResult");
                    var warningCount = TiaOpennessV18WorkerProtocol.ReadRequiredNonNegativeInt(
                        message.Values,
                        "warningCount",
                        "BuildResult");
                    var errorCount = TiaOpennessV18WorkerProtocol.ReadRequiredNonNegativeInt(
                        message.Values,
                        "errorCount",
                        "BuildResult");
                    return TiaOpennessV18WorkerCommandStep<TiaOpennessV18BuildHostResult>.Complete(
                        new TiaOpennessV18BuildHostResult(
                            status,
                            warningCount,
                            errorCount,
                            diagnostics,
                            evidence));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV18WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "code",
                        "Error");
                    var workerMessage = TiaOpennessV18WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "message",
                        "Error");
                    return TiaOpennessV18WorkerCommandStep<TiaOpennessV18BuildHostResult>.Complete(
                        new TiaOpennessV18BuildHostResult(
                            MapWorkerOperationStatus(code),
                            0,
                            1,
                            new[]
                            {
                                new TiaDiagnostic(
                                    code,
                                    TiaDiagnosticSeverity.Error,
                                    workerMessage,
                                    "Build",
                                    "tia-openness-v18-worker")
                            },
                            evidence));
                }

                throw dispatcher.CreateTransportException(
                    "TIA Openness V18 worker returned an unexpected build response.");
            },
            cancellationToken);
    }

    public Task<TiaOpennessV18SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        return dispatcher.DispatchAsync(
            "Safety-program authorization requirement discovery",
            TiaOpennessV18WorkerProtocol.CreateRequest(
                "ProbeSafetyAuthorization",
                new KeyValuePair<string, string>("targetId", target.Id)),
            message =>
            {
                if (string.Equals(message.Type, "SafetyAuthorizationRequirementResult", StringComparison.OrdinalIgnoreCase))
                {
                    var status = TiaOpennessV18WorkerProtocol.ReadRequiredOperationStatus(
                        message.Values,
                        "status",
                        "SafetyAuthorizationRequirementResult");
                    var requirementText = TiaOpennessV18WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "requirement",
                        "SafetyAuthorizationRequirementResult");
                    if (!Enum.TryParse<TiaSafetyProgramAuthorizationRequirement>(requirementText, true, out var requirement)
                        || !Enum.IsDefined(requirement))
                    {
                        throw dispatcher.CreateTransportException(
                            "TIA Openness V18 worker returned an invalid Safety authorization requirement.");
                    }

                    return TiaOpennessV18WorkerCommandStep<TiaOpennessV18SafetyAuthorizationRequirementHostResult>.Complete(
                        new TiaOpennessV18SafetyAuthorizationRequirementHostResult(
                            status,
                            requirement,
                            Array.Empty<TiaDiagnostic>(),
                            Array.Empty<TiaEvidence>()));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV18WorkerProtocol.ReadRequiredField(message.Values, "code", "Error");
                    var workerMessage = TiaOpennessV18WorkerProtocol.ReadRequiredField(message.Values, "message", "Error");
                    return TiaOpennessV18WorkerCommandStep<TiaOpennessV18SafetyAuthorizationRequirementHostResult>.Complete(
                        new TiaOpennessV18SafetyAuthorizationRequirementHostResult(
                            MapSafetyOperationStatus(code),
                            TiaSafetyProgramAuthorizationRequirement.NotRequired,
                            new[] { CreateSafetyDiagnostic(code, workerMessage, "DiscoverSafetyAuthorizationRequirement") },
                            Array.Empty<TiaEvidence>()));
                }

                throw dispatcher.CreateTransportException(
                    "TIA Openness V18 worker returned an unexpected Safety authorization requirement response.");
            },
            cancellationToken);
    }

    public async Task<TiaOpennessV18SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        var requirement = await GetSafetyProgramAuthorizationRequirementAsync(target, cancellationToken)
            .ConfigureAwait(false);
        return new TiaOpennessV18SafetyAuthorizationHostResult(
            requirement.Status,
            requirement.Status == TiaOperationStatus.Succeeded
                && requirement.Requirement == TiaSafetyProgramAuthorizationRequirement.NotRequired,
            requirement.Diagnostics,
            requirement.Evidence);
    }
    public Task<TiaOpennessV18TransferHostResult> TransferAsync(
        TiaProjectTargetRef projectTarget,
        TiaRuntimeTransferTarget runtimeTarget,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<TiaDiagnostic>();
        var evidence = new List<TiaEvidence>();

        return dispatcher.DispatchAsync(
            "transfer",
            TiaOpennessV18WorkerProtocol.CreateRequest(
                "TransferTarget",
                new KeyValuePair<string, string>("targetId", projectTarget.Id),
                new KeyValuePair<string, string>("runtimeTargetId", runtimeTarget.Id),
                new KeyValuePair<string, string>("runtimeTargetDisplayName", runtimeTarget.DisplayName),
                new KeyValuePair<string, string>("runtimeInstanceName", runtimeTarget.RuntimeInstanceName ?? string.Empty),
                new KeyValuePair<string, string>("connectionModeName", MapConnectionModeName(runtimeTarget.ConnectionMode)),
                new KeyValuePair<string, string>("pcInterfaceName", MapPcInterfaceName(runtimeTarget.PcInterface)),
                new KeyValuePair<string, string>("pcInterfaceNumber", runtimeTarget.PcInterfaceNumber?.ToString() ?? string.Empty),
                new KeyValuePair<string, string>("targetInterfaceName", runtimeTarget.TargetInterfaceName ?? string.Empty)),
            message =>
            {
                if (string.Equals(message.Type, "TransferDiagnostic", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(CreateTransferDiagnostic(message.Values));
                    return TiaOpennessV18WorkerCommandStep<TiaOpennessV18TransferHostResult>.Continue();
                }

                if (string.Equals(message.Type, "TransferEvidence", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(CreateTransferEvidence(message.Values));
                    return TiaOpennessV18WorkerCommandStep<TiaOpennessV18TransferHostResult>.Continue();
                }

                if (string.Equals(message.Type, "TransferResult", StringComparison.OrdinalIgnoreCase))
                {
                    var status = TiaOpennessV18WorkerProtocol.ReadRequiredOperationStatus(
                        message.Values,
                        "status",
                        "TransferResult");
                    return TiaOpennessV18WorkerCommandStep<TiaOpennessV18TransferHostResult>.Complete(
                        new TiaOpennessV18TransferHostResult(
                            status,
                            diagnostics,
                            evidence));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV18WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "code",
                        "Error");
                    var workerMessage = TiaOpennessV18WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "message",
                        "Error");
                    diagnostics.Add(
                        new TiaDiagnostic(
                            code,
                            TiaDiagnosticSeverity.Error,
                            workerMessage,
                            "Transfer",
                            "tia-openness-v18-worker"));
                    return TiaOpennessV18WorkerCommandStep<TiaOpennessV18TransferHostResult>.Complete(
                        new TiaOpennessV18TransferHostResult(
                            MapWorkerOperationStatus(code),
                            diagnostics,
                            evidence));
                }

                throw dispatcher.CreateTransportException(
                    "TIA Openness V18 worker returned an unexpected transfer response.");
            },
            cancellationToken);
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        return dispatcher.CloseAsync(cancellationToken);
    }

    private static string MapConnectionModeName(TiaTransferConnectionMode connectionMode)
    {
        return connectionMode switch
        {
            TiaTransferConnectionMode.PnIe => "PN/IE",
            _ => throw new ArgumentOutOfRangeException(nameof(connectionMode), connectionMode, "Unsupported TIA transfer connection mode.")
        };
    }

    private static string MapPcInterfaceName(TiaTransferPcInterface pcInterface)
    {
        return pcInterface switch
        {
            TiaTransferPcInterface.PlcSim => "PLCSIM",
            _ => throw new ArgumentOutOfRangeException(nameof(pcInterface), pcInterface, "Unsupported TIA transfer PC interface.")
        };
    }

    private static TiaDiagnostic CreateBuildDiagnostic(IReadOnlyDictionary<string, string> values)
    {
        return new TiaDiagnostic(
            values.TryGetValue("code", out var code) ? code : "TiaOpenness.V18.BuildMessage",
            TiaOpennessV18WorkerProtocol.ReadRequiredDiagnosticSeverity(
                values,
                "severity",
                "BuildDiagnostic"),
            values.TryGetValue("message", out var message) ? message : "TIA Openness V18 compiler message.",
            values.TryGetValue("phase", out var phase) ? phase : "Build",
            values.TryGetValue("evidenceId", out var evidenceId) ? evidenceId : null);
    }

    private static TiaDiagnostic CreateTransferDiagnostic(IReadOnlyDictionary<string, string> values)
    {
        return new TiaDiagnostic(
            values.TryGetValue("code", out var code) ? code : "TiaOpenness.V18.TransferMessage",
            TiaOpennessV18WorkerProtocol.ReadRequiredDiagnosticSeverity(
                values,
                "severity",
                "TransferDiagnostic"),
            values.TryGetValue("message", out var message) ? message : "TIA Openness V18 transfer message.",
            values.TryGetValue("phase", out var phase) ? phase : "Transfer",
            values.TryGetValue("evidenceId", out var evidenceId) ? evidenceId : null);
    }

    private static TiaEvidence CreateBuildEvidence(IReadOnlyDictionary<string, string> values)
    {
        return new TiaEvidence(
            values.TryGetValue("id", out var id) ? id : "tia-openness-v18-build-evidence",
            values.TryGetValue("kind", out var kind) ? kind : "BuildEvidence",
            values.TryGetValue("summary", out var summary) ? summary : string.Empty);
    }

    private static TiaEvidence CreateTransferEvidence(IReadOnlyDictionary<string, string> values)
    {
        return new TiaEvidence(
            values.TryGetValue("id", out var id) ? id : "tia-openness-v18-transfer-evidence",
            values.TryGetValue("kind", out var kind) ? kind : "TransferEvidence",
            values.TryGetValue("summary", out var summary) ? summary : string.Empty);
    }

    private static bool IsTargetContextInvalidated(string code)
    {
        return string.Equals(
                code,
                "TiaOpenness.V18.Worker.BuildTargetNotFound",
                StringComparison.Ordinal)
            || string.Equals(
                code,
                "TiaOpenness.V18.Worker.TransferTargetNotFound",
                StringComparison.Ordinal);
    }

    private static TiaOperationStatus MapSafetyOperationStatus(string code)
    {
        return code.EndsWith("NotSupported", StringComparison.Ordinal)
            ? TiaOperationStatus.NotSupported
            : IsTargetContextInvalidated(code)
                || code.EndsWith("TargetNotFound", StringComparison.Ordinal)
                    ? TiaOperationStatus.Invalidated
                    : TiaOperationStatus.Failed;
    }

    private static TiaOperationStatus MapWorkerOperationStatus(string code)
    {
        return code.EndsWith("NotSupported", StringComparison.Ordinal)
            ? TiaOperationStatus.NotSupported
            : IsTargetContextInvalidated(code)
                ? TiaOperationStatus.Invalidated
                : TiaOperationStatus.Failed;
    }

    private static TiaDiagnostic CreateSafetyDiagnostic(string code, string message, string phase)
    {
        return new TiaDiagnostic(
            code,
            TiaDiagnosticSeverity.Error,
            message,
            phase,
            "tia-openness-v18-safety-authorization");
    }
}
