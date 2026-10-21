using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20;

internal sealed class TiaOpennessV20WorkerSessionHandle : ITiaOpennessV20EngineeringSessionHandle
{
    private readonly TiaOpennessV20WorkerCommandDispatcher dispatcher;

    public TiaOpennessV20WorkerSessionHandle(
        TiaOpennessV20WorkerProcessLifetime processLifetime,
        string sessionId,
        string projectSource,
        string productVersion,
        string projectVersion)
    {
        ArgumentNullException.ThrowIfNull(processLifetime);
        dispatcher = new TiaOpennessV20WorkerCommandDispatcher(processLifetime);
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

    public Task<TiaOpennessV20TargetDiscoveryHostResult> DiscoverTargetsAsync(
        CancellationToken cancellationToken)
    {
        var targets = new List<TiaOpennessV20TargetProbe>();
        return dispatcher.DispatchAsync(
            "target discovery",
            TiaOpennessV20WorkerProtocol.CreateRequest("ProbeTargets"),
            message =>
            {
                if (string.Equals(message.Type, "EndTargets", StringComparison.OrdinalIgnoreCase))
                {
                    return TiaOpennessV20WorkerCommandStep<TiaOpennessV20TargetDiscoveryHostResult>.Complete(
                        TiaOpennessV20TargetDiscoveryHostResult.SucceededResult(targets));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV20WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "code",
                        "Error");
                    var workerMessage = TiaOpennessV20WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "message",
                        "Error");
                    return TiaOpennessV20WorkerCommandStep<TiaOpennessV20TargetDiscoveryHostResult>.Complete(
                        TiaOpennessV20TargetDiscoveryHostResult.Failed(
                            new TiaDiagnostic(
                                code,
                                TiaDiagnosticSeverity.Error,
                                workerMessage,
                                "DiscoverTargets",
                                "tia-openness-v20-worker")));
                }

                if (!string.Equals(message.Type, "Target", StringComparison.OrdinalIgnoreCase))
                {
                    throw dispatcher.CreateTransportException(
                        "TIA Openness V20 worker returned an unexpected target discovery response.");
                }

                targets.Add(TiaOpennessV20TargetProbe.From(message.Values));
                return TiaOpennessV20WorkerCommandStep<TiaOpennessV20TargetDiscoveryHostResult>.Continue();
            },
            cancellationToken);
    }

    public Task<TiaOpennessV20BuildHostResult> BuildAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<TiaDiagnostic>();
        var evidence = new List<TiaEvidence>();

        return dispatcher.DispatchAsync(
            "build",
            TiaOpennessV20WorkerProtocol.CreateRequest(
                "BuildTarget",
                new KeyValuePair<string, string>("targetId", target.Id)),
            message =>
            {
                if (string.Equals(message.Type, "BuildDiagnostic", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(CreateBuildDiagnostic(message.Values));
                    return TiaOpennessV20WorkerCommandStep<TiaOpennessV20BuildHostResult>.Continue();
                }

                if (string.Equals(message.Type, "BuildEvidence", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(CreateBuildEvidence(message.Values));
                    return TiaOpennessV20WorkerCommandStep<TiaOpennessV20BuildHostResult>.Continue();
                }

                if (string.Equals(message.Type, "BuildResult", StringComparison.OrdinalIgnoreCase))
                {
                    var status = TiaOpennessV20WorkerProtocol.ReadRequiredOperationStatus(
                        message.Values,
                        "status",
                        "BuildResult");
                    var warningCount = TiaOpennessV20WorkerProtocol.ReadRequiredNonNegativeInt(
                        message.Values,
                        "warningCount",
                        "BuildResult");
                    var errorCount = TiaOpennessV20WorkerProtocol.ReadRequiredNonNegativeInt(
                        message.Values,
                        "errorCount",
                        "BuildResult");
                    return TiaOpennessV20WorkerCommandStep<TiaOpennessV20BuildHostResult>.Complete(
                        new TiaOpennessV20BuildHostResult(
                            status,
                            warningCount,
                            errorCount,
                            diagnostics,
                            evidence));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV20WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "code",
                        "Error");
                    var workerMessage = TiaOpennessV20WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "message",
                        "Error");
                    return TiaOpennessV20WorkerCommandStep<TiaOpennessV20BuildHostResult>.Complete(
                        new TiaOpennessV20BuildHostResult(
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
                                    "tia-openness-v20-worker")
                            },
                            evidence));
                }

                throw dispatcher.CreateTransportException(
                    "TIA Openness V20 worker returned an unexpected build response.");
            },
            cancellationToken);
    }

    public Task<TiaOpennessV20SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        return dispatcher.DispatchAsync(
            "Safety-program authorization requirement discovery",
            TiaOpennessV20WorkerProtocol.CreateRequest(
                "ProbeSafetyAuthorization",
                new KeyValuePair<string, string>("targetId", target.Id)),
            message =>
            {
                if (string.Equals(message.Type, "SafetyAuthorizationRequirementResult", StringComparison.OrdinalIgnoreCase))
                {
                    var status = TiaOpennessV20WorkerProtocol.ReadRequiredOperationStatus(
                        message.Values,
                        "status",
                        "SafetyAuthorizationRequirementResult");
                    var requirementText = TiaOpennessV20WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "requirement",
                        "SafetyAuthorizationRequirementResult");
                    if (!Enum.TryParse<TiaSafetyProgramAuthorizationRequirement>(requirementText, true, out var requirement)
                        || !Enum.IsDefined(requirement))
                    {
                        throw dispatcher.CreateTransportException(
                            "TIA Openness V20 worker returned an invalid Safety authorization requirement.");
                    }

                    return TiaOpennessV20WorkerCommandStep<TiaOpennessV20SafetyAuthorizationRequirementHostResult>.Complete(
                        new TiaOpennessV20SafetyAuthorizationRequirementHostResult(
                            status,
                            requirement,
                            Array.Empty<TiaDiagnostic>(),
                            Array.Empty<TiaEvidence>()));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV20WorkerProtocol.ReadRequiredField(message.Values, "code", "Error");
                    var workerMessage = TiaOpennessV20WorkerProtocol.ReadRequiredField(message.Values, "message", "Error");
                    return TiaOpennessV20WorkerCommandStep<TiaOpennessV20SafetyAuthorizationRequirementHostResult>.Complete(
                        new TiaOpennessV20SafetyAuthorizationRequirementHostResult(
                            MapSafetyOperationStatus(code),
                            TiaSafetyProgramAuthorizationRequirement.NotRequired,
                            new[] { CreateSafetyDiagnostic(code, workerMessage, "DiscoverSafetyAuthorizationRequirement") },
                            Array.Empty<TiaEvidence>()));
                }

                throw dispatcher.CreateTransportException(
                    "TIA Openness V20 worker returned an unexpected Safety authorization requirement response.");
            },
            cancellationToken);
    }

    public async Task<TiaOpennessV20SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        var requirement = await GetSafetyProgramAuthorizationRequirementAsync(target, cancellationToken)
            .ConfigureAwait(false);
        return new TiaOpennessV20SafetyAuthorizationHostResult(
            requirement.Status,
            requirement.Status == TiaOperationStatus.Succeeded
                && requirement.Requirement == TiaSafetyProgramAuthorizationRequirement.NotRequired,
            requirement.Diagnostics,
            requirement.Evidence);
    }
    public Task<TiaOpennessV20TransferHostResult> TransferAsync(
        TiaProjectTargetRef projectTarget,
        TiaRuntimeTransferTarget runtimeTarget,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<TiaDiagnostic>();
        var evidence = new List<TiaEvidence>();

        return dispatcher.DispatchAsync(
            "transfer",
            TiaOpennessV20WorkerProtocol.CreateRequest(
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
                    return TiaOpennessV20WorkerCommandStep<TiaOpennessV20TransferHostResult>.Continue();
                }

                if (string.Equals(message.Type, "TransferEvidence", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(CreateTransferEvidence(message.Values));
                    return TiaOpennessV20WorkerCommandStep<TiaOpennessV20TransferHostResult>.Continue();
                }

                if (string.Equals(message.Type, "TransferResult", StringComparison.OrdinalIgnoreCase))
                {
                    var status = TiaOpennessV20WorkerProtocol.ReadRequiredOperationStatus(
                        message.Values,
                        "status",
                        "TransferResult");
                    return TiaOpennessV20WorkerCommandStep<TiaOpennessV20TransferHostResult>.Complete(
                        new TiaOpennessV20TransferHostResult(
                            status,
                            diagnostics,
                            evidence));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV20WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "code",
                        "Error");
                    var workerMessage = TiaOpennessV20WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "message",
                        "Error");
                    diagnostics.Add(
                        new TiaDiagnostic(
                            code,
                            TiaDiagnosticSeverity.Error,
                            workerMessage,
                            "Transfer",
                            "tia-openness-v20-worker"));
                    return TiaOpennessV20WorkerCommandStep<TiaOpennessV20TransferHostResult>.Complete(
                        new TiaOpennessV20TransferHostResult(
                            MapWorkerOperationStatus(code),
                            diagnostics,
                            evidence));
                }

                throw dispatcher.CreateTransportException(
                    "TIA Openness V20 worker returned an unexpected transfer response.");
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
            values.TryGetValue("code", out var code) ? code : "TiaOpenness.V20.BuildMessage",
            TiaOpennessV20WorkerProtocol.ReadRequiredDiagnosticSeverity(
                values,
                "severity",
                "BuildDiagnostic"),
            values.TryGetValue("message", out var message) ? message : "TIA Openness V20 compiler message.",
            values.TryGetValue("phase", out var phase) ? phase : "Build",
            values.TryGetValue("evidenceId", out var evidenceId) ? evidenceId : null);
    }

    private static TiaDiagnostic CreateTransferDiagnostic(IReadOnlyDictionary<string, string> values)
    {
        return new TiaDiagnostic(
            values.TryGetValue("code", out var code) ? code : "TiaOpenness.V20.TransferMessage",
            TiaOpennessV20WorkerProtocol.ReadRequiredDiagnosticSeverity(
                values,
                "severity",
                "TransferDiagnostic"),
            values.TryGetValue("message", out var message) ? message : "TIA Openness V20 transfer message.",
            values.TryGetValue("phase", out var phase) ? phase : "Transfer",
            values.TryGetValue("evidenceId", out var evidenceId) ? evidenceId : null);
    }

    private static TiaEvidence CreateBuildEvidence(IReadOnlyDictionary<string, string> values)
    {
        return new TiaEvidence(
            values.TryGetValue("id", out var id) ? id : "tia-openness-v20-build-evidence",
            values.TryGetValue("kind", out var kind) ? kind : "BuildEvidence",
            values.TryGetValue("summary", out var summary) ? summary : string.Empty);
    }

    private static TiaEvidence CreateTransferEvidence(IReadOnlyDictionary<string, string> values)
    {
        return new TiaEvidence(
            values.TryGetValue("id", out var id) ? id : "tia-openness-v20-transfer-evidence",
            values.TryGetValue("kind", out var kind) ? kind : "TransferEvidence",
            values.TryGetValue("summary", out var summary) ? summary : string.Empty);
    }

    private static bool IsTargetContextInvalidated(string code)
    {
        return string.Equals(
                code,
                "TiaOpenness.V20.Worker.BuildTargetNotFound",
                StringComparison.Ordinal)
            || string.Equals(
                code,
                "TiaOpenness.V20.Worker.TransferTargetNotFound",
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
            "tia-openness-v20-safety-authorization");
    }
}
