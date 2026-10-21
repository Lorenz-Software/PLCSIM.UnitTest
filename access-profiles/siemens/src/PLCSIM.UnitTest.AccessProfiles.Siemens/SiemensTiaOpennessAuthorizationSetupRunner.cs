using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Common.PluginLoading;
using PLCSIM.UnitTest.Common.UnitTests;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens;

/// <summary>
/// Profile-owned entry point used by the shipped PowerShell setup script.
/// </summary>
public static class SiemensTiaOpennessAuthorizationSetupRunner
{
    public static async Task<TiaAuthorizationProbeResult> ProbeAsync(
        string packageRootPath,
        string entryAssemblyPath,
        string entryPointTypeName,
        string logDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPointTypeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        PluginPackageLoadScope? packageScope = null;
        ISiemensTechnologyBinding? binding = null;
        TiaAuthorizationProbeResult? result = null;
        Exception? operationFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            packageScope = new PluginPackageLoadScope(
                packageRootPath,
                entryAssemblyPath,
                [
                    typeof(ISiemensTechnologyBinding).Assembly,
                    typeof(UnitTestContractVersions).Assembly,
                    typeof(Diagnostic).Assembly,
                    typeof(ILoggerFactory).Assembly
                ]);
            binding = packageScope.LoadEntryPoint<ISiemensTechnologyBinding>(entryPointTypeName);
            binding.Initialize(new SiemensBindingContext(
                "siemens",
                new OperationalLoggingOptions(
                    Path.GetFullPath(logDirectory),
                    LogLevel.Information,
                    10 * 1024 * 1024,
                    5)));
            if (binding is not ITiaOpennessAuthorizationProbeBinding authorizationProbe)
            {
                throw new InvalidOperationException(
                    "The selected binding does not implement the advertised TIA Openness authorization probe.");
            }

            result = await authorizationProbe.ProbeAuthorizationAsync(
                new TiaAuthorizationProbeRequest(Guid.NewGuid().ToString("N")),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            operationFailure = ex;
        }
        finally
        {
            if (binding is IAsyncDisposable asyncDisposable)
            {
                try
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(ex);
                }
            }
            else if (binding is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(ex);
                }
            }

            if (packageScope is not null)
            {
                try
                {
                    packageScope.Dispose();
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(ex);
                }
            }
        }

        if (cleanupFailures.Count > 0)
        {
            return CreateFailure(
                TiaAuthorizationProbeStatus.CleanupFailed,
                "SiemensProfile.AuthorizationSetup.BindingCleanupFailed",
                string.Join(" | ", cleanupFailures.Select(failure => failure.Message)));
        }

        if (operationFailure is not null)
        {
            return CreateFailure(
                TiaAuthorizationProbeStatus.Failed,
                "SiemensProfile.AuthorizationSetup.BindingProbeFailed",
                operationFailure.GetBaseException().Message);
        }

        return result ?? CreateFailure(
            TiaAuthorizationProbeStatus.Failed,
            "SiemensProfile.AuthorizationSetup.BindingProbeMissingResult",
            "The selected TIA Openness binding returned no authorization-probe result.");
    }

    private static TiaAuthorizationProbeResult CreateFailure(
        TiaAuthorizationProbeStatus status,
        string code,
        string message)
    {
        return new TiaAuthorizationProbeResult(
            status,
            [
                new TiaDiagnostic(
                    code,
                    TiaDiagnosticSeverity.Error,
                    message,
                    "ProbeAuthorization",
                    null)
            ],
            Array.Empty<TiaEvidence>());
    }
}
