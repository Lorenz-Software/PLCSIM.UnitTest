using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21;

internal sealed class TiaOpennessV21WorkerSessionHandle : ITiaOpennessV21EngineeringSessionHandle
{
    private readonly TiaOpennessV21WorkerCommandDispatcher dispatcher;

    public TiaOpennessV21WorkerSessionHandle(
        TiaOpennessV21WorkerProcessLifetime processLifetime,
        string sessionId,
        string projectSource,
        string productVersion,
        string projectVersion)
    {
        ArgumentNullException.ThrowIfNull(processLifetime);
        dispatcher = new TiaOpennessV21WorkerCommandDispatcher(processLifetime);
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

    public Task<TiaOpennessV21TargetDiscoveryHostResult> DiscoverTargetsAsync(
        CancellationToken cancellationToken)
    {
        var targets = new List<TiaOpennessV21TargetProbe>();
        return dispatcher.DispatchAsync(
            "target discovery",
            TiaOpennessV21WorkerProtocol.CreateRequest("ProbeTargets"),
            message =>
            {
                if (string.Equals(message.Type, "EndTargets", StringComparison.OrdinalIgnoreCase))
                {
                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21TargetDiscoveryHostResult>.Complete(
                        TiaOpennessV21TargetDiscoveryHostResult.SucceededResult(targets));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV21WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "code",
                        "Error");
                    var workerMessage = TiaOpennessV21WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "message",
                        "Error");
                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21TargetDiscoveryHostResult>.Complete(
                        TiaOpennessV21TargetDiscoveryHostResult.Failed(
                            new TiaDiagnostic(
                                code,
                                TiaDiagnosticSeverity.Error,
                                workerMessage,
                                "DiscoverTargets",
                                "tia-openness-v21-worker")));
                }

                if (!string.Equals(message.Type, "Target", StringComparison.OrdinalIgnoreCase))
                {
                    throw dispatcher.CreateTransportException(
                        "TIA Openness V21 worker returned an unexpected target discovery response.");
                }

                targets.Add(TiaOpennessV21TargetProbe.From(message.Values));
                return TiaOpennessV21WorkerCommandStep<TiaOpennessV21TargetDiscoveryHostResult>.Continue();
            },
            cancellationToken);
    }

    public Task<TiaOpennessV21BuildHostResult> BuildAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<TiaDiagnostic>();
        var evidence = new List<TiaEvidence>();

        return dispatcher.DispatchAsync(
            "build",
            TiaOpennessV21WorkerProtocol.CreateRequest(
                "BuildTarget",
                new KeyValuePair<string, string>("targetId", target.Id)),
            message =>
            {
                if (string.Equals(message.Type, "BuildDiagnostic", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(CreateBuildDiagnostic(message.Values));
                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21BuildHostResult>.Continue();
                }

                if (string.Equals(message.Type, "BuildEvidence", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(CreateBuildEvidence(message.Values));
                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21BuildHostResult>.Continue();
                }

                if (string.Equals(message.Type, "BuildResult", StringComparison.OrdinalIgnoreCase))
                {
                    var status = TiaOpennessV21WorkerProtocol.ReadRequiredOperationStatus(
                        message.Values,
                        "status",
                        "BuildResult");
                    var warningCount = TiaOpennessV21WorkerProtocol.ReadRequiredNonNegativeInt(
                        message.Values,
                        "warningCount",
                        "BuildResult");
                    var errorCount = TiaOpennessV21WorkerProtocol.ReadRequiredNonNegativeInt(
                        message.Values,
                        "errorCount",
                        "BuildResult");
                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21BuildHostResult>.Complete(
                        new TiaOpennessV21BuildHostResult(
                            status,
                            warningCount,
                            errorCount,
                            diagnostics,
                            evidence));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV21WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "code",
                        "Error");
                    var workerMessage = TiaOpennessV21WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "message",
                        "Error");
                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21BuildHostResult>.Complete(
                        new TiaOpennessV21BuildHostResult(
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
                                    "tia-openness-v21-worker")
                            },
                            evidence));
                }

                throw dispatcher.CreateTransportException(
                    "TIA Openness V21 worker returned an unexpected build response.");
            },
            cancellationToken);
    }

    public Task<TiaOpennessV21SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        return dispatcher.DispatchAsync(
            "Safety-program authorization requirement discovery",
            TiaOpennessV21WorkerProtocol.CreateRequest(
                "ProbeSafetyAuthorization",
                new KeyValuePair<string, string>("targetId", target.Id)),
            message =>
            {
                if (string.Equals(message.Type, "SafetyAuthorizationRequirementResult", StringComparison.OrdinalIgnoreCase))
                {
                    var status = TiaOpennessV21WorkerProtocol.ReadRequiredOperationStatus(
                        message.Values,
                        "status",
                        "SafetyAuthorizationRequirementResult");
                    var requirementText = TiaOpennessV21WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "requirement",
                        "SafetyAuthorizationRequirementResult");
                    if (!Enum.TryParse<TiaSafetyProgramAuthorizationRequirement>(requirementText, true, out var requirement)
                        || !Enum.IsDefined(requirement))
                    {
                        throw dispatcher.CreateTransportException(
                            "TIA Openness V21 worker returned an invalid Safety authorization requirement.");
                    }

                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21SafetyAuthorizationRequirementHostResult>.Complete(
                        new TiaOpennessV21SafetyAuthorizationRequirementHostResult(
                            status,
                            requirement,
                            Array.Empty<TiaDiagnostic>(),
                            Array.Empty<TiaEvidence>()));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV21WorkerProtocol.ReadRequiredField(message.Values, "code", "Error");
                    var workerMessage = TiaOpennessV21WorkerProtocol.ReadRequiredField(message.Values, "message", "Error");
                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21SafetyAuthorizationRequirementHostResult>.Complete(
                        new TiaOpennessV21SafetyAuthorizationRequirementHostResult(
                            MapSafetyOperationStatus(code),
                            TiaSafetyProgramAuthorizationRequirement.NotRequired,
                            new[] { CreateSafetyDiagnostic(code, workerMessage, "DiscoverSafetyAuthorizationRequirement") },
                            Array.Empty<TiaEvidence>()));
                }

                throw dispatcher.CreateTransportException(
                    "TIA Openness V21 worker returned an unexpected Safety authorization requirement response.");
            },
            cancellationToken);
    }

    public Task<TiaOpennessV21SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        return dispatcher.DispatchAsync(
            "Safety-program authorization",
            TiaOpennessV21WorkerProtocol.CreateProtectedCredentialRequest(
                "AuthorizeSafetyProgram",
                password,
                new KeyValuePair<string, string>("targetId", target.Id)),
            message =>
            {
                if (string.Equals(message.Type, "SafetyAuthorizationResult", StringComparison.OrdinalIgnoreCase))
                {
                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21SafetyAuthorizationHostResult>.Complete(
                        new TiaOpennessV21SafetyAuthorizationHostResult(
                            TiaOpennessV21WorkerProtocol.ReadRequiredOperationStatus(
                                message.Values,
                                "status",
                                "SafetyAuthorizationResult"),
                            TiaOpennessV21WorkerProtocol.ReadRequiredBoolean(
                                message.Values,
                                "authorized",
                                "SafetyAuthorizationResult"),
                            Array.Empty<TiaDiagnostic>(),
                            Array.Empty<TiaEvidence>()));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV21WorkerProtocol.ReadRequiredField(message.Values, "code", "Error");
                    var workerMessage = TiaOpennessV21WorkerProtocol.ReadRequiredField(message.Values, "message", "Error");
                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21SafetyAuthorizationHostResult>.Complete(
                        new TiaOpennessV21SafetyAuthorizationHostResult(
                            MapSafetyOperationStatus(code),
                            false,
                            new[] { CreateSafetyDiagnostic(code, workerMessage, "AuthorizeSafetyProgram") },
                            Array.Empty<TiaEvidence>()));
                }

                throw dispatcher.CreateTransportException(
                    "TIA Openness V21 worker returned an unexpected Safety authorization response.");
            },
            cancellationToken);
    }

    public Task<TiaOpennessV21TransferHostResult> TransferAsync(
        TiaProjectTargetRef projectTarget,
        TiaRuntimeTransferTarget runtimeTarget,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<TiaDiagnostic>();
        var evidence = new List<TiaEvidence>();

        return dispatcher.DispatchAsync(
            "transfer",
            TiaOpennessV21WorkerProtocol.CreateRequest(
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
                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21TransferHostResult>.Continue();
                }

                if (string.Equals(message.Type, "TransferEvidence", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(CreateTransferEvidence(message.Values));
                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21TransferHostResult>.Continue();
                }

                if (string.Equals(message.Type, "TransferResult", StringComparison.OrdinalIgnoreCase))
                {
                    var status = TiaOpennessV21WorkerProtocol.ReadRequiredOperationStatus(
                        message.Values,
                        "status",
                        "TransferResult");
                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21TransferHostResult>.Complete(
                        new TiaOpennessV21TransferHostResult(
                            status,
                            diagnostics,
                            evidence));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV21WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "code",
                        "Error");
                    var workerMessage = TiaOpennessV21WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "message",
                        "Error");
                    diagnostics.Add(
                        new TiaDiagnostic(
                            code,
                            TiaDiagnosticSeverity.Error,
                            workerMessage,
                            "Transfer",
                            "tia-openness-v21-worker"));
                    return TiaOpennessV21WorkerCommandStep<TiaOpennessV21TransferHostResult>.Complete(
                        new TiaOpennessV21TransferHostResult(
                            MapWorkerOperationStatus(code),
                            diagnostics,
                            evidence));
                }

                throw dispatcher.CreateTransportException(
                    "TIA Openness V21 worker returned an unexpected transfer response.");
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
            values.TryGetValue("code", out var code) ? code : "TiaOpenness.V21.BuildMessage",
            TiaOpennessV21WorkerProtocol.ReadRequiredDiagnosticSeverity(
                values,
                "severity",
                "BuildDiagnostic"),
            values.TryGetValue("message", out var message) ? message : "TIA Openness V21 compiler message.",
            values.TryGetValue("phase", out var phase) ? phase : "Build",
            values.TryGetValue("evidenceId", out var evidenceId) ? evidenceId : null);
    }

    private static TiaDiagnostic CreateTransferDiagnostic(IReadOnlyDictionary<string, string> values)
    {
        return new TiaDiagnostic(
            values.TryGetValue("code", out var code) ? code : "TiaOpenness.V21.TransferMessage",
            TiaOpennessV21WorkerProtocol.ReadRequiredDiagnosticSeverity(
                values,
                "severity",
                "TransferDiagnostic"),
            values.TryGetValue("message", out var message) ? message : "TIA Openness V21 transfer message.",
            values.TryGetValue("phase", out var phase) ? phase : "Transfer",
            values.TryGetValue("evidenceId", out var evidenceId) ? evidenceId : null);
    }

    private static TiaEvidence CreateBuildEvidence(IReadOnlyDictionary<string, string> values)
    {
        return new TiaEvidence(
            values.TryGetValue("id", out var id) ? id : "tia-openness-v21-build-evidence",
            values.TryGetValue("kind", out var kind) ? kind : "BuildEvidence",
            values.TryGetValue("summary", out var summary) ? summary : string.Empty);
    }

    private static TiaEvidence CreateTransferEvidence(IReadOnlyDictionary<string, string> values)
    {
        return new TiaEvidence(
            values.TryGetValue("id", out var id) ? id : "tia-openness-v21-transfer-evidence",
            values.TryGetValue("kind", out var kind) ? kind : "TransferEvidence",
            values.TryGetValue("summary", out var summary) ? summary : string.Empty);
    }

    private static bool IsTargetContextInvalidated(string code)
    {
        return string.Equals(
                code,
                "TiaOpenness.V21.Worker.BuildTargetNotFound",
                StringComparison.Ordinal)
            || string.Equals(
                code,
                "TiaOpenness.V21.Worker.TransferTargetNotFound",
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
            "tia-openness-v21-safety-authorization");
    }
}
