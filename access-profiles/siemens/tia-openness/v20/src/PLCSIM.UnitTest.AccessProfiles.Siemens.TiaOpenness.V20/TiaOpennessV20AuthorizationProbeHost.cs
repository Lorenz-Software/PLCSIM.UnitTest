using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20;

internal interface ITiaOpennessV20AuthorizationProbeHost
{
    Task<TiaOpennessV20AuthorizationProbeHostResult> ProbeAsync(
        string publicApiDirectory,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken);
}

internal sealed class TiaOpennessV20AuthorizationProbeHost : ITiaOpennessV20AuthorizationProbeHost
{
    public async Task<TiaOpennessV20AuthorizationProbeHostResult> ProbeAsync(
        string publicApiDirectory,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(bindingContext);

        var workerPath = TiaOpennessV20WorkerProcessHost.ResolveDeployedWorkerPath();
        if (!File.Exists(workerPath))
        {
            return TiaOpennessV20AuthorizationProbeHostResult.Failed(
                TiaAuthorizationProbeStatus.Unavailable,
                "TiaOpenness.V20.AuthorizationProbeWorkerMissing",
                "TIA Openness V20 authorization-probe worker executable was not found.",
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v20-authorization-probe-worker",
                        "AuthorizationProbeWorker",
                        workerPath)
                });
        }

        var pipeName = TiaOpennessV20WorkerProcessHost.CreatePipeName();
        var workerInstanceId = Guid.NewGuid().ToString("N");
        var startInfo = TiaOpennessV20WorkerProcessHost.CreateAuthorizationProbeStartInfo(
            workerPath,
            pipeName,
            publicApiDirectory,
            bindingContext,
            workerInstanceId);

        TiaOpennessV20WorkerProcessLifetime? processLifetime = null;
        try
        {
            var processStart = await new TiaOpennessV20WorkerProcessHost()
                .StartAsync(startInfo, pipeName, cancellationToken)
                .ConfigureAwait(false);
            if (!processStart.Succeeded || processStart.Lifetime is null)
            {
                return TiaOpennessV20AuthorizationProbeHostResult.Failed(
                    MapStartFailure(processStart.Code),
                    processStart.Code,
                    processStart.Message,
                    processStart.Evidence);
            }

            processLifetime = processStart.Lifetime;
            var initialResponse = TiaOpennessV20WorkerProtocol.ParseMessage(
                processStart.InitialResponse ?? string.Empty);
            if (!initialResponse.IsProtocolVersionSupported)
            {
                processLifetime.Terminate();
                return TiaOpennessV20AuthorizationProbeHostResult.Failed(
                    TiaAuthorizationProbeStatus.Failed,
                    "TiaOpenness.V20.AuthorizationProbeInvalidResponse",
                    "TIA Openness V20 authorization-probe worker returned an invalid response.",
                    Array.Empty<TiaEvidence>());
            }

            if (string.Equals(initialResponse.Type, "Error", StringComparison.OrdinalIgnoreCase))
            {
                var code = ReadOptional(initialResponse.Values, "code")
                    ?? "TiaOpenness.V20.AuthorizationProbeFailed";
                var message = ReadOptional(initialResponse.Values, "message")
                    ?? "TIA Openness V20 authorization probe failed.";
                await processLifetime.CompleteFailedStartAsync(cancellationToken).ConfigureAwait(false);
                return TiaOpennessV20AuthorizationProbeHostResult.Failed(
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
                return TiaOpennessV20AuthorizationProbeHostResult.Failed(
                    TiaAuthorizationProbeStatus.Failed,
                    "TiaOpenness.V20.AuthorizationProbeUnexpectedResponse",
                    "TIA Openness V20 authorization-probe worker returned an unexpected response.",
                    Array.Empty<TiaEvidence>());
            }

            var evidence = new[]
            {
                new TiaEvidence(
                    "tia-openness-v20-authorization-probe-worker",
                    "AuthorizationProbeWorker",
                    workerPath),
                new TiaEvidence(
                    "tia-openness-v20-authorization-probe-public-api",
                    "PublicApiDirectory",
                    publicApiDirectory)
            };

            try
            {
                await new TiaOpennessV20WorkerCommandDispatcher(processLifetime)
                    .CloseAsync(cancellationToken)
                    .ConfigureAwait(false);
                processLifetime = null;
                return TiaOpennessV20AuthorizationProbeHostResult.Authorized(evidence);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                processLifetime = null;
                return TiaOpennessV20AuthorizationProbeHostResult.Failed(
                    TiaAuthorizationProbeStatus.CleanupFailed,
                    "TiaOpenness.V20.AuthorizationProbeCleanupFailed",
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
            return TiaOpennessV20AuthorizationProbeHostResult.Failed(
                TiaAuthorizationProbeStatus.Failed,
                "TiaOpenness.V20.AuthorizationProbeFailed",
                "TIA Openness V20 authorization probe failed: " + ex.Message,
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
        return string.Equals(code, "TiaOpenness.V20.SecurityDenied", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "TiaOpenness.V20.AccessDenied", StringComparison.OrdinalIgnoreCase)
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

internal sealed record TiaOpennessV20AuthorizationProbeHostResult(
    TiaAuthorizationProbeStatus Status,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public static TiaOpennessV20AuthorizationProbeHostResult Authorized(
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV20AuthorizationProbeHostResult(
            TiaAuthorizationProbeStatus.Authorized,
            Array.Empty<TiaDiagnostic>(),
            evidence);
    }

    public static TiaOpennessV20AuthorizationProbeHostResult Failed(
        TiaAuthorizationProbeStatus status,
        string code,
        string message,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV20AuthorizationProbeHostResult(
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
