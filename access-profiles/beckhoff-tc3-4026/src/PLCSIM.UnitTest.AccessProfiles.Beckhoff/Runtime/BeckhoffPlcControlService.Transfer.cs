using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.References;
using TwinCAT.Ads;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;

internal sealed partial class BeckhoffPlcControlService
{
    internal TimeSpan TransferObservationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task TransferAsync(RuntimeTargetRef runtime, int plcPort,
        Func<string, CancellationToken, Task> activate,
        Func<string, CancellationToken, Task> download, CancellationToken cancellationToken)
    {
        EnsureRuntimeActive(cancellationToken);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureRuntimeActive(cancellationToken);
            if (projectOwnership != ProjectOwnershipMode.ApplicationManaged)
                throw new NotSupportedException("An externally managed session cannot activate or download a project.");
            var descriptor = prepared ?? throw new InvalidOperationException("Prepare a Runtime before transferring a project.");
            if (runtime.Id != descriptor.Result.RuntimeTarget.Id)
                throw new ArgumentException("The transfer target is not the prepared Runtime.");
            if (plcPort is < 851 or > 65535 || descriptor.ConfiguredPlcPort is { } configured && configured != plcPort)
                throw new ArgumentException("The selected project port conflicts with the prepared Runtime configuration.");
            var active = observation ?? throw new InvalidOperationException("Reconnect Runtime access before transferring a project.");
            await VerifyTransferIdentityAsync(active, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                SystemObservation baseline;
                await active.Io.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    active.PlcObservationSuspended = true;
                    active.LastPlc = null;
                    // Retain the effective endpoint even when deployment is interrupted.
                    prepared = active.Descriptor = descriptor = descriptor with { PlcPort = plcPort };
                    await active.Access.BindPlcPortAsync(plcPort).ConfigureAwait(false);
                    await active.Access.ReleasePlcAccessAsync().ConfigureAwait(false);
                    await ObserveAsync(active, false, cancellationToken).ConfigureAwait(false);
                    Publish(active);
                    baseline = active.SystemMonitor.Latest;
                    if (baseline.Value is null || active.Quarantined)
                        throw new IOException("No verified system observation is available before activation.");
                }
                finally { active.Io.Release(); }
                // The system monitor remains active; never hold its IO gate across engineering.
                Logger.LogDebug("Activating configuration: Target={NetId}:{Port}", descriptor.Local.NetId, plcPort);
                await activate(descriptor.Local.NetId, cancellationToken).ConfigureAwait(false);
                active.OnPlcStateChanged(this, EventArgs.Empty);
                await active.SystemMonitor.WaitForRunAsync(baseline, true, TransferObservationTimeout, cancellationToken).ConfigureAwait(false);
                Logger.LogDebug("System restart observation confirmed RUN: Target={NetId}:10000", descriptor.Local.NetId);
                await VerifyTransferIdentityAsync(active, cancellationToken).ConfigureAwait(false);
                if (!active.SystemMonitor.Latest.IsRun)
                    throw new IOException("System readiness was lost before Login.");
                await download(descriptor.Local.NetId, cancellationToken).ConfigureAwait(false);
                Logger.LogDebug("Engineering download returned: Target={NetId}:{Port}", descriptor.Local.NetId, plcPort);

                await active.Io.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    baseline = active.SystemMonitor.Latest;
                    active.LastPlc = null;
                    active.PlcObservationSuspended = false;
                }
                finally { active.Io.Release(); }
                active.OnPlcStateChanged(this, EventArgs.Empty);
                await WaitForTransferredApplicationAsync(active, baseline, cancellationToken).ConfigureAwait(false);
                await VerifyTransferIdentityAsync(active, cancellationToken).ConfigureAwait(false);
                Logger.LogInformation("Transfer completed and application observed: Target={NetId}:{Port}", descriptor.Local.NetId, plcPort);
            }
            finally
            {
                await active.Io.WaitAsync().ConfigureAwait(false);
                try
                {
                    active.PlcObservationSuspended = false;
                    active.IdentityVerified = false;
                    await ObserveAsync(active, true, CancellationToken.None).ConfigureAwait(false);
                    Publish(active);
                }
                finally { active.Io.Release(); }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _ = Fail<object>("TransferFailed", error, DiagnosticPhase.ProjectTransfer);
            throw;
        }
        finally { operations.Release(); }
    }

    private async Task VerifyTransferIdentityAsync(RuntimeObservationContext active, CancellationToken token)
    {
        await active.Io.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _ = VerifyIdentity(await active.Access.ReadIdentityAsync(token).ConfigureAwait(false),
                active.Descriptor.Local.NetId, active.Descriptor.Local, token);
        }
        catch (InvalidDataException error)
        {
            active.Quarantined = true;
            active.SystemObserved = false;
            active.Problem = error.Message;
            active.ProblemCode = "IdentityInvalid";
            active.SystemMonitor.Invalidate(error.Message);
            Publish(active);
            throw;
        }
        finally { active.Io.Release(); }
    }

    private async Task WaitForTransferredApplicationAsync(RuntimeObservationContext active,
        SystemObservation baseline, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(TransferObservationTimeout, Context.TimeProvider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            while (true)
            {
                await active.SystemMonitor.WaitForRunAsync(baseline, false, TransferObservationTimeout, cancellation.Token).ConfigureAwait(false);
                await active.Io.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    baseline = active.SystemMonitor.Latest;
                    if (baseline.IsRun && active.Problem is null && active.LastPlc is AdsState.Run or AdsState.Stop)
                        return;
                }
                finally { active.Io.Release(); }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("TwinCAT transfer did not reach an accessible PLC application.");
        }
    }
}
