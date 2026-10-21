using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff;
using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using PLCSIM.UnitTest.Common.AccessProfiles;
using PLCSIM.UnitTest.Common.Diagnostics;
using PLCSIM.UnitTest.Common.Logging;
using PLCSIM.UnitTest.Common.Operations;
using PLCSIM.UnitTest.Common.Runtime;
using PLCSIM.UnitTest.Common.Services;
using TwinCAT.Ads;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class RuntimeControlTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DataBindingGenerationRetainsObservedInterruption(int kind)
    {
        await using var test = new Fixture(interval: TimeSpan.FromMilliseconds(5));
        var prepared = await test.Prepare();
        var endpoint = await test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default);
        var generation = endpoint.BindingGeneration();
        if (kind == 0) test.Access.InvalidateSystem();
        else if (kind == 1) test.Access.InvalidatePlc();
        else
        {
            if (kind == 2) test.Access.SystemState = AdsState.Config;
            else test.Access.PlcState = AdsState.Stop;
            test.Access.Notify();
        }
        await Until(() => endpoint.BindingGeneration() > generation);
        test.Access.SystemState = AdsState.Run;
        test.Access.PlcState = AdsState.Run;
        test.Access.Notify();
        await Until(endpoint.IsAvailable);
        Assert.True(endpoint.IsCurrent());
        Assert.True(endpoint.BindingGeneration() > generation);
        Assert.Empty(test.Access.Writes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AcceptedPlcCommandSurvivesCancellationWithoutInventingObservedState(bool start, bool duringObservation)
    {
        await using var test = new Fixture();
        test.Access.PlcState = start ? AdsState.Stop : AdsState.Run;
        var prepared = await test.Prepare();
        var before = test.Service.CurrentState;
        using var cancellation = new CancellationTokenSource();
        test.Access.OnWrite = () =>
        {
            if (duringObservation)
                test.Access.ReadSystem = _ => { cancellation.Cancel(); return Task.FromResult(AdsState.Run); };
            else cancellation.Cancel();
        };
        var result = start
            ? await test.Service.StartAsync(prepared.Runtime, prepared.RuntimeTarget, cancellation.Token)
            : await test.Service.StopAsync(prepared.Runtime, prepared.RuntimeTarget, cancellation.Token);
        Assert.True(result.IsSuccessful, result.Message);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(before, result.Value);
        Assert.Equal(start ? AdsState.Run : AdsState.Stop, Assert.Single(test.Access.Writes));
        test.Access.ReadSystem = null;
        test.Access.Notify();
        await Until(() => test.Service.CurrentState.OperatingState ==
            (start ? PlcOperatingState.Running : PlcOperatingState.Stopped));
        Assert.Single(test.Access.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledUnconfirmedPlcWriteIsNotSuccess(bool explicitFailure)
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        using var cancellation = new CancellationTokenSource();
        test.Access.OnWrite = cancellation.Cancel;
        test.Access.WriteFailure = explicitFailure
            ? new AdsAccessException("Write rejected", AdsErrorCode.DeviceInvalidState)
            : new OperationCanceledException(cancellation.Token);
        if (explicitFailure)
        {
            var result = await test.Service.StopAsync(prepared.Runtime, prepared.RuntimeTarget, cancellation.Token);
            Assert.False(result.IsSuccessful);
            Assert.Contains("Write rejected", result.Message);
        }
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Service.StopAsync(prepared.Runtime, prepared.RuntimeTarget, cancellation.Token));
        Assert.Single(test.Access.Writes);
    }

    [Fact]
    public async Task CancelledPlcCommandBeforeDispatchDoesNotWrite()
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Service.StopAsync(prepared.Runtime, prepared.RuntimeTarget, new CancellationToken(true)));
        Assert.Empty(test.Access.Writes);
    }

    [Fact]
    public async Task FailedPostCommandObservationPreservesAcceptanceButNotHealthyState()
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        test.Access.OnWrite = () => test.Access.SystemFailure = new IOException("Observation unavailable");
        var result = await test.Service.StopAsync(prepared.Runtime, prepared.RuntimeTarget, default);
        Assert.True(result.IsSuccessful);
        Assert.False(result.Value!.ReadyForExecution);
        Assert.Equal(PlcOperatingState.Unavailable, result.Value.OperatingState);
        Assert.NotEqual(AccessHealthState.Healthy, test.Service.Health.State);
        Assert.Single(test.Access.Writes);
    }

    [Fact]
    public async Task NoOpCommandDoesNotInventWriteAcceptanceOnCancellation()
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        test.Access.ReadSystem = _ =>
        {
            if (++reads == 2) cancellation.Cancel();
            return Task.FromResult(AdsState.Run);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            test.Service.StartAsync(prepared.Runtime, prepared.RuntimeTarget, cancellation.Token));
        Assert.Empty(test.Access.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LossImmediatelyBlocksExistingAndNewDataEndpointsUntilPlcVerification(bool plcOnly)
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        var endpoint = await test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default);
        var systemEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var systemRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plcEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plcRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Access.ReadSystem = async token =>
        {
            systemEntered.TrySetResult();
            await systemRelease.Task.WaitAsync(token);
            return AdsState.Run;
        };
        test.Access.ReadPlc = async token =>
        {
            plcEntered.TrySetResult();
            await plcRelease.Task.WaitAsync(token);
            return AdsState.Run;
        };
        if (plcOnly) test.Access.InvalidatePlc();
        else test.Access.InvalidateSystem();
        Assert.True(endpoint.IsCurrent());
        Assert.False(endpoint.IsAvailable());
        await systemEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default));
        systemRelease.SetResult();
        await plcEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(endpoint.IsAvailable());
        plcRelease.SetResult();
        await Until(endpoint.IsAvailable);
        Assert.True((await test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default)).IsAvailable());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LossDuringObservationCannotReopenDataAccess(bool duringPlcRead)
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        var endpoint = await test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default);
        var nextRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void LoseConnection()
        {
            test.Access.ReadSystem = async token =>
            {
                nextRead.TrySetResult();
                await release.Task.WaitAsync(token);
                return AdsState.Run;
            };
            test.Access.InvalidateSystem();
        }
        if (duringPlcRead) test.Access.AfterPlcRead = LoseConnection;
        else test.Access.ReadSystem = _ => { LoseConnection(); return Task.FromResult(AdsState.Run); };
        test.Access.Notify();
        await nextRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(endpoint.IsAvailable());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default));
        release.SetResult();
        await Until(endpoint.IsAvailable);
    }

    [Fact]
    public async Task IsolatedPlcLossWakesStoppedPlcObservationWithUnchangedSystemRun()
    {
        await using var test = new Fixture();
        test.Access.PlcState = AdsState.Stop;
        var prepared = await test.Prepare();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Access.ReadPlc = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return AdsState.Run;
        };
        test.Access.InvalidatePlc();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(test.Service.CurrentState.ReadyForExecution);
        release.SetResult();
        await Until(() => test.Service.CurrentState.ReadyForExecution);
        Assert.True((await test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default)).IsAvailable());
        Assert.Empty(test.Access.Writes);
        Assert.Empty(test.Diagnostics);
    }

    [Fact]
    public async Task RecoveryToStoppedPlcKeepsDataBlockedButAllowsStart()
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        var endpoint = await test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default);
        test.Access.PlcState = AdsState.Stop;
        test.Access.InvalidateSystem();
        await Until(() => test.Service.CurrentState.OperatingState == PlcOperatingState.Stopped);
        Assert.False(endpoint.IsAvailable());
        Assert.True((await test.Service.StartAsync(prepared.Runtime, prepared.RuntimeTarget, default)).IsSuccessful);
        Assert.True(endpoint.IsAvailable());
    }

    [Fact]
    public async Task SystemMonitoringContinuesThroughoutActivationAndDownloadWithoutPlcReads()
    {
        await using var test = new Fixture(ownership: ProjectOwnershipMode.ApplicationManaged,
            interval: TimeSpan.FromMilliseconds(5));
        var prepared = await test.Prepare();
        var access = test.Access;
        var plcReads = access.PlcReads;
        async Task ObserveDuringEngineering()
        {
            var count = Volatile.Read(ref access.SystemReads);
            await WaitUntilAsync(() => Volatile.Read(ref access.SystemReads) >= count + 2);
            Assert.Equal(plcReads, access.PlcReads);
            Assert.Equal(0, access.Disposals);
            Assert.False(test.Service.CurrentState.ReadyForExecution);
        }
        await test.Service.TransferAsync(prepared.RuntimeTarget, 852, async (_, _) =>
        {
            await ObserveDuringEngineering();
            access.RestartCounter++;
        }, async (_, _) =>
        {
            await ObserveDuringEngineering();
            access.PlcState = AdsState.Stop;
        }, default);
        Assert.Single(test.Opened);
        Assert.Equal(852, access.BoundPort);
        Assert.Equal(PlcOperatingState.Stopped, test.Service.CurrentState.OperatingState);
        Assert.Empty(access.Writes);
    }

    [Fact]
    public async Task UnchangedRunCannotAuthorizeDownloadAndTimeoutKeepsObserverAlive()
    {
        await using var test = new Fixture(ownership: ProjectOwnershipMode.ApplicationManaged,
            interval: TimeSpan.FromMilliseconds(5), transferTimeout: TimeSpan.FromMilliseconds(100));
        var prepared = await test.Prepare();
        var downloads = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => test.Service.TransferAsync(prepared.RuntimeTarget, 852,
            (_, _) => Task.CompletedTask, (_, _) => { downloads++; return Task.CompletedTask; }, default));
        Assert.Equal(0, downloads);
        var reads = test.Access.SystemReads;
        await WaitUntilAsync(() => Volatile.Read(ref test.Access.SystemReads) > reads);
        Assert.Equal(0, test.Access.Disposals);
    }

    [Fact]
    public async Task CancellationDuringRestartWaitDoesNotDownloadOrStopObserver()
    {
        await using var test = new Fixture(ownership: ProjectOwnershipMode.ApplicationManaged,
            interval: TimeSpan.FromMilliseconds(5));
        var prepared = await test.Prepare();
        using var cancellation = new CancellationTokenSource();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloads = 0;
        var transfer = test.Service.TransferAsync(prepared.RuntimeTarget, 852,
            (_, _) => { activated.SetResult(); return Task.CompletedTask; },
            (_, _) => { downloads++; return Task.CompletedTask; }, cancellation.Token);
        await activated.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.Equal(0, downloads);
        var reads = test.Access.SystemReads;
        await WaitUntilAsync(() => Volatile.Read(ref test.Access.SystemReads) > reads);
        Assert.Equal(0, test.Access.Disposals);
    }

    [Fact]
    public async Task DownloadFailureRetainsObserverAndSelectedEndpoint()
    {
        await using var test = new Fixture(ownership: ProjectOwnershipMode.ApplicationManaged,
            interval: TimeSpan.FromMilliseconds(5));
        var prepared = await test.Prepare();
        await Assert.ThrowsAsync<IOException>(() => test.Service.TransferAsync(prepared.RuntimeTarget, 852,
            (_, _) => { test.Access.RestartCounter++; return Task.CompletedTask; },
            (_, _) => throw new IOException("download failed"), default));
        Assert.Equal(852, (await test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default)).Port);
        Assert.Equal(0, test.Access.Disposals);
        Assert.Single(test.Opened);
    }

    [Fact]
    public async Task PortRebindingInvalidatesOldDataEndpointWithoutReplacingSystemContext()
    {
        await using var test = new Fixture(ownership: ProjectOwnershipMode.ApplicationManaged);
        var prepared = await test.Prepare();
        Task Activate(string _, CancellationToken token) { test.Access.RestartCounter++; return Task.CompletedTask; }
        Task Download(string _, CancellationToken token) => Task.CompletedTask;
        await test.Service.TransferAsync(prepared.RuntimeTarget, 852, Activate, Download, default);
        var old = await test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default);
        await test.Service.TransferAsync(prepared.RuntimeTarget, 853, Activate, Download, default);
        Assert.False(old.IsCurrent());
        Assert.False(old.IsAvailable());
        var current = await test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default);
        Assert.Equal(853, current.Port);
        Assert.True(current.IsCurrent());
        Assert.Single(test.Opened);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    [Fact]
    public async Task ProjectReleaseDoesNotEraseTransferredEndpoint()
    {
        await using var runtime = new Fixture(ownership: ProjectOwnershipMode.ApplicationManaged);
        var prepared = await runtime.Prepare();
        await using var project = new EngineeringFixture(runtimeTransfer: runtime.Service);
        project.ConfigureAccess = access => access.AfterActivate = () => runtime.Access.RestartCounter++;
        var context = (await project.Service.PrepareProjectAsync(new(project.Config()), default)).Value!;
        Assert.True((await project.Service.TransferToAsync(context, project.Service.SelectedTarget!.ToReference(),
            prepared.RuntimeTarget, default)).IsSuccessful);
        Assert.True((await project.Service.ReleaseProjectAsync(default)).IsSuccessful);
        Assert.Null(project.Service.CurrentProject);
        var endpoint = await runtime.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default);
        Assert.Equal(852, endpoint.Port);
        Assert.True(endpoint.IsCurrent());
        Assert.Empty(runtime.Access.Writes);
    }

    [Fact]
    public async Task ManagedTransferUsesSelectedPortAndRetainsItAcrossReconnect()
    {
        await using var test = new Fixture(ownership: ProjectOwnershipMode.ApplicationManaged);
        var prepared = await test.Prepare();
        var old = test.Access;
        test.Access.SystemState = AdsState.Config;
        test.Access.PlcFailure = new AdsAccessException("absent", AdsErrorCode.TargetPortNotFound);
        var calls = new List<string>();
        await test.Service.TransferAsync(prepared.RuntimeTarget, 853, (netId, _) =>
        {
            Assert.Equal(test.Catalog.Local.NetId, netId);
            Assert.Equal(0, old.Disposals);
            calls.Add("activate");
            test.Access.SystemState = AdsState.Run;
            test.Access.RestartCounter++;
            return Task.CompletedTask;
        }, (netId, _) =>
        {
            Assert.Equal(test.Catalog.Local.NetId, netId);
            calls.Add("download");
            test.Access.PlcFailure = null;
            test.Access.PlcState = AdsState.Stop;
            return Task.CompletedTask;
        }, default);
        Assert.Equal(["activate", "download"], calls);
        Assert.Equal(PlcOperatingState.Stopped, test.Service.CurrentState.OperatingState);
        Assert.Empty(test.Access.Writes);
        test.Access = new FakeAccess();
        var reconnect = await test.Service.ReconnectRuntimeAsync(new(test.Config, RuntimeProvisioningMode.UseExisting,
            prepared.Runtime, prepared.RuntimeTarget), default);
        Assert.True(reconnect.IsSuccessful, reconnect.Message);
        Assert.Equal(853, (await test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default)).Port);
        Assert.Equal(853, old.BoundPort);
        Assert.Equal(new int?[] { null, 853 }, test.Opened.Select(item => item.Port));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("identity")]
    [InlineData("port")]
    [InlineData("external")]
    public async Task InvalidTransferCannotInvokeEngineering(string problem)
    {
        await using var test = new Fixture(ownership: problem == "external" ? ProjectOwnershipMode.ExternallyManaged : ProjectOwnershipMode.ApplicationManaged,
            json: problem == "port" ? """{"runtimeRef":{"plcPort":854}}""" : "{}");
        var prepared = await test.Prepare();
        var target = prepared.RuntimeTarget;
        if (problem == "target") target = target with { Id = new("192.0.2.99.1.1") };
        if (problem == "identity") test.Access.Identity = test.Access.Identity with { NetId = "192.0.2.99.1.1" };
        var calls = 0;
        Task Callback(string netId, CancellationToken token) { calls++; return Task.CompletedTask; }
        await Assert.ThrowsAnyAsync<Exception>(() => test.Service.TransferAsync(target, 852, Callback, Callback, default));
        Assert.Equal(0, calls);
        Assert.Equal(0, test.Access.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationFailureOrCancellationDoesNotDownloadAndRetainsEndpoint(bool cancel)
    {
        await using var test = new Fixture(ownership: ProjectOwnershipMode.ApplicationManaged);
        var prepared = await test.Prepare();
        using var cancellation = new CancellationTokenSource();
        var downloads = 0;
        Task Activate(string netId, CancellationToken token)
        {
            if (!cancel) throw new IOException("activation failed");
            cancellation.Cancel();
            return Task.CompletedTask;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => test.Service.TransferAsync(prepared.RuntimeTarget, 852, Activate,
            (_, _) => { downloads++; return Task.CompletedTask; }, cancellation.Token));
        Assert.Equal(0, downloads);
        Assert.Empty(test.Access.Writes);
        Assert.Equal(852, (await test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default)).Port);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferRequiresSystemRunAndAccessibleApplication(bool systemRun)
    {
        await using var test = new Fixture(ownership: ProjectOwnershipMode.ApplicationManaged, transferTimeout: TimeSpan.FromMilliseconds(100));
        var prepared = await test.Prepare();
        test.Access.SystemState = systemRun ? AdsState.Run : AdsState.Config;
        test.Access.PlcFailure = new AdsAccessException("absent", AdsErrorCode.TargetPortNotFound);
        var downloads = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => test.Service.TransferAsync(prepared.RuntimeTarget, 852,
            (_, _) => { test.Access.RestartCounter++; return Task.CompletedTask; },
            (_, _) => { downloads++; return Task.CompletedTask; }, default));
        Assert.Equal(systemRun ? 1 : 0, downloads);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
        Assert.Empty(test.Access.Writes);
    }

    [Fact]
    public async Task RouterRegistrationFailureReturnsDiagnosticWithoutTargetFallback()
    {
        await using var test = new Fixture();
        test.BeforeOpen = () => throw new TwinCAT.Ads.Server.LoopbackNotRegisteredException();
        var result = await test.Service.PrepareRuntimeAsync(new(test.Config,
            RuntimeProvisioningMode.UseExisting, test.Catalog.Local.ToReference()), default);
        Assert.False(result.IsSuccessful);
        Assert.Empty(test.Opened);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
        Assert.Equal("Beckhoff.Runtime.PrepareFailed", Assert.Single(test.Diagnostics).Code);
        test.BeforeOpen = null;
        await test.Prepare();
    }

    [Fact]
    public async Task DataEndpointIsBoundToTechnicalContextAndResolvedPort()
    {
        await using var test = new Fixture(json: """{"runtimeRef":{"plcPort":852}}""");
        var prepared = await test.Prepare();
        var endpoint = await test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default);
        Assert.Equal(test.Catalog.Local.NetId, endpoint.NetId);
        Assert.Equal(852, endpoint.Port);
        Assert.True(endpoint.IsCurrent());
        Assert.True(endpoint.IsAvailable());
        var old = test.Access;
        test.Access = new FakeAccess();
        var released = false;
        test.BeforeOpen = () => Assert.True(released);
        var result = await test.Service.ReconnectRuntimeAsync(new(test.Config, RuntimeProvisioningMode.UseExisting,
            prepared.Runtime, prepared.RuntimeTarget), default, () =>
            {
                Assert.Equal(0, old.Disposals);
                released = true;
                return Task.CompletedTask;
            });
        Assert.True(result.IsSuccessful);
        Assert.Equal(prepared.Runtime.Id, result.Value!.Runtime.Id);
        Assert.False(endpoint.IsCurrent());
        Assert.False(endpoint.IsAvailable());
        var replacement = await test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default);
        Assert.True(replacement.IsCurrent());
    }

    [Fact]
    public async Task InvalidReconnectDoesNotReleaseDependentCommunication()
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        var released = false;
        var result = await test.Service.ReconnectRuntimeAsync(new(test.Config, RuntimeProvisioningMode.UseExisting,
            prepared.Runtime with { Id = new("wrong") }, prepared.RuntimeTarget), default, () =>
            {
                released = true;
                return Task.CompletedTask;
            });
        Assert.False(result.IsSuccessful);
        Assert.False(released);
        Assert.Equal(0, test.Access.Disposals);
    }

    [Fact]
    public async Task UnresolvedManagedPortCannotCreateDataEndpoint()
    {
        await using var test = new Fixture(ownership: ProjectOwnershipMode.ApplicationManaged);
        var prepared = await test.Prepare();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Service.GetDataEndpointAsync(prepared.Runtime, prepared.RuntimeTarget, default));
    }

    [Fact]
    public async Task DiscoveryDoesNotOpenAdsOrClaimAvailability()
    {
        await using var test = new Fixture();
        var result = await test.Service.DiscoverRuntimeTargetsAsync(new(test.Config), default);
        Assert.True(result.IsSuccessful);
        Assert.Equal(test.Catalog.Local.ToReference(), result.Value!.DefaultTarget);
        Assert.Empty(test.Opened);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
        Assert.Equal(AccessHealthState.NotEstablished, test.Service.Health.State);
    }

    [Fact]
    public async Task DiscoveryDesignatesNamedDefaultRatherThanFirstCandidate()
    {
        await using var test = new Fixture();
        var alternate = new LocalUsermodeRuntime("Alternate", "192.0.2.99.1.1", @"C:\alternate-runtime\3.1");
        test.Catalog.Candidates = [alternate, test.Catalog.Local];
        var result = await test.Service.DiscoverRuntimeTargetsAsync(new(test.Config), default);
        Assert.True(result.IsSuccessful);
        Assert.Equal(alternate.ToReference(), result.Value!.Candidates[0]);
        Assert.Equal(test.Catalog.Local.ToReference(), result.Value.DefaultTarget);
        Assert.Empty(test.Opened);
    }

    [Fact]
    public async Task DiscoveryDoesNotDesignateMismatchingDefaultIdentity()
    {
        await using var test = new Fixture();
        test.Catalog.Candidates = [test.Catalog.Local with { ConfigurationDirectory = @"C:\different-runtime\3.1" }];
        var result = await test.Service.DiscoverRuntimeTargetsAsync(new(test.Config), default);
        Assert.True(result.IsSuccessful);
        Assert.Null(result.Value!.DefaultTarget);
        Assert.Empty(test.Opened);
    }

    [Theory]
    [InlineData(RuntimeResourceReleaseMode.KeepResource)]
    [InlineData(RuntimeResourceReleaseMode.DestroyResource)]
    public async Task ExistingRuntimeAndBothReleaseIntentsPreserveRuntime(RuntimeResourceReleaseMode intent)
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        Assert.Equal(test.Catalog.Local.ToReference(), prepared.RuntimeTarget);
        Assert.Equal((test.Catalog.Local.NetId, (int?)851), Assert.Single(test.Opened));
        var resolution = await test.Service.ResolveRuntimeTargetAsync(prepared.Runtime, default);
        Assert.True(resolution.IsSuccessful);
        Assert.True(test.Service.CurrentState.ReadyForExecution);
        var access = test.Access;
        var lateCallback = access.CaptureCallback();
        var result = await test.Service.ReleaseRuntimeAsync(new(prepared.Runtime, prepared.RuntimeTarget, intent), default);
        Assert.True(result.IsSuccessful);
        Assert.Equal(PlcRuntimeReleaseOutcome.ResourceKept, result.Value);
        Assert.Equal(1, access.Disposals);
        Assert.Empty(access.Writes);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
        Assert.Equal(AccessHealthState.NotEstablished, test.Service.Health.State);
        lateCallback?.Invoke(access, EventArgs.Empty);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
    }

    [Fact]
    public async Task ManagedOmittedPortIsNotReplacedWith851()
    {
        await using var test = new Fixture(ownership: ProjectOwnershipMode.ApplicationManaged);
        await test.Prepare();
        Assert.Null(Assert.Single(test.Opened).Port);
        Assert.Equal(0, test.Access.PlcReads);
        Assert.Equal(0, test.Access.Subscriptions);
        Assert.True(test.Service.CurrentState.ReadyForTransfer);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
    }

    [Fact]
    public async Task ExplicitTargetAndPortAreRetained()
    {
        await using var test = new Fixture(json: """{"runtimeRef":{"id":"192.0.2.10.1.1","plcPort":852}}""");
        var prepared = await test.Prepare();
        Assert.Equal(("192.0.2.10.1.1", (int?)852), Assert.Single(test.Opened));
        Assert.Equal("192.0.2.10.1.1", prepared.RuntimeTarget.Id.Value);
    }

    [Fact]
    public async Task DefaultFailureCannotFallBackToAnotherCandidate()
    {
        await using var test = new Fixture();
        test.Catalog.DefaultFailure = new IOException("Default not available.");
        var result = await test.Service.DiscoverRuntimeTargetsAsync(new(test.Config), default);
        Assert.True(result.IsSuccessful);
        Assert.Null(result.Value!.DefaultTarget);
        Assert.Single(result.Value.Candidates);
        Assert.Empty(test.Opened);
        Assert.Empty(test.Diagnostics);
        var prepared = await test.Prepare();
        Assert.Equal(test.Catalog.Local.ToReference(), prepared.RuntimeTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateOwnedNeverReusesAnExistingRuntime(bool explicitTarget)
    {
        await using var test = new Fixture(json: explicitTarget
            ? """{"runtimeRef":{"id":"192.0.2.10.1.1"}}""" : "{}");
        var result = await test.Service.PrepareRuntimeAsync(new(test.Config, RuntimeProvisioningMode.CreateOwned,
            explicitTarget ? test.Catalog.Local.ToReference() : null), default);
        Assert.Equal(OperationResultState.NotSupported, result.State);
        Assert.Empty(test.Opened);
        Assert.Empty(test.Access.Writes);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
    }

    [Fact]
    public async Task UseExistingWithoutResolvedTargetDoesNotSelectDefaultDuringPreparation()
    {
        await using var test = new Fixture();
        var result = await test.Service.PrepareRuntimeAsync(new(test.Config, RuntimeProvisioningMode.UseExisting, null), default);
        Assert.False(result.IsSuccessful);
        Assert.Empty(test.Opened);
    }

    [Fact]
    public async Task IdentityMismatchDisposesAccessWithoutCommands()
    {
        await using var test = new Fixture();
        test.Access.Identity = test.Access.Identity with { NetId = "192.0.2.11.1.1" };
        var result = await test.Service.PrepareRuntimeAsync(new(test.Config, RuntimeProvisioningMode.UseExisting, test.Catalog.Local.ToReference()), default);
        Assert.False(result.IsSuccessful);
        Assert.Equal(1, test.Access.Disposals);
        Assert.Empty(test.Access.Writes);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
    }

    [Fact]
    public async Task ConfigRetainsSelectionBlocksCommandsAndReestablishesPlcOnRun()
    {
        await using var test = new Fixture(interval: TimeSpan.FromMilliseconds(15));
        test.Access.SystemState = AdsState.Config;
        var prepared = await test.Prepare();
        Assert.Equal(RuntimeInstanceState.Available, test.Service.CurrentState.InstanceState);
        Assert.Equal(PlcOperatingState.Unavailable, test.Service.CurrentState.OperatingState);
        Assert.True(test.Service.CurrentState.ReadyForTransfer);
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
        Assert.False((await test.Service.StartAsync(prepared.Runtime, prepared.RuntimeTarget, default)).IsSuccessful);
        Assert.Empty(test.Access.Writes);
        Assert.Equal(0, test.Access.PlcReads);
        var reads = test.Access.SystemReads;
        await Until(() => test.Access.SystemReads > reads + 2);
        Assert.Single(test.Diagnostics, d => d.Code == "Beckhoff.Runtime.SystemConfig");
        var identityReads = test.Access.IdentityReads;
        test.Access.PlcState = AdsState.Stop;
        test.Access.SystemState = AdsState.Run;
        await Until(() => test.Service.CurrentState.OperatingState == PlcOperatingState.Stopped);
        Assert.True(test.Access.IdentityReads > identityReads);
        Assert.Equal(prepared.RuntimeTarget, Assert.Single(test.Service.CurrentState.RuntimeTargets));
        Assert.True(test.Access.Subscriptions > 0);
        Assert.Empty(test.Access.Writes);
    }

    [Fact]
    public async Task SystemMonitoringDoesNotContinuouslyPollAHealthyPlc()
    {
        await using var test = new Fixture(interval: TimeSpan.FromMilliseconds(15));
        await test.Prepare();
        var reads = test.Access.SystemReads;
        await Until(() => test.Access.SystemReads > reads + 2);
        Assert.Equal(1, test.Access.PlcReads);
        test.Access.PlcState = AdsState.Stop;
        test.Access.Notify();
        await Until(() => test.Service.CurrentState.OperatingState == PlcOperatingState.Stopped);
        Assert.True(test.Access.PlcReads >= 2);
    }

    [Fact]
    public async Task ManagedPreparationAllowsMissingPlcPortAndCanRecover()
    {
        await using var test = new Fixture(interval: TimeSpan.FromMilliseconds(15),
            ownership: ProjectOwnershipMode.ApplicationManaged, json: """{"runtimeRef":{"plcPort":852}}""");
        test.Access.PlcFailure = new AdsAccessException("Read PLC", AdsErrorCode.TargetPortNotFound);
        await test.Prepare();
        Assert.Equal(RuntimeInstanceState.Available, test.Service.CurrentState.InstanceState);
        Assert.Equal(PlcOperatingState.Unavailable, test.Service.CurrentState.OperatingState);
        Assert.True(test.Service.CurrentState.ReadyForTransfer);
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
        Assert.Empty(test.Diagnostics);
        test.Access.PlcFailure = null;
        await Until(() => test.Service.CurrentState.ReadyForExecution);
        Assert.Empty(test.Access.Writes);
    }

    [Theory]
    [InlineData("{}", 851)]
    [InlineData("{\"runtimeRef\":{\"plcPort\":852}}", 852)]
    public async Task ExternalPreparationRejectsMissingPlcPortAndAllowsRetry(string json, int port)
    {
        await using var test = new Fixture(json: json);
        test.Access.PlcFailure = new AdsAccessException("Read PLC", AdsErrorCode.TargetPortNotFound);
        var result = await test.Service.PrepareRuntimeAsync(new(test.Config,
            RuntimeProvisioningMode.UseExisting, test.Catalog.Local.ToReference()), default);
        Assert.False(result.IsSuccessful);
        Assert.Null(result.Value);
        var diagnostic = Assert.Single(test.Diagnostics);
        Assert.Equal("Beckhoff.Runtime.PrepareFailed", diagnostic.Code);
        Assert.Contains(test.Catalog.Local.NetId, diagnostic.Message);
        Assert.Contains($"PLC port {port}", diagnostic.Message);
        Assert.Contains("XAE", diagnostic.Message);
        Assert.Equal(1, test.Access.Disposals);
        Assert.Empty(test.Access.Writes);
        test.Access = new() { PlcState = AdsState.Stop };
        await test.Prepare();
        Assert.Equal(PlcOperatingState.Stopped, test.Service.CurrentState.OperatingState);
        Assert.Empty(test.Access.Writes);
    }

    [Fact]
    public async Task ExternalPortLossAfterPreparationRetainsRecovery()
    {
        await using var test = new Fixture(interval: TimeSpan.FromMilliseconds(15));
        await test.Prepare();
        test.Access.PlcFailure = new AdsAccessException("Read PLC", AdsErrorCode.TargetPortNotFound);
        test.Access.Notify();
        await Until(() => test.Service.CurrentState.OperatingState == PlcOperatingState.Unavailable);
        test.Access.PlcFailure = null;
        await Until(() => test.Service.CurrentState.ReadyForExecution);
        Assert.DoesNotContain(test.Diagnostics, d => d.Code == "Beckhoff.Runtime.PrepareFailed");
        Assert.Empty(test.Access.Writes);
    }

    [Fact]
    public async Task PlcReadFailureDoesNotInventSystemLossAndDoesNotSpam()
    {
        await using var test = new Fixture(interval: TimeSpan.FromMilliseconds(15));
        await test.Prepare();
        test.Access.PlcFailure = new AdsAccessException("Read PLC", AdsErrorCode.ClientSyncTimeOut);
        test.Access.Notify();
        await Until(() => test.Service.Health.State == AccessHealthState.RevalidationRequired);
        var reads = test.Access.SystemReads;
        await Until(() => test.Access.SystemReads > reads + 3);
        Assert.Equal(RuntimeInstanceState.Available, test.Service.CurrentState.InstanceState);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
        Assert.Single(test.Diagnostics, d => d.Code == "Beckhoff.Runtime.ObservationFailed");
        Assert.DoesNotContain(test.Diagnostics, d => d.Code == "Beckhoff.Runtime.SystemConfig");
        test.Access.PlcFailure = null;
        await Until(() => test.Service.Health.State == AccessHealthState.Healthy);
        await Until(() => test.Diagnostics.Any(d => d.Code == "Beckhoff.Runtime.Recovered"));
        Assert.Single(test.Diagnostics, d => d.Code == "Beckhoff.Runtime.Recovered");
    }

    [Theory]
    [InlineData(AdsErrorCode.TargetPortNotFound)]
    [InlineData(AdsErrorCode.TargetMachineNotFound)]
    [InlineData(AdsErrorCode.ClientSyncTimeOut)]
    [InlineData(AdsErrorCode.DeviceInvalidState)]
    [InlineData(AdsErrorCode.MailBoxError)]
    public async Task RelevantErrorsBringSystemCheckForwardAndAreCoalesced(AdsErrorCode code)
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        test.Access.SystemState = AdsState.Config;
        for (var i = 0; i < 100; i++)
            test.Service.RequestSystemStateCheck(prepared.Runtime, code);
        await Until(() => test.Service.CurrentState.OperatingState == PlcOperatingState.Unavailable);
        await Until(() => test.Diagnostics.Any(d => d.Code == "Beckhoff.Runtime.SystemConfig"));
        Assert.Equal(2, test.Access.SystemReads);
        Assert.Single(test.Diagnostics, d => d.Code == "Beckhoff.Runtime.SystemConfig");
    }

    [Fact]
    public async Task WritesAffectOnlySelectedPlcAndDoNotReplayAfterFailure()
    {
        await using var test = new Fixture();
        test.Access.PlcState = AdsState.Stop;
        var prepared = await test.Prepare();
        Assert.True((await test.Service.StartAsync(prepared.Runtime, prepared.RuntimeTarget, default)).IsSuccessful);
        Assert.True((await test.Service.StartAsync(prepared.Runtime, prepared.RuntimeTarget, default)).IsSuccessful);
        Assert.Equal([AdsState.Run], test.Access.Writes.ToArray());
        test.Access.WriteFailure = new AdsAccessException("Write PLC", AdsErrorCode.ClientSyncTimeOut);
        Assert.False((await test.Service.StopAsync(prepared.Runtime, prepared.RuntimeTarget, default)).IsSuccessful);
        Assert.Equal([AdsState.Run, AdsState.Stop], test.Access.Writes.ToArray());
        Assert.Equal(PlcOperatingState.Running, test.Service.CurrentState.OperatingState);
    }

    [Fact]
    public async Task ReconnectDisposesOldAccessFirstAndPreservesLogicalContext()
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        var old = test.Access;
        var late = old.CaptureCallback();
        test.Access = new();
        test.BeforeOpen = () => Assert.Equal(1, old.Disposals);
        var result = await test.Service.ReconnectRuntimeAsync(new(test.Config, RuntimeProvisioningMode.UseExisting,
            prepared.Runtime, prepared.RuntimeTarget), default);
        Assert.True(result.IsSuccessful);
        Assert.Equal(prepared, result.Value);
        var count = test.Access.SystemReads;
        late?.Invoke(old, EventArgs.Empty);
        Assert.Equal(count, test.Access.SystemReads);
        Assert.Empty(old.Writes);
        Assert.Empty(test.Access.Writes);
    }

    [Fact]
    public async Task ReconnectInConfigSucceedsWithoutWaitingForRunOrIssuingCommands()
    {
        await using var test = new Fixture();
        test.Access.SystemState = AdsState.Config;
        var prepared = await test.Prepare();
        var old = test.Access;
        test.Access = new() { SystemState = AdsState.Config };
        var result = await test.Service.ReconnectRuntimeAsync(new(test.Config, RuntimeProvisioningMode.UseExisting,
            prepared.Runtime, prepared.RuntimeTarget), default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.IsSuccessful, result.Message);
        Assert.Equal(prepared, result.Value);
        Assert.Equal(1, old.Disposals);
        Assert.Equal(RuntimeInstanceState.Available, test.Service.CurrentState.InstanceState);
        Assert.Equal(PlcOperatingState.Unavailable, test.Service.CurrentState.OperatingState);
        Assert.Equal(AccessHealthState.Healthy, test.Service.Health.State);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
        Assert.True(test.Service.CurrentState.ReadyForTransfer);
        Assert.Equal(0, test.Access.PlcReads);
        Assert.Empty(test.Access.Writes);
    }

    [Fact]
    public async Task FailedReconnectIsNotHealthyAndCanRetrySameTarget()
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        test.Access = new() { SystemFailure = new IOException("Unavailable") };
        var request = new PlcRuntimeReconnectRequest(test.Config, RuntimeProvisioningMode.UseExisting, prepared.Runtime, prepared.RuntimeTarget);
        Assert.False((await test.Service.ReconnectRuntimeAsync(request, default)).IsSuccessful);
        Assert.Equal(AccessHealthState.Invalidated, test.Service.Health.State);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
        Assert.Equal(1, test.Access.Disposals);
        test.Access = new();
        var retry = await test.Service.ReconnectRuntimeAsync(request, default);
        Assert.True(retry.IsSuccessful);
        Assert.Equal(prepared, retry.Value);
    }

    [Fact]
    public async Task CancelledReconnectDoesNotDetachAndCancellationAfterDetachDoesNotInterrupt()
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        var request = new PlcRuntimeReconnectRequest(test.Config, RuntimeProvisioningMode.UseExisting, prepared.Runtime, prepared.RuntimeTarget);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Service.ReconnectRuntimeAsync(request, new(true)));
        Assert.Equal(0, test.Access.Disposals);
        using var cancellation = new CancellationTokenSource();
        test.Access.OnDispose = cancellation.Cancel;
        test.BeforeOpen = () => test.Access = new();
        Assert.True((await test.Service.ReconnectRuntimeAsync(request, cancellation.Token)).IsSuccessful);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task ReleaseCancelsInflightObservationBeforeDisposingAndLateReadCannotPublish()
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        test.Access.ReadSystem = async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return AdsState.Run;
        };
        test.Access.OnDispose = () => Assert.True(cancelled);
        test.Access.Notify();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var result = await test.Service.ReleaseRuntimeAsync(new(prepared.Runtime, prepared.RuntimeTarget, RuntimeResourceReleaseMode.KeepResource), default);
        Assert.True(result.IsSuccessful);
        Assert.Equal(1, test.Access.Disposals);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
    }

    [Fact]
    public async Task TechnicalCleanupFailureIsNotSuccessfulResourcePreservation()
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        test.Access.OnDispose = () => throw new IOException("Disposal failed");
        var result = await test.Service.ReleaseRuntimeAsync(new(prepared.Runtime, prepared.RuntimeTarget, RuntimeResourceReleaseMode.DestroyResource), default);
        Assert.False(result.IsSuccessful);
        Assert.Equal(ServiceState.CleanupRequired, test.Service.State);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
    }

    [Fact]
    public async Task ReservedCreationAndDestructionAreUnsupportedWithoutDestructiveCalls()
    {
        await using var test = new Fixture(strategy: RuntimeProvisioningStrategy.CreateDedicated);
        var result = await test.Service.PrepareRuntimeAsync(new(test.Config, RuntimeProvisioningMode.CreateOwned, null), default);
        Assert.Equal(OperationResultState.NotSupported, result.State);
        Assert.Empty(test.Opened);
        var prepared = await test.Prepare();
        Assert.Equal(OperationResultState.NotSupported, (await test.Service.ReleaseRuntimeAsync(
            new(prepared.Runtime, prepared.RuntimeTarget, RuntimeResourceReleaseMode.DestroyResource), default)).State);
        Assert.Equal(0, test.Access.Disposals);
    }

    [Fact]
    public async Task CancellationDuringPreparationDisposesCandidateAndAllowsRetry()
    {
        await using var test = new Fixture();
        using var cancellation = new CancellationTokenSource();
        test.Access.ReadSystem = _ => { cancellation.Cancel(); return Task.FromResult(AdsState.Run); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Service.PrepareRuntimeAsync(
            new(test.Config, RuntimeProvisioningMode.UseExisting, test.Catalog.Local.ToReference()), cancellation.Token));
        Assert.Equal(1, test.Access.Disposals);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
        Assert.Equal(ServiceState.NotPrepared, test.Service.State);
        test.Access = new();
        await test.Prepare();
    }

    [Fact]
    public async Task ThrowingPreparationObserverDoesNotLeaveDisposedAccessAttached()
    {
        await using var test = new Fixture();
        EventHandler<PlcStateChangedEventArgs> observer = (_, args) => throw new InvalidOperationException("Observer failed.");
        test.Service.PlcStateChangedEventHandler += observer;
        var request = new PlcRuntimePrepareRequest(test.Config, RuntimeProvisioningMode.UseExisting, test.Catalog.Local.ToReference());
        Assert.False((await test.Service.PrepareRuntimeAsync(request, default)).IsSuccessful);
        Assert.Equal(1, test.Access.Disposals);
        Assert.False(test.Service.CurrentState.ReadyForExecution);
        test.Service.PlcStateChangedEventHandler -= observer;
        test.Access = new();
        await test.Prepare();
    }

    [Fact]
    public async Task IdentityChangeOnRecoveryQuarantinesInsteadOfAdoptingNewTarget()
    {
        await using var test = new Fixture(interval: TimeSpan.FromMilliseconds(15));
        test.Access.SystemState = AdsState.Config;
        var prepared = await test.Prepare();
        test.Access.Identity = test.Access.Identity with { NetId = "192.0.2.11.1.1" };
        test.Access.SystemState = AdsState.Run;
        await Until(() => test.Service.Health.State == AccessHealthState.Invalidated);
        Assert.Equal(prepared.RuntimeTarget, Assert.Single(test.Service.CurrentState.RuntimeTargets));
        Assert.False(test.Service.CurrentState.ReadyForExecution);
        Assert.Equal(0, test.Access.PlcReads);
        Assert.Empty(test.Access.Writes);
        Assert.False((await test.Service.StartAsync(prepared.Runtime, prepared.RuntimeTarget, default)).IsSuccessful);
        Assert.Empty(test.Access.Writes);
    }

    [Fact]
    public async Task ReconnectWithDifferentPortIsRejectedBeforeDetachment()
    {
        await using var test = new Fixture();
        var prepared = await test.Prepare();
        var configuration = new BeckhoffConfigurationService("", ProjectOwnershipMode.ExternallyManaged, TimeProvider.System);
        Assert.True(configuration.LoadConfig("""{"runtimeRef":{"plcPort":852}}""").IsSuccessful);
        Assert.False((await test.Service.ReconnectRuntimeAsync(new(configuration.Configuration,
            RuntimeProvisioningMode.UseExisting, prepared.Runtime, prepared.RuntimeTarget), default)).IsSuccessful);
        Assert.Equal(0, test.Access.Disposals);
        Assert.Single(test.Opened);
        Assert.True(test.Service.CurrentState.ReadyForExecution);
    }

    [Fact]
    public async Task IndependentSessionsDoNotControlEachOthersAccess()
    {
        await using var first = new Fixture();
        await using var second = new Fixture();
        var prepared = await first.Prepare();
        await second.Prepare();
        Assert.False((await second.Service.StopAsync(prepared.Runtime, prepared.RuntimeTarget, default)).IsSuccessful);
        Assert.Empty(second.Access.Writes);
        Assert.True((await first.Service.StopAsync(prepared.Runtime, prepared.RuntimeTarget, default)).IsSuccessful);
        Assert.Equal([AdsState.Stop], first.Access.Writes.ToArray());
        Assert.Empty(second.Access.Writes);
        Assert.True(second.Service.CurrentState.ReadyForExecution);
    }

    [Fact]
    public async Task NotificationDuringInitialReadIsNotLost()
    {
        await using var test = new Fixture();
        test.Access.AfterPlcRead = () => { test.Access.PlcState = AdsState.Stop; test.Access.Notify(); };
        await test.Prepare();
        await Until(() => test.Service.CurrentState.OperatingState == PlcOperatingState.Stopped);
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeCatalog Catalog { get; } = new();
        public FakeAccess Access { get; set; } = new();
        public BeckhoffPlcControlService Service { get; }
        public AccessProfileConfig Config { get; }
        public List<(string NetId, int? Port)> Opened { get; } = [];
        public ConcurrentQueue<Diagnostic> Diagnostics { get; } = new();
        public Action? BeforeOpen { get; set; }
        public Fixture(TimeSpan? interval = null, ProjectOwnershipMode ownership = ProjectOwnershipMode.ExternallyManaged,
            string json = "{}", RuntimeProvisioningStrategy strategy = RuntimeProvisioningStrategy.ReuseExisting, TimeSpan? transferTimeout = null)
        {
            var config = new BeckhoffConfigurationService("", ownership, TimeProvider.System);
            Assert.True(config.LoadConfig(json).IsSuccessful);
            Config = config.Configuration;
            var context = new AccessProfileContext(NullLoggerFactory.Instance, TimeProvider.System, AppContext.BaseDirectory,
                Path.GetTempPath(), new OperationalLoggingOptions(Path.GetTempPath(), LogLevel.Information, 1048576, 2));
            Service = new(context, ownership, Catalog, (netId, port) =>
            {
                BeforeOpen?.Invoke();
                Opened.Add((netId, port));
                return Access;
            }, interval ?? TimeSpan.FromMinutes(1), strategy) { TransferObservationTimeout = transferTimeout ?? TimeSpan.FromSeconds(30) };
            Service.DiagnosticEventHandler += (_, args) => Diagnostics.Enqueue(args.Diagnostic);
        }

        public async Task<PreparedPlcRuntime> Prepare(RuntimeProvisioningMode mode = RuntimeProvisioningMode.UseExisting)
        {
            var result = await Service.PrepareRuntimeAsync(new(Config, mode,
                mode == RuntimeProvisioningMode.UseExisting ? Catalog.Local.ToReference() : null), default);
            Assert.True(result.IsSuccessful, result.Message);
            return result.Value!;
        }
        public async ValueTask DisposeAsync() => await Service.CloseAsync();
    }

    private sealed class FakeCatalog : ILocalUsermodeRuntimeCatalog
    {
        public LocalUsermodeRuntime Local { get; } = new("UmRT_Default", "192.0.2.10.1.1", @"C:\test-runtime\3.1");
        public Exception? DefaultFailure { get; set; }
        public IReadOnlyList<LocalUsermodeRuntime>? Candidates { get; set; }
        public IReadOnlyList<LocalUsermodeRuntime> Discover(CancellationToken token) => Candidates ?? [Local];
        public LocalUsermodeRuntime ResolveDefault(CancellationToken token) => DefaultFailure is null ? Local : throw DefaultFailure;
        public LocalUsermodeRuntime Verify(OnlineRuntimeIdentity identity, CancellationToken token) =>
            identity.DeviceType == "PC-WIN-U" && identity.ConfigurationDirectory == Local.ConfigurationDirectory
                ? Local : throw new InvalidDataException("Not the selected local Usermode identity.");
    }

    private sealed class FakeAccess : IAdsRuntimeAccess
    {
        public event EventHandler? SystemStateInvalidated;
        public event EventHandler? PlcStateInvalidated;
        public void InvalidatePlc() => PlcStateInvalidated?.Invoke(this, EventArgs.Empty);
        public void InvalidateSystem() => SystemStateInvalidated?.Invoke(this, EventArgs.Empty);
        public int RestartCounter = 1;
        public int? BoundPort;
        public async Task<SystemStateRead> ReadSystemObservationAsync(CancellationToken token) =>
            new(await ReadSystemStateAsync(token), RestartCounter);
        public Task BindPlcPortAsync(int port) { BoundPort = port; return Task.CompletedTask; }
        public event EventHandler? PlcStateChanged;
        public OnlineRuntimeIdentity Identity { get; set; } = new("192.0.2.10.1.1", @"C:\test-runtime\3.1", "PC-WIN-U");
        public AdsState SystemState { get; set; } = AdsState.Run;
        public AdsState PlcState { get; set; } = AdsState.Run;
        public Exception? SystemFailure { get; set; }
        public Exception? PlcFailure { get; set; }
        public Exception? WriteFailure { get; set; }
        public Action? OnWrite { get; set; }
        public Action? OnDispose { get; set; }
        public Action? AfterPlcRead { get; set; }
        public Func<CancellationToken, Task<AdsState>>? ReadSystem { get; set; }
        public Func<CancellationToken, Task<AdsState>>? ReadPlc { get; set; }
        public ConcurrentQueue<AdsState> Writes { get; } = new();
        public int SystemReads, PlcReads, Subscriptions, Disposals, IdentityReads;
        public Task<OnlineRuntimeIdentity> ReadIdentityAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref IdentityReads);
            return Task.FromResult(Identity);
        }
        public Task<AdsState> ReadSystemStateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref SystemReads);
            if (SystemFailure is not null) throw SystemFailure;
            return ReadSystem?.Invoke(token) ?? Task.FromResult(SystemState);
        }
        public Task<AdsState> ReadPlcStateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref PlcReads);
            if (PlcFailure is not null) throw PlcFailure;
            if (ReadPlc is not null) return ReadPlc(token);
            var state = PlcState;
            var afterRead = AfterPlcRead;
            AfterPlcRead = null;
            afterRead?.Invoke();
            return Task.FromResult(state);
        }
        public Task SubscribePlcStateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Subscriptions);
            return Task.CompletedTask;
        }
        public Task ReleasePlcAccessAsync() => Task.CompletedTask;
        public Task WritePlcStateAsync(AdsState state, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Writes.Enqueue(state);
            OnWrite?.Invoke();
            if (WriteFailure is not null) throw WriteFailure;
            PlcState = state;
            return Task.CompletedTask;
        }
        public void Notify() => PlcStateChanged?.Invoke(this, EventArgs.Empty);
        public EventHandler? CaptureCallback() => PlcStateChanged;
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            PlcStateChanged = null;
            OnDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
