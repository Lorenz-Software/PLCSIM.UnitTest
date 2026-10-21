using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1;

internal interface ITiaOpennessV15_1AuthorizationProbeHost
{
    Task<TiaOpennessV15_1AuthorizationProbeHostResult> ProbeAsync(
        string publicApiDirectory,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken);
}

internal sealed class TiaOpennessV15_1AuthorizationProbeHost : ITiaOpennessV15_1AuthorizationProbeHost
{
    public async Task<TiaOpennessV15_1AuthorizationProbeHostResult> ProbeAsync(
        string publicApiDirectory,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(bindingContext);

        var workerPath = TiaOpennessV15_1WorkerProcessHost.ResolveDeployedWorkerPath();
        if (!File.Exists(workerPath))
        {
            return TiaOpennessV15_1AuthorizationProbeHostResult.Failed(
                TiaAuthorizationProbeStatus.Unavailable,
                "TiaOpenness.V15_1.AuthorizationProbeWorkerMissing",
                "TIA Openness V15.1 authorization-probe worker executable was not found.",
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v15_1-authorization-probe-worker",
                        "AuthorizationProbeWorker",
                        workerPath)
                });
        }

        var pipeName = TiaOpennessV15_1WorkerProcessHost.CreatePipeName();
        var workerInstanceId = Guid.NewGuid().ToString("N");
        var startInfo = TiaOpennessV15_1WorkerProcessHost.CreateAuthorizationProbeStartInfo(
            workerPath,
            pipeName,
            publicApiDirectory,
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
                return TiaOpennessV15_1AuthorizationProbeHostResult.Failed(
                    MapStartFailure(processStart.Code),
                    processStart.Code,
                    processStart.Message,
                    processStart.Evidence);
            }

            processLifetime = processStart.Lifetime;
            var initialResponse = TiaOpennessV15_1WorkerProtocol.ParseMessage(
                processStart.InitialResponse ?? string.Empty);
            if (!initialResponse.IsProtocolVersionSupported)
            {
                processLifetime.Terminate();
                return TiaOpennessV15_1AuthorizationProbeHostResult.Failed(
                    TiaAuthorizationProbeStatus.Failed,
                    "TiaOpenness.V15_1.AuthorizationProbeInvalidResponse",
                    "TIA Openness V15.1 authorization-probe worker returned an invalid response.",
                    Array.Empty<TiaEvidence>());
            }

            if (string.Equals(initialResponse.Type, "Error", StringComparison.OrdinalIgnoreCase))
            {
                var code = ReadOptional(initialResponse.Values, "code")
                    ?? "TiaOpenness.V15_1.AuthorizationProbeFailed";
                var message = ReadOptional(initialResponse.Values, "message")
                    ?? "TIA Openness V15.1 authorization probe failed.";
                await processLifetime.CompleteFailedStartAsync(cancellationToken).ConfigureAwait(false);
                return TiaOpennessV15_1AuthorizationProbeHostResult.Failed(
                    MapWorkerFailure(code),
                    code,
                    message,
                    Array.Empty<TiaEvidence>());
            }

            if (!string.Equals(
                    initialResponse.Type,
                    "AuthorizationReady",
                    StringComparison.OrdinalIgnoreCase))
            {
                processLifetime.Terminate();
                return TiaOpennessV15_1AuthorizationProbeHostResult.Failed(
                    TiaAuthorizationProbeStatus.Failed,
                    "TiaOpenness.V15_1.AuthorizationProbeUnexpectedResponse",
                    "TIA Openness V15.1 authorization-probe worker returned an unexpected response.",
                    Array.Empty<TiaEvidence>());
            }

            var evidence = new[]
            {
                new TiaEvidence(
                    "tia-openness-v15_1-authorization-probe-worker",
                    "AuthorizationProbeWorker",
                    workerPath),
                new TiaEvidence(
                    "tia-openness-v15_1-authorization-probe-public-api",
                    "PublicApiDirectory",
                    publicApiDirectory)
            };

            try
            {
                await new TiaOpennessV15_1WorkerCommandDispatcher(processLifetime)
                    .CloseAsync(cancellationToken)
                    .ConfigureAwait(false);
                processLifetime = null;
                return TiaOpennessV15_1AuthorizationProbeHostResult.Authorized(evidence);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                processLifetime = null;
                return TiaOpennessV15_1AuthorizationProbeHostResult.Failed(
                    TiaAuthorizationProbeStatus.CleanupFailed,
                    "TiaOpenness.V15_1.AuthorizationProbeCleanupFailed",
                    ex.Message,
                    evidence);
            }
        }
        catch (OperationCanceledException)
        {
            processLifetime?.Terminate();
            throw;
        }
        catch (Exception ex)
        {
            processLifetime?.Terminate();
            return TiaOpennessV15_1AuthorizationProbeHostResult.Failed(
                TiaAuthorizationProbeStatus.Failed,
                "TiaOpenness.V15_1.AuthorizationProbeFailed",
                "TIA Openness V15.1 authorization probe failed: " + ex.Message,
                Array.Empty<TiaEvidence>());
        }
    }

    private static TiaAuthorizationProbeStatus MapStartFailure(string code)
    {
        return code.EndsWith("TimedOut", StringComparison.OrdinalIgnoreCase)
            ? TiaAuthorizationProbeStatus.TimedOut
            : TiaAuthorizationProbeStatus.Failed;
    }

    private static TiaAuthorizationProbeStatus MapWorkerFailure(string code)
    {
        return string.Equals(code, "TiaOpenness.V15_1.SecurityDenied", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "TiaOpenness.V15_1.AccessDenied", StringComparison.OrdinalIgnoreCase)
                ? TiaAuthorizationProbeStatus.Denied
                : TiaAuthorizationProbeStatus.Failed;
    }

    private static string? ReadOptional(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }
}

internal sealed record TiaOpennessV15_1AuthorizationProbeHostResult(
    TiaAuthorizationProbeStatus Status,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public static TiaOpennessV15_1AuthorizationProbeHostResult Authorized(
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV15_1AuthorizationProbeHostResult(
            TiaAuthorizationProbeStatus.Authorized,
            Array.Empty<TiaDiagnostic>(),
            evidence);
    }

    public static TiaOpennessV15_1AuthorizationProbeHostResult Failed(
        TiaAuthorizationProbeStatus status,
        string code,
        string message,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV15_1AuthorizationProbeHostResult(
            status,
            new[]
            {
                new TiaDiagnostic(
                    code,
                    TiaDiagnosticSeverity.Error,
                    message,
                    "ProbeAuthorization",
                    evidence.FirstOrDefault()?.Id)
            },
            evidence);
    }
}
