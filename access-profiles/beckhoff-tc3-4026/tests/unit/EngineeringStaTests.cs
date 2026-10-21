using PLCSIM.UnitTest.AccessProfiles.Beckhoff.Engineering;
using Xunit;

namespace PLCSIM.UnitTest.Tests.AccessProfiles.Beckhoff.Unit;

public sealed class EngineeringStaTests
{
    [Fact]
    public async Task BackendFactoryAndAllCallsRunOnOneDedicatedSta()
    {
        await using var test = new EngineeringFixture();
        var backend = new FakeXae();
        var caller = Environment.CurrentManagedThreadId;
        var factoryThread = 0;
        var host = new StaEngineeringAccess(() => { factoryThread = Environment.CurrentManagedThreadId; return backend; });
        try
        {
            await host.OpenAsync(ProjectWorkspace.Create(ProjectSettings.Parse(test.Config())), default);
            await host.DiscoverAsync(default);
            var target = EngineeringPlcTarget.FromXml(test.Root, Path.Combine(test.Root, "system.tsproj"), "System", EngineeringFixture.PlcXml());
            await host.EnsureStandardPlcAsync(target, default);
            await host.BuildAsync(target, default);
            await host.ActivateAsync(target, "192.0.2.10.1.1", default);
            await host.DownloadAsync(target, "192.0.2.10.1.1", default);
        }
        finally { await host.CloseAsync(default); }
        Assert.NotEqual(caller, factoryThread);
        Assert.All(backend.Calls, call => { Assert.Equal(factoryThread, call.Thread); Assert.Equal(ApartmentState.STA, call.Apartment); });
        Assert.Equal(["open", "discover", "authorization", "build", "activate", "download", "close"], backend.Calls.Select(call => call.Name));
        await host.CloseAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DiscoverAsync(default));
        Assert.Equal(7, backend.Calls.Count);
    }

    [Fact]
    public async Task ImmediateCloseCannotOvertakeQueuedOpenDuringStaInitialization()
    {
        await using var test = new EngineeringFixture();
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var backend = new FakeXae();
            var host = new StaEngineeringAccess(() => backend);
            var open = host.OpenAsync(ProjectWorkspace.Create(ProjectSettings.Parse(test.Config())), default);
            var close = host.CloseAsync(default);
            await open.WaitAsync(TimeSpan.FromSeconds(5));
            await close.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["open", "close"], backend.Calls.Select(call => call.Name));
        }
    }

    [Fact]
    public async Task CancellationDoesNotAbandonDispatchedCallAndSkipsQueuedWork()
    {
        await using var test = new EngineeringFixture();
        using var entered = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var backend = new FakeXae { OnOpen = () => { entered.Set(); Assert.True(unblock.Wait(TimeSpan.FromSeconds(10))); } };
        var host = new StaEngineeringAccess(() => backend);
        Task? close = null;
        try
        {
            var open = host.OpenAsync(ProjectWorkspace.Create(ProjectSettings.Parse(test.Config())), cancellation.Token);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var queued = host.DiscoverAsync(cancellation.Token);
            cancellation.Cancel();
            close = host.CloseAsync(default);
            Assert.False(open.IsCompleted);
            Assert.False(close.IsCompleted);
            unblock.Set();
            await open;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            await close;
            Assert.Equal(["open", "close"], backend.Calls.Select(call => call.Name));
        }
        finally { unblock.Set(); await (close ?? host.CloseAsync(default)); }
    }

    [Fact]
    public async Task FailedOpenStillAllowsCleanupAndFailedCleanupCanBeRetried()
    {
        await using var test = new EngineeringFixture();
        var backend = new FakeXae
        {
            OnOpen = () => throw new IOException("open"),
            OnClose = () => throw new IOException("close")
        };
        var host = new StaEngineeringAccess(() => backend);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => host.OpenAsync(ProjectWorkspace.Create(ProjectSettings.Parse(test.Config())), default));
            await Assert.ThrowsAsync<IOException>(() => host.CloseAsync(default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.DiscoverAsync(default));
        }
        finally { backend.OnClose = null; await host.CloseAsync(default); }
        Assert.Equal(["open", "close", "close"], backend.Calls.Select(call => call.Name));
    }

    [Fact]
    public async Task CancelledBeforeDispatchDoesNotCreateBackend()
    {
        await using var test = new EngineeringFixture();
        var created = false;
        var host = new StaEngineeringAccess(() => { created = true; return new FakeXae(); });
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                host.OpenAsync(ProjectWorkspace.Create(ProjectSettings.Parse(test.Config())), new(true)));
        }
        finally { await host.CloseAsync(default); }
        Assert.False(created);
    }

    private sealed class FakeXae : IXaeProjectAccess
    {
        internal readonly List<(string Name, int Thread, ApartmentState Apartment)> Calls = [];
        internal Action? OnOpen, OnClose;
        public void Open(ProjectWorkspace workspace, CancellationToken cancellationToken) { Record("open"); OnOpen?.Invoke(); }
        public IReadOnlyList<EngineeringPlcTarget> Discover(CancellationToken cancellationToken) { Record("discover"); return []; }
        public void EnsureStandardPlc(EngineeringPlcTarget target, CancellationToken cancellationToken) => Record("authorization");
        public EngineeringBuildResult Build(EngineeringPlcTarget target, CancellationToken cancellationToken) { Record("build"); return new(true, []); }
        public void Activate(EngineeringPlcTarget target, string netId, CancellationToken cancellationToken) { Record("activate"); }
        public void Download(EngineeringPlcTarget target, string netId, CancellationToken cancellationToken) { Record("download"); }
        public void Close() { Record("close"); OnClose?.Invoke(); }
        private void Record(string name) => Calls.Add((name, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
    }
}
