using Microsoft.Extensions.Logging;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V18;
using PLCSIM.UnitTest.Common.Logging;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Siemens.TiaOpenness.V18;

internal sealed class RecordingV18EnvironmentProbe : ITiaOpennessV18EnvironmentProbe
{
    private readonly TiaOpennessV18EnvironmentProbeResult result;

    public RecordingV18EnvironmentProbe(TiaOpennessV18EnvironmentProbeResult result)
    {
        this.result = result;
    }

    public bool WasCalled { get; private set; }

    public int CallCount { get; private set; }

    public Task<TiaOpennessV18EnvironmentProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WasCalled = true;
        CallCount++;
        return Task.FromResult(result);
    }
}

internal sealed class RecordingV18EngineeringSessionHandle : ITiaOpennessV18EngineeringSessionHandle
{
    private readonly Exception? closeException;
    private readonly Exception? discoveryException;
    private readonly Exception? buildException;
    private readonly Exception? transferException;
    private readonly IReadOnlyList<TiaOpennessV18TargetProbe> targets;
    private readonly TiaOpennessV18BuildHostResult buildResult;
    private readonly TiaOpennessV18TransferHostResult transferResult;

    public RecordingV18EngineeringSessionHandle(
        string sessionId,
        Exception? closeException = null,
        Exception? discoveryException = null,
        Exception? buildException = null,
        Exception? transferException = null,
        string? projectSource = null,
        IReadOnlyList<TiaOpennessV18TargetProbe>? targets = null,
        TiaOpennessV18BuildHostResult? buildResult = null,
        TiaOpennessV18TransferHostResult? transferResult = null)
    {
        SessionId = sessionId;
        ProjectSource = projectSource ?? "sample.ap18";
        this.closeException = closeException;
        this.discoveryException = discoveryException;
        this.buildException = buildException;
        this.transferException = transferException;
        this.targets = targets ?? new[] { TiaOpennessV18TestSupport.CreateTarget("recording-plc") };
        this.buildResult = buildResult ?? new TiaOpennessV18BuildHostResult(
            TiaOperationStatus.Succeeded,
            0,
            0,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>());
        this.transferResult = transferResult ?? new TiaOpennessV18TransferHostResult(
            TiaOperationStatus.Succeeded,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>());
    }

    public string SessionId { get; }

    public string ProjectSource { get; }

    public string ProductVersion { get; } = "18";

    public string ProjectVersion { get; } = "18";

    public int CloseCount { get; private set; }

    public int TransferCount { get; private set; }

    public Task<TiaOpennessV18TargetDiscoveryHostResult> DiscoverTargetsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (discoveryException is not null)
        {
            throw discoveryException;
        }

        return Task.FromResult(TiaOpennessV18TargetDiscoveryHostResult.SucceededResult(targets));
    }

    public Task<TiaOpennessV18BuildHostResult> BuildAsync(
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

    public Task<TiaOpennessV18SafetyAuthorizationRequirementHostResult> GetSafetyProgramAuthorizationRequirementAsync(
        TiaProjectTargetRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TiaOpennessV18SafetyAuthorizationRequirementHostResult(
            TiaOperationStatus.Succeeded,
            TiaSafetyProgramAuthorizationRequirement.NotRequired,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>()));
    }

    public Task<TiaOpennessV18SafetyAuthorizationHostResult> AuthorizeSafetyProgramAsync(
        TiaProjectTargetRef target,
        System.Security.SecureString password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TiaOpennessV18SafetyAuthorizationHostResult(
            TiaOperationStatus.Succeeded,
            true,
            Array.Empty<TiaDiagnostic>(),
            Array.Empty<TiaEvidence>()));
    }
    public Task<TiaOpennessV18TransferHostResult> TransferAsync(
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

internal static class TiaOpennessV18TestSupport
{
    public static OperationalLoggingOptions DefaultOperationalLogging { get; } = new(
        Path.GetTempPath(),
        LogLevel.Information,
        10 * 1024 * 1024,
        5);

    public static TiaOpennessV18TargetProbe CreateTarget(string id)
    {
        return new TiaOpennessV18TargetProbe(
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
