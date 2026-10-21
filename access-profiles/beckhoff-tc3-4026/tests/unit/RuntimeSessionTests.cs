using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Runtime;
using TwinCAT.Ads;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class RuntimeSessionTests
{
    [Theory]
    [InlineData(AdsState.Run)]
    [InlineData(AdsState.Stop)]
    public async Task IsolatedPlcLossReconnectsOnlyPlcAndRetainsHandler(AdsState state)
    {
        var system = new FakeSession();
        var plc = new FakeSession { State = state };
        await using var access = new AdsRuntimeAccess(system, () => plc);
        var plcLosses = 0;
        var systemLosses = 0;
        access.PlcStateInvalidated += (_, _) => plcLosses++;
        access.SystemStateInvalidated += (_, _) => systemLosses++;
        await Ready(access);
        for (var i = 1; i <= 3; i++)
        {
            plc.RaiseLost();
            Assert.Equal(i, plcLosses);
            Assert.Equal(i - 1, plc.Disconnects);
            await Assert.ThrowsAsync<IOException>(() => access.ReadPlcStateAsync(default));
            await Assert.ThrowsAsync<IOException>(() => access.WritePlcStateAsync(AdsState.Run, default));
            await Ready(access);
            Assert.Equal(state, await access.ReadPlcStateAsync(default));
            Assert.Equal(i, plc.Disconnects);
            Assert.Equal(i + 1, plc.Connects);
            Assert.Equal(1, plc.Registers);
            Assert.Equal(1, plc.LostSubscribers);
        }
        Assert.Equal(0, systemLosses);
        Assert.Equal(1, system.Connects);
        Assert.Equal(0, system.Disconnects);
        Assert.Equal(0, plc.Unregisters);
        Assert.Equal(0, plc.Writes);
    }

    [Fact]
    public async Task IntentionalDisconnectDoesNotCreateAdditionalLossOrRecoveryLoop()
    {
        var system = new FakeSession();
        var plc = new FakeSession();
        plc.DuringDisconnect = plc.RaiseLost;
        await using var access = new AdsRuntimeAccess(system, () => plc);
        var losses = 0;
        access.PlcStateInvalidated += (_, _) => losses++;
        await Ready(access);
        plc.RaiseLost();
        await Ready(access);
        await Ready(access);
        Assert.Equal(1, losses);
        Assert.Equal(1, plc.Disconnects);
        system.State = AdsState.Config;
        await access.ReadSystemStateAsync(default);
        Assert.Equal(1, losses);
        system.State = AdsState.Run;
        await Ready(access);
        await Ready(access);
        Assert.Equal(2, plc.Disconnects);
        Assert.Equal(3, plc.Connects);
        Assert.Equal(1, plc.Registers);
    }

    [Fact]
    public async Task PlcLossDuringSystemReadCannotAuthorizePlcAccess()
    {
        var system = new FakeSession();
        var plc = new FakeSession();
        await using var access = new AdsRuntimeAccess(system, () => plc);
        await Ready(access);
        system.DuringRead = plc.RaiseLost;
        Assert.Equal(AdsState.Run, await access.ReadSystemStateAsync(default));
        await Assert.ThrowsAsync<IOException>(() => access.ReadPlcStateAsync(default));
        system.DuringRead = null;
        await Ready(access);
        Assert.Equal(1, plc.Disconnects);
        Assert.Equal(1, plc.Registers);
    }

    [Fact]
    public async Task PlcLossDuringConnectCannotAuthorizePlcAccess()
    {
        var system = new FakeSession();
        var plc = new FakeSession();
        plc.DuringConnect = plc.RaiseLost;
        await using var access = new AdsRuntimeAccess(system, () => plc);
        await access.ReadSystemStateAsync(default);
        await Assert.ThrowsAsync<IOException>(() => access.ReadPlcStateAsync(default));
        plc.DuringConnect = null;
        await Ready(access);
        Assert.Equal(1, plc.Disconnects);
        Assert.Equal(1, plc.Registers);
    }

    [Fact]
    public async Task ObsoletePlcLossCallbacksAreIgnoredAfterRebindingAndDisposal()
    {
        var system = new FakeSession();
        var first = new FakeSession();
        var second = new FakeSession();
        var access = new AdsRuntimeAccess(system, () => first, _ => second, 851);
        await Ready(access);
        var oldCallback = first.CaptureLost();
        var losses = 0;
        access.PlcStateInvalidated += (_, _) => losses++;
        await access.BindPlcPortAsync(852);
        Assert.Equal(0, first.LostSubscribers);
        await Ready(access);
        oldCallback?.Invoke(first, EventArgs.Empty);
        Assert.Equal(AdsState.Run, await access.ReadPlcStateAsync(default));
        var currentCallback = second.CaptureLost();
        await access.DisposeAsync();
        currentCallback?.Invoke(second, EventArgs.Empty);
        Assert.Equal(0, losses);
        Assert.Equal(0, second.LostSubscribers);
    }

    [Fact]
    public async Task PortRebindingDisposesOnlyPlcSessionAndPreservesSystemSession()
    {
        var system = new FakeSession();
        var first = new FakeSession();
        var second = new FakeSession();
        var ports = new List<int>();
        await using var access = new AdsRuntimeAccess(system, () => first,
            port => { ports.Add(port); return second; }, 851);
        await Ready(access);
        await access.BindPlcPortAsync(852);
        Assert.Equal(1, first.Unregisters);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, system.Disposals);
        Assert.Equal(1, system.Connects);
        await Assert.ThrowsAsync<IOException>(() => access.ReadPlcStateAsync(default));
        await Ready(access);
        Assert.Equal(new[] { 852 }, ports);
        Assert.Equal(1, second.Registers);
        await access.BindPlcPortAsync(852);
        Assert.Equal(0, second.Disposals);
    }

    [Theory]
    [InlineData(AdsState.Config)]
    [InlineData(AdsState.Stop)]
    [InlineData(AdsState.Start)]
    public async Task NonRunDisconnectsOnceAndRetainsRegisteredHandler(AdsState unavailable)
    {
        var system = new FakeSession();
        var plc = new FakeSession();
        await using var access = new AdsRuntimeAccess(system, () => plc);
        await Ready(access);
        system.State = unavailable;
        await access.ReadSystemStateAsync(default);
        await access.ReadSystemStateAsync(default);
        Assert.Equal(1, plc.Disconnects);
        Assert.Equal(0, plc.Unregisters);
        Assert.Equal(0, system.Disconnects);
        await Assert.ThrowsAsync<IOException>(() => access.ReadPlcStateAsync(default));
        system.State = AdsState.Run;
        await Ready(access);
        Assert.Equal(2, plc.Connects);
        Assert.Equal(1, plc.Registers);
    }

    [Fact]
    public async Task LostWithoutObservedConfigDisconnectsBeforeAcceptingFreshRun()
    {
        var system = new FakeSession();
        var plc = new FakeSession();
        await using var access = new AdsRuntimeAccess(system, () => plc);
        var signals = 0;
        access.PlcStateChanged += (_, _) => signals++;
        await Ready(access);
        for (var i = 1; i <= 3; i++)
        {
            system.RaiseLost();
            Assert.Equal(i, signals);
            Assert.Equal(i - 1, plc.Disconnects); // No vendor IO inside callback.
            await Assert.ThrowsAsync<IOException>(() => access.WritePlcStateAsync(AdsState.Run, default));
            await Ready(access);
            Assert.Equal(i, plc.Disconnects);
            Assert.Equal(i + 1, plc.Connects);
            Assert.Equal(1, plc.Registers);
        }
        plc.RaisePlcState();
        Assert.Equal(4, signals);
    }

    [Fact]
    public async Task LossDuringSystemReadCannotConfirmRun()
    {
        var system = new FakeSession();
        var plc = new FakeSession();
        await using var access = new AdsRuntimeAccess(system, () => plc);
        await Ready(access);
        system.DuringRead = system.RaiseLost;
        await Assert.ThrowsAsync<IOException>(() => access.ReadSystemStateAsync(default));
        Assert.Equal(1, plc.Disconnects);
        await Assert.ThrowsAsync<IOException>(() => access.ReadPlcStateAsync(default));
        system.DuringRead = null;
        await Ready(access);
        Assert.Equal(1, plc.Registers);
    }

    [Fact]
    public async Task InitialConfigDoesNotCreatePlcSessionAndMissingApplicationDoesNotRegister()
    {
        var system = new FakeSession { State = AdsState.Config };
        var plc = new FakeSession { State = AdsState.Invalid };
        var created = 0;
        await using var access = new AdsRuntimeAccess(system, () => { created++; return plc; });
        await access.ReadSystemStateAsync(default);
        await Assert.ThrowsAsync<IOException>(() => access.SubscribePlcStateAsync(default));
        Assert.Equal(0, created);
        system.State = AdsState.Run;
        await access.ReadSystemStateAsync(default);
        await Assert.ThrowsAsync<AdsAccessException>(() => access.SubscribePlcStateAsync(default));
        Assert.Equal(0, plc.Registers);
        plc.State = AdsState.Stop;
        await access.SubscribePlcStateAsync(default);
        Assert.Equal(1, plc.Registers);
        Assert.Equal(1, created);
    }

    [Fact]
    public async Task PlcStopDoesNotDisconnectAndCancellationCannotConfirmSystemRun()
    {
        var system = new FakeSession();
        var plc = new FakeSession { State = AdsState.Stop };
        await using var access = new AdsRuntimeAccess(system, () => plc);
        await Ready(access);
        Assert.Equal(AdsState.Stop, await access.ReadPlcStateAsync(default));
        Assert.Equal(0, plc.Disconnects);
        using var cancellation = new CancellationTokenSource();
        system.DuringRead = cancellation.Cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(() => access.ReadSystemStateAsync(cancellation.Token));
        await Assert.ThrowsAsync<IOException>(() => access.ReadPlcStateAsync(default));
    }

    [Fact]
    public async Task FailedSystemReadDoesNotReusePreviousRun()
    {
        var system = new FakeSession();
        var plc = new FakeSession();
        await using var access = new AdsRuntimeAccess(system, () => plc);
        await Ready(access);
        system.DuringRead = () => throw new IOException("Unavailable");
        await Assert.ThrowsAsync<IOException>(() => access.ReadSystemStateAsync(default));
        await Assert.ThrowsAsync<IOException>(() => access.WritePlcStateAsync(AdsState.Run, default));
        Assert.Equal(0, plc.Writes);
    }

    [Fact]
    public async Task DisposeReleasesBothSessionsEvenWhenUnregisterFailsAndIgnoresLateCallbacks()
    {
        var system = new FakeSession();
        var plc = new FakeSession { FailUnregister = true };
        var access = new AdsRuntimeAccess(system, () => plc);
        await Ready(access);
        var signals = 0;
        access.PlcStateChanged += (_, _) => signals++;
        await Assert.ThrowsAsync<IOException>(() => access.DisposeAsync().AsTask());
        await access.DisposeAsync();
        system.RaiseLost();
        plc.RaisePlcState();
        Assert.Equal(0, signals);
        Assert.Equal(1, plc.Unregisters);
        Assert.Equal(1, plc.Disposals);
        Assert.Equal(1, system.Disposals);
    }

    [Fact]
    public async Task CancellationAfterRegistrationRetainsOwnershipForCleanupAndDoesNotDuplicate()
    {
        var system = new FakeSession();
        using var cancellation = new CancellationTokenSource();
        var plc = new FakeSession { DuringRegister = cancellation.Cancel };
        var access = new AdsRuntimeAccess(system, () => plc);
        await access.ReadSystemStateAsync(default);
        await Assert.ThrowsAsync<OperationCanceledException>(() => access.SubscribePlcStateAsync(cancellation.Token));
        await access.SubscribePlcStateAsync(default);
        Assert.Equal(1, plc.Registers);
        await access.DisposeAsync();
        Assert.Equal(1, plc.Unregisters);
    }

    [Fact]
    public async Task CancelledPlcAccessDoesNotCreateConnection()
    {
        var system = new FakeSession();
        var plc = new FakeSession();
        await using var access = new AdsRuntimeAccess(system, () => plc);
        await access.ReadSystemStateAsync(default);
        var token = new CancellationToken(true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => access.SubscribePlcStateAsync(token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => access.ReadPlcStateAsync(token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => access.WritePlcStateAsync(AdsState.Run, token));
        Assert.Equal(0, plc.Connects);
    }

    [Fact]
    public async Task UnresolvedManagedPortAllowsSystemObservationOnly()
    {
        var system = new FakeSession();
        await using var access = new AdsRuntimeAccess(system, null);
        Assert.Equal(AdsState.Run, await access.ReadSystemStateAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => access.ReadPlcStateAsync(default));
    }

    private static async Task Ready(AdsRuntimeAccess access)
    {
        Assert.Equal(AdsState.Run, await access.ReadSystemStateAsync(default));
        await access.SubscribePlcStateAsync(default);
    }

    private sealed class FakeSession : IRuntimeAdsSession
    {
        public event EventHandler? Lost;
        public event EventHandler? PlcStateChanged;
        public AdsState State = AdsState.Run;
        public Action? DuringRead;
        public Action? DuringRegister;
        public Action? DuringDisconnect;
        public Action? DuringConnect;
        public int LostSubscribers => Lost?.GetInvocationList().Length ?? 0;
        public EventHandler? CaptureLost() => Lost;
        public int Connects, Disconnects, Registers, Unregisters, Disposals, Writes;
        public bool FailUnregister;
        public void RaiseLost() => Lost?.Invoke(this, EventArgs.Empty);
        public void RaisePlcState() => PlcStateChanged?.Invoke(this, EventArgs.Empty);
        public void Connect() { Connects++; DuringConnect?.Invoke(); }
        public void Disconnect() { Disconnects++; DuringDisconnect?.Invoke(); }
        public Task<SystemStateRead> ReadSystemStateAsync(CancellationToken token)
        {
            DuringRead?.Invoke();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new SystemStateRead(State, 1));
        }
        public Task<AdsState> ReadPlcStateAsync(CancellationToken token) => Task.FromResult(State);
        public Task RegisterPlcStateAsync(CancellationToken token) { Registers++; DuringRegister?.Invoke(); return Task.CompletedTask; }
        public Task UnregisterPlcStateAsync()
        {
            Unregisters++;
            if (FailUnregister) throw new IOException("Cleanup failed");
            return Task.CompletedTask;
        }
        public Task WritePlcStateAsync(AdsState state, CancellationToken token) { Writes++; return Task.CompletedTask; }
        public Task<byte[]?> ReadRegistryEntryAsync(uint index, byte[] request, CancellationToken token) => Task.FromResult<byte[]?>(null);
        public void Dispose() => Disposals++;
    }
}
