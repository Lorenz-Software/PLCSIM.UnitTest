using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V15_1;
using PLCSIM.UnitTest.Common.Logging;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V15_1;

internal sealed class RecordingV15_1EnvironmentProbe : ITiaOpennessV15_1EnvironmentProbe
{
    private readonly TiaOpennessV15_1EnvironmentProbeResult result;

    public RecordingV15_1EnvironmentProbe(TiaOpennessV15_1EnvironmentProbeResult result)
    {
        this.result = result;
    }

    public bool WasCalled { get; private set; }

    public int CallCount { get; private set; }

    public Task<TiaOpennessV15_1EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WasCalled = true;
        CallCount++;
        return Task.FromResult(result);
    }
}

internal sealed class RecordingV15_1EngineeringSessionHandle : ITiaOpennessV15_1EngineeringSessionHandle
{
    private readonly Exception? closeException;
    private readonly Exception? discoveryException;
    private readonly Exception? buildException;
    private readonly Exception? transferException;
    private readonly IReadOnlyList<TiaOpennessV15_1TargetProbe> targets;
    private readonly TiaOpennessV15_1BuildHostResult buildResult;
    private readonly TiaOpennessV15_1TransferHostResult transferResult;

    public RecordingV15_1EngineeringSessionHandle(
        string sessionId,
        Exception? closeException = null,
        Exception? discoveryException = null,
        Exception? buildException = null,
        Exception? transferException = null,
        string? projectSource = null,
        IReadOnlyList<TiaOpennessV15_1TargetProbe>? targets = null,
        TiaOpennessV15_1BuildHostResult? buildResult = null,
        TiaOpennessV15_1TransferHostResult? transferResult = null)
    {
        SessionId = sessionId;
        ProjectSource = projectSource ?? "sample.ap15_1";
        this.closeException = closeException;
        this.discoveryException = discoveryException;
        this.buildException = buildException;
        this.transferException = transferException;
        this.targets = targets ?? new[] { TiaOpennessV15_1TestSupport.CreateTarget("recording-plc") };
        this.buildResult = buildResult ?? new TiaOpennessV15_1BuildHostResult(
            TiaOperationStatus.Succeeded,
            0,
            0,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>());
        this.transferResult = transferResult ?? new TiaOpennessV15_1TransferHostResult(
            TiaOperationStatus.Succeeded,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>());
    }

    public string SessionId { get; }

    public string ProjectSource { get; }

    public string ProductVersion { get; } = "15.1";

    public string ProjectVersion { get; } = "15.1";

    public int CloseCount { get; private set; }

    public int TransferCount { get; private set; }

    public Task<TiaOpennessV15_1TargetDiscoveryHostResult> DiscoverTargetsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (discoveryException is not null)
        {
            throw discoveryException;
        }

        return Task.FromResult(TiaOpennessV15_1TargetDiscoveryHostResult.SucceededResult(targets));
    }

    public Task<TiaOpennessV15_1BuildHostResult> BuildAsync(
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

    public Task<TiaOpennessV15_1SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TiaOpennessV15_1SafetyAuthorizationRequirementHostResult(
            TiaOperationStatus.Succeeded,
            TiaSafetyProgramAuthorizationRequirement.NotRequired,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>()));
    }

    public Task<TiaOpennessV15_1SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TiaOpennessV15_1SafetyAuthorizationHostResult(
            TiaOperationStatus.Succeeded,
            true,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>()));
    }
    public Task<TiaOpennessV15_1TransferHostResult> TransferAsync(
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

internal static class TiaOpennessV15_1TestSupport
{
    public static OperationalLoggingOptions DefaultOperationalLogging { get; } = new(
        Path.GetTempPath(),
        LogLevel.Information,
        10 * 1024 * 1024,
        5);

    public static TiaOpennessV15_1TargetProbe CreateTarget(string id)
    {
        return new TiaOpennessV15_1TargetProbe(
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
