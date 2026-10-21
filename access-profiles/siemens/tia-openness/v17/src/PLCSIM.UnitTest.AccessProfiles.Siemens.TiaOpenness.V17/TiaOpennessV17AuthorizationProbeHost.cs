using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V17;

internal interface ITiaOpennessV17AuthorizationProbeHost
{
    Task<TiaOpennessV17AuthorizationProbeHostResult> ProbeAsync(
        string publicApiDirectory,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken);
}

internal sealed class TiaOpennessV17AuthorizationProbeHost : ITiaOpennessV17AuthorizationProbeHost
{
    public async Task<TiaOpennessV17AuthorizationProbeHostResult> ProbeAsync(
        string publicApiDirectory,
        SiemensBindingContext bindingContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(bindingContext);

        var workerPath = TiaOpennessV17WorkerProcessHost.ResolveDeployedWorkerPath();
        if (!File.Exists(workerPath))
        {
            return TiaOpennessV17AuthorizationProbeHostResult.Failed(
                TiaAuthorizationProbeStatus.Unavailable,
                "TiaOpenness.V17.AuthorizationProbeWorkerMissing",
                "TIA Openness V17 authorization-probe worker executable was not found.",
                new[]
                {
                    new TiaEvidence(
                        "tia-openness-v17-authorization-probe-worker",
                        "AuthorizationProbeWorker",
                        workerPath)
                });
        }

        var pipeName = TiaOpennessV17WorkerProcessHost.CreatePipeName();
        var workerInstanceId = Guid.NewGuid().ToString("N");
        var startInfo = TiaOpennessV17WorkerProcessHost.CreateAuthorizationProbeStartInfo(
            workerPath,
            pipeName,
            publicApiDirectory,
            bindingContext,
            workerInstanceId);

        TiaOpennessV17WorkerProcessLifetime? processLifetime = null;
        try
        {
            var processStart = await new TiaOpennessV17WorkerProcessHost()
                .StartAsync(startInfo, pipeName, cancellationToken)
                .ConfigureAwait(false);
            if (!processStart.Succeeded || processStart.Lifetime is null)
            {
                return TiaOpennessV17AuthorizationProbeHostResult.Failed(
                    MapStartFailure(processStart.Code),
                    processStart.Code,
                    processStart.Message,
                    processStart.Evidence);
            }

            processLifetime = processStart.Lifetime;
            var initialResponse = TiaOpennessV17WorkerProtocol.ParseMessage(
                processStart.InitialResponse ?? string.Empty);
            if (!initialResponse.IsProtocolVersionSupported)
            {
                processLifetime.Terminate();
                return TiaOpennessV17AuthorizationProbeHostResult.Failed(
                    TiaAuthorizationProbeStatus.Failed,
                    "TiaOpenness.V17.AuthorizationProbeInvalidResponse",
                    "TIA Openness V17 authorization-probe worker returned an invalid response.",
                    Array.Empty<TiaEvidence>());
            }

            if (string.Equals(initialResponse.Type, "Error", StringComparison.OrdinalIgnoreCase))
            {
                var code = ReadOptional(initialResponse.Values, "code")
                    ?? "TiaOpenness.V17.AuthorizationProbeFailed";
                var message = ReadOptional(initialResponse.Values, "message")
                    ?? "TIA Openness V17 authorization probe failed.";
                await processLifetime.CompleteFailedStartAsync(cancellationToken).ConfigureAwait(false);
                return TiaOpennessV17AuthorizationProbeHostResult.Failed(
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
                return TiaOpennessV17AuthorizationProbeHostResult.Failed(
                    TiaAuthorizationProbeStatus.Failed,
                    "TiaOpenness.V17.AuthorizationProbeUnexpectedResponse",
                    "TIA Openness V17 authorization-probe worker returned an unexpected response.",
                    Array.Empty<TiaEvidence>());
            }

            var evidence = new[]
            {
                new TiaEvidence(
                    "tia-openness-v17-authorization-probe-worker",
                    "AuthorizationProbeWorker",
                    workerPath),
                new TiaEvidence(
                    "tia-openness-v17-authorization-probe-public-api",
                    "PublicApiDirectory",
                    publicApiDirectory)
            };

            try
            {
                await new TiaOpennessV17WorkerCommandDispatcher(processLifetime)
                    .CloseAsync(cancellationToken)
                    .ConfigureAwait(false);
                processLifetime = null;
                return TiaOpennessV17AuthorizationProbeHostResult.Authorized(evidence);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                processLifetime = null;
                return TiaOpennessV17AuthorizationProbeHostResult.Failed(
                    TiaAuthorizationProbeStatus.CleanupFailed,
                    "TiaOpenness.V17.AuthorizationProbeCleanupFailed",
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
            return TiaOpennessV17AuthorizationProbeHostResult.Failed(
                TiaAuthorizationProbeStatus.Failed,
                "TiaOpenness.V17.AuthorizationProbeFailed",
                "TIA Openness V17 authorization probe failed: " + ex.Message,
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
        return string.Equals(code, "TiaOpenness.V17.SecurityDenied", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "TiaOpenness.V17.AccessDenied", StringComparison.OrdinalIgnoreCase)
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

internal sealed record TiaOpennessV17AuthorizationProbeHostResult(
    TiaAuthorizationProbeStatus Status,
    IReadOnlyList<TiaDiagnostic> Diagnostics,
    IReadOnlyList<TiaEvidence> Evidence)
{
    public static TiaOpennessV17AuthorizationProbeHostResult Authorized(
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV17AuthorizationProbeHostResult(
            TiaAuthorizationProbeStatus.Authorized,
            Array.Empty<TiaDiagnostic>(),
            evidence);
    }

    public static TiaOpennessV17AuthorizationProbeHostResult Failed(
        TiaAuthorizationProbeStatus status,
        string code,
        string message,
        IReadOnlyList<TiaEvidence> evidence)
    {
        return new TiaOpennessV17AuthorizationProbeHostResult(
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
