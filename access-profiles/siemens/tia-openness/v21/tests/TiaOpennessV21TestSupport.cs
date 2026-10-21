using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21;
using PLCSIM.UnitTest.Common.Logging;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V21;

internal sealed class RecordingV21EnvironmentProbe : ITiaOpennessV21EnvironmentProbe
{
    private readonly TiaOpennessV21EnvironmentProbeResult result;

    public RecordingV21EnvironmentProbe(TiaOpennessV21EnvironmentProbeResult result)
    {
        this.result = result;
    }

    public bool WasCalled { get; private set; }

    public int CallCount { get; private set; }

    public Task<TiaOpennessV21EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WasCalled = true;
        CallCount++;
        return Task.FromResult(result);
    }
}

internal sealed class RecordingV21EngineeringSessionHandle : ITiaOpennessV21EngineeringSessionHandle
{
    private readonly Exception? closeException;
    private readonly Exception? discoveryException;
    private readonly Exception? buildException;
    private readonly Exception? transferException;
    private readonly IReadOnlyList<TiaOpennessV21TargetProbe> targets;
    private readonly TiaOpennessV21BuildHostResult buildResult;
    private readonly TiaOpennessV21TransferHostResult transferResult;
    private readonly TiaOpennessV21SafetyAuthorizationRequirementHostResult safetyRequirementResult;
    private readonly TiaOpennessV21SafetyAuthorizationHostResult safetyAuthorizationResult;

    public RecordingV21EngineeringSessionHandle(
        string sessionId,
        Exception? closeException = null,
        Exception? discoveryException = null,
        Exception? buildException = null,
        Exception? transferException = null,
        string? projectSource = null,
        IReadOnlyList<TiaOpennessV21TargetProbe>? targets = null,
        TiaOpennessV21BuildHostResult? buildResult = null,
        TiaOpennessV21TransferHostResult? transferResult = null,
        TiaOpennessV21SafetyAuthorizationRequirementHostResult? safetyRequirementResult = null,
        TiaOpennessV21SafetyAuthorizationHostResult? safetyAuthorizationResult = null)
    {
        SessionId = sessionId;
        ProjectSource = projectSource ?? "sample.ap21";
        this.closeException = closeException;
        this.discoveryException = discoveryException;
        this.buildException = buildException;
        this.transferException = transferException;
        this.targets = targets ?? new[] { TiaOpennessV21TestSupport.CreateTarget("recording-plc") };
        this.buildResult = buildResult ?? new TiaOpennessV21BuildHostResult(
            TiaOperationStatus.Succeeded,
            0,
            0,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>());
        this.transferResult = transferResult ?? new TiaOpennessV21TransferHostResult(
            TiaOperationStatus.Succeeded,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>());
        this.safetyRequirementResult = safetyRequirementResult
            ?? new TiaOpennessV21SafetyAuthorizationRequirementHostResult(
                TiaOperationStatus.Succeeded,
                TiaSafetyProgramAuthorizationRequirement.NotRequired,
                Array.Empty<TiaDiagnostic>(),
                Array.Empty<TiaEvidence>());
        this.safetyAuthorizationResult = safetyAuthorizationResult
            ?? new TiaOpennessV21SafetyAuthorizationHostResult(
                TiaOperationStatus.Succeeded,
                true,
                Array.Empty<TiaDiagnostic>(),
                Array.Empty<TiaEvidence>());
    }

    public string SessionId { get; }

    public string ProjectSource { get; }

    public string ProductVersion { get; } = "21";

    public string ProjectVersion { get; } = "21";

    public int CloseCount { get; private set; }

    public int TransferCount { get; private set; }

    public int SafetyRequirementCount { get; private set; }

    public int SafetyAuthorizationCount { get; private set; }

    public Task<TiaOpennessV21TargetDiscoveryHostResult> DiscoverTargetsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (discoveryException is not null)
        {
            throw discoveryException;
        }

        return Task.FromResult(TiaOpennessV21TargetDiscoveryHostResult.SucceededResult(targets));
    }

    public Task<TiaOpennessV21BuildHostResult> BuildAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buildException is not null)
        {
            throw buildException;
        }

        return Task.FromResult(buildResult);
    }

    public Task<TiaOpennessV21SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SafetyRequirementCount++;
        return Task.FromResult(safetyRequirementResult);
    }

    public Task<TiaOpennessV21SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SafetyAuthorizationCount++;
        return Task.FromResult(safetyAuthorizationResult);
    }

    public Task<TiaOpennessV21TransferHostResult> TransferAsync(
        TiaProjectTargetRef projectTarget,
        TiaRuntimeTransferTarget runtimeTarget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransferCount++;
        if (transferException is not null)
        {
            throw transferException;
        }

        return Task.FromResult(transferResult);
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseCount++;
        if (closeException is not null)
        {
            throw closeException;
        }

        return Task.CompletedTask;
    }
}

internal static class TiaOpennessV21TestSupport
{
    public static OperationalLoggingOptions DefaultOperationalLogging { get; } = new(
        Path.GetTempPath(),
        LogLevel.Information,
        10 * 1024 * 1024,
        5);

    public static TiaOpennessV21TargetProbe CreateTarget(string id)
    {
        return new TiaOpennessV21TargetProbe(
            id,
            id,
            "Project/" + id,
            "S7-1500",
            "OrderNumber:6ES7 515-2AN03-0AB0/V4.1",
            "OrderNumber:6ES7 515-2AN03-0AB0/V4.1",
            "CPU",
            true);
    }
}
