using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1;

internal sealed class TiaOpennessV15_1WorkerSessionHandle : ITiaOpennessV15_1EngineeringSessionHandle
{
    private readonly TiaOpennessV15_1WorkerCommandDispatcher dispatcher;

    public TiaOpennessV15_1WorkerSessionHandle(
        TiaOpennessV15_1WorkerProcessLifetime processLifetime,
        string sessionId,
        string projectSource,
        string productVersion,
        string projectVersion)
    {
        ArgumentNullException.ThrowIfNull(processLifetime);
        dispatcher = new TiaOpennessV15_1WorkerCommandDispatcher(processLifetime);
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

    public Task<TiaOpennessV15_1TargetDiscoveryHostResult> DiscoverTargetsAsync(
        CancellationToken cancellationToken)
    {
        var targets = new List<TiaOpennessV15_1TargetProbe>();
        return dispatcher.DispatchAsync(
            "target discovery",
            TiaOpennessV15_1WorkerProtocol.CreateRequest("ProbeTargets"),
            message =>
            {
                if (string.Equals(message.Type, "EndTargets", StringComparison.OrdinalIgnoreCase))
                {
                    return TiaOpennessV15_1WorkerCommandStep<TiaOpennessV15_1TargetDiscoveryHostResult>.Complete(
                        TiaOpennessV15_1TargetDiscoveryHostResult.SucceededResult(targets));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV15_1WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "code",
                        "Error");
                    var workerMessage = TiaOpennessV15_1WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "message",
                        "Error");
                    return TiaOpennessV15_1WorkerCommandStep<TiaOpennessV15_1TargetDiscoveryHostResult>.Complete(
                        TiaOpennessV15_1TargetDiscoveryHostResult.Failed(
                            new TiaDiagnostic(
                                code,
                                TiaDiagnosticSeverity.Error,
                                workerMessage,
                                "DiscoverTargets",
                                "tia-openness-v15_1-worker")));
                }

                if (!string.Equals(message.Type, "Target", StringComparison.OrdinalIgnoreCase))
                {
                    throw dispatcher.CreateTransportException(
                        "TIA Openness V15.1 worker returned an unexpected target discovery response.");
                }

                targets.Add(TiaOpennessV15_1TargetProbe.From(message.Values));
                return TiaOpennessV15_1WorkerCommandStep<TiaOpennessV15_1TargetDiscoveryHostResult>.Continue();
            },
            cancellationToken);
    }

    public Task<TiaOpennessV15_1BuildHostResult> BuildAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<TiaDiagnostic>();
        var evidence = new List<TiaEvidence>();

        return dispatcher.DispatchAsync(
            "build",
            TiaOpennessV15_1WorkerProtocol.CreateRequest(
                "BuildTarget",
                new KeyValuePair<string, string>("targetId", target.Id)),
            message =>
            {
                if (string.Equals(message.Type, "BuildDiagnostic", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(CreateBuildDiagnostic(message.Values));
                    return TiaOpennessV15_1WorkerCommandStep<TiaOpennessV15_1BuildHostResult>.Continue();
                }

                if (string.Equals(message.Type, "BuildEvidence", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(CreateBuildEvidence(message.Values));
                    return TiaOpennessV15_1WorkerCommandStep<TiaOpennessV15_1BuildHostResult>.Continue();
                }

                if (string.Equals(message.Type, "BuildResult", StringComparison.OrdinalIgnoreCase))
                {
                    var status = TiaOpennessV15_1WorkerProtocol.ReadRequiredOperationStatus(
                        message.Values,
                        "status",
                        "BuildResult");
                    var warningCount = TiaOpennessV15_1WorkerProtocol.ReadRequiredNonNegativeInt(
                        message.Values,
                        "warningCount",
                        "BuildResult");
                    var errorCount = TiaOpennessV15_1WorkerProtocol.ReadRequiredNonNegativeInt(
                        message.Values,
                        "errorCount",
                        "BuildResult");
                    return TiaOpennessV15_1WorkerCommandStep<TiaOpennessV15_1BuildHostResult>.Complete(
                        new TiaOpennessV15_1BuildHostResult(
                            status,
                            warningCount,
                            errorCount,
                            diagnostics,
                            evidence));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV15_1WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "code",
                        "Error");
                    var workerMessage = TiaOpennessV15_1WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "message",
                        "Error");
                    return TiaOpennessV15_1WorkerCommandStep<TiaOpennessV15_1BuildHostResult>.Complete(
                        new TiaOpennessV15_1BuildHostResult(
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
                                    "tia-openness-v15_1-worker")
                            },
                            evidence));
                }

                throw dispatcher.CreateTransportException(
                    "TIA Openness V15.1 worker returned an unexpected build response.");
            },
            cancellationToken);
    }

    public Task<TiaOpennessV15_1SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        return dispatcher.DispatchAsync(
            "Safety-program authorization requirement discovery",
            TiaOpennessV15_1WorkerProtocol.CreateRequest(
                "ProbeSafetyAuthorization",
                new KeyValuePair<string, string>("targetId", target.Id)),
            message =>
            {
                if (string.Equals(message.Type, "SafetyAuthorizationRequirementResult", StringComparison.OrdinalIgnoreCase))
                {
                    var status = TiaOpennessV15_1WorkerProtocol.ReadRequiredOperationStatus(
                        message.Values,
                        "status",
                        "SafetyAuthorizationRequirementResult");
                    var requirementText = TiaOpennessV15_1WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "requirement",
                        "SafetyAuthorizationRequirementResult");
                    if (!Enum.TryParse<TiaSafetyProgramAuthorizationRequirement>(requirementText, true, out var requirement)
                        || !Enum.IsDefined(requirement))
                    {
                        throw dispatcher.CreateTransportException(
                            "TIA Openness V15.1 worker returned an invalid Safety authorization requirement.");
                    }

                    return TiaOpennessV15_1WorkerCommandStep<TiaOpennessV15_1SafetyAuthorizationRequirementHostResult>.Complete(
                        new TiaOpennessV15_1SafetyAuthorizationRequirementHostResult(
                            status,
                            requirement,
                            Array.Empty<TiaDiagnostic>(),
                            Array.Empty<TiaEvidence>()));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV15_1WorkerProtocol.ReadRequiredField(message.Values, "code", "Error");
                    var workerMessage = TiaOpennessV15_1WorkerProtocol.ReadRequiredField(message.Values, "message", "Error");
                    return TiaOpennessV15_1WorkerCommandStep<TiaOpennessV15_1SafetyAuthorizationRequirementHostResult>.Complete(
                        new TiaOpennessV15_1SafetyAuthorizationRequirementHostResult(
                            MapSafetyOperationStatus(code),
                            TiaSafetyProgramAuthorizationRequirement.NotRequired,
                            new[] { CreateSafetyDiagnostic(code, workerMessage, "DiscoverSafetyAuthorizationRequirement") },
                            Array.Empty<TiaEvidence>()));
                }

                throw dispatcher.CreateTransportException(
                    "TIA Openness V15.1 worker returned an unexpected Safety authorization requirement response.");
            },
            cancellationToken);
    }

    public async Task<TiaOpennessV15_1SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        var requirement = await GetSafetyProgramAuthorizationRequirementAsync(target, cancellationToken)
            .ConfigureAwait(false);
        return new TiaOpennessV15_1SafetyAuthorizationHostResult(
            requirement.Status,
            requirement.Status == TiaOperationStatus.Succeeded
                && requirement.Requirement == TiaSafetyProgramAuthorizationRequirement.NotRequired,
            requirement.Diagnostics,
            requirement.Evidence);
    }
    public Task<TiaOpennessV15_1TransferHostResult> TransferAsync(
        TiaProjectTargetRef projectTarget,
        TiaRuntimeTransferTarget runtimeTarget,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<TiaDiagnostic>();
        var evidence = new List<TiaEvidence>();

        return dispatcher.DispatchAsync(
            "transfer",
            TiaOpennessV15_1WorkerProtocol.CreateRequest(
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
                    return TiaOpennessV15_1WorkerCommandStep<TiaOpennessV15_1TransferHostResult>.Continue();
                }

                if (string.Equals(message.Type, "TransferEvidence", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(CreateTransferEvidence(message.Values));
                    return TiaOpennessV15_1WorkerCommandStep<TiaOpennessV15_1TransferHostResult>.Continue();
                }

                if (string.Equals(message.Type, "TransferResult", StringComparison.OrdinalIgnoreCase))
                {
                    var status = TiaOpennessV15_1WorkerProtocol.ReadRequiredOperationStatus(
                        message.Values,
                        "status",
                        "TransferResult");
                    return TiaOpennessV15_1WorkerCommandStep<TiaOpennessV15_1TransferHostResult>.Complete(
                        new TiaOpennessV15_1TransferHostResult(
                            status,
                            diagnostics,
                            evidence));
                }

                if (string.Equals(message.Type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = TiaOpennessV15_1WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "code",
                        "Error");
                    var workerMessage = TiaOpennessV15_1WorkerProtocol.ReadRequiredField(
                        message.Values,
                        "message",
                        "Error");
                    diagnostics.Add(
                        new TiaDiagnostic(
                            code,
                            TiaDiagnosticSeverity.Error,
                            workerMessage,
                            "Transfer",
                            "tia-openness-v15_1-worker"));
                    return TiaOpennessV15_1WorkerCommandStep<TiaOpennessV15_1TransferHostResult>.Complete(
                        new TiaOpennessV15_1TransferHostResult(
                            MapWorkerOperationStatus(code),
                            diagnostics,
                            evidence));
                }

                throw dispatcher.CreateTransportException(
                    "TIA Openness V15.1 worker returned an unexpected transfer response.");
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
            values.TryGetValue("code", out var code) ? code : "TiaOpenness.V15_1.BuildMessage",
            TiaOpennessV15_1WorkerProtocol.ReadRequiredDiagnosticSeverity(
                values,
                "severity",
                "BuildDiagnostic"),
            values.TryGetValue("message", out var message) ? message : "TIA Openness V15.1 compiler message.",
            values.TryGetValue("phase", out var phase) ? phase : "Build",
            values.TryGetValue("evidenceId", out var evidenceId) ? evidenceId : null);
    }

    private static TiaDiagnostic CreateTransferDiagnostic(IReadOnlyDictionary<string, string> values)
    {
        return new TiaDiagnostic(
            values.TryGetValue("code", out var code) ? code : "TiaOpenness.V15_1.TransferMessage",
            TiaOpennessV15_1WorkerProtocol.ReadRequiredDiagnosticSeverity(
                values,
                "severity",
                "TransferDiagnostic"),
            values.TryGetValue("message", out var message) ? message : "TIA Openness V15.1 transfer message.",
            values.TryGetValue("phase", out var phase) ? phase : "Transfer",
            values.TryGetValue("evidenceId", out var evidenceId) ? evidenceId : null);
    }

    private static TiaEvidence CreateBuildEvidence(IReadOnlyDictionary<string, string> values)
    {
        return new TiaEvidence(
            values.TryGetValue("id", out var id) ? id : "tia-openness-v15_1-build-evidence",
            values.TryGetValue("kind", out var kind) ? kind : "BuildEvidence",
            values.TryGetValue("summary", out var summary) ? summary : string.Empty);
    }

    private static TiaEvidence CreateTransferEvidence(IReadOnlyDictionary<string, string> values)
    {
        return new TiaEvidence(
            values.TryGetValue("id", out var id) ? id : "tia-openness-v15_1-transfer-evidence",
            values.TryGetValue("kind", out var kind) ? kind : "TransferEvidence",
            values.TryGetValue("summary", out var summary) ? summary : string.Empty);
    }

    private static bool IsTargetContextInvalidated(string code)
    {
        return string.Equals(
                code,
                "TiaOpenness.V15_1.Worker.BuildTargetNotFound",
                StringComparison.Ordinal)
            || string.Equals(
                code,
                "TiaOpenness.V15_1.Worker.TransferTargetNotFound",
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
            "tia-openness-v15_1-safety-authorization");
    }
}
