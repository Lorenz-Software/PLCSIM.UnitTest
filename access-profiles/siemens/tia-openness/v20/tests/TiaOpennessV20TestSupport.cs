using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V20;
using PLCSIM.UnitTest.Common.Logging;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V20;

internal sealed class RecordingV20EnvironmentProbe : ITiaOpennessV20EnvironmentProbe
{
    private readonly TiaOpennessV20EnvironmentProbeResult result;

    public RecordingV20EnvironmentProbe(TiaOpennessV20EnvironmentProbeResult result)
    {
        this.result = result;
    }

    public bool WasCalled { get; private set; }

    public int CallCount { get; private set; }

    public Task<TiaOpennessV20EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WasCalled = true;
        CallCount++;
        return Task.FromResult(result);
    }
}

internal sealed class RecordingV20EngineeringSessionHandle : ITiaOpennessV20EngineeringSessionHandle
{
    private readonly Exception? closeException;
    private readonly Exception? discoveryException;
    private readonly Exception? buildException;
    private readonly Exception? transferException;
    private readonly IReadOnlyList<TiaOpennessV20TargetProbe> targets;
    private readonly TiaOpennessV20BuildHostResult buildResult;
    private readonly TiaOpennessV20TransferHostResult transferResult;

    public RecordingV20EngineeringSessionHandle(
        string sessionId,
        Exception? closeException = null,
        Exception? discoveryException = null,
        Exception? buildException = null,
        Exception? transferException = null,
        string? projectSource = null,
        IReadOnlyList<TiaOpennessV20TargetProbe>? targets = null,
        TiaOpennessV20BuildHostResult? buildResult = null,
        TiaOpennessV20TransferHostResult? transferResult = null)
    {
        SessionId = sessionId;
        ProjectSource = projectSource ?? "sample.ap20";
        this.closeException = closeException;
        this.discoveryException = discoveryException;
        this.buildException = buildException;
        this.transferException = transferException;
        this.targets = targets ?? new[] { TiaOpennessV20TestSupport.CreateTarget("recording-plc") };
        this.buildResult = buildResult ?? new TiaOpennessV20BuildHostResult(
            TiaOperationStatus.Succeeded,
            0,
            0,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>());
        this.transferResult = transferResult ?? new TiaOpennessV20TransferHostResult(
            TiaOperationStatus.Succeeded,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>());
    }

    public string SessionId { get; }

    public string ProjectSource { get; }

    public string ProductVersion { get; } = "20";

    public string ProjectVersion { get; } = "20";

    public int CloseCount { get; private set; }

    public int TransferCount { get; private set; }

    public Task<TiaOpennessV20TargetDiscoveryHostResult> DiscoverTargetsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (discoveryException is not null)
        {
            throw discoveryException;
        }

        return Task.FromResult(TiaOpennessV20TargetDiscoveryHostResult.SucceededResult(targets));
    }

    public Task<TiaOpennessV20BuildHostResult> BuildAsync(
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

    public Task<TiaOpennessV20SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TiaOpennessV20SafetyAuthorizationRequirementHostResult(
            TiaOperationStatus.Succeeded,
            TiaSafetyProgramAuthorizationRequirement.NotRequired,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>()));
    }

    public Task<TiaOpennessV20SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TiaOpennessV20SafetyAuthorizationHostResult(
            TiaOperationStatus.Succeeded,
            true,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>()));
    }
    public Task<TiaOpennessV20TransferHostResult> TransferAsync(
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

internal static class TiaOpennessV20TestSupport
{
    public static OperationalLoggingOptions DefaultOperationalLogging { get; } = new(
        Path.GetTempPath(),
        LogLevel.Information,
        10 * 1024 * 1024,
        5);

    public static TiaOpennessV20TargetProbe CreateTarget(string id)
    {
        return new TiaOpennessV20TargetProbe(
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
