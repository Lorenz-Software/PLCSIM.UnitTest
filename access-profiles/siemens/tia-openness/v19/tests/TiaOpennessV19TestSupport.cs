using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V19;
using PLCSIM.UnitTest.Common.Logging;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V19;

internal sealed class RecordingV19EnvironmentProbe : ITiaOpennessV19EnvironmentProbe
{
    private readonly TiaOpennessV19EnvironmentProbeResult result;

    public RecordingV19EnvironmentProbe(TiaOpennessV19EnvironmentProbeResult result)
    {
        this.result = result;
    }

    public bool WasCalled { get; private set; }

    public int CallCount { get; private set; }

    public Task<TiaOpennessV19EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WasCalled = true;
        CallCount++;
        return Task.FromResult(result);
    }
}

internal sealed class RecordingV19EngineeringSessionHandle : ITiaOpennessV19EngineeringSessionHandle
{
    private readonly Exception? closeException;
    private readonly Exception? discoveryException;
    private readonly Exception? buildException;
    private readonly Exception? transferException;
    private readonly IReadOnlyList<TiaOpennessV19TargetProbe> targets;
    private readonly TiaOpennessV19BuildHostResult buildResult;
    private readonly TiaOpennessV19TransferHostResult transferResult;

    public RecordingV19EngineeringSessionHandle(
        string sessionId,
        Exception? closeException = null,
        Exception? discoveryException = null,
        Exception? buildException = null,
        Exception? transferException = null,
        string? projectSource = null,
        IReadOnlyList<TiaOpennessV19TargetProbe>? targets = null,
        TiaOpennessV19BuildHostResult? buildResult = null,
        TiaOpennessV19TransferHostResult? transferResult = null)
    {
        SessionId = sessionId;
        ProjectSource = projectSource ?? "sample.ap19";
        this.closeException = closeException;
        this.discoveryException = discoveryException;
        this.buildException = buildException;
        this.transferException = transferException;
        this.targets = targets ?? new[] { TiaOpennessV19TestSupport.CreateTarget("recording-plc") };
        this.buildResult = buildResult ?? new TiaOpennessV19BuildHostResult(
            TiaOperationStatus.Succeeded,
            0,
            0,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>());
        this.transferResult = transferResult ?? new TiaOpennessV19TransferHostResult(
            TiaOperationStatus.Succeeded,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>());
    }

    public string SessionId { get; }

    public string ProjectSource { get; }

    public string ProductVersion { get; } = "19";

    public string ProjectVersion { get; } = "19";

    public int CloseCount { get; private set; }

    public int TransferCount { get; private set; }

    public Task<TiaOpennessV19TargetDiscoveryHostResult> DiscoverTargetsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (discoveryException is not null)
        {
            throw discoveryException;
        }

        return Task.FromResult(TiaOpennessV19TargetDiscoveryHostResult.SucceededResult(targets));
    }

    public Task<TiaOpennessV19BuildHostResult> BuildAsync(
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

    public Task<TiaOpennessV19SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TiaOpennessV19SafetyAuthorizationRequirementHostResult(
            TiaOperationStatus.Succeeded,
            TiaSafetyProgramAuthorizationRequirement.NotRequired,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>()));
    }

    public Task<TiaOpennessV19SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TiaOpennessV19SafetyAuthorizationHostResult(
            TiaOperationStatus.Succeeded,
            true,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>()));
    }
    public Task<TiaOpennessV19TransferHostResult> TransferAsync(
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

internal static class TiaOpennessV19TestSupport
{
    public static OperationalLoggingOptions DefaultOperationalLogging { get; } = new(
        Path.GetTempPath(),
        LogLevel.Information,
        10 * 1024 * 1024,
        5);

    public static TiaOpennessV19TargetProbe CreateTarget(string id)
    {
        return new TiaOpennessV19TargetProbe(
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
